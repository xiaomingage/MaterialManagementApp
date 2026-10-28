using System;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaterialManagementApp.Controls;

/// <summary>
/// 通用分页条（依赖属性驱动，页面只需绑定几个属性即可复用）。
/// 数据表格页的 DataGrid（CommunityToolkit 7.1.2）不提供行虚拟化，因此统一改为分页加载：
/// 本控件负责显示「第 X / Y 页，共 N 条」、上一页 / 下一页按钮以及每页条数切换，
/// 实际的翻页与重新查询由页面视图模型提供的三个命令完成。
/// </summary>
public sealed partial class PagerBar : UserControl
{
    /// <summary>下拉框可选的每页条数，索引与 ComboBoxItem 一一对应。</summary>
    private static readonly int[] PageSizeChoices = { 20, 50, 100 };

    /// <summary>
    /// 抑制下拉框选择事件：控件构造时先选中「20 条/页」，
    /// 此时若触发命令会额外发起一次查询，因此在 Loaded 之前一律忽略。
    /// </summary>
    private bool _suppressPageSizeChange = true;

    /// <summary>初始化分页条。</summary>
    public PagerBar()
    {
        InitializeComponent();

        // 默认每页 20 条，与视图模型的 PageSize 默认值保持一致
        PageSizeSelector.SelectedIndex = 0;
        _suppressPageSizeChange = false;

        Loaded += (_, _) => RefreshComputedState();
        RefreshComputedState();
    }

    /// <summary>当前页码（从 1 开始）。</summary>
    public static readonly DependencyProperty CurrentPageProperty = DependencyProperty.Register(
        nameof(CurrentPage), typeof(int), typeof(PagerBar), new PropertyMetadata(1, OnPagingStateChanged));

    /// <summary>当前页码（从 1 开始）。</summary>
    public int CurrentPage
    {
        get => (int)GetValue(CurrentPageProperty);
        set => SetValue(CurrentPageProperty, value);
    }

    /// <summary>总页数。</summary>
    public static readonly DependencyProperty TotalPagesProperty = DependencyProperty.Register(
        nameof(TotalPages), typeof(int), typeof(PagerBar), new PropertyMetadata(1, OnPagingStateChanged));

    /// <summary>总页数。</summary>
    public int TotalPages
    {
        get => (int)GetValue(TotalPagesProperty);
        set => SetValue(TotalPagesProperty, value);
    }

    /// <summary>满足当前筛选条件的总记录数。</summary>
    public static readonly DependencyProperty TotalCountProperty = DependencyProperty.Register(
        nameof(TotalCount), typeof(long), typeof(PagerBar), new PropertyMetadata(0L, OnPagingStateChanged));

    /// <summary>满足当前筛选条件的总记录数。</summary>
    public long TotalCount
    {
        get => (long)GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }

    /// <summary>「上一页」命令。</summary>
    public static readonly DependencyProperty PreviousCommandProperty = DependencyProperty.Register(
        nameof(PreviousCommand), typeof(ICommand), typeof(PagerBar), new PropertyMetadata(null));

    /// <summary>「上一页」命令。</summary>
    public ICommand? PreviousCommand
    {
        get => (ICommand?)GetValue(PreviousCommandProperty);
        set => SetValue(PreviousCommandProperty, value);
    }

    /// <summary>「下一页」命令。</summary>
    public static readonly DependencyProperty NextCommandProperty = DependencyProperty.Register(
        nameof(NextCommand), typeof(ICommand), typeof(PagerBar), new PropertyMetadata(null));

    /// <summary>「下一页」命令。</summary>
    public ICommand? NextCommand
    {
        get => (ICommand?)GetValue(NextCommandProperty);
        set => SetValue(NextCommandProperty, value);
    }

    /// <summary>「切换每页条数」命令，参数为新的每页条数。</summary>
    public static readonly DependencyProperty PageSizeCommandProperty = DependencyProperty.Register(
        nameof(PageSizeCommand), typeof(ICommand), typeof(PagerBar), new PropertyMetadata(null));

    /// <summary>「切换每页条数」命令，参数为新的每页条数。</summary>
    public ICommand? PageSizeCommand
    {
        get => (ICommand?)GetValue(PageSizeCommandProperty);
        set => SetValue(PageSizeCommandProperty, value);
    }

    /// <summary>页码提示文本，例如「第 2 / 5 页，共 86 条」。</summary>
    private static readonly DependencyProperty PageTextProperty = DependencyProperty.Register(
        nameof(PageText), typeof(string), typeof(PagerBar), new PropertyMetadata("第 1 / 1 页，共 0 条"));

    /// <summary>页码提示文本。</summary>
    public string PageText => (string)GetValue(PageTextProperty);

    /// <summary>是否可点击「上一页」。</summary>
    private static readonly DependencyProperty CanGoPreviousProperty = DependencyProperty.Register(
        nameof(CanGoPrevious), typeof(bool), typeof(PagerBar), new PropertyMetadata(false));

    /// <summary>是否可点击「上一页」。</summary>
    public bool CanGoPrevious => (bool)GetValue(CanGoPreviousProperty);

    /// <summary>是否可点击「下一页」。</summary>
    private static readonly DependencyProperty CanGoNextProperty = DependencyProperty.Register(
        nameof(CanGoNext), typeof(bool), typeof(PagerBar), new PropertyMetadata(false));

    /// <summary>是否可点击「下一页」。</summary>
    public bool CanGoNext => (bool)GetValue(CanGoNextProperty);

    /// <summary>页码 / 总页数 / 总条数变化时重算提示文本与按钮可用性。</summary>
    /// <param name="d">属性所属对象。</param>
    /// <param name="e">变更参数。</param>
    private static void OnPagingStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PagerBar bar)
        {
            bar.RefreshComputedState();
        }
    }

    /// <summary>按当前分页状态刷新只读显示属性。</summary>
    private void RefreshComputedState()
    {
        var totalPages = TotalPages < 1 ? 1 : TotalPages;
        var currentPage = CurrentPage < 1 ? 1 : CurrentPage;

        SetValue(PageTextProperty, $"第 {currentPage} / {totalPages} 页，共 {TotalCount} 条");
        SetValue(CanGoPreviousProperty, currentPage > 1);
        SetValue(CanGoNextProperty, currentPage < totalPages);
    }

    /// <summary>点击「上一页」。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        var command = PreviousCommand;
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>点击「下一页」。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        var command = NextCommand;
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>切换每页条数。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选择变化参数。</param>
    private void OnPageSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPageSizeChange)
        {
            return;
        }

        var index = PageSizeSelector.SelectedIndex;
        if (index < 0 || index >= PageSizeChoices.Length)
        {
            return;
        }

        var size = PageSizeChoices[index];
        var command = PageSizeCommand;
        if (command is not null && command.CanExecute(size))
        {
            command.Execute(size);
        }
    }
}
