using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 物资核销实体，对应数据库 MaterialWriteOff 表。
/// 用于「月度核销」与「核销台账」：按核销月份汇总物资的核销数量与金额。
/// 「核销台账」页面在表格内直接编辑，因此实现属性变更通知；
/// 数量或单价变化时金额按 数量 × 单价 自动重算，不手工录入。
/// </summary>
public class MaterialWriteOff : INotifyPropertyChanged
{
    private string _materialName = string.Empty;
    private string _writeOffMonth = string.Empty;
    private double _writeOffQty;
    private double _unitPrice;
    private double _amount;
    private string _reason = string.Empty;
    private string _remark = string.Empty;
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>物资名称。</summary>
    public string MaterialName
    {
        get => _materialName;
        set => SetField(ref _materialName, value);
    }

    /// <summary>核销月份，格式 yyyy-MM。</summary>
    public string WriteOffMonth
    {
        get => _writeOffMonth;
        set => SetField(ref _writeOffMonth, value);
    }

    /// <summary>核销数量。变化后金额自动重算。</summary>
    public double WriteOffQty
    {
        get => _writeOffQty;
        set
        {
            if (SetField(ref _writeOffQty, value))
            {
                RecalculateAmount();
            }
        }
    }

    /// <summary>核销单价（元/计量单位）。变化后金额自动重算。</summary>
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

    /// <summary>核销金额（元），由数量 × 单价得出。</summary>
    public double Amount
    {
        get => _amount;
        set => SetField(ref _amount, value);
    }

    /// <summary>核销原因，如正常消耗、损耗、报废。</summary>
    public string Reason
    {
        get => _reason;
        set => SetField(ref _reason, value);
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

    /// <summary>按 数量 × 单价 重算金额，保留两位小数。</summary>
    private void RecalculateAmount()
    {
        Amount = Math.Round(WriteOffQty * UnitPrice, 2);
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
