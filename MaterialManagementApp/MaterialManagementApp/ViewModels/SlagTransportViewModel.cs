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
/// 渣土运输页面（地材台账 → 渣土运输）的视图模型。
/// 上方为票据日期区间与车牌号筛选；下方 DataGrid 直接承载数据录入：
/// 点击「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后写入数据库、
/// 「取消」直接移除。「OCR识别」打开离线识别窗口，识别完成的字段由页面回调填入空白行。
/// </summary>
public partial class SlagTransportViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<SlagTransportViewModel>();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public SlagTransportViewModel()
    {
        PlateKeyword = string.Empty;
        StatusMessage = string.Empty;
        CurrentPage = 1;
        PageSize = 20;
        SelectedDept = null;
        SelectedPhase = null;
    }

    /// <summary>渣土运输记录集合，绑定到 DataGrid。</summary>
    public ObservableCollection<SlagTransport> Items { get; } = new();

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SlagTransport? _selectedTransport;

    /// <summary>筛选：开始日期（含当天）。为 null 表示不限制下界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _startDate;

    /// <summary>筛选：结束日期（含当天）。为 null 表示不限制上界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _endDate;

    /// <summary>筛选：车牌号关键词，模糊包含匹配；为空表示不限制。</summary>
    [ObservableProperty]
    private string _plateKeyword;

    /// <summary>筛选：项目部；null 或空表示不限制。</summary>
    [ObservableProperty]
    private string? _selectedDept;

    /// <summary>筛选：期数；null 不限制、空串仅无期数、其余精确匹配。</summary>
    [ObservableProperty]
    private string? _selectedPhase;

    /// <summary>当前列表的数量合计。</summary>
    [ObservableProperty]
    private double _totalQuantity;

    /// <summary>当前列表的运输费合计。</summary>
    [ObservableProperty]
    private double _totalTransportFee;

    /// <summary>状态提示文本。</summary>
    [ObservableProperty]
    private string _statusMessage;

    /// <summary>
    /// 保存失败事件：数据校验不通过、数据库写入异常等场景触发。
    /// 页面订阅后弹出模态警告弹窗；视图模型无法直接操作 UI，故用事件解耦。
    /// </summary>
    public event EventHandler? SaveFailed;

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

    /// <summary>是否已选中某行（保留供表格选中态使用）。</summary>
    public bool HasSelection => SelectedTransport is not null;

    /// <summary>当前是否启用了任一筛选条件（用于区分提示文案）。</summary>
    private bool IsFilterActive =>
        StartDate.HasValue
        || EndDate.HasValue
        || !string.IsNullOrWhiteSpace(PlateKeyword);

    /// <summary>
    /// 按当前筛选条件从数据库分页加载渣土运输记录并重算合计。
    /// 先统计总条数求出总页数，再按当前页取一页数据；合计仍按当前页已加载的行计算。
    /// </summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载渣土运输数据...");
        try
        {
            var dateFrom = StartDate?.ToString("yyyy-MM-dd");
            var dateTo = EndDate?.ToString("yyyy-MM-dd");
            // 空白关键字不加车牌条件
            var plateKeyword = string.IsNullOrWhiteSpace(PlateKeyword) ? null : PlateKeyword;

            TotalCount = DatabaseService.Instance.CountSlagTransports(dateFrom, dateTo, plateKeyword, SelectedDept, SelectedPhase);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetSlagTransportsPaged(CurrentPage, PageSize, dateFrom, dateTo, plateKeyword, SelectedDept, SelectedPhase);

            DetachTotalsTracking();
            Items.Clear();

            foreach (var item in filtered)
            {
                AttachTotalsTracking(item);
                Items.Add(item);
            }

            RecalculateTotals();

            StatusMessage = IsFilterActive
                ? $"已加载 {Items.Count} 条渣土运输记录（筛选已启用）。"
                : $"已加载 {Items.Count} 条渣土运输记录。";
            _logger.LogInformation("渣土运输数据加载完成，共 {Count} 条。", Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载渣土运输数据失败。");
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
        _logger.LogInformation("正在按条件查询渣土运输记录...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        StartDate = null;
        EndDate = null;
        PlateKeyword = string.Empty;
        SelectedDept = null;
        SelectedPhase = null;
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

        var row = new SlagTransport
        {
            // 日期与时间给出当天默认值，其余字段保持空白由用户直接在表格里填写
            EntryDate = DateTime.Now.ToString("yyyy-MM-dd"),
            EntryTime = DateTime.Now.ToString("HH:mm"),
            IsNewRow = true
        };

        AttachTotalsTracking(row);
        Items.Insert(0, row);
        RecalculateTotals();

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白渣土运输行，当前列表 {Count} 条。", Items.Count);
    }

    /// <summary>
    /// 把 OCR 识别抽取出的字段按名称映射填入一条新的空白行（新建并置于表格首行）。
    /// 该行保持未保存状态，由用户核对后点击行末「保存」入库。
    /// </summary>
    /// <param name="fields"><see cref="ReceiptFieldExtractor.Extract"/> 的抽取结果。</param>
    public void ApplyOcrFields(IReadOnlyList<ReceiptField> fields)
    {
        NewRow();

        var row = Items.FirstOrDefault(x => x.IsNewRow);
        if (row is null)
        {
            return;
        }

        foreach (var field in fields)
        {
            // 「未识别」占位文本与空值都不能往行里填，避免污染待保存数据
            if (string.IsNullOrWhiteSpace(field.Value)
                || string.Equals(field.Value, ReceiptFieldExtractor.NotFound, StringComparison.Ordinal))
            {
                continue;
            }

            switch (field.Label)
            {
                case "物资名称":
                    row.MaterialName = field.Value;
                    break;
                case "规格":
                    row.Specification = field.Value;
                    break;
                case "数量":
                    row.Quantity = ParseDouble(field.Value);
                    break;
                case "单位":
                    row.Unit = field.Value;
                    break;
                case "进场日期":
                    row.EntryDate = NormalizeDate(field.Value);
                    break;
                case "车牌号":
                    row.PlateNumber = field.Value;
                    break;
                case "单据编号":
                    // 模型只存编号后四位，与历史「NO 后四位」口径保持一致
                    row.NoLast4 = field.Value.Length > 4 ? field.Value[^4..] : field.Value;
                    break;
                case "经办人":
                    row.SiteSignature = field.Value;
                    break;
            }
        }

        StatusMessage = "OCR 识别完成，字段已填入表格首行的空白记录，请核对后点击该行末的「保存」。";
        _logger.LogInformation("OCR 识别字段已填入渣土运输新增行，材料={Name}，车牌={Plate}。", row.MaterialName, row.PlateNumber);
    }

    /// <summary>保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。</summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(SlagTransport? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.MaterialName))
        {
            StatusMessage = "材料种类不能为空，未保存。";
            _logger.LogWarning("保存渣土运输被拒绝：材料种类为空。");
            SaveFailed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 边界防护：方量与运输费不允许为负，防止负数金额污染统计
        if (row.Quantity < 0 || row.TransportFee < 0)
        {
            StatusMessage = "数量和运输费不能为负数，未保存。";
            _logger.LogWarning("保存渣土运输被拒绝：数量或运输费为负。数量={Qty}，费用={Fee}。", row.Quantity, row.TransportFee);
            SaveFailed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            // 表格内直接编辑的日期是自由文本，落库前统一规范成 yyyy-MM-dd，无法解析时回退当天
            row.EntryDate = NormalizeDate(row.EntryDate);

            if (isInsert)
            {
                DatabaseService.Instance.AddSlagTransport(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdateSlagTransport(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert
                ? $"已新增渣土运输记录：{row.MaterialName}（{row.PlateNumber}）。"
                : $"已保存渣土运输记录：{row.MaterialName}（{row.PlateNumber}）。";
            _logger.LogInformation("保存渣土运输成功，Id={Id}，车牌={Plate}，新增={IsInsert}。", row.Id, row.PlateNumber, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存渣土运输失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
            SaveFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(SlagTransport? row)
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
    public void DeleteRow(SlagTransport? row)
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
                DatabaseService.Instance.DeleteSlagTransport(row.Id);
            }

            RecalculateTotals();
            StatusMessage = "已删除。";
            _logger.LogInformation("删除渣土运输成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除渣土运输失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部记录，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部记录。</returns>
    public List<SlagTransport> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetSlagTransports()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的记录。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(SlagTransport item)
    {
        if (StartDate.HasValue || EndDate.HasValue)
        {
            // EntryDate 以 yyyy-MM-dd 文本存储，解析失败说明是脏数据，直接排除以免混入结果
            if (!DateTime.TryParse(item.EntryDate, out var date))
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

        if (!string.IsNullOrWhiteSpace(PlateKeyword)
            && !item.PlateNumber.Contains(PlateKeyword.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DeptPhaseService.MatchesDimension(SelectedDept, SelectedPhase, item.ProjectDept, item.ProjectPhase);
    }

    /// <summary>重算合计行：数量合计与运输费合计。</summary>
    private void RecalculateTotals()
    {
        TotalQuantity = Math.Round(Items.Sum(x => x.Quantity), 2);
        TotalTransportFee = Math.Round(Items.Sum(x => x.TransportFee), 2);
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

    /// <summary>从文本中提取数值，无法解析时返回 0。</summary>
    /// <param name="value">数值文本，可能带单位。</param>
    /// <returns>解析出的数值；失败为 0。</returns>
    private static double ParseDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var match = System.Text.RegularExpressions.Regex.Match(value, @"-?\d+(?:\.\d+)?");
        return match.Success && double.TryParse(match.Value, out var result) ? result : 0;
    }

    /// <summary>订阅单条记录的数量/运输费变化，使表格内编辑后合计立即刷新。</summary>
    /// <param name="item">刚加入集合的记录。</param>
    private void AttachTotalsTracking(SlagTransport item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
    }

    /// <summary>取消单条记录的订阅。</summary>
    /// <param name="item">要移除的记录。</param>
    private void DetachTotalsTracking(SlagTransport item)
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
        if (e.PropertyName is nameof(SlagTransport.Quantity) or nameof(SlagTransport.TransportFee))
        {
            RecalculateTotals();
        }
    }
}
