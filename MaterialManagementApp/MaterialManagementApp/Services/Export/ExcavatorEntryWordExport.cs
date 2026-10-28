using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 挖机入场资料的 Word 导出适配器。以模板 docx 为基底填充 21 行 × 5 列表格中的
/// 18 个业务字段，保留合并单元格与原有照片占位。
/// </summary>
public static class ExcavatorEntryWordExport
{
    /// <summary>
    /// 字段与模板表格行列的映射：键为 (行索引, 单元格索引)，值按实体取值。
    /// 行索引对应模板表格的 21 行（0 基）：第 4、6 行是设备与人员信息值行，
    /// 第 9/10/12/14/17/20 行分别是附属设备、随机资料、随机工具、机况、验收结果、验收人员的内容区。
    /// </summary>
    private static readonly Dictionary<(int Row, int Cell), Func<ExcavatorEntry, string>> _cellMap = new()
    {
        [(4, 0)] = entry => entry.EquipmentName,
        [(4, 1)] = entry => entry.Model,
        [(4, 2)] = entry => entry.FactoryCode,
        [(4, 3)] = entry => entry.FactoryDate,

        [(6, 0)] = entry => entry.Manufacturer,
        [(6, 1)] = entry => entry.StorageLocation,
        [(6, 2)] = entry => entry.Operator,
        [(6, 3)] = entry => entry.EntryDate,

        [(9, 1)] = entry => entry.AuxiliaryEquipment,
        [(10, 2)] = entry => entry.RandomDocuments,
        [(12, 2)] = entry => entry.RandomTools,
        [(14, 2)] = entry => entry.MachineCondition,
        [(17, 1)] = entry => entry.AcceptanceResult,
        [(20, 1)] = entry => entry.AcceptancePerson
    };

    /// <summary>弹出保存对话框，把单条入场记录导出为 Word 文档。</summary>
    /// <param name="entry">要导出的入场记录。</param>
    /// <returns>保存成功返回 true；用户取消返回 false。</returns>
    /// <exception cref="FileNotFoundException">模板文件不存在时抛出。</exception>
    /// <exception cref="IOException">目标文件被占用或无写入权限时抛出。</exception>
    public static Task<bool> ExportAsync(ExcavatorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return WordExporter.SaveFromTemplateAsync(
            TemplatePaths.ExcavatorEntry,
            BuildFileName(entry),
            document =>
            {
                var table = WordExporter.GetFirstTable(document);
                if (table is null)
                {
                    return;
                }

                // 第 2 行是「使用工程名称 / 使用单位 / 验收日期」的整行合并单元格，写成一串更贴近模板
                WordExporter.SetCellText(
                    table,
                    2,
                    0,
                    $"使用工程名称：{entry.ProjectName}　使用单位：{entry.UsingUnit}　验收日期：{entry.AcceptanceDate}");

                foreach (var pair in _cellMap)
                {
                    WordExporter.SetCellText(table, pair.Key.Row, pair.Key.Cell, pair.Value(entry));
                }
            });
    }

    /// <summary>按设备名称与进场日期生成文件名，并替换 Windows 文件名非法字符。</summary>
    /// <param name="entry">入场记录。</param>
    /// <returns>不含路径的建议文件名。</returns>
    private static string BuildFileName(ExcavatorEntry entry)
    {
        var raw = $"{entry.EquipmentName}_{entry.EntryDate}";

        // 设备名称由用户自由填写，可能含 / : 等非法字符，不替换会导致保存对话框报错
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            raw = raw.Replace(invalid, '_');
        }

        raw = raw.Trim(' ', '_');
        return string.IsNullOrWhiteSpace(raw) ? "挖机入场资料" : $"挖机入场资料_{raw}";
    }
}
