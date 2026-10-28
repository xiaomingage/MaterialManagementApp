using MaterialManagementApp.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 通用占位页面。所有尚未实现的功能菜单都导航到这里，
/// 由导航参数传入菜单标题，避免为每个待建页面创建重复文件。
/// </summary>
public sealed partial class PlaceholderPage : Page
{
    /// <summary>初始化占位页面。</summary>
    public PlaceholderPage()
    {
        InitializeComponent();
    }

    /// <summary>读取导航参数作为页面标题，并渲染说明文字。</summary>
    /// <param name="e">导航事件参数，其 Parameter 应为菜单标题字符串。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var title = e.Parameter as string;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "功能页面";
        }

        TitleText.Text = title;
        DescriptionText.Text = $"「{title}」功能正在建设中，将按工作日志页的实现模式逐步落地。";
    }

    /// <summary>返回到工作台页面。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnBackToDashboardClick(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(DashboardPage));
    }
}
