using MaterialManagementApp.Common;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

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

        // 首屏分级加载：今日记录与地图数据先出，统计卡查询放后面，避免首屏等待计数
        ViewModel.RefreshConstructionData();
        PushMapData();
        ViewModel.Load();

        // 后台扫描入库完成后刷新地图；先退订再订阅，避免页面反复导航导致重复触发
        WatermarkScannerService.Instance.ScanCompleted -= OnScanCompleted;
        WatermarkScannerService.Instance.ScanCompleted += OnScanCompleted;

        // 扫描进度：处理一张更新一次，完成后短暂展示再淡出
        WatermarkScannerService.Instance.ScanProgressChanged -= OnScanProgressChanged;
        WatermarkScannerService.Instance.ScanProgressChanged += OnScanProgressChanged;

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
        WatermarkScannerService.Instance.ScanProgressChanged -= OnScanProgressChanged;
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        _scanProgressTimer?.Stop();
    }

    /// <summary>扫描进度计时器：完成后展示几秒再隐藏。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _scanProgressTimer;

    /// <summary>扫描进度变化（后台线程）：回投 UI 线程更新一行文本，不阻塞识别。</summary>
    private void OnScanProgressChanged(object? sender, ScanProgress e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 新进度到来时取消上一次的自动隐藏
            _scanProgressTimer?.Stop();

            if (e.IsCompleted)
            {
                ViewModel.ScanProgressText = $"扫描完成（今日新增 {e.NewRecords} 条）";
                ViewModel.IsScanProgressVisible = true;
                HideScanProgressLater(TimeSpan.FromSeconds(5));
                return;
            }

            ViewModel.ScanProgressText = $"正在扫描 {e.Processed}/{e.Total}";
            ViewModel.IsScanProgressVisible = true;
        });
    }

    /// <summary>延时隐藏扫描进度文本（完成后短暂展示，不做动画）。</summary>
    private void HideScanProgressLater(TimeSpan delay)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ViewModel.IsScanProgressVisible = false;
        };
        _scanProgressTimer = timer;
        timer.Start();
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
                MapLoadingText.Visibility = Visibility.Collapsed;
                ProgressMap.NavigateToString(
                    "<html><body style=\"font-family:'Microsoft YaHei UI',sans-serif;padding:24px;color:#334155;\">"
                    + "地图模板缺失：未找到 Assets\\map.html，请重新安装软件。</body></html>");
            }
        }
        catch (Exception ex)
        {
            // WebView2 运行时缺失（旧系统未安装）或内核初始化失败时给出可读提示，不影响页面其他功能
            MapLoadingText.Visibility = Visibility.Collapsed;
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
        // 导航结束即撤掉占位文案，成功由 map-ready 继续推送数据，失败则展示可读提示
        MapLoadingText.Visibility = Visibility.Collapsed;

        if (args.IsSuccess && _mapPageReady)
        {
            PushMapData();
        }
        else if (!args.IsSuccess)
        {
            MapErrorText.Text = "地图加载失败：请检查网络与高德 Key 配置，或稍后重新进入本页。";
            MapErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 接收 map.html 的消息：就绪信号用于推送工点数据；map-loaded 计一次地图加载估算；
    /// site-click: 前缀为气泡点击（弹出 WinUI 详情面板）；map-console: 前缀为前端报错转发。
    /// </summary>
    private void OnMapWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var message = args.TryGetWebMessageAsString();
            if (string.Equals(message, "map-ready", StringComparison.OrdinalIgnoreCase))
            {
                _mapPageReady = true;
                MapLoadingText.Visibility = Visibility.Collapsed;
                PushMapData();
                return;
            }

            // 地图脚本加载并完成初始化：本地估算一次调用（非高德官方配额数据）
            if (string.Equals(message, "map-loaded", StringComparison.OrdinalIgnoreCase))
            {
                UsageStatsService.Instance.RecordMapLoad();
                return;
            }

            const string siteClickPrefix = "site-click:";
            if (message.StartsWith(siteClickPrefix, StringComparison.Ordinal))
            {
                ShowSiteDetail(message[siteClickPrefix.Length..]);
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

    /// <summary>防止详情面板重复弹出（连点气泡时 WebMessageReceived 会多次触发）。</summary>
    private bool _isSiteDetailOpen;

    /// <summary>
    /// 气泡点击后的工点详情面板（居中等宽弹窗）：名称、类别、状态、最近 14 天时间线、照片缩略图，
    /// 以及「标记为已完工 / 撤销完工标记」操作。操作完成后刷新地图气泡与今日记录列表。
    /// </summary>
    /// <param name="siteName">工点名（与 SiteProgressDaily.SiteName 一致）。</param>
    private async void ShowSiteDetail(string siteName)
    {
        if (_isSiteDetailOpen || string.IsNullOrWhiteSpace(siteName))
        {
            return;
        }

        _isSiteDetailOpen = true;
        try
        {
            var site = SiteProgressService.Instance.BuildMapSites(DateTime.Today)
                .FirstOrDefault(item => string.Equals(item.SiteName, siteName, StringComparison.Ordinal));
            var statusOverride = SiteStatusService.Instance.GetOverride(siteName);
            var isCompleted = statusOverride?.IsCompleted == true;
            var history = DatabaseService.Instance.GetSiteProgressHistory(siteName, 14);

            var panel = new StackPanel { Spacing = 10, Width = 430 };

            // 状态行：色点 + 状态文案 + 连续无更新天数
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            statusRow.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = ParseBrush(site?.ColorHex ?? "#94A3B8")
            });
            statusRow.Children.Add(new TextBlock
            {
                Text = isCompleted
                    ? $"已完工（{FormatDateTime(statusOverride!.CompletedAt)} 标记）"
                    : site?.StatusText ?? "在建",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(statusRow);

            // 类别：取最近一条已定位记录的类别（没有记录时明确说明）
            var latestRow = history.FirstOrDefault();
            panel.Children.Add(new TextBlock
            {
                Text = $"类别：{(latestRow is null ? "暂无已定位记录" : CategoryText(latestRow.Category))}",
                FontSize = 12
            });

            if (isCompleted && !string.IsNullOrWhiteSpace(statusOverride!.Remark))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"完工备注：{statusOverride.Remark}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 116, 139))
                });
            }

            // 类别汇总：按日期 + 类别列出（时间线）
            panel.Children.Add(new TextBlock
            {
                Text = "最近 14 天施工记录",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            var timeline = new StackPanel { Spacing = 3 };
            if (history.Count == 0)
            {
                timeline.Children.Add(new TextBlock { Text = "最近 14 天没有已定位的施工记录。" });
            }
            else
            {
                foreach (var group in history.GroupBy(item => new { item.Date, item.Category })
                             .OrderByDescending(group => group.Key.Date, StringComparer.Ordinal))
                {
                    timeline.Children.Add(new TextBlock
                    {
                        Text = $"{group.Key.Date}　{CategoryText(group.Key.Category)}　{group.Sum(item => item.PhotoCount)} 张",
                        FontSize = 12
                    });
                }
            }

            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = timeline
            });

            // 照片缩略图：点击把下方预览区切换为该图（同一弹窗内放大，不再嵌套第二个 ContentDialog）
            var photos = history
                .Select(item => item.SourceFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            var preview = new Image
            {
                Width = 400,
                Height = 220,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed
            };

            if (photos.Count > 0)
            {
                var thumbRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var photo in photos)
                {
                    var image = new Image
                    {
                        Width = 92,
                        Height = 68,
                        Stretch = Stretch.UniformToFill,
                        Source = LoadBitmap(photo, 160)
                    };
                    image.Tapped += (_, _) =>
                    {
                        preview.Source = LoadBitmap(photo, 800);
                        preview.Visibility = Visibility.Visible;
                    };
                    thumbRow.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Child = image
                    });
                }

                panel.Children.Add(new TextBlock { Text = "最近照片（点击放大）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                panel.Children.Add(thumbRow);
                panel.Children.Add(preview);
            }

            // 未完工且连续多天无更新：只提示，不自动判定完工（不做「超过 N 天自动完工」）
            if (!isCompleted && site is not null && site.DaysSinceActivity >= 3)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"已连续 {site.DaysSinceActivity} 天无更新，如已完工请手动标记。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 116, 139))
                });
            }

            TextBox? remarkBox = null;
            if (!isCompleted)
            {
                remarkBox = new TextBox
                {
                    Header = "完工备注（可选）",
                    PlaceholderText = "例如：主体结构已完成，进入收尾",
                    MaxLength = 200
                };
                panel.Children.Add(remarkBox);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"工点详情 · {siteName}",
                Content = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel
                },
                PrimaryButtonText = isCompleted ? "撤销完工标记" : "标记为已完工",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (isCompleted)
            {
                SiteStatusService.Instance.Revoke(siteName);
            }
            else
            {
                SiteStatusService.Instance.MarkCompleted(siteName, remarkBox?.Text);
            }

            // 操作后立即刷新地图气泡与今日记录（已完工变灰、撤销后恢复天数判定）
            ViewModel.RefreshConstructionData();
            PushMapData();
        }
        catch (Exception ex)
        {
            // async void：详情面板的任何意外只记录，不带崩进程
            Debug.WriteLine($"[工点详情] 打开或操作失败：{ex.Message}");
        }
        finally
        {
            _isSiteDetailOpen = false;
        }
    }

    /// <summary>类别枚举转中文展示名。</summary>
    private static string CategoryText(SiteProgressCategory category) => category switch
    {
        SiteProgressCategory.EarthMaterialInbound => "地材进场",
        SiteProgressCategory.SpoilTransfer => "渣土转运",
        _ => "待确认"
    };

    /// <summary>把 yyyy-MM-dd HH:mm:ss 转为更短的展示文本；解析失败原样返回。</summary>
    private static string FormatDateTime(string text) =>
        DateTime.TryParse(text, out var value) ? value.ToString("yyyy-MM-dd HH:mm") : text;

    /// <summary>按 #RRGGBB 生成画刷；解析失败回退中立灰。</summary>
    private static SolidColorBrush ParseBrush(string? hex)
    {
        var value = (hex ?? string.Empty).TrimStart('#');
        if (value.Length == 6
            && byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 116, 139));
    }

    /// <summary>从本地文件加载图片为 BitmapImage；失败返回 null（缩略图位置留空，不影响面板）。</summary>
    private static BitmapImage? LoadBitmap(string path, int decodePixelWidth)
    {
        try
        {
            return new BitmapImage
            {
                UriSource = new Uri(path),
                DecodePixelWidth = decodePixelWidth
            };
        }
        catch (Exception)
        {
            return null;
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

    /// <summary>关闭启动自检提示条。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnDismissStartupWarningClick(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissStartupWarning();
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
