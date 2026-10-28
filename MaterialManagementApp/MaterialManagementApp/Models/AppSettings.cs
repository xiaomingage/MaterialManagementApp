namespace MaterialManagementApp.Models;

using System.Text.Json.Serialization;

/// <summary>
/// 应用设置模型，序列化为 settings.json。
/// 所有字段均给出默认值，避免反序列化旧版配置时出现 null 或 0 导致下游异常。
/// </summary>
public sealed class AppSettings
{
    /// <summary>库存预警阈值，库存低于该值即视为预警。</summary>
    public int LowStockThreshold { get; set; } = 10;

    /// <summary>单据默认工程名称。</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>单据默认使用单位。</summary>
    public string UsingUnit { get; set; } = string.Empty;

    /// <summary>单据默认计量单位。</summary>
    public string DefaultUnit { get; set; } = string.Empty;

    /// <summary>单据默认经办人。</summary>
    public string DefaultHandler { get; set; } = string.Empty;

    /// <summary>界面主题，取值 Default / Light / Dark。</summary>
    /// <remarks>旧版字段，仅供旧配置迁移读取；新逻辑以 <see cref="ThemeMode"/> 为准。</remarks>
    public string Theme { get; set; } = "Default";

    /// <summary>界面主题模式：Light（浅色）/ Dark（深色）/ Acrylic（透明毛玻璃）/ System（跟随系统）。</summary>
    public string ThemeMode { get; set; } = "System";

    /// <summary>透明毛玻璃模式使用的基底主题（记住最近一次选择的浅色/深色），取值 Light / Dark。</summary>
    public string BaseTheme { get; set; } = "Light";

    /// <summary>关闭窗口时是否最小化到托盘后台运行；为 false 时点击关闭按钮直接退出程序。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>强调色（十六进制，如 #2563EB），为空时使用系统默认强调色。</summary>
    public string AccentColor { get; set; } = "#2563EB";

    /// <summary>数据库文件的完整路径；为空表示使用程序目录下 Data\MaterialSystem.db（配置了绝对路径则优先使用）。</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>本地 AI（Ollama）服务地址，默认本机 11434 端口。</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>本地 AI 使用的模型名称；为空表示未配置本地 AI，OCR 直接走系统识别。</summary>
    public string OllamaModel { get; set; } = string.Empty;

    /// <summary>数据库备份保留份数，超出后自动清理最旧的备份，最小保留 1 份。</summary>
    public int BackupRetentionCount { get; set; } = 15;

    /// <summary>
    /// 是否已配置可用的本地 AI（地址与模型名都不为空）。
    /// OCR 识别优先走本地 AI，未配置时回退系统 OCR。
    /// 派生属性不写回配置文件，避免 settings.json 里出现冗余字段。
    /// </summary>
    [JsonIgnore]
    public bool IsLocalAiConfigured =>
        !string.IsNullOrWhiteSpace(OllamaUrl) && !string.IsNullOrWhiteSpace(OllamaModel);
}
