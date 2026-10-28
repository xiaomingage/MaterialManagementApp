using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.ViewModels;

/// <summary>
/// 库存查询页面（库存管理 → 库存查询）的视图模型。
/// 数据为 GetStockSummary 按物资名称/规格/单位聚合的账面结存，没有 Id、无法入库，
/// 因此整页只读：上方为物资来源与物资名称筛选，下方为只读表格，工具栏提供导出与刷新。
/// </summary>
public partial class StockCheckViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<StockCheckViewModel>();

    /// <summary>筛选后的全量结果缓存；分页显示时始终以它为基准切片。</summary>
    private List<StockSummaryItem> _allItems = new();

    /// <summary>当前筛选条件下的全量结果，供导出使用（不受分页影响）。</summary>
    public IReadOnlyList<StockSummaryItem> AllItems => _allItems;

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public StockCheckViewModel()
    {
        SelectedSource = "甲供";
        NameKeyword = string.Empty;
        StatusMessage = string.Empty;
        TotalText = "合计：0 种物资";
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>账面结存集合，绑定到 DataGrid。</summary>
    public ObservableCollection<StockSummaryItem> Items { get; } = new();

    /// <summary>物资来源下拉选项。GetStockSummary 必须指定来源，因此不提供「全部」。</summary>
    public ObservableCollection<string> SourceOptions { get; } = new() { "甲供", "自购" };

    /// <summary>筛选：物资来源，默认「甲供」。</summary>
    [ObservableProperty]
    private string _selectedSource;

    /// <summary>筛选：物资名称关键字，空白表示不限制。</summary>
    [ObservableProperty]
    private string _nameKeyword;

    /// <summary>状态提示文本。</summary>
    [ObservableProperty]
    private string _statusMessage;

    /// <summary>合计文本。</summary>
    [ObservableProperty]
    private string _totalText;

    /// <summary>当前页码，从 1 开始。</summary>
    [ObservableProperty]
    private int _currentPage;

    /// <summary>每页条数，默认 20。</summary>
    [ObservableProperty]
    private int _pageSize;

    /// <summary>满足当前筛选条件的总记录数。</summary>
    [ObservableProperty]
    private long _totalCount;

    /// <summary>总页数；无记录时按 1 页显示，便于页脚呈现「第 1 / 1 页」。</summary>
    [ObservableProperty]
    private int _totalPages;

    /// <summary>当前是否启用了名称筛选（用于区分提示文案）。</summary>
    private bool IsFilterActive => !string.IsNullOrWhiteSpace(NameKeyword);

    /// <summary>按当前筛选条件加载账面结存数据。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载库存查询数据...");
        try
        {
            var summary = DatabaseService.Instance.GetStockSummary(SelectedSource);

            // 名称筛选在内存完成：聚合结果数据量有限，无需下推到 SQL
            _allItems = summary.Where(MatchesFilter).ToList();

            TotalCount = _allItems.Count;
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            ApplyPage();

            var total = Math.Round(_allItems.Sum(x => x.Balance), 2);
            TotalText = $"合计：{_allItems.Count} 种物资　|　账面结存总计 {total}";
            StatusMessage = IsFilterActive
                ? $"已加载 {_allItems.Count} 种物资的账面结存（筛选已启用）。"
                : $"已加载 {_allItems.Count} 种物资的账面结存。";
            _logger.LogInformation("库存查询数据加载完成，共 {Count} 条。", _allItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载库存查询数据失败。");
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>按当前筛选条件重新加载列表（「查询」按钮）。</summary>
    [RelayCommand]
    public void Query()
    {
        _logger.LogInformation("正在按条件查询库存结存...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        SelectedSource = "甲供";
        NameKeyword = string.Empty;
        CurrentPage = 1;
        Load();
    }

    /// <summary>翻到上一页。</summary>
    [RelayCommand]
    public void PreviousPage()
    {
        if (CurrentPage <= 1)
        {
            return;
        }

        CurrentPage--;
        ApplyPage();
    }

    /// <summary>翻到下一页。</summary>
    [RelayCommand]
    public void NextPage()
    {
        if (CurrentPage >= TotalPages)
        {
            return;
        }

        CurrentPage++;
        ApplyPage();
    }

    /// <summary>切换每页条数并回到第一页。</summary>
    /// <param name="size">新的每页条数。</param>
    [RelayCommand]
    public void ChangePageSize(int size)
    {
        PageSize = size is < 1 or > 1000 ? 20 : size;
        CurrentPage = 1;
        ApplyPage();
    }

    /// <summary>按当前页码把全量结果的一页写入绑定集合（不重新查询数据库）。</summary>
    private void ApplyPage()
    {
        Items.Clear();
        foreach (var item in _allItems.Skip((CurrentPage - 1) * PageSize).Take(PageSize))
        {
            Items.Add(item);
        }
    }

    /// <summary>判断一条汇总记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的汇总行。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(StockSummaryItem item)
    {
        return string.IsNullOrWhiteSpace(NameKeyword)
            || (item.MaterialName?.Contains(NameKeyword.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
