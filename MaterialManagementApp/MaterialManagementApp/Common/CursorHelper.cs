using System;
using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace MaterialManagementApp.Common;

/// <summary>
/// 为任意 UIElement 悬浮时显示手型光标。WinUI 3 的 ProtectedCursor 是 protected 属性，
/// 只能反射设置；指针离开时恢复系统默认箭头。
/// 用法（XAML）：xmlns:ui="using:MaterialManagementApp.Common" 后 ui:CursorHelper.IsHandCursor="True"。
/// </summary>
public static class CursorHelper
{
    /// <summary>附加属性：是否在悬浮时把手型光标应用到目标元素。</summary>
    public static readonly DependencyProperty IsHandCursorProperty =
        DependencyProperty.RegisterAttached(
            "IsHandCursor", typeof(bool), typeof(CursorHelper), new PropertyMetadata(false, OnIsHandCursorChanged));

    public static void SetIsHandCursor(UIElement element, bool value) =>
        element.SetValue(IsHandCursorProperty, value);

    public static bool GetIsHandCursor(UIElement element) =>
        (bool)element.GetValue(IsHandCursorProperty);

    private static readonly InputCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private static void OnIsHandCursorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && (bool)e.NewValue)
        {
            element.PointerEntered += OnPointerEntered;
            element.PointerExited += OnPointerExited;
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetCursor(sender, HandCursor);

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e) =>
        SetCursor(sender, null);

    /// <summary>反射写入 ProtectedCursor（protected 属性，无法直接访问）；失败仅影响光标观感，静默忽略。</summary>
    private static void SetCursor(object sender, InputCursor? cursor)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        try
        {
            typeof(UIElement)
                .GetProperty(
                    "ProtectedCursor",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.SetValue(element, cursor);
        }
        catch
        {
            // 光标是锦上添花，任何环境差异都不应影响功能
        }
    }
}
