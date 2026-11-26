using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>SettingsService 用户配置路径、一次性迁移与读写校正逻辑的单元测试。</summary>
public sealed class SettingsServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mma-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ==================== 用户配置路径与迁移 ====================

    [Fact]
    public void UserSettingsFilePath_固定在当前用户MaterialManagementApp目录()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MaterialManagementApp",
            "AppSettings.json");

        Assert.Equal(expected, AppPaths.UserSettingsFilePath);
    }

    [Fact]
    public void ResolveSettingsFilePath_无旧配置时创建默认配置()
    {
        var target = Path.Combine(TempDir(), "AppSettings.json");

        var resolved = SettingsService.ResolveSettingsFilePath(target, Array.Empty<string?>());

        Assert.Equal(target, resolved);
        Assert.True(File.Exists(target));
        Assert.Equal(10, SettingsService.LoadSettingsFrom(target).LowStockThreshold);
    }

    [Fact]
    public void ResolveSettingsFilePath_迁移旧配置并保留源文件()
    {
        var legacy = Path.Combine(TempDir(), "settings.json");
        SettingsService.SaveSettingsTo(legacy, new AppSettings { AmapJsApiKey = "legacy-key" });
        var target = Path.Combine(TempDir(), "AppSettings.json");

        SettingsService.ResolveSettingsFilePath(target, new[] { legacy });

        Assert.True(File.Exists(legacy));
        Assert.Equal("legacy-key", SettingsService.LoadSettingsFrom(target).AmapJsApiKey);
    }

    [Fact]
    public void ResolveSettingsFilePath_新配置存在时不被旧配置覆盖()
    {
        var target = Path.Combine(TempDir(), "AppSettings.json");
        SettingsService.SaveSettingsTo(target, new AppSettings { AmapJsApiKey = "current-key" });
        var legacy = Path.Combine(TempDir(), "settings.json");
        SettingsService.SaveSettingsTo(legacy, new AppSettings { AmapJsApiKey = "legacy-key" });

        SettingsService.ResolveSettingsFilePath(target, new[] { legacy });

        Assert.Equal("current-key", SettingsService.LoadSettingsFrom(target).AmapJsApiKey);
    }

    [Fact]
    public void ResolveSettingsFilePath_新配置Key为空时从旧配置补齐且不覆盖其他设置()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "AppSettings.json");
        SettingsService.SaveSettingsTo(target, new AppSettings { ProjectName = "当前项目" });
        var legacy = Path.Combine(dir, "settings.json");
        SettingsService.SaveSettingsTo(legacy, new AppSettings
        {
            AmapJsApiKey = "legacy-key",
            AmapSecurityCode = "legacy-secret",
            ProjectName = "旧项目"
        });

        SettingsService.ResolveSettingsFilePath(target, new[] { legacy });

        var loaded = SettingsService.LoadSettingsFrom(target);
        Assert.Equal("legacy-key", loaded.AmapJsApiKey);
        Assert.Equal("legacy-secret", loaded.AmapSecurityCode);
        Assert.Equal("当前项目", loaded.ProjectName);
        Assert.True(File.Exists(Path.Combine(dir, "settings.key-merged")));
    }

    [Fact]
    public void ResolveSettingsFilePath_新配置已有Key时不被旧配置覆盖并写合并标记()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "AppSettings.json");
        SettingsService.SaveSettingsTo(target, new AppSettings { AmapJsApiKey = "current-key" });
        var legacy = Path.Combine(dir, "settings.json");
        SettingsService.SaveSettingsTo(legacy, new AppSettings { AmapJsApiKey = "legacy-key" });

        SettingsService.ResolveSettingsFilePath(target, new[] { legacy });

        Assert.Equal("current-key", SettingsService.LoadSettingsFrom(target).AmapJsApiKey);
        Assert.True(File.Exists(Path.Combine(dir, "settings.key-merged")));
    }

    [Fact]
    public void ResolveSettingsFilePath_用户清空Key后不会再次回填()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "AppSettings.json");
        var legacy = Path.Combine(dir, "settings.json");
        SettingsService.SaveSettingsTo(legacy, new AppSettings { AmapJsApiKey = "legacy-key" });
        SettingsService.SaveSettingsTo(target, new AppSettings());

        // 首次启动：从旧配置补齐并落标记
        SettingsService.ResolveSettingsFilePath(target, new[] { legacy });
        Assert.Equal("legacy-key", SettingsService.LoadSettingsFrom(target).AmapJsApiKey);

        // 用户主动清空后保存
        var cleared = SettingsService.LoadSettingsFrom(target);
        cleared.AmapJsApiKey = string.Empty;
        SettingsService.SaveSettingsTo(target, cleared);

        SettingsService.ResolveSettingsFilePath(target, new[] { legacy });

        Assert.Equal(string.Empty, SettingsService.LoadSettingsFrom(target).AmapJsApiKey);
    }

    [Fact]
    public void ResolveSettingsFilePath_新配置已有Key但缺安全密钥时只补安全密钥()
    {
        var dir = TempDir();
        var target = Path.Combine(dir, "AppSettings.json");
        SettingsService.SaveSettingsTo(target, new AppSettings { AmapJsApiKey = "current-key" });
        var legacy = Path.Combine(dir, "settings.json");
        SettingsService.SaveSettingsTo(legacy, new AppSettings
        {
            AmapJsApiKey = "legacy-key",
            AmapSecurityCode = "legacy-secret"
        });

        SettingsService.ResolveSettingsFilePath(target, new[] { legacy });

        var loaded = SettingsService.LoadSettingsFrom(target);
        Assert.Equal("current-key", loaded.AmapJsApiKey);
        Assert.Equal("legacy-secret", loaded.AmapSecurityCode);
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

}
