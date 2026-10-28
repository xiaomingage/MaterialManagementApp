using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 机具设备实体，对应数据库 Equipment 表。
/// 「机具入场」「小型机具明细」「工程机械」「维保与年检」共用本表，通过 <see cref="Status"/> 区分在用、闲置、维修、已退场等状态。
/// 实现 <see cref="INotifyPropertyChanged"/>，使 DataGrid 行内新增/保存后操作列按钮能按 <see cref="IsNewRow"/> 即时切换。
/// </summary>
public class Equipment : INotifyPropertyChanged
{
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>机具或设备名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>型号规格。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>入场日期，格式 yyyy-MM-dd。</summary>
    public string EntryDate { get; set; } = string.Empty;

    /// <summary>产权方或使用方。</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>当前状态：在用 / 闲置 / 维修 / 已退场。</summary>
    public string Status { get; set; } = string.Empty;

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
