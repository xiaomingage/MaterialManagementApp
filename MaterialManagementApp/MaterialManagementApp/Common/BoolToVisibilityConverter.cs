using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MaterialManagementApp.Common;

/// <summary>
/// bool 到 Visibility 的转换器。默认 true → Visible、false → Collapsed；
/// 传 ConverterParameter="Invert" 时反转，用于让同一单元格模板里的两组按钮互斥显示
/// （新增行显示「保存 / 取消」，已有行显示「保存 / 删除」）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>反转结果的参数值。</summary>
    private const string InvertParameter = "Invert";

    /// <summary>把布尔值转换为 Visibility。</summary>
    /// <param name="value">绑定源，期望为 bool。</param>
    /// <param name="targetType">目标类型，此处为 Visibility。</param>
    /// <param name="parameter">可选参数，等于 Invert 时反转结果。</param>
    /// <param name="language">语言标识，未使用。</param>
    /// <returns>Visibility.Visible 或 Visibility.Collapsed。</returns>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is bool flag && flag;

        if (parameter is string text && string.Equals(text, InvertParameter, StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>不支持反向转换，本转换器只用于单向显示。</summary>
    /// <param name="value">目标值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">参数。</param>
    /// <param name="language">语言标识。</param>
    /// <returns>始终抛出异常。</returns>
    /// <exception cref="NotSupportedException">任何调用都会抛出。</exception>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException("BoolToVisibilityConverter 仅支持单向转换。");
    }
}

/// <summary>
/// 数量到 Visibility 的转换器：0（或 null）→ Visible、非 0 → Collapsed，
/// 用于表格/列表的空态覆盖层（记录数为 0 时显示引导提示）。
/// 传 ConverterParameter="Invert" 时反转（0 → Collapsed）。
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    private const string InvertParameter = "Invert";

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        long count = 0;
        if (value is int i)
        {
            count = i;
        }
        else if (value is long l)
        {
            count = l;
        }
        else if (value is System.Collections.ICollection collection)
        {
            count = collection.Count;
        }

        var empty = count == 0;
        if (parameter is string text && string.Equals(text, InvertParameter, StringComparison.OrdinalIgnoreCase))
        {
            empty = !empty;
        }

        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException("CountToVisibilityConverter 仅支持单向转换。");
    }
}

/// <summary>
/// 字符串到 Visibility 的转换器：空白（null/空/全空格）→ Collapsed、非空白 → Visible，
/// 用于状态提示条（有消息时才显示容器）。
/// 传 ConverterParameter="Invert" 时反转。
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    private const string InvertParameter = "Invert";

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasText = value is string text && !string.IsNullOrWhiteSpace(text);
        if (parameter is string p && string.Equals(p, InvertParameter, StringComparison.OrdinalIgnoreCase))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException("StringToVisibilityConverter 仅支持单向转换。");
    }
}
