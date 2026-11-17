using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>文件落盘结果。失败时 <see cref="Error"/> 给出可读原因，调用方必须把原因反馈给用户。</summary>
/// <param name="Success">是否成功写入。</param>
/// <param name="Path">成功时为文件绝对路径；失败时为 null。</param>
/// <param name="Error">失败原因；成功时为 null。</param>
public sealed record FileStoreResult(bool Success, string? Path, string? Error)
{
    /// <summary>构造成功结果。</summary>
    public static FileStoreResult Ok(string path) => new(true, path, null);

    /// <summary>构造失败结果。</summary>
    public static FileStoreResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 本地文件归档服务（单例）：把导入文件、导出文件与缓存统一落到约定目录，并负责启动清理。
///
/// 目录约定（归档根见 <see cref="CurrentArchiveRoot"/>）：
///   Imports\&lt;类别&gt;\yyyyMMdd\&lt;文件名&gt;
///   Exports\&lt;类别&gt;\yyyyMMdd\&lt;文件名&gt;
///   ScanPhotos\（本轮只创建空目录）
/// 归档根默认沿用 AppPaths 解析顺序（安装版 DataRoot、便携版程序目录\Data），
/// 用户可在设置页覆盖 AppSettings.ArchiveRootPath；覆盖值不可写时回退
/// %LOCALAPPDATA%\MaterialSystem\Storage 并写 Warning 日志。
///
/// 命名规则：日期目录 yyyyMMdd；文件名剔除 Windows 非法字符；同名时追加 _1/_2，
/// 大小写不敏感判重，绝不覆盖已有文件。
/// </summary>
public sealed class FileStorageService
{
    /// <summary>导入类别：供应商合同 PDF。</summary>
    public const string ContractCategory = "合同";

    /// <summary>导入类别：地材单等扫描图片。</summary>
    public const string ReceiptCategory = "单据";

    /// <summary>导出类别：Excel 工作簿。</summary>
    public const string ExcelCategory = "Excel";

    /// <summary>导出类别：Word 文档。</summary>
    public const string WordCategory = "Word";

    /// <summary>导出类别：CSV 文本。</summary>
    public const string CsvCategory = "Csv";

    /// <summary>Temp 目录保留天数。</summary>
    private const int TempRetentionDays = 7;

    /// <summary>OcrCache 保留天数。</summary>
    private const int OcrCacheRetentionDays = 30;

    /// <summary>OcrCache 容量上限：500MB。</summary>
    private const long OcrCacheMaxBytes = 500L * 1024 * 1024;

    private static readonly ILogger Logger = AppLog.CreateLogger<FileStorageService>();

    /// <summary>已提示过的不可写根目录，避免每次读写都重复写 Warning。</summary>
    private readonly HashSet<string> _warnedRoots = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _warnLock = new();

    private FileStorageService()
    {
    }

    /// <summary>全局唯一实例。</summary>
    public static FileStorageService Instance { get; } = new();

    /// <summary>设置页填写的归档根；为空表示使用默认位置。</summary>
    public string ConfiguredRoot => (SettingsService.Instance.Settings.ArchiveRootPath ?? string.Empty).Trim();

    /// <summary>
    /// 当前实际生效的归档根：设置值优先；不可写时回退本地 Storage 目录并写 Warning。
    /// </summary>
    public string CurrentArchiveRoot => ResolveRoot(out _);

    /// <summary>当前生效的导入根目录（Imports）。</summary>
    public string ImportsRoot => AppPaths.ImportsDir(CurrentArchiveRoot);

    /// <summary>当前生效的导出根目录（Exports）。</summary>
    public string ExportsRoot => AppPaths.ExportsDir(CurrentArchiveRoot);

    /// <summary>当前生效的扫描照片目录（ScanPhotos，本轮不启用）。</summary>
    public string ScanPhotosRoot => AppPaths.ScanPhotosDir(CurrentArchiveRoot);

    /// <summary>
    /// 解析当前归档根：设置值 → 默认可写位置 → 本地 Storage 回退。
    /// </summary>
    /// <param name="fellBack">是否发生了回退。</param>
    /// <returns>可用且已创建的归档根绝对路径。</returns>
    private string ResolveRoot(out bool fellBack)
    {
        fellBack = false;
        var configured = ConfiguredRoot;
        var preferred = configured.Length > 0 ? configured : AppPaths.DefaultArchiveRoot;

        if (TryEnsureWritable(preferred, out _))
        {
            return preferred;
        }

        var fallback = AppPaths.StorageFallbackRoot;
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch (Exception ex)
        {
            // Storage 位于本机 LocalAppData，极少数情况下仍不可写；此时抛出，
            // 由 StoreImport/StoreExport 捕获并作为失败原因返回，绝不静默改写用户文件位置
            throw new IOException($"归档根目录不可写，回退目录也无法创建：{fallback}", ex);
        }

        fellBack = true;
        LogFallbackOnce(preferred, fallback);
        return fallback;
    }

    /// <summary>同一归档根只提示一次回退原因，避免刷屏。</summary>
    private void LogFallbackOnce(string preferred, string fallback)
    {
        lock (_warnLock)
        {
            if (!_warnedRoots.Add(preferred))
            {
                return;
            }
        }

        Logger.LogWarning(
            "归档根目录不可写，已回退到本机 Storage。期望路径={Preferred}，回退路径={Fallback}。",
            preferred,
            fallback);
    }

    /// <summary>
    /// 创建目录并做一次真实写入探测（仅创建目录不代表可写，例如 Program Files 下的标准用户）。
    /// </summary>
    /// <param name="directory">待检查的目录绝对路径。</param>
    /// <param name="error">不可写时的原因。</param>
    /// <returns>可写返回 true。</returns>
    public static bool TryEnsureWritable(string directory, out string? error)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "目录路径为空。";
            return false;
        }

        try
        {
            if (!Path.IsPathRooted(directory))
            {
                error = "必须是完整路径（如 D:\\Archive）。";
                return false;
            }

            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write_probe_{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 设置页实时预览：根据输入值推算归档根与 Imports / Exports 路径，不做任何磁盘写入。
    /// </summary>
    /// <param name="configuredRoot">设置页当前输入值，可空。</param>
    /// <param name="root">推算出的归档根。</param>
    /// <param name="imports">Imports 路径。</param>
    /// <param name="exports">Exports 路径。</param>
    public static void PreviewLayout(string? configuredRoot, out string root, out string imports, out string exports)
    {
        var trimmed = (configuredRoot ?? string.Empty).Trim();
        root = trimmed.Length > 0 ? trimmed : AppPaths.DefaultArchiveRoot;
        imports = AppPaths.ImportsDir(root);
        exports = AppPaths.ExportsDir(root);
    }

    /// <summary>
    /// 创建归档结构与本机缓存目录；失败只记日志不抛出，避免阻断应用启动。
    /// </summary>
    public void EnsureCreated()
    {
        try
        {
            var root = CurrentArchiveRoot;
            if (AppPaths.TryEnsureCreated(root))
            {
                Logger.LogInformation(
                    "文件归档目录已就绪。归档根={Root}，导入={Imports}，导出={Exports}。",
                    root,
                    AppPaths.ImportsDir(root),
                    AppPaths.ExportsDir(root));
            }
            else
            {
                Logger.LogWarning("部分归档目录创建失败，详见调试输出。归档根={Root}。", root);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "文件归档目录初始化失败。");
        }
    }

    /// <summary>
    /// 把导入文件复制到 Imports\&lt;类别&gt;\yyyyMMdd\ 下，返回归档后的绝对路径。
    /// 复制失败不降级到其他目录，返回失败原因由调用方提示用户。
    /// </summary>
    /// <param name="category">文件类别（合同 / 单据 / Excel 等）。</param>
    /// <param name="sourcePath">用户选择的源文件绝对路径。</param>
    /// <returns>归档结果。</returns>
    public FileStoreResult StoreImport(string category, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            Logger.LogError("导入文件不存在，未归档：{Source}", sourcePath);
            return FileStoreResult.Fail("源文件不存在或已被移动。");
        }

        try
        {
            var root = ResolveRoot(out _);
            var directory = AppPaths.ImportDateDir(root, SanitizeFileName(category), DateTime.Now);
            Directory.CreateDirectory(directory);

            var destination = Path.Combine(directory, BuildUniqueFileName(directory, Path.GetFileName(sourcePath)));
            File.Copy(sourcePath, destination, overwrite: false);

            Logger.LogInformation("导入文件已归档：{Path}。", destination);
            return FileStoreResult.Ok(destination);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "导入文件归档失败：{Source}，类别={Category}。", sourcePath, category);
            return FileStoreResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 把导出内容写入 Exports\&lt;类别&gt;\yyyyMMdd\ 下，返回归档后的绝对路径。
    /// 写入过程失败会删除不完整文件并返回失败原因，不静默改写到其他目录。
    /// </summary>
    /// <param name="category">导出类别（Excel / Word / Csv）。</param>
    /// <param name="fileName">期望文件名（含扩展名）；非法字符会被剔除，冲突时追加序号。</param>
    /// <param name="write">写入委托，参数为目标文件流。</param>
    /// <returns>归档结果。</returns>
    public FileStoreResult StoreExport(string category, string fileName, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        string? destination = null;
        try
        {
            var root = ResolveRoot(out _);
            var directory = AppPaths.ExportDateDir(root, SanitizeFileName(category), DateTime.Now);
            Directory.CreateDirectory(directory);

            destination = Path.Combine(directory, BuildUniqueFileName(directory, fileName));

            // CreateNew：即使判重期间有并发写入也绝不覆盖已有文件
            using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            Logger.LogInformation("导出文件已归档：{Path}。", destination);
            return FileStoreResult.Ok(destination);
        }
        catch (Exception ex)
        {
            TryDelete(destination);
            Logger.LogError(ex, "导出文件写入失败：{FileName}，类别={Category}。", fileName, category);
            return FileStoreResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 启动清理：Temp 删除 7 天前的条目；OcrCache 删除 30 天前或超出 500MB 的最久未用条目。
    /// Imports / Exports 永不自动清理。单项删除失败只记日志，不影响其余条目。
    /// </summary>
    public void CleanupExpired()
    {
        var tempDeleted = CleanupByAge(AppPaths.TempDir, TimeSpan.FromDays(TempRetentionDays));
        var cacheDeleted = CleanupOcrCache();

        Logger.LogInformation(
            "启动清理完成。Temp 删除 {TempCount} 项（保留 {TempDays} 天），OcrCache 删除 {CacheCount} 项（上限 500MB / {CacheDays} 天）。",
            tempDeleted,
            TempRetentionDays,
            cacheDeleted,
            OcrCacheRetentionDays);
    }

    /// <summary>删除目录下超过指定年龄的文件与空目录。</summary>
    private static int CleanupByAge(string directory, TimeSpan maxAge)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;

        foreach (var file in EnumerateFilesSafe(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "清理过期文件失败：{Path}。", file);
            }
        }

        DeleteEmptyDirectories(directory);
        return deleted;
    }

    /// <summary>按「30 天 LRU + 总容量 500MB」清理 OcrCache。</summary>
    private static int CleanupOcrCache()
    {
        var directory = AppPaths.OcrCacheDir;
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-OcrCacheRetentionDays);
        var deleted = 0;
        var remaining = new List<(string Path, long Length, DateTime LastUsed)>();

        foreach (var file in EnumerateFilesSafe(directory))
        {
            try
            {
                var info = new FileInfo(file);
                var lastUsed = GetLastUsedUtc(info);

                // 30 天未使用的缓存优先删除
                if (lastUsed < cutoff)
                {
                    info.Delete();
                    deleted++;
                    continue;
                }

                remaining.Add((file, info.Length, lastUsed));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "清理 OCR 缓存失败：{Path}。", file);
            }
        }

        // 仍超出容量上限时，按最久未用顺序继续删除
        long total = remaining.Sum(x => x.Length);
        foreach (var item in remaining.OrderBy(x => x.LastUsed))
        {
            if (total <= OcrCacheMaxBytes)
            {
                break;
            }

            try
            {
                File.Delete(item.Path);
                total -= item.Length;
                deleted++;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "清理 OCR 缓存失败：{Path}。", item.Path);
            }
        }

        DeleteEmptyDirectories(directory);
        return deleted;
    }

    /// <summary>
    /// 取文件最近使用时间：优先 LastAccessTimeUtc；系统关闭访问时间更新时该值可能停滞，
    /// 退回 LastWriteTimeUtc，保证 LRU 至少按修改时间推进。
    /// </summary>
    private static DateTime GetLastUsedUtc(FileInfo info)
    {
        var lastAccess = info.LastAccessTimeUtc;
        return lastAccess < info.LastWriteTimeUtc ? info.LastWriteTimeUtc : lastAccess;
    }

    /// <summary>枚举目录下全部文件，权限不足时返回空集合。</summary>
    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "枚举目录失败：{Path}。", directory);
            return Array.Empty<string>();
        }
    }

    /// <summary>自底向上删除空目录（保留根目录本身）。</summary>
    private static void DeleteEmptyDirectories(string root)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "清理空目录失败：{Path}。", root);
        }
    }

    /// <summary>
    /// 剔除 Windows 文件名非法字符，处理保留设备名与结尾的点和空格。
    /// 供落盘与单元测试共用。
    /// </summary>
    /// <param name="name">原始文件名或目录名。</param>
    /// <returns>可安全落盘的文件名。</returns>
    internal static string SanitizeFileName(string? name)
    {
        var source = (name ?? string.Empty).Trim();
        if (source.Length == 0)
        {
            return "file";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = builder.ToString().TrimEnd(' ', '.');

        // Windows 保留设备名（CON、PRN、AUX、NUL、COM1-9、LPT1-9）不能直接作为文件名
        var stem = Path.GetFileNameWithoutExtension(result);
        if (IsReservedDeviceName(stem))
        {
            result = "_" + result;
        }

        // 超长文件名会触发路径过长异常，文件名部分限制在 120 字符内（保留扩展名）
        if (result.Length > 120)
        {
            var extension = Path.GetExtension(result);
            var maxStem = Math.Max(1, 120 - extension.Length);
            var stemText = Path.GetFileNameWithoutExtension(result);
            result = stemText[..Math.Min(stemText.Length, maxStem)] + extension;
        }

        return result.Length == 0 ? "file" : result;
    }

    /// <summary>判断是否为 Windows 保留设备名（不区分大小写，忽略扩展名）。</summary>
    private static bool IsReservedDeviceName(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        var reserved = new[] { "CON", "PRN", "AUX", "NUL" };
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && char.IsDigit(stem[3]);
    }

    /// <summary>
    /// 生成不冲突的文件名：同名（大小写不敏感）时依次追加 _1、_2……，绝不覆盖。
    /// 供落盘与单元测试共用。
    /// </summary>
    /// <param name="directory">目标目录；不存在时视为无冲突。</param>
    /// <param name="fileName">期望文件名（含扩展名）。</param>
    /// <returns>目录内唯一且已 sanitize 的文件名。</returns>
    internal static string BuildUniqueFileName(string directory, string? fileName)
    {
        var safeName = SanitizeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        if (string.IsNullOrEmpty(stem))
        {
            stem = "file";
        }

        var candidate = safeName;
        for (var index = 1; FileNameExists(directory, candidate); index++)
        {
            candidate = $"{stem}_{index}{extension}";
        }

        return candidate;
    }

    /// <summary>大小写不敏感判断目录内是否已存在同名文件。</summary>
    private static bool FileNameExists(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(directory)
                .Any(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // 枚举失败时按已存在处理，交给 CreateNew 兜底，避免误判为可覆盖
            return true;
        }
    }

    /// <summary>尽力删除写失败的半成品文件。</summary>
    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略：半成品残留不会覆盖用户文件，下次写入使用新序号
        }
    }
}
