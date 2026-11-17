using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MaterialManagementApp.Models;

namespace MaterialManagementApp.Services;

/// <summary>
/// 施工进度聚合服务：把数据库中的 <see cref="SiteProgressDaily"/> 记录整理成地图气泡数据。
/// 状态语义只来自「当日有无作业 + 连续无更新天数」，不伪造任何进度百分比：
/// 当天有记录 → 绿色「有作业」；1–2 天无更新 → 黄色「停滞」；3 天及以上 → 红色「中断」。
/// </summary>
public sealed class SiteProgressService
{
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
    /// 生成推送给高德地图 WebView2 的 JSON：仅包含工点点位数据。
    /// 高德 Key 与安全密钥走 URL 查询参数注入（见 DashboardPage 与 map.html），不在此重复传递。
    /// </summary>
    /// <returns>可直接交给 PostWebMessageAsJson 的 JSON 字符串。</returns>
    public string BuildMapPayload()
    {
        var payload = new
        {
            type = "map-data",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            sites = BuildMapSites(DateTime.Today)
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// 按工点聚合最近记录与当日记录，生成气泡所需的语义与状态色。
    /// </summary>
    /// <param name="today">判定「当日」使用的日期。</param>
    /// <returns>工点点位列表；无数据时返回空列表。</returns>
    public List<SiteProgressMapSite> BuildMapSites(DateTime today)
    {
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var latestRows = DatabaseService.Instance.GetLatestSiteProgress();
        var todayRows = DatabaseService.Instance.GetSiteProgressForDate(todayText)
            .Where(row => row.IsLocated)
            .GroupBy(row => row.SiteName)
            .ToDictionary(group => group.Key, group => group.ToList());

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

            string color;
            string statusText;
            if (daysSince <= 0)
            {
                color = "#10B981";
                statusText = "有作业";
            }
            else if (daysSince <= 2)
            {
                color = "#F59E0B";
                statusText = $"停滞 {daysSince} 天";
            }
            else
            {
                color = "#EF4444";
                statusText = $"中断 {daysSince} 天";
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
                LastShotAt = latest.LastShotAt
            });
        }

        return sites;
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
