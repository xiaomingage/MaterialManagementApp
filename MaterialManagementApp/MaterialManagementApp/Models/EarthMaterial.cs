namespace MaterialManagementApp.Models;

/// <summary>
/// 地材台账实体，对应数据库 EarthMaterial 表。
/// 用于记录渣土、砂石等地方材料的进场明细及运输费用。
/// </summary>
public class EarthMaterial
{
    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>地材类型，如渣土、砂石、碎石、回填土。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>规格型号，如粒径、强度等级等描述。</summary>
    public string Specification { get; set; } = string.Empty;

    /// <summary>计量单位，如 m3、t、车。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>数量。</summary>
    public double Quantity { get; set; }

    /// <summary>单价（元/计量单位）。</summary>
    public double UnitPrice { get; set; }

    /// <summary>运输费合计（元）。</summary>
    public double TransportFee { get; set; }

    /// <summary>供应商名称。</summary>
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>记录日期，格式 yyyy-MM-dd。</summary>
    public string RecordDate { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Remark { get; set; } = string.Empty;
}
