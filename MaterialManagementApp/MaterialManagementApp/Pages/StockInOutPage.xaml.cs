using System;
using System.Collections.Generic;
using System.Linq;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 入库领用退库页面（库存管理 → 入库领用退库）。
/// 上半部分为查询筛选面板，下半部分为可编辑表格：
/// 「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后入库、「取消」直接移除。
/// </summary>
public sealed partial class StockInOutPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public StockInOutViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public StockInOutPage()
    {
        InitializeComponent();

        // ComboBox.SelectedItem 与可空 DatePicker.Date 都无法被 x:Bind 直接处理，
        // 这里设置 DataContext 让经典 Binding 在运行时完成类型转换
        DataContext = ViewModel;
    }

    /// <summary>进入页面时按当前筛选条件加载库存流水。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>表格选中行变化时同步到视图模型，保留选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选中项变化参数。</param>
    private void OnLedgerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedItem = LedgerGrid.SelectedItem as StockRecord;
    }

    /// <summary>
    /// 「新增」：在表格首行插入空白行，并让该行立即进入编辑状态，
    /// 用户无需弹窗即可直接开始录入。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnNewRowClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NewRowCommand.Execute(null);

        var row = ViewModel.Items.FirstOrDefault(x => x.IsNewRow);
        if (row is null)
        {
            return;
        }

        LedgerGrid.SelectedItem = row;
        LedgerGrid.CurrentColumn = LedgerGrid.Columns[0];
        LedgerGrid.ScrollIntoView(row, LedgerGrid.Columns[0]);

        // 新行的单元格容器要等本次布局完成后才生成，BeginEdit 必须延后一帧再调用
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                LedgerGrid.BeginEdit();
            }
            catch (Exception ex)
            {
                // 尚未生成行容器时 BeginEdit 会失败，此时保持选中态即可，不影响手工点击编辑
                System.Diagnostics.Debug.WriteLine($"[StockInOutPage] 自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>行内「保存」：新增行执行入库，已有行执行更新。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StockRecord row })
        {
            ViewModel.SaveRow(row);
        }
    }

    /// <summary>行内「取消」：移除尚未入库的空白新增行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StockRecord row })
        {
            ViewModel.CancelNewRow(row);
        }
    }

    /// <summary>行内「删除」：二次确认后从集合与数据库中移除该行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StockRecord row })
        {
            return;
        }

        await ConfirmAndDeleteAsync(row);
    }

    /// <summary>工具栏「删除」：二次确认后删除当前选中行；未选中时提示。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } row)
        {
            ViewModel.StatusMessage = "请先在表格中选中要删除的行。";
            return;
        }

        await ConfirmAndDeleteAsync(row);
    }

    /// <summary>导出：把当前列表（不含未保存的空白行）写入 Excel 并弹出保存对话框。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 导出全部筛选结果而非当前页；尚未保存的空白行不入导出结果，避免把空记录写进 Excel
            var rows = ViewModel.GetFilteredRowsForExport()
                .Where(x => !x.IsNewRow)
                .Select(x => (IReadOnlyList<string?>)new string?[]
                {
                    x.OccurDate,
                    x.MaterialSource,
                    x.ChangeType,
                    x.MaterialName,
                    x.Specification,
                    x.Unit,
                    x.Quantity.ToString("0.##"),
                    x.Handler,
                    x.Remark
                })
                .ToList();

            string[] headers = { "发生日期", "物资来源", "变更类型", "物资名称", "规格型号", "单位", "数量", "经办人", "备注" };
            var saved = await SimpleListExport.ExportAsync("库存流水", headers, rows);
            ViewModel.StatusMessage = saved ? "已导出库存流水。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>弹出确认对话框，确认后删除指定行。</summary>
    /// <param name="row">待删除的记录。</param>
    private async System.Threading.Tasks.Task ConfirmAndDeleteAsync(StockRecord row)
    {
        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除物资「{row.MaterialName}」（{row.OccurDate}）的流水记录吗？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteRow(row);
        }
    }
}
