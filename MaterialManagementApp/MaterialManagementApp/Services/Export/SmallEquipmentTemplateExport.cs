using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClosedXML.Excel;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 小型机具管理台账的模板导出适配器。
/// 采用单 Sheet 复刻模板的 25 列（A-Y）三层合并表头，模板中系统没有对应字段的列原样留空以保版式。
/// </summary>
public static class SmallEquipmentTemplateExport
{
    /// <summary>工作表名（不再按队伍拆分）。</summary>
    public const string SheetName = "小型机具管理台账";

    /// <summary>表头第一行行号。</summary>
    public const int FirstHeaderRow = 4;

    /// <summary>首条数据行号。</summary>
    public const int FirstDataRow = 7;

    /// <summary>模板总列数（A 到 Y）。</summary>
    public const int ColumnCount = 25;

    /// <summary>25 列列宽（字符宽），按模板比例近似取值。</summary>
    public static readonly double[] ColumnWidths =
    {
        7, 10, 12, 10, 14, 14, 9, 8, 12, 14, 12, 12, 10, 12, 12, 12, 14, 12, 14, 14, 14, 12, 16, 10, 14
    };

    /// <summary>表头区块描述：起始行、起始列、跨行数、跨列数与表头文字。</summary>
    private readonly record struct HeaderBlock(int Row, int Column, int RowSpan, int ColumnSpan, string Text);

    /// <summary>
    /// 按模板实际合并结构描述的表头区块。
    /// A-M 为三层纵向合并；N-Q 为「仅公司自有资产填写」下的四个二级列；
    /// R-U 与 V-X 分别为「验收及退场记录」「领用记录」下的若干三级列；Y 为三层纵向合并的备注列。
    /// </summary>
    private static readonly HeaderBlock[] _headerBlocks =
    {
        new(4, 1, 3, 1, "序号"),
        new(4, 2, 3, 1, "项目简称"),
        new(4, 3, 3, 1, "使用项目工点"),
        new(4, 4, 3, 1, "类别"),
        new(4, 5, 3, 1, "小型机具名称"),
        new(4, 6, 3, 1, "规格型号"),
        new(4, 7, 3, 1, "计量单位"),
        new(4, 8, 3, 1, "数量"),
        new(4, 9, 3, 1, "配置方式"),
        new(4, 10, 3, 1, "资产所属单位"),
        new(4, 11, 3, 1, "生厂厂家"),
        new(4, 12, 3, 1, "出厂时间"),
        new(4, 13, 3, 1, "性能状态"),

        new(4, 14, 1, 4, "仅公司自有资产填写"),
        new(5, 14, 2, 1, "入账类型"),
        new(5, 15, 2, 1, "采购单价（元）"),
        new(5, 16, 2, 1, "采购金额（元）"),
        new(5, 17, 2, 1, "财务账面价值（元）"),

        new(5, 18, 1, 4, "验收及退场记录"),
        new(6, 18, 1, 1, "进场日期"),
        new(6, 19, 1, 1, "安全验收人员"),
        new(6, 20, 1, 1, "安全验收结果"),
        new(6, 21, 1, 1, "使用保管责任人"),

        new(5, 22, 1, 3, "领用记录"),
        new(6, 22, 1, 1, "领用时间"),
        new(6, 23, 1, 1, "领用单位/厂队"),
        new(6, 24, 1, 1, "领用人"),

        new(4, 25, 3, 1, "备注")
    };

    /// <summary>弹出保存对话框并导出全部小型机具记录。</summary>
    /// <param name="records">要导出的记录集合。</param>
    /// <returns>保存成功返回 true；用户取消返回 false。</returns>
    /// <exception cref="System.IO.IOException">目标文件被占用或无写入权限时抛出。</exception>
    public static Task<bool> ExportAsync(IReadOnlyList<SmallEquipment> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return ExcelExporter.SaveAsync(SheetName, workbook =>
        {
            var sheet = workbook.Worksheets.Add(SheetName);

            ExcelExporter.WriteTitle(sheet, "中国水利水电第十四工程局有限公司", ColumnCount, 1);
            ExcelExporter.WriteTitle(sheet, "小型机具管理台账", ColumnCount, 2);

            // 模板第 3 行左端为更新日期、右端为打印日期，两处都写当前日期即可
            var today = DateTime.Now.ToString("yyyy年M月d日");
            sheet.Cell(3, 1).Value = $"更新日期：{today}";
            sheet.Cell(3, ColumnCount).Value = $"日期：{today}";

            WriteHeaderBlocks(sheet);

            var row = FirstDataRow;
            var index = 1;

            foreach (var record in records)
            {
                WriteRecord(sheet, row, record, index);
                row++;
                index++;
            }

            var lastRow = row - 1;
            ExcelExporter.ApplyTableStyle(sheet, FirstHeaderRow, lastRow, ColumnCount);
            ExcelExporter.SetColumnWidths(sheet, ColumnWidths);
            ExcelExporter.SetNumberFormat(sheet, 8, FirstDataRow, lastRow, "0.##");

            // 冻结前 3 行（两行标题与更新日期行），数据多时向下滚动仍能看到表头区域
            sheet.SheetView.FreezeRows(3);
        });
    }

    /// <summary>把一条记录写入指定行。模板中系统无对应字段的列保持空白。</summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="row">写入行号。</param>
    /// <param name="record">数据来源。</param>
    /// <param name="index">序号，从 1 开始。</param>
    private static void WriteRecord(IXLWorksheet sheet, int row, SmallEquipment record, int index)
    {
        sheet.Cell(row, 1).Value = index;
        sheet.Cell(row, 2).Value = record.ProjectShortName;
        sheet.Cell(row, 3).Value = record.WorkSite;
        sheet.Cell(row, 4).Value = record.Category;
        sheet.Cell(row, 5).Value = record.Name;
        sheet.Cell(row, 6).Value = record.Specification;
        sheet.Cell(row, 7).Value = record.Unit;
        sheet.Cell(row, 8).Value = record.Quantity;
        sheet.Cell(row, 9).Value = record.ConfigMethod;
        sheet.Cell(row, 10).Value = record.AssetOwner;
        sheet.Cell(row, 11).Value = record.Manufacturer;
        sheet.Cell(row, 12).Value = record.FactoryDate;
        sheet.Cell(row, 13).Value = record.PerformanceStatus;

        // 第 14-24 列（仅公司自有资产填写、验收及退场记录、领用记录）系统内无对应字段，
        // 保持空白以维持模板版式；第 25 列备注系统内亦无字段，同样留空
    }

    /// <summary>按描述表逐块写入并合并表头区域。</summary>
    /// <param name="sheet">目标工作表。</param>
    private static void WriteHeaderBlocks(IXLWorksheet sheet)
    {
        foreach (var block in _headerBlocks)
        {
            var range = sheet.Range(
                block.Row,
                block.Column,
                block.Row + block.RowSpan - 1,
                block.Column + block.ColumnSpan - 1);

            if (block.RowSpan > 1 || block.ColumnSpan > 1)
            {
                range.Merge();
            }

            range.Value = block.Text;
            range.Style.Font.Bold = true;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Alignment.WrapText = true;
        }

        // 表头三行给足高度，否则合并后的长标题会被压扁看不清
        sheet.Row(4).Height = 28;
        sheet.Row(5).Height = 24;
        sheet.Row(6).Height = 24;
    }
}
