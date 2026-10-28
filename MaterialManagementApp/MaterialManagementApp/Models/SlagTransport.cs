using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MaterialManagementApp.Models;

/// <summary>
/// 渣土运输台账实体，对应数据库 SlagTransport 表。
/// 实现 <see cref="INotifyPropertyChanged"/>：渣土运输页在表格内直接编辑，
/// 且 OCR 识别完成后会把字段填充进未保存的空白行，需要属性变更通知让单元格立即刷新。
/// 单据字段口径与地材单一致，另含运输费这一渣土场景特有字段。
/// </summary>
public class SlagTransport : INotifyPropertyChanged
{
    private int _id;
    private string _noLast4 = string.Empty;
    private string _plateNumber = string.Empty;
    private string _usageAddress = string.Empty;
    private string _materialName = string.Empty;
    private string _specification = string.Empty;
    private double _quantity;
    private string _unit = string.Empty;
    private double _transportFee;
    private string _entryDate = string.Empty;
    private string _entryTime = string.Empty;
    private string _siteSignature = string.Empty;
    private string _remark = string.Empty;
    private string _projectDept = string.Empty;
    private string _projectPhase = string.Empty;
    private bool _isNewRow;

    /// <summary>属性变更通知，供 DataGrid 双向绑定刷新界面。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>主键，自增。</summary>
    public int Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    /// <summary>单据编号后四位。</summary>
    public string NoLast4
    {
        get => _noLast4;
        set => SetField(ref _noLast4, value);
    }

    /// <summary>车牌号。</summary>
    public string PlateNumber
    {
        get => _plateNumber;
        set => SetField(ref _plateNumber, value);
    }

    /// <summary>使用地址。</summary>
    public string UsageAddress
    {
        get => _usageAddress;
        set => SetField(ref _usageAddress, value);
    }

    /// <summary>材料种类，渣土运输通常为「渣土」。</summary>
    public string MaterialName
    {
        get => _materialName;
        set => SetField(ref _materialName, value);
    }

    /// <summary>规格，与地材单同口径。</summary>
    public string Specification
    {
        get => _specification;
        set => SetField(ref _specification, value);
    }

    /// <summary>数量。</summary>
    public double Quantity
    {
        get => _quantity;
        set => SetField(ref _quantity, value);
    }

    /// <summary>计量单位，如车、方。</summary>
    public string Unit
    {
        get => _unit;
        set => SetField(ref _unit, value);
    }

    /// <summary>运输费（元）。渣土车按车次计费，故单独成列而不折入单价。</summary>
    public double TransportFee
    {
        get => _transportFee;
        set => SetField(ref _transportFee, value);
    }

    /// <summary>票据日期，格式 yyyy-MM-dd。</summary>
    public string EntryDate
    {
        get => _entryDate;
        set => SetField(ref _entryDate, value);
    }

    /// <summary>手写时间，格式 HH:mm。</summary>
    public string EntryTime
    {
        get => _entryTime;
        set => SetField(ref _entryTime, value);
    }

    /// <summary>现场签字文字。</summary>
    public string SiteSignature
    {
        get => _siteSignature;
        set => SetField(ref _siteSignature, value);
    }

    /// <summary>备注。</summary>
    public string Remark
    {
        get => _remark;
        set => SetField(ref _remark, value);
    }

    /// <summary>所属项目部（如信宜、梧州）。空串表示未指定；候选项在设置页维护。</summary>
    public string ProjectDept
    {
        get => _projectDept;
        set => SetField(ref _projectDept, value);
    }

    /// <summary>所属期数（如一期、二期）。空串表示该记录无期数（如梧州项目）。</summary>
    public string ProjectPhase
    {
        get => _projectPhase;
        set => SetField(ref _projectPhase, value);
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
