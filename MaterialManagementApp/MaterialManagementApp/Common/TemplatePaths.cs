using System;
using System.IO;

namespace MaterialManagementApp.Common;

/// <summary>
/// 内置台账模板文件的路径解析器。
/// 所有模板随生成复制到输出目录的 Assets\Templates 下，运行期不再依赖外部共享目录。
/// </summary>
public static class TemplatePaths
{
    /// <summary>地材台账模板的文件名。注意「模版」为模板原始命名，不要改成「模板」否则找不到文件。</summary>
    private const string EarthLedgerFileName = "地材台账模版.xlsx";

    /// <summary>小型机具管理台账模板的文件名。</summary>
    private const string SmallEquipmentFileName = "小型机具管理台账模板.xlsx";

    /// <summary>月度结算模板的文件名。</summary>
    private const string SupplierSettlementFileName = "月度结算模板.xlsx";

    /// <summary>挖机入场资料模板的文件名。</summary>
    private const string ExcavatorEntryFileName = "挖机入场模板.docx";

    /// <summary>工作日志模板的文件名。</summary>
    private const string WorkLogFileName = "工作日志模板.docx";

    /// <summary>静态构造时一次性解析目录与各模板路径，避免每次调用重复拼接字符串。</summary>
    static TemplatePaths()
    {
        TemplatesFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates");

        EarthLedger = Path.Combine(TemplatesFolder, EarthLedgerFileName);
        SmallEquipment = Path.Combine(TemplatesFolder, SmallEquipmentFileName);
        SupplierSettlement = Path.Combine(TemplatesFolder, SupplierSettlementFileName);
        ExcavatorEntry = Path.Combine(TemplatesFolder, ExcavatorEntryFileName);
        WorkLog = Path.Combine(TemplatesFolder, WorkLogFileName);
    }

    /// <summary>模板所在目录的绝对路径（AppContext.BaseDirectory\Assets\Templates）。</summary>
    public static string TemplatesFolder { get; }

    /// <summary>地材台账模板（.xlsx）的绝对路径。</summary>
    public static string EarthLedger { get; }

    /// <summary>小型机具管理台账模板（.xlsx）的绝对路径。</summary>
    public static string SmallEquipment { get; }

    /// <summary>月度结算模板（.xlsx）的绝对路径。</summary>
    public static string SupplierSettlement { get; }

    /// <summary>挖机入场资料模板（.docx）的绝对路径。</summary>
    public static string ExcavatorEntry { get; }

    /// <summary>工作日志模板（.docx）的绝对路径。</summary>
    public static string WorkLog { get; }

    /// <summary>
    /// 断言模板文件存在并返回其绝对路径。
    /// </summary>
    /// <param name="path">由本类属性给出的模板绝对路径。</param>
    /// <returns>可直接传给 ClosedXML / OpenXml 的绝对路径。</returns>
    /// <exception cref="FileNotFoundException">模板文件未随生成复制到输出目录时抛出。</exception>
    public static string EnsureExists(string path)
    {
        if (!File.Exists(path))
        {
            // 把绝对路径带入异常消息，便于现场判断是「模板未拷贝」还是「文件名不一致」
            throw new FileNotFoundException(
                $"台账模板文件缺失：{path}。请确认 Assets\\Templates 目录已随生成复制到输出目录。",
                path);
        }

        return path;
    }
}
