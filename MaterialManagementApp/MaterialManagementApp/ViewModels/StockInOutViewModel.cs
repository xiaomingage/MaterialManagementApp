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
/// 入库领用退库页面（库存管理 → 入库领用退库）的视图模型。
/// 上半部分是发生日期区间、物资名称与物资来源筛选条件；下半部分的 DataGrid 直接承载数据录入：
/// 点击「新增」会在表格首行插入一条全编辑状态的空白行，行末「保存」校验后写入数据库并刷新、
/// 「取消」则直接移除该行。
/// </summary>
public partial class StockInOutViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<StockInOutViewModel>();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public StockInOutViewModel()
    {
        SelectedSource = AllSources;
        NameKeyword = string.Empty;
        StatusMessage = string.Empty;
        TotalText = "合计：0 条流水";
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>物资来源筛选中的「全部」选项文本。</summary>
    public const string AllSources = "全部";

    /// <summary>流水集合，绑定到 DataGrid。加载后按发生日期倒序排列。</summary>
    public ObservableCollection<StockRecord> Items { get; } = new();

    /// <summary>物资来源下拉选项，首项固定为「全部」。</summary>
    public ObservableCollection<string> SourceOptions { get; } = new() { AllSources, "甲供", "自购" };

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private StockRecord? _selectedItem;

    /// <summary>筛选：开始日期（含当天）。为 null 表示不限制下界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _startDate;

    /// <summary>筛选：结束日期（含当天）。为 null 表示不限制上界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _endDate;

    /// <summary>筛选：物资名称关键字，空白表示不限制。</summary>
    [ObservableProperty]
    private string _nameKeyword;

    /// <summary>筛选：当前选中的物资来源，默认「全部」。</summary>
    [ObservableProperty]
    private string _selectedSource;

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

    /// <summary>是否已选中某行（供工具栏「删除」使用）。</summary>
    public bool HasSelection => SelectedItem is not null;

    /// <summary>当前是否启用了任一筛选条件（用于区分提示文案）。</summary>
    private bool IsFilterActive =>
        StartDate.HasValue
        || EndDate.HasValue
        || !string.IsNullOrWhiteSpace(NameKeyword)
        || !string.Equals(SelectedSource, AllSources, StringComparison.Ordinal);

    /// <summary>按当前筛选条件从数据库加载库存流水并重算合计。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载库存流水数据...");
        try
        {
            var dateFrom = StartDate?.ToString("yyyy-MM-dd");
            var dateTo = EndDate?.ToString("yyyy-MM-dd");
            // 下拉框选「全部」时不加来源条件
            var materialSource = string.Equals(SelectedSource, AllSources, StringComparison.Ordinal)
                ? null
                : SelectedSource;
            // 名称关键字为空白时不加名称条件
            var nameKeyword = string.IsNullOrWhiteSpace(NameKeyword) ? null : NameKeyword.Trim();

            TotalCount = DatabaseService.Instance.CountStockRecords(dateFrom, dateTo, materialSource, nameKeyword);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetStockRecordsPaged(CurrentPage, PageSize, dateFrom, dateTo, materialSource, nameKeyword);

            DetachTotalsTracking();
            Items.Clear();

            foreach (var item in filtered)
            {
                AttachTotalsTracking(item);
                Items.Add(item);
            }

            UpdateTotal();
            StatusMessage = IsFilterActive
                ? $"已加载 {Items.Count} 条库存流水（筛选已启用）。"
                : $"已加载 {Items.Count} 条库存流水。";
            _logger.LogInformation("库存流水数据加载完成，共 {Count} 条。", Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载库存流水数据失败。");
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>按当前筛选条件重新加载列表（「查询」按钮）。</summary>
    [RelayCommand]
    public void Query()
    {
        _logger.LogInformation("正在按条件查询库存流水...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        StartDate = null;
        EndDate = null;
        NameKeyword = string.Empty;
        SelectedSource = AllSources;
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

        var settings = SettingsService.Instance.Settings;
        var row = new StockRecord
        {
            // 发生日期给出当天默认值，单位与经办人取设置页默认值，其余字段由用户直接在表格里填写
            OccurDate = DateTime.Now.ToString("yyyy-MM-dd"),
            MaterialSource = string.Equals(SelectedSource, AllSources, StringComparison.Ordinal) ? "甲供" : SelectedSource,
            ChangeType = "入库",
            Unit = settings.DefaultUnit,
            Handler = settings.DefaultHandler,
            IsNewRow = true
        };

        AttachTotalsTracking(row);
        Items.Insert(0, row);
        UpdateTotal();

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白库存流水行，当前列表 {Count} 条。", Items.Count);
    }

    /// <summary>保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。</summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(StockRecord? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.MaterialName))
        {
            StatusMessage = "物资名称不能为空，未保存。";
            _logger.LogWarning("保存库存流水被拒绝：物资名称为空。");
            return;
        }

        if (row.ChangeType is not ("入库" or "出库" or "退库" or "盘点"))
        {
            StatusMessage = "变更类型只能是「入库 / 出库 / 退库 / 盘点」，未保存。";
            _logger.LogWarning("保存库存流水被拒绝：变更类型非法，类型={ChangeType}。", row.ChangeType);
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            // 表格内直接编辑的日期是自由文本，落库前统一规范成 yyyy-MM-dd，无法解析时回退当天
            row.OccurDate = NormalizeDate(row.OccurDate);
            row.MaterialName = row.MaterialName.Trim();

            if (isInsert)
            {
                DatabaseService.Instance.AddStockRecord(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdateStockRecord(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert
                ? $"已新增流水：{row.MaterialName}。"
                : $"已保存流水：{row.MaterialName}。";
            _logger.LogInformation("保存库存流水成功，Id={Id}，名称={Name}，新增={IsInsert}。", row.Id, row.MaterialName, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存库存流水失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(StockRecord? row)
    {
        if (row is null || !row.IsNewRow)
        {
            return;
        }

        DetachTotalsTracking(row);
        Items.Remove(row);
        UpdateTotal();

        StatusMessage = "已取消新增。";
        _logger.LogInformation("已移除未保存的表格新增行。");
    }

    /// <summary>删除指定行：从集合移除并同步删除数据库记录。</summary>
    /// <param name="row">表格中被点击「删除」的记录。</param>
    [RelayCommand]
    public void DeleteRow(StockRecord? row)
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
                DatabaseService.Instance.DeleteStockRecord(row.Id);
            }

            UpdateTotal();
            StatusMessage = "已删除。";
            _logger.LogInformation("删除库存流水成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除库存流水失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部记录，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部库存流水。</returns>
    public List<StockRecord> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetStockRecords()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的库存流水。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(StockRecord item)
    {
        if (StartDate.HasValue || EndDate.HasValue)
        {
            // OccurDate 以 yyyy-MM-dd 文本存储，解析失败说明是脏数据，直接排除以免混入结果
            if (!DateTime.TryParse(item.OccurDate, out var date))
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

        if (!string.Equals(SelectedSource, AllSources, StringComparison.Ordinal)
            && !string.Equals(item.MaterialSource, SelectedSource, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(NameKeyword)
            && (item.MaterialName is null || !item.MaterialName.Contains(NameKeyword.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    /// <summary>把表格内直接输入的日期文本规范成 yyyy-MM-dd；无法解析时回退当天。</summary>
    /// <param name="value">原始日期文本。</param>
    /// <returns>yyyy-MM-dd 格式日期。</returns>
    private static string NormalizeDate(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToString("yyyy-MM-dd")
            : DateTime.Now.ToString("yyyy-MM-dd");
    }

    /// <summary>重算合计文本。</summary>
    private void UpdateTotal()
    {
        var total = Math.Round(Items.Sum(x => x.Quantity), 2);
        TotalText = $"合计：{Items.Count} 条流水　|　数量合计 {total}";
    }

    /// <summary>订阅单条记录的数量变化，使表格内编辑后合计立即刷新。</summary>
    /// <param name="item">刚加入集合的记录。</param>
    private void AttachTotalsTracking(StockRecord item)
    {
        item.PropertyChanged += OnRecordPropertyChanged;
    }

    /// <summary>取消单条记录的订阅。</summary>
    /// <param name="item">要移除的记录。</param>
    private void DetachTotalsTracking(StockRecord item)
    {
        item.PropertyChanged -= OnRecordPropertyChanged;
    }

    /// <summary>
    /// 取消当前集合中所有记录的订阅。Load 每次都会重建集合对象，
    /// 不先解绑会导致旧对象残留事件引用，既泄漏内存又可能触发多次重算。
    /// </summary>
    private void DetachTotalsTracking()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnRecordPropertyChanged;
        }
    }

    /// <summary>记录属性变化时按需重算合计。</summary>
    /// <param name="sender">发生变化的记录。</param>
    /// <param name="e">属性变化参数。</param>
    private void OnRecordPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StockRecord.Quantity))
        {
            UpdateTotal();
        }
    }
}
