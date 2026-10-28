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
/// 库存预警页面（库存管理 → 库存预警）的视图模型。
/// 结存低于阈值的物资会被列出，只读展示；阈值默认取设置页配置，可在查询区临时调整后重新查询。
/// </summary>
public partial class StockWarningViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<StockWarningViewModel>();

    /// <summary>筛选后的全量结果缓存；分页显示时始终以它为基准切片。</summary>
    private List<StockWarningItem> _allItems = new();

    /// <summary>当前筛选条件下的全量结果，供导出使用（不受分页影响）。</summary>
    public IReadOnlyList<StockWarningItem> AllItems => _allItems;

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把默认值集中在这里赋值，避免 CS8618 警告；阈值初始值取设置页配置。
    /// </summary>
    public StockWarningViewModel()
    {
        Threshold = SettingsService.Instance.Settings.LowStockThreshold;
        NameKeyword = string.Empty;
        StatusMessage = string.Empty;
        TotalText = "合计：0 种物资";
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>预警清单，绑定到 DataGrid。</summary>
    public ObservableCollection<StockWarningItem> Items { get; } = new();

    /// <summary>预警阈值，结存低于该值即视为预警；默认取设置页配置。</summary>
    [ObservableProperty]
    private double _threshold;

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

    /// <summary>按当前阈值与名称关键字加载低库存物资清单。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载库存预警数据...");
        try
        {
            var threshold = (int)Math.Round(Threshold);
            var warnings = DatabaseService.Instance.GetStockWarnings(threshold);

            // 名称筛选在内存完成：预警清单数据量有限，无需下推到 SQL
            _allItems = warnings.Where(MatchesFilter).ToList();

            TotalCount = _allItems.Count;
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            ApplyPage();

            TotalText = $"合计：{_allItems.Count} 种物资低于阈值 {threshold}";
            StatusMessage = _allItems.Count == 0
                ? "所有物资库存均高于阈值，暂无预警。"
                : $"共 {_allItems.Count} 种物资低于阈值 {threshold}，请及时补充。";
            _logger.LogInformation("库存预警数据加载完成，共 {Count} 条。", _allItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载库存预警数据失败。");
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>按当前筛选条件重新加载列表（「查询」按钮）。</summary>
    [RelayCommand]
    public void Query()
    {
        _logger.LogInformation("正在按条件查询库存预警...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>恢复设置页阈值、清空名称关键字并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        Threshold = SettingsService.Instance.Settings.LowStockThreshold;
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

    /// <summary>判断一条预警记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的预警行。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(StockWarningItem item)
    {
        return string.IsNullOrWhiteSpace(NameKeyword)
            || (item.MaterialName?.Contains(NameKeyword.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
