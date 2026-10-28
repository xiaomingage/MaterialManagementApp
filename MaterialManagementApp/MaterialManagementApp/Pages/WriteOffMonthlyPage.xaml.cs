using System;
using System.Collections.Generic;
using System.Linq;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 月度核销页面（物资核销 → 月度核销）。
/// 只读汇总页：上方为月份筛选（留空为全部月份），下方为只读月度汇总表格，工具栏提供导出与刷新。
/// </summary>
public sealed partial class WriteOffMonthlyPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public WriteOffMonthlyViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public WriteOffMonthlyPage()
    {
        InitializeComponent();
    }

    /// <summary>进入页面时加载月度汇总。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>导出：把当前月度汇总写入 Excel 并弹出保存对话框。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 导出始终取当前筛选条件下的全量结果，不受当前页码限制
            var rows = ViewModel.AllItems
                .Select(x => (IReadOnlyList<string?>)new string?[]
                {
                    x.WriteOffMonth,
                    x.ItemCount.ToString(),
                    x.TotalQty.ToString("0.##"),
                    x.TotalAmount.ToString("0.##")
                })
                .ToList();

            string[] headers = { "核销月份", "记录条数", "数量合计", "金额合计（元）" };
            var saved = await SimpleListExport.ExportAsync("月度核销汇总", headers, rows);
            ViewModel.StatusMessage = saved ? "已导出月度核销汇总。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }
}
