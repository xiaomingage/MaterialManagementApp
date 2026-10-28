using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using MEL = Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>
/// 全局日志工厂（静态单例）。项目未引入依赖注入容器，
/// 各 ViewModel / Service 通过 <see cref="CreateLogger{T}"/> 获取按类型分类的 <see cref="MEL.ILogger"/>。
/// 底层使用 Serilog，按级别分流到两个按天滚动的日志文件：
/// Information/Warning → Logs\operation-yyyyMMdd.log（业务操作日志）；
/// Error/Fatal → Logs\system-yyyyMMdd.log（系统与崩溃日志）。
/// 同时输出到 IDE 输出窗口（Debug Sink），方便开发期排查。
/// </summary>
public static class AppLog
{
    /// <summary>日志文件所在目录（安装版为数据目录下的 Logs，开发态为应用安装目录下的 Logs）。</summary>
    public static string LogsDirectory { get; }

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    private static readonly ILoggerFactory Factory;

    static AppLog()
    {
        LogsDirectory = ResolveLogsDirectory();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Debug(outputTemplate: OutputTemplate)
            // 业务操作日志：仅收录 Information/Warning，按天滚动 + 单文件 5MB 分卷，保留 31 个文件
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e => e.Level < LogEventLevel.Error)
                .WriteTo.File(
                    Path.Combine(LogsDirectory, "operation-.log"),
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    retainedFileCountLimit: 31,
                    outputTemplate: OutputTemplate))
            // 系统/BUG 日志：仅收录 Error/Fatal，按天滚动 + 单文件 5MB 分卷，保留 62 个文件
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e => e.Level >= LogEventLevel.Error)
                .WriteTo.File(
                    Path.Combine(LogsDirectory, "system-.log"),
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    retainedFileCountLimit: 62,
                    outputTemplate: OutputTemplate))
            .CreateLogger();

        Factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(MEL.LogLevel.Information);
            // dispose:false：Serilog 全局 Logger 由 App 退出时统一 CloseAndFlush
            builder.AddSerilog(Log.Logger, dispose: false);
        });
    }

    /// <summary>
    /// 日志目录。安装版为向导中选择的数据存储目录下的 Logs（与数据库同级，便于随数据一起备份），
    /// 开发态为安装目录下的 Logs；两者都写不进去时退化到 LocalApplicationData，保证日志始终可用。
    /// </summary>
    private static string ResolveLogsDirectory()
    {
        // 安装版：数据目录由用户指定，天然可写，优先使用
        if (AppPaths.DataRoot is { } dataRoot)
        {
            var dataLogs = Path.Combine(dataRoot, "Logs");
            if (TryPrepareWritable(dataLogs, out var prepared))
            {
                return prepared;
            }
        }

        var installDirLogs = Path.Combine(AppContext.BaseDirectory, "Logs");
        if (TryPrepareWritable(installDirLogs, out var installPrepared))
        {
            return installPrepared;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MaterialManagementApp", "Logs");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>
    /// 确保目录存在且确实可写。仅创建目录不代表可写（如 Program Files 下标准用户），
    /// 因此这里用一次真实写入探测权限。
    /// </summary>
    /// <param name="directory">待检查的目录。</param>
    /// <param name="writableDirectory">可写时返回该目录。</param>
    /// <returns>目录可写返回 true。</returns>
    private static bool TryPrepareWritable(string directory, out string writableDirectory)
    {
        writableDirectory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write_probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建以 <typeparamref name="T"/> 类型名作为分类名的日志实例。</summary>
    /// <typeparam name="T">请求日志的分类类型，通常传入当前类。</typeparam>
    /// <returns>线程安全的 <see cref="MEL.ILogger"/> 实例。</returns>
    public static MEL.ILogger CreateLogger<T>() => Factory.CreateLogger<T>();

    /// <summary>创建以指定类型全名作为分类名的日志实例（供静态类等无法作为类型参数的场景使用）。</summary>
    /// <param name="type">日志分类类型。</param>
    /// <returns>线程安全的 <see cref="MEL.ILogger"/> 实例。</returns>
    public static MEL.ILogger CreateLogger(Type type) => Factory.CreateLogger(type.FullName ?? type.Name);
}
