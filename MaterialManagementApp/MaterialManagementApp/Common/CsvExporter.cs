using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MaterialManagementApp.Common;

/// <summary>
/// CSV 导出辅助类：负责弹出保存对话框、初始化 WinRT 窗口句柄并写出带 BOM 的 UTF-8 文件。
/// 带 BOM 是必须的，否则 Excel 打开中文会乱码。
/// </summary>
public static class CsvExporter
{
    /// <summary>弹出保存对话框并写出 CSV 文件。</summary>
    /// <param name="suggestedFileName">建议文件名（含 .csv 扩展名，不含路径）。</param>
    /// <param name="content">完整的 CSV 文本，需已包含表头。</param>
    /// <returns>写入成功返回 true；用户取消或无法获取窗口句柄时返回 false。</returns>
    /// <exception cref="IOException">目标文件被占用或无写入权限时抛出，由调用方提示。</exception>
    public static async Task<bool> SaveAsync(string suggestedFileName, string content)
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return false;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });

        // WinUI 3 的保存对话框是 Win32 对话框，必须先绑定窗口句柄，否则构造后直接抛异常
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        // FileIO.WriteTextAsync 写的是无 BOM 的 UTF-8，Excel 会按 ANSI 解析导致中文乱码，
        // 因此手动拼上 UTF-8 BOM 再写字节
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(content);
        var buffer = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, buffer, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, buffer, preamble.Length, body.Length);

        await FileIO.WriteBytesAsync(file, buffer);
        return true;
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
