namespace MaterialManagementApp.Models;

/// <summary>
/// 库存汇总行（只读 DTO）。属性名必须与聚合 SQL 的列别名完全一致，
/// 因为 <c>DatabaseService.Query&lt;T&gt;</c> 按「列名 = 属性名」反射映射。
/// </summary>
public sealed class StockSummaryItem
{
    /// <summary>物资名称。</summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>规格型号。</summary>
    public string Specification { get; set; } = string.Empty;

    /// <summary>计量单位。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>累计入库数量。</summary>
    public double InQty { get; set; }

    /// <summary>累计出库数量。</summary>
    public double OutQty { get; set; }

    /// <summary>累计退库数量。</summary>
    public double ReturnQty { get; set; }

    /// <summary>当前结存 = 入库 - 出库 + 退库 + 盘点差异。</summary>
    public double Balance { get; set; }
}
