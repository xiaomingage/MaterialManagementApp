using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 自购物资实体，对应数据库 SelfPurchasedMaterial 表。
/// 用于「物资申请表」与「物资采购明细」：申请数量与实际采购数量分开记录，便于对比偏差。
/// 实现 <see cref="INotifyPropertyChanged"/>，使 DataGrid 内直接编辑采购数量或单价时，
/// 金额列能立即刷新为「采购数量 × 单价」的结果，而不必等重新加载。
/// </summary>
public class SelfPurchasedMaterial : INotifyPropertyChanged
{
    private int _id;
    private string _materialName = string.Empty;
    private string _specification = string.Empty;
    private string _unit = string.Empty;
    private double _applyQty;
    private double _purchaseQty;
    private double _unitPrice;
    private double _amount;
    private string _supplierName = string.Empty;
    private string _recordDate = string.Empty;
    private string _remark = string.Empty;
    private string _projectDept = string.Empty;
    private string _projectPhase = string.Empty;
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    /// <summary>物资名称。</summary>
    public string MaterialName
    {
        get => _materialName;
        set => SetField(ref _materialName, value);
    }

    /// <summary>规格型号。</summary>
    public string Specification
    {
        get => _specification;
        set => SetField(ref _specification, value);
    }

    /// <summary>计量单位。</summary>
    public string Unit
    {
        get => _unit;
        set => SetField(ref _unit, value);
    }

    /// <summary>申请数量。</summary>
    public double ApplyQty
    {
        get => _applyQty;
        set => SetField(ref _applyQty, value);
    }

    /// <summary>实际采购数量。赋值后会自动重算 <see cref="Amount"/>。</summary>
    public double PurchaseQty
    {
        get => _purchaseQty;
        set
        {
            if (SetField(ref _purchaseQty, value))
            {
                RecalculateAmount();
            }
        }
    }

    /// <summary>采购单价（元/计量单位）。赋值后会自动重算 <see cref="Amount"/>。</summary>
    public double UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (SetField(ref _unitPrice, value))
            {
                RecalculateAmount();
            }
        }
    }

    /// <summary>采购金额（元），由 采购数量 × 单价 自动计算，不从界面直接录入。</summary>
    public double Amount
    {
        get => _amount;
        set => SetField(ref _amount, value);
    }

    /// <summary>供应商名称。</summary>
    public string SupplierName
    {
        get => _supplierName;
        set => SetField(ref _supplierName, value);
    }

    /// <summary>记录日期，格式 yyyy-MM-dd。</summary>
    public string RecordDate
    {
        get => _recordDate;
        set => SetField(ref _recordDate, value);
    }

    /// <summary>备注。</summary>
    public string Remark
    {
        get => _remark;
        set => SetField(ref _remark, value);
    }

    /// <summary>所属项目部（如信宜、梧州）。空串表示未指定；候选项在设置页维护。</summary>
    public string ProjectDept
    {
        get => _projectDept;
        set => SetField(ref _projectDept, value);
    }

    /// <summary>所属期数（如一期、二期）。空串表示该记录无期数（如梧州项目）。</summary>
    public string ProjectPhase
    {
        get => _projectPhase;
        set => SetField(ref _projectPhase, value);
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

    /// <summary>按「采购数量 × 单价」重算金额，保留两位小数以符合人民币金额习惯。</summary>
    private void RecalculateAmount()
    {
        Amount = Math.Round(_purchaseQty * _unitPrice, 2);
    }

    /// <summary>字段赋值并在变化时触发属性变更通知。</summary>
    /// <typeparam name="T">字段类型。</typeparam>
    /// <param name="field">目标字段引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">变更的属性名，由编译器自动填充。</param>
    /// <returns>值发生变化返回 true。</returns>
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
    /// <param name="propertyName">变更的属性名，由编译器自动填充。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
