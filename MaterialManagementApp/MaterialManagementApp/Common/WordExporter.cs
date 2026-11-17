using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MaterialManagementApp.Services;

namespace MaterialManagementApp.Common;

/// <summary>
/// 基于模板 docx 的 Word 导出引擎：把模板整份读入内存后原地填充，再写入归档导出目录，
/// 从而最大限度保留模板的合并单元格、字体与版式。
/// </summary>
public static class WordExporter
{
    /// <summary>以模板为基底填充并写入归档导出目录（Exports\Word\yyyyMMdd）。</summary>
    /// <param name="templatePath">模板 docx 的绝对路径，通常来自 <see cref="TemplatePaths"/>。</param>
    /// <param name="suggestedFileName">建议文件名（不含扩展名）。</param>
    /// <param name="fill">填充委托，参数为内存中已打开的 Word 文档。</param>
    /// <returns>保存成功返回 true。</returns>
    /// <exception cref="FileNotFoundException">模板文件不存在时抛出。</exception>
    /// <exception cref="IOException">归档目录不可写或文件被占用时抛出，由调用方提示。</exception>
    public static Task<bool> SaveFromTemplateAsync(
        string templatePath,
        string suggestedFileName,
        Action<WordprocessingDocument> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        TemplatePaths.EnsureExists(templatePath);

        var result = FileStorageService.Instance.StoreExport(
            FileStorageService.WordCategory,
            $"{suggestedFileName}.docx",
            target =>
            {
                // 以模板副本为基底填充，绝不直接修改用户提供的模板文件
                using var buffer = new MemoryStream(File.ReadAllBytes(templatePath));

                using (var document = WordprocessingDocument.Open(buffer, true))
                {
                    fill(document);

                    // 关闭前必须显式保存主文档部件，否则改动只停留在内存对象上
                    document.MainDocumentPart?.Document.Save();
                }

                buffer.Position = 0;
                buffer.CopyTo(target);
            });

        if (!result.Success)
        {
            throw new IOException(result.Error);
        }

        return Task.FromResult(true);
    }

    /// <summary>获取文档正文中的第一个表格。</summary>
    /// <param name="document">已打开的 Word 文档。</param>
    /// <returns>第一个表格；文档无表格时返回 null。</returns>
    public static Table? GetFirstTable(WordprocessingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.MainDocumentPart?.Document?.Body?.Elements<Table>().FirstOrDefault();
    }

    /// <summary>
    /// 把表格中指定行列的单元格文本替换为指定内容，保留原有段落与字体属性。
    /// </summary>
    /// <param name="table">目标表格。</param>
    /// <param name="row">行索引，从 0 开始；越界时静默返回。</param>
    /// <param name="cell">单元格索引，从 0 开始；越界时静默返回。</param>
    /// <param name="text">要写入的文本。</param>
    public static void SetCellText(Table table, int row, int cell, string text)
    {
        ArgumentNullException.ThrowIfNull(table);

        var rows = table.Elements<TableRow>().ToList();
        if (row < 0 || row >= rows.Count)
        {
            return;
        }

        var cells = rows[row].Elements<TableCell>().ToList();
        if (cell < 0 || cell >= cells.Count)
        {
            return;
        }

        var targetCell = cells[cell];

        var paragraph = targetCell.Elements<Paragraph>().FirstOrDefault();
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            targetCell.Append(paragraph);
        }

        var run = paragraph.Elements<Run>().FirstOrDefault();
        if (run is null)
        {
            run = new Run();
            paragraph.Append(run);
        }

        var textElement = run.Elements<Text>().FirstOrDefault();
        if (textElement is null)
        {
            textElement = new Text();
            run.Append(textElement);
        }

        // 只改 Text 的值、不动 RunProperties，否则模板里的字体、字号、加粗会丢失
        textElement.Text = text;
        textElement.Space = SpaceProcessingModeValues.Preserve;

        // 同一段落若存在多个 Run（模板常有拆分过的文字），删掉多余部分避免占位文字残留
        foreach (var extra in paragraph.Elements<Run>().Skip(1).ToList())
        {
            extra.Remove();
        }
    }
}
