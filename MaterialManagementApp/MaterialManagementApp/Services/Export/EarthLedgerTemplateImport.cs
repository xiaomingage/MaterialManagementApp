using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 地材台账的模板导入适配器：把模板行数据转换成实体集合，并可批量写入数据库。
/// 表头文字按模板固定列名匹配，同时兼容用户另存后可能出现的别名。
/// </summary>
public static class EarthLedgerTemplateImport
{
    /// <summary>模板表头所在行号。</summary>
    public const int HeaderRow = 2;

    /// <summary>首条数据所在行号。</summary>
    public const int FirstDataRow = 3;

    /// <summary>弹出打开对话框，读取模板行并解析为实体集合。</summary>
    /// <returns>用户取消返回 null；否则返回解析结果（可能为空集合）。</returns>
    /// <exception cref="System.IO.InvalidDataException">文件不是合法的 xlsx 时抛出。</exception>
    public static async Task<List<MaterialLedger>?> PickAndParseAsync()
    {
        using var workbook = await ExcelImporter.PickAndOpenAsync();
        if (workbook is null)
        {
            return null;
        }

        var sheet = ExcelImporter.FindWorksheet(workbook, "地材", "台账");
        if (sheet is null)
        {
            return new List<MaterialLedger>();
        }

        var rows = ExcelImporter.ReadRows(sheet, HeaderRow, FirstDataRow);
        var result = new List<MaterialLedger>();

        foreach (var row in rows)
        {
            var materialName = GetValue(row, "商品名称");
            if (string.IsNullOrWhiteSpace(materialName))
            {
                // 商品名称为空说明不是有效数据行，跳过而不是写入空记录
                continue;
            }

            var quantity = ParseDouble(GetValue(row, "数量"));
            var unitPrice = ParseDouble(GetValue(row, "单价"));

            result.Add(new MaterialLedger
            {
                EntryDate = NormalizeDate(GetValue(row, "入库日期")),
                DocNo = GetValue(row, "编号"),
                MaterialName = materialName,
                Specification = GetValue(row, "商品规格", "规格"),
                Unit = GetValue(row, "单位"),
                Quantity = quantity,
                UnitPrice = unitPrice,

                // 金额不读模板值：模板里公式若未重算会读到 0 或旧值，统一按数量×单价重算
                Amount = Math.Round(quantity * unitPrice, 2),
                Handler = GetValue(row, "经办人"),
                ProjectAddress = GetValue(row, "收货位置"),
                Remark = GetValue(row, "备注")
            });
        }

        return result;
    }

    /// <summary>把已解析的实体集合写入数据库。</summary>
    /// <param name="items">解析结果。</param>
    /// <param name="replaceAll">true 表示先清空现有地材台账再写入。</param>
    /// <returns>实际写入条数。</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">SQL 执行失败时抛出。</exception>
    public static int Commit(IReadOnlyList<MaterialLedger> items, bool replaceAll)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (replaceAll)
        {
            DatabaseService.Instance.ExecuteNonQuery("DELETE FROM MaterialLedger;");
        }

        var count = 0;
        foreach (var item in items)
        {
            DatabaseService.Instance.AddMaterialLedger(item);
            count++;
        }

        return count;
    }

    /// <summary>按表头文字取值，支持多个候选标题（兼容用户改过表头的情况）。</summary>
    /// <param name="row">一行数据。</param>
    /// <param name="titles">候选表头文字，按顺序优先匹配。</param>
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

    /// <summary>解析数值，去除千分位与全角空格后按当前文化解析；失败返回 0。</summary>
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

    /// <summary>把模板中的多种日期写法统一为 yyyy-MM-dd，无法解析时回退当天。</summary>
    /// <param name="value">原始日期文本，可能是「2026.8.2」这类写法。</param>
    /// <returns>yyyy-MM-dd 格式日期。</returns>
    private static string NormalizeDate(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToString("yyyy-MM-dd")
            : DateTime.Now.ToString("yyyy-MM-dd");
    }
}
