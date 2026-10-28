using System;
using System.Collections.Generic;
using System.Linq;
using MaterialManagementApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaterialManagementApp.Controls;

/// <summary>
/// 录入用期数下拉框。首项固定为空白选项「无」（选中存空串，表示该记录没有期数，如梧州项目），
/// 其后为 DeptPhaseService 缓存中的全部期数；期数由用户在设置页自由维护，不与项目部绑定。
/// 记录里已保存的期数即使后来被设置页删除，也会临时并入候选项以正确显示。
/// </summary>
public sealed partial class PhasePicker : UserControl
{
    /// <summary>同步选中态期间置位，避免 SelectionChanged 回写 Value 造成循环。</summary>
    private bool _syncing;

    /// <summary>初始化控件。</summary>
    public PhasePicker()
    {
        InitializeComponent();
    }

    /// <summary>当前选中的期数名称；空串表示无期数。</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(PhasePicker), new PropertyMetadata(string.Empty, OnValueChanged));

    /// <summary>当前选中的期数名称；空串表示无期数。</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value ?? string.Empty);
    }

    /// <summary>Value 变化时把选中态同步到下拉框。</summary>
    /// <param name="d">属性所属对象。</param>
    /// <param name="e">变更参数。</param>
    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PhasePicker picker)
        {
            picker.SyncSelection();
        }
    }

    /// <summary>控件载入时刷新候选项并同步选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshOptions();
        SyncSelection();
    }

    /// <summary>重新读取期数候选项；当前值不在候选项中时临时并入，保证已存数据可见。</summary>
    public void RefreshOptions()
    {
        var options = DeptPhaseService.Instance.GetPhaseEntryOptions().ToList();
        var current = Value;
        if (!string.IsNullOrEmpty(current) && !options.Contains(current))
        {
            options.Add(current);
        }

        InnerBox.ItemsSource = options;
    }

    /// <summary>按 Value 更新下拉框选中项；空串映射到首项「无」。</summary>
    private void SyncSelection()
    {
        _syncing = true;
        try
        {
            var display = string.IsNullOrEmpty(Value)
                ? DeptPhaseService.EntryBlankPhaseText
                : Value;

            if (InnerBox.ItemsSource is not IList<string> items || !items.Contains(display))
            {
                RefreshOptions();
            }

            InnerBox.SelectedItem = display;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>用户改变选中项时回写 Value；选中「无」时存空串。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选择变化参数。</param>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var selected = InnerBox.SelectedItem as string;
        Value = string.IsNullOrEmpty(selected)
            || string.Equals(selected, DeptPhaseService.EntryBlankPhaseText, StringComparison.Ordinal)
                ? string.Empty
                : selected;
    }
}
