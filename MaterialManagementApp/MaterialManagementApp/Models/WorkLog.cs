using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 工作日志实体，对应数据库 WorkLog 表。
/// 实现 <see cref="INotifyPropertyChanged"/>，让表格行末操作列能按 <see cref="IsNewRow"/> 状态切换显示。
/// </summary>
public class WorkLog : INotifyPropertyChanged
{
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>记录日期，格式 yyyy-MM-dd（数据库以 TEXT 存储）。</summary>
    public string LogDate { get; set; } = string.Empty;

    /// <summary>日志正文内容。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>创建时间戳，格式 yyyy-MM-dd HH:mm:ss，用于同日期内排序。</summary>
    public string CreatedAt { get; set; } = string.Empty;

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
