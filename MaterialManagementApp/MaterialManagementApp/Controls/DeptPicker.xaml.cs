using System.Collections.Generic;
using System.Linq;
using MaterialManagementApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaterialManagementApp.Controls;

/// <summary>
/// 录入用项目部下拉框。候选项来自 DeptPhaseService 缓存（设置页可随时维护），
/// 用在 DataGrid 编辑单元格等无法直接绑定外部列表的场景。
/// 记录里已保存的项目部即使后来被设置页删除，也会临时并入候选项以正确显示。
/// </summary>
public sealed partial class DeptPicker : UserControl
{
    /// <summary>同步选中态期间置位，避免 SelectionChanged 回写 Value 造成循环。</summary>
    private bool _syncing;

    /// <summary>初始化控件。</summary>
    public DeptPicker()
    {
        InitializeComponent();
    }

    /// <summary>当前选中的项目部名称；空串表示未选择。</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(DeptPicker), new PropertyMetadata(string.Empty, OnValueChanged));

    /// <summary>当前选中的项目部名称；空串表示未选择。</summary>
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
        if (d is DeptPicker picker)
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

    /// <summary>重新读取项目部候选项；当前值不在候选项中时临时并入，保证已存数据可见。</summary>
    public void RefreshOptions()
    {
        var options = DeptPhaseService.Instance.GetDeptNames().ToList();
        var current = Value;
        if (!string.IsNullOrEmpty(current) && !options.Contains(current))
        {
            options.Add(current);
        }

        InnerBox.ItemsSource = options;
    }

    /// <summary>按 Value 更新下拉框选中项；候选项缺失时先刷新再选中。</summary>
    private void SyncSelection()
    {
        _syncing = true;
        try
        {
            var value = Value;
            if (string.IsNullOrEmpty(value))
            {
                InnerBox.SelectedItem = null;
                return;
            }

            if (InnerBox.ItemsSource is not IList<string> items || !items.Contains(value))
            {
                RefreshOptions();
            }

            InnerBox.SelectedItem = value;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>用户改变选中项时回写 Value。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选择变化参数。</param>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        Value = InnerBox.SelectedItem as string ?? string.Empty;
    }
}
