using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaterialManagementApp.Services;

namespace MaterialManagementApp.Common;

/// <summary>
/// CSV 导出辅助类：负责把带 BOM 的 UTF-8 文件写入归档导出目录。
/// 带 BOM 是必须的，否则 Excel 打开中文会乱码。
/// </summary>
public static class CsvExporter
{
    /// <summary>把 CSV 写入归档导出目录（Exports\Csv\yyyyMMdd）。</summary>
    /// <param name="suggestedFileName">建议文件名（含 .csv 扩展名，不含路径）。</param>
    /// <param name="content">完整的 CSV 文本，需已包含表头。</param>
    /// <returns>写入成功返回 true。</returns>
    /// <exception cref="IOException">归档目录不可写或文件被占用时抛出，由调用方提示。</exception>
    public static Task<bool> SaveAsync(string suggestedFileName, string content)
    {
        // 无 BOM 的 UTF-8 会被 Excel 按 ANSI 解析导致中文乱码，因此手动拼上 UTF-8 BOM 再写字节
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(content);
        var buffer = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, buffer, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, buffer, preamble.Length, body.Length);

        var result = FileStorageService.Instance.StoreExport(
            FileStorageService.CsvCategory,
            suggestedFileName,
            target => target.Write(buffer, 0, buffer.Length));

        if (!result.Success)
        {
            throw new IOException(result.Error);
        }

        return Task.FromResult(true);
    }

    /// <summary>按 RFC 4180 转义单元格：含逗号、引号或换行时用双引号包裹并转义内部引号。</summary>
    /// <param name="value">原始单元格值，可为 null。</param>
    /// <returns>可安全写入 CSV 的文本。</returns>
    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }

    /// <summary>把表头与数据行拼装为 CSV 文本。</summary>
    /// <param name="headers">表头列名。</param>
    /// <param name="rows">数据行集合，每行的列数应与表头一致。</param>
    /// <returns>以换行分隔的完整 CSV 文本。</returns>
    public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(Escape)));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(Escape)));
        }

        return builder.ToString();
    }
}
