using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 甲供物资实体，对应数据库 PartyAMaterial 表。
/// 同一条记录贯穿「物资申请档案 → 入库验收 → 出库领用 → 退库记录 → 库存查询」全流程，
/// 各环节数量分别累加到对应字段，库存量 = 入库数量 - 出库数量 + 退库数量。
/// 合并后的「甲供物资台账」页面在表格内直接编辑各环节数量，
/// 因此数量字段实现属性变更通知，让库存量列在编辑时立即刷新。
/// </summary>
public class PartyAMaterial : INotifyPropertyChanged
{
    private double _applyQty;
    private double _inboundQty;
    private double _outboundQty;
    private double _returnQty;
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>物资名称。</summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>规格型号。</summary>
    public string Specification { get; set; } = string.Empty;

    /// <summary>计量单位。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>申请数量。</summary>
    public double ApplyQty
    {
        get => _applyQty;
        set => SetField(ref _applyQty, value);
    }

    /// <summary>入库验收数量。</summary>
    public double InboundQty
    {
        get => _inboundQty;
        set
        {
            if (SetField(ref _inboundQty, value))
            {
                OnPropertyChanged(nameof(StockQty));
            }
        }
    }

    /// <summary>出库领用数量。</summary>
    public double OutboundQty
    {
        get => _outboundQty;
        set
        {
            if (SetField(ref _outboundQty, value))
            {
                OnPropertyChanged(nameof(StockQty));
            }
        }
    }

    /// <summary>退库数量。</summary>
    public double ReturnQty
    {
        get => _returnQty;
        set
        {
            if (SetField(ref _returnQty, value))
            {
                OnPropertyChanged(nameof(StockQty));
            }
        }
    }

    /// <summary>库存量，派生值：入库数量 - 出库数量 + 退库数量。只读，不写入数据库。</summary>
    public double StockQty => InboundQty - OutboundQty + ReturnQty;

    /// <summary>记录日期，格式 yyyy-MM-dd。</summary>
    public string RecordDate { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>验收照片的本地绝对路径；为空表示未附照片。</summary>
    public string PhotoPath { get; set; } = string.Empty;

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
