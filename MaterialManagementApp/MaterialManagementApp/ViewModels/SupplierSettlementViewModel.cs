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
/// 月度结算页面（供应商管理 → 月度结算）的视图模型。
/// 只读展示各供应商的合同与结算金额汇总，供对账核对使用；
/// 上方为名称关键字与签约日期范围筛选，下方为只读表格。
/// </summary>
public partial class SupplierSettlementViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<SupplierSettlementViewModel>();

    /// <summary>筛选后的全量结果缓存；分页显示时始终以它为基准切片。</summary>
    private List<SupplierSettlementItem> _allItems = new();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public SupplierSettlementViewModel()
    {
        NameKeyword = string.Empty;
        StatusMessage = string.Empty;
        TotalText = "合计：0 家供应商";
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>结算汇总集合，绑定到 DataGrid。</summary>
    public ObservableCollection<SupplierSettlementItem> Items { get; } = new();

    /// <summary>筛选：供应商名称关键字，模糊匹配；为空表示不限制。</summary>
    [ObservableProperty]
    private string _nameKeyword;

    /// <summary>筛选：签约开始日期（含当天）。为 null 表示不限制下界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _startDate;

    /// <summary>筛选：签约结束日期（含当天）。为 null 表示不限制上界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _endDate;

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

    /// <summary>当前是否启用了任一筛选条件（用于区分提示文案）。</summary>
    private bool IsFilterActive =>
        !string.IsNullOrWhiteSpace(NameKeyword) || StartDate.HasValue || EndDate.HasValue;

    /// <summary>按当前筛选条件从数据库加载供应商结算汇总，筛选在内存完成。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载供应商结算数据...");
        try
        {
            _allItems = DatabaseService.Instance.GetSupplierSettlementSummary()
                .Where(MatchesFilter)
                .ToList();

            TotalCount = _allItems.Count;
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            ApplyPage();

            var amount = Math.Round(_allItems.Sum(x => x.MonthlySettlement), 2);
            TotalText = $"合计：{_allItems.Count} 家供应商　|　结算总额 {amount} 元";
            StatusMessage = IsFilterActive
                ? $"已加载 {_allItems.Count} 条结算记录（筛选已启用）。"
                : $"已加载 {_allItems.Count} 条结算记录。";
            _logger.LogInformation("供应商结算数据加载完成，共 {Count} 条。", _allItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载供应商结算数据失败。");
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>按当前筛选条件重新加载列表（「查询」按钮）。</summary>
    [RelayCommand]
    public void Query()
    {
        _logger.LogInformation("正在按条件查询供应商结算...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        NameKeyword = string.Empty;
        StartDate = null;
        EndDate = null;
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

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的记录。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(SupplierSettlementItem item)
    {
        if (!string.IsNullOrWhiteSpace(NameKeyword)
            && !item.Name.Contains(NameKeyword.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (StartDate.HasValue || EndDate.HasValue)
        {
            // SignDate 以 yyyy-MM-dd 文本存储，解析失败说明是脏数据，直接排除以免混入结果
            if (!DateTime.TryParse(item.SignDate, out var date))
            {
                return false;
            }

            if (StartDate.HasValue && date.Date < StartDate.Value.Date)
            {
                return false;
            }

            if (EndDate.HasValue && date.Date > EndDate.Value.Date)
            {
                return false;
            }
        }

        return true;
    }
}
