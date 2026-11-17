using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace MaterialManagementApp.Models;

/// <summary>
/// 工点人工状态覆盖的取值。数据库 <c>SiteStatusOverride.Status</c> 列存这两个字符串。
/// </summary>
public static class SiteLifecycleStatus
{
    /// <summary>人工标记为已完工；优先于「连续无更新天数」判定，不参与任何天数告警。</summary>
    public const string Completed = "Completed";

    /// <summary>已完工工点又扫描到新照片：自动恢复为在建，气泡显示「复工」角标。</summary>
    public const string Revived = "Revived";
}

/// <summary>
/// 工点状态覆盖表 <c>SiteStatusOverride</c> 的实体。
/// 工点没有独立主数据表，<see cref="SiteKey"/> 必须与实际聚合键（<c>SiteProgressDaily.SiteName</c>）完全一致，
/// 否则左连接会失效。
/// </summary>
public sealed class SiteStatusOverride
{
    /// <summary>工点聚合键（即 SiteProgressDaily.SiteName）。</summary>
    public string SiteKey { get; set; } = string.Empty;

    /// <summary>状态：Completed（已完工）/ Revived（已完工后复工）。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>人工标记完工的时间，格式 yyyy-MM-dd HH:mm:ss。</summary>
    public string CompletedAt { get; set; } = string.Empty;

    /// <summary>完工备注（可选，仅记录用户填写内容）。</summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>本行最后更新时间（复工检测会刷新），格式 yyyy-MM-dd HH:mm:ss。</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>是否处于「已完工」状态（不参与天数告警）。</summary>
    [JsonIgnore]
    public bool IsCompleted =>
        string.Equals(Status, SiteLifecycleStatus.Completed, StringComparison.Ordinal);

    /// <summary>今日是否检测到复工（用于气泡角标与今日记录黄底提示）。</summary>
    /// <param name="today">判定使用的日期。</param>
    /// <returns>状态为 Revived 且更新日期为今天时返回 true。</returns>
    public bool IsRevivedToday(DateTime today) =>
        string.Equals(Status, SiteLifecycleStatus.Revived, StringComparison.Ordinal)
        && DateTime.TryParseExact(
            UpdatedAt, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated)
        && updated.Date == today.Date;
}
