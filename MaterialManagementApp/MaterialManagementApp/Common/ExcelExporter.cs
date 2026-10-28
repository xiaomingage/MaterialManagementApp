using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Windows.Storage.Pickers;

namespace MaterialManagementApp.Common;

/// <summary>
/// 多级表头中的单个表头单元，描述文字与横向/纵向合并跨度。
/// </summary>
/// <param name="Text">表头文字。</param>
/// <param name="ColSpan">横向合并的列数，默认 1。</param>
/// <param name="RowSpan">纵向合并的行数，默认 1（用于两行表头中跨行的列）。</param>
public readonly record struct HeaderCell(string Text, int ColSpan = 1, int RowSpan = 1);

/// <summary>
/// 基于 ClosedXML 的 Excel 导出引擎。负责保存对话框与模板样式助手，
/// 不含任何业务字段知识；业务语义由 Services\Export 下的适配类提供。
/// </summary>
public static class ExcelExporter
{
    /// <summary>弹出保存对话框并把构建好的工作簿写入用户选择的位置。</summary>
    /// <param name="suggestedFileName">建议文件名（不含扩展名）。</param>
    /// <param name="build">在工作簿上写入内容的委托，由调用方决定版式。</param>
    /// <returns>保存成功返回 true；用户取消或无法获取窗口句柄时返回 false。</returns>
    /// <exception cref="IOException">目标文件被占用或无写入权限时抛出，由调用方提示。</exception>
    public static async Task<bool> SaveAsync(string suggestedFileName, Action<IXLWorkbook> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        // 工作簿由本方法负责释放，调用方不必关心
        using var workbook = new XLWorkbook();
        build(workbook);

        return await SaveWorkbookAsync(suggestedFileName, workbook);
    }

    /// <summary>弹出保存对话框并保存已构建完成的工作簿。</summary>
    /// <param name="suggestedFileName">建议文件名（不含扩展名）。</param>
    /// <param name="workbook">已写入内容的工作簿，本方法只保存不释放。</param>
    /// <returns>保存成功返回 true；用户取消时返回 false。</returns>
    public static Task<bool> SaveAsync(string suggestedFileName, IXLWorkbook workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        return SaveWorkbookAsync(suggestedFileName, workbook);
    }

    /// <summary>在指定行写入横向合并的大标题。</summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="title">标题文字。</param>
    /// <param name="columnCount">合并到的列数（从第 1 列起）。</param>
    /// <param name="row">标题所在行号，从 1 开始。</param>
    public static void WriteTitle(IXLWorksheet sheet, string title, int columnCount, int row = 1)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (columnCount < 1)
        {
            return;
        }

        var range = sheet.Range(row, 1, row, columnCount);
        if (columnCount > 1)
        {
            range.Merge();
        }

        range.Value = title;
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 16;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(row).Height = 28;
    }

    /// <summary>在指定起始行写入单行表头。</summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="headers">表头文字，顺序即列序。</param>
    /// <param name="startRow">表头所在行号，从 1 开始。</param>
    public static void WriteHeader(IXLWorksheet sheet, IReadOnlyList<string> headers, int startRow)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(headers);

        for (var i = 0; i < headers.Count; i++)
        {
            var range = sheet.Cell(startRow, i + 1).AsRange();
            range.Value = headers[i];
            ApplyHeaderStyle(range);
        }
    }

    /// <summary>在指定起始行写入两行多级合并表头。</summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="firstRow">第一层表头，可含跨列（ColSpan）与跨行（RowSpan）。</param>
    /// <param name="secondRow">
    /// 第二层表头，按第一层展开后的每一列逐项给出；
    /// 某列若已被第一层纵向合并，对应位置传 null 表示不再写文字，该列只前进一列。
    /// </param>
    /// <param name="startRow">第一层表头所在行号，从 1 开始。</param>
    public static void WriteMergedHeader(
        IXLWorksheet sheet,
        IReadOnlyList<HeaderCell> firstRow,
        IReadOnlyList<HeaderCell?> secondRow,
        int startRow)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(firstRow);

        var column = 1;

        foreach (var header in firstRow)
        {
            var colSpan = Math.Max(header.ColSpan, 1);
            var rowSpan = Math.Max(header.RowSpan, 1);

            var range = sheet.Range(startRow, column, startRow + rowSpan - 1, column + colSpan - 1);
            if (colSpan > 1 || rowSpan > 1)
            {
                range.Merge();
            }

            range.Value = header.Text;
            ApplyHeaderStyle(range);

            column += colSpan;
        }

        if (secondRow is null || secondRow.Count == 0)
        {
            return;
        }

        var secondHeaderRow = startRow + 1;
        column = 1;

        foreach (var cell in secondRow)
        {
            if (cell is { } header && !string.IsNullOrEmpty(header.Text))
            {
                var colSpan = Math.Max(header.ColSpan, 1);
                var range = sheet.Range(secondHeaderRow, column, secondHeaderRow, column + colSpan - 1);
                if (colSpan > 1)
                {
                    range.Merge();
                }

                range.Value = header.Text;
                ApplyHeaderStyle(range);

                column += colSpan;
            }
            else
            {
                // null 表示该列已被第一层纵向合并占用，只前进一列不写值
                column++;
            }
        }
    }

    /// <summary>批量设置列宽（单位：字符宽）。</summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="widths">
    /// 各列宽度，数组下标 0 对应第 1 列；某项小于等于 0 表示该列保持自动宽度。
    /// </param>
    public static void SetColumnWidths(IXLWorksheet sheet, params double[] widths)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(widths);

        for (var i = 0; i < widths.Length; i++)
        {
            if (widths[i] > 0)
            {
                sheet.Column(i + 1).Width = widths[i];
            }
        }
    }

    /// <summary>为指定区域加细边框、垂直居中与自动换行。</summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="firstRow">起始行号（含）。</param>
    /// <param name="lastRow">结束行号（含）；小于起始行时直接返回。</param>
    /// <param name="columnCount">区域列数（从第 1 列起）。</param>
    public static void ApplyTableStyle(IXLWorksheet sheet, int firstRow, int lastRow, int columnCount)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (lastRow < firstRow || columnCount < 1)
        {
            return;
        }

        var range = sheet.Range(firstRow, 1, lastRow, columnCount);
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
    }

    /// <summary>把数据区某一列设置为指定数值格式。</summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="column">列号，从 1 开始。</param>
    /// <param name="firstRow">起始行号（含）。</param>
    /// <param name="lastRow">结束行号（含）；小于起始行时直接返回。</param>
    /// <param name="format">Excel 数值格式字符串，默认两位小数。</param>
    public static void SetNumberFormat(IXLWorksheet sheet, int column, int firstRow, int lastRow, string format = "0.00")
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (lastRow < firstRow || column < 1)
        {
            return;
        }

        sheet.Range(firstRow, column, lastRow, column).Style.NumberFormat.Format = format;
    }

    /// <summary>为表头区域套用统一字体与居中样式。</summary>
    /// <param name="range">表头单元格区域，可能是合并后的区域。</param>
    private static void ApplyHeaderStyle(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
    }

    /// <summary>弹出保存对话框并把工作簿写入用户选择的文件。</summary>
    /// <param name="suggestedFileName">建议文件名（不含扩展名）。</param>
    /// <param name="workbook">待保存的工作簿。</param>
    /// <returns>保存成功返回 true；用户取消或无法获取窗口句柄时返回 false。</returns>
    private static async Task<bool> SaveWorkbookAsync(string suggestedFileName, IXLWorkbook workbook)
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return false;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add("Excel 工作簿", new List<string> { ".xlsx" });

        // WinUI 3 的保存对话框是 Win32 对话框，必须先绑定窗口句柄，否则构造后直接抛异常
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        using var stream = await file.OpenStreamForWriteAsync();

        // 用户选择已存在的同名文件时，若不清空会把新内容覆盖到旧内容的开头，尾部残留旧字节
        stream.SetLength(0);
        workbook.SaveAs(stream);
        await stream.FlushAsync();
        return true;
    }
}
