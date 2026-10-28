using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 小型机具台账实体，对应数据库 SmallEquipment 表（表3：小型机具台账）。
/// 不记录队伍信息，全项目共用一张台账。
/// 实现 <see cref="INotifyPropertyChanged"/>，使 DataGrid 行内新增/保存后操作列按钮能按 <see cref="IsNewRow"/> 即时切换。
/// </summary>
public class SmallEquipment : INotifyPropertyChanged
{
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>机具编号，用于现场标识与快速查找。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>机具照片的本地绝对路径；为空表示未附照片。</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>项目简称。</summary>
    public string ProjectShortName { get; set; } = string.Empty;

    /// <summary>使用项目工点。</summary>
    public string WorkSite { get; set; } = string.Empty;

    /// <summary>类别，如发电机、水泵、电焊机。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>小型机具名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>规格型号。</summary>
    public string Specification { get; set; } = string.Empty;

    /// <summary>计量单位，如台、套、个。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>数量。</summary>
    public double Quantity { get; set; }

    /// <summary>配置方式，如自有、租赁、调拨。</summary>
    public string ConfigMethod { get; set; } = string.Empty;

    /// <summary>资产所属单位。</summary>
    public string AssetOwner { get; set; } = string.Empty;

    /// <summary>生产厂家。</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>出厂时间，格式 yyyy-MM。</summary>
    public string FactoryDate { get; set; } = string.Empty;

    /// <summary>性能状态，如良好、待修、报废。</summary>
    public string PerformanceStatus { get; set; } = string.Empty;

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
