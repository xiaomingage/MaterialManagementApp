using System;
using System.IO;
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

            // 先写临时文件再原子替换：避免写入途中断电导致配置文件被截断成无效 JSON
            var tempPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(Settings, _jsonOptions));
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
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
    /// 解析配置文件路径：安装版放在向导中选择的数据存储目录，与数据库同级，便于随数据库一起备份；
    /// 开发态仍优先本地应用数据目录，异常时回退到用户本地应用数据目录。
    /// </summary>
    /// <returns>settings.json 的绝对路径。</returns>
    private static string ResolveSettingsFilePath()
    {
        if (AppPaths.DataRoot is { } dataRoot)
        {
            Directory.CreateDirectory(dataRoot);
            var target = Path.Combine(dataRoot, "settings.json");
            TryMigrateLegacySettings(target);
            return target;
        }

        var directory = ResolveLegacySettingsDirectory();
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
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
