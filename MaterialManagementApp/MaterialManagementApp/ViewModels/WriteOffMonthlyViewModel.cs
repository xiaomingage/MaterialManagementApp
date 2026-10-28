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
/// 月度核销页面（物资核销 → 月度核销）的视图模型。
/// 只读展示各月份的核销汇总：上方为月份筛选（留空为全部月份），下方为只读汇总表格。
/// </summary>
public partial class WriteOffMonthlyViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<WriteOffMonthlyViewModel>();

    /// <summary>筛选后的全量结果缓存；分页显示时始终以它为基准切片。</summary>
    private List<WriteOffSummaryItem> _allItems = new();

    /// <summary>当前筛选条件下的全量结果，供导出使用（不受分页影响）。</summary>
    public IReadOnlyList<WriteOffSummaryItem> AllItems => _allItems;

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public WriteOffMonthlyViewModel()
    {
        MonthFilter = string.Empty;
        StatusMessage = string.Empty;
        TotalText = "合计：0 个月份";
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>月度汇总集合，绑定到 DataGrid。</summary>
    public ObservableCollection<WriteOffSummaryItem> Summaries { get; } = new();

    /// <summary>月份过滤文本，格式 yyyy-MM；为空表示全部月份。</summary>
    [ObservableProperty]
    private string _monthFilter;

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

    /// <summary>按月份过滤条件加载月度汇总。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载月度核销汇总数据...");
        try
        {
            var month = (MonthFilter ?? string.Empty).Trim();
            var monthArg = string.IsNullOrEmpty(month) ? null : month;

            _allItems = DatabaseService.Instance.GetMonthlyWriteOffSummary(monthArg);

            TotalCount = _allItems.Count;
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            ApplyPage();

            var totalCount = _allItems.Sum(x => x.ItemCount);
            var totalQty = Math.Round(_allItems.Sum(x => x.TotalQty), 2);
            var totalAmount = Math.Round(_allItems.Sum(x => x.TotalAmount), 2);

            TotalText = $"合计：{_allItems.Count} 个月份　|　记录 {totalCount} 条　|　数量 {totalQty}　|　金额 {totalAmount} 元";
            StatusMessage = monthArg is null
                ? $"已加载全部月份汇总，共 {_allItems.Count} 个月份。"
                : $"已加载 {monthArg} 汇总。";
            _logger.LogInformation("月度核销汇总数据加载完成，共 {Count} 个月份。", _allItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载月度核销汇总数据失败。");
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>按当前筛选条件重新加载列表（「查询」按钮）。</summary>
    [RelayCommand]
    public void Query()
    {
        _logger.LogInformation("正在按条件查询月度核销汇总...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空月份过滤并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        MonthFilter = string.Empty;
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
        Summaries.Clear();
        foreach (var item in _allItems.Skip((CurrentPage - 1) * PageSize).Take(PageSize))
        {
            Summaries.Add(item);
        }
    }
}
