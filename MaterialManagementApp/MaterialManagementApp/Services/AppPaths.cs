using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaterialManagementApp.Services;

/// <summary>
/// 安装期数据目录解析。安装程序会把用户在向导中选择的数据存储路径写入
/// 程序目录下的 install-config.json，本类负责读取它，
/// 让数据库、日志与 settings.json 统一落到用户指定的数据目录。
/// 开发态（无安装程序生成该文件）时 <see cref="DataRoot"/> 为空，
/// 各服务退回原有的「程序目录」方案，不影响 F5 调试。
/// </summary>
public static class AppPaths
{
    /// <summary>安装程序写入的数据目录配置文件（与可执行文件同目录）。</summary>
    private const string InstallConfigFileName = "install-config.json";

    /// <summary>
    /// 用户在安装向导中选择的数据存储根目录；未安装或未配置时为 null。
    /// 目录存在性不做保证，由使用方在使用前自行创建。
    /// </summary>
    public static string? DataRoot { get; }

    /// <summary>
    /// 本机应用数据根目录：%LOCALAPPDATA%\MaterialSystem。
    /// 数据库备份统一存放于此，与程序目录（exe/dll/资源）分离。
    /// </summary>
    public static string LocalAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaterialSystem");

    /// <summary>用户配置目录：%LOCALAPPDATA%\MaterialManagementApp。</summary>
    public static string UserSettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaterialManagementApp");

    /// <summary>用户配置文件：%LOCALAPPDATA%\MaterialManagementApp\AppSettings.json。</summary>
    public static string UserSettingsFilePath => Path.Combine(UserSettingsDirectory, "AppSettings.json");

    /// <summary>
    /// 程序目录下的数据目录：主程序 exe 所在目录\Data。
    /// 数据库默认存放于此，软件拷到哪数据就跟到哪（绿色便携）。
    /// 取 exe 目录用 AppContext.BaseDirectory：不随当前工作目录漂移，
    /// 单文件发布/普通发布均指向主程序所在目录，等价于 Python 的
    /// os.path.dirname(sys.executable)。
    /// </summary>
    public static string ProgramDataDir => Path.Combine(AppContext.BaseDirectory, "Data");

    /// <summary>数据库备份目录：%LOCALAPPDATA%\MaterialSystem\Backup。</summary>
    public static string BackupDir => Path.Combine(LocalAppDataRoot, "Backup");

    /// <summary>每日自动备份目录：Backup\daily，按 yyyyMMdd.db 每天一份，保留最近 30 天。</summary>
    public static string BackupDailyDir => Path.Combine(BackupDir, "daily");

    /// <summary>
    /// 默认归档根目录：安装版取安装向导选择的 <see cref="DataRoot"/>，
    /// 便携版取程序目录下的 <see cref="ProgramDataDir"/>，与数据库现有解析顺序一致。
    /// 用户可在设置页覆盖（AppSettings.ArchiveRootPath），覆盖后以设置为准。
    /// </summary>
    public static string DefaultArchiveRoot => DataRoot ?? ProgramDataDir;

    /// <summary>归档根不可写时的回退目录：%LOCALAPPDATA%\MaterialSystem\Storage。</summary>
    public static string StorageFallbackRoot => Path.Combine(LocalAppDataRoot, "Storage");

    /// <summary>归档根下的导入目录：&lt;归档根&gt;\Imports。</summary>
    /// <param name="archiveRoot">归档根目录绝对路径。</param>
    public static string ImportsDir(string archiveRoot) => Path.Combine(archiveRoot, "Imports");

    /// <summary>归档根下的导出目录：&lt;归档根&gt;\Exports。</summary>
    /// <param name="archiveRoot">归档根目录绝对路径。</param>
    public static string ExportsDir(string archiveRoot) => Path.Combine(archiveRoot, "Exports");

    /// <summary>归档根下的扫描照片目录：&lt;归档根&gt;\ScanPhotos（本轮只创建，不启用）。</summary>
    /// <param name="archiveRoot">归档根目录绝对路径。</param>
    public static string ScanPhotosDir(string archiveRoot) => Path.Combine(archiveRoot, "ScanPhotos");

    /// <summary>某类导入文件的日期目录：Imports\&lt;类别&gt;\yyyyMMdd。</summary>
    /// <param name="archiveRoot">归档根目录绝对路径。</param>
    /// <param name="category">文件类别（如 合同 / 单据）。</param>
    /// <param name="date">归档日期。</param>
    public static string ImportDateDir(string archiveRoot, string category, DateTime date) =>
        Path.Combine(ImportsDir(archiveRoot), category, date.ToString("yyyyMMdd"));

    /// <summary>某类导出文件的日期目录：Exports\&lt;类别&gt;\yyyyMMdd。</summary>
    /// <param name="archiveRoot">归档根目录绝对路径。</param>
    /// <param name="category">文件类别（如 Excel / Word）。</param>
    /// <param name="date">归档日期。</param>
    public static string ExportDateDir(string archiveRoot, string category, DateTime date) =>
        Path.Combine(ExportsDir(archiveRoot), category, date.ToString("yyyyMMdd"));

    /// <summary>OCR 缓存目录：%LOCALAPPDATA%\MaterialSystem\OcrCache。</summary>
    public static string OcrCacheDir => Path.Combine(LocalAppDataRoot, "OcrCache");

    /// <summary>通用临时目录：%LOCALAPPDATA%\MaterialSystem\Temp。</summary>
    public static string TempDir => Path.Combine(LocalAppDataRoot, "Temp");

    /// <summary>
    /// 创建归档结构与本机缓存目录（只建空目录，不清空已有内容）：
    /// Imports / Exports / ScanPhotos 位于归档根，OcrCache / Temp 位于本机应用数据根。
    /// 单个目录创建失败不抛出：调用方继续尝试其余目录，失败原因由上层日志记录。
    /// </summary>
    /// <param name="archiveRoot">当前生效的归档根目录绝对路径。</param>
    /// <returns>全部创建成功返回 true；任一目录失败返回 false。</returns>
    public static bool TryEnsureCreated(string archiveRoot)
    {
        var directories = new[]
        {
            ImportsDir(archiveRoot),
            ExportsDir(archiveRoot),
            ScanPhotosDir(archiveRoot),
            OcrCacheDir,
            TempDir
        };

        var allCreated = true;
        foreach (var directory in directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                allCreated = false;
                System.Diagnostics.Debug.WriteLine($"[AppPaths] 创建目录失败：{directory}，{ex.Message}");
            }
        }

        return allCreated;
    }

    static AppPaths()
    {
        DataRoot = ReadDataRoot();
    }

    /// <summary>读取 install-config.json 中的 dataRoot 字段。</summary>
    /// <returns>配置的目录绝对路径；文件缺失、字段为空或解析失败时返回 null。</returns>
    private static string? ReadDataRoot()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, InstallConfigFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            // 旧版安装程序用 GBK 写入，新版用 UTF-8。ReadBestEncoding 会自动检测并解码出正确的中文路径。
            var json = ReadBestEncoding(path);
            var config = JsonSerializer.Deserialize<InstallConfig>(json);
            var root = config?.DataRoot;

            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            // 安装程序可能写入正斜杠或反斜杠，统一成本地分隔符后再交给 IO API
            return Path.GetFullPath(root.Trim().Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception ex)
        {
            // 配置损坏不能阻断启动：退回程序目录方案，用户仍可正常使用
            System.Diagnostics.Debug.WriteLine($"[AppPaths] 读取安装配置失败，使用默认数据目录：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 自动检测文件编码并解码：先尝试 UTF-8，若解码结果包含替换字符（U+FFFD，
    /// 表明原始字节不是合法 UTF-8 序列），则回退到 GBK（代码页 936）。
    /// 用来兼容旧版 Inno Setup 默认 ANSI 写入的 install-config.json。
    /// </summary>
    /// <param name="filePath">配置文件的完整路径。</param>
    /// <returns>解码后的文本内容。</returns>
    private static string ReadBestEncoding(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);

        // UTF-8 解码
        var utf8 = Encoding.UTF8.GetString(bytes);

        // 只要出现 U+FFFD（Unicode 替换字符）就说明字节不是合法 UTF-8，
        // 很可能是 Inno Setup 旧版写入的 GBK，回退到中文 ANSI 代码页
        if (utf8.Contains('\uFFFD'))
        {
            try
            {
                var gbk = Encoding.GetEncoding(936).GetString(bytes);
                if (!gbk.Contains('\uFFFD'))
                {
                    // 解码结果正确（没有替换字符），顺手把文件重写为 UTF-8，
                    // 下次启动就不用再走 GBK 回退了
                    try
                    {
                        File.WriteAllText(filePath, gbk, Encoding.UTF8);
                        System.Diagnostics.Debug.WriteLine("[AppPaths] 检测到 GBK 编码的 install-config.json，已自动转换为 UTF-8。");
                    }
                    catch (Exception rewriteEx)
                    {
                        // 重写失败（只读 / 权限不足）不影响当前读取结果
                        System.Diagnostics.Debug.WriteLine($"[AppPaths] install-config.json 重写 UTF-8 失败：{rewriteEx.Message}");
                    }

                    return gbk;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppPaths] GBK 回退解码失败：{ex.Message}");
            }
        }

        return utf8;
    }

    /// <summary>install-config.json 的结构。</summary>
    private sealed class InstallConfig
    {
        /// <summary>数据存储根目录，由安装向导选择。</summary>
        [JsonPropertyName("dataRoot")]
        public string? DataRoot { get; set; }
    }
}
