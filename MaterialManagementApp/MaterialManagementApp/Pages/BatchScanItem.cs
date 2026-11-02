using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 批量扫描识别窗口中的一张地材单图片卡片。
/// 一张图片对应一个卡片：缩略图、可手动修正的识别全文、可手动填入的字段区，
/// 以及独立的「保存入台账」按钮——每张单据互不影响，改好一张存一张。
/// </summary>
public sealed class BatchScanItem : INotifyPropertyChanged
{
    private string _rawText = string.Empty;
    private string _materialName = string.Empty;
    private string _specification = string.Empty;
    private string _quantityText = string.Empty;
    private string _unit = string.Empty;
    private string _unitPriceText = string.Empty;
    private string _entryDateText = string.Empty;
    private string _docNo = string.Empty;
    private string _supplier = string.Empty;
    private string _handler = string.Empty;
    private string _plateNumber = string.Empty;
    private bool _isBusy;
    private bool _isSaved;
    private string _statusText = "排队等待识别...";

    /// <summary>属性变更通知，供卡片模板双向绑定刷新。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>图片当前的来源路径（选择/拖入的原始文件，或剪贴板截图落盘的临时文件）。保存台账时会复制进数据目录。
    /// 注意必须用普通 set：XamlTypeInfo 生成器会为公开属性生成赋值代码，init-only 会编译失败。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>文件名，仅用于卡片标题显示。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>缩略图（低分辨率解码，多张卡片不占内存）。</summary>
    public ImageSource? Thumbnail { get; set; }

    /// <summary>是否尚待首次识别。识别队列按此标记逐张处理。</summary>
    public bool RecognitionPending { get; set; } = true;

    /// <summary>识别全文。识别有误时可直接在此修改，再点「按当前文字重提字段」。</summary>
    public string RawText
    {
        get => _rawText;
        set => SetField(ref _rawText, value);
    }

    /// <summary>物资名称（必填，保存的硬校验字段）。</summary>
    public string MaterialName
    {
        get => _materialName;
        set => SetField(ref _materialName, value);
    }

    /// <summary>规格。</summary>
    public string Specification
    {
        get => _specification;
        set => SetField(ref _specification, value);
    }

    /// <summary>数量（自由文本，保存时解析数值，无法解析按 0 处理）。</summary>
    public string QuantityText
    {
        get => _quantityText;
        set => SetField(ref _quantityText, value);
    }

    /// <summary>计量单位。</summary>
    public string Unit
    {
        get => _unit;
        set => SetField(ref _unit, value);
    }

    /// <summary>单价（自由文本，保存时解析数值；识别不出可参照合同匹配结果手动填）。</summary>
    public string UnitPriceText
    {
        get => _unitPriceText;
        set => SetField(ref _unitPriceText, value);
    }

    /// <summary>进场日期（自由文本，保存时规范成 yyyy-MM-dd，无法解析回退当天）。</summary>
    public string EntryDateText
    {
        get => _entryDateText;
        set => SetField(ref _entryDateText, value);
    }

    /// <summary>单据编号。</summary>
    public string DocNo
    {
        get => _docNo;
        set => SetField(ref _docNo, value);
    }

    /// <summary>供应商。</summary>
    public string Supplier
    {
        get => _supplier;
        set => SetField(ref _supplier, value);
    }

    /// <summary>经办人。</summary>
    public string Handler
    {
        get => _handler;
        set => SetField(ref _handler, value);
    }

    /// <summary>车牌号。</summary>
    public string PlateNumber
    {
        get => _plateNumber;
        set => SetField(ref _plateNumber, value);
    }

    /// <summary>该卡片是否正在识别中（显示进度圈并锁住操作按钮）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    /// <summary>是否已保存入台账。保存后锁定字段与按钮，防止重复入库。</summary>
    public bool IsSaved
    {
        get => _isSaved;
        set
        {
            if (SetField(ref _isSaved, value))
            {
                // IsEditable 与 IsSaved 恒反相，一并通知让卡片模板的 IsEnabled 绑定即时刷新
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable)));
            }
        }
    }

    /// <summary>卡片是否可编辑（未保存）。直接用作字段区与按钮的 IsEnabled 绑定。</summary>
    public bool IsEditable => !_isSaved;

    /// <summary>卡片状态提示（识别中 / 识别完成 / 校验不通过原因 / 已保存等）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    /// <summary>触发属性变更通知；值无变化时跳过并返回 false。</summary>
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
