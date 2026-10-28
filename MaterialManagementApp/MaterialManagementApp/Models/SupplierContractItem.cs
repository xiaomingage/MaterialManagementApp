using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 合同材料价格明细实体，对应数据库 SupplierContractItem 表。
/// 一张供应商合同（Supplier）可以对应多种材料，每种材料有各自的单价与计量单位。
/// OCR 导入台账时，按「供应商 + 物资名称」到这里查合同约定的单价，
/// 以避免 OCR 识别单价的高误差。
/// </summary>
public class SupplierContractItem : INotifyPropertyChanged
{
    private int _supplierId;
    private string _supplierName = string.Empty;
    private string _materialName = string.Empty;
    private string _specification = string.Empty;
    private string _unit = string.Empty;
    private double _unitPrice;
    private string _remark = string.Empty;
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>所属供应商主键（Supplier.Id）。</summary>
    public int SupplierId
    {
        get => _supplierId;
        set => SetField(ref _supplierId, value);
    }

    /// <summary>所属供应商名称（Supplier.Name），方便 UI 展示，不从数据库写入。</summary>
    public string SupplierName
    {
        get => _supplierName;
        set => SetField(ref _supplierName, value);
    }

    /// <summary>材料名称，如水泥、砂、石粉、碎石、渣土车。</summary>
    public string MaterialName
    {
        get => _materialName;
        set => SetField(ref _materialName, value);
    }

    /// <summary>规格描述（可选），如 P.O42.5、20-30mm。</summary>
    public string Specification
    {
        get => _specification;
        set => SetField(ref _specification, value);
    }

    /// <summary>计量单位，如 吨、方、辆。</summary>
    public string Unit
    {
        get => _unit;
        set => SetField(ref _unit, value);
    }

    /// <summary>合同单价（元 / 计量单位）。</summary>
    public double UnitPrice
    {
        get => _unitPrice;
        set => SetField(ref _unitPrice, value);
    }

    /// <summary>备注。</summary>
    public string Remark
    {
        get => _remark;
        set => SetField(ref _remark, value);
    }

    /// <summary>
    /// 是否为「表格内联新增」尚未入库的空白行。该标记只存在于内存中，不写入数据库，
    /// 仅用于让行末操作列显示「保存 / 取消」而不是「保存 / 删除」。
    /// </summary>
    public bool IsNewRow
    {
        get => _isNewRow;
        set => SetField(ref _isNewRow, value);
    }

    /// <summary>字段赋值并在变化时触发属性变更通知。</summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>触发属性变更通知。</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
