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
/// 物资采购明细页面（自购物资 → 物资采购明细）的视图模型。
/// 上方为采购日期区间与物资名称筛选；下方 DataGrid 直接承载数据录入：
/// 点击「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后写入数据库、
/// 「取消」直接移除。金额由 采购数量 × 单价 自动计算，不手工录入。
/// </summary>
public partial class SelfPurchaseDetailViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<SelfPurchaseDetailViewModel>();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public SelfPurchaseDetailViewModel()
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

    /// <summary>采购记录集合，绑定到 DataGrid。</summary>
    public ObservableCollection<SelfPurchasedMaterial> Items { get; } = new();

    /// <summary>物资名称下拉选项，首项固定为「全部」，其余为库中已存在的物资名称。</summary>
    public ObservableCollection<string> MaterialNameOptions { get; } = new() { AllMaterialNames };

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SelfPurchasedMaterial? _selectedMaterial;

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

    /// <summary>当前列表的采购数量合计。</summary>
    [ObservableProperty]
    private double _totalQuantity;

    /// <summary>当前列表的采购金额合计。</summary>
    [ObservableProperty]
    private double _totalAmount;

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
    /// 按当前筛选条件从数据库分页加载采购记录，刷新物资名称下拉选项并重算合计。
    /// 先统计总条数求出总页数，再按当前页取一页数据；合计仍按当前页已加载的行计算。
    /// </summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载自购材料明细数据...");
        try
        {
            var dateFrom = StartDate?.ToString("yyyy-MM-dd");
            var dateTo = EndDate?.ToString("yyyy-MM-dd");
            // 下拉框选「全部」时不加名称条件
            var materialName = string.Equals(SelectedMaterialName, AllMaterialNames, StringComparison.Ordinal)
                ? null
                : SelectedMaterialName;

            TotalCount = DatabaseService.Instance.CountSelfPurchasedMaterials(dateFrom, dateTo, materialName, SelectedDept, SelectedPhase);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetSelfPurchasedMaterialsPaged(CurrentPage, PageSize, dateFrom, dateTo, materialName, SelectedDept, SelectedPhase);

            DetachTotalsTracking();
            Items.Clear();

            foreach (var item in filtered)
            {
                // 历史数据可能采购量/单价非 0 但金额未写，加载时兜底校正一次
                if (item.Amount == 0 && item.PurchaseQty != 0 && item.UnitPrice != 0)
                {
                    item.Amount = Math.Round(item.PurchaseQty * item.UnitPrice, 2);
                }

                AttachTotalsTracking(item);
                Items.Add(item);
            }

            RefreshMaterialNameOptions();
            RecalculateTotals();

            StatusMessage = IsFilterActive
                ? $"已加载 {Items.Count} 条采购记录（筛选已启用）。"
                : $"已加载 {Items.Count} 条采购记录。";
            _logger.LogInformation("自购材料明细数据加载完成，共 {Count} 条。", Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载自购材料明细数据失败。");
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
        _logger.LogInformation("正在按条件查询自购材料明细...");
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
        var pending = Items.FirstOrDefault(x => x.IsNewRow);
        if (pending is not null)
        {
            DetachTotalsTracking(pending);
            Items.Remove(pending);
        }

        var row = new SelfPurchasedMaterial
        {
            // 日期给出当天默认值、计量单位取设置页默认值，其余字段保持空白由用户直接在表格里填写
            RecordDate = DateTime.Now.ToString("yyyy-MM-dd"),
            Unit = SettingsService.Instance.Settings.DefaultUnit,
            IsNewRow = true
        };

        AttachTotalsTracking(row);
        Items.Insert(0, row);
        RecalculateTotals();

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白采购行，当前列表 {Count} 条。", Items.Count);
    }

    /// <summary>保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。</summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(SelfPurchasedMaterial? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.MaterialName))
        {
            StatusMessage = "物资名称不能为空，未保存。";
            _logger.LogWarning("保存自购材料明细被拒绝：物资名称为空。");
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            // 表格内直接编辑的日期是自由文本，落库前统一规范成 yyyy-MM-dd，无法解析时回退当天
            row.RecordDate = NormalizeDate(row.RecordDate);
            row.Amount = Math.Round(row.PurchaseQty * row.UnitPrice, 2);

            if (isInsert)
            {
                DatabaseService.Instance.AddSelfPurchasedMaterial(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdateSelfPurchasedMaterial(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert
                ? $"已新增采购记录：{row.MaterialName}。"
                : $"已保存采购记录：{row.MaterialName}。";
            _logger.LogInformation("保存自购材料明细成功，Id={Id}，名称={Name}，新增={IsInsert}。", row.Id, row.MaterialName, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存自购材料明细失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(SelfPurchasedMaterial? row)
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
    public void DeleteRow(SelfPurchasedMaterial? row)
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
                DatabaseService.Instance.DeleteSelfPurchasedMaterial(row.Id);
            }

            RecalculateTotals();
            StatusMessage = "已删除。";
            _logger.LogInformation("删除自购材料明细成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除自购材料明细失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部记录，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部采购记录。</returns>
    public List<SelfPurchasedMaterial> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetSelfPurchasedMaterials()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的记录。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(SelfPurchasedMaterial item)
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

        return DeptPhaseService.MatchesDimension(SelectedDept, SelectedPhase, item.ProjectDept, item.ProjectPhase);
    }

    /// <summary>
    /// 依据数据库中物资名称列的去重取值刷新下拉选项，并保证当前选中项仍然有效。
    /// 直接查询列值而非依赖全量加载结果，分页后也能拿到完整的候选项。
    /// </summary>
    private void RefreshMaterialNameOptions()
    {
        var names = DatabaseService.Instance.GetDistinctValues("SelfPurchasedMaterial", "MaterialName");
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

    /// <summary>重算合计行：采购数量合计与采购金额合计（Σ(采购数量 × 单价) 现算）。</summary>
    private void RecalculateTotals()
    {
        TotalQuantity = Math.Round(Items.Sum(x => x.PurchaseQty), 2);
        TotalAmount = Math.Round(Items.Sum(x => x.PurchaseQty * x.UnitPrice), 2);
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
    private void AttachTotalsTracking(SelfPurchasedMaterial item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
    }

    /// <summary>取消单条记录的订阅。</summary>
    /// <param name="item">要移除的记录。</param>
    private void DetachTotalsTracking(SelfPurchasedMaterial item)
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
        if (e.PropertyName is nameof(SelfPurchasedMaterial.PurchaseQty) or nameof(SelfPurchasedMaterial.UnitPrice))
        {
            RecalculateTotals();
        }
    }
}
