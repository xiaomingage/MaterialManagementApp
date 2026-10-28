using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using ClosedXML.Excel;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 小型机具管理台账的模板导入适配器。
/// 模板为三层合并表头，读取时自下而上取第一个非空文字作为该列标题。
/// </summary>
public static class SmallEquipmentTemplateImport
{
    /// <summary>表头第一层行号。</summary>
    public const int FirstHeaderRow = 4;

    /// <summary>表头第二层行号。</summary>
    public const int SecondHeaderRow = 5;

    /// <summary>表头第三层行号。</summary>
    public const int ThirdHeaderRow = 6;

    /// <summary>首条数据行号。</summary>
    public const int FirstDataRow = 7;

    /// <summary>弹出打开对话框并解析为实体集合。</summary>
    /// <returns>用户取消返回 null；否则返回解析结果（可能为空集合）。</returns>
    /// <exception cref="System.IO.InvalidDataException">文件不是合法的 xlsx 时抛出。</exception>
    public static async Task<List<SmallEquipment>?> PickAndParseAsync()
    {
        using var workbook = await ExcelImporter.PickAndOpenAsync();
        if (workbook is null)
        {
            return null;
        }

        var sheet = ExcelImporter.FindWorksheet(workbook, "小型机具", "台账");
        if (sheet is null)
        {
            return new List<SmallEquipment>();
        }

        var headerMap = BuildHeaderMap(sheet);
        if (headerMap.Count == 0)
        {
            return new List<SmallEquipment>();
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? FirstDataRow;
        var result = new List<SmallEquipment>();

        for (var row = FirstDataRow; row <= lastRow; row++)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in headerMap)
            {
                values[pair.Value] = GetCellText(sheet.Cell(row, pair.Key));
            }

            if (ExcelImporter.ShouldSkipRow(values))
            {
                continue;
            }

            var name = GetValue(values, "小型机具名称", "名称");
            if (string.IsNullOrWhiteSpace(name))
            {
                // 名称为空说明不是有效数据行，跳过而不是写入空记录
                continue;
            }

            result.Add(new SmallEquipment
            {
                ProjectShortName = GetValue(values, "项目简称"),
                WorkSite = GetValue(values, "使用项目工点", "作业地点"),
                Category = GetValue(values, "类别"),
                Name = name,
                Specification = GetValue(values, "规格型号", "规格"),
                Unit = GetValue(values, "计量单位", "单位"),
                Quantity = ParseDouble(GetValue(values, "数量")),
                ConfigMethod = GetValue(values, "配置方式"),
                AssetOwner = GetValue(values, "资产所属单位"),
                Manufacturer = GetValue(values, "生厂厂家", "生产厂家"),
                FactoryDate = GetValue(values, "出厂时间"),
                PerformanceStatus = GetValue(values, "性能状态")
            });
        }

        return result;
    }

    /// <summary>把已解析的实体集合写入数据库。</summary>
    /// <param name="items">解析结果。</param>
    /// <param name="replaceAll">true 表示先清空现有小型机具台账再写入。</param>
    /// <returns>实际写入条数。</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">SQL 执行失败时抛出。</exception>
    public static int Commit(IReadOnlyList<SmallEquipment> items, bool replaceAll)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (replaceAll)
        {
            DatabaseService.Instance.ExecuteNonQuery("DELETE FROM SmallEquipment;");
        }

        var count = 0;
        foreach (var item in items)
        {
            DatabaseService.Instance.AddSmallEquipment(item);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 构造「列号 → 列标题」映射。三层表头中合并单元格只有左上角有值，
    /// 因此自下而上取第一个非空文字，才能拿到最具体的字段名。
    /// </summary>
    /// <param name="sheet">目标工作表。</param>
    /// <returns>列号到标题的映射，跳过标题为空的列与重复标题。</returns>
    private static Dictionary<int, string> BuildHeaderMap(IXLWorksheet sheet)
    {
        var map = new Dictionary<int, string>();
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        for (var column = 1; column <= lastColumn; column++)
        {
            var title = GetCellText(sheet.Cell(ThirdHeaderRow, column));

            if (string.IsNullOrWhiteSpace(title))
            {
                title = GetCellText(sheet.Cell(SecondHeaderRow, column));
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = GetCellText(sheet.Cell(FirstHeaderRow, column));
            }

            title = title.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

            if (string.IsNullOrEmpty(title) || map.ContainsValue(title))
            {
                continue;
            }

            map[column] = title;
        }

        return map;
    }

    /// <summary>按列标题取值，支持多个候选标题（兼容用户改过表头或系统写法差异）。</summary>
    /// <param name="row">一行数据。</param>
    /// <param name="titles">候选标题，按顺序优先匹配。</param>
    /// <returns>命中的单元格文本；都未命中时返回空串。</returns>
    private static string GetValue(IReadOnlyDictionary<string, string> row, params string[] titles)
    {
        foreach (var title in titles)
        {
            if (row.TryGetValue(title, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>把单元格内容转为可解析文本，日期统一为 yyyy-MM-dd。</summary>
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
            // 不走 GetFormattedString：日期会依赖单元格格式，容易读出序列号
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd"),
            XLDataType.Number => cell.GetDouble().ToString("0.####"),
            XLDataType.Boolean => cell.GetBoolean() ? "是" : "否",
            _ => cell.GetString().Trim()
        };
    }

    /// <summary>解析数值，去除千分位后按当前文化解析；失败返回 0。</summary>
    /// <param name="value">原始数值文本。</param>
    /// <returns>解析出的数值。</returns>
    private static double ParseDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var cleaned = value.Replace(",", string.Empty).Replace("，", string.Empty).Trim();
        return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : 0;
    }
}
