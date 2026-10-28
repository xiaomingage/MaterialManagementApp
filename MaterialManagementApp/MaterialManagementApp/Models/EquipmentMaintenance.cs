using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 机具维保与年检记录。与 <see cref="Equipment"/> 通过设备名称关联，
/// 独立成表是因为同一台设备会有多次维保/年检记录，一对多关系不适合塞进设备主表。
/// 实现 <see cref="INotifyPropertyChanged"/>，使 DataGrid 行内新增/保存后操作列按钮能按 <see cref="IsNewRow"/> 即时切换。
/// </summary>
public sealed class EquipmentMaintenance : INotifyPropertyChanged
{
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>设备名称，对应 <see cref="Equipment"/> 的 Name。</summary>
    public string EquipmentName { get; set; } = string.Empty;

    /// <summary>记录类型，取值「维保」或「年检」。</summary>
    public string MaintainType { get; set; } = "维保";

    /// <summary>本次维保/年检日期，格式 yyyy-MM-dd。</summary>
    public string MaintainDate { get; set; } = string.Empty;

    /// <summary>下次到期日期，格式 yyyy-MM-dd；为空表示不提醒。</summary>
    public string NextDueDate { get; set; } = string.Empty;

    /// <summary>费用（元）。</summary>
    public double Cost { get; set; }

    /// <summary>维保/检测单位。</summary>
    public string Vendor { get; set; } = string.Empty;

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
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
