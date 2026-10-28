using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 「项目部 / 期数」维度适用栏目配置实体，对应数据库 DeptPhaseApplicableTable 表。
/// 每一行代表一个可启用该维度的业务表：勾选（IsEnabled = true）时会给该表
/// 自动补 ProjectDept / ProjectPhase 两列（ALTER TABLE 迁移），取消勾选只隐藏界面、保留历史数据。
/// </summary>
public class DeptPhaseApplicableTable : INotifyPropertyChanged
{
    private bool _isEnabled;

    /// <summary>属性变更通知，供设置页复选框列表刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>业务表名（数据库表名，代码常量），全库唯一。</summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>栏目显示名，用于设置页复选框列表展示。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否已启用「项目部 / 期数」维度。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
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
