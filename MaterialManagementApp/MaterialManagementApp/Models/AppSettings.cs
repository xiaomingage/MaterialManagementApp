namespace MaterialManagementApp.Models;

using System;
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

    /// <summary>旧版字段（bool 开关），仅供旧配置迁移读取；新逻辑以 <see cref="CloseAction"/> 为准。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>
    /// 点击窗口关闭按钮（X）时的行为：Ask（每次弹窗询问）/ MinimizeToTray（最小化到托盘后台）/ Exit（直接退出）。
    /// 在关闭确认弹窗中勾选「记住我的选择」后会写回该字段，之后不再弹窗。
    /// </summary>
    public string CloseAction { get; set; } = "Ask";

    /// <summary>强调色（十六进制，如 #2563EB），为空时使用系统默认强调色。</summary>
    public string AccentColor { get; set; } = "#2563EB";

    /// <summary>数据库文件的完整路径；为空表示使用程序目录下 Data\MaterialSystem.db（配置了绝对路径则优先使用）。</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>本地 AI（Ollama）服务地址，默认本机 11434 端口。</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>本地 AI 使用的模型名称；为空表示未配置本地 AI，OCR 直接走系统识别。</summary>
    public string OllamaModel { get; set; } = string.Empty;

    /// <summary>
    /// OCR 引擎模式，记录用户选择的 OCR 引擎：
    /// LocalOllama（本地 Ollama AI 识别）/ LocalPaddle（本地 PaddleOCR 识别）/ OnlineAi（在线 AI 识别）；其他值回退系统 OCR。
    /// </summary>
    public string OcrEngineMode { get; set; } = "LocalOllama";

    /// <summary>
    /// AI 服务商：Ollama（本地）或 DeepSeek 等在线服务商。
    /// Ollama 视为 AiProvider 的一个选项，选择 Ollama 时读取 OllamaUrl / OllamaModel，否则读取在线 AI 配置。
    /// </summary>
    public string AiProvider { get; set; } = "DeepSeek";

    /// <summary>在线 AI 服务地址（Base URL，如 https://api.deepseek.com）；本地 Ollama 模式下不使用。</summary>
    public string AiBaseUrl { get; set; } = string.Empty;

    /// <summary>在线 AI 的 API Key；本地 Ollama 模式下不使用。内存中始终为明文，落盘时经 DPAPI 加密（见 <see cref="AiApiKeyStored"/>）。</summary>
    [JsonIgnore]
    public string AiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// settings.json 中实际序列化的 API Key 字段（字段名仍为 AiApiKey，值为密文）：
    /// 读文件时自动解密到 <see cref="AiApiKey"/>，写文件时自动加密，业务层全程只接触明文。
    /// 兼容规则：带 dpapi: 前缀的值走 DPAPI 解密（换机后解不开视为空，需重填）；
    /// 不带前缀的旧版明文原样读入，首次保存后自动转为密文，老用户无需重填。
    /// </summary>
    [JsonPropertyName("AiApiKey")]
    public string AiApiKeyStored
    {
        get => ProtectApiKey(AiApiKey);
        set => AiApiKey = UnprotectApiKey(value);
    }

    /// <summary>DPAPI 密文前缀标识；不带该前缀的值视为旧版明文。</summary>
    private const string EncryptedPrefix = "dpapi:";

    /// <summary>用 Windows 当前用户级 DPAPI 加密 API Key；加密失败宁可返回空也不把明文写进配置文件。</summary>
    /// <param name="plain">明文 Key。</param>
    /// <returns>dpapi: 前缀的 Base64 密文；空串原样返回。</returns>
    private static string ProtectApiKey(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(bytes);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return string.Empty;
        }
    }

    /// <summary>解密配置文件中的 API Key；旧版明文原样返回（下次保存自动转密文），跨机解密失败返回空。</summary>
    /// <param name="stored">配置文件中存储的值。</param>
    /// <returns>明文 Key。</returns>
    private static string UnprotectApiKey(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return string.Empty;
        }

        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(stored[EncryptedPrefix.Length..]), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            // 密文损坏或从其他电脑拷贝而来（DPAPI 绑定当前用户），一律视为未配置
            return string.Empty;
        }
    }

    /// <summary>在线 AI 使用的模型名称（如 deepseek-chat）；本地 Ollama 模式下不使用。</summary>
    public string AiModelName { get; set; } = string.Empty;

    /// <summary>数据库备份保留份数，超出后自动清理最旧的备份，最小保留 1 份。</summary>
    public int BackupRetentionCount { get; set; } = 15;

    /// <summary>当前 AI 服务商是否为本地 Ollama；否则视为在线 AI 服务商。</summary>
    [JsonIgnore]
    public bool IsOllamaProvider =>
        string.Equals(AiProvider, "Ollama", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否已配置可用的 AI 服务：
    /// 选择本地 Ollama 时要求 OllamaUrl 与 OllamaModel 不为空；选择在线 AI 时要求 AiBaseUrl、AiApiKey、AiModelName 均不为空
    /// （模型名缺失会让识别请求每次都失败回退离线，必须在配置判定阶段就视为未配置，避免界面显示「已启用」而实际无效）。
    /// 派生属性不写回配置文件，避免 settings.json 里出现冗余字段。
    /// </summary>
    [JsonIgnore]
    public bool IsAiConfigured =>
        IsOllamaProvider
            ? !string.IsNullOrWhiteSpace(OllamaUrl) && !string.IsNullOrWhiteSpace(OllamaModel)
            : !string.IsNullOrWhiteSpace(AiBaseUrl)
                && !string.IsNullOrWhiteSpace(AiApiKey)
                && !string.IsNullOrWhiteSpace(AiModelName);
}
