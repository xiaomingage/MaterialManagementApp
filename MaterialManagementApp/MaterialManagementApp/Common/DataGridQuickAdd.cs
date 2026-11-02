using System;
using System.Windows.Input;
using CommunityToolkit.WinUI.UI.Controls;
using CommunityToolkit.WinUI.UI.Controls.Primitives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MaterialManagementApp.Common;

/// <summary>
/// DataGrid「点空白即新增」附加行为：用户点击表格数据区最后一行以下的空白处时，
/// 触发绑定的命令（由页面创建草稿行并进入编辑），不再需要先点工具栏「新增」按钮。
/// 适用于所有「表格即表单」式台账页，配合页面的行末「删除」与工具栏「保存」使用。
/// 空白判定用命中测试：点击位置找不到任何 DataGridRow，且不在滚动条或列头上，即视为空白。
/// 前提：DataGrid 的 Background 不能为 null（null 区域不可命中测试，事件不会经过表格），
/// 页面须显式设置 Background="Transparent"。
/// </summary>
public static class DataGridQuickAdd
{
    /// <summary>附加属性：是否启用空白点击新增。</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(DataGridQuickAdd),
            new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>附加属性：命中空白区时执行的命令；页面据此创建草稿行并 BeginEdit。</summary>
    public static readonly DependencyProperty BlankClickCommandProperty =
        DependencyProperty.RegisterAttached(
            "BlankClickCommand",
            typeof(ICommand),
            typeof(DataGridQuickAdd),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DataGrid obj)
    {
        return (bool)obj.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DataGrid obj, bool value)
    {
        obj.SetValue(IsEnabledProperty, value);
    }

    public static ICommand? GetBlankClickCommand(DataGrid obj)
    {
        return (ICommand?)obj.GetValue(BlankClickCommandProperty);
    }

    public static void SetBlankClickCommand(DataGrid obj, ICommand? value)
    {
        obj.SetValue(BlankClickCommandProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            // 必须用 handledEventsToo=true 订阅：DataGrid 内部会把指针按下标记为已处理
            // （选中、滚动等），普通 CLR 事件订阅（handledEventsToo=false）会被整体跳过，
            // 导致空白点击永远收不到通知；拿到事件后再自行用命中测试排除行内点击
            grid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnGridPointerPressed), true);
        }
        else
        {
            grid.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnGridPointerPressed));
        }
    }

    private static void OnGridPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        var command = GetBlankClickCommand(grid);
        if (command is null || !command.CanExecute(null))
        {
            return;
        }

        var properties = e.GetCurrentPoint(grid).Properties;
        if (!properties.IsLeftButtonPressed || properties.IsRightButtonPressed)
        {
            return;
        }

        // FindElementsInHostCoordinates 的 Point 参数使用根视觉坐标系（相对窗口内容根），
        // 而事件坐标是相对 grid 的；直接传入会被解释到错误位置，导致行内点击也命中 0 个元素
        // 而被误判为空白。必须先变换到根坐标系再做命中测试。
        var pointInGrid = e.GetCurrentPoint(grid).Position;
        var point = grid.TransformToVisual(null).TransformPoint(pointInGrid);
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(point, grid);

        // 命中数据行：属于正常行交互（选中/编辑/行内按钮），不触发新增
        foreach (var hit in hits)
        {
            if (hit is DataGridRow)
            {
                return;
            }
        }

        // 命中滚动条或列头：不是空白区，不触发新增
        foreach (var hit in hits)
        {
            if (HasAncestor<ScrollBar>(hit, grid) || HasAncestor<DataGridColumnHeader>(hit, grid))
            {
                return;
            }
        }

        // 点击空白前先提交进行中的单元格编辑：已有行借此触发 Excel 式自动保存，
        // 草稿行保持草稿状态；随后再执行新增命令，保证新增行进入重载后的最新集合。
        // 无编辑状态时 CommitEdit 无副作用，异常也仅意味着当前无可提交内容
        try
        {
            grid.CommitEdit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataGridQuickAdd] 提交编辑失败：{ex.Message}");
        }

        command.Execute(null);
    }

    /// <summary>判断元素向上到 subtree 根之间是否存在指定类型的祖先节点。</summary>
    private static bool HasAncestor<T>(DependencyObject start, DependencyObject bound)
        where T : class
    {
        var current = start;
        while (current is not null && !ReferenceEquals(current, bound))
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
