using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.UI.Controls;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.Services;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI.Popups;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 台账明细页面（地材台账 → 台账明细）。
/// 上半部分为查询筛选面板，下半部分为数据列表与操作：
/// 点击表格空白处即插入草稿行（浅绿底标记，可连续录入多条），工具栏「保存」批量校验入库；
/// 已入库的行离开编辑即自动保存（Excel 式），行末仅保留「删除」；
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

    /// <summary>
    /// 「点空白即新增」的命令：由 DataGridQuickAdd 附加行为在点击表格空白区时触发，
    /// 也可供后续入口（如快捷键）复用。
    /// </summary>
    public ICommand CreateDraftRowCommand { get; }

    /// <summary>初始化页面。</summary>
    public MaterialLedgerPage()
    {
        InitializeComponent();

        // ComboBox.SelectedItem 与可空 DatePicker.Date 都无法被 x:Bind 直接处理，
        // 这里设置 DataContext 让经典 Binding 在运行时完成类型转换
        DataContext = ViewModel;

        // 保存失败（校验不通过 / 数据库异常）时弹出模态警告弹窗，表单内容保留
        ViewModel.SaveFailed += OnViewModelSaveFailed;

        // 删除成功后右下角弹「已删除 [撤销]」Toast（软删除可撤销）
        ViewModel.Deleted += OnViewModelDeleted;

        // 翻页 / 查询 / 刷新 / 重置前存在草稿行时，由页面弹出「离开将丢弃」确认弹窗
        ViewModel.ConfirmDiscardDraftsAsync = ConfirmDiscardDraftsAsync;

        // 开启「点空白即新增」：行为组件做空白命中测试，命中后执行本页创建草稿行的命令
        CreateDraftRowCommand = new RelayCommand(CreateDraftRowAndBeginEdit);
        DataGridQuickAdd.SetIsEnabled(LedgerGrid, true);
        DataGridQuickAdd.SetBlankClickCommand(LedgerGrid, CreateDraftRowCommand);
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
        // 商品名称列头是 StackPanel（含必填红星），不能按 Header 文本匹配，改按绑定路径定位；
        // 表格没有当前行时 set_CurrentColumn 会抛 InvalidOperationException 曾致应用退出，必须防护
        try
        {
            LedgerGrid.CurrentColumn = LedgerGrid.Columns.FirstOrDefault(c =>
                c is DataGridTextColumn textColumn &&
                string.Equals(textColumn.Binding?.Path?.Path, "MaterialName", StringComparison.Ordinal));
        }
        catch (InvalidOperationException ex)
        {
            // 无当前行时定位失败不影响弹窗流程，用户可手工点格编辑
            System.Diagnostics.Debug.WriteLine($"[MaterialLedgerPage] 定位商品名称列失败：{ex.Message}");
        }
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

    /// <summary>
    /// 离开确认弹窗：表格中存在未保存的草稿行时，翻页 / 查询 / 刷新 / 重置都会先弹此窗；
    /// 返回 true 表示用户确认丢弃草稿并继续。
    /// </summary>
    /// <returns>确认离开返回 true。</returns>
    private async Task<bool> ConfirmDiscardDraftsAsync()
    {
        var count = ViewModel.Ledgers.Count(x => x.IsNewRow);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "有未保存的新增行",
            Content = $"表格中有 {count} 行尚未保存的新增记录（浅绿底），离开后这些行将被丢弃。确定要离开吗？",
            PrimaryButtonText = "丢弃并离开",
            CloseButtonText = "留下继续编辑",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>进入页面时按当前筛选条件加载台账数据。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DeptPhaseService.Instance.Refresh();
        DeptPhaseFilter.RefreshOptions();
        ViewModel.Load();
    }

    /// <summary>表格选中行变化时同步到视图模型，保留选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选中项变化参数。</param>
    private void OnLedgerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedLedger = LedgerGrid.SelectedItem as MaterialLedger;
    }

    /// <summary>
    /// 「点空白即新增」入口：在表格首行插入草稿行，并让该行立即进入编辑状态，
    /// 用户无需任何按钮即可开始录入。
    /// </summary>
    private void CreateDraftRowAndBeginEdit()
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

        // 新行的单元格容器要等本次布局完成后才生成，BeginEdit 必须延后一帧再调用；
        // 点空白新增时焦点仍停留在窗口级（没有任何单元格被点击），BeginEdit 前必须先让表格拿到焦点
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!LedgerGrid.Focus(FocusState.Pointer))
                {
                    System.Diagnostics.Debug.WriteLine("[MaterialLedgerPage] 表格获取焦点失败，保持选中态。");
                }

                LedgerGrid.BeginEdit();
            }
            catch (Exception ex)
            {
                // 尚未生成行容器时 BeginEdit 会失败，此时保持选中态即可，不影响手工点击编辑
                System.Diagnostics.Debug.WriteLine($"[MaterialLedgerPage] 自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 行编辑结束：已入库的行在提交时自动落库（Excel 式体验，无需行末保存按钮）；
    /// 草稿行不自动入库（可能尚未填完，且名称为空会误弹失败窗），交给工具栏「保存」批量处理，
    /// 并在下一帧把整表按入库日期倒序归位。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">行编辑结束参数。</param>
    private void OnLedgerRowEditEnded(object sender, DataGridRowEditEndedEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.DataContext is not MaterialLedger row)
        {
            return;
        }

        if (row.IsNewRow)
        {
            // 草稿行：日期可能刚被改过，延迟一帧重排，避免在行编辑事件内直接搬动集合
            DispatcherQueue.TryEnqueue(ViewModel.Resort);
        }
        else
        {
            // 已入库行：延迟一帧保存。SaveRow 对已有行是原地更新（不重建集合），
            // 延迟不会丢草稿；同步执行则会在 RowEditEnded 事件内触碰集合引发重入异常
            DispatcherQueue.TryEnqueue(() => ViewModel.SaveRow(row));
        }
    }

    /// <summary>行加载时给未保存的草稿行铺一层浅绿底色，与已入库行区分；行容器会复用，非草稿行必须清掉旧底色。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">行加载参数。</param>
    private void OnLedgerLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Background = e.Row.DataContext is MaterialLedger { IsNewRow: true }
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0x10, 0x8A, 0x43))
            : null;
    }

    /// <summary>行内「删除」：未入库的草稿行直接移除；已入库行二次确认后从集合与数据库中删除。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MaterialLedger row })
        {
            return;
        }

        // 草稿行没有历史数据，直接移除不打扰确认
        if (row.IsNewRow)
        {
            ViewModel.DeleteRow(row);
            return;
        }

        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除物资「{row.MaterialName}」（{row.EntryDate}）的台账记录吗？删除后可在右下角提示中撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteRow(row);
        }
    }

    /// <summary>勾选列点击：把行的勾选状态同步给视图模型（勾选 Id 集合 + 批量按钮显隐）。</summary>
    /// <param name="sender">被点击的复选框（DataContext 为该行）。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnRowCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaterialLedger row })
        {
            ViewModel.SyncCheckedRow(row);
        }
    }

    /// <summary>「批量修改」弹窗：选择目标字段（项目部/期数/经办人），填入统一的新值后一键改写所有勾选行。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnBatchUpdateClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CheckedIds.Count == 0)
        {
            ViewModel.StatusMessage = "请先勾选要修改的行。";
            return;
        }

        var fieldBox = new ComboBox
        {
            Header = "修改字段",
            MinWidth = 280,
            ItemsSource = new List<string> { "项目部", "期数", "经办人" },
            SelectedIndex = 0
        };
        var deptBox = new MaterialManagementApp.Controls.DeptPicker { MinWidth = 280 };
        var phaseBox = new MaterialManagementApp.Controls.PhasePicker { MinWidth = 280, Visibility = Visibility.Collapsed };
        var valueText = new TextBox
        {
            Header = "新值（留空表示清空该字段）",
            MaxLength = 50,
            Visibility = Visibility.Collapsed
        };

        // 项目部/期数用系统中维护的维度选择器，避免手输错别字；经办人自由输入
        fieldBox.SelectionChanged += (_, _) =>
        {
            deptBox.Visibility = fieldBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            phaseBox.Visibility = fieldBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            valueText.Visibility = fieldBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(fieldBox);
        panel.Children.Add(deptBox);
        panel.Children.Add(phaseBox);
        panel.Children.Add(valueText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"批量修改 {ViewModel.CheckedIds.Count} 条记录",
            Content = panel,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var (field, value) = fieldBox.SelectedIndex switch
        {
            0 => ("ProjectDept", deptBox.Value ?? string.Empty),
            1 => ("ProjectPhase", phaseBox.Value ?? string.Empty),
            _ => ("Handler", valueText.Text?.Trim() ?? string.Empty)
        };

        var affected = ViewModel.UpdateChecked(field, value);
        ViewModel.StatusMessage = $"批量修改完成，更新 {affected} 条记录。";
    }

    /// <summary>「删除选中」：二次确认后批量软删除勾选行，删除后弹撤销 Toast。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteCheckedClick(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.CheckedIds.Count;
        if (count == 0)
        {
            ViewModel.StatusMessage = "请先勾选要删除的行。";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除勾选的 {count} 条台账记录吗？删除后可在右下角提示中一次性撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteCheckedRows();
        }
    }

    /// <summary>Toast「撤销」按钮：恢复最近一次删除并关闭提示。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        UndoToast.IsOpen = false;
        ViewModel.UndoLastDelete();
    }

    /// <summary>Toast 关闭（手动关/自动超时）：停掉计时器。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnUndoToastClosed(InfoBar sender, object e)
    {
        _undoToastTimer?.Stop();
    }

    /// <summary>收到删除事件：右下角弹「已删除 N 条记录 [撤销]」，8 秒无操作自动消失。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="ids">本次删除的记录主键。</param>
    private void OnViewModelDeleted(object? sender, IReadOnlyList<int> ids)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            UndoToast.Message = $"已删除 {ids.Count} 条记录";
            UndoToast.IsOpen = true;
            _undoToastTimer ??= CreateUndoToastTimer();
            _undoToastTimer.Stop();
            _undoToastTimer.Start();
        });
    }

    /// <summary>创建撤销 Toast 的 8 秒自动关闭计时器（创建一次重复使用）。</summary>
    /// <returns>定时器实例。</returns>
    private DispatcherQueueTimer CreateUndoToastTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(8);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => UndoToast.IsOpen = false;
        return timer;
    }

    /// <summary>右键表格弹出「显示/隐藏列」菜单：按当前列状态动态重建菜单项。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnColumnMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu)
        {
            return;
        }

        menu.Items.Clear();

        // 菜单标题：禁用态提示项，不可点击
        var title = new MenuFlyoutItem
        {
            Text = "显示 / 隐藏列（点击切换）",
            IsEnabled = false
        };
        menu.Items.Add(title);
        menu.Items.Add(new MenuFlyoutSeparator());

        foreach (var column in LedgerGrid.Columns)
        {
            // 勾选列无表头文字，不在配置菜单中出现
            if (string.IsNullOrEmpty(column.Header as string))
            {
                continue;
            }

            var item = new ToggleMenuFlyoutItem
            {
                Text = column.Header.ToString(),
                IsChecked = column.Visibility == Visibility.Visible
            };
            var captured = column;
            item.Click += (_, _) =>
            {
                // 至少保留一列可见，避免整表被藏起来后看起来像空白故障
                if (!item.IsChecked && LedgerGrid.Columns.Count(
                        c => c.Visibility == Visibility.Visible && !string.IsNullOrEmpty(c.Header as string)) <= 1)
                {
                    item.IsChecked = true;
                    ViewModel.StatusMessage = "至少需要保留一列。";
                    return;
                }

                captured.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            };
            menu.Items.Add(item);
        }
    }

    /// <summary>撤销 Toast 的自动关闭计时器（首次弹 Toast 时懒创建，重复使用）。</summary>
    private DispatcherQueueTimer? _undoToastTimer;

    /// <summary>商品名称联想防抖句柄：每次输入重置，300ms 后查询历史名称。</summary>
    private System.Threading.CancellationTokenSource? _materialSuggestCts;

    /// <summary>经办人联想防抖句柄：每次输入重置，300ms 后查询历史姓名。</summary>
    private System.Threading.CancellationTokenSource? _handlerSuggestCts;

    /// <summary>商品名称单元格输入变化：300ms 防抖后按前缀查历史名称给下拉联想。</summary>
    /// <param name="sender">正在编辑的 AutoSuggestBox。</param>
    /// <param name="args">文本变化参数。</param>
    private void OnMaterialNameSuggestText(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        RunDebouncedSuggest(sender, sender.Text,
            prefix => DatabaseService.Instance.GetMaterialNameSuggestions(prefix),
            () => _materialSuggestCts,
            value => _materialSuggestCts = value);
    }

    /// <summary>经办人单元格输入变化：300ms 防抖后按前缀查历史姓名给下拉联想。</summary>
    /// <param name="sender">正在编辑的 AutoSuggestBox。</param>
    /// <param name="args">文本变化参数。</param>
    private void OnHandlerSuggestText(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        RunDebouncedSuggest(sender, sender.Text,
            prefix => DatabaseService.Instance.GetHandlerSuggestions(prefix),
            () => _handlerSuggestCts,
            value => _handlerSuggestCts = value);
    }

    /// <summary>联想查询的统一防抖入口：取消上一次未完成的查询，300ms 后在后台线程查库再回 UI 线程。</summary>
    /// <param name="box">展示候选的 AutoSuggestBox。</param>
    /// <param name="text">当前输入文本。</param>
    /// <param name="query">查询委托：前缀 → 候选列表（在后台线程执行）。</param>
    /// <param name="getCurrent">读取该输入框当前防抖令牌。</param>
    /// <param name="setCurrent">写入新的防抖令牌。</param>
    private static async void RunDebouncedSuggest(
        AutoSuggestBox box,
        string text,
        Func<string, List<string>> query,
        Func<System.Threading.CancellationTokenSource?> getCurrent,
        Action<System.Threading.CancellationTokenSource?> setCurrent)
    {
        getCurrent()?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        setCurrent(cts);
        var token = cts.Token;

        try
        {
            await System.Threading.Tasks.Task.Delay(300, token);
            var dispatcher = box.DispatcherQueue;
            var suggestions = await System.Threading.Tasks.Task.Run(() => query(text?.Trim() ?? string.Empty), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            dispatcher.TryEnqueue(() =>
            {
                // 编辑已结束或控件已换绑时不再回填
                if (!token.IsCancellationRequested)
                {
                    box.ItemsSource = suggestions;
                }
            });
        }
        catch (System.Threading.Tasks.TaskCanceledException)
        {
            // 防抖被新输入打断，属于正常路径，无需处理
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
                // 退订静态进度事件并收起悬浮进度卡片，避免持有已销毁窗口的引用
                OcrBatchImportWindow.BatchProgressChanged -= OnBatchProgressChanged;
                _ = DispatcherQueue.TryEnqueue(() => ScanProgressCard.IsOpen = false);
                ViewModel.Load();
            };

            // 订阅识别进度：台账页右下角悬浮卡片实时显示「正在解析第 N/M 张」
            OcrBatchImportWindow.BatchProgressChanged += OnBatchProgressChanged;
        }

        _batchOcrWindow.Activate();
    }

    /// <summary>批量识别进度回调：刷新悬浮进度卡片文案；完成后提示可进入批量核对。</summary>
    /// <param name="sender">事件源（批量扫描窗口）。</param>
    /// <param name="e">进度快照（第 N 张 / 共 M 张 / 是否完成）。</param>
    private void OnBatchProgressChanged(object? sender, OcrBatchImportWindow.OcrBatchProgress e)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ScanProgressCard.Message = e.Finished
                ? $"批量识别完成（共 {e.Total} 张），点击「打开批量核对」核对并保存。"
                : $"正在解析第 {e.Current}/{e.Total} 张图片...";
            ScanProgressCard.Severity = e.Finished ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
            ScanProgressCard.IsOpen = true;
        });
    }

    /// <summary>进度卡片「打开批量核对」：把批量扫描窗口激活置前，直接进入核对界面。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnOpenBatchReviewClick(object sender, RoutedEventArgs e)
    {
        ScanProgressCard.IsOpen = false;
        _batchOcrWindow?.Activate();
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
