using System;
using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MaterialManagementApp.Common;

/// <summary>
/// DataGrid 单元格编辑框统一样式辅助。
/// WinUI 隐式样式按精确类型匹配，而 DataGrid 的编辑元素是内部 new 出来的普通 TextBox，
/// 无法用隐式样式只命中表格单元格而不影响普通页面输入框，
/// 因此通过附加属性挂在 DataGrid 上，在 PreparingCellForEdit 时统一调整编辑元素：
/// 内边距收紧为 2、文字垂直居中、小圆角。
/// 柔和描边与无黑色焦点环由 DesignTokens.xaml 覆盖的系统键全局生效，这里无需重复处理。
/// </summary>
public static class DataGridCellEdit
{
    /// <summary>附加属性：为 DataGrid 开启单元格编辑框紧凑样式。</summary>
    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.RegisterAttached(
            "Compact",
            typeof(bool),
            typeof(DataGridCellEdit),
            new PropertyMetadata(false, OnCompactChanged));

    public static bool GetCompact(DataGrid obj)
    {
        return (bool)obj.GetValue(CompactProperty);
    }

    public static void SetCompact(DataGrid obj, bool value)
    {
        obj.SetValue(CompactProperty, value);
    }

    private static void OnCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            grid.PreparingCellForEdit += OnPreparingCellForEdit;
        }
        else
        {
            grid.PreparingCellForEdit -= OnPreparingCellForEdit;
        }
    }

    private static void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        ApplyCompact(e.EditingElement);
    }

    /// <summary>编辑元素是 TextBox 时直接调整；模板列根是容器时递归查找其中的 TextBox。</summary>
    private static void ApplyCompact(FrameworkElement? element)
    {
        if (element is null)
        {
            return;
        }

        if (element is TextBox box)
        {
            // 文字紧贴单元格左右，消除默认 8,4 的多余缩进留白；垂直居中且光标位置正常
            box.Padding = new Thickness(2);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            // 直角：与表格直角网格线一致（聚焦黑白焦点圈已由 DesignTokens 透明化）
            box.CornerRadius = new CornerRadius(0);
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
            {
                ApplyCompact(child);
            }
        }
    }
}
