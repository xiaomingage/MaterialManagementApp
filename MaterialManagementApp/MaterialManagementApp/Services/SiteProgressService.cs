using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;

namespace MaterialManagementApp.Services;

/// <summary>
/// 施工进度聚合服务：把数据库中的 <see cref="SiteProgressDaily"/> 记录整理成地图气泡数据。
/// 状态语义只来自「当日有无作业 + 连续无更新天数 + 人工完工标记」，不伪造任何进度百分比：
/// 当天有记录 → 绿色「作业中」；1–2 天无更新 → 黄色「停滞」；3 天及以上 → 红色「中断」；
/// 人工标记完工 → 灰色「已完工」，优先于天数判定且不参与任何告警。
/// </summary>
public sealed class SiteProgressService
{
    /// <summary>已完工状态色（灰蓝），与三档告警色区分开。</summary>
    public const string CompletedColorHex = "#64748B";

    private static readonly Lazy<SiteProgressService> LazyInstance = new(() => new SiteProgressService());

    /// <summary>JSON 采用 camelCase，与前端 map.html 的字段名保持一致。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private SiteProgressService()
    {
    }

    /// <summary>全局唯一实例。</summary>
    public static SiteProgressService Instance => LazyInstance.Value;

    /// <summary>
    /// 生成推送给高德地图 WebView2 的 JSON：工点点位 + 固定参照点 + 中心点。
    /// 高德 Key 与安全密钥走 URL 查询参数注入（见 DashboardPage 与 map.html），不在此重复传递。
    /// </summary>
    /// <returns>可直接交给 PostWebMessageAsJson 的 JSON 字符串。</returns>
    public string BuildMapPayload()
    {
        var today = DateTime.Today;
        var sites = BuildMapSites(today);
        var fixedPoints = BuildFixedPoints(SettingsService.Instance.Settings);
        var center = ComputeCenter(sites, fixedPoints);

        var payload = new
        {
            type = "map-data",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            center,
            fixedPoints,
            sites
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// 按工点聚合最近记录与当日记录，生成气泡所需的语义与状态色。
    /// </summary>
    /// <param name="today">判定「当日」使用的日期。</param>
    /// <returns>工点点位列表；无数据时返回空列表。</returns>
    public List<SiteProgressMapSite> BuildMapSites(DateTime today) =>
        BuildMapSites(DatabaseService.Instance, today);

    /// <summary>
    /// 按工点聚合（可注入数据库，供单元测试使用）：状态覆盖表按 SiteKey 左连接，
    /// 已完工优先于天数判定；今日复工的工点附带角标标记。
    /// </summary>
    /// <param name="database">数据库服务。</param>
    /// <param name="today">判定「当日」使用的日期。</param>
    /// <returns>工点点位列表。</returns>
    internal List<SiteProgressMapSite> BuildMapSites(DatabaseService database, DateTime today)
    {
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var latestRows = database.GetLatestSiteProgress();
        var todayRows = database.GetSiteProgressForDate(todayText)
            .Where(row => row.IsLocated)
            .GroupBy(row => row.SiteName)
            .ToDictionary(group => group.Key, group => group.ToList());

        var overrides = LoadOverrides(database);
        var sites = new List<SiteProgressMapSite>();

        foreach (var group in latestRows.GroupBy(row => row.SiteName))
        {
            var rows = group.ToList();
            var latest = rows
                .OrderByDescending(row => row.Date, StringComparer.Ordinal)
                .ThenByDescending(row => row.LastShotAt, StringComparer.Ordinal)
                .First();

            if (Math.Abs(latest.Lng) < 1e-6 && Math.Abs(latest.Lat) < 1e-6)
            {
                continue;
            }

            todayRows.TryGetValue(latest.SiteName, out var todaySiteRows);
            todaySiteRows ??= new List<SiteProgressDaily>();

            var daysSince = CalculateDaysSince(today, latest.Date);
            overrides.TryGetValue(latest.SiteName, out var statusOverride);
            var isCompleted = statusOverride?.IsCompleted == true;
            var isRevived = SiteStatusService.IsRevivedToday(statusOverride, today);

            string color;
            string statusText;
            string lifecycleStatus;
            if (isCompleted)
            {
                // 已完工优先于天数判定：无论多少天无照片都不标红、不告警
                color = CompletedColorHex;
                statusText = "已完工";
                lifecycleStatus = "completed";
            }
            else if (daysSince <= 0)
            {
                color = "#10B981";
                statusText = "作业中";
                lifecycleStatus = "working";
            }
            else if (daysSince <= 2)
            {
                color = "#F59E0B";
                statusText = $"停滞 {daysSince} 天";
                lifecycleStatus = "stalled";
            }
            else
            {
                color = "#EF4444";
                statusText = $"中断 {daysSince} 天";
                lifecycleStatus = "interrupted";
            }

            sites.Add(new SiteProgressMapSite
            {
                SiteName = latest.SiteName,
                Lng = latest.Lng,
                Lat = latest.Lat,
                ProgressText = BuildProgressText(todaySiteRows),
                StatusText = statusText,
                ColorHex = color,
                DaysSinceActivity = daysSince,
                TodayPhotoCount = todaySiteRows.Sum(row => row.PhotoCount),
                LastShotAt = latest.LastShotAt,
                IsCompleted = isCompleted,
                IsRevived = isRevived,
                LifecycleStatus = lifecycleStatus
            });
        }

        return sites;
    }

    /// <summary>
    /// 由设置中的项目部 / 仓库坐标生成固定参照点；未配置或坐标非法的点直接跳过。
    /// </summary>
    /// <param name="settings">应用设置。</param>
    /// <returns>固定点列表（0–2 个）。</returns>
    internal static List<MapFixedPoint> BuildFixedPoints(AppSettings settings)
    {
        var points = new List<MapFixedPoint>();

        if (GeoCoordinate.TryParseConfigured(settings.ProjectDeptLng, settings.ProjectDeptLat, out var deptLng, out var deptLat))
        {
            points.Add(new MapFixedPoint(
                "projectDept",
                string.IsNullOrWhiteSpace(settings.ProjectDeptName) ? "项目部" : settings.ProjectDeptName.Trim(),
                deptLng,
                deptLat));
        }

        if (GeoCoordinate.TryParseConfigured(settings.WarehouseLng, settings.WarehouseLat, out var storeLng, out var storeLat))
        {
            points.Add(new MapFixedPoint(
                "warehouse",
                string.IsNullOrWhiteSpace(settings.WarehouseName) ? "仓库" : settings.WarehouseName.Trim(),
                storeLng,
                storeLat));
        }

        return points;
    }

    /// <summary>
    /// 计算地图中心点（C# 侧统一计算，map.html 只消费）：
    /// 1) 项目部坐标有效 → 项目部坐标、zoom 15；
    /// 2) 否则有工点 → 工点经纬度均值、zoom 11；
    /// 3) 否则 null（前端沿用默认中心）。
    /// </summary>
    /// <param name="sites">工点点位。</param>
    /// <param name="fixedPoints">固定参照点。</param>
    /// <returns>中心点或 null。</returns>
    internal static MapCenter? ComputeCenter(
        IReadOnlyCollection<SiteProgressMapSite> sites,
        IReadOnlyCollection<MapFixedPoint> fixedPoints)
    {
        var dept = fixedPoints.FirstOrDefault(point => point.Type == "projectDept");
        if (dept is not null)
        {
            return new MapCenter(dept.Lng, dept.Lat, 15);
        }

        if (sites.Count > 0)
        {
            var lng = Math.Round(sites.Average(site => site.Lng), 6);
            var lat = Math.Round(sites.Average(site => site.Lat), 6);
            return new MapCenter(lng, lat, 11);
        }

        return null;
    }

    /// <summary>读取状态覆盖字典；读取失败时退化为「不覆盖」，不影响地图渲染。</summary>
    private static Dictionary<string, SiteStatusOverride> LoadOverrides(DatabaseService database)
    {
        var result = new Dictionary<string, SiteStatusOverride>(StringComparer.Ordinal);
        try
        {
            foreach (var row in database.GetSiteStatusOverrides())
            {
                if (!string.IsNullOrWhiteSpace(row.SiteKey))
                {
                    result[row.SiteKey] = row;
                }
            }
        }
        catch (Exception)
        {
            // 覆盖表读取失败（如极旧库尚未迁移）时按纯天数判定，不阻断地图
        }

        return result;
    }

    /// <summary>计算距最近一次有记录的天数；日期无法解析时按「中断」处理，避免误显示为绿色。</summary>
    private static int CalculateDaysSince(DateTime today, string dateText)
    {
        if (!DateTime.TryParseExact(
                dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return 3;
        }

        var days = (today.Date - date.Date).Days;
        return Math.Max(0, days);
    }

    /// <summary>拼装当日进度语义：按类别列出张数，没有记录时明确写「今日无作业记录」。</summary>
    private static string BuildProgressText(List<SiteProgressDaily> todayRows)
    {
        if (todayRows.Count == 0)
        {
            return "今日无作业记录";
        }

        var parts = new List<string>();
        foreach (var group in todayRows.GroupBy(row => row.Category))
        {
            var count = group.Sum(row => row.PhotoCount);
            parts.Add(group.Key switch
            {
                SiteProgressCategory.EarthMaterialInbound => $"今日地材进场 {count} 张",
                SiteProgressCategory.SpoilTransfer => $"今日渣土转运 {count} 张",
                _ => $"今日作业记录 {count} 张（待确认）"
            });
        }

        return string.Join(" · ", parts);
    }
}
