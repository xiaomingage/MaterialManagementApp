using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 库存流水记录：所有出入库、退库、盘点动作统一记录在本表，
/// 当前结存由同一物资的流水汇总得出，保证每一笔变动都有据可查。
/// 实现 <see cref="INotifyPropertyChanged"/>，使 DataGrid 内直接编辑数量时合计能立即刷新，
/// 行末操作列也能按 <see cref="IsNewRow"/> 状态切换显示。
/// </summary>
public class StockRecord : INotifyPropertyChanged
{
    private double _quantity;
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>物资来源，取值「甲供」或「自购」。</summary>
    public string MaterialSource { get; set; } = "甲供";

    /// <summary>物资名称。</summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>规格型号。</summary>
    public string Specification { get; set; } = string.Empty;

    /// <summary>计量单位。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>变更类型，取值「入库」「出库」「退库」「盘点」；盘点记录的数量为差异值（可正可负）。</summary>
    public string ChangeType { get; set; } = "入库";

    /// <summary>变更数量。入库与退库为正、出库为正（在汇总时取负），盘点为差异值。</summary>
    public double Quantity
    {
        get => _quantity;
        set => SetField(ref _quantity, value);
    }

    /// <summary>发生日期，格式 yyyy-MM-dd。</summary>
    public string OccurDate { get; set; } = string.Empty;

    /// <summary>经办人。</summary>
    public string Handler { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Remark { get; set; } = string.Empty;

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
