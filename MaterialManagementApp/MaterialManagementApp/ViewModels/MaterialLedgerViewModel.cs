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
/// 物资台账页面（地材台账 → 台账明细）的视图模型。
/// 上半部分是日期区间与物资名称筛选条件；下半部分的 DataGrid 直接承载数据录入：
/// 点击「新增」会在表格首行插入一条全编辑状态的空白行，行末「保存」校验后写入数据库并刷新、
/// 「取消」则直接移除该行。金额由 数量 × 单价 自动计算，不手工录入。
/// </summary>
public partial class MaterialLedgerViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<MaterialLedgerViewModel>();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public MaterialLedgerViewModel()
    {
        SelectedMaterialName = AllMaterialNames;
        StatusMessage = string.Empty;
        CurrentPage = 1;
        PageSize = 20;
        SelectedDept = null;
        SelectedPhase = null;
    }

    /// <summary>物资名称筛选中的「全部」选项文本。</summary>
    public const string AllMaterialNames = "全部";

    /// <summary>台账记录集合，绑定到 DataGrid。加载后按进场日期倒序排列。</summary>
    public ObservableCollection<MaterialLedger> Ledgers { get; } = new();

    /// <summary>物资名称下拉选项，首项固定为「全部」，其余为库中已存在的物资名称。</summary>
    public ObservableCollection<string> MaterialNameOptions { get; } = new() { AllMaterialNames };

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private MaterialLedger? _selectedLedger;

    /// <summary>筛选：开始日期（含当天）。为 null 表示不限制下界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _startDate;

    /// <summary>筛选：结束日期（含当天）。为 null 表示不限制上界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _endDate;

    /// <summary>筛选：当前选中的物资名称，默认「全部」。</summary>
    [ObservableProperty]
    private string _selectedMaterialName;

    /// <summary>筛选：项目部；null 或空表示不限制。</summary>
    [ObservableProperty]
    private string? _selectedDept;

    /// <summary>筛选：期数；null 不限制、空串仅无期数、其余精确匹配。</summary>
    [ObservableProperty]
    private string? _selectedPhase;

    /// <summary>当前列表的数量合计。</summary>
    [ObservableProperty]
    private double _totalQuantity;

    /// <summary>当前列表的金额合计。</summary>
    [ObservableProperty]
    private double _totalAmount;

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
    public bool HasSelection => SelectedLedger is not null;

    /// <summary>当前是否启用了任一筛选条件（用于区分提示文案）。</summary>
    private bool IsFilterActive =>
        StartDate.HasValue
        || EndDate.HasValue
        || !string.Equals(SelectedMaterialName, AllMaterialNames, StringComparison.Ordinal);

    /// <summary>
    /// 按当前筛选条件从数据库分页加载台账记录，刷新物资名称下拉选项并重算合计。
    /// 先统计总条数求出总页数，再按当前页取一页数据；合计仍按当前页已加载的行计算。
    /// </summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载物料台账数据...");
        try
        {
            var dateFrom = StartDate?.ToString("yyyy-MM-dd");
            var dateTo = EndDate?.ToString("yyyy-MM-dd");
            // 下拉框选「全部」时不加名称条件
            var materialName = string.Equals(SelectedMaterialName, AllMaterialNames, StringComparison.Ordinal)
                ? null
                : SelectedMaterialName;

            TotalCount = DatabaseService.Instance.CountMaterialLedgers(dateFrom, dateTo, materialName, SelectedDept, SelectedPhase);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetMaterialLedgersPaged(CurrentPage, PageSize, dateFrom, dateTo, materialName, SelectedDept, SelectedPhase);

            DetachTotalsTracking();
            Ledgers.Clear();

            foreach (var item in filtered)
            {
                // 历史数据可能存在数量/单价不为 0 但金额未写过的情况，加载时兜底校正一次
                if (item.Amount == 0 && item.Quantity != 0 && item.UnitPrice != 0)
                {
                    item.Amount = Math.Round(item.Quantity * item.UnitPrice, 2);
                }

                AttachTotalsTracking(item);
                Ledgers.Add(item);
            }

            RefreshMaterialNameOptions();
            RecalculateTotals();

            StatusMessage = IsFilterActive
                ? $"已加载 {Ledgers.Count} 条台账（筛选已启用）。"
                : $"已加载 {Ledgers.Count} 条台账。";
            _logger.LogInformation("物料台账数据加载完成，共 {Count} 条。", Ledgers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载物料台账数据失败。");
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
        _logger.LogInformation("正在按条件查询物料台账...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        StartDate = null;
        EndDate = null;
        SelectedMaterialName = AllMaterialNames;
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
        var pending = Ledgers.FirstOrDefault(x => x.IsNewRow);
        if (pending is not null)
        {
            DetachTotalsTracking(pending);
            Ledgers.Remove(pending);
        }

        var row = new MaterialLedger
        {
            // 日期与时间给出当天默认值，其余字段保持空白由用户直接在表格里填写
            EntryDate = DateTime.Now.ToString("yyyy-MM-dd"),
            EntryTime = DateTime.Now.ToString("HH:mm"),
            IsNewRow = true
        };

        AttachTotalsTracking(row);
        Ledgers.Insert(0, row);
        RecalculateTotals();

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白台账行，当前列表 {Count} 条。", Ledgers.Count);
    }

    /// <summary>
    /// 保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。
    /// </summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(MaterialLedger? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.MaterialName))
        {
            StatusMessage = "物资名称不能为空，未保存。";
            _logger.LogWarning("保存台账被拒绝：物资名称为空。");
            SaveFailed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 边界防护：数量与单价不允许为负，防止负数金额污染统计与库存
        if (row.Quantity < 0 || row.UnitPrice < 0)
        {
            StatusMessage = "数量和单价不能为负数，未保存。";
            _logger.LogWarning("保存台账被拒绝：数量或单价为负。数量={Qty}，单价={Price}。", row.Quantity, row.UnitPrice);
            SaveFailed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            // 表格内直接编辑的日期是自由文本，落库前统一规范成 yyyy-MM-dd，无法解析时回退当天
            row.EntryDate = NormalizeDate(row.EntryDate);
            row.Amount = Math.Round(row.Quantity * row.UnitPrice, 2);

            if (isInsert)
            {
                DatabaseService.Instance.AddMaterialLedger(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdateMaterialLedger(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert
                ? $"已新增台账：{row.MaterialName}。"
                : $"已保存台账：{row.MaterialName}。";
            _logger.LogInformation("保存台账成功，Id={Id}，名称={Name}，新增={IsInsert}。", row.Id, row.MaterialName, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存台账失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
            SaveFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(MaterialLedger? row)
    {
        if (row is null || !row.IsNewRow)
        {
            return;
        }

        DetachTotalsTracking(row);
        Ledgers.Remove(row);
        RecalculateTotals();

        StatusMessage = "已取消新增。";
        _logger.LogInformation("已移除未保存的表格新增行。");
    }

    /// <summary>删除指定行：从集合移除并同步删除数据库记录。</summary>
    /// <param name="row">表格中被点击「删除」的记录。</param>
    [RelayCommand]
    public void DeleteRow(MaterialLedger? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            // 先解绑合计订阅再移出集合，避免残留事件引用导致内存泄漏与重复重算
            DetachTotalsTracking(row);
            Ledgers.Remove(row);

            // Id 为 0 表示尚未落库的行，此时无需访问数据库
            if (row.Id != 0)
            {
                DatabaseService.Instance.DeleteMaterialLedger(row.Id);
            }

            RecalculateTotals();
            StatusMessage = "已删除。";
            _logger.LogInformation("删除物料台账成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除物料台账失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部记录，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部台账记录。</returns>
    public List<MaterialLedger> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetMaterialLedgers()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的台账记录。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(MaterialLedger item)
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

        if (!string.Equals(SelectedMaterialName, AllMaterialNames, StringComparison.Ordinal)
            && !string.Equals(item.MaterialName, SelectedMaterialName, StringComparison.Ordinal))
        {
            return false;
        }

        return DeptPhaseService.MatchesDimension(SelectedDept, SelectedPhase, item.ProjectDept, item.ProjectPhase);
    }

    /// <summary>
    /// 依据数据库中物资名称列的去重取值刷新下拉选项，并保证当前选中项仍然有效。
    /// 直接查询列值而非依赖全量加载结果，分页后也能拿到完整的候选项。
    /// </summary>
    private void RefreshMaterialNameOptions()
    {
        var names = DatabaseService.Instance.GetDistinctValues("MaterialLedger", "MaterialName");
        var previous = SelectedMaterialName;

        MaterialNameOptions.Clear();
        MaterialNameOptions.Add(AllMaterialNames);
        foreach (var name in names)
        {
            MaterialNameOptions.Add(name);
        }

        // 选中的物资可能因数据变化已不存在，回退「全部」，避免下拉框空白且筛选结果恒为空
        SelectedMaterialName = MaterialNameOptions.Contains(previous) ? previous : AllMaterialNames;
    }

    /// <summary>
    /// 重算合计行。数量合计为 Σ数量；金额合计按 Σ(数量 × 单价) 现算，
    /// 而不是 Σ金额——Amount 是派生值，历史数据或未触发 setter 的记录可能为 0，直接求和会低估。
    /// </summary>
    private void RecalculateTotals()
    {
        TotalQuantity = Math.Round(Ledgers.Sum(x => x.Quantity), 2);
        TotalAmount = Math.Round(Ledgers.Sum(x => x.Quantity * x.UnitPrice), 2);
    }

    /// <summary>
    /// 把 OCR 抽取字段填入表格首行的空白新增行。
    /// 已有未保存的空行时先移除旧的，保证新填入的数据在最上方；
    /// 「未识别」占位文本与空值都会跳过，避免污染待保存数据。
    /// </summary>
    /// <param name="fields"><see cref="ReceiptFieldExtractor.Extract"/> 的抽取结果。</param>
    public void ApplyOcrFields(IReadOnlyList<ReceiptField> fields)
    {
        // 复用 NewRow 逻辑：内部会清理旧空行 + 创建新空行 + 加入集合
        NewRow();

        var row = Ledgers.FirstOrDefault(x => x.IsNewRow);
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
                // 单价不再从 OCR 识别（识别率太低），留给后面的合同匹配填入
                // case "单价":
                //     row.UnitPrice = ParseDouble(field.Value);
                //     break;
                case "进场日期":
                    row.EntryDate = NormalizeDate(field.Value);
                    break;
                case "单据编号":
                    row.DocNo = field.Value;
                    break;
                case "供应商":
                    row.Supplier = field.Value;
                    break;
                case "经办人":
                    row.Handler = field.Value;
                    break;
                case "车牌号":
                    row.PlateNumber = field.Value;
                    break;
                case "金额":
                    // 金额由 数量 × 单价 自动重算，不手工填入
                    break;
            }
        }

        // ---- 合同单价匹配 ----
        // 如果 OCR 没识别到单价（现在默认不识别），按「供应商 + 物资名称」去合同材料表里找约定单价。
        // 找到就回填单价与单位；如果 OCR 也没识别到供应商，顺带回填供应商名。
        var contractItem = DatabaseService.Instance.MatchContractPrice(row.Supplier, row.MaterialName);
        if (contractItem is not null)
        {
            // 只有单价为 0 时才覆盖，避免 OCR 万一识别到了被错误覆盖
            if (row.UnitPrice == 0)
            {
                row.UnitPrice = contractItem.UnitPrice;
            }

            // 如果合同里的单位也明确，且 OCR 推断的单位为空或不一致，用合同里的
            if (!string.IsNullOrWhiteSpace(contractItem.Unit))
            {
                row.Unit = contractItem.Unit;
            }

            // OCR 没识别到供应商时，用合同里的供应商名回填
            if (string.IsNullOrWhiteSpace(row.Supplier))
            {
                row.Supplier = contractItem.SupplierName;
            }

            StatusMessage = $"已匹配到合同单价 {contractItem.UnitPrice} 元/{contractItem.Unit}。";
            _logger.LogInformation("合同单价匹配成功，供应商={Supplier}，材料={Material}，单价={Price}。",
                contractItem.SupplierName, row.MaterialName, contractItem.UnitPrice);
        }
        else
        {
            StatusMessage = "OCR 识别字段已填入。合同材料表未找到该材料的约定单价，请手动填写。";
            _logger.LogInformation("OCR 识别字段已填入物资台账新增行，材料={Name}，未匹配到合同单价。", row.MaterialName);
        }
    }

    /// <summary>从文本中提取数值，无法解析时返回 0。</summary>
    /// <param name="value">数值文本，可能带单位（如「12.5 吨」「12.5吨」）。</param>
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

    /// <summary>把表格内直接输入的日期文本规范成 yyyy-MM-dd；无法解析时回退当天。</summary>
    /// <param name="value">原始日期文本。</param>
    /// <returns>yyyy-MM-dd 格式日期。</returns>
    private static string NormalizeDate(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToString("yyyy-MM-dd")
            : DateTime.Now.ToString("yyyy-MM-dd");
    }

    /// <summary>订阅单条记录的数量/单价变化，使表格内编辑后合计立即刷新。</summary>
    /// <param name="item">刚加入集合的记录。</param>
    private void AttachTotalsTracking(MaterialLedger item)
    {
        item.PropertyChanged += OnLedgerPropertyChanged;
    }

    /// <summary>取消单条记录的订阅。</summary>
    /// <param name="item">要移除的记录。</param>
    private void DetachTotalsTracking(MaterialLedger item)
    {
        item.PropertyChanged -= OnLedgerPropertyChanged;
    }

    /// <summary>
    /// 取消当前集合中所有记录的订阅。Load 每次都会重建集合对象，
    /// 不先解绑会导致旧对象残留事件引用，既泄漏内存又可能触发多次重算。
    /// </summary>
    private void DetachTotalsTracking()
    {
        foreach (var item in Ledgers)
        {
            item.PropertyChanged -= OnLedgerPropertyChanged;
        }
    }

    /// <summary>记录属性变化时按需重算合计。</summary>
    /// <param name="sender">发生变化的记录。</param>
    /// <param name="e">属性变化参数。</param>
    private void OnLedgerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MaterialLedger.Quantity) or nameof(MaterialLedger.UnitPrice))
        {
            RecalculateTotals();
        }
    }
}
