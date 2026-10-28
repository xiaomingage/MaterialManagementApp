using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 月度结算模板导出适配器。数据优先取结算汇总，缺失字段回查供应商表补齐，
/// 合计行使用 SUM 公式而非计算值。
/// </summary>
public static class SupplierSettlementTemplateExport
{
    /// <summary>工作表名。</summary>
    public const string SheetName = "月度结算";

    /// <summary>表头所在行号。</summary>
    private const int HeaderRow = 2;

    /// <summary>首条数据所在行号。</summary>
    private const int FirstDataRow = 3;

    /// <summary>8 列表头文字，顺序即模板列序。</summary>
    public static readonly string[] Headers =
    {
        "序号", "供应商名称", "合同编号", "联系人", "电话", "月度结算金额（元）", "签订日期", "备注"
    };

    /// <summary>8 列列宽（字符宽）。</summary>
    public static readonly double[] ColumnWidths = { 7, 22, 18, 12, 16, 18, 14, 20 };

    /// <summary>导出月度结算表。</summary>
    /// <param name="month">结算月份，用于标题文字。</param>
    /// <returns>保存成功返回 true；用户取消返回 false。</returns>
    /// <exception cref="System.IO.IOException">目标文件被占用或无写入权限时抛出。</exception>
    public static Task<bool> ExportAsync(DateTime month)
    {
        // 结算汇总不含联系人与电话，按供应商名称建索引回查补齐
        var summary = DatabaseService.Instance.GetSupplierSettlementSummary();
        var suppliers = DatabaseService.Instance.GetSuppliers()
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return ExcelExporter.SaveAsync(SheetName, workbook =>
        {
            var sheet = workbook.Worksheets.Add(SheetName);

            ExcelExporter.WriteTitle(sheet, $"{month:yyyy年MM月}月度结算表", Headers.Length, 1);
            ExcelExporter.WriteHeader(sheet, Headers, HeaderRow);

            var row = FirstDataRow;
            var index = 1;

            foreach (var item in summary)
            {
                suppliers.TryGetValue(item.Name, out var supplier);

                sheet.Cell(row, 1).Value = index;
                sheet.Cell(row, 2).Value = item.Name;
                sheet.Cell(row, 3).Value = item.ContractNo;
                sheet.Cell(row, 4).Value = supplier?.ContactPerson ?? string.Empty;
                sheet.Cell(row, 5).Value = supplier?.Phone ?? string.Empty;
                sheet.Cell(row, 6).Value = item.MonthlySettlement;
                sheet.Cell(row, 7).Value = item.SignDate;
                sheet.Cell(row, 8).Value = supplier?.Remark ?? string.Empty;

                row++;
                index++;
            }

            var lastDataRow = row - 1;

            if (summary.Count > 0)
            {
                // 合计行：A 列写「合计」并向右合并到 E 列，F 列写 SUM 公式而非计算值
                var totalRange = sheet.Range(row, 1, row, 5);
                totalRange.Merge();
                totalRange.Value = "合计";
                totalRange.Style.Font.Bold = true;
                totalRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                sheet.Cell(row, 6).FormulaA1 = $"SUM(F{FirstDataRow}:F{lastDataRow})";
                sheet.Cell(row, 6).Style.Font.Bold = true;
            }

            var lastRow = summary.Count > 0 ? row : lastDataRow;
            ExcelExporter.ApplyTableStyle(sheet, HeaderRow, lastRow, Headers.Length);
            ExcelExporter.SetNumberFormat(sheet, 6, FirstDataRow, lastRow);
            ExcelExporter.SetColumnWidths(sheet, ColumnWidths);
            sheet.SheetView.FreezeRows(HeaderRow);
        });
    }
}
