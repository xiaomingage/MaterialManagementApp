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
/// 供应商列表页面（供应商管理 → 供应商列表）。
/// 上半部分为查询筛选面板，下半部分为可编辑表格，
/// 「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后入库、「取消」直接移除。
/// </summary>
public sealed partial class SupplierListPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public SupplierListViewModel ViewModel { get; } = new();

    /// <summary>合同材料单价明细视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public ContractItemViewModel ContractItems { get; } = new();

    /// <summary>初始化页面。</summary>
    public SupplierListPage()
    {
        InitializeComponent();
        ContractItems.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ContractItemViewModel.StatusMessage))
            {
                ItemStatusText.Text = ContractItems.StatusMessage;
            }
        };
    }

    /// <summary>进入页面时按当前筛选条件加载供应商列表。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>表格选中行变化时同步到视图模型，并联动加载该供应商的合同材料单价明细。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选中项变化参数。</param>
    private void OnLedgerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedSupplier = LedgerGrid.SelectedItem as Supplier;
        ContractItems.LoadForSupplier(ViewModel.SelectedSupplier?.Id);
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
                System.Diagnostics.Debug.WriteLine($"[SupplierListPage] 自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>行内「保存」：新增行执行入库，已有行执行更新。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Supplier row })
        {
            ViewModel.SaveRow(row);
        }
    }

    /// <summary>行内「取消」：移除尚未入库的空白新增行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Supplier row })
        {
            ViewModel.CancelNewRow(row);
        }
    }

    /// <summary>行内「删除」：二次确认后从集合与数据库中移除该行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Supplier row })
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
        if (ViewModel.SelectedSupplier is not { } row)
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
                    x.Name,
                    x.ContractNo,
                    x.ContactPerson,
                    x.Phone,
                    x.MonthlySettlement.ToString("0.##"),
                    x.SignDate,
                    x.Remark
                })
                .ToList();

            string[] headers = { "供应商名称", "合同编号", "联系人", "联系电话", "月度结算", "签约日期", "备注" };
            var saved = await SimpleListExport.ExportAsync("供应商列表", headers, rows);
            ViewModel.StatusMessage = saved ? "已导出供应商列表。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>弹出确认对话框，确认后删除指定行。</summary>
    /// <param name="row">待删除的记录。</param>
    private async System.Threading.Tasks.Task ConfirmAndDeleteAsync(Supplier row)
    {
        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除供应商「{row.Name}」吗？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteRow(row);
        }
    }

    // ------------------------------------------------------------------
    // 合同材料单价明细事件处理
    // ------------------------------------------------------------------

    /// <summary>明细表选中行变化时同步到视图模型。</summary>
    private void OnItemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContractItems.Selected = ItemGrid.SelectedItem as SupplierContractItem;
    }

    /// <summary>明细「新增」：插入空白行并进入编辑状态。</summary>
    private void OnItemNewRowClick(object sender, RoutedEventArgs e)
    {
        ContractItems.NewRow();

        var row = ContractItems.Items.FirstOrDefault(x => x.IsNewRow);
        if (row is null)
        {
            return;
        }

        ItemGrid.SelectedItem = row;
        ItemGrid.CurrentColumn = ItemGrid.Columns[0];
        ItemGrid.ScrollIntoView(row, ItemGrid.Columns[0]);

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ItemGrid.BeginEdit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupplierListPage] 明细自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>明细行内「保存」：新增入库或已有行更新。</summary>
    private void OnItemSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SupplierContractItem row })
        {
            ContractItems.SaveRow(row);
        }
    }

    /// <summary>明细行内「取消」：移除未入库的新增行。</summary>
    private void OnItemCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SupplierContractItem row })
        {
            ContractItems.CancelNewRow(row);
        }
    }

    /// <summary>明细行内「删除」：二次确认后删除。</summary>
    private async void OnItemDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SupplierContractItem row })
        {
            return;
        }

        await ConfirmAndDeleteItemAsync(row);
    }

    /// <summary>明细工具栏「删除」：二次确认后删除当前选中行。</summary>
    private async void OnItemDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ContractItems.Selected is not { } row)
        {
            ContractItems.StatusMessage = "请先在明细表格中选中要删除的行。";
            return;
        }

        await ConfirmAndDeleteItemAsync(row);
    }

    /// <summary>明细「刷新」：按当前供应商重新加载明细。</summary>
    private void OnItemRefreshClick(object sender, RoutedEventArgs e)
    {
        ContractItems.LoadForSupplier(ContractItems.CurrentSupplierId);
    }

    /// <summary>弹出确认对话框，确认后删除合同材料明细。</summary>
    private async System.Threading.Tasks.Task ConfirmAndDeleteItemAsync(SupplierContractItem row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除材料「{row.MaterialName}」（单价 {row.UnitPrice} 元/{row.Unit}）吗？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ContractItems.DeleteRow(row);
        }
    }
}
