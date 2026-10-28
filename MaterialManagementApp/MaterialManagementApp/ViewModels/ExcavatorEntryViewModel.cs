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
/// 挖机进场资料页面（机具设备 → 机具入场）的视图模型（表1：挖机进场资料模版）。
/// 上半部分是进场日期区间与设备名称筛选条件；下半部分的 DataGrid 直接承载 18 个字段的录入：
/// 点击「新增」会在表格首行插入一条全编辑状态的空白行，行末「保存」校验后写入数据库并刷新、
/// 「取消」则直接移除该行。工具栏「导出」对当前选中行按模板生成 Word 验收单。
/// </summary>
public partial class ExcavatorEntryViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<ExcavatorEntryViewModel>();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public ExcavatorEntryViewModel()
    {
        NameKeyword = string.Empty;
        StatusMessage = string.Empty;
        TotalText = "合计：0 条记录";
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>进场资料集合，绑定到 DataGrid。</summary>
    public ObservableCollection<ExcavatorEntry> Items { get; } = new();

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ExcavatorEntry? _selectedEntry;

    /// <summary>筛选：开始日期（含当天，按进场日期比较）。为 null 表示不限制下界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _startDate;

    /// <summary>筛选：结束日期（含当天，按进场日期比较）。为 null 表示不限制上界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _endDate;

    /// <summary>筛选：设备名称关键字，空白表示不限制。</summary>
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

    /// <summary>是否已选中某行（供工具栏「删除」「导出」使用）。</summary>
    public bool HasSelection => SelectedEntry is not null;

    /// <summary>当前是否启用了任一筛选条件（用于区分提示文案）。</summary>
    private bool IsFilterActive =>
        StartDate.HasValue
        || EndDate.HasValue
        || !string.IsNullOrWhiteSpace(NameKeyword);

    /// <summary>按当前筛选条件从数据库加载进场资料并重算合计。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载挖掘机进出场数据...");
        try
        {
            var dateFrom = StartDate?.ToString("yyyy-MM-dd");
            var dateTo = EndDate?.ToString("yyyy-MM-dd");
            // 空白关键字不加设备名称条件
            var nameKeyword = string.IsNullOrWhiteSpace(NameKeyword) ? null : NameKeyword;

            TotalCount = DatabaseService.Instance.CountExcavatorEntries(dateFrom, dateTo, nameKeyword);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetExcavatorEntriesPaged(CurrentPage, PageSize, dateFrom, dateTo, nameKeyword);

            Items.Clear();
            foreach (var item in filtered)
            {
                Items.Add(item);
            }

            UpdateTotal();
            StatusMessage = IsFilterActive
                ? $"已加载 {Items.Count} 条记录（筛选已启用）。"
                : $"已加载 {Items.Count} 条记录。";
            _logger.LogInformation("挖掘机进出场数据加载完成，共 {Count} 条。", Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载挖掘机进出场数据失败。");
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
        _logger.LogInformation("正在按条件查询挖机进场资料...");
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
            Items.Remove(pending);
        }

        var row = new ExcavatorEntry
        {
            // 验收与进场日期给出当天默认值，其余字段保持空白由用户直接在表格里填写
            AcceptanceDate = DateTime.Now.ToString("yyyy-MM-dd"),
            EntryDate = DateTime.Now.ToString("yyyy-MM-dd"),
            IsNewRow = true
        };

        Items.Insert(0, row);
        UpdateTotal();

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白进场资料行，当前列表 {Count} 条。", Items.Count);
    }

    /// <summary>保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。</summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(ExcavatorEntry? row)
    {
        if (row is null)
        {
            return;
        }

        // 设备名称是识别一条进场资料的最小信息，为空则没有保存价值
        if (string.IsNullOrWhiteSpace(row.EquipmentName))
        {
            StatusMessage = "设备名称不能为空，未保存。";
            _logger.LogWarning("保存挖掘机进出场被拒绝：设备名称为空。");
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            // 表格内直接编辑的日期是自由文本，落库前统一规范成 yyyy-MM-dd，无法解析时回退当天
            row.AcceptanceDate = NormalizeDate(row.AcceptanceDate);
            row.EntryDate = NormalizeDate(row.EntryDate);
            if (!string.IsNullOrWhiteSpace(row.FactoryDate))
            {
                row.FactoryDate = NormalizeDate(row.FactoryDate);
            }

            if (isInsert)
            {
                DatabaseService.Instance.AddExcavatorEntry(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdateExcavatorEntry(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert
                ? $"已新增进场资料：{row.EquipmentName}。"
                : $"已保存进场资料：{row.EquipmentName}。";
            _logger.LogInformation("保存挖掘机进出场成功，Id={Id}，设备={Name}，新增={IsInsert}。", row.Id, row.EquipmentName, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存挖掘机进出场失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(ExcavatorEntry? row)
    {
        if (row is null || !row.IsNewRow)
        {
            return;
        }

        Items.Remove(row);
        UpdateTotal();

        StatusMessage = "已取消新增。";
        _logger.LogInformation("已移除未保存的表格新增行。");
    }

    /// <summary>删除指定行：从集合移除并同步删除数据库记录。</summary>
    /// <param name="row">表格中被点击「删除」的记录。</param>
    [RelayCommand]
    public void DeleteRow(ExcavatorEntry? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            Items.Remove(row);

            // Id 为 0 表示尚未落库的行，此时无需访问数据库
            if (row.Id != 0)
            {
                DatabaseService.Instance.DeleteExcavatorEntry(row.Id);
            }

            UpdateTotal();
            StatusMessage = "已删除。";
            _logger.LogInformation("删除挖掘机进出场成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除挖掘机进出场失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部记录，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部进场资料记录。</returns>
    public List<ExcavatorEntry> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetExcavatorEntries()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的进场资料记录。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(ExcavatorEntry item)
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

        if (!string.IsNullOrWhiteSpace(NameKeyword)
            && (item.EquipmentName is null || !item.EquipmentName.Contains(NameKeyword.Trim(), StringComparison.OrdinalIgnoreCase)))
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
        TotalText = $"合计：{Items.Count} 条记录";
    }
}
