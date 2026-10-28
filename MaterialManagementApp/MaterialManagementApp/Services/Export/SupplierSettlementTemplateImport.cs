using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MaterialManagementApp.Common;
using MaterialManagementApp.Services;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 月度结算模板导入适配器：按供应商名称匹配并更新本月结算金额。
/// 匹配不到的供应商名称会收集返回，便于界面提示用户核对。
/// </summary>
public static class SupplierSettlementTemplateImport
{
    /// <summary>表头所在行号。</summary>
    public const int HeaderRow = 2;

    /// <summary>首条数据所在行号。</summary>
    public const int FirstDataRow = 3;

    /// <summary>弹出打开对话框并解析。</summary>
    /// <returns>用户取消返回 null；否则返回「供应商名称、结算金额、签订日期、备注」四元组列表。</returns>
    /// <exception cref="System.IO.InvalidDataException">文件不是合法的 xlsx 时抛出。</exception>
    public static async Task<List<(string Name, double Amount, string SignDate, string Remark)>?> PickAndParseAsync()
    {
        using var workbook = await ExcelImporter.PickAndOpenAsync();
        if (workbook is null)
        {
            return null;
        }

        var sheet = ExcelImporter.FindWorksheet(workbook, "月度结算", "结算");
        if (sheet is null)
        {
            return new List<(string, double, string, string)>();
        }

        var rows = ExcelImporter.ReadRows(sheet, HeaderRow, FirstDataRow);
        var result = new List<(string Name, double Amount, string SignDate, string Remark)>();

        foreach (var row in rows)
        {
            var name = GetValue(row, "供应商名称");
            if (string.IsNullOrWhiteSpace(name))
            {
                // 供应商名称为空说明不是有效数据行（如合计行的前置空列），跳过
                continue;
            }

            result.Add((
                name,
                ParseDouble(GetValue(row, "月度结算金额（元）", "月度结算金额", "月度结算")),
                GetValue(row, "签订日期"),
                GetValue(row, "备注")));
        }

        return result;
    }

    /// <summary>按供应商名称匹配更新，未匹配到的名称收集返回。</summary>
    /// <param name="items">已解析的结算信息。</param>
    /// <returns>更新成功条数与未匹配的供应商名称列表。</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">SQL 执行失败时抛出。</exception>
    public static (int Updated, List<string> NotFound) Commit(
        IReadOnlyList<(string Name, double Amount, string SignDate, string Remark)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 一次性取出全部供应商建索引，避免逐行查询导致的 N 次数据库往返
        var suppliers = DatabaseService.Instance.GetSuppliers()
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var updated = 0;
        var notFound = new List<string>();

        foreach (var item in items)
        {
            if (!suppliers.TryGetValue(item.Name, out var supplier))
            {
                notFound.Add(item.Name);
                continue;
            }

            supplier.MonthlySettlement = item.Amount;

            // 日期与备注为空时保留原值，避免导入一份只含金额的表把已有信息清空
            if (!string.IsNullOrWhiteSpace(item.SignDate))
            {
                supplier.SignDate = item.SignDate;
            }

            if (!string.IsNullOrWhiteSpace(item.Remark))
            {
                supplier.Remark = item.Remark;
            }

            DatabaseService.Instance.UpdateSupplier(supplier);
            updated++;
        }

        return (updated, notFound);
    }

    /// <summary>按列标题取值，支持多个候选标题。</summary>
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
