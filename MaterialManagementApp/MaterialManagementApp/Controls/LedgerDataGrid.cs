using System;
using System.Collections;
using CommunityToolkit.WinUI.UI.Controls;
using MaterialManagementApp.Common;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaterialManagementApp.Controls;

/// <summary>
/// 全项目统一的台账表格控件：内置与「地材台账明细」一致的交互基线，
/// 所有模块（供应商、渣土运输、机具、领用/退库等）一律使用本控件，保证风格与行为 100% 统一。
///
/// 相比原生 DataGrid 的增强：
/// 1. 统一默认外观：全边框、关闭自动生成列（各页面列定义保持显式声明）；
/// 2. 空白行防呆：编辑结束（RowEditEnding）时若该行所有用户可填字段均为空
///   （日期、序号、Id 等自动填充值不算，见 <see cref="BlankRowGuard"/>），
///    自动从绑定的集合中移除该行，防止产生空白脏数据；
/// 3. 编辑竞态防护：提供 <see cref="CancelPendingEdit"/>，页面在「新增/删除/重载」
///    等修改集合的操作前先取消尚未提交的单元格编辑，避免 DataGrid
///    在编辑状态下被外部改动集合而崩溃（供应商合同明细闪退类问题）；
/// 4. <see cref="BeginEditWhenReady"/>：新行插入后延后一帧进入编辑状态，
///    统一封装「等行容器生成后再 BeginEdit」的时序处理。
/// </summary>
public class LedgerDataGrid : DataGrid
{
    /// <summary>是否启用空白行防呆（默认开启；纯展示型表格可关闭）。</summary>
    public static readonly DependencyProperty BlankRowGuardEnabledProperty =
        DependencyProperty.Register(
            nameof(BlankRowGuardEnabled), typeof(bool), typeof(LedgerDataGrid),
            new PropertyMetadata(true));

    /// <summary>是否启用空白行防呆。</summary>
    public bool BlankRowGuardEnabled
    {
        get => (bool)GetValue(BlankRowGuardEnabledProperty);
        set => SetValue(BlankRowGuardEnabledProperty, value);
    }

    /// <summary>一行空白脏数据被自动移除时触发，参数为被移除的行对象；页面可据此刷新状态提示。</summary>
    public event EventHandler<object>? BlankRowRemoved;

    /// <summary>待移除的空白行标记：RowEditEnding 与延迟移除之间的重入保护。</summary>
    private readonly System.Collections.Generic.HashSet<object> _pendingRemovals = new();

    /// <summary>初始化统一默认样式与事件。</summary>
    public LedgerDataGrid()
    {
        GridLinesVisibility = DataGridGridLinesVisibility.All;
        AutoGenerateColumns = false;
        RowEditEnding += OnRowEditEndingGuard;
    }

    /// <summary>
    /// 编辑行结束（提交或取消）时执行空白校验：空白行延迟一帧从集合移除，
    /// 不能在事件内直接改集合（DataGrid 正在结束编辑管线，同步改集合会引发竞态崩溃）。
    ///
    /// 安全边界：只对「新增行」（模型含 IsNewRow=true）生效——已入库的行即使被清空，
    /// 也不在 UI 层悄悄移除，避免用户误清空导致「看起来删了、库里还在」的困惑。
    /// </summary>
    private void OnRowEditEndingGuard(object? sender, DataGridRowEditEndingEventArgs e)
    {
        if (!BlankRowGuardEnabled)
        {
            return;
        }

        // CommunityToolkit 的 DataGridRow 没有 WPF 的 Item 属性；行内模板绑定依赖 DataContext，其值即数据项
        var row = e.Row?.DataContext;
        if (row is null)
        {
            return;
        }
        if (!_pendingRemovals.Add(row) ||
            !BlankRowGuard.IsRowBlank(row) || !IsNewRowItem(row))
        {
            _pendingRemovals.Remove(row);
            return;
        }

        // 编辑提交后再移除：延后到下一帧，等 DataGrid 结束编辑管线
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                CancelPendingEdit();
                if (ItemsSource is IList list && BlankRowGuard.TryRemove(list, row))
                {
                    BlankRowRemoved?.Invoke(this, row);
                }
            }
            finally
            {
                _pendingRemovals.Remove(row);
            }
        });
    }

    /// <summary>判断行对象是否为「新增未入库」的行：模型含 IsNewRow 且为 true 才算。</summary>
    private static bool IsNewRowItem(object row)
    {
        var prop = row.GetType().GetProperty("IsNewRow");
        return prop?.GetValue(row) is true;
    }

    /// <summary>
    /// 取消尚未提交的单元格/行编辑。页面在「新增、删除、重载数据」等
    /// 会改动绑定集合的操作前必须先调用本方法，防止编辑态集合竞态崩溃。
    /// </summary>
    public void CancelPendingEdit()
    {
        try
        {
            // 同时按行与单元格粒度取消，保证任何编辑阶段都能安全退出
            CancelEdit(DataGridEditingUnit.Row);
            CancelEdit(DataGridEditingUnit.Cell);
        }
        catch
        {
            // 无正在进行的编辑时 CancelEdit 会抛异常，属于正常情况，忽略
        }
    }

    /// <summary>
    /// 让表格进入编辑状态：等待一帧（新行容器生成后）再 BeginEdit，
    /// 失败时保持选中态由用户手工点击编辑，绝不抛异常打断操作。
    /// </summary>
    public void BeginEditWhenReady()
    {
        var queue = DispatcherQueue;
        if (queue is null)
        {
            return;
        }
        queue.TryEnqueue(() =>
        {
            try
            {
                BeginEdit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LedgerDataGrid] 自动进入编辑失败：{ex.Message}");
            }
        });
    }
}
