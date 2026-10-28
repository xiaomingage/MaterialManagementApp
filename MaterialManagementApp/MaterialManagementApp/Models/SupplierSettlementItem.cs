namespace MaterialManagementApp.Models;

/// <summary>
/// 供应商结算汇总行（只读 DTO）。属性名与查询 SQL 的列名保持一致。
/// </summary>
public sealed class SupplierSettlementItem
{
    /// <summary>供应商名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>合同编号。</summary>
    public string ContractNo { get; set; } = string.Empty;

    /// <summary>月度结算金额（元）。</summary>
    public double MonthlySettlement { get; set; }

    /// <summary>合同签订日期，格式 yyyy-MM-dd。</summary>
    public string SignDate { get; set; } = string.Empty;
}
