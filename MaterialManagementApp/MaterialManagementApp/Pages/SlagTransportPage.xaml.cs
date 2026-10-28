using System;
using System.Collections.Generic;
using System.Linq;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI.Popups;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 渣土运输页面（地材台账 → 渣土运输）。
/// 上半部分为查询筛选面板，下半部分为可编辑表格：
/// 「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后入库、「取消」直接移除；
/// 「OCR识别」打开非模态离线识别窗口，识别完成的字段自动填入空白行，保持待保存状态。
/// </summary>
public sealed partial class SlagTransportPage : Page
{
    /// <summary>当前打开的非模态 OCR 识别窗口；同一时间只保留一个实例。</summary>
    private OcrImportWindow? _ocrWindow;

    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public SlagTransportViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public SlagTransportPage()
    {
        InitializeComponent();

        // 可空 DatePicker.Date 无法被 x:Bind 直接处理，
        // 这里设置 DataContext 让经典 Binding 在运行时完成类型转换
        DataContext = ViewModel;

        // 保存失败（校验不通过 / 数据库异常）时弹出模态警告弹窗，表单内容保留
        ViewModel.SaveFailed += OnViewModelSaveFailed;
    }

    /// <summary>渣土台账保存失败：弹出 WinUI 原生警告 MessageDialog，阻塞交互提醒用户。</summary>
    private async void OnViewModelSaveFailed(object? sender, EventArgs e)
    {
        var dialog = new MessageDialog("台账保存失败，请检查后重试！", "保存失败")
        {
            Commands = { new UICommand("确认") },
            DefaultCommandIndex = 0,
            CancelCommandIndex = 0
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
        WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
        await dialog.ShowAsync();
    }

    /// <summary>进入页面时按当前筛选条件加载渣土运输数据。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DeptPhaseService.Instance.Refresh();
        DeptPhaseFilter.RefreshOptions();
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>表格选中行变化时同步到视图模型，保留选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选中项变化参数。</param>
    private void OnLedgerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedTransport = LedgerGrid.SelectedItem as SlagTransport;
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
        FocusNewRow();
    }

    /// <summary>行内「保存」：新增行执行入库，已有行执行更新。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SlagTransport row })
        {
            ViewModel.SaveRow(row);
        }
    }

    /// <summary>行内「取消」：移除尚未入库的空白新增行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SlagTransport row })
        {
            ViewModel.CancelNewRow(row);
        }
    }

    /// <summary>行内「删除」：二次确认后从集合与数据库中移除该行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SlagTransport row })
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
        if (ViewModel.SelectedTransport is not { } row)
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
                    x.EntryDate,
                    x.EntryTime,
                    x.NoLast4,
                    x.PlateNumber,
                    x.UsageAddress,
                    x.MaterialName,
                    x.Specification,
                    x.Quantity.ToString("0.##"),
                    x.Unit,
                    x.TransportFee.ToString("0.##"),
                    x.SiteSignature,
                    x.ProjectDept,
                    x.ProjectPhase,
                    x.Remark
                })
                .ToList();

            string[] headers = { "票据日期", "时间", "NO 后四位", "车牌", "使用地址", "材料种类", "规格", "数量", "单位", "运输费", "现场签字", "项目部", "期数", "备注" };
            var phase = ViewModel.SelectedPhase;
            var sheetName = string.IsNullOrEmpty(phase) ? "渣土运输" : $"渣土运输-{phase}";
            var saved = await SimpleListExport.ExportAsync(sheetName, headers, rows);
            ViewModel.StatusMessage = saved ? "已导出渣土运输记录。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 「OCR识别」：打开非模态的离线 OCR 识别窗口，并订阅识别完成事件。
    /// 识别成功后把抽取字段填入表格首行的空白新增行，保持待保存状态由用户核对入库。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnOcrImportClick(object sender, RoutedEventArgs e)
    {
        if (_ocrWindow is null)
        {
            _ocrWindow = new OcrImportWindow();
            _ocrWindow.FillRequested += OnOcrFillRequested;
            _ocrWindow.Closed += (_, _) => _ocrWindow = null;
        }

        _ocrWindow.Activate();
    }

    /// <summary>OCR 窗口「填入表格」按钮点击回调：新建空白行并把抽取字段按名称映射填充进去。</summary>
    /// <param name="sender">OCR 识别窗口。</param>
    /// <param name="fields"><see cref="ReceiptFieldExtractor.Extract"/> 的抽取结果。</param>
    private void OnOcrFillRequested(object? sender, IReadOnlyList<ReceiptField> fields)
    {
        ViewModel.ApplyOcrFields(fields);
        FocusNewRow();
    }

    /// <summary>选中并滚动到未保存的空白行，尽量让其直接进入编辑状态。</summary>
    private void FocusNewRow()
    {
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
                System.Diagnostics.Debug.WriteLine($"[SlagTransportPage] 自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>弹出确认对话框，确认后删除指定行。</summary>
    /// <param name="row">待删除的记录。</param>
    private async System.Threading.Tasks.Task ConfirmAndDeleteAsync(SlagTransport row)
    {
        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除车牌「{row.PlateNumber}」（{row.EntryDate}）的渣土运输记录吗？该操作不可撤销。",
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
