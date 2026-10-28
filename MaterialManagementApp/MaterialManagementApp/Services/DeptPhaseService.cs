using System;
using System.Collections.Generic;
using System.Linq;

namespace MaterialManagementApp.Services;

/// <summary>
/// 「项目部 / 期数」维度的共享缓存服务（单例）。
/// 各页面的下拉框（录入单元格与筛选区）统一从这里取候选项，避免每个控件各自查库；
/// 设置页维护项目部/期数或调整适用栏目后调用 <see cref="Refresh"/> 刷新缓存，
/// 页面在导航进入时也会调用 <see cref="Refresh"/> 保证候选项是最新的。
/// </summary>
public sealed class DeptPhaseService
{
    /// <summary>筛选下拉框中「全部」选项的显示文本（项目部与期数共用）。</summary>
    public const string FilterAllText = "全部";

    /// <summary>期数筛选下拉框中「仅无期数」选项的显示文本（对应 ProjectPhase 为空的记录，如梧州项目）。</summary>
    public const string FilterBlankPhaseText = "无期数";

    /// <summary>录入下拉框中期数空白选项的显示文本，选中后记录存空串。</summary>
    public const string EntryBlankPhaseText = "无";

    private static readonly Lazy<DeptPhaseService> _instance = new(() => new DeptPhaseService());

    private readonly object _syncRoot = new();

    private List<string> _deptNames = new();
    private List<string> _phaseNames = new();
    private Dictionary<string, bool> _enabledTables = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    private DeptPhaseService()
    {
    }

    /// <summary>全局唯一实例。</summary>
    public static DeptPhaseService Instance => _instance.Value;

    /// <summary>刷新缓存：重新读取项目部、期数候选项与适用栏目配置。</summary>
    public void Refresh()
    {
        var depts = DatabaseService.Instance.GetProjectDepts()
            .Select(x => x.Name)
            .ToList();
        var phases = DatabaseService.Instance.GetProjectPhases()
            .Select(x => x.Name)
            .ToList();
        var enabled = DatabaseService.Instance.GetDeptPhaseApplicableTables()
            .ToDictionary(x => x.TableName, x => x.IsEnabled, StringComparer.OrdinalIgnoreCase);

        lock (_syncRoot)
        {
            _deptNames = depts;
            _phaseNames = phases;
            _enabledTables = enabled;
            _loaded = true;
        }
    }

    /// <summary>首次访问时惰性加载，避免每个下拉控件各自查库，也不要求调用方保证刷新顺序。</summary>
    private void EnsureLoaded()
    {
        lock (_syncRoot)
        {
            if (_loaded)
            {
                return;
            }
        }

        Refresh();
    }

    /// <summary>项目部候选项快照（按设置页显示顺序）。</summary>
    /// <returns>名称列表副本，调用方可安全修改。</returns>
    public IReadOnlyList<string> GetDeptNames()
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return _deptNames.ToList();
        }
    }

    /// <summary>期数候选项快照（按设置页显示顺序），不含空白选项。</summary>
    /// <returns>名称列表副本，调用方可安全修改。</returns>
    public IReadOnlyList<string> GetPhaseNames()
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return _phaseNames.ToList();
        }
    }

    /// <summary>项目部筛选下拉框候选项：首项「全部」，其后为全部项目部。</summary>
    /// <returns>筛选候选项列表。</returns>
    public IReadOnlyList<string> GetDeptFilterOptions()
    {
        var options = new List<string> { FilterAllText };
        options.AddRange(GetDeptNames());
        return options;
    }

    /// <summary>期数筛选下拉框候选项：首项「全部」，次项「无期数」，其后为全部期数。</summary>
    /// <returns>筛选候选项列表。</returns>
    public IReadOnlyList<string> GetPhaseFilterOptions()
    {
        var options = new List<string> { FilterAllText, FilterBlankPhaseText };
        options.AddRange(GetPhaseNames());
        return options;
    }

    /// <summary>期数录入下拉框候选项：首项固定为空白选项「无」（选中存空串），其后为全部期数。</summary>
    /// <returns>录入候选项列表。</returns>
    public IReadOnlyList<string> GetPhaseEntryOptions()
    {
        var options = new List<string> { EntryBlankPhaseText };
        options.AddRange(GetPhaseNames());
        return options;
    }

    /// <summary>判断指定业务表是否已启用「项目部 / 期数」维度（读缓存，不查库）。</summary>
    /// <param name="tableName">业务表名。</param>
    /// <returns>已启用返回 true；未知表名返回 false。</returns>
    public bool IsTableEnabled(string tableName)
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return _enabledTables.TryGetValue(tableName, out var enabled) && enabled;
        }
    }

    /// <summary>
    /// 判断一条业务记录是否符合当前维度筛选条件（供各页面 MatchesFilter 复用，统一三态期数语义）。
    /// </summary>
    /// <param name="selectedDept">筛选项目部；null 或空表示不限制。</param>
    /// <param name="selectedPhase">筛选期数；null 不限制、空串表示仅无期数、其余值精确匹配。</param>
    /// <param name="itemDept">记录的项目部取值。</param>
    /// <param name="itemPhase">记录的期数取值。</param>
    /// <returns>符合条件返回 true。</returns>
    public static bool MatchesDimension(string? selectedDept, string? selectedPhase, string itemDept, string itemPhase)
    {
        if (!string.IsNullOrWhiteSpace(selectedDept)
            && !string.Equals(itemDept, selectedDept, StringComparison.Ordinal))
        {
            return false;
        }

        if (selectedPhase is null)
        {
            return true;
        }

        if (selectedPhase.Length == 0)
        {
            // 「仅无期数」：空串与历史 NULL 都算无期数
            return string.IsNullOrEmpty(itemPhase);
        }

        return string.Equals(itemPhase, selectedPhase, StringComparison.Ordinal);
    }
}
