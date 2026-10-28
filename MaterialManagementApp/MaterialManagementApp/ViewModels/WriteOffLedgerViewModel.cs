using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.ViewModels;

/// <summary>
/// 核销台账页面（物资核销 → 核销台账）的视图模型。
/// 上方为物资名称与核销月份筛选，下方表格支持行内新增（首行插入空白行）、行内编辑保存与删除；
/// 金额由 核销数量 × 单价 自动计算，不手工录入。
/// </summary>
public partial class WriteOffLedgerViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<WriteOffLedgerViewModel>();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public WriteOffLedgerViewModel()
    {
        MaterialNameKeyword = string.Empty;
        MonthFilter = string.Empty;
        StatusMessage = string.Empty;
        TotalText = "合计：0 条核销记录";
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>核销记录集合，绑定到 DataGrid。</summary>
    public ObservableCollection<MaterialWriteOff> Items { get; } = new();

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    private MaterialWriteOff? _selectedWriteOff;

    /// <summary>筛选：物资名称关键字，模糊匹配；为空表示不限制。</summary>
    [ObservableProperty]
    private string _materialNameKeyword;

    /// <summary>筛选：核销月份，格式 yyyy-MM；为空表示全部月份。</summary>
    [ObservableProperty]
    private string _monthFilter;

    /// <summary>状态提示文本。</summary>
    [ObservableProperty]
    private string _statusMessage;

    /// <summary>合计文本（记录条数、核销数量与金额）。</summary>
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
        !string.IsNullOrWhiteSpace(MaterialNameKeyword) || !string.IsNullOrWhiteSpace(MonthFilter);

    /// <summary>
    /// 按当前筛选条件从数据库分页加载核销记录。
    /// 先统计总条数求出总页数，再按当前页取一页数据；合计仍按当前页已加载的行计算。
    /// </summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载核销台账数据...");
        try
        {
            // 筛选条件为空白时不加对应条件
            var materialNameKeyword = string.IsNullOrWhiteSpace(MaterialNameKeyword) ? null : MaterialNameKeyword;
            var month = string.IsNullOrWhiteSpace(MonthFilter) ? null : MonthFilter;

            TotalCount = DatabaseService.Instance.CountMaterialWriteOffs(materialNameKeyword, month);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetMaterialWriteOffsPaged(CurrentPage, PageSize, materialNameKeyword, month);

            DetachTotalsTracking();
            Items.Clear();

            foreach (var item in filtered)
            {
                AttachTotalsTracking(item);
                Items.Add(item);
            }

            RecalculateTotals();
            StatusMessage = IsFilterActive
                ? $"已加载 {Items.Count} 条核销记录（筛选已启用）。"
                : $"已加载 {Items.Count} 条核销记录。";
            _logger.LogInformation("核销台账数据加载完成，共 {Count} 条。", Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载核销台账数据失败。");
            StatusMessage = $"加载失败：{ex.Message}";
        }
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
        Load();
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
        Load();
    }

    /// <summary>切换每页条数并回到第一页。</summary>
    /// <param name="size">新的每页条数。</param>
    [RelayCommand]
    public void ChangePageSize(int size)
    {
        PageSize = size is < 1 or > 1000 ? 20 : size;
        CurrentPage = 1;
        Load();
    }

    /// <summary>按当前筛选条件重新加载列表（「查询」按钮）。</summary>
    [RelayCommand]
    public void Query()
    {
        _logger.LogInformation("正在按条件查询核销台账...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        MaterialNameKeyword = string.Empty;
        MonthFilter = string.Empty;
        CurrentPage = 1;
        Load();
    }

    /// <summary>
    /// 新增：在表格第一行插入一条全编辑状态的空白记录。
    /// 已存在未保存的空白行时先移除旧的，避免出现多条空行；
    /// 该行不写数据库，只有点击行末「保存」才会校验并入库。
    /// </summary>
    [RelayCommand]
    public void NewRow()
    {
        var pending = Items.FirstOrDefault(x => x.IsNewRow);
        if (pending is not null)
        {
            DetachTotalsTracking(pending);
            Items.Remove(pending);
        }

        var row = new MaterialWriteOff
        {
            // 核销月份给出当月默认值，其余字段保持空白由用户直接在表格里填写
            WriteOffMonth = DateTime.Now.ToString("yyyy-MM"),
            IsNewRow = true
        };

        AttachTotalsTracking(row);
        Items.Insert(0, row);
        RecalculateTotals();

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白核销行，当前列表 {Count} 条。", Items.Count);
    }

    /// <summary>保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。</summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(MaterialWriteOff? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.MaterialName))
        {
            StatusMessage = "物资名称不能为空，未保存。";
            _logger.LogWarning("保存核销记录被拒绝：物资名称为空。");
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            // 表格内直接编辑的月份是自由文本，落库前统一规范成 yyyy-MM，无法解析时回退当月
            row.WriteOffMonth = NormalizeMonth(row.WriteOffMonth);
            row.Amount = Math.Round(row.WriteOffQty * row.UnitPrice, 2);

            if (isInsert)
            {
                DatabaseService.Instance.AddMaterialWriteOff(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdateMaterialWriteOff(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert
                ? $"已新增核销记录：{row.MaterialName}。"
                : $"已保存核销记录：{row.MaterialName}。";
            _logger.LogInformation("保存核销记录成功，Id={Id}，名称={Name}，新增={IsInsert}。", row.Id, row.MaterialName, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存核销记录失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(MaterialWriteOff? row)
    {
        if (row is null || !row.IsNewRow)
        {
            return;
        }

        DetachTotalsTracking(row);
        Items.Remove(row);
        RecalculateTotals();

        StatusMessage = "已取消新增。";
        _logger.LogInformation("已移除未保存的表格新增行。");
    }

    /// <summary>删除指定行：从集合移除并同步删除数据库记录。</summary>
    /// <param name="row">表格中被点击「删除」的记录。</param>
    [RelayCommand]
    public void DeleteRow(MaterialWriteOff? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            // 先解绑合计订阅再移出集合，避免残留事件引用导致内存泄漏与重复重算
            DetachTotalsTracking(row);
            Items.Remove(row);

            // Id 为 0 表示尚未落库的行，此时无需访问数据库
            if (row.Id != 0)
            {
                DatabaseService.Instance.DeleteMaterialWriteOff(row.Id);
            }

            RecalculateTotals();
            StatusMessage = "已删除。";
            _logger.LogInformation("删除核销记录成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除核销记录失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部记录，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部核销记录。</returns>
    public List<MaterialWriteOff> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetMaterialWriteOffs()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的记录。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(MaterialWriteOff item)
    {
        if (!string.IsNullOrWhiteSpace(MaterialNameKeyword)
            && !item.MaterialName.Contains(MaterialNameKeyword.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MonthFilter)
            && !string.Equals(item.WriteOffMonth, MonthFilter.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>重算合计文本：记录条数、核销数量与金额。</summary>
    private void RecalculateTotals()
    {
        var qty = Math.Round(Items.Sum(x => x.WriteOffQty), 2);
        var amount = Math.Round(Items.Sum(x => x.Amount), 2);
        TotalText = $"合计：{Items.Count} 条　|　核销数量 {qty}　|　金额 {amount} 元";
    }

    /// <summary>把表格内直接输入的月份文本规范成 yyyy-MM；无法解析时回退当月。</summary>
    /// <param name="value">原始月份文本。</param>
    /// <returns>yyyy-MM 格式月份。</returns>
    private static string NormalizeMonth(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToString("yyyy-MM")
            : DateTime.Now.ToString("yyyy-MM");
    }

    /// <summary>订阅单条记录的数量与金额变化，使表格内编辑后合计立即刷新。</summary>
    /// <param name="item">刚加入集合的记录。</param>
    private void AttachTotalsTracking(MaterialWriteOff item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
    }

    /// <summary>取消单条记录的订阅。</summary>
    /// <param name="item">要移除的记录。</param>
    private void DetachTotalsTracking(MaterialWriteOff item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
    }

    /// <summary>
    /// 取消当前集合中所有记录的订阅。Load 每次都会重建集合对象，
    /// 不先解绑会导致旧对象残留事件引用，既泄漏内存又可能触发多次重算。
    /// </summary>
    private void DetachTotalsTracking()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    /// <summary>记录属性变化时按需重算合计。</summary>
    /// <param name="sender">发生变化的记录。</param>
    /// <param name="e">属性变化参数。</param>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MaterialWriteOff.WriteOffQty)
            or nameof(MaterialWriteOff.Amount))
        {
            RecalculateTotals();
        }
    }
}
