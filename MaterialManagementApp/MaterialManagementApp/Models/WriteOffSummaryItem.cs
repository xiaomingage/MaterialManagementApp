namespace MaterialManagementApp.Models;

/// <summary>
/// 月度核销汇总行（只读 DTO）。属性名与聚合 SQL 的列别名保持一致。
/// </summary>
public sealed class WriteOffSummaryItem
{
    /// <summary>核销月份，格式 yyyy-MM。</summary>
    public string WriteOffMonth { get; set; } = string.Empty;

    /// <summary>该月的核销记录条数。</summary>
    public int ItemCount { get; set; }

    /// <summary>该月的核销数量合计。</summary>
    public double TotalQty { get; set; }

    /// <summary>该月的核销金额合计（元）。</summary>
    public double TotalAmount { get; set; }
}
