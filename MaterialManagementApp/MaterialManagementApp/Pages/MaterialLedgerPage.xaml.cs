using System;
using System.Collections.Generic;
using System.Linq;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.Services;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI.Popups;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 台账明细页面（地材台账 → 台账明细）。
/// 上半部分为查询筛选面板，下半部分为数据列表与操作：
/// 「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后入库、「取消」直接移除；
/// 金额列为 数量 × 单价 的派生值，不手工录入。
/// </summary>
public sealed partial class MaterialLedgerPage : Page
{
    /// <summary>当前打开的非模态 OCR 识别窗口；同一时间只保留一个实例。</summary>
    private OcrImportWindow? _ocrWindow;

    /// <summary>当前打开的非模态批量扫描识别窗口；同一时间只保留一个实例。</summary>
    private OcrBatchImportWindow? _batchOcrWindow;

    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public MaterialLedgerViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public MaterialLedgerPage()
    {
        InitializeComponent();

        // ComboBox.SelectedItem 与可空 DatePicker.Date 都无法被 x:Bind 直接处理，
        // 这里设置 DataContext 让经典 Binding 在运行时完成类型转换
        DataContext = ViewModel;

        // 保存失败（校验不通过 / 数据库异常）时弹出模态警告弹窗，表单内容保留
        ViewModel.SaveFailed += OnViewModelSaveFailed;
    }

    /// <summary>
    /// 台账保存失败：弹出与全局一致的警告对话框，关闭后把焦点送回出错行
    /// 的「商品名称」单元格并直接进入编辑，用户无需重新找行点格。
    /// </summary>
    private async void OnViewModelSaveFailed(object? sender, EventArgs e)
    {
        var row = ViewModel.SelectedLedger ?? ViewModel.Ledgers.FirstOrDefault(x => x.IsNewRow);

        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "保存失败",
            Content = ViewModel.StatusMessage ?? "台账保存失败，请检查后重试！",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();

        // 对话框关闭后定位回出错行继续编辑（行可能已被用户改动，重新解析上下文）
        var target = ViewModel.Ledgers.FirstOrDefault(x => x.IsNewRow) ?? ViewModel.SelectedLedger ?? row;
        if (target is null)
        {
            return;
        }

        LedgerGrid.SelectedItem = target;
        // 商品名称列头是 StackPanel（含必填红星），不能按 Header 文本匹配，改按绑定路径定位
        LedgerGrid.CurrentColumn = LedgerGrid.Columns.FirstOrDefault(c =>
            c is CommunityToolkit.WinUI.UI.Controls.DataGridTextColumn textColumn &&
            string.Equals(textColumn.Binding?.Path?.Path, "MaterialName", StringComparison.Ordinal));
        LedgerGrid.ScrollIntoView(target, LedgerGrid.CurrentColumn);

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                LedgerGrid.BeginEdit();
            }
            catch (Exception ex)
            {
                // 行容器未就绪时 BeginEdit 失败不影响使用，用户可手工点格编辑
                System.Diagnostics.Debug.WriteLine($"[MaterialLedgerPage] 失败回焦编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>进入页面时按当前筛选条件加载台账数据。</summary>
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
        ViewModel.SelectedLedger = LedgerGrid.SelectedItem as MaterialLedger;
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

        var row = ViewModel.Ledgers.FirstOrDefault(x => x.IsNewRow);
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
                System.Diagnostics.Debug.WriteLine($"[MaterialLedgerPage] 自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>行内「保存」：新增行执行入库，已有行执行更新。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialLedger row })
        {
            ViewModel.SaveRow(row);
        }
    }

    /// <summary>行内「取消」：移除尚未入库的空白新增行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialLedger row })
        {
            ViewModel.CancelNewRow(row);
        }
    }

    /// <summary>行内「删除」：二次确认后从集合与数据库中移除该行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MaterialLedger row })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除物资「{row.MaterialName}」（{row.EntryDate}）的台账记录吗？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteRow(row);
        }
    }

    /// <summary>按模板导出地材台账：弹出保存对话框并生成 12 列的 Excel 文件。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 导出全部筛选结果而非当前页；尚未保存的空白行不入导出结果，避免把空记录写进 Excel
            var rows = ViewModel.GetFilteredRowsForExport().Where(x => !x.IsNewRow).ToList();
            var phase = ViewModel.SelectedPhase;
            string? suffix = string.IsNullOrEmpty(phase) ? null : $"-{phase}";
            var saved = await EarthLedgerTemplateExport.ExportAsync(rows, suffix);
            ViewModel.StatusMessage = saved ? "已按模板导出地材台账。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 「导入单据 / OCR 识别」：打开非模态的离线 OCR 识别窗口，并订阅「填入」事件。
    /// 用户在识别窗口点击「填入表格」后，抽取字段会填入到表格首行的空白新增行。
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

    /// <summary>OCR 窗口「填入表格」按钮点击回调：新建空白行、填入抽取字段、匹配合同单价后自动保存。</summary>
    /// <param name="sender">OCR 识别窗口。</param>
    /// <param name="fields"><see cref="ReceiptFieldExtractor.Extract"/> 的抽取结果。</param>
    private void OnOcrFillRequested(object? sender, IReadOnlyList<ReceiptField> fields)
    {
        ViewModel.ApplyOcrFields(fields);

        // OCR 字段填入并匹配合同单价后直接落库，无需用户再手动点击「保存」
        var row = ViewModel.Ledgers.FirstOrDefault(x => x.IsNewRow);
        if (row is not null)
        {
            ViewModel.SaveRow(row);
        }
    }

    /// <summary>
    /// 「批量扫描识别」：打开非模态的批量 OCR 窗口，一次识别多张地材单图片。
    /// 每张卡片可手动修正后单独保存，保存时图片自动复制进数据目录并关联台账记录。
    /// </summary>
    private void OnBatchOcrImportClick(object sender, RoutedEventArgs e)
    {
        if (_batchOcrWindow is null)
        {
            _batchOcrWindow = new OcrBatchImportWindow();

            // 窗口关闭后刷新列表：批量保存的记录与图片直接落库，需要重新加载才能看到
            _batchOcrWindow.Closed += (_, _) =>
            {
                _batchOcrWindow = null;
                ViewModel.Load();
            };
        }

        _batchOcrWindow.Activate();
    }

    /// <summary>「地材单图片」列的「查看」按钮：用系统默认看图工具打开该记录关联的扫描图片。</summary>
    private void OnViewLedgerImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MaterialLedger row }
            || string.IsNullOrWhiteSpace(row.LedgerImagePath))
        {
            return;
        }

        if (!System.IO.File.Exists(row.LedgerImagePath))
        {
            ViewModel.StatusMessage = $"图片文件不存在：{row.LedgerImagePath}";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.LedgerImagePath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"打开图片失败：{ex.Message}";
        }
    }
}
