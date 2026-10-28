using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Windows.Storage.Pickers;

namespace MaterialManagementApp.Common;

/// <summary>
/// 基于 ClosedXML 的 Excel 导入引擎。负责打开对话框、Sheet 与表头定位、行数据抽取，
/// 不含业务字段映射；业务语义由各页面或 Services\Export 下的适配类完成。
/// </summary>
public static class ExcelImporter
{
    /// <summary>导入时需要跳过的非数据行关键字。</summary>
    private static readonly string[] _skipKeywords = { "合计", "小计", "累计", "签字", "审核", "制表", "负责人" };

    /// <summary>弹出打开对话框并读取选中的 xlsx。</summary>
    /// <returns>用户取消或无法获取窗口句柄时返回 null；否则返回已打开的工作簿。</returns>
    /// <exception cref="InvalidDataException">文件不是合法的 xlsx 时抛出，由调用方提示。</exception>
    public static async Task<IXLWorkbook?> PickAndOpenAsync()
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return null;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".xlsx");

        // 同保存对话框，必须先绑定窗口句柄再调用 PickSingleFileAsync
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return null;
        }

        using var source = await file.OpenStreamForReadAsync();

        // 整份读入内存再交给 ClosedXML：直接读用户文件流会长期占用文件，
        // 导致用户随后想「另存为」或移动文件时失败
        var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        buffer.Position = 0;

        // 故意不 Dispose buffer：ClosedXML 部分 API 会延迟读取，提前释放会抛 ObjectDisposedException，
        // 内存流无原生句柄，交由 GC 回收即可
        return new XLWorkbook(buffer);
    }

    /// <summary>按关键字模糊匹配 Sheet 名，全部匹配不到时退回第一个工作表。</summary>
    /// <param name="workbook">已打开的工作簿。</param>
    /// <param name="nameKeywords">Sheet 名包含的关键字，按顺序优先匹配。</param>
    /// <returns>命中的工作表；工作簿无任何 Sheet 时返回 null。</returns>
    public static IXLWorksheet? FindWorksheet(IXLWorkbook workbook, params string[] nameKeywords)
    {
        ArgumentNullException.ThrowIfNull(workbook);

        if (nameKeywords is not null)
        {
            foreach (var keyword in nameKeywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    continue;
                }

                foreach (var worksheet in workbook.Worksheets)
                {
                    if (worksheet.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        return worksheet;
                    }
                }
            }
        }

        return workbook.Worksheets.FirstOrDefault();
    }

    /// <summary>
    /// 以指定表头行作为列名来源，读取其后的数据行。
    /// 返回的每个字典以「表头文字（已 Trim）」为键、单元格文本为值。
    /// </summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="headerRow">表头所在行号，从 1 开始。</param>
    /// <param name="firstDataRow">首条数据所在行号，从 1 开始。</param>
    /// <returns>数据行字典集合，已剔除空行与合计/签字类行。</returns>
    public static List<Dictionary<string, string>> ReadRows(IXLWorksheet sheet, int headerRow, int firstDataRow)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var result = new List<Dictionary<string, string>>();
        var headerMap = BuildHeaderMap(sheet, headerRow);

        if (headerMap.Count == 0)
        {
            return result;
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow;

        for (var row = firstDataRow; row <= lastRow; row++)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in headerMap)
            {
                values[pair.Value] = GetCellText(sheet.Cell(row, pair.Key));
            }

            if (ShouldSkipRow(values))
            {
                continue;
            }

            result.Add(values);
        }

        return result;
    }

    /// <summary>
    /// 读取两行多级表头，返回合并后的完整列标题：
    /// 第二行有文字时以第二行为准，否则取第一行（对应纵向合并的列）。
    /// </summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="firstHeaderRow">第一层表头行号，从 1 开始。</param>
    /// <param name="secondHeaderRow">第二层表头行号，从 1 开始。</param>
    /// <returns>按列顺序排列的列标题，空标题列为空串。</returns>
    public static List<string> ReadMergedHeaders(IXLWorksheet sheet, int firstHeaderRow, int secondHeaderRow)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var headers = new List<string>();
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        for (var column = 1; column <= lastColumn; column++)
        {
            var second = GetCellText(sheet.Cell(secondHeaderRow, column));
            var first = GetCellText(sheet.Cell(firstHeaderRow, column));

            headers.Add(string.IsNullOrWhiteSpace(second) ? first : second);
        }

        return headers;
    }

    /// <summary>判断该行是否为需要跳过的非数据行（空行或合计/签字类行）。</summary>
    /// <param name="row">已按表头映射出的行数据。</param>
    /// <returns>需要跳过返回 true。</returns>
    public static bool ShouldSkipRow(IReadOnlyDictionary<string, string> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var values = row.Values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (values.Count == 0)
        {
            return true;
        }

        // 首列是纯数字（序号）时一定是数据行，避免「商品名称」里恰好含「合计」等字样被误跳过
        if (double.TryParse(values[0], out _))
        {
            return false;
        }

        var joined = string.Concat(values);
        foreach (var keyword in _skipKeywords)
        {
            if (joined.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>构造「列号 → 表头文字」映射，跳过空标题列。</summary>
    /// <param name="sheet">目标工作表。</param>
    /// <param name="headerRow">表头所在行号。</param>
    /// <returns>列号到标题的映射。</returns>
    private static Dictionary<int, string> BuildHeaderMap(IXLWorksheet sheet, int headerRow)
    {
        var map = new Dictionary<int, string>();
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        for (var column = 1; column <= lastColumn; column++)
        {
            var title = NormalizeHeader(GetCellText(sheet.Cell(headerRow, column)));
            if (string.IsNullOrEmpty(title) || map.ContainsValue(title))
            {
                continue;
            }

            map[column] = title;
        }

        return map;
    }

    /// <summary>把单元格内容转为可解析的文本，日期统一为 yyyy-MM-dd。</summary>
    /// <param name="cell">目标单元格。</param>
    /// <returns>单元格文本；空单元格返回空串。</returns>
    private static string GetCellText(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return string.Empty;
        }

        return cell.DataType switch
        {
            // 不能直接用 GetFormattedString：日期会依赖单元格格式，容易读出序列号或带时间的长串
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd"),
            XLDataType.Number => cell.GetDouble().ToString("0.####"),
            XLDataType.Boolean => cell.GetBoolean() ? "是" : "否",
            _ => cell.GetString().Trim()
        };
    }

    /// <summary>规范化表头文字：去掉换行与多余空白，便于与业务字典精准匹配。</summary>
    /// <param name="title">原始表头文字。</param>
    /// <returns>去除换行与首尾空白后的标题。</returns>
    private static string NormalizeHeader(string title)
    {
        return title.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
    }
}
