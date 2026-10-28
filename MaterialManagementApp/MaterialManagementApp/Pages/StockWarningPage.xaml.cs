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
/// 库存预警页面（库存管理 → 库存预警）。列出结存低于阈值的物资，整页只读，
/// 上方为预警阈值与物资名称查询，工具栏提供导出与刷新。
/// </summary>
public sealed partial class StockWarningPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public StockWarningViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public StockWarningPage()
    {
        InitializeComponent();
    }

    /// <summary>进入页面时按当前阈值加载预警清单。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>导出：把当前预警清单写入 Excel 并弹出保存对话框。</summary>
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
                    x.MaterialName,
                    x.Specification,
                    x.Unit,
                    x.Balance.ToString("0.##")
                })
                .ToList();

            string[] headers = { "物资名称", "规格型号", "单位", "当前结存" };
            var saved = await SimpleListExport.ExportAsync("库存预警", headers, rows);
            ViewModel.StatusMessage = saved ? "已导出库存预警清单。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }
}
