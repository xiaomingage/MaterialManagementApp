using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 设备租赁台账实体，对应数据库 RentalDevice 表。
/// 记录外部租赁设备的租期与租金结算情况。
/// 实现 <see cref="INotifyPropertyChanged"/>，使 DataGrid 行内新增/保存后操作列按钮能按 <see cref="IsNewRow"/> 即时切换。
/// </summary>
public class RentalDevice : INotifyPropertyChanged
{
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>设备名称。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>出租方名称。</summary>
    public string Lessor { get; set; } = string.Empty;

    /// <summary>起租日期，格式 yyyy-MM-dd。</summary>
    public string RentStart { get; set; } = string.Empty;

    /// <summary>退租日期，格式 yyyy-MM-dd。</summary>
    public string RentEnd { get; set; } = string.Empty;

    /// <summary>月租金（元）。</summary>
    public double MonthlyRent { get; set; }

    /// <summary>已结算金额（元）。</summary>
    public double SettlementAmount { get; set; }

    /// <summary>备注。</summary>
    public string Remark { get; set; } = string.Empty;

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

    /// <summary>触发属性变更通知。</summary>
    /// <param name="propertyName">变更的属性名，由编译器自动填充。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
