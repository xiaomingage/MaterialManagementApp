using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.ViewModels;

/// <summary>
/// 小型机具台账页面（机具设备 → 小型机具明细）的视图模型。
/// 不区分队伍，全项目共用一张台账。上半部分是机具名称、编号与性能状态筛选条件；
/// 下半部分的 DataGrid 直接承载数据录入：点击「新增」会在表格首行插入一条全编辑状态的空白行，
/// 行末「保存」校验后写入数据库并刷新、「取消」则直接移除该行。
/// </summary>
public partial class SmallEquipmentViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<SmallEquipmentViewModel>();

    /// <summary>性能状态筛选中的「全部」选项文本。</summary>
    public const string AllPerformanceStatus = "全部";

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public SmallEquipmentViewModel()
    {
        NameKeyword = string.Empty;
        CodeKeyword = string.Empty;
        SelectedPerformanceStatus = AllPerformanceStatus;
        StatusMessage = string.Empty;
        TotalText = "合计：0 条记录";
        CurrentPage = 1;
        PageSize = 20;
    }

    /// <summary>台账记录集合，绑定到 DataGrid。</summary>
    public ObservableCollection<SmallEquipment> Items { get; } = new();

    /// <summary>性能状态下拉选项，首项固定为「全部」。</summary>
    public ObservableCollection<string> PerformanceStatusOptions { get; } = new() { AllPerformanceStatus, "良好", "待修", "报废" };

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SmallEquipment? _selectedItem;

    /// <summary>筛选：机具名称关键字，空白表示不限制。</summary>
    [ObservableProperty]
    private string _nameKeyword;

    /// <summary>筛选：机具编号关键字，空白表示不限制。</summary>
    [ObservableProperty]
    private string _codeKeyword;

    /// <summary>筛选：当前选中的性能状态，默认「全部」。</summary>
    [ObservableProperty]
    private string _selectedPerformanceStatus;

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
        !string.IsNullOrWhiteSpace(NameKeyword)
        || !string.IsNullOrWhiteSpace(CodeKeyword)
        || !string.Equals(SelectedPerformanceStatus, AllPerformanceStatus, StringComparison.Ordinal);

    /// <summary>按当前筛选条件从数据库加载小型机具台账记录并重算合计。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载小型设备数据...");
        try
        {
            // 空白关键字不加名称/编号条件
            var nameKeyword = string.IsNullOrWhiteSpace(NameKeyword) ? null : NameKeyword;
            var codeKeyword = string.IsNullOrWhiteSpace(CodeKeyword) ? null : CodeKeyword;
            // 下拉框选「全部」时不加性能状态条件
            var performanceStatus = string.Equals(SelectedPerformanceStatus, AllPerformanceStatus, StringComparison.Ordinal)
                ? null
                : SelectedPerformanceStatus;

            TotalCount = DatabaseService.Instance.CountSmallEquipments(nameKeyword, codeKeyword, performanceStatus);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetSmallEquipmentsPaged(CurrentPage, PageSize, nameKeyword, codeKeyword, performanceStatus);

            Items.Clear();
            foreach (var item in filtered)
            {
                Items.Add(item);
            }

            UpdateTotal();
            StatusMessage = IsFilterActive
                ? $"已加载 {Items.Count} 条记录（筛选已启用）。"
                : $"已加载 {Items.Count} 条记录。";
            _logger.LogInformation("小型设备数据加载完成，共 {Count} 条。", Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载小型设备数据失败。");
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
        _logger.LogInformation("正在按条件查询小型机具台账...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        NameKeyword = string.Empty;
        CodeKeyword = string.Empty;
        SelectedPerformanceStatus = AllPerformanceStatus;
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

        var row = new SmallEquipment
        {
            // 性能状态默认「良好」，其余字段保持空白由用户直接在表格里填写
            PerformanceStatus = "良好",
            IsNewRow = true
        };

        Items.Insert(0, row);
        UpdateTotal();

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白小型机具行，当前列表 {Count} 条。", Items.Count);
    }

    /// <summary>保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。</summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(SmallEquipment? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Name))
        {
            StatusMessage = "机具名称不能为空，未保存。";
            _logger.LogWarning("保存小型设备被拒绝：机具名称为空。");
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            if (isInsert)
            {
                DatabaseService.Instance.AddSmallEquipment(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdateSmallEquipment(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert
                ? $"已新增小型机具：{row.Name}。"
                : $"已保存小型机具：{row.Name}。";
            _logger.LogInformation("保存小型设备成功，Id={Id}，名称={Name}，新增={IsInsert}。", row.Id, row.Name, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存小型设备失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(SmallEquipment? row)
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
    public void DeleteRow(SmallEquipment? row)
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
                DatabaseService.Instance.DeleteSmallEquipment(row.Id);
            }

            UpdateTotal();
            StatusMessage = "已删除。";
            _logger.LogInformation("删除小型设备成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除小型设备失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>用系统默认程序打开指定记录的机具照片。</summary>
    /// <param name="row">要查看照片的记录；为 null 或未附照片时给出提示。</param>
    public void OpenPhoto(SmallEquipment? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.ImagePath))
        {
            StatusMessage = "该记录未附照片。";
            return;
        }

        try
        {
            if (!File.Exists(row.ImagePath))
            {
                StatusMessage = "照片文件不存在，可能已被移动或删除。";
                return;
            }

            // 交给系统关联程序打开，不必自己实现图片查看器；UseShellExecute 才能用默认程序打开
            Process.Start(new ProcessStartInfo(row.ImagePath) { UseShellExecute = true });
            StatusMessage = "已用默认程序打开照片。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开照片失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部记录，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部小型机具记录。</returns>
    public List<SmallEquipment> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetSmallEquipments()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条记录是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的小型机具记录。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(SmallEquipment item)
    {
        if (!string.IsNullOrWhiteSpace(NameKeyword)
            && (item.Name is null || !item.Name.Contains(NameKeyword.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(CodeKeyword)
            && (item.Code is null || !item.Code.Contains(CodeKeyword.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.Equals(SelectedPerformanceStatus, AllPerformanceStatus, StringComparison.Ordinal)
            && !string.Equals(item.PerformanceStatus, SelectedPerformanceStatus, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>重算合计文本。</summary>
    private void UpdateTotal()
    {
        TotalText = $"合计：{Items.Count} 条记录";
    }
}
