using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MaterialManagementApp.Models;

namespace MaterialManagementApp.Services;

/// <summary>
/// 应用配置服务（单例）。配置固定存放于 %LOCALAPPDATA%\MaterialManagementApp\AppSettings.json，
/// 与安装目录和数据库分离，覆盖安装不会替换用户配置。
/// 选用 JSON 而非数据库表：配置是单例键值对，用表存需额外的表结构与反序列化代码，收益为零。
/// </summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());

    /// <summary>序列化选项：缩进输出，便于用户手工查看与编辑。</summary>
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly object _saveLock = new();

    private SettingsService()
    {
        SettingsFilePath = ResolveSettingsFilePath();
        Settings = new AppSettings();
        Load();
    }

    /// <summary>懒加载单例，首次访问时构造并读取配置。</summary>
    public static SettingsService Instance => _instance.Value;

    /// <summary>当前生效的设置对象；读取失败时为默认实例，永不为 null。</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>当前用户配置文件的完整路径，供设置页展示。</summary>
    public string SettingsFilePath { get; }

    /// <summary>
    /// 默认数据库存放目录：
    /// 安装器指定了数据根目录时（install-config.json 的 dataRoot，见 AppPaths.DataRoot）
    /// 用 根目录\Data——「为所有用户安装」时程序目录在 Program Files 不可写，
    /// 数据库必须跟随配置与图片一起落到用户本地数据目录；
    /// 未安装或未配置时用主程序 exe 所在目录\Data（AppContext.BaseDirectory），
    /// 软件拷到哪数据就跟到哪（绿色便携）。
    /// 该目录由 <c>DatabaseService</c> 在解析路径时自动创建，本属性只给出位置，不做 IO。
    /// </summary>
    public static string DefaultDatabaseFolder => AppPaths.DataRoot is { } dataRoot
        ? Path.Combine(dataRoot, "Data")
        : AppPaths.ProgramDataDir;

    /// <summary>旧版默认配置目录，仅用于首次启动迁移。</summary>
    private static string LegacyDefaultSettingsDirectory => AppPaths.DataRoot ?? AppPaths.ProgramDataDir;

    /// <summary>旧版配置引导文件，仅在迁移时读取，不再写入或作为生效路径。</summary>
    private static string LegacySettingsRedirectFilePath =>
        Path.Combine(LegacyDefaultSettingsDirectory, "settings.path");

    /// <summary>读取旧版 settings.path 指向的配置文件，供一次性迁移使用。</summary>
    private static string? GetLegacyCustomSettingsFile()
    {
        try
        {
            if (!File.Exists(LegacySettingsRedirectFilePath))
            {
                return null;
            }

            var line = File.ReadAllLines(LegacySettingsRedirectFilePath)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var full = Path.GetFullPath(line);
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 读取旧配置引导文件失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 当前实际生效的数据库文件完整路径。
    /// 配置中的 <see cref="AppSettings.DatabasePath"/> 为空时返回默认路径（程序目录\Data\MaterialSystem.db）。
    /// 每次访问实时计算，保证用户在设置页修改后无需重启即可反映最新值。
    /// </summary>
    public string EffectiveDatabasePath
    {
        get
        {
            var configured = Settings.DatabasePath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            return Path.Combine(DefaultDatabaseFolder, "MaterialSystem.db");
        }
    }

    /// <summary>配置保存成功后触发，用于刷新主题与阈值显示。</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    /// 从磁盘读取配置。文件缺失或解析异常时回退默认值且不抛出——
    /// 配置文件损坏不应阻断应用启动。
    /// </summary>
    public void Load()
    {
        try
        {
            Settings = LoadSettingsFrom(SettingsFilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 读取配置失败，使用默认值：{ex.Message}");
            Settings = new AppSettings();
        }
    }

    /// <summary>
    /// 从指定路径读取配置并校正（供 <see cref="Load"/> 与单元测试复用）。
    /// 文件缺失返回默认配置；JSON 损坏时抛出异常，由调用方决定兜底方式。
    /// </summary>
    /// <param name="path">配置文件完整路径。</param>
    /// <returns>校正后的配置对象，永不为 null。</returns>
    internal static AppSettings LoadSettingsFrom(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        // 文件中只写了 "null" 时反序列化结果为 null，同样回退默认值避免空引用
        return Normalize(loaded ?? new AppSettings());
    }

    /// <summary>将当前设置写入磁盘，成功后触发 <see cref="SettingsChanged"/>。</summary>
    /// <exception cref="IOException">目标目录无写入权限或磁盘不可写时抛出，由调用方提示用户。</exception>
    public void Save()
    {
        lock (_saveLock)
        {
            SaveSettingsTo(SettingsFilePath, Settings);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把配置原子写入指定路径（供 <see cref="Save"/> 与单元测试复用）：
    /// 先写唯一临时文件再替换，避免写入途中断电导致配置被截断成无效 JSON；
    /// 临时名带唯一后缀，残留旧 .tmp 被占用时不会顶撞本次保存；失败时清理临时文件后重抛。
    /// </summary>
    /// <param name="path">目标配置文件完整路径。</param>
    /// <param name="settings">要写入的配置对象。</param>
    /// <exception cref="IOException">目录不可写或磁盘不可写时抛出。</exception>
    /// <exception cref="UnauthorizedAccessException">无文件访问权限时抛出。</exception>
    internal static void SaveSettingsTo(string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, _jsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>尽力删除临时文件；删除失败（仍被占用）不影响主流程。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略：临时文件最多残留一个，下次保存使用新的唯一名称不会冲突
        }
    }

    /// <summary>恢复出厂默认值并立即落盘，随后触发 <see cref="SettingsChanged"/>。</summary>
    public void Reset()
    {
        Settings = new AppSettings();
        Save();
    }

    /// <summary>校正反序列化后不合法的字段，保证下游使用安全。</summary>
    /// <param name="settings">从磁盘读出的设置对象。</param>
    /// <returns>校正后的同一对象，便于链式返回。</returns>
    internal static AppSettings Normalize(AppSettings settings)
    {
        if (settings.LowStockThreshold <= 0)
        {
            settings.LowStockThreshold = 10;
        }

        if (settings.Theme is not ("Default" or "Light" or "Dark"))
        {
            settings.Theme = "Default";
        }

        // 主题模式校验：无效或缺失时从旧版 Theme 字段迁移（Default→System、Light→Light、Dark→Dark）
        if (settings.ThemeMode is not ("Light" or "Dark" or "Acrylic" or "System"))
        {
            settings.ThemeMode = settings.Theme switch
            {
                "Light" => "Light",
                "Dark" => "Dark",
                _ => "System"
            };
        }

        if (settings.BaseTheme != "Dark")
        {
            settings.BaseTheme = "Light";
        }

        // 关闭行为校验与迁移：旧版仅有 MinimizeToTray 布尔开关，true→托盘后台，false→恢复默认询问；
        // 同步回写旧字段保持序列化一致，非法值一律回退 Ask
        if (settings.CloseAction is not ("Ask" or "MinimizeToTray" or "Exit"))
        {
            settings.CloseAction = settings.MinimizeToTray ? "MinimizeToTray" : "Ask";
        }
        settings.MinimizeToTray = string.Equals(settings.CloseAction, "MinimizeToTray", StringComparison.OrdinalIgnoreCase);

        // 旧版配置可能缺少新增字段，反序列化后为 null，这里统一补空串避免后续 Trim 空引用
        settings.ProjectName ??= string.Empty;
        settings.UsingUnit ??= string.Empty;
        settings.DefaultUnit ??= string.Empty;
        settings.DefaultHandler ??= string.Empty;
        settings.DatabasePath ??= string.Empty;
        settings.ArchiveRootPath ??= string.Empty;
        settings.WatermarkRootPath ??= string.Empty;
        settings.AmapJsApiKey ??= string.Empty;
        settings.AmapSecurityCode ??= string.Empty;
        settings.ProjectDeptName ??= string.Empty;
        settings.ProjectDeptAddress ??= string.Empty;
        settings.ProjectDeptLng ??= string.Empty;
        settings.ProjectDeptLat ??= string.Empty;
        settings.WarehouseName ??= string.Empty;
        settings.WarehouseLng ??= string.Empty;
        settings.WarehouseLat ??= string.Empty;
        settings.OllamaUrl ??= "http://localhost:11434";
        settings.OllamaModel ??= string.Empty;
        settings.AccentColor ??= "#2563EB";
        if (!System.Text.RegularExpressions.Regex.IsMatch(settings.AccentColor, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$"))
        {
            settings.AccentColor = "#2563EB";
        }

        // 备份保留份数非法（缺失 / 0 / 负数）时回退默认 15 份，至少保留 1 份
        if (settings.BackupRetentionCount <= 0)
        {
            settings.BackupRetentionCount = 15;
        }

        // 额度上限：负数视为非法（清空为「不显示额度条」）；地图默认值由字段初始化器给出
        if (settings.AiMonthlyCallLimit is < 0)
        {
            settings.AiMonthlyCallLimit = null;
        }

        if (settings.MapMonthlyQuota is < 0)
        {
            settings.MapMonthlyQuota = null;
        }

        return settings;
    }

    /// <summary>解析固定用户配置路径；新配置不存在时迁移旧配置，已存在时只补齐缺失的高德凭证。</summary>
    internal static string ResolveSettingsFilePath()
    {
        return ResolveSettingsFilePath(AppPaths.UserSettingsFilePath, GetLegacySettingsCandidates());
    }

    /// <summary>迁移旧配置或创建默认配置；新配置已存在时只补齐空的高德凭证。独立路径参数供单元测试隔离真实用户目录。</summary>
    internal static string ResolveSettingsFilePath(string targetPath, IEnumerable<string?> legacyCandidates)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(targetPath))
        {
            TryMergeMissingAmapCredentials(targetPath, legacyCandidates);
        }
        else
        {
            TryMigrateLegacySettings(targetPath, legacyCandidates);

            if (!File.Exists(targetPath))
            {
                SaveSettingsTo(targetPath, new AppSettings());
            }
        }

        return targetPath;
    }

    /// <summary>一次性合并标记：存在表示已评估过高德凭证，不再从旧配置回填。</summary>
    private static string MergeMarkerPath(string targetPath) =>
        Path.Combine(Path.GetDirectoryName(targetPath) ?? string.Empty, "settings.key-merged");

    /// <summary>
    /// 新配置已存在时补齐缺失的高德凭证：只在新文件中 Key / 安全密钥为空、旧配置有值时写入，
    /// 绝不覆盖已有值，也不合并其他设置；成功后写标记文件，避免用户主动清空 Key 后被旧配置回填。
    /// </summary>
    private static void TryMergeMissingAmapCredentials(string targetPath, IEnumerable<string?> legacyCandidates)
    {
        var markerPath = MergeMarkerPath(targetPath);
        if (File.Exists(markerPath))
        {
            return;
        }

        try
        {
            var current = LoadSettingsFrom(targetPath);
            if (!string.IsNullOrWhiteSpace(current.AmapJsApiKey)
                && !string.IsNullOrWhiteSpace(current.AmapSecurityCode))
            {
                WriteMergeMarker(markerPath);
                return;
            }

            var merged = false;
            foreach (var candidate in legacyCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var legacy = Path.GetFullPath(candidate);
                if (string.Equals(legacy, targetPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacy))
                {
                    continue;
                }

                AppSettings legacySettings;
                try
                {
                    legacySettings = LoadSettingsFrom(legacy);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SettingsService] 读取旧配置失败，跳过：{legacy}，{ex.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(current.AmapJsApiKey)
                    && !string.IsNullOrWhiteSpace(legacySettings.AmapJsApiKey))
                {
                    current.AmapJsApiKey = legacySettings.AmapJsApiKey;
                    merged = true;
                }

                if (string.IsNullOrWhiteSpace(current.AmapSecurityCode)
                    && !string.IsNullOrWhiteSpace(legacySettings.AmapSecurityCode))
                {
                    current.AmapSecurityCode = legacySettings.AmapSecurityCode;
                    merged = true;
                }

                if (!string.IsNullOrWhiteSpace(current.AmapJsApiKey)
                    && !string.IsNullOrWhiteSpace(current.AmapSecurityCode))
                {
                    break;
                }
            }

            if (merged)
            {
                SaveSettingsTo(targetPath, current);
                System.Diagnostics.Debug.WriteLine("[SettingsService] 已从旧配置补齐缺失的高德凭证。");
            }

            // 已有 Key 时也落标记：之后用户主动清空 Key 不再回填
            if (merged || !string.IsNullOrWhiteSpace(current.AmapJsApiKey))
            {
                WriteMergeMarker(markerPath);
            }
        }
        catch (Exception ex)
        {
            // 目标文件损坏或不可写时不阻断启动，留给 Load() 的默认值兜底
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 合并旧配置高德凭证失败：{ex.Message}");
        }
    }

    /// <summary>写入合并标记；失败只影响下次重复检查，不影响启动。</summary>
    private static void WriteMergeMarker(string markerPath)
    {
        try
        {
            File.WriteAllText(markerPath, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 写入合并标记失败：{ex.Message}");
        }
    }

    /// <summary>旧版配置候选，优先迁移升级前实际生效的自定义配置。</summary>
    private static IEnumerable<string?> GetLegacySettingsCandidates()
    {
        var legacyDefaultDirectory = LegacyDefaultSettingsDirectory;
        var legacyAppDataDirectory = ResolveLegacySettingsDirectory();
        return new[]
        {
            GetLegacyCustomSettingsFile(),
            Path.Combine(legacyDefaultDirectory, "settings.json"),
            Path.Combine(legacyDefaultDirectory, "AppSettings.json"),
            Path.Combine(AppContext.BaseDirectory, "settings.json"),
            Path.Combine(AppContext.BaseDirectory, "AppSettings.json"),
            Path.Combine(legacyAppDataDirectory, "settings.json"),
            Path.Combine(AppPaths.UserSettingsDirectory, "settings.json")
        };
    }

    /// <summary>解析更早期使用的 WinUI 应用数据目录，仅用于配置迁移。</summary>
    private static string ResolveLegacySettingsDirectory()
    {
        try
        {
            var folder = Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                return folder;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 旧应用数据目录不可用，回退 LocalApplicationData：{ex.Message}");
        }

        return AppPaths.UserSettingsDirectory;
    }

    /// <summary>把首个存在的旧配置复制到用户目录，源文件保持不变。</summary>
    private static void TryMigrateLegacySettings(string targetPath, IEnumerable<string?> legacyCandidates)
    {
        try
        {
            foreach (var candidate in legacyCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var legacy = Path.GetFullPath(candidate);
                if (!string.Equals(legacy, targetPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy))
                {
                    File.Copy(legacy, targetPath, overwrite: false);
                    System.Diagnostics.Debug.WriteLine($"[SettingsService] 已从旧位置迁移配置：{legacy}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 迁移旧配置失败：{ex.Message}");
        }
    }
}
