using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace MaterialManagementApp.ViewModels;

/// <summary>
/// 设置页面视图模型。负责读取、编辑、保存与重置应用配置，
/// 并提供数据库备份与打开所在目录两项运维操作。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到 IDE 输出窗口（Debug Provider）。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<SettingsViewModel>();

    /// <summary>主题下拉框索引与配置值的对应关系，顺序必须与 XAML 中 ComboBoxItem 一致。</summary>
    private static readonly string[] _themeKeys = { "Light", "Dark", "Acrylic", "System" };

    /// <summary>预设强调色板，供设置页快速选择；自定义色通过 ColorPicker 另行指定。</summary>
    public static readonly (string Name, string Hex)[] AccentColorPresets =
    {
        ("经典蓝", "#2563EB"),
        ("翡翠绿", "#059669"),
        ("琥珀橙", "#D97706"),
        ("紫罗兰", "#7C3AED"),
        ("玫红", "#DB2777"),
        ("青色", "#0891B2"),
        ("石墨", "#475569"),
    };

    /// <summary>
    /// 模型下拉框的预置候选。OCR 需要模型能读图，因此以视觉模型为主，
    /// 同时保留用户示例中的通用模型；用户也可直接在框内手动输入其它模型名。
    /// </summary>
    private static readonly string[] _builtinModelOptions =
    {
        "qwen2.5vl:7b",
        "qwen2.5vl:32b",
        "llama3.2-vision:11b",
        "llava:13b",
        "minicpm-v:8b",
        "moondream:1.8b",
        "qwen2.5-coder:7b"
    };

    /// <summary>AI 服务商下拉框预置选项，顺序与设置页 ComboBoxItem 一致；「自定义」不自动填充地址。</summary>
    public string[] AiProviderOptions { get; } = { "DeepSeek", "Kimi", "通义千问", "Ollama", "自定义" };

    /// <summary>各在线服务商的默认服务地址与推荐模型，切换服务商时自动带入，减少手工输入。</summary>
    private static readonly Dictionary<string, (string BaseUrl, string ModelName)> _aiProviderDefaults = new()
    {
        ["DeepSeek"] = ("https://api.deepseek.com", "deepseek-chat"),
        ["Kimi"] = ("https://api.moonshot.cn/v1", "moonshot-v1-8k-vision-preview"),
        ["通义千问"] = ("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-vl-max")
    };

    /// <summary>在线 AI 连接测试专用客户端；静态复用避免频繁建连，短超时快速失败。</summary>
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>构造与 Load 期间置为 true，避免赋值触发 OnAiProviderChanged 把磁盘上已保存的配置覆盖为默认值。</summary>
    private bool _suppressProviderAutoFill = true;

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把默认值集中在这里赋值，随后立即从配置服务载入真实值。
    /// </summary>
    public SettingsViewModel()
    {
        LowStockThreshold = 10;
        ProjectName = string.Empty;
        UsingUnit = string.Empty;
        DefaultUnit = string.Empty;
        DefaultHandler = string.Empty;
        ThemeIndex = 3;
        ThemeHint = string.Empty;
        CloseAction = "Ask";
        CloseActionIndex = 0;
        AccentColor = "#2563EB";
        DatabasePath = string.Empty;
        SettingsFilePath = string.Empty;
        ConfiguredDatabasePath = string.Empty;
        DefaultDatabaseFolder = string.Empty;
        OllamaUrl = "http://localhost:11434";
        OllamaModel = string.Empty;
        OcrEngineMode = "LocalOllama";
        AiProvider = "DeepSeek";
        AiBaseUrl = string.Empty;
        AiApiKey = string.Empty;
        AiModelName = string.Empty;
        AiStatus = string.Empty;
        StatusMessage = string.Empty;
        NewDeptName = string.Empty;
        NewPhaseName = string.Empty;

        // 预置候选先入列表，测试连接成功后会把服务端实际拥有的模型补进来
        OllamaModelOptions = new ObservableCollection<string>(_builtinModelOptions);

        Load();
    }

    /// <summary>本地 AI 模型下拉框候选，支持用户另填。</summary>
    public ObservableCollection<string> OllamaModelOptions { get; }

    /// <summary>项目部保留名称：「全部」已被筛选下拉框占用，不能用作项目部名。</summary>
    private static readonly string[] _deptReservedNames = { DeptPhaseService.FilterAllText };

    /// <summary>期数保留名称：「全部」「无期数」是筛选项、「无」是录入空白项，均已被占用。</summary>
    private static readonly string[] _phaseReservedNames =
    {
        DeptPhaseService.FilterAllText,
        DeptPhaseService.FilterBlankPhaseText,
        DeptPhaseService.EntryBlankPhaseText
    };

    /// <summary>项目部列表，绑定到设置页「项目部管理」区域。</summary>
    public ObservableCollection<ProjectDept> ProjectDepts { get; } = new();

    /// <summary>期数列表，绑定到设置页「期数管理」区域。</summary>
    public ObservableCollection<ProjectPhase> ProjectPhases { get; } = new();

    /// <summary>适用栏目配置列表，绑定到设置页「适用栏目」复选框区域。</summary>
    public ObservableCollection<DeptPhaseApplicableTable> DeptPhaseTables { get; } = new();

    /// <summary>新增项目部的名称输入。</summary>
    [ObservableProperty]
    private string _newDeptName;

    /// <summary>新增期数的名称输入。</summary>
    [ObservableProperty]
    private string _newPhaseName;

    /// <summary>库存预警阈值。</summary>
    [ObservableProperty]
    private int _lowStockThreshold;

    /// <summary>单据默认工程名称。</summary>
    [ObservableProperty]
    private string _projectName;

    /// <summary>单据默认使用单位。</summary>
    [ObservableProperty]
    private string _usingUnit;

    /// <summary>单据默认计量单位。</summary>
    [ObservableProperty]
    private string _defaultUnit;

    /// <summary>单据默认经办人。</summary>
    [ObservableProperty]
    private string _defaultHandler;

    /// <summary>主题下拉框选中索引：0 浅色、1 深色、2 透明毛玻璃、3 跟随系统。</summary>
    [ObservableProperty]
    private int _themeIndex;

    /// <summary>主题降级提示（如系统不支持毛玻璃），为空时不显示。</summary>
    [ObservableProperty]
    private string _themeHint;

    /// <summary>关闭窗口行为下拉候选，顺序必须与 XAML 中 ComboBoxItem 一致。</summary>
    public string[] CloseActionOptions { get; } = { "每次询问", "最小化到托盘", "直接退出" };

    /// <summary>关闭窗口行为（Ask / MinimizeToTray / Exit），与下拉框索引联动；保存后由主窗口在点 X 时生效。</summary>
    [ObservableProperty]
    private string _closeAction = "Ask";

    /// <summary>关闭窗口行为下拉框选中索引（0 询问 / 1 托盘 / 2 退出）。</summary>
    [ObservableProperty]
    private int _closeActionIndex;

    /// <summary>当前强调色十六进制值（如 #2563EB）。</summary>
    [ObservableProperty]
    private string _accentColor;

    /// <summary>数据库文件完整路径（只读展示）。</summary>
    [ObservableProperty]
    private string _databasePath;

    /// <summary>配置文件完整路径（只读展示）。</summary>
    [ObservableProperty]
    private string _settingsFilePath;

    /// <summary>配置中填写的数据库路径（只读展示）；为空表示使用默认路径。</summary>
    [ObservableProperty]
    private string _configuredDatabasePath;

    /// <summary>默认数据库存放目录（只读展示）。</summary>
    [ObservableProperty]
    private string _defaultDatabaseFolder;

    /// <summary>本地 AI（Ollama）服务地址。</summary>
    [ObservableProperty]
    private string _ollamaUrl;

    /// <summary>本地 AI 使用的模型名称；为空表示未启用本地 AI 识别。</summary>
    [ObservableProperty]
    private string _ollamaModel;

    /// <summary>OCR 引擎模式：LocalOllama（本地 Ollama）/ LocalPaddle（本地 Paddle）/ OnlineAi（在线 AI），其他值回退系统 OCR。</summary>
    [ObservableProperty]
    private string _ocrEngineMode;

    /// <summary>OCR 引擎单选框索引：0 本地离线识别 / 1 在线 AI 识别；与 OcrEngineMode 在 VM 内双向联动。</summary>
    [ObservableProperty]
    private int _ocrEngineIndex;

    /// <summary>AI 服务商：DeepSeek / Kimi / 通义千问 / Ollama / 自定义。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OllamaFieldsVisibility))]
    private string _aiProvider;

    /// <summary>在线 AI 服务地址（Base URL）；选择 Ollama 时使用独立的 OllamaUrl 字段。</summary>
    [ObservableProperty]
    private string _aiBaseUrl;

    /// <summary>在线 AI 的 API Key；选择 Ollama 时不需要填写。</summary>
    [ObservableProperty]
    private string _aiApiKey;

    /// <summary>在线 AI 使用的模型名称；选择 Ollama 时使用独立的 OllamaModel 字段。</summary>
    [ObservableProperty]
    private string _aiModelName;

    /// <summary>AI 板块的独立提示文本（测试连接结果），与全局 StatusMessage 分开避免互相覆盖。</summary>
    [ObservableProperty]
    private string _aiStatus;

    /// <summary>仅在选择 Ollama 服务商时显示本机服务地址与模型配置区块，其余服务商隐藏以保持界面整洁。</summary>
    public Visibility OllamaFieldsVisibility =>
        string.Equals(AiProvider, "Ollama", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>数据库备份保留份数；保存设置时写回配置文件。</summary>
    [ObservableProperty]
    private int _backupRetentionCount;

    /// <summary>数据库备份板块的独立提示文本（手动备份/恢复结果），与全局 StatusMessage 分开避免互相覆盖。</summary>
    [ObservableProperty]
    private string _backupStatus = string.Empty;

    /// <summary>操作结果提示文本。</summary>
    [ObservableProperty]
    private string _statusMessage;

    /// <summary>把当前配置值填充到表单属性。</summary>
    public void Load()
    {
        _logger.LogInformation("正在加载应用设置数据...");
        try
        {
            var settings = SettingsService.Instance.Settings;

            LowStockThreshold = settings.LowStockThreshold;
            ProjectName = settings.ProjectName;
            UsingUnit = settings.UsingUnit;
            DefaultUnit = settings.DefaultUnit;
            DefaultHandler = settings.DefaultHandler;

            var themeIndex = Array.IndexOf(_themeKeys, settings.ThemeMode);
            ThemeIndex = themeIndex >= 0 ? themeIndex : 3;
            ThemeHint = ThemeHelper.ComputeHint(settings) ?? string.Empty;
            CloseAction = settings.CloseAction;
            AccentColor = string.IsNullOrWhiteSpace(settings.AccentColor) ? "#2563EB" : settings.AccentColor;

            DatabasePath = DatabaseService.Instance.DatabasePath;
            SettingsFilePath = SettingsService.Instance.SettingsFilePath;
            ConfiguredDatabasePath = settings.DatabasePath;
            DefaultDatabaseFolder = SettingsService.DefaultDatabaseFolder;

            OllamaUrl = string.IsNullOrWhiteSpace(settings.OllamaUrl) ? "http://localhost:11434" : settings.OllamaUrl;
            OllamaModel = settings.OllamaModel ?? string.Empty;
            OcrEngineMode = string.IsNullOrWhiteSpace(settings.OcrEngineMode) ? "LocalOllama" : settings.OcrEngineMode;
            AiProvider = string.IsNullOrWhiteSpace(settings.AiProvider) ? "DeepSeek" : settings.AiProvider;
            AiBaseUrl = settings.AiBaseUrl ?? string.Empty;
            AiApiKey = settings.AiApiKey ?? string.Empty;
            AiModelName = settings.AiModelName ?? string.Empty;
            AiStatus = settings.IsAiConfigured
                ? $"已启用 AI（{settings.AiProvider}）：识别单据时优先调用 {(settings.IsOllamaProvider ? OllamaModel : settings.AiModelName)}，失败自动回退系统 OCR。"
                : "未配置模型，识别单据时将使用系统 OCR。";

            BackupRetentionCount = settings.BackupRetentionCount;

            LoadDimensionData();

            _logger.LogInformation("应用设置数据加载完成。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载应用设置数据失败。");
            StatusMessage = $"读取设置失败：{ex.Message}";
        }
        finally
        {
            // 加载完成后才允许「切换服务商自动填充」，避免把磁盘配置覆盖为默认值
            _suppressProviderAutoFill = false;
        }
    }

    /// <summary>保存配置：写入 JSON、立即应用主题并提示结果。</summary>
    [RelayCommand]
    private void Save()
    {
        // 阈值参与库存预警查询，0 或负数会让「低于阈值」恒真，必须拦截
        if (LowStockThreshold <= 0 || LowStockThreshold > 9999)
        {
            StatusMessage = "库存预警阈值需在 1 到 9999 之间。";
            return;
        }

        try
        {
            var settings = SettingsService.Instance.Settings;

            settings.LowStockThreshold = LowStockThreshold;
            settings.ProjectName = (ProjectName ?? string.Empty).Trim();
            settings.UsingUnit = (UsingUnit ?? string.Empty).Trim();
            settings.DefaultUnit = (DefaultUnit ?? string.Empty).Trim();
            settings.DefaultHandler = (DefaultHandler ?? string.Empty).Trim();
            settings.ThemeMode = _themeKeys[Math.Clamp(ThemeIndex, 0, _themeKeys.Length - 1)];

            // 记住最近一次选择的浅色/深色，作为「透明毛玻璃」模式的基底主题
            if (settings.ThemeMode is "Light" or "Dark")
            {
                settings.BaseTheme = settings.ThemeMode;
            }

            settings.CloseAction = CloseAction is "Ask" or "MinimizeToTray" or "Exit" ? CloseAction : "Ask";
            settings.AccentColor = string.IsNullOrWhiteSpace(AccentColor) ? "#2563EB" : AccentColor.Trim();

            // 数据库保存位置：为空表示使用默认位置（程序目录\Data\MaterialSystem.db），修改后重启程序生效
            var newDatabasePath = (ConfiguredDatabasePath ?? string.Empty).Trim();
            var databasePathChanged = !string.Equals(newDatabasePath, settings.DatabasePath, StringComparison.OrdinalIgnoreCase);
            settings.DatabasePath = newDatabasePath;

            // 地址留空会被视为「未配置」，因此这里补回默认本机地址，保证回退逻辑判断稳定
            settings.OllamaUrl = string.IsNullOrWhiteSpace(OllamaUrl) ? "http://localhost:11434" : OllamaUrl.Trim();
            settings.OllamaModel = (OllamaModel ?? string.Empty).Trim();

            settings.OcrEngineMode = string.IsNullOrWhiteSpace(OcrEngineMode) ? "LocalOllama" : OcrEngineMode.Trim();
            settings.AiProvider = string.IsNullOrWhiteSpace(AiProvider) ? "DeepSeek" : AiProvider.Trim();
            settings.AiBaseUrl = (AiBaseUrl ?? string.Empty).Trim();
            settings.AiApiKey = (AiApiKey ?? string.Empty).Trim();
            settings.AiModelName = (AiModelName ?? string.Empty).Trim();

            settings.BackupRetentionCount = Math.Max(1, BackupRetentionCount);

            SettingsService.Instance.Save();

            // 主题必须立刻反映到界面，否则用户要重启才能看到效果
            ApplyTheme(settings.ThemeMode);

            AiStatus = settings.IsAiConfigured
                ? $"已启用 AI（{settings.AiProvider}）：识别单据时优先调用 {(settings.IsOllamaProvider ? settings.OllamaModel : settings.AiModelName)}，失败自动回退系统 OCR。"
                : "未配置模型，识别单据时将使用系统 OCR。";

            StatusMessage = databasePathChanged
                ? "设置已保存。数据库保存位置已修改，重启程序后生效。"
                : "设置已保存。";
            _logger.LogInformation("应用设置已保存。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存应用设置失败。");
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>恢复默认配置并刷新表单与主题。</summary>
    [RelayCommand]
    private void ResetToDefault()
    {
        try
        {
            SettingsService.Instance.Reset();
            Load();
            ApplyTheme(SettingsService.Instance.Settings.ThemeMode);
            ThemeHelper.ApplyAccentColor(SettingsService.Instance.Settings.AccentColor);
            StatusMessage = "已恢复默认设置。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"恢复默认失败：{ex.Message}";
        }
    }

    /// <summary>放弃未保存的改动，把表单恢复为磁盘上已保存的值。</summary>
    [RelayCommand]
    private void Cancel()
    {
        Load();
        StatusMessage = "已取消未保存的修改。";
    }

    /// <summary>
    /// 弹出文件选择器选择数据库文件位置，选中的完整路径写入「数据库保存位置」输入框；
    /// 仍需点击「保存」写入配置，重启程序后在新位置生效。
    /// </summary>
    [RelayCommand]
    private async Task BrowseDatabasePathAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".db");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ConfiguredDatabasePath = file.Path;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择数据库文件失败。");
            StatusMessage = $"选择数据库文件失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 测试 AI 连接：选择 Ollama 时沿用原有「读取模型列表」逻辑（顺带补全下拉候选）；
    /// 在线服务商则向 {BaseUrl}/models 发送极简 GET 请求验证密钥有效性（DeepSeek / Kimi /
    /// 通义千问等 OpenAI 兼容接口均支持该端点），结果输出到 AiStatus。
    /// </summary>
    [RelayCommand]
    private async Task TestAiConnectionAsync()
    {
        // ==================== 本地 Ollama ====================
        if (string.Equals(AiProvider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            var url = string.IsNullOrWhiteSpace(OllamaUrl) ? "http://localhost:11434" : OllamaUrl.Trim();

            AiStatus = $"正在测试连接 {url} …";
            try
            {
                var models = await OllamaService.Instance.ListModelsAsync(url);

                if (models.Count == 0)
                {
                    AiStatus = $"连接成功（{url}），但服务端还没有任何模型，请先执行 ollama pull 拉取模型。";
                    return;
                }

                // 把服务端实际存在的模型并入候选列表（去重），方便用户直接在下拉框中选择
                foreach (var model in models)
                {
                    if (!OllamaModelOptions.Contains(model, StringComparer.OrdinalIgnoreCase))
                    {
                        OllamaModelOptions.Add(model);
                    }
                }

                // 尚未选择模型时，自动带入服务端第一个可用模型，避免用户面对空下拉框
                if (string.IsNullOrWhiteSpace(OllamaModel))
                {
                    OllamaModel = models[0];
                }

                var preview = string.Join("、", models.Take(8));
                var suffix = models.Count > 8 ? $" 等 {models.Count} 个" : string.Empty;
                AiStatus = $"连接成功（{url}）。可用模型：{preview}{suffix}";
                _logger.LogInformation("本地 AI 连接测试成功：{Url}，模型数 {Count}。", url, models.Count);
            }
            catch (Exception ex)
            {
                AiStatus = $"连接失败：{ex.Message}";
                _logger.LogWarning(ex, "本地 AI 连接测试失败：{Url}。", url);
            }

            return;
        }

        // ==================== 在线服务商 ====================
        var baseUrl = (AiBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var apiKey = (AiApiKey ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            AiStatus = "请先填写服务地址（Base URL）和 API Key 再测试。";
            return;
        }

        AiStatus = $"正在测试连接 {baseUrl} …";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                AiStatus = $"连接成功（{baseUrl}），API Key 有效。";
                _logger.LogInformation("在线 AI 连接测试成功：{BaseUrl}。", baseUrl);
            }
            else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                AiStatus = $"连接失败：API Key 无效或无权限（HTTP {(int)response.StatusCode}）。";
                _logger.LogWarning("在线 AI 密钥校验失败：{BaseUrl}，HTTP {Code}。", baseUrl, (int)response.StatusCode);
            }
            else
            {
                AiStatus = $"连接失败：服务返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。";
                _logger.LogWarning("在线 AI 连接测试失败：{BaseUrl}，HTTP {Code}。", baseUrl, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            AiStatus = $"连接失败：{ex.Message}";
            _logger.LogWarning(ex, "在线 AI 连接测试失败：{BaseUrl}。", baseUrl);
        }
    }

    /// <summary>把当前数据库复制到 backup 子目录，文件名带时间戳便于区分多次备份。</summary>
    [RelayCommand]
    private void BackupDatabase()
    {
        try
        {
            var source = DatabaseService.Instance.DatabasePath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                StatusMessage = "数据库文件尚不存在，无法备份。";
                return;
            }

            var directory = Path.Combine(Path.GetDirectoryName(source)!, "backup");
            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            DatabaseService.Instance.BackupTo(target);

            StatusMessage = $"备份完成：{target}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"备份失败：{ex.Message}";
        }
    }

    /// <summary>用资源管理器打开数据库所在目录。</summary>
    [RelayCommand]
    private void OpenDatabaseFolder()
    {
        try
        {
            var directory = Path.GetDirectoryName(DatabaseService.Instance.DatabasePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                StatusMessage = "数据库目录不存在。";
                return;
            }

            // 非打包模式下 Launcher.LaunchFolderAsync 常因缺少包身份失败，直接调资源管理器更可靠
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            StatusMessage = "已打开数据库所在文件夹。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开文件夹失败：{ex.Message}";
        }
    }

    /// <summary>把主题模式应用到主窗口（含毛玻璃开关与降级提示）。</summary>
    /// <param name="mode">主题模式，取值 Light / Dark / Acrylic / System。</param>
    private void ApplyTheme(string mode)
    {
        // 快照仅含主题模式字段：未点保存前 ComboBox 变化也能即时预览，不影响其他配置
        var snapshot = new AppSettings
        {
            ThemeMode = mode,
            BaseTheme = mode is "Light" or "Dark"
                ? mode
                : SettingsService.Instance.Settings.BaseTheme
        };

        ThemeHint = ThemeHelper.ApplyMode(App.MainWindow, App.MainWindow?.Content as FrameworkElement, snapshot)
            ?? string.Empty;
    }

    /// <summary>主题下拉框变化时即时切换界面主题，无需等待保存。</summary>
    partial void OnThemeIndexChanged(int value)
    {
        ApplyTheme(_themeKeys[Math.Clamp(value, 0, _themeKeys.Length - 1)]);
    }

    /// <summary>强调色变化时即时应用到全局控件。</summary>
    partial void OnAccentColorChanged(string value)
    {
        ThemeHelper.ApplyAccentColor(value);
    }

    /// <summary>OCR 引擎单选框切换：把选中索引翻译为 OcrEngineMode 存储值。</summary>
    partial void OnOcrEngineIndexChanged(int value)
    {
        // 本地离线档保留当前具体引擎（LocalOllama / LocalPaddle），从未设置过时默认 Ollama
        OcrEngineMode = value == 1
            ? "OnlineAi"
            : OcrEngineMode is "LocalOllama" or "LocalPaddle"
                ? OcrEngineMode
                : "LocalOllama";
    }

    /// <summary>OcrEngineMode 变化时回写单选框索引；值未变时生成代码自动去重，不会递归。</summary>
    partial void OnOcrEngineModeChanged(string value)
    {
        OcrEngineIndex = string.Equals(value, "OnlineAi", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>关闭行为下拉框切换：把选中索引翻译为 CloseAction 存储值。</summary>
    partial void OnCloseActionIndexChanged(int value)
    {
        CloseAction = value switch
        {
            1 => "MinimizeToTray",
            2 => "Exit",
            _ => "Ask"
        };
    }

    /// <summary>CloseAction 变化时回写下拉框索引；值未变时生成代码自动去重，不会递归。</summary>
    partial void OnCloseActionChanged(string value)
    {
        CloseActionIndex = value switch
        {
            "MinimizeToTray" => 1,
            "Exit" => 2,
            _ => 0
        };
    }

    /// <summary>
    /// 切换 AI 服务商时自动带入该服务商的默认服务地址与推荐模型，减少手工输入。
    /// Ollama 使用独立的 OllamaUrl / OllamaModel 字段、「自定义」完全保留用户输入，均不做自动填充。
    /// </summary>
    partial void OnAiProviderChanged(string value)
    {
        // Load / 构造期间的赋值不触发自动填充，否则会把用户已保存的配置覆盖为默认值
        if (_suppressProviderAutoFill || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (_aiProviderDefaults.TryGetValue(value, out var defaults))
        {
            AiBaseUrl = defaults.BaseUrl;
            AiModelName = defaults.ModelName;
            AiStatus = $"已带入 {value} 默认服务地址与模型，请填写 API Key 后测试连接。";
        }
    }

    // ==================== 「项目部 / 期数」维度管理 ====================

    /// <summary>读取项目部、期数与适用栏目配置并填充管理列表（加载与增删改后调用）。</summary>
    public void LoadDimensionData()
    {
        try
        {
            ProjectDepts.Clear();
            foreach (var dept in DatabaseService.Instance.GetProjectDepts())
            {
                dept.OriginalName = dept.Name;
                ProjectDepts.Add(dept);
            }

            ProjectPhases.Clear();
            foreach (var phase in DatabaseService.Instance.GetProjectPhases())
            {
                phase.OriginalName = phase.Name;
                ProjectPhases.Add(phase);
            }

            DeptPhaseTables.Clear();
            foreach (var table in DatabaseService.Instance.GetDeptPhaseApplicableTables())
            {
                DeptPhaseTables.Add(table);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载项目部/期数维度数据失败。");
            StatusMessage = $"加载项目部/期数失败：{ex.Message}";
        }
    }

    /// <summary>新增项目部：校验名称合法后入库并刷新共享缓存。</summary>
    public void AddProjectDept()
    {
        var name = (NewDeptName ?? string.Empty).Trim();
        if (!ValidateDimensionName(name, _deptReservedNames, ProjectDepts.Select(x => x.Name), "项目部", out var error))
        {
            StatusMessage = error;
            return;
        }

        try
        {
            DatabaseService.Instance.AddProjectDept(name);
            NewDeptName = string.Empty;
            LoadDimensionData();
            DeptPhaseService.Instance.Refresh();

            StatusMessage = $"已添加项目部「{name}」。";
            _logger.LogInformation("已新增项目部 {Name}。", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "新增项目部 {Name} 失败。", name);
            StatusMessage = $"新增项目部失败：{ex.Message}";
        }
    }

    /// <summary>保存行内重命名后的项目部，并把业务记录中的旧名级联改为新名。</summary>
    /// <param name="dept">被编辑的项目部行（Name 已是新值，OriginalName 保留旧值）。</param>
    public void RenameProjectDept(ProjectDept? dept)
    {
        if (dept is null)
        {
            return;
        }

        var newName = (dept.Name ?? string.Empty).Trim();
        var oldName = dept.OriginalName;

        if (string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            StatusMessage = $"项目部「{oldName}」名称未变化。";
            return;
        }

        if (!ValidateDimensionName(newName, _deptReservedNames, ProjectDepts.Where(x => x.Id != dept.Id).Select(x => x.Name), "项目部", out var error))
        {
            // 校验失败时把显示还原为旧名，避免界面上留下未入库的脏值
            dept.Name = oldName;
            StatusMessage = error;
            return;
        }

        try
        {
            DatabaseService.Instance.UpdateProjectDept(dept);
            var affected = DatabaseService.Instance.RenameProjectDeptEverywhere(oldName, newName);
            dept.OriginalName = newName;
            DeptPhaseService.Instance.Refresh();

            StatusMessage = affected > 0
                ? $"已将项目部「{oldName}」改名为「{newName}」，并同步更新 {affected} 条业务记录。"
                : $"已将项目部「{oldName}」改名为「{newName}」。";
            _logger.LogInformation("项目部重命名 {OldName} -> {NewName}，级联更新 {Affected} 条记录。", oldName, newName, affected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重命名项目部 {OldName} 失败。", oldName);
            StatusMessage = $"重命名项目部失败：{ex.Message}";
            // 从库重读，界面回滚到实际存储值
            LoadDimensionData();
        }
    }

    /// <summary>删除项目部；仍有业务记录引用时拒绝删除，防止历史数据失去归属。</summary>
    /// <param name="dept">要删除的项目部行。</param>
    public void DeleteProjectDept(ProjectDept? dept)
    {
        if (dept is null)
        {
            return;
        }

        try
        {
            var usage = DatabaseService.Instance.CountProjectDeptUsage(dept.Name);
            if (usage > 0)
            {
                StatusMessage = $"项目部「{dept.Name}」仍被 {usage} 条记录使用，不能删除；可先在相关页面把这些记录改为其它项目部。";
                _logger.LogWarning("删除项目部 {Name} 被拒绝：仍被 {Usage} 条记录引用。", dept.Name, usage);
                return;
            }

            DatabaseService.Instance.DeleteProjectDept(dept.Id);
            ProjectDepts.Remove(dept);
            DeptPhaseService.Instance.Refresh();

            StatusMessage = $"已删除项目部「{dept.Name}」。";
            _logger.LogInformation("已删除项目部 {Name}。", dept.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除项目部 {Name} 失败。", dept.Name);
            StatusMessage = $"删除项目部失败：{ex.Message}";
        }
    }

    /// <summary>新增期数：校验名称合法后入库并刷新共享缓存。期数不与项目部绑定。</summary>
    public void AddProjectPhase()
    {
        var name = (NewPhaseName ?? string.Empty).Trim();
        if (!ValidateDimensionName(name, _phaseReservedNames, ProjectPhases.Select(x => x.Name), "期数", out var error))
        {
            StatusMessage = error;
            return;
        }

        try
        {
            DatabaseService.Instance.AddProjectPhase(name);
            NewPhaseName = string.Empty;
            LoadDimensionData();
            DeptPhaseService.Instance.Refresh();

            StatusMessage = $"已添加期数「{name}」。";
            _logger.LogInformation("已新增期数 {Name}。", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "新增期数 {Name} 失败。", name);
            StatusMessage = $"新增期数失败：{ex.Message}";
        }
    }

    /// <summary>保存行内重命名后的期数，并把业务记录中的旧名级联改为新名。</summary>
    /// <param name="phase">被编辑的期数行（Name 已是新值，OriginalName 保留旧值）。</param>
    public void RenameProjectPhase(ProjectPhase? phase)
    {
        if (phase is null)
        {
            return;
        }

        var newName = (phase.Name ?? string.Empty).Trim();
        var oldName = phase.OriginalName;

        if (string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            StatusMessage = $"期数「{oldName}」名称未变化。";
            return;
        }

        if (!ValidateDimensionName(newName, _phaseReservedNames, ProjectPhases.Where(x => x.Id != phase.Id).Select(x => x.Name), "期数", out var error))
        {
            phase.Name = oldName;
            StatusMessage = error;
            return;
        }

        try
        {
            DatabaseService.Instance.UpdateProjectPhase(phase);
            var affected = DatabaseService.Instance.RenameProjectPhaseEverywhere(oldName, newName);
            phase.OriginalName = newName;
            DeptPhaseService.Instance.Refresh();

            StatusMessage = affected > 0
                ? $"已将期数「{oldName}」改名为「{newName}」，并同步更新 {affected} 条业务记录。"
                : $"已将期数「{oldName}」改名为「{newName}」。";
            _logger.LogInformation("期数重命名 {OldName} -> {NewName}，级联更新 {Affected} 条记录。", oldName, newName, affected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重命名期数 {OldName} 失败。", oldName);
            StatusMessage = $"重命名期数失败：{ex.Message}";
            LoadDimensionData();
        }
    }

    /// <summary>删除期数；仍有业务记录引用时拒绝删除，防止历史数据失去归属。</summary>
    /// <param name="phase">要删除的期数行。</param>
    public void DeleteProjectPhase(ProjectPhase? phase)
    {
        if (phase is null)
        {
            return;
        }

        try
        {
            var usage = DatabaseService.Instance.CountProjectPhaseUsage(phase.Name);
            if (usage > 0)
            {
                StatusMessage = $"期数「{phase.Name}」仍被 {usage} 条记录使用，不能删除；可先在相关页面把这些记录的期数改为其它值或留空。";
                _logger.LogWarning("删除期数 {Name} 被拒绝：仍被 {Usage} 条记录引用。", phase.Name, usage);
                return;
            }

            DatabaseService.Instance.DeleteProjectPhase(phase.Id);
            ProjectPhases.Remove(phase);
            DeptPhaseService.Instance.Refresh();

            StatusMessage = $"已删除期数「{phase.Name}」。";
            _logger.LogInformation("已删除期数 {Name}。", phase.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除期数 {Name} 失败。", phase.Name);
            StatusMessage = $"删除期数失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 适用栏目复选框切换后持久化：启用即自动补列迁移（ALTER TABLE），
    /// 停用仅隐藏界面入口，列与历史数据保留。
    /// </summary>
    /// <param name="table">被切换的栏目配置行（IsEnabled 已是新值）。</param>
    public void ApplyDeptPhaseTableToggle(DeptPhaseApplicableTable? table)
    {
        if (table is null)
        {
            return;
        }

        try
        {
            DatabaseService.Instance.SetDeptPhaseTableEnabled(table.TableName, table.IsEnabled);
            DeptPhaseService.Instance.Refresh();

            StatusMessage = table.IsEnabled
                ? $"已为「{table.DisplayName}」启用项目部/期数维度，数据列已就绪。"
                : $"已停用「{table.DisplayName}」的项目部/期数维度，历史数据仍然保留。";
            _logger.LogInformation("栏目 {TableName} 的维度启用状态已切换为 {Enabled}。", table.TableName, table.IsEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换栏目 {TableName} 维度启用状态失败。", table.TableName);
            StatusMessage = $"切换适用栏目失败：{ex.Message}";
            LoadDimensionData();
        }
    }

    /// <summary>校验维度名称：非空、不撞界面保留用词、不与现有项重名。</summary>
    /// <param name="name">待校验名称（已去空白）。</param>
    /// <param name="reserved">保留用词清单。</param>
    /// <param name="existing">现有名称清单（重命名时排除自身）。</param>
    /// <param name="label">维度中文名（项目部 / 期数），用于拼接提示文案。</param>
    /// <param name="error">校验失败时的提示文案。</param>
    /// <returns>合法返回 true。</returns>
    private static bool ValidateDimensionName(string name, string[] reserved, IEnumerable<string> existing, string label, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = $"{label}名称不能为空。";
            return false;
        }

        if (reserved.Contains(name, StringComparer.Ordinal))
        {
            error = $"「{name}」是下拉框保留用词，不能用作{label}名称。";
            return false;
        }

        if (existing.Contains(name, StringComparer.Ordinal))
        {
            error = $"{label}「{name}」已存在。";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
