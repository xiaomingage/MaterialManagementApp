using System;
using System.Threading.Tasks;
using MaterialManagementApp.Common;

namespace MaterialManagementApp.Services.Export;

/// <summary>
/// 工作日志的 Word 导出适配器。以「工作日志模板.docx」为基底，
/// 把自动取得的日期与星期、用户选择的天气、填写的工作内容以及固定记录人
/// 填入模板表格对应单元格后，另存为「工作日志-yyyy-MM-dd.docx」。
/// </summary>
/// <remarks>
/// 模板表格为 3 行：第 0 行是「日期 / 星期 / 天气」三列，第 1 行是跨列合并的工作内容，
/// 第 2 行是跨列合并的记录人。
/// </remarks>
public static class WorkLogWordExport
{
    /// <summary>模板表格中「日期 / 星期 / 天气」所在的行索引。</summary>
    private const int HeaderRow = 0;

    /// <summary>模板表格中工作内容所在的行索引。</summary>
    private const int ContentRow = 1;

    /// <summary>模板表格中记录人所在的行索引。</summary>
    private const int RecorderRow = 2;

    /// <summary>记录人姓名。按业务要求固定，不提供界面配置项。</summary>
    public const string RecorderName = "何南平";

    /// <summary>弹出保存对话框，把今日工作日志按模板导出为 Word 文档。</summary>
    /// <param name="dateText">日期文本，格式如 2026年10月19日。</param>
    /// <param name="weekdayText">星期汉字，取值 日/一/二/三/四/五/六。</param>
    /// <param name="weather">天气，来自界面下拉框。</param>
    /// <param name="content">工作内容，来自界面多行文本框。</param>
    /// <returns>保存成功返回 true；用户取消返回 false。</returns>
    /// <exception cref="System.IO.FileNotFoundException">模板文件缺失时抛出。</exception>
    /// <exception cref="System.IO.IOException">目标文件被占用或无写入权限时抛出。</exception>
    public static Task<bool> ExportAsync(string dateText, string weekdayText, string weather, string content)
    {
        return WordExporter.SaveFromTemplateAsync(
            TemplatePaths.WorkLog,
            // 保存对话框会自动补 .docx 扩展名，这里只给不含扩展名的主文件名
            $"工作日志-{DateTime.Now:yyyy-MM-dd}",
            document =>
            {
                var table = WordExporter.GetFirstTable(document);
                if (table is null)
                {
                    return;
                }

                WordExporter.SetCellText(table, HeaderRow, 0, $"日期：{dateText}");
                WordExporter.SetCellText(table, HeaderRow, 1, $"星期：{weekdayText}");
                WordExporter.SetCellText(table, HeaderRow, 2, $"天气：{weather}");
                WordExporter.SetCellText(table, ContentRow, 0, content);
                WordExporter.SetCellText(table, RecorderRow, 0, $"记录人：{RecorderName}");
            });
    }
}
