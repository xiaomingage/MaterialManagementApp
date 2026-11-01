using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MaterialManagementApp.Common;
using MaterialManagementApp.Pages;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace MaterialManagementApp;

/// <summary>
/// 主窗口。承载 NavigationView 菜单、内容 Frame 与底部状态栏，
/// 负责把菜单选择转换为页面导航，并驱动右下角的实时时钟；
/// 同时编排托盘后台运行：点 X 按配置隐藏到托盘，真实退出只能走托盘菜单。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>每秒刷新一次状态栏时钟的计时器。</summary>
    private readonly DispatcherTimer _clockTimer = new();

    /// <summary>主窗口句柄，托盘唤起时用于还原与前置窗口。</summary>
    private readonly IntPtr _hwnd;

    /// <summary>是否为托盘菜单触发的真实退出（区分「点 X 隐藏到托盘」）。</summary>
    private bool _forceExit;

    /// <summary>主窗口日志实例：关闭行为保存失败等异常需要留痕，便于排查用户现场问题。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<MainWindow>();

    /// <summary>初始化主窗口、默认导航到工作台、启动时钟并刷新状态栏。</summary>
    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);

        SetWindowIcon();

        // 托盘后台运行：托盘图标常驻启用（「每次询问」「最小化到托盘」两种关闭行为都可能用到）；
        // 点 X 的关闭行为在 OnAppWindowClosing 按 CloseAction 分流。
        // 窗口毛玻璃背景由 ThemeHelper.ApplyMode 按主题模式统一开关（XAML 中的默认背景仅作兜底）。
        TrayIconManager.Instance.Configure(
            Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            Title ?? "物资管理系统",
            ShowFromTray,
            ExitFromTray);
        TrayIconManager.Instance.SetEnabled(true);
        AppWindow.Closing += OnAppWindowClosing;

        ContentFrame.Navigate(typeof(DashboardPage));

        // 设置保存后需要立刻把主题应用到根元素，否则用户要重启才能看到效果
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;

        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();

        UpdateClock();
        UpdateStatusBar();

        // 左下角版本号（可点击检查更新）：文本统一取自 AppInfo.CurrentVersion，
        // 版本号唯一来源是 csproj 的 <Version>，XAML 与代码里都不写死字面量
        VersionRun.Text = $"v{AppInfo.CurrentVersion}";

        Closed += OnWindowClosed;
    }

    /// <summary>
    /// 设置窗口标题栏与任务栏图标。WinUI 3 的 Window 没有 Icon 属性，
    /// 必须通过 AppWindow.SetIcon 指定图标文件（exe 图标由 csproj 的 ApplicationIcon 决定）。
    /// </summary>
    private void SetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch (Exception ex)
        {
            // 图标缺失只影响观感，不应阻断窗口创建
            System.Diagnostics.Debug.WriteLine($"[MainWindow] 设置窗口图标失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 菜单选择变化时解析路由键并导航到对应页面。
    /// 分组项（带二级菜单）的 Tag 为空，因此在这里直接返回，只做展开/收起。
    /// </summary>
    /// <param name="sender">NavigationView 实例。</param>
    /// <param name="args">选择变化事件参数。</param>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string route || string.IsNullOrWhiteSpace(route))
        {
            return;
        }

        Navigate(route, sender);
    }

    /// <summary>
    /// 处理页脚「设置」项的点击。页脚项设置了 SelectsOnInvoked=False，
    /// 不会触发 SelectionChanged，只能通过 ItemInvoked 捕获；
    /// 普通菜单仍走 SelectionChanged，这里直接返回以避免重复导航。
    /// </summary>
    /// <param name="sender">NavigationView 实例。</param>
    /// <param name="args">项目调用事件参数。</param>
    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is not string route || route != PageRoutes.Settings)
        {
            return;
        }

        Navigate(route, sender);

        // 页脚项不参与选中态，导航后清空选中避免与业务菜单同时高亮
        sender.SelectedItem = null;
    }

    /// <summary>按路由键导航到目标页面。</summary>
    /// <param name="route">菜单项 Tag 中的路由键。</param>
    /// <param name="sender">NavigationView 实例，仅用于设置页清空选中态。</param>
    private void Navigate(string route, NavigationView sender)
    {
        var (pageType, title) = PageRoutes.Resolve(route);

        // 占位页靠导航参数区分标题，因此不能仅凭「页面类型相同」就跳过导航
        if (ContentFrame.CurrentSourcePageType == pageType && pageType != typeof(PlaceholderPage))
        {
            return;
        }

        ContentFrame.Navigate(pageType, title);
    }

    /// <summary>
    /// 全局导航入口：按路由键选中左侧导航项，由选中态驱动内容区跳转。
    /// 供工作台快捷磁贴等页面调用，保证主内容与侧边栏选中态（高亮/蓝条）始终同步，
    /// 不会出现「右侧页面变了、左侧还停在工作台」的情况。
    /// </summary>
    /// <param name="route">路由键（PageRoutes 常量）。</param>
    /// <returns>找到并选中导航项返回 true；路由不存在时返回 false。</returns>
    public bool NavigateTo(string route)
    {
        var item = FindNavItemByTag(NavView.MenuItems, route)
            ?? FindNavItemByTag(NavView.FooterMenuItems, route);
        if (item is null)
        {
            return false;
        }

        // 设置选中项即触发 OnNavSelectionChanged → Navigate，页面与高亮一并更新
        NavView.SelectedItem = item;
        return true;
    }

    /// <summary>递归查找 Tag 匹配路由键的导航项（含分组项的二级菜单）。</summary>
    /// <param name="items">候选导航项集合。</param>
    /// <param name="route">路由键。</param>
    private static NavigationViewItem? FindNavItemByTag(IList<object> items, string route)
    {
        foreach (var item in items)
        {
            if (item is not NavigationViewItem container)
            {
                continue;
            }

            if (container.Tag as string == route)
            {
                return container;
            }

            if (container.MenuItems.Count > 0)
            {
                var nested = FindNavItemByTag(container.MenuItems, route);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>计时器回调：刷新时钟文本。</summary>
    /// <param name="sender">计时器实例。</param>
    /// <param name="e">事件参数。</param>
    private void OnClockTick(object? sender, object e)
    {
        UpdateClock();
    }

    /// <summary>把当前时间格式化为 yyyy-MM-dd HH:mm:ss 星期X 并写入状态栏右侧。</summary>
    private void UpdateClock()
    {
        var now = DateTime.Now;

        // DayOfWeek 是 0=周日 的枚举，直接索引中文字符串比 switch 更简洁
        var week = "日一二三四五六"[(int)now.DayOfWeek];
        ClockText.Text = $"{now:yyyy-MM-dd HH:mm:ss} 星期{week}";
    }

    /// <summary>把数据库文件名与主要表记录数写入状态栏左侧；异常时显示提示而非崩溃。</summary>
    private void UpdateStatusBar()
    {
        try
        {
            var databasePath = DatabaseService.Instance.DatabasePath;
            var fileName = string.IsNullOrWhiteSpace(databasePath)
                ? "（未初始化）"
                : Path.GetFileName(databasePath);

            var ledgerCount = DatabaseService.Instance.ExecuteScalarCount("SELECT COUNT(*) FROM MaterialLedger;");
            var logCount = DatabaseService.Instance.ExecuteScalarCount("SELECT COUNT(*) FROM WorkLog;");

            StatusText.Text = $"数据库：{fileName}　|　台账 {ledgerCount} 条　|　工作日志 {logCount} 条";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"状态读取失败：{ex.Message}";
        }
    }

    // ==================== 托盘后台运行 ====================

    /// <summary>
    /// 拦截窗口关闭，按设置的关闭行为（CloseAction）分流：
    /// Exit 直接放行（Closed 事件负责清理）；MinimizeToTray 且托盘可用时隐藏到托盘；
    /// Ask（以及托盘不可用的兜底路径）弹窗询问「直接退出 / 最小化到托盘」，支持「记住我的选择」。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceExit)
        {
            return;
        }

        var action = SettingsService.Instance.Settings.CloseAction;
        if (string.Equals(action, "Exit", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(action, "MinimizeToTray", StringComparison.OrdinalIgnoreCase)
            && TrayIconManager.Instance.IsIconVisible)
        {
            args.Cancel = true;
            HideToTray();
            return;
        }

        // Ask（或 MinimizeToTray 但托盘不可用的兜底）：取消本次关闭，改为弹窗询问
        args.Cancel = true;
        DispatcherQueue.TryEnqueue(ShowCloseConfirmDialogAsync);
    }

    /// <summary>防止关闭确认弹窗重复弹出（快速连点 X 时 Closing 会多次触发）。</summary>
    private bool _isCloseDialogOpen;

    /// <summary>
    /// 关闭确认弹窗：直接退出 / 最小化到托盘二选一，可勾选「记住我的选择，以后不再提示」，
    /// 勾选后写回设置，之后点击 X 按记住的行为执行，不再弹窗（仍可在设置页修改）。
    /// </summary>
    private async void ShowCloseConfirmDialogAsync()
    {
        if (_isCloseDialogOpen)
        {
            return;
        }

        _isCloseDialogOpen = true;
        try
        {
            var remember = new CheckBox { Content = "记住我的选择，以后不再提示" };

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "要直接退出程序，还是最小化到系统托盘继续后台运行？",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(remember);

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "关闭物资管理系统",
                Content = content,
                PrimaryButtonText = "直接退出",
                SecondaryButtonText = "最小化到托盘",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            switch (result)
            {
                case ContentDialogResult.Primary:
                    if (remember.IsChecked == true)
                    {
                        SaveCloseAction("Exit");
                    }

                    _forceExit = true;
                    Close();
                    break;

                case ContentDialogResult.Secondary when TrayIconManager.Instance.IsIconVisible:
                    if (remember.IsChecked == true)
                    {
                        SaveCloseAction("MinimizeToTray");
                    }

                    HideToTray();
                    break;

                case ContentDialogResult.Secondary:
                    // 托盘图标不可用，无法后台驻留：按直接退出处理，避免窗口藏起来找不回
                    if (remember.IsChecked == true)
                    {
                        SaveCloseAction("Exit");
                    }

                    _forceExit = true;
                    Close();
                    break;
            }
            // Cancel：什么都不做，窗口保持打开
        }
        catch (Exception ex)
        {
            // async void 未捕获异常会直接带崩进程：弹窗路径的任何意外（如保存配置失败）只记录、不外抛
            _logger.LogError(ex, "关闭确认弹窗处理异常");
        }
        finally
        {
            _isCloseDialogOpen = false;
        }
    }

    /// <summary>
    /// 把用户在关闭确认弹窗中勾选的行为写入设置并立即保存。
    /// 保存失败只影响「记住选择」（本次不记住），弹窗中选择的退出/托盘行为照常执行，绝不抛异常崩溃。
    /// </summary>
    private void SaveCloseAction(string action)
    {
        try
        {
            var settings = SettingsService.Instance.Settings;
            settings.CloseAction = action;
            SettingsService.Instance.Save();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存关闭行为设置失败，本次不记住选择");
        }
    }

    /// <summary>
    /// 隐藏主窗口到托盘后台：暂停状态栏时钟降低占用（后台降速优化），
    /// 后台任务不受窗口隐藏影响。进程真实退出只能从托盘菜单发起。
    /// </summary>
    private void HideToTray()
    {
        _clockTimer.Stop();
        AppWindow.Hide();
    }

    /// <summary>从托盘唤起主窗口：显示、还原最小化并前置，恢复状态栏时钟。</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        TrayIconManager.ShowAndActivate(_hwnd);
        _clockTimer.Start();
        UpdateClock();
    }

    /// <summary>托盘菜单「退出软件」：销毁托盘图标后关闭窗口，进程真实退出。</summary>
    public void ExitFromTray()
    {
        _forceExit = true;
        Close();
    }

    /// <summary>设置保存后重新应用主题（关闭行为与托盘显隐即时生效于下次点 X，无需额外处理）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var settings = SettingsService.Instance.Settings;
        ThemeHelper.ApplyMode(this, Content as FrameworkElement, settings);
    }

    // ==================== 版本更新（左下角版本号点击检查） ====================

    /// <summary>防止更新弹窗重复弹出（连点版本号时 Tapped 会多次触发）。</summary>
    private bool _isUpdateDialogOpen;

    /// <summary>
    /// 版本号点击入口：到 GitHub Releases 检查是否有新版本。
    /// 检测请求在后台线程执行（await 释放 UI 线程），期间展示可取消的「检查中」弹窗；
    /// 结果分流：已是最新 / 发现新版本（用户确认后才下载）/ 网络失败友好提示。
    /// 缓存由 UpdateService 维护（5 分钟内复用上次结果，不重复请求）。
    /// </summary>
    private async void OnVersionTextTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isUpdateDialogOpen)
        {
            return;
        }

        _isUpdateDialogOpen = true;
        try
        {
            var checking = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "检查更新",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "正在连接 GitHub 检查更新...", TextWrapping = TextWrapping.Wrap },
                        new ProgressBar { IsIndeterminate = true }
                    }
                },
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            var checkTask = checking.ShowAsync().AsTask();

            UpdateCheckResult result;
            try
            {
                result = await UpdateService.Instance.CheckForUpdateAsync();
            }
            catch (Exception ex)
            {
                // 网络 / 接口异常：给友好提示，绝不卡死或崩溃
                _logger.LogWarning(ex, "检查更新失败（网络或接口异常）");
                if (!checkTask.IsCompleted)
                {
                    checking.Hide();
                    await checkTask;
                    await ShowInfoDialogAsync("检查更新失败", "无法连接 GitHub，请检查网络后重试。");
                }
                return;
            }

            if (checkTask.IsCompleted)
            {
                // 用户在检测期间点了「取消」：结果丢弃，不打扰
                return;
            }

            checking.Hide();
            await checkTask;

            if (!result.HasUpdate)
            {
                await ShowInfoDialogAsync("检查更新", $"当前已是最新版本（v{result.CurrentVersion}）。");
                return;
            }

            await ShowUpdateDialogAsync(result);
        }
        catch (Exception ex)
        {
            // async void：弹窗流程任何意外只记录，不外抛崩溃
            _logger.LogError(ex, "版本更新流程异常");
        }
        finally
        {
            _isUpdateDialogOpen = false;
        }
    }

    /// <summary>
    /// 「发现新版本」弹窗：展示当前版本、最新版本与更新日志（release body）。
    /// 点「立即更新」切换为下载进度模式（百分比实时刷新），「稍后再说」变「取消下载」，
    /// 取消即中止请求并删除半截临时文件；下载完成后自动拉起安装向导并退出本程序。
    /// </summary>
    private async Task ShowUpdateDialogAsync(UpdateCheckResult result)
    {
        if (string.IsNullOrEmpty(result.SetupDownloadUrl))
        {
            // release 存在但没上传安装包附件：引导用户到 Releases 页面手动下载
            await ShowInfoDialogAsync(
                "无法自动更新",
                $"最新版本 v{result.LatestVersion} 未附带安装包，请到 GitHub Releases 页面手动下载：\n" +
                $"https://github.com/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest");
            return;
        }

        var notesText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(result.ReleaseNotes) ? "（本次更新没有说明文字）" : result.ReleaseNotes,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var notesScroll = new ScrollViewer
        {
            MaxHeight = 200,
            Margin = new Thickness(0, 4, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = notesText
        };
        var percentText = new TextBlock { Text = "准备下载...", FontSize = 12, Opacity = 0.8 };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100 };
        var progressPanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        progressPanel.Children.Add(percentText);
        progressPanel.Children.Add(bar);

        var infoPanel = new StackPanel { Spacing = 8, MaxWidth = 440 };
        infoPanel.Children.Add(new TextBlock { Text = $"当前版本：v{result.CurrentVersion}" });
        infoPanel.Children.Add(new TextBlock { Text = $"最新版本：v{result.LatestVersion}" });
        infoPanel.Children.Add(notesScroll);
        infoPanel.Children.Add(progressPanel);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"发现新版本 v{result.LatestVersion}",
            Content = infoPanel,
            PrimaryButtonText = "立即更新",
            SecondaryButtonText = "稍后再说",
            DefaultButton = ContentDialogButton.Primary
        };
        Task showTask = dialog.ShowAsync().AsTask();

        var downloading = false;
        CancellationTokenSource? downloadCts = null;

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            // 阻止弹窗关闭并切换到下载模式；下载中再点「立即更新」无效
            args.Cancel = true;
            if (downloading)
            {
                return;
            }

            downloading = true;
            dialog.Title = $"正在下载 v{result.LatestVersion}";
            dialog.SecondaryButtonText = "取消下载";
            dialog.IsPrimaryButtonEnabled = false;
            notesScroll.Visibility = Visibility.Collapsed;
            progressPanel.Visibility = Visibility.Visible;
            _ = RunDownloadAsync();
        };

        dialog.SecondaryButtonClick += (_, _) =>
        {
            // 阶段一「稍后再说」：直接关闭；阶段二「取消下载」：中止请求并清理临时文件
            if (downloading)
            {
                downloadCts?.Cancel();
            }
        };

        async Task RunDownloadAsync()
        {
            downloadCts = new CancellationTokenSource();
            var destPath = UpdateService.GetDownloadPath(result.LatestVersion);
            var progress = new Progress<double>(p =>
            {
                var clamped = Math.Min(p, 100);
                bar.Value = clamped;
                percentText.Text = $"下载进度：{clamped:F0}%";
            });
            try
            {
                await UpdateService.Instance
                    .DownloadSetupAsync(result.SetupDownloadUrl!, destPath, progress, downloadCts.Token)
                    .ConfigureAwait(true);

                // 下载完成：关弹窗 → 拉起安装向导 → 退出本程序。
                // Inno 覆盖安装保留数据库与配置（安装包排除 *.db 且不清理旧目录），装完由用户手动打开新版。
                dialog.Hide();
                await showTask;
                UpdateService.LaunchInstaller(destPath);
                _forceExit = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                // 用户取消下载：删除半截临时文件，回到当前版本继续使用
                TryDeleteFile(destPath);
                dialog.Hide();
                await showTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "下载更新包失败");
                TryDeleteFile(destPath);
                dialog.Hide();
                await showTask;
                await ShowInfoDialogAsync("下载失败", "更新包下载失败，请检查网络后重试。（未完成的下载文件已清理）");
            }
        }

        await showTask;
    }

    /// <summary>尽力删除临时下载文件；删除失败不影响流程（TEMP 残留会被系统清理）。</summary>
    private static void TryDeleteFile(string path)
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
            // 文件被短暂占用等场景忽略即可，不影响功能
        }
    }

    /// <summary>简单信息弹窗（标题 + 文案 + 确定），复用主窗口 XamlRoot。</summary>
    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    /// <summary>窗口关闭时停止计时器、解除事件订阅并销毁托盘图标，避免泄漏。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="args">窗口事件参数。</param>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        TrayIconManager.Instance.Shutdown();
    }
}
