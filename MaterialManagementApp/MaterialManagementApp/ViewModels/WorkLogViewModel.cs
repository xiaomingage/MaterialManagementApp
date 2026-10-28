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
/// 工作日志页面的视图模型。
/// 上半部分是记录日期区间与内容关键字筛选条件；下半部分的 DataGrid 直接承载数据录入：
/// 点击「新增」会在表格首行插入一条全编辑状态的空白行，行末「保存」校验后写入数据库并刷新、
/// 「取消」则直接移除该行。
/// </summary>
public partial class WorkLogViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到文件与 IDE 输出窗口。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<WorkLogViewModel>();

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把筛选默认值与提示文本放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public WorkLogViewModel()
    {
        ContentKeyword = string.Empty;
        StatusMessage = string.Empty;
        CurrentPage = 1;
        PageSize = 20;
        TotalText = "合计：0 条日志";
        WorkContent = string.Empty;
        SelectedWeather = WeatherOptions[0];

        // 日期与星期在页面构造时读取一次系统时间自动填充，用户无需手动选择
        var today = DateTime.Now;
        TodayDateText = today.ToString("yyyy年M月d日");
        TodayWeekdayText = ChineseWeekdays[(int)today.DayOfWeek];
        TodayDisplayText = $"今天是 {TodayDateText}　星期{TodayWeekdayText}";
    }

    /// <summary>中文星期汉字，索引与 <see cref="DayOfWeek"/> 一致（0 为星期日）。</summary>
    private static readonly string[] ChineseWeekdays = { "日", "一", "二", "三", "四", "五", "六" };

    /// <summary>工作日志集合，绑定到 DataGrid。加载后按日期倒序、Id 倒序排列。</summary>
    public ObservableCollection<WorkLog> Items { get; } = new();

    /// <summary>DataGrid 中当前选中的行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private WorkLog? _selectedItem;

    /// <summary>筛选：开始日期（含当天）。为 null 表示不限制下界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _startDate;

    /// <summary>筛选：结束日期（含当天）。为 null 表示不限制上界。</summary>
    [ObservableProperty]
    private DateTimeOffset? _endDate;

    /// <summary>筛选：日志内容关键字，空白表示不限制。</summary>
    [ObservableProperty]
    private string _contentKeyword;

    /// <summary>状态提示文本，用于向用户反馈操作结果或错误信息。</summary>
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

    /// <summary>天气下拉框的可选项，默认选中首项「晴」。</summary>
    public ObservableCollection<string> WeatherOptions { get; } = new() { "晴", "多云", "阴", "小雨", "大雨" };

    /// <summary>天气下拉框当前选中项，导出 Word 时写入模板。</summary>
    [ObservableProperty]
    private string _selectedWeather;

    /// <summary>今日工作内容，来自界面多行文本框，导出 Word 时写入模板正文单元格。</summary>
    [ObservableProperty]
    private string _workContent;

    /// <summary>今日日期文本（yyyy年M月d日），页面构造时自动取值。</summary>
    [ObservableProperty]
    private string _todayDateText;

    /// <summary>今日星期汉字（日/一/二/…），页面构造时自动取值。</summary>
    [ObservableProperty]
    private string _todayWeekdayText;

    /// <summary>界面上「今日」提示行的完整文本，由日期与星期拼成。</summary>
    [ObservableProperty]
    private string _todayDisplayText;

    /// <summary>是否已选中某行（供工具栏「删除」使用）。</summary>
    public bool HasSelection => SelectedItem is not null;

    /// <summary>当前是否启用了任一筛选条件（用于区分提示文案）。</summary>
    private bool IsFilterActive =>
        StartDate.HasValue
        || EndDate.HasValue
        || !string.IsNullOrWhiteSpace(ContentKeyword);

    /// <summary>
    /// 按当前筛选条件从数据库分页加载工作日志。
    /// 先统计总条数求出总页数，再按当前页取一页数据；合计文本用总条数表达。
    /// </summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载工作日志数据...");
        try
        {
            var dateFrom = StartDate?.ToString("yyyy-MM-dd");
            var dateTo = EndDate?.ToString("yyyy-MM-dd");
            // 空白关键字不加内容条件
            var contentKeyword = string.IsNullOrWhiteSpace(ContentKeyword) ? null : ContentKeyword;

            TotalCount = DatabaseService.Instance.CountWorkLogs(dateFrom, dateTo, contentKeyword);
            TotalPages = TotalCount == 0 ? 1 : (int)((TotalCount + PageSize - 1) / PageSize);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            var filtered = DatabaseService.Instance.GetWorkLogsPaged(CurrentPage, PageSize, dateFrom, dateTo, contentKeyword);

            Items.Clear();
            foreach (var item in filtered)
            {
                Items.Add(item);
            }

            TotalText = $"合计：{TotalCount} 条日志";
            StatusMessage = IsFilterActive
                ? $"已加载 {Items.Count} 条日志（筛选已启用）。"
                : $"已加载 {Items.Count} 条日志。";
            _logger.LogInformation("工作日志数据加载完成，共 {Count} 条。", Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载工作日志数据失败。");
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
        _logger.LogInformation("正在按条件查询工作日志...");
        CurrentPage = 1;
        Load();
    }

    /// <summary>清空全部筛选条件并重新加载。</summary>
    [RelayCommand]
    public void ResetFilter()
    {
        StartDate = null;
        EndDate = null;
        ContentKeyword = string.Empty;
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

        var row = new WorkLog
        {
            // 记录日期给出当天默认值，内容由用户直接在表格里填写
            LogDate = DateTime.Now.ToString("yyyy-MM-dd"),
            IsNewRow = true
        };

        Items.Insert(0, row);
        TotalText = $"合计：{Items.Count} 条日志";

        StatusMessage = "已在表格首行插入空白记录，请直接填写后点击该行末的「保存」。";
        _logger.LogInformation("已在表格首行插入空白日志行，当前列表 {Count} 条。", Items.Count);
    }

    /// <summary>保存一行：空白新增行执行插入，已有行执行更新；成功后重新加载列表。</summary>
    /// <param name="row">表格中被点击「保存」的记录。</param>
    public void SaveRow(WorkLog? row)
    {
        if (row is null)
        {
            return;
        }

        // 空内容没有记录价值，且会让列表出现无法辨认的空行，因此前置拦截
        if (string.IsNullOrWhiteSpace(row.Content))
        {
            StatusMessage = "日志内容不能为空，未保存。";
            _logger.LogWarning("保存工作日志被拒绝：内容为空。");
            return;
        }

        var isInsert = row.IsNewRow;

        try
        {
            // 表格内直接编辑的日期是自由文本，落库前统一规范成 yyyy-MM-dd，无法解析时回退当天
            row.LogDate = NormalizeDate(row.LogDate);
            row.Content = row.Content.Trim();

            if (isInsert)
            {
                DatabaseService.Instance.AddWorkLog(row);
                row.IsNewRow = false;
                // 新增的记录出现在第一页
                CurrentPage = 1;
            }
            else
            {
                DatabaseService.Instance.UpdateWorkLog(row);
            }

            Load();

            // Load 内部会改写提示文本，因此成功提示必须放在它之后，否则会被覆盖
            StatusMessage = isInsert ? "已新增日志。" : "已保存日志。";
            _logger.LogInformation("保存工作日志成功，Id={Id}，日期={LogDate}，新增={IsInsert}。", row.Id, row.LogDate, isInsert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存工作日志失败，Id={Id}。", row.Id);
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>取消新增：把尚未入库的空白行从表格中移除。</summary>
    /// <param name="row">表格中被点击「取消」的新增行。</param>
    public void CancelNewRow(WorkLog? row)
    {
        if (row is null || !row.IsNewRow)
        {
            return;
        }

        Items.Remove(row);
        TotalText = $"合计：{Items.Count} 条日志";

        StatusMessage = "已取消新增。";
        _logger.LogInformation("已移除未保存的表格新增行。");
    }

    /// <summary>删除指定行：从集合移除并同步删除数据库记录。</summary>
    /// <param name="row">表格中被点击「删除」的记录。</param>
    [RelayCommand]
    public void DeleteRow(WorkLog? row)
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
                DatabaseService.Instance.DeleteWorkLog(row.Id);
            }

            TotalText = $"合计：{TotalCount} 条日志";
            StatusMessage = "已删除。";
            _logger.LogInformation("删除工作日志成功，Id={Id}。", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除工作日志失败，Id={Id}。", row.Id);
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出用：返回满足当前筛选条件的全部日志，不受分页限制。
    /// 未保存的空白新增行由调用方按原逻辑再次排除。
    /// </summary>
    /// <returns>符合筛选条件的全部日志。</returns>
    public List<WorkLog> GetFilteredRowsForExport()
    {
        return DatabaseService.Instance.GetWorkLogs()
            .Where(MatchesFilter)
            .ToList();
    }

    /// <summary>判断一条日志是否符合当前筛选条件。</summary>
    /// <param name="item">待判断的日志。</param>
    /// <returns>符合条件返回 true。</returns>
    private bool MatchesFilter(WorkLog item)
    {
        if (StartDate.HasValue || EndDate.HasValue)
        {
            // LogDate 以 yyyy-MM-dd 文本存储，解析失败说明是脏数据，直接排除以免混入结果
            if (!DateTime.TryParse(item.LogDate, out var date))
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

        if (!string.IsNullOrWhiteSpace(ContentKeyword)
            && (item.Content is null || !item.Content.Contains(ContentKeyword.Trim(), StringComparison.OrdinalIgnoreCase)))
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
}
