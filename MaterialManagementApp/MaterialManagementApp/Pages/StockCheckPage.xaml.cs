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
/// 库存查询页面（库存管理 → 库存查询）。展示按物资聚合的账面结存，整页只读，
/// 上方为查询筛选面板，工具栏提供导出与刷新。
/// </summary>
public sealed partial class StockCheckPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public StockCheckViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public StockCheckPage()
    {
        InitializeComponent();

        // ComboBox.SelectedItem 无法被 x:Bind 直接处理，这里设置 DataContext 让经典 Binding 在运行时完成类型转换
        DataContext = ViewModel;
    }

    /// <summary>进入页面时按当前筛选条件加载账面结存。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>导出：把当前列表写入 Excel 并弹出保存对话框。</summary>
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
                    x.InQty.ToString("0.##"),
                    x.OutQty.ToString("0.##"),
                    x.ReturnQty.ToString("0.##"),
                    x.Balance.ToString("0.##")
                })
                .ToList();

            string[] headers = { "物资名称", "规格型号", "单位", "累计入库", "累计出库", "累计退库", "账面结存" };
            var saved = await SimpleListExport.ExportAsync("库存查询", headers, rows);
            ViewModel.StatusMessage = saved ? "已导出库存查询结果。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }
}
