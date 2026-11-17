using MaterialManagementApp.Common;
using MaterialManagementApp.Services;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 工作台（首页）页面。上部为台账概况四张统计卡片，下部为四个快捷操作按钮。
/// </summary>
public sealed partial class DashboardPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public DashboardViewModel ViewModel { get; } = new();

    /// <summary>WebView2 内核是否已初始化（页面生命周期内只初始化一次）。</summary>
    private bool _mapCoreInitialized;

    /// <summary>map.html 是否已加载完成并回发了就绪消息，未就绪时不推送数据。</summary>
    private bool _mapPageReady;

    /// <summary>当前地图 URL 已注入的高德 Key（用于判断设置保存后是否需要重新导航）。</summary>
    private string _activeMapKey = string.Empty;

    /// <summary>当前地图 URL 已注入的高德安全密钥。</summary>
    private string _activeMapCode = string.Empty;

    /// <summary>初始化工作台页面。</summary>
    public DashboardPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 每次进入页面都重新统计，并刷新施工进度地图与今日记录。
    /// 用 OnNavigatedTo 而非构造函数，这样从其他页面返回时数字会刷新，不会停留在旧值。
    /// </summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
        ViewModel.RefreshConstructionData();
        PushMapData();

        // 后台扫描入库完成后刷新地图；先退订再订阅，避免页面反复导航导致重复触发
        WatermarkScannerService.Instance.ScanCompleted -= OnScanCompleted;
        WatermarkScannerService.Instance.ScanCompleted += OnScanCompleted;

        // 设置页保存高德 Key / 安全密钥后，无需重启即可重新加载地图
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>页面卸载时退订事件，避免持有已离开页面的引用。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        WatermarkScannerService.Instance.ScanCompleted -= OnScanCompleted;
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
    }

    /// <summary>后台扫描完成：回投 UI 线程刷新列表与地图气泡。</summary>
    private void OnScanCompleted(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.RefreshConstructionData();
            PushMapData();
        });
    }

    /// <summary>
    /// 地图控件首次加载：初始化 WebView2 内核，映射虚拟域名并载入 map.html。
    /// 顺序固定为「确保内核 → 开启脚本 → 映射 assets.local → 导航」，映射必须先于导航。
    /// </summary>
    /// <param name="sender">WebView2 控件。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnProgressMapLoaded(object sender, RoutedEventArgs e)
    {
        if (_mapCoreInitialized)
        {
            return;
        }

        _mapCoreInitialized = true;

        try
        {
            await ProgressMap.EnsureCoreWebView2Async();
            var core = ProgressMap.CoreWebView2;

            // 显式开启脚本与默认脚本对话框，避免依赖内核默认值
            // 注意：WebView2 的正式属性名是 IsScriptEnabled（即 JS 开关）
            core.Settings.IsScriptEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = true;

            core.WebMessageReceived += OnMapWebMessageReceived;
            core.NavigationCompleted += OnMapNavigationCompleted;

            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            if (File.Exists(Path.Combine(assetsDir, "map.html")))
            {
                // 用虚拟域名承载本地模板：高德 JS API 需要正式的 https 来源，
                // about:blank / file:// 会被域名校验拒绝。若 Key 配置了域名白名单，需加入 https://assets.local。
                core.SetVirtualHostNameToFolderMapping(
                    "assets.local", assetsDir, CoreWebView2HostResourceAccessKind.Allow);
                NavigateMap();
            }
            else
            {
                ProgressMap.NavigateToString(
                    "<html><body style=\"font-family:'Microsoft YaHei UI',sans-serif;padding:24px;color:#334155;\">"
                    + "地图模板缺失：未找到 Assets\\map.html，请重新安装软件。</body></html>");
            }
        }
        catch (Exception ex)
        {
            // WebView2 运行时缺失（旧系统未安装）或内核初始化失败时给出可读提示，不影响页面其他功能
            MapErrorText.Text = $"地图组件初始化失败（可能未安装 WebView2 运行时）：{ex.Message}";
            MapErrorText.Visibility = Visibility.Visible;
            ViewModel.StatusMessage = "地图组件初始化失败，施工记录与统计功能不受影响。";
        }
    }

    /// <summary>
    /// 导航到地图模板，并把设置页保存的高德 Key / 安全密钥以查询参数注入（方式 A）。
    /// 每次导航前记录当前注入值，供设置变更时判断是否需要重新加载。
    /// </summary>
    private void NavigateMap()
    {
        if (ProgressMap.CoreWebView2 is null)
        {
            return;
        }

        var settings = SettingsService.Instance.Settings;
        _activeMapKey = (settings.AmapJsApiKey ?? string.Empty).Trim();
        _activeMapCode = (settings.AmapSecurityCode ?? string.Empty).Trim();

        // Uri.EscapeDataString 保证密钥中的特殊字符不会破坏查询串；
        // 空值同样保留参数，map.html 据此显示「未配置」提示而不是白屏。
        var url = "https://assets.local/map.html"
                  + "?key=" + Uri.EscapeDataString(_activeMapKey)
                  + "&jscode=" + Uri.EscapeDataString(_activeMapCode);

        _mapPageReady = false;
        ProgressMap.CoreWebView2.Navigate(url);
    }

    /// <summary>
    /// 设置页保存后回投 UI 线程：仅当高德 Key / 安全密钥确实变化时才重新导航地图，
    /// 避免保存主题等无关设置时打断已经渲染好的地图。
    /// </summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var settings = SettingsService.Instance.Settings;
        var key = (settings.AmapJsApiKey ?? string.Empty).Trim();
        var code = (settings.AmapSecurityCode ?? string.Empty).Trim();

        if (string.Equals(key, _activeMapKey, StringComparison.Ordinal)
            && string.Equals(code, _activeMapCode, StringComparison.Ordinal))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.RefreshConstructionData();
            if (_mapCoreInitialized && ProgressMap.CoreWebView2 is not null)
            {
                NavigateMap();
            }
        });
    }

    /// <summary>页面导航完成：若前端已就绪则补推送一次工点数据（覆盖先就绪后导航完成的时序）。</summary>
    private void OnMapNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess && _mapPageReady)
        {
            PushMapData();
        }
    }

    /// <summary>
    /// 接收 map.html 的消息：就绪信号用于推送工点数据；map-console: 前缀为前端报错转发，
    /// 便于在 IDE 调试输出中直接看到高德鉴权失败（如 INVALID_USER_SCODE）等 console 错误。
    /// </summary>
    private void OnMapWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var message = args.TryGetWebMessageAsString();
            if (string.Equals(message, "map-ready", StringComparison.OrdinalIgnoreCase))
            {
                _mapPageReady = true;
                PushMapData();
                return;
            }

            const string consolePrefix = "map-console:";
            if (message.StartsWith(consolePrefix, StringComparison.Ordinal))
            {
                Debug.WriteLine($"[地图前端] {message[consolePrefix.Length..]}");
            }
        }
        catch (Exception)
        {
            // 非文本消息（或脚本异常）忽略，地图数据会在下次刷新时重新推送
        }
    }

    /// <summary>把当前工点 JSON 通过 PostWebMessageAsJson 推送给高德地图页面。</summary>
    private void PushMapData()
    {
        if (!_mapPageReady || ProgressMap.CoreWebView2 is null)
        {
            return;
        }

        var payload = string.IsNullOrWhiteSpace(ViewModel.MapPayload) ? "{}" : ViewModel.MapPayload;
        ProgressMap.CoreWebView2.PostWebMessageAsJson(payload);
    }

    /// <summary>快捷磁贴「台账明细」：走全局导航，跳转页面并同步左侧导航栏选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnMaterialLedgerClick(object sender, RoutedEventArgs e)
    {
        // 选中导航项即触发 MainWindow 的 SelectionChanged → 内容区跳转 + 侧栏高亮同步
        if ((App.MainWindow as MainWindow)?.NavigateTo(PageRoutes.EarthLedger) != true)
        {
            ViewModel.ShowNotImplemented("台账明细");
        }
    }

    /// <summary>快捷磁贴「登记入库/领用」：跳转出入库页面并同步侧栏选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnStockInOutClick(object sender, RoutedEventArgs e)
    {
        if ((App.MainWindow as MainWindow)?.NavigateTo(PageRoutes.StockInOut) != true)
        {
            ViewModel.ShowNotImplemented("登记入库");
        }
    }

    /// <summary>快捷磁贴「登记进退场」：跳转机具入场页面并同步侧栏选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCampEntryExitClick(object sender, RoutedEventArgs e)
    {
        if ((App.MainWindow as MainWindow)?.NavigateTo(PageRoutes.EquipEntry) != true)
        {
            ViewModel.ShowNotImplemented("登记进退场");
        }
    }

    /// <summary>快捷磁贴「备份数据」：跳转设置页（数据库备份区）并同步侧栏选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnBackupClick(object sender, RoutedEventArgs e)
    {
        if ((App.MainWindow as MainWindow)?.NavigateTo(PageRoutes.Settings) != true)
        {
            ViewModel.ShowNotImplemented("备份数据");
        }
    }

    /// <summary>
    /// 统计卡悬停进入：换卡片悬停底色作视觉提示。统计卡为纯数据展示（Border），
    /// 没有点击行为，指针始终是普通箭头。
    /// </summary>
    /// <param name="sender">事件源（统计卡 Border）。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnStatCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card && Application.Current.Resources.TryGetValue("AppCardHoverBrush", out var hover))
        {
            card.Background = (Brush)hover;
        }
    }

    /// <summary>统计卡悬停退出：恢复卡片表面底色（与样式默认一致）。</summary>
    /// <param name="sender">事件源（统计卡 Border）。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnStatCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card && Application.Current.Resources.TryGetValue("AppSurfaceBrush", out var surface))
        {
            card.Background = (Brush)surface;
        }
    }
}
