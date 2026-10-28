using System.Collections.Generic;
using System.Threading.Tasks;
using MaterialManagementApp.Common;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 通用列表导出工具。各台账页面的「导出」按钮统一走这里：
/// 给定工作表名、表头与行数据，生成单工作表的 xlsx 并弹出保存对话框。
/// 适用于没有专属模板版式要求的页面；有专属模板的页面仍走各自的 TemplateExport。
/// </summary>
public static class SimpleListExport
{
    /// <summary>弹出保存对话框，把行数据按表头顺序写入单工作表。</summary>
    /// <param name="sheetName">工作表名，同时作为建议文件名。</param>
    /// <param name="headers">表头文字，顺序即列序。</param>
    /// <param name="rows">行数据；每行的列数应与表头一致，元素为显示文本。</param>
    /// <returns>保存成功返回 true；用户取消返回 false。</returns>
    public static Task<bool> ExportAsync(
        string sheetName,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        return ExcelExporter.SaveAsync(sheetName, workbook =>
        {
            var sheet = workbook.Worksheets.Add(sheetName);

            ExcelExporter.WriteHeader(sheet, headers, 1);

            var rowIndex = 2;
            foreach (var row in rows)
            {
                for (var column = 0; column < headers.Count && column < row.Count; column++)
                {
                    sheet.Cell(rowIndex, column + 1).Value = row[column];
                }

                rowIndex++;
            }

            // 无数据时 ApplyTableStyle 内部已保护，不会越界
            ExcelExporter.ApplyTableStyle(sheet, 1, rowIndex - 1, headers.Count);
            sheet.SheetView.FreezeRows(1);

            for (var column = 1; column <= headers.Count; column++)
            {
                sheet.Column(column).AdjustToContents(1, System.Math.Max(1, rowIndex - 1));
            }
        });
    }
}
