using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 挖机进场资料实体，对应数据库 ExcavatorEntry 表（表1：挖机进场资料模版）。
/// 一份记录对应一次设备进场验收，18 个字段覆盖设备身份、验收情况与人员。
/// 实现 <see cref="INotifyPropertyChanged"/>，使 DataGrid 行内新增/保存后操作列按钮能按 <see cref="IsNewRow"/> 即时切换。
/// </summary>
public class ExcavatorEntry : INotifyPropertyChanged
{
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>使用工程名称。</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>使用单位。</summary>
    public string UsingUnit { get; set; } = string.Empty;

    /// <summary>验收日期，格式 yyyy-MM-dd。</summary>
    public string AcceptanceDate { get; set; } = string.Empty;

    /// <summary>设备名称，如挖掘机。</summary>
    public string EquipmentName { get; set; } = string.Empty;

    /// <summary>型号。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>出厂编码。</summary>
    public string FactoryCode { get; set; } = string.Empty;

    /// <summary>出厂日期，格式 yyyy-MM-dd。</summary>
    public string FactoryDate { get; set; } = string.Empty;

    /// <summary>制造厂。</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>存放地点。</summary>
    public string StorageLocation { get; set; } = string.Empty;

    /// <summary>操作手姓名。</summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>进场日期，格式 yyyy-MM-dd。</summary>
    public string EntryDate { get; set; } = string.Empty;

    /// <summary>附属设备，如破碎锤、液压剪。</summary>
    public string AuxiliaryEquipment { get; set; } = string.Empty;

    /// <summary>验收情况描述。</summary>
    public string AcceptanceStatus { get; set; } = string.Empty;

    /// <summary>随机资料，如合格证、说明书、保修卡。</summary>
    public string RandomDocuments { get; set; } = string.Empty;

    /// <summary>随机工具。</summary>
    public string RandomTools { get; set; } = string.Empty;

    /// <summary>机况描述。</summary>
    public string MachineCondition { get; set; } = string.Empty;

    /// <summary>验收结果，如合格 / 不合格 / 整改后合格。</summary>
    public string AcceptanceResult { get; set; } = string.Empty;

    /// <summary>验收人员姓名。</summary>
    public string AcceptancePerson { get; set; } = string.Empty;

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
