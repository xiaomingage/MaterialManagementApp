using System;
using System.Collections.Generic;
using System.IO;
using MaterialManagementApp.Common;
using MaterialManagementApp.Pages;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
