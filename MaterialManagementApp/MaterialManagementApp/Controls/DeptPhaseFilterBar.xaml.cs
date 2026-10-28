using System;
using System.Linq;
using MaterialManagementApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaterialManagementApp.Controls;

/// <summary>
/// 筛选用「项目部 + 期数」双下拉条，嵌入各表格页查询面板复用。
/// 项目部候选项：「全部」+ 设置页维护的全部项目部；
/// 期数候选项：「全部」+「无期数」（仅看无分期记录）+ 设置页维护的全部期数。
/// SelectedDept 为 null 表示不限项目部；SelectedPhase 为 null 表示不限期数、空串表示仅无期数。
/// 页面导航进入时调用 <see cref="RefreshOptions"/> 以拿到设置页的最新维护结果。
/// </summary>
public sealed partial class DeptPhaseFilterBar : UserControl
{
    /// <summary>同步选中态期间置位，避免 SelectionChanged 回写依赖属性造成循环。</summary>
    private bool _syncing;

    /// <summary>初始化控件。</summary>
    public DeptPhaseFilterBar()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshOptions();
    }

    /// <summary>选中的项目部；null 表示「全部」。</summary>
    public static readonly DependencyProperty SelectedDeptProperty = DependencyProperty.Register(
        nameof(SelectedDept), typeof(string), typeof(DeptPhaseFilterBar), new PropertyMetadata(null, OnSelectionPropertyChanged));

    /// <summary>选中的项目部；null 表示「全部」。</summary>
    public string? SelectedDept
    {
        get => (string?)GetValue(SelectedDeptProperty);
        set => SetValue(SelectedDeptProperty, value);
    }

    /// <summary>选中的期数；null 表示「全部」，空串表示「无期数」。</summary>
    public static readonly DependencyProperty SelectedPhaseProperty = DependencyProperty.Register(
        nameof(SelectedPhase), typeof(string), typeof(DeptPhaseFilterBar), new PropertyMetadata(null, OnSelectionPropertyChanged));

    /// <summary>选中的期数；null 表示「全部」，空串表示「无期数」。</summary>
    public string? SelectedPhase
    {
        get => (string?)GetValue(SelectedPhaseProperty);
        set => SetValue(SelectedPhaseProperty, value);
    }

    /// <summary>依赖属性变化时同步两个下拉框的选中态。</summary>
    /// <param name="d">属性所属对象。</param>
    /// <param name="e">变更参数。</param>
    private static void OnSelectionPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DeptPhaseFilterBar bar)
        {
            bar.SyncSelections();
        }
    }

    /// <summary>
    /// 重新读取项目部与期数候选项并同步选中态。
    /// 当前选中值不在候选项中（如已被设置页删除）时临时并入，避免筛选被静默放宽。
    /// </summary>
    public void RefreshOptions()
    {
        var deptOptions = DeptPhaseService.Instance.GetDeptFilterOptions().ToList();
        if (SelectedDept is { Length: > 0 } dept && !deptOptions.Contains(dept))
        {
            deptOptions.Add(dept);
        }

        var phaseOptions = DeptPhaseService.Instance.GetPhaseFilterOptions().ToList();
        if (SelectedPhase is { Length: > 0 } phase && !phaseOptions.Contains(phase))
        {
            phaseOptions.Add(phase);
        }

        DeptBox.ItemsSource = deptOptions;
        PhaseBox.ItemsSource = phaseOptions;

        SyncSelections();
    }

    /// <summary>按依赖属性值更新两个下拉框的选中项；候选项尚未加载时跳过。</summary>
    private void SyncSelections()
    {
        if (DeptBox.ItemsSource is null || PhaseBox.ItemsSource is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            DeptBox.SelectedItem = SelectedDept ?? DeptPhaseService.FilterAllText;
            PhaseBox.SelectedItem = SelectedPhase switch
            {
                null => DeptPhaseService.FilterAllText,
                "" => DeptPhaseService.FilterBlankPhaseText,
                var phase => phase
            };
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>用户改变项目部选中项时回写依赖属性；「全部」映射为 null。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选择变化参数。</param>
    private void OnDeptSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var selected = DeptBox.SelectedItem as string;
        SelectedDept = string.IsNullOrEmpty(selected)
            || string.Equals(selected, DeptPhaseService.FilterAllText, StringComparison.Ordinal)
                ? null
                : selected;
    }

    /// <summary>用户改变期数选中项时回写依赖属性；「全部」→ null，「无期数」→ 空串。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选择变化参数。</param>
    private void OnPhaseSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var selected = PhaseBox.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)
            || string.Equals(selected, DeptPhaseService.FilterAllText, StringComparison.Ordinal))
        {
            SelectedPhase = null;
        }
        else if (string.Equals(selected, DeptPhaseService.FilterBlankPhaseText, StringComparison.Ordinal))
        {
            SelectedPhase = string.Empty;
        }
        else
        {
            SelectedPhase = selected;
        }
    }
}
