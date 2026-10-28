using System;
using System.Linq;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 挖机进场资料页面（机具设备 → 机具入场）。
/// 上半部分为查询筛选面板，下半部分为可编辑表格（18 个字段全部行内编辑）：
/// 「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后入库、「取消」直接移除。
/// 工具栏「导出」对当前选中行按模板生成 Word 验收单。
/// </summary>
public sealed partial class ExcavatorEntryPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public ExcavatorEntryViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public ExcavatorEntryPage()
    {
        InitializeComponent();

        // 可空 DatePicker.Date 无法被 x:Bind 直接处理，这里设置 DataContext 让经典 Binding 在运行时完成类型转换
        DataContext = ViewModel;
    }

    /// <summary>进入页面时按当前筛选条件加载进场资料。</summary>
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
        ViewModel.SelectedEntry = LedgerGrid.SelectedItem as ExcavatorEntry;
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
                System.Diagnostics.Debug.WriteLine($"[ExcavatorEntryPage] 自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>行内「保存」：新增行执行入库，已有行执行更新。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExcavatorEntry row })
        {
            ViewModel.SaveRow(row);
        }
    }

    /// <summary>行内「取消」：移除尚未入库的空白新增行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExcavatorEntry row })
        {
            ViewModel.CancelNewRow(row);
        }
    }

    /// <summary>行内「删除」：二次确认后从集合与数据库中移除该行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExcavatorEntry row })
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
        if (ViewModel.SelectedEntry is not { } row)
        {
            ViewModel.StatusMessage = "请先在表格中选中要删除的行。";
            return;
        }

        await ConfirmAndDeleteAsync(row);
    }

    /// <summary>导出：按模板把当前选中行生成 Word 验收单；未选中时提示。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnExportWordClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedEntry is not { } row)
        {
            ViewModel.StatusMessage = "请先在表格中选中要导出的进场记录。";
            return;
        }

        if (row.IsNewRow)
        {
            ViewModel.StatusMessage = "该行尚未保存，请先点击行末「保存」后再导出。";
            return;
        }

        try
        {
            var saved = await ExcavatorEntryWordExport.ExportAsync(row);
            ViewModel.StatusMessage = saved ? "已按模板导出 Word 验收单。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 模板缺失、目标文件被占用等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>弹出确认对话框，确认后删除指定行。</summary>
    /// <param name="row">待删除的记录。</param>
    private async System.Threading.Tasks.Task ConfirmAndDeleteAsync(ExcavatorEntry row)
    {
        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除设备「{row.EquipmentName}」的进场资料吗？该操作不可撤销。",
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
