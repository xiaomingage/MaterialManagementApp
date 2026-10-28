using MaterialManagementApp.Common;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 工作台（首页）页面。上部为台账概况四张统计卡片，下部为四个快捷操作按钮。
/// </summary>
public sealed partial class DashboardPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public DashboardViewModel ViewModel { get; } = new();

    /// <summary>初始化工作台页面。</summary>
    public DashboardPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 每次进入页面都重新统计。用 OnNavigatedTo 而非构造函数，
    /// 这样从其他页面返回时数字会刷新，不会停留在旧值。
    /// </summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
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
