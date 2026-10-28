namespace MaterialManagementApp.Models;

/// <summary>
/// 低库存预警行（只读 DTO）。属性名与聚合 SQL 的列别名保持一致。
/// </summary>
public sealed class StockWarningItem
{
    /// <summary>物资名称。</summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>规格型号。</summary>
    public string Specification { get; set; } = string.Empty;

    /// <summary>计量单位。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>当前结存数量。</summary>
    public double Balance { get; set; }
}
