using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// SettingsService 配置文件选择（settings.path 引导重定向）与读写校正逻辑的单元测试。
/// 测试进程的默认配置目录 = 测试输出目录\Data（测试环境无 install-config.json，AppPaths.DataRoot 为 null），
/// 所有文件 IO 均落在测试输出目录内，不触碰真实用户配置。
/// xunit 同一测试类内用例顺序执行，且每个用例的构造函数都会重置引导文件，用例之间互不依赖。
/// </summary>
public sealed class SettingsServiceTests
{
    private static string DataDir => SettingsService.DefaultSettingsDirectory;

    private static string BootPath => SettingsService.SettingsRedirectFilePath;

    private static string DefaultSettingsJson => Path.Combine(DataDir, "settings.json");

    public SettingsServiceTests()
    {
        Directory.CreateDirectory(DataDir);
        ResetBootFile();
    }

    private static void ResetBootFile()
    {
        if (File.Exists(BootPath))
        {
            File.Delete(BootPath);
        }
    }

    /// <summary>创建一个内容为 "{}" 的临时 json 文件，返回完整路径。</summary>
    private static string NewTempJson()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mma-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ==================== 引导文件：选择 / 恢复默认 ====================

    [Fact]
    public void SetCustomSettingsFile_写入后引导文件存在且Get返回完整路径()
    {
        var target = NewTempJson();

        SettingsService.SetCustomSettingsFile(target);

        Assert.True(File.Exists(BootPath), "引导文件 settings.path 应已写入");
        Assert.Equal(Path.GetFullPath(target), SettingsService.GetCustomSettingsFile());
    }

    [Fact]
    public void SetCustomSettingsFile_相对路径写入后归一化为绝对路径()
    {
        SettingsService.SetCustomSettingsFile("relative-settings.json");

        var content = File.ReadAllText(BootPath).Trim();
        Assert.Equal(Path.GetFullPath("relative-settings.json"), content);
    }

    [Fact]
    public void SetCustomSettingsFile_传null删除引导文件恢复默认()
    {
        var target = NewTempJson();
        SettingsService.SetCustomSettingsFile(target);
        Assert.True(File.Exists(BootPath));

        SettingsService.SetCustomSettingsFile(null);

        Assert.False(File.Exists(BootPath), "传 null 应删除引导文件");
        Assert.Null(SettingsService.GetCustomSettingsFile());
    }

    [Fact]
    public void SetCustomSettingsFile_传空串删除引导文件恢复默认()
    {
        var target = NewTempJson();
        SettingsService.SetCustomSettingsFile(target);

        SettingsService.SetCustomSettingsFile(string.Empty);

        Assert.False(File.Exists(BootPath));
        Assert.Null(SettingsService.GetCustomSettingsFile());
    }

    // ==================== 引导文件：读取容错 ====================

    [Fact]
    public void GetCustomSettingsFile_引导文件缺失返回null()
    {
        Assert.Null(SettingsService.GetCustomSettingsFile());
    }

    [Fact]
    public void GetCustomSettingsFile_指向的文件不存在返回null()
    {
        SettingsService.SetCustomSettingsFile(Path.Combine(TempDir(), "ghost.json"));

        Assert.Null(SettingsService.GetCustomSettingsFile());
    }

    [Fact]
    public void GetCustomSettingsFile_引导文件为空内容返回null()
    {
        File.WriteAllText(BootPath, string.Empty);

        Assert.Null(SettingsService.GetCustomSettingsFile());
    }

    [Fact]
    public void GetCustomSettingsFile_引导文件只有空白行返回null()
    {
        File.WriteAllText(BootPath, "   \n \t \n");

        Assert.Null(SettingsService.GetCustomSettingsFile());
    }

    [Fact]
    public void GetCustomSettingsFile_读取时去除首尾空白()
    {
        var target = NewTempJson();
        File.WriteAllText(BootPath, $"  {target}  \n");

        Assert.Equal(Path.GetFullPath(target), SettingsService.GetCustomSettingsFile());
    }

    // ==================== 配置文件路径解析 ====================

    [Fact]
    public void ResolveSettingsFilePath_无重定向时返回默认目录settings点json()
    {
        // 预先创建默认 settings.json，短路旧配置迁移，保证断言确定性
        File.WriteAllText(DefaultSettingsJson, "{}");

        Assert.Equal(DefaultSettingsJson, SettingsService.ResolveSettingsFilePath());
    }

    [Fact]
    public void ResolveSettingsFilePath_有重定向时返回自定义路径()
    {
        var target = NewTempJson();
        SettingsService.SetCustomSettingsFile(target);

        Assert.Equal(Path.GetFullPath(target), SettingsService.ResolveSettingsFilePath());
    }

    [Fact]
    public void ResolveSettingsFilePath_重定向目标不存在时回退默认()
    {
        File.WriteAllText(DefaultSettingsJson, "{}");
        SettingsService.SetCustomSettingsFile(Path.Combine(TempDir(), "ghost.json"));

        Assert.Equal(DefaultSettingsJson, SettingsService.ResolveSettingsFilePath());
    }

    [Fact]
    public void 默认配置目录位于程序目录Data下与exe同盘携带()
    {
        // 测试环境无 install-config.json：默认目录应为 测试输出目录\Data（跟 exe 走，不在 C 盘 AppData）
        Assert.EndsWith($"Data{Path.DirectorySeparatorChar}", DataDir + Path.DirectorySeparatorChar);
        Assert.StartsWith(AppContext.BaseDirectory, DataDir, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== 配置读写往返与容错 ====================

    [Fact]
    public void SaveLoadSettingsTo_往返后字段全部保留()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        var original = new AppSettings
        {
            LowStockThreshold = 25,
            ProjectName = "信宜项目",
            UsingUnit = "某使用单位",
            DefaultUnit = "台",
            DefaultHandler = "张三",
            ThemeMode = "Dark",
            BaseTheme = "Dark",
            CloseAction = "Exit",
            MinimizeToTray = false,
            AccentColor = "#059669",
            DatabasePath = @"D:\MaterialData\MaterialSystem.db",
            OllamaUrl = "http://127.0.0.1:11434",
            OllamaModel = "qwen2.5vl:7b",
            OcrEngineMode = "OnlineAi",
            AiProvider = "Kimi",
            AiBaseUrl = "https://api.moonshot.cn/v1",
            AiApiKey = "sk-test",
            AiModelName = "moonshot-v1-8k-vision-preview",
            BackupRetentionCount = 7,
        };

        SettingsService.SaveSettingsTo(path, original);
        var loaded = SettingsService.LoadSettingsFrom(path);

        Assert.Equal(25, loaded.LowStockThreshold);
        Assert.Equal("信宜项目", loaded.ProjectName);
        Assert.Equal("某使用单位", loaded.UsingUnit);
        Assert.Equal("台", loaded.DefaultUnit);
        Assert.Equal("张三", loaded.DefaultHandler);
        Assert.Equal("Dark", loaded.ThemeMode);
        Assert.Equal("Dark", loaded.BaseTheme);
        Assert.Equal("Exit", loaded.CloseAction);
        Assert.False(loaded.MinimizeToTray, "CloseAction=Exit 时 MinimizeToTray 应同步为 false");
        Assert.Equal("#059669", loaded.AccentColor);
        Assert.Equal(@"D:\MaterialData\MaterialSystem.db", loaded.DatabasePath);
        Assert.Equal("http://127.0.0.1:11434", loaded.OllamaUrl);
        Assert.Equal("qwen2.5vl:7b", loaded.OllamaModel);
        Assert.Equal("OnlineAi", loaded.OcrEngineMode);
        Assert.Equal("Kimi", loaded.AiProvider);
        Assert.Equal("https://api.moonshot.cn/v1", loaded.AiBaseUrl);
        Assert.Equal("sk-test", loaded.AiApiKey);
        Assert.Equal("moonshot-v1-8k-vision-preview", loaded.AiModelName);
        Assert.Equal(7, loaded.BackupRetentionCount);
    }

    [Fact]
    public void SaveSettingsTo_保存后目录无临时文件残留()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");

        SettingsService.SaveSettingsTo(path, new AppSettings());
        SettingsService.SaveSettingsTo(path, new AppSettings());

        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n));
    }

    [Fact]
    public void LoadSettingsFrom_文件缺失返回默认配置()
    {
        var settings = SettingsService.LoadSettingsFrom(Path.Combine(TempDir(), "missing.json"));

        Assert.Equal(10, settings.LowStockThreshold);
        Assert.Equal("Ask", settings.CloseAction);
        Assert.Equal(string.Empty, settings.DatabasePath);
        Assert.Equal("http://localhost:11434", settings.OllamaUrl);
    }

    [Fact]
    public void LoadSettingsFrom_内容为null字面量返回默认配置()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        File.WriteAllText(path, "null");

        var settings = SettingsService.LoadSettingsFrom(path);

        Assert.Equal(10, settings.LowStockThreshold);
        Assert.Equal("Ask", settings.CloseAction);
    }

    [Fact]
    public void LoadSettingsFrom_配置损坏时抛出异常由调用方兜底()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        File.WriteAllText(path, "{ 这不是合法 JSON");

        Assert.ThrowsAny<Exception>(() => SettingsService.LoadSettingsFrom(path));
    }

    // ==================== Normalize 字段校正 ====================

    [Fact]
    public void Normalize_关闭行为非法且旧开关为true时迁移为最小化到托盘()
    {
        var settings = new AppSettings { CloseAction = "bogus", MinimizeToTray = true };

        var result = SettingsService.Normalize(settings);

        Assert.Equal("MinimizeToTray", result.CloseAction);
        Assert.True(result.MinimizeToTray, "迁移后应同步回写旧字段保持序列化一致");
    }

    [Fact]
    public void Normalize_关闭行为非法且旧开关为false时回退每次询问()
    {
        var settings = new AppSettings { CloseAction = "bogus", MinimizeToTray = false };

        var result = SettingsService.Normalize(settings);

        Assert.Equal("Ask", result.CloseAction);
        Assert.False(result.MinimizeToTray);
    }

    [Fact]
    public void Normalize_关闭行为合法时同步旧开关字段()
    {
        var exit = SettingsService.Normalize(new AppSettings { CloseAction = "Exit" });
        Assert.Equal("Exit", exit.CloseAction);
        Assert.False(exit.MinimizeToTray);

        var tray = SettingsService.Normalize(new AppSettings { CloseAction = "MinimizeToTray" });
        Assert.Equal("MinimizeToTray", tray.CloseAction);
        Assert.True(tray.MinimizeToTray);

        var ask = SettingsService.Normalize(new AppSettings { CloseAction = "Ask" });
        Assert.Equal("Ask", ask.CloseAction);
        Assert.False(ask.MinimizeToTray);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Normalize_库存阈值非正数回退默认10(int invalid)
    {
        var result = SettingsService.Normalize(new AppSettings { LowStockThreshold = invalid });

        Assert.Equal(10, result.LowStockThreshold);
    }

    [Fact]
    public void Normalize_库存阈值合法值保留()
    {
        var result = SettingsService.Normalize(new AppSettings { LowStockThreshold = 25 });

        Assert.Equal(25, result.LowStockThreshold);
    }

    [Theory]
    [InlineData("Default", "System")]
    [InlineData("Light", "Light")]
    [InlineData("Dark", "Dark")]
    public void Normalize_主题模式非法时从旧Theme字段迁移(string legacyTheme, string expected)
    {
        var result = SettingsService.Normalize(new AppSettings { ThemeMode = "bogus", Theme = legacyTheme });

        Assert.Equal(expected, result.ThemeMode);
    }

    [Fact]
    public void Normalize_主题模式合法时保留不迁移()
    {
        var result = SettingsService.Normalize(new AppSettings { ThemeMode = "Acrylic", Theme = "Dark" });

        Assert.Equal("Acrylic", result.ThemeMode);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Normalize_强调色非法回退经典蓝(string invalid)
    {
        var result = SettingsService.Normalize(new AppSettings { AccentColor = invalid });

        Assert.Equal("#2563EB", result.AccentColor);
    }

    [Theory]
    [InlineData("#2563EB")]
    [InlineData("#FF2563EB")]
    public void Normalize_强调色合法格式保留(string valid)
    {
        var result = SettingsService.Normalize(new AppSettings { AccentColor = valid });

        Assert.Equal(valid, result.AccentColor);
    }

    [Fact]
    public void Normalize_备份保留份数非正数回退默认15()
    {
        Assert.Equal(15, SettingsService.Normalize(new AppSettings { BackupRetentionCount = 0 }).BackupRetentionCount);
        Assert.Equal(15, SettingsService.Normalize(new AppSettings { BackupRetentionCount = -3 }).BackupRetentionCount);
        Assert.Equal(3, SettingsService.Normalize(new AppSettings { BackupRetentionCount = 3 }).BackupRetentionCount);
    }

    [Fact]
    public void Normalize_旧版配置缺失字段补默认值()
    {
        // 模拟旧版 settings.json：字符串字段反序列化为 null，应统一补空串/Ollama 默认地址
        var settings = new AppSettings
        {
            ProjectName = null!,
            UsingUnit = null!,
            DefaultUnit = null!,
            DefaultHandler = null!,
            DatabasePath = null!,
            OllamaUrl = null!,
            OllamaModel = null!,
            AccentColor = null!,
        };

        var result = SettingsService.Normalize(settings);

        Assert.Equal(string.Empty, result.ProjectName);
        Assert.Equal(string.Empty, result.UsingUnit);
        Assert.Equal(string.Empty, result.DefaultUnit);
        Assert.Equal(string.Empty, result.DefaultHandler);
        Assert.Equal(string.Empty, result.DatabasePath);
        Assert.Equal(string.Empty, result.OllamaModel);
        Assert.Equal("#2563EB", result.AccentColor);
        Assert.Equal("http://localhost:11434", result.OllamaUrl);
    }

    // ==================== 单例 Save 事件 ====================

    [Fact]
    public void Save_保存成功后触发SettingsChanged事件()
    {
        // 该用例首次触碰单例：先重置引导文件，确保单例解析到默认路径
        ResetBootFile();
        var instance = SettingsService.Instance;
        var fired = 0;
        instance.SettingsChanged += (_, _) => fired++;

        instance.Save();

        Assert.Equal(1, fired);
    }
}
