using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 地材台账的模板导出适配器。按 12 列模板版式输出，
/// 金额列使用 Excel 公式 =数量*单价，不写计算值。
/// </summary>
public static class EarthLedgerTemplateExport
{
    /// <summary>工作表名。</summary>
    public const string SheetName = "地材台账";

    /// <summary>表头所在行号。</summary>
    private const int HeaderRow = 2;

    /// <summary>首条数据所在行号。</summary>
    private const int FirstDataRow = 3;

    /// <summary>12 列表头文字，顺序即模板列序。</summary>
    public static readonly string[] Headers =
    {
        "序号", "入库日期", "编号", "商品名称", "商品规格", "单位",
        "数量", "单价", "金额", "经办人", "收货位置", "备注"
    };

    /// <summary>12 列列宽（字符宽）；G 列填 0 表示沿用自动宽度。</summary>
    public static readonly double[] ColumnWidths =
    {
        7.6, 13.3, 7.1, 12.9, 7.1, 7.1, 0, 9.1, 11.9, 9.9, 20.4, 12.3
    };

    /// <summary>弹出保存对话框，导出全部地材台账记录。</summary>
    /// <param name="records">要导出的记录集合，通常为 DB.GetMaterialLedgers() 的返回值。</param>
    /// <param name="fileNameSuffix">建议文件名后缀（如期数筛选时传「-三期」）；为空时文件名不带后缀。工作表名保持「地材台账」不变。</param>
    /// <returns>保存成功返回 true；用户取消返回 false。</returns>
    /// <exception cref="System.IO.IOException">目标文件被占用或无写入权限时抛出。</exception>
    public static Task<bool> ExportAsync(IReadOnlyList<MaterialLedger> records, string? fileNameSuffix = null)
    {
        ArgumentNullException.ThrowIfNull(records);

        var suggestedName = string.IsNullOrEmpty(fileNameSuffix) ? SheetName : SheetName + fileNameSuffix;

        return ExcelExporter.SaveAsync(suggestedName, workbook =>
        {
            var sheet = workbook.Worksheets.Add(SheetName);

            ExcelExporter.WriteTitle(sheet, SheetName, Headers.Length, 1);
            ExcelExporter.WriteHeader(sheet, Headers, HeaderRow);

            var row = FirstDataRow;
            var index = 1;

            foreach (var record in records)
            {
                sheet.Cell(row, 1).Value = index;
                sheet.Cell(row, 2).Value = NormalizeDate(record.EntryDate);
                sheet.Cell(row, 3).Value = record.DocNo;
                sheet.Cell(row, 4).Value = record.MaterialName;
                sheet.Cell(row, 5).Value = record.Specification;
                sheet.Cell(row, 6).Value = record.Unit;
                sheet.Cell(row, 7).Value = record.Quantity;
                sheet.Cell(row, 8).Value = record.UnitPrice;

                // 金额写公式而非计算值：指令文档要求保留 =数量*单价，用户改数后 Excel 会自动重算
                sheet.Cell(row, 9).FormulaA1 = $"G{row}*H{row}";

                sheet.Cell(row, 10).Value = record.Handler;
                sheet.Cell(row, 11).Value = record.ProjectAddress;
                sheet.Cell(row, 12).Value = record.Remark;

                row++;
                index++;
            }

            var lastRow = row - 1;

            // 记录为空时 lastRow 会小于表头行，ApplyTableStyle 内部已做保护，这里只需保证不越界
            ExcelExporter.ApplyTableStyle(sheet, HeaderRow, lastRow, Headers.Length);
            ExcelExporter.SetNumberFormat(sheet, 7, FirstDataRow, lastRow);
            ExcelExporter.SetNumberFormat(sheet, 8, FirstDataRow, lastRow);
            ExcelExporter.SetNumberFormat(sheet, 9, FirstDataRow, lastRow);
            ExcelExporter.SetColumnWidths(sheet, ColumnWidths);

            // 冻结标题与表头两行，数据多时向下滚动仍能看到列名
            sheet.SheetView.FreezeRows(HeaderRow);
        });
    }

    /// <summary>把日期规范为 yyyy-MM-dd；无法解析时原样返回，避免把脏数据写成空。</summary>
    /// <param name="value">原始日期文本。</param>
    /// <returns>规范化后的日期文本。</returns>
    private static string NormalizeDate(string value)
    {
        return DateTime.TryParse(value, out var parsed) ? parsed.ToString("yyyy-MM-dd") : value;
    }
}
