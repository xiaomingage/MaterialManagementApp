using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 供应商实体，对应数据库 Supplier 表。
/// 保存供应商合同信息与月度结算金额。
/// 「供应商列表 / 供应商合同」页面在表格内直接编辑，
/// 因此实现属性变更通知，让界面在编辑时立即刷新。
/// </summary>
public class Supplier : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _contractNo = string.Empty;
    private string _contactPerson = string.Empty;
    private string _phone = string.Empty;
    private double _monthlySettlement;
    private string _signDate = string.Empty;
    private string _remark = string.Empty;
    private string _contractFilePath = string.Empty;
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>供应商名称。</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>合同编号。</summary>
    public string ContractNo
    {
        get => _contractNo;
        set => SetField(ref _contractNo, value);
    }

    /// <summary>联系人姓名。</summary>
    public string ContactPerson
    {
        get => _contactPerson;
        set => SetField(ref _contactPerson, value);
    }

    /// <summary>联系电话。</summary>
    public string Phone
    {
        get => _phone;
        set => SetField(ref _phone, value);
    }

    /// <summary>月度结算金额（元）。</summary>
    public double MonthlySettlement
    {
        get => _monthlySettlement;
        set => SetField(ref _monthlySettlement, value);
    }

    /// <summary>合同签订日期，格式 yyyy-MM-dd。</summary>
    public string SignDate
    {
        get => _signDate;
        set => SetField(ref _signDate, value);
    }

    /// <summary>备注。</summary>
    public string Remark
    {
        get => _remark;
        set => SetField(ref _remark, value);
    }

    /// <summary>合同 PDF 文件的存储路径（相对数据目录，便于随数据一起迁移）。为空表示未上传。</summary>
    public string ContractFilePath
    {
        get => _contractFilePath;
        set => SetField(ref _contractFilePath, value);
    }

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
