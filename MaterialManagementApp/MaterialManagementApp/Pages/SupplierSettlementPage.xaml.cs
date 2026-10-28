using System;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 月度结算页面（供应商管理 → 月度结算）。只读展示各供应商结算汇总，
/// 支持按模板导出 Excel 与从模板导入更新结算金额。
/// </summary>
public sealed partial class SupplierSettlementPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public SupplierSettlementViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public SupplierSettlementPage()
    {
        InitializeComponent();

        // 可空 DatePicker.Date 无法被 x:Bind 直接处理，
        // 这里设置 DataContext 让经典 Binding 在运行时完成类型转换
        DataContext = ViewModel;
    }

    /// <summary>进入页面时加载结算汇总。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>按模板导出本月结算表：弹出保存对话框并生成含 SUM 合计公式的 Excel。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = await SupplierSettlementTemplateExport.ExportAsync(DateTime.Now);
            ViewModel.StatusMessage = saved ? "已按模板导出月度结算表。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>从模板导入结算金额：按供应商名称匹配并更新，未匹配项在提示中列出。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnImportTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var items = await SupplierSettlementTemplateImport.PickAndParseAsync();
            if (items is null)
            {
                ViewModel.StatusMessage = "已取消导入。";
                return;
            }

            if (items.Count == 0)
            {
                ViewModel.StatusMessage = "所选文件中没有可导入的数据行（已跳过标题行与合计行）。";
                return;
            }

            var dialog = new ContentDialog
            {
                // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
                XamlRoot = XamlRoot,
                Title = "确认导入",
                Content = $"共解析到 {items.Count} 条结算记录，将按「供应商名称」匹配并更新月度结算金额，未匹配的名称会被跳过。是否继续？",
                PrimaryButtonText = "导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                ViewModel.StatusMessage = "已取消导入。";
                return;
            }

            var (updated, notFound) = SupplierSettlementTemplateImport.Commit(items);

            ViewModel.Load();

            // Load 内部会改写 StatusMessage，因此结果提示必须放在它之后
            ViewModel.StatusMessage = notFound.Count == 0
                ? $"已更新 {updated} 家供应商的月度结算金额。"
                : $"已更新 {updated} 家；以下供应商未匹配，已跳过：{string.Join("、", notFound)}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"导入失败：{ex.Message}";
        }
    }
}
