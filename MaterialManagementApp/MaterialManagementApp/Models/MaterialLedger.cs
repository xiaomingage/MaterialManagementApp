using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 物资台账实体，对应数据库 MaterialLedger 表（表2：物资台账表）。
/// 实现 <see cref="INotifyPropertyChanged"/>，使 DataGrid 内直接编辑数量或单价时，
/// 金额列能立即刷新为「数量 × 单价」的结果，而不必等重新加载。
/// </summary>
public class MaterialLedger : INotifyPropertyChanged
{
    private double _quantity;
    private double _unitPrice;
    private double _amount;
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>进场日期，格式 yyyy-MM-dd。</summary>
    public string EntryDate { get; set; } = string.Empty;

    /// <summary>进场时间，格式 HH:mm。</summary>
    public string EntryTime { get; set; } = string.Empty;

    /// <summary>单据编号。</summary>
    public string DocNo { get; set; } = string.Empty;

    /// <summary>物资名称。</summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>数量。赋值后会自动重算 <see cref="Amount"/>。</summary>
    public double Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity.Equals(value))
            {
                return;
            }

            _quantity = value;
            OnPropertyChanged();
            RecalculateAmount();
        }
    }

    /// <summary>计量单位，如 m3、t、个。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>单价（元/计量单位）。赋值后会自动重算 <see cref="Amount"/>。</summary>
    public double UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (_unitPrice.Equals(value))
            {
                return;
            }

            _unitPrice = value;
            OnPropertyChanged();
            RecalculateAmount();
        }
    }

    /// <summary>金额（元），由 数量 × 单价 自动计算，不从界面直接录入。</summary>
    public double Amount
    {
        get => _amount;
        set
        {
            if (_amount.Equals(value))
            {
                return;
            }

            _amount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>供应商名称。</summary>
    public string Supplier { get; set; } = string.Empty;

    /// <summary>项目地址。</summary>
    public string ProjectAddress { get; set; } = string.Empty;

    /// <summary>经办人。</summary>
    public string Handler { get; set; } = string.Empty;

    /// <summary>车牌号。OCR 导入单据时从图片识别填入。</summary>
    public string PlateNumber { get; set; } = string.Empty;

    /// <summary>规格。OCR 导入单据时从图片识别填入。</summary>
    public string Specification { get; set; } = string.Empty;

    /// <summary>现场签字文字。OCR 导入单据时从图片识别填入。</summary>
    public string SiteSignature { get; set; } = string.Empty;

    /// <summary>备注。对应模板的「备注」列，与现场签字语义区分。</summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>所属项目部（如信宜、梧州）。空串表示未指定；候选项在设置页维护。</summary>
    public string ProjectDept { get; set; } = string.Empty;

    /// <summary>所属期数（如一期、二期）。空串表示该记录无期数（如梧州项目）。</summary>
    public string ProjectPhase { get; set; } = string.Empty;

    /// <summary>
    /// 是否为「表格内联新增」尚未入库的空白行。该标记只存在于内存中，不写入数据库，
    /// 仅用于让行末操作列显示「保存 / 取消」而不是「保存 / 删除」。
    /// </summary>
    public bool IsNewRow
    {
        get => _isNewRow;
        set
        {
            if (_isNewRow == value)
            {
                return;
            }

            _isNewRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>按「数量 × 单价」重算金额，保留两位小数以符合人民币金额习惯。</summary>
    private void RecalculateAmount()
    {
        Amount = Math.Round(_quantity * _unitPrice, 2);
    }

    /// <summary>触发属性变更通知。</summary>
    /// <param name="propertyName">变更的属性名，由编译器自动填充。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
