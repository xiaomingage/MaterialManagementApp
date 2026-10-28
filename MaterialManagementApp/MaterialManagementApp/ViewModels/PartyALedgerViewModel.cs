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
/// 甲供物资台账页面的视图模型。由原「入库验收 / 出库领用 / 退库记录 / 库存查询」四个页面合并而来：
/// 同一条 PartyAMaterial 记录承载申请、入库、出库、退库四个环节数量，库存量为派生值，
/// 因此合并为一个可编辑表格即可覆盖全部环节。上方为物资名称与记录日期筛选，
/// 下方表格支持行内新增（首行插入空白行）、行内编辑保存与删除。
/// </summary>
public partial class PartyALedgerViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<PartyALedgerViewModel>();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public PartyALedgerViewModel()
    {
        SelectedMaterialName = AllMaterialNames;
        StatusMessage = string.Empty;
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>物资名称筛选中的「全部」选项文本。</summary>
    public const string AllMaterialNames = "全部";

    /// <summary>甲供物资记录集合，绑定到 DataGrid。加载后按记录日期倒序排列。</summary>
    public ObservableCollection<PartyAMaterial> Materials { get; } = new();

    /// <summary>物资名称下拉选项，首项固定为「全部」，其余为库中已存在的物资名称。</summary>
    public ObservableCollection<string> MaterialNameOptions { get; } = new() { AllMaterialNames };

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PartyAMaterial? _selectedMaterial;

    /// <summary>筛选：开始日期（含当天）。为 null 表示不限制下界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _startDate;

    /// <summary>筛选：结束日期（含当天）。为 null 表示不限制上界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _endDate;

    /// <summary>筛选：当前选中的物资名称，默认「全部」。</summary>
    [ObservableProperty]
    private string _selectedMaterialName;

    /// <summary>当前列表的入库数量合计。</summary>
    [ObservableProperty]
    private double _totalInbound;

    /// <summary>当前列表的出库数量合计。</summary>
    [ObservableProperty]
    private double _totalOutbound;

    /// <summary>当前列表的库存量合计。</summary>
    [ObservableProperty]
    private double _totalStock;

    /// <summary>状态提示文本。</summary>
    [ObservableProperty]
    private string _statusMessage;

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
    public bool HasSelection => SelectedMaterial is not null;

    /// <summary>当前是否启用了任一筛选条件（用于区分提示文案）。</summary>
    private bool IsFilterActive =>
        StartDate.HasValue
        || EndDate.HasValue
        || !string.Equals(SelectedMaterialName, AllMaterialNames, StringComparison.Ordinal);

    /// <summary>
    /// 按当前筛选条件从数据库分页加载甲供物资记录，刷新物资名称下拉选项并重算合计。
    /// 先统计总条数求出总页数，再按当前页取一页数据；合计仍按当前页已加载的行计算。
    /// </summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载甲供物资台账数据...");
        try
        {
            var dateFrom = StartDate?.ToString("yyyy-MM-dd");
            var dateTo = EndDate?.ToString("yyyy-MM-dd");
            // 下拉框选「全部」时不加名称条件
            var materialName = string.Equals(SelectedMaterialName, AllMaterialNames, StringComparison.Ordinal)
                ? null
                : SelectedMaterialName;

            TotalCount = DatabaseService.Instance.CountPartyAMaterials(dateFrom, dateTo, materialName);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetPartyAMaterialsPaged(CurrentPage, PageSize, dateFrom, dateTo, materialName);

            DetachTotalsTracking();
            Materials.Clear();

            foreach (var item in filtered)
            {
                AttachTotalsTracking(item);
                Materials.Add(item);
            }

            RefreshMaterialNameOptions();
            RecalculateTotals();

            StatusMessage = IsFilterActive
                ? $"已加载 {Materials.Count} 条甲供物资记录（筛选已启用）。"
                : $"已加载 {Materials.Count} 条甲供物资记录。";
            _logger.LogInformation("甲供物资台账数据加载完成，共 {Count} 条。", Materials.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载甲供物资台账数据失败。");
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
        _logger.LogInformation("正在按条件查询甲供物资台账...");
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
        var pending = Materials.FirstOrDefault(x => x.IsNewRow);
        if (pending is not null)
        {
            DetachTotalsTracking(pending);
            Materials.Remove(pending);
        }

        var row = new PartyAMaterial
        {
            // 记录日期给出当天默认值，其余字段保持空白由用户直接在表格里填写
            RecordDate = DateTime.Now.ToString("yyyy-MM-dd"),
            IsNewRow = true
        };

        AttachTotalsTracking(row);
        Materials.Insert(0, row);
        RecalculateTotals();

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白甲供物资行，当前列表 {Count} 条。", Materials.Count);
    }

    /// <summary>保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。</summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(PartyAMaterial? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.MaterialName))
        {
            StatusMessage = "物资名称不能为空，未保存。";
            _logger.LogWarning("保存甲供物资被拒绝：物资名称为空。");
            return;
        }

        // 边界防护：申请/入库/出库/退库数量不允许为负，防止负数污染库存余量（StockQty 为三者推算值）
        if (row.ApplyQty < 0 || row.InboundQty < 0 || row.OutboundQty < 0 || row.ReturnQty < 0)
        {
            StatusMessage = "申请、入库、出库、退库数量不能为负数，未保存。";
            _logger.LogWarning("保存甲供物资被拒绝：存在负数数量。入库={In}，出库={Out}，退库={Ret}。", row.InboundQty, row.OutboundQty, row.ReturnQty);
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            // 表格内直接编辑的日期是自由文本，落库前统一规范成 yyyy-MM-dd，无法解析时回退当天
            row.RecordDate = NormalizeDate(row.RecordDate);

            if (isInsert)
            {
                DatabaseService.Instance.AddPartyAMaterial(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdatePartyAMaterial(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert
                ? $"已新增甲供物资：{row.MaterialName}。"
                : $"已保存甲供物资：{row.MaterialName}。";
            _logger.LogInformation("保存甲供物资成功，Id={Id}，名称={Name}，新增={IsInsert}。", row.Id, row.MaterialName, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存甲供物资失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(PartyAMaterial? row)
    {
        if (row is null || !row.IsNewRow)
        {
            return;
        }

        DetachTotalsTracking(row);
        Materials.Remove(row);
        RecalculateTotals();

        StatusMessage = "已取消新增。";
        _logger.LogInformation("已移除未保存的表格新增行。");
    }

    /// <summary>删除指定行：从集合移除并同步删除数据库记录。</summary>
    /// <param name="row">表格中被点击「删除」的记录。</param>
    [RelayCommand]
    public void DeleteRow(PartyAMaterial? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            // 先解绑合计订阅再移出集合，避免残留事件引用导致内存泄漏与重复重算
            DetachTotalsTracking(row);
            Materials.Remove(row);

            // Id 为 0 表示尚未落库的行，此时无需访问数据库
            if (row.Id != 0)
            {
                DatabaseService.Instance.DeletePartyAMaterial(row.Id);
            }

            RecalculateTotals();
            StatusMessage = "已删除。";
            _logger.LogInformation("删除甲供物资成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除甲供物资失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部记录，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部记录。</returns>
    public List<PartyAMaterial> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetPartyAMaterials()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的记录。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(PartyAMaterial item)
    {
        if (StartDate.HasValue || EndDate.HasValue)
        {
            // RecordDate 以 yyyy-MM-dd 文本存储，解析失败说明是脏数据，直接排除以免混入结果
            if (!DateTime.TryParse(item.RecordDate, out var date))
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

        return true;
    }

    /// <summary>
    /// 依据数据库中物资名称列的去重取值刷新下拉选项，并保证当前选中项仍然有效。
    /// 直接查询列值而非依赖全量加载结果，分页后也能拿到完整的候选项。
    /// </summary>
    private void RefreshMaterialNameOptions()
    {
        var names = DatabaseService.Instance.GetDistinctValues("PartyAMaterial", "MaterialName");
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

    /// <summary>重算合计行：入库、出库与库存（Σ(入库-出库+退库)）三个合计值。</summary>
    private void RecalculateTotals()
    {
        TotalInbound = Math.Round(Materials.Sum(x => x.InboundQty), 2);
        TotalOutbound = Math.Round(Materials.Sum(x => x.OutboundQty), 2);
        TotalStock = Math.Round(Materials.Sum(x => x.StockQty), 2);
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

    /// <summary>订阅单条记录的数量变化，使表格内编辑后合计立即刷新。</summary>
    /// <param name="item">刚加入集合的记录。</param>
    private void AttachTotalsTracking(PartyAMaterial item)
    {
        item.PropertyChanged += OnMaterialPropertyChanged;
    }

    /// <summary>取消单条记录的订阅。</summary>
    /// <param name="item">要移除的记录。</param>
    private void DetachTotalsTracking(PartyAMaterial item)
    {
        item.PropertyChanged -= OnMaterialPropertyChanged;
    }

    /// <summary>
    /// 取消当前集合中所有记录的订阅。Load 每次都会重建集合对象，
    /// 不先解绑会导致旧对象残留事件引用，既泄漏内存又可能触发多次重算。
    /// </summary>
    private void DetachTotalsTracking()
    {
        foreach (var item in Materials)
        {
            item.PropertyChanged -= OnMaterialPropertyChanged;
        }
    }

    /// <summary>记录属性变化时按需重算合计。</summary>
    /// <param name="sender">发生变化的记录。</param>
    /// <param name="e">属性变化参数。</param>
    private void OnMaterialPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PartyAMaterial.InboundQty)
            or nameof(PartyAMaterial.OutboundQty)
            or nameof(PartyAMaterial.ReturnQty))
        {
            RecalculateTotals();
        }
    }
}
