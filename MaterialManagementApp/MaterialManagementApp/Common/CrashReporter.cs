using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MaterialManagementApp.Common;

/// <summary>
/// 全局崩溃记录器：把未处理异常的完整堆栈写入 Crash.log，
/// 并弹出友好提示（优先 WinUI ContentDialog，失败回退 Win32 MessageBox），
/// 保证「程序不闪退、现场有据可查」。
///
/// Crash.log 固定写在用户本地数据目录（%LOCALAPPDATA%\MaterialSystem\Logs），
/// 该目录任何安装模式下都可写；写不进去时回退到临时目录。
/// </summary>
public static class CrashReporter
{
    /// <summary>连续崩溃计数：短时间内反复崩溃说明 UI 已不可用，放行终止避免死循环弹窗。</summary>
    private static int _consecutiveCrashes;

    /// <summary>上一个崩溃的时间点，用于重置连续计数。</summary>
    private static DateTime _lastCrashAt = DateTime.MinValue;

    /// <summary>连续崩溃上限：超过则交给默认行为终止进程。</summary>
    private const int MaxConsecutiveCrashes = 5;

    /// <summary>Crash.log 所在目录（首次使用时确定并缓存）。</summary>
    private static string? _logDirectory;

    /// <summary>Crash.log 完整路径（供弹窗文案与排障指引使用）。</summary>
    public static string LogFilePath => Path.Combine(EnsureLogDirectory(), "Crash.log");

    /// <summary>
    /// 处理一次未处理异常：写 Crash.log、弹友好提示。
    /// 返回 true 表示已安全消化、应用可继续运行；false 表示连续崩溃过多，应放行终止。
    /// </summary>
    /// <param name="ex">捕获到的异常。</param>
    /// <param name="source">异常来源描述（如「UI线程」「后台任务」）。</param>
    public static bool Report(Exception? ex, string source)
    {
        var now = DateTime.Now;
        // 5 分钟内的崩溃视为连续崩溃；间隔够长则重新计数
        _consecutiveCrashes = (now - _lastCrashAt).TotalMinutes < 5 ? _consecutiveCrashes + 1 : 1;
        _lastCrashAt = now;

        WriteCrashLog(ex, source, now);

        if (_consecutiveCrashes > MaxConsecutiveCrashes)
        {
            return false;
        }

        ShowFriendlyDialog(ex, source);
        return true;
    }

    /// <summary>把异常详情追加写入 Crash.log（UTF-8，含类型、消息、堆栈与内部异常链）。</summary>
    private static void WriteCrashLog(Exception? ex, string source, DateTime now)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("================ 未处理异常 ================");
            sb.AppendLine($"时间　　：{now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"来源　　：{source}");
            sb.AppendLine($"连续次数：{_consecutiveCrashes}");
            var level = 0;
            for (var e = ex; e is not null; e = e.InnerException, level++)
            {
                if (level > 0)
                {
                    sb.AppendLine($"---- 内部异常（第 {level} 层）----");
                }
                sb.AppendLine($"类型　　：{e.GetType().FullName}");
                sb.AppendLine($"消息　　：{e.Message}");
                sb.AppendLine($"堆栈　　：{e.StackTrace ?? "<无堆栈>"}");
            }
            if (ex is null)
            {
                sb.AppendLine("类型　　：<非 Exception 对象或 null>");
            }
            sb.AppendLine();

            var path = LogFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Crash.log 写入失败（磁盘满/权限）不能再抛异常，吞掉以保证主流程继续
        }
    }

    /// <summary>
    /// 弹出友好错误提示。优先用主窗口的 ContentDialog（与 UI 主题一致），
    /// 失败（XamlRoot 不可用/线程不符）时回退 Win32 MessageBox。
    /// </summary>
    private static void ShowFriendlyDialog(Exception? ex, string source)
    {
        var summary = ex?.Message ?? "未知错误";
        var detail = $"操作未能完成，已自动记录错误详情，程序可以继续使用。\n\n" +
                     $"错误类型：{ex?.GetType().Name ?? "Unknown"}\n" +
                     $"错误信息：{summary}\n\n" +
                     $"完整堆栈已写入：{LogFilePath}";
        var fired = false;

        try
        {
            var window = App.MainWindow;
            var root = window?.Content as Microsoft.UI.Xaml.FrameworkElement;
            if (root?.XamlRoot is { } xamlRoot)
            {
                // ContentDialog 必须在 UI 线程弹出；TryEnqueue 失败说明当前不在 UI 线程，走回退
                fired = window!.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                        {
                            XamlRoot = xamlRoot,
                            Title = "发生错误",
                            Content = detail,
                            CloseButtonText = "确定",
                            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close
                        };
                        await dialog.ShowAsync();
                    }
                    catch
                    {
                        ShowFallbackDialog(detail);
                    }
                });
            }
        }
        catch
        {
            fired = false;
        }

        if (!fired)
        {
            ShowFallbackDialog(detail);
        }
    }

    /// <summary>回退方案：Win32 MessageBox，不依赖任何 XAML 资源。</summary>
    private static void ShowFallbackDialog(string detail)
    {
        try
        {
            _ = Interop.NativeMethods.MessageBox(IntPtr.Zero, detail, "物资管理系统 - 发生错误",
                0x10 /* MB_ICONERROR */ | 0x0 /* MB_OK */);
        }
        catch
        {
            // 连系统对话框都失败时已无能为力，静默放弃
        }
    }

    /// <summary>确定并缓存 Crash.log 目录：优先用户本地数据目录，失败回退临时目录。</summary>
    private static string EnsureLogDirectory()
    {
        if (_logDirectory is not null)
        {
            return _logDirectory;
        }

        // 与 AppPaths.LocalAppDataRoot 同源：任何安装模式下用户目录都可写
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MaterialSystem", "Logs");
        try
        {
            Directory.CreateDirectory(candidate);
            _logDirectory = candidate;
        }
        catch
        {
            _logDirectory = Path.GetTempPath();
        }
        return _logDirectory;
    }
}

/// <summary>原生 Win32 封装（供崩溃兜底使用，避免依赖任何托管 UI）。</summary>
internal static class Interop
{
    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }
}
