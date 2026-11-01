using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MaterialManagementApp.Models;

namespace MaterialManagementApp.Services;

/// <summary>
/// 应用配置服务（单例）。配置以 JSON 形式存放于本地应用数据目录，与 app.db 同级，
/// 便于用户直接查看与随数据库一起备份。
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

    /// <summary>settings.json 的完整路径，供设置页展示。</summary>
    public string SettingsFilePath { get; }

    /// <summary>
    /// 默认数据库存放目录：主程序 exe 所在目录\Data（AppContext.BaseDirectory）。
    /// 配置 DatabasePath 为空时数据库落在这里，软件拷到哪数据就跟到哪。
    /// 该目录由 <c>DatabaseService</c> 在解析路径时自动创建，本属性只给出位置，不做 IO。
    /// </summary>
    public static string DefaultDatabaseFolder => AppPaths.ProgramDataDir;

    /// <summary>引导文件名：内容为一行自定义 settings.json 的完整路径；文件缺失或为空表示使用默认位置。</summary>
    private const string SettingsRedirectFileName = "settings.path";

    /// <summary>
    /// settings.json 的默认存放目录：安装器指定的数据目录（历史安装留下的 install-config.json），
    /// 未配置时为程序目录\Data——配置跟 exe 走，拷贝整个程序文件夹即可带着配置搬家，不再落到 C 盘。
    /// </summary>
    public static string DefaultSettingsDirectory => AppPaths.DataRoot ?? AppPaths.ProgramDataDir;

    /// <summary>引导文件（settings.path）的完整路径，位于默认配置目录内。</summary>
    public static string SettingsRedirectFilePath => Path.Combine(DefaultSettingsDirectory, SettingsRedirectFileName);

    /// <summary>
    /// 读取引导文件中记录的自定义 settings.json 路径。
    /// 引导文件缺失、内容为空或指向的文件已不存在时返回 null（使用默认位置）。
    /// </summary>
    public static string? GetCustomSettingsFile()
    {
        try
        {
            var boot = SettingsRedirectFilePath;
            if (!File.Exists(boot))
            {
                return null;
            }

            var line = File.ReadAllLines(boot)
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
            // 引导文件读不出来不能阻断启动：回退默认位置
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 读取配置引导文件失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 写入或清除配置文件重定向（引导文件）。供设置页「选择配置文件」调用，
    /// 立即生效——下次启动按引导文件加载；path 为空表示恢复默认位置（删除引导文件）。
    /// </summary>
    /// <exception cref="IOException">目录不可写时抛出，由调用方提示用户。</exception>
    public static void SetCustomSettingsFile(string? path)
    {
        var boot = SettingsRedirectFilePath;
        Directory.CreateDirectory(DefaultSettingsDirectory);

        var trimmed = (path ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            if (File.Exists(boot))
            {
                File.Delete(boot);
            }
            return;
        }

        File.WriteAllText(boot, Path.GetFullPath(trimmed));
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
            if (!File.Exists(SettingsFilePath))
            {
                Settings = new AppSettings();
                return;
            }

            var json = File.ReadAllText(SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);

            // 文件中只写了 "null" 时反序列化结果为 null，同样回退默认值避免空引用
            Settings = Normalize(loaded ?? new AppSettings());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 读取配置失败，使用默认值：{ex.Message}");
            Settings = new AppSettings();
        }
    }

    /// <summary>将当前设置写入磁盘，成功后触发 <see cref="SettingsChanged"/>。</summary>
    /// <exception cref="IOException">目标目录无写入权限或磁盘不可写时抛出，由调用方提示用户。</exception>
    public void Save()
    {
        lock (_saveLock)
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再原子替换：避免写入途中断电导致配置文件被截断成无效 JSON。
            // 临时名带唯一后缀：残留的旧 .tmp 被杀毒/索引器短暂占用时不会顶撞本次保存；
            // 保存结束后无论成败都清掉本次临时文件，目录不留垃圾。
            var tempPath = $"{SettingsFilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(Settings, _jsonOptions));
                File.Move(tempPath, SettingsFilePath, overwrite: true);
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

        SettingsChanged?.Invoke(this, EventArgs.Empty);
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
    private static AppSettings Normalize(AppSettings settings)
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
        return settings;
    }

    /// <summary>
    /// 解析配置文件路径：优先按引导文件（settings.path）重定向到用户在设置页选择的
    /// settings.json；未重定向时使用默认目录（安装器数据目录或程序目录\Data）下的 settings.json，
    /// 与数据库同级，便于随数据一起备份；首次切换目录时自动迁移旧位置配置。
    /// </summary>
    /// <returns>settings.json 的绝对路径。</returns>
    private static string ResolveSettingsFilePath()
    {
        var directory = DefaultSettingsDirectory;
        Directory.CreateDirectory(directory);

        var target = GetCustomSettingsFile() ?? Path.Combine(directory, "settings.json");
        TryMigrateLegacySettings(target);
        return target;
    }

    /// <summary>
    /// 解析改造前使用的配置目录：WinUI 应用数据目录，不可用时回退 %LOCALAPPDATA%\MaterialManagementApp。
    /// </summary>
    /// <returns>配置目录的绝对路径。</returns>
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
            // 非打包 / 无包身份等场景可能不可用，回退保证配置仍可读写
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 应用数据目录不可用，回退 LocalApplicationData：{ex.Message}");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MaterialManagementApp");
    }

    /// <summary>
    /// 首次切换到安装版数据目录时，把旧位置已有的 settings.json 复制过来，
    /// 避免升级后此前配置的预警阈值、默认工程名等凭空消失。仅在目标文件不存在时执行一次。
    /// </summary>
    /// <param name="targetPath">新的配置文件完整路径。</param>
    private static void TryMigrateLegacySettings(string targetPath)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                return;
            }

            var candidates = new[]
            {
                Path.Combine(ResolveLegacySettingsDirectory(), "settings.json"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MaterialManagementApp",
                    "settings.json"),
            };

            foreach (var legacy in candidates)
            {
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
            // 迁移失败不阻断启动：用户仍可在设置页重新填写
            System.Diagnostics.Debug.WriteLine($"[SettingsService] 迁移旧配置失败：{ex.Message}");
        }
    }
}
