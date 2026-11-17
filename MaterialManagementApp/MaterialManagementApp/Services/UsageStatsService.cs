using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>本地使用统计快照（只读副本，供状态栏仪表盘渲染）。</summary>
/// <param name="OcrTodaySuccess">今日 OCR 识别成功张数。</param>
/// <param name="OcrTodayFail">今日 OCR 识别失败张数。</param>
/// <param name="AiTodayCalls">今日在线 AI 调用次数。</param>
/// <param name="AiMonthCalls">本月在线 AI 调用次数。</param>
/// <param name="MapTodayLoads">今日地图成功初始化并加载脚本的估算次数。</param>
/// <param name="MapMonthLoads">本月地图加载估算次数。</param>
public sealed record UsageStatsSnapshot(
    int OcrTodaySuccess,
    int OcrTodayFail,
    int AiTodayCalls,
    int AiMonthCalls,
    int MapTodayLoads,
    int MapMonthLoads);

/// <summary>
/// 本地使用统计（单例）：OCR 成功/失败、在线 AI 调用、地图加载估算次数。
/// 仅用于底部状态栏仪表盘展示，不参与任何业务阻断。
/// 持久化为 <c>%LOCALAPPDATA%\MaterialSystem\usage-stats.json</c>（小 JSON，避免新增数据库迁移）；
/// 按自然日 / 自然月自动重置，跨天时先把上一日汇总写一条 Information 日志。
/// 识别高频调用时写入做了节流（约 3 秒一次），不会每张照片都落盘或刷日志。
/// </summary>
public sealed class UsageStatsService
{
    private static readonly Lazy<UsageStatsService> LazyInstance = new(() => new UsageStatsService());

    private static readonly ILogger Logger = AppLog.CreateLogger<UsageStatsService>();

    /// <summary>写入节流间隔：OCR 批量扫描时避免每张照片都写盘。</summary>
    private static readonly TimeSpan SaveThrottle = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();

    private UsageStatsData _data;

    private DateTime _lastSaveUtc = DateTime.MinValue;

    private UsageStatsService()
    {
        _data = Load();
    }

    /// <summary>全局唯一实例。</summary>
    public static UsageStatsService Instance => LazyInstance.Value;

    /// <summary>统计文件完整路径（%LOCALAPPDATA%\MaterialSystem\usage-stats.json）。</summary>
    public string StatsFilePath => Path.Combine(AppPaths.LocalAppDataRoot, "usage-stats.json");

    /// <summary>记录一次 OCR 识别成功。</summary>
    public void RecordOcrSuccess() => Mutate(data => data.OcrTodaySuccess++);

    /// <summary>记录一次 OCR 识别失败（所有引擎均失败时）。</summary>
    public void RecordOcrFailure() => Mutate(data => data.OcrTodayFail++);

    /// <summary>记录一次在线 AI 调用。</summary>
    public void RecordAiCall() => Mutate(data =>
    {
        data.AiTodayCalls++;
        data.AiMonthCalls++;
    });

    /// <summary>记录一次地图成功初始化（map.html 加载脚本并完成 initMap 后回发一次）。</summary>
    public void RecordMapLoad() => Mutate(data =>
    {
        data.MapTodayLoads++;
        data.MapMonthLoads++;
    });

    /// <summary>读取当前统计快照（会先处理跨天/跨月重置）。</summary>
    /// <returns>只读快照，永不为 null。</returns>
    public UsageStatsSnapshot Snapshot()
    {
        lock (_lock)
        {
            ApplyRollover(_data, DateTime.Now);
            return new UsageStatsSnapshot(
                _data.OcrTodaySuccess,
                _data.OcrTodayFail,
                _data.AiTodayCalls,
                _data.AiMonthCalls,
                _data.MapTodayLoads,
                _data.MapMonthLoads);
        }
    }

    /// <summary>
    /// 清零全部本地计数（设置页「重置统计」调用）。清空后立即落盘，重启不会恢复旧值。
    /// </summary>
    public void ResetAll()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            _data = new UsageStatsData
            {
                DateKey = now.ToString("yyyy-MM-dd"),
                MonthKey = now.ToString("yyyy-MM")
            };
            Save(force: true);
        }
    }

    /// <summary>在锁内累加计数、处理跨天重置并按节流落盘。</summary>
    /// <param name="change">对当前数据对象的修改动作。</param>
    private void Mutate(Action<UsageStatsData> change)
    {
        lock (_lock)
        {
            ApplyRollover(_data, DateTime.Now);
            change(_data);
            Save(force: false);
        }
    }

    /// <summary>
    /// 跨天 / 跨月重置：上一日（且当日有统计）先写一条汇总 Information，
    /// 然后清零日计数；月份变化时清零月计数。所有计数只在每日首次写入时汇总一次。
    /// </summary>
    /// <param name="data">当前统计数据。</param>
    /// <param name="now">当前时间。</param>
    private static void ApplyRollover(UsageStatsData data, DateTime now)
    {
        var dateKey = now.ToString("yyyy-MM-dd");
        var monthKey = now.ToString("yyyy-MM");

        if (!string.Equals(data.DateKey, dateKey, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(data.DateKey)
                && data.OcrTodaySuccess + data.OcrTodayFail + data.AiTodayCalls + data.MapTodayLoads > 0)
            {
                Logger.LogInformation(
                    "每日统计汇总（{Date}）：OCR 识别 {Success} 张、失败 {Fail} 张，在线 AI 调用 {Ai} 次，地图加载估算 {Map} 次。",
                    data.DateKey, data.OcrTodaySuccess, data.OcrTodayFail, data.AiTodayCalls, data.MapTodayLoads);
            }

            data.DateKey = dateKey;
            data.OcrTodaySuccess = 0;
            data.OcrTodayFail = 0;
            data.AiTodayCalls = 0;
            data.MapTodayLoads = 0;
        }

        if (!string.Equals(data.MonthKey, monthKey, StringComparison.Ordinal))
        {
            data.MonthKey = monthKey;
            data.AiMonthCalls = 0;
            data.MapMonthLoads = 0;
        }
    }

    /// <summary>按节流策略写盘；跨天重置与重置统计走 <paramref name="force"/> 立即写。</summary>
    /// <param name="force">true 时忽略节流立即写入。</param>
    private void Save(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastSaveUtc < SaveThrottle)
        {
            return;
        }

        _lastSaveUtc = now;
        try
        {
            var directory = Path.GetDirectoryName(StatsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = StatsFilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, StatsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 统计写盘失败不能影响扫描/识别主流程，最多丢失本次计数
            Logger.LogWarning(ex, "写入本地使用统计失败：{Path}", StatsFilePath);
        }
    }

    /// <summary>读取统计文件；缺失或损坏时返回当日空统计，不抛出。</summary>
    /// <returns>统计数据对象。</returns>
    private UsageStatsData Load()
    {
        try
        {
            if (File.Exists(StatsFilePath))
            {
                var loaded = JsonSerializer.Deserialize<UsageStatsData>(File.ReadAllText(StatsFilePath));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读取本地使用统计失败，已按空统计启动：{Path}", StatsFilePath);
        }

        var now = DateTime.Now;
        return new UsageStatsData
        {
            DateKey = now.ToString("yyyy-MM-dd"),
            MonthKey = now.ToString("yyyy-MM")
        };
    }

    /// <summary>usage-stats.json 的结构；字段全部使用默认值，兼容旧文件。</summary>
    private sealed class UsageStatsData
    {
        /// <summary>日计数归属日期（yyyy-MM-dd）。</summary>
        public string DateKey { get; set; } = string.Empty;

        /// <summary>月计数归属月份（yyyy-MM）。</summary>
        public string MonthKey { get; set; } = string.Empty;

        /// <summary>今日 OCR 成功张数。</summary>
        public int OcrTodaySuccess { get; set; }

        /// <summary>今日 OCR 失败张数。</summary>
        public int OcrTodayFail { get; set; }

        /// <summary>今日在线 AI 调用次数。</summary>
        public int AiTodayCalls { get; set; }

        /// <summary>本月在线 AI 调用次数。</summary>
        public int AiMonthCalls { get; set; }

        /// <summary>今日地图加载估算次数。</summary>
        public int MapTodayLoads { get; set; }

        /// <summary>本月地图加载估算次数。</summary>
        public int MapMonthLoads { get; set; }
    }
}
