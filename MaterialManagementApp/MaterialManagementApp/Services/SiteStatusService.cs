using System;
using System.Collections.Generic;
using MaterialManagementApp.Models;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>
/// 工点人工状态服务（单例）：维护「已完工 / 复工」覆盖，并在扫描入库后做复工检测。
/// 工点没有独立主数据表，<c>SiteKey</c> 始终等于 <c>SiteProgressDaily.SiteName</c>。
/// 状态优先级：已完工 &gt; 连续无更新天数；已完工工点不参与任何天数告警。
/// </summary>
public sealed class SiteStatusService
{
    private static readonly Lazy<SiteStatusService> LazyInstance = new(() => new SiteStatusService());

    private static readonly ILogger Logger = AppLog.CreateLogger<SiteStatusService>();

    private SiteStatusService()
    {
    }

    /// <summary>全局唯一实例。</summary>
    public static SiteStatusService Instance => LazyInstance.Value;

    /// <summary>
    /// 读取全部覆盖行并转为按 SiteKey 索引的字典；数据库异常时返回空字典（地图仍按天数判定）。
    /// </summary>
    /// <returns>SiteKey → 覆盖行。</returns>
    public Dictionary<string, SiteStatusOverride> GetOverrides()
    {
        var result = new Dictionary<string, SiteStatusOverride>(StringComparer.Ordinal);
        try
        {
            foreach (var row in DatabaseService.Instance.GetSiteStatusOverrides())
            {
                if (!string.IsNullOrWhiteSpace(row.SiteKey))
                {
                    result[row.SiteKey] = row;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读取工点状态覆盖失败，本次按天数判定。");
        }

        return result;
    }

    /// <summary>读取单个工点的覆盖行；不存在时返回 null。</summary>
    /// <param name="siteKey">工点聚合键。</param>
    /// <returns>覆盖行或 null。</returns>
    public SiteStatusOverride? GetOverride(string siteKey) =>
        GetOverrides().TryGetValue(siteKey, out var row) ? row : null;

    /// <summary>
    /// 人工标记工点已完工：写入覆盖行并记录 Information 日志（不记录照片内容）。
    /// </summary>
    /// <param name="siteKey">工点聚合键。</param>
    /// <param name="remark">完工备注（可选）。</param>
    public void MarkCompleted(string siteKey, string? remark)
    {
        if (string.IsNullOrWhiteSpace(siteKey))
        {
            return;
        }

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var previous = GetOverride(siteKey);
        DatabaseService.Instance.SaveSiteStatusOverride(new SiteStatusOverride
        {
            SiteKey = siteKey,
            Status = SiteLifecycleStatus.Completed,
            CompletedAt = now,
            Remark = (remark ?? string.Empty).Trim(),
            UpdatedAt = now
        });

        Logger.LogInformation(
            "工点状态变更：工点={Site}，原状态={Old}，新状态=已完工，时间={Time}，备注={Remark}。",
            siteKey,
            previous?.Status ?? "无覆盖（按天数判定）",
            now,
            string.IsNullOrWhiteSpace(remark) ? "（无）" : remark.Trim());
    }

    /// <summary>
    /// 撤销完工标记：删除覆盖行，工点恢复按「连续无更新天数」判定，并记录 Information 日志。
    /// </summary>
    /// <param name="siteKey">工点聚合键。</param>
    public void Revoke(string siteKey)
    {
        if (string.IsNullOrWhiteSpace(siteKey))
        {
            return;
        }

        var previous = GetOverride(siteKey);
        if (previous is null)
        {
            return;
        }

        DatabaseService.Instance.DeleteSiteStatusOverride(siteKey);
        Logger.LogInformation(
            "工点状态变更：工点={Site}，原状态={Old}，新状态=在建（恢复天数判定），原完工时间={CompletedAt}，时间={Time}。",
            siteKey,
            previous.Status,
            previous.CompletedAt,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>
    /// 复工检测：已标记完工的工点扫描到新照片时，自动恢复为在建并记录 Warning 日志。
    /// 同一工点只记录一次（状态改为 Revived 后不再重复触发）。
    /// </summary>
    /// <param name="siteKey">工点聚合键。</param>
    /// <returns>本次是否检测到复工。</returns>
    public bool DetectRevival(string siteKey)
    {
        if (string.IsNullOrWhiteSpace(siteKey))
        {
            return false;
        }

        var previous = GetOverride(siteKey);
        if (previous is null || !previous.IsCompleted)
        {
            return false;
        }

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        DatabaseService.Instance.SaveSiteStatusOverride(new SiteStatusOverride
        {
            SiteKey = siteKey,
            Status = SiteLifecycleStatus.Revived,
            CompletedAt = previous.CompletedAt,
            Remark = previous.Remark,
            UpdatedAt = now
        });

        Logger.LogWarning(
            "检测到已完工工点复工：工点={Site}，原状态=已完工（{CompletedAt}），新状态=在建（复工），时间={Time}。",
            siteKey, previous.CompletedAt, now);
        return true;
    }

    /// <summary>判断覆盖行是否表示「今天刚检测到复工」（气泡角标与今日记录黄底提示依据）。</summary>
    /// <param name="row">覆盖行。</param>
    /// <param name="today">判定日期。</param>
    /// <returns>今日复工返回 true。</returns>
    public static bool IsRevivedToday(SiteStatusOverride? row, DateTime today) =>
        row is not null && row.IsRevivedToday(today);
}
