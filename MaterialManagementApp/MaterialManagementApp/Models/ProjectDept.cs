using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 项目部实体，对应数据库 ProjectDept 表。
/// 项目部是「项目部 / 期数」维度的一半，由用户在设置页自由维护（如信宜、梧州），软件不预设业务含义。
/// 实现 <see cref="INotifyPropertyChanged"/>，使设置页列表内直接重命名后界面立即刷新。
/// </summary>
public class ProjectDept : INotifyPropertyChanged
{
    private string _name = string.Empty;

    /// <summary>属性变更通知，供设置页列表双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>项目部名称，全库唯一。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, System.StringComparison.Ordinal))
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
        }
    }

    /// <summary>显示顺序，越小越靠前；新增时自动取最大值 + 1。</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 从数据库加载时的原始名称（仅内存使用，无对应数据列）。
    /// 设置页行内重命名后用「新名 vs 原始名」比对，以便把业务记录里的旧名级联改为新名。
    /// </summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>触发属性变更通知。</summary>
    /// <param name="propertyName">变更的属性名，由编译器自动填充。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
