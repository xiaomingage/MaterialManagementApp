using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.ViewModels;

/// <summary>
/// 物资台账页面（地材台账 → 台账明细）的视图模型。
/// 上半部分是日期区间与物资名称筛选条件；下半部分的 DataGrid 直接承载数据录入：
/// 点击表格空白处即在首行插入草稿行（浅绿底标记，支持连续多条），
/// 已入库的行离开编辑即自动保存（Excel 式），草稿行由工具栏「保存」批量校验入库。
/// 金额由 数量 × 单价 自动计算，不手工录入。
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

    /// <summary>
    /// 离开确认回调：翻页 / 查询 / 刷新 / 重置等重新加载动作发生且存在未保存草稿行时，
    /// 由页面弹出「离开将丢弃」确认弹窗（确认离开返回 true）。
    /// 视图模型无法直接操作 UI，故以委托注入；未注入时（如无 UI 环境）直接放行。
    /// </summary>
    public Func<Task<bool>>? ConfirmDiscardDraftsAsync;

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

    /// <summary>表格中是否存在尚未入库的草稿行（浅绿底标记的行）。</summary>
    public bool HasDrafts => Ledgers.Any(x => x.IsNewRow);

    /// <summary>当前是否启用了任一筛选条件（用于区分提示文案）。</summary>
    private bool IsFilterActive =>
        StartDate.HasValue
        || EndDate.HasValue
        || !string.Equals(SelectedMaterialName, AllMaterialNames, StringComparison.Ordinal);

    /// <summary>
    /// 按当前筛选条件从数据库分页加载台账记录，刷新物资名称下拉选项并重算合计。
    /// 先统计总条数求出总页数，再按当前页取一页数据；合计仍按当前页已加载的行计算。
    /// 注意：不负责草稿行保护——用户主动触发的加载走 <see cref="RefreshGuardedAsync"/> 等带确认的命令，
    /// 程序内部刷新（如保存成功后）直接调用本方法。
    /// </summary>
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

    /// <summary>
    /// 「刷新」：存在未保存草稿行时先经页面弹窗确认，确认后丢弃草稿并重新加载。
    /// </summary>
    [RelayCommand]
    private async Task RefreshGuardedAsync()
    {
        if (!await EnsureDraftsDiscardedAsync())
        {
            return;
        }

        Load();
    }

    /// <summary>翻到上一页（有草稿行时先确认离开）。</summary>
    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage <= 1 || !await EnsureDraftsDiscardedAsync())
        {
            return;
        }

        CurrentPage--;
        Load();
    }

    /// <summary>翻到下一页（有草稿行时先确认离开）。</summary>
    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPage >= TotalPages || !await EnsureDraftsDiscardedAsync())
        {
            return;
        }

        CurrentPage++;
        Load();
    }

    /// <summary>切换每页条数并回到第一页（有草稿行时先确认离开）。</summary>
    /// <param name="size">新的每页条数。</param>
    [RelayCommand]
    private async Task ChangePageSizeAsync(int size)
    {
        if (!await EnsureDraftsDiscardedAsync())
        {
            return;
        }

        PageSize = size is < 1 or > 1000 ? 20 : size;
        CurrentPage = 1;
        Load();
    }

    /// <summary>按当前筛选条件重新加载列表（「查询」按钮，有草稿行时先确认离开）。</summary>
    [RelayCommand]
    private async Task QueryAsync()
    {
        if (!await EnsureDraftsDiscardedAsync())
        {
            return;
        }

        _logger.LogInformation("正在按条件查询物料台账...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载（有草稿行时先确认离开）。</summary>
    [RelayCommand]
    private async Task ResetFilterAsync()
    {
        if (!await EnsureDraftsDiscardedAsync())
        {
            return;
        }

        StartDate = null;
        EndDate = null;
        SelectedMaterialName = AllMaterialNames;
        SelectedDept = null;
        SelectedPhase = null;
        CurrentPage = 1;
        Load();
    }

    /// <summary>
    /// 离开保护：存在未保存草稿行时先经页面弹窗确认，确认离开则丢弃全部草稿行。
    /// 无草稿行或页面未注入确认回调时直接放行。
    /// </summary>
    /// <returns>true 表示可以继续执行加载动作。</returns>
    private async Task<bool> EnsureDraftsDiscardedAsync()
    {
        if (!HasDrafts)
        {
            return true;
        }

        if (ConfirmDiscardDraftsAsync is null)
        {
            _logger.LogWarning("存在未保存草稿行但页面未注入离开确认回调，直接放行。");
            return true;
        }

        if (await ConfirmDiscardDraftsAsync())
        {
            DiscardDrafts();
            return true;
        }

        return false;
    }

    /// <summary>丢弃全部未入库的草稿行（仅移出集合，不涉及数据库）。</summary>
    private void DiscardDrafts()
    {
        for (var i = Ledgers.Count - 1; i >= 0; i--)
        {
            var draft = Ledgers[i];
            if (draft.IsNewRow)
            {
                DetachTotalsTracking(draft);
                Ledgers.RemoveAt(i);
            }
        }

        RecalculateTotals();
        _logger.LogInformation("已丢弃全部未保存的草稿行。");
    }

    /// <summary>
    /// 新增：在表格第一行插入一条全编辑状态的草稿行（浅绿底标记，界面由页面负责进入编辑）。
    /// 支持连续多条草稿行批量录入，不移除已有草稿；该行不写数据库，
    /// 只有点击工具栏「保存」才会整体校验入库。
    /// </summary>
    [RelayCommand]
    public void NewRow()
    {
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

        StatusMessage = "已在表格首行插入草稿行（浅绿底），填写完成后点击上方「保存」入库。";
        _logger.LogInformation("已在表格首行插入草稿台账行，当前列表 {Count} 条。", Ledgers.Count);
    }

    /// <summary>
    /// 批量保存全部草稿行：先整体校验（名称必填、数量/单价非负），任一行不合法都不落库，
    /// 草稿原样保留供修正并弹出失败提示；全部合法时逐行写入数据库，最后统一刷新一次列表。
    /// </summary>
    [RelayCommand]
    public void SaveAllDrafts()
    {
        var drafts = Ledgers.Where(x => x.IsNewRow).ToList();
        if (drafts.Count == 0)
        {
            StatusMessage = "没有待保存的新增行。";
            return;
        }

        var invalid = drafts.FirstOrDefault(d =>
            string.IsNullOrWhiteSpace(d.MaterialName) || d.Quantity < 0 || d.UnitPrice < 0);
        if (invalid is not null)
        {
            StatusMessage = string.IsNullOrWhiteSpace(invalid.MaterialName)
                ? "有草稿行未填写物资名称，未保存；请补全后重试。"
                : $"「{invalid.MaterialName}」行数量或单价为负数，未保存；请修正后重试。";
            _logger.LogWarning("批量保存台账被拒绝：{Reason}。", StatusMessage);
            SaveFailed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            foreach (var draft in drafts)
            {
                // 表格内直接编辑的日期是自由文本，落库前统一规范成 yyyy-MM-dd，无法解析时回退当天
                draft.EntryDate = NormalizeDate(draft.EntryDate);
                draft.Amount = Math.Round(draft.Quantity * draft.UnitPrice, 2);
                DatabaseService.Instance.AddMaterialLedger(draft);
                draft.IsNewRow = false;
            }

            // 新增记录默认进第一页；统一 Load 一次，避免逐行保存反复刷新
            CurrentPage = 1;
            Load();

            StatusMessage = $"已保存 {drafts.Count} 条新增台账。";
            _logger.LogInformation("批量保存台账草稿成功，共 {Count} 条。", drafts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量保存台账草稿失败。");
            StatusMessage = $"保存失败：{ex.Message}";
            SaveFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 把当前可见列表按入库日期倒序重排（日期相同的行保持原相对顺序）。
    /// 草稿行的日期文本先规范化，避免自由文本解析失败把行排到末尾。
    /// </summary>
    public void Resort()
    {
        foreach (var draft in Ledgers.Where(x => x.IsNewRow))
        {
            draft.EntryDate = NormalizeDate(draft.EntryDate);
        }

        var sorted = Ledgers
            .Select((row, index) => (Row: row, Index: index, Key: TryParseDate(row.EntryDate)))
            .OrderByDescending(x => x.Key)
            .ThenBy(x => x.Index)
            .Select(x => x.Row)
            .ToList();

        // 顺序未变化时直接返回，避免无谓的集合刷新打断表格状态
        var changed = false;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(sorted[i], Ledgers[i]))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return;
        }

        // 用 Move 保持元素实例不变，DataGrid 依据实例维护选中态与编辑态
        for (var i = 0; i < sorted.Count; i++)
        {
            var currentIndex = Ledgers.IndexOf(sorted[i]);
            if (currentIndex != i)
            {
                Ledgers.Move(currentIndex, i);
            }
        }
    }

    /// <summary>
    /// 保存一行：空白新增行执行插入后刷新列表，已有行执行原地更新（不重建集合）。
    /// 已有行的保存由表格 RowEditEnded 自动触发，此时 DataGrid 正在结束行编辑，
    /// 直接 Load 重建集合会触发 ObservableCollection 重入异常，因此只更新数据库与当前行对象；
    /// OCR 等非表格事件场景的新增路径仍走 Load 以刷新分页与下拉选项。
    /// </summary>
    /// <param name="row">被保存的记录。</param>
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
                // 新增的记录出现在第一页；Load 会重建集合并刷新下拉选项
                CurrentPage = 1;
                Load();
            }
            else
            {
                DatabaseService.Instance.UpdateMaterialLedger(row);
                // 原地更新：改过日期的行按日期倒序归位（顺序未变时 Resort 内部直接跳过）
                Resort();
            }

            RecalculateTotals();

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
    /// 先清掉完全空白的草稿行（避免残留空行占用表格），保留用户已填写内容的草稿；
    /// 「未识别」占位文本与空值都会跳过，避免污染待保存数据。
    /// </summary>
    /// <param name="fields"><see cref="ReceiptFieldExtractor.Extract"/> 的抽取结果。</param>
    public void ApplyOcrFields(IReadOnlyList<ReceiptField> fields)
    {
        // 移除没有任何内容的草稿行后，新建一条干净的行供 OCR 字段填入
        RemoveBlankDrafts();
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

    /// <summary>解析日期文本用于排序；无法解析时回退 DateTime.MinValue（排到末尾）。</summary>
    /// <param name="value">原始日期文本。</param>
    /// <returns>解析出的日期。</returns>
    private static DateTime TryParseDate(string value)
    {
        return DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;
    }

    /// <summary>判断草稿行是否完全空白（所有录入列都没有内容），供 OCR 填入前清理。</summary>
    /// <param name="row">待判断的行。</param>
    /// <returns>完全空白返回 true。</returns>
    private static bool IsBlankDraft(MaterialLedger row)
    {
        return string.IsNullOrWhiteSpace(row.MaterialName)
            && string.IsNullOrWhiteSpace(row.DocNo)
            && string.IsNullOrWhiteSpace(row.Specification)
            && string.IsNullOrWhiteSpace(row.Unit)
            && string.IsNullOrWhiteSpace(row.Handler)
            && string.IsNullOrWhiteSpace(row.ProjectAddress)
            && string.IsNullOrWhiteSpace(row.Remark)
            && string.IsNullOrWhiteSpace(row.ProjectDept)
            && string.IsNullOrWhiteSpace(row.ProjectPhase)
            && row.Quantity == 0
            && row.UnitPrice == 0;
    }

    /// <summary>移除全部完全空白的草稿行；用户已开始填写内容的草稿行保持原样。</summary>
    private void RemoveBlankDrafts()
    {
        for (var i = Ledgers.Count - 1; i >= 0; i--)
        {
            var draft = Ledgers[i];
            if (draft.IsNewRow && IsBlankDraft(draft))
            {
                DetachTotalsTracking(draft);
                Ledgers.RemoveAt(i);
            }
        }
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
