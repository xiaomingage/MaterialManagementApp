using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>
/// 启动自检：数据库可打开、归档根可写、离线 OCR 引擎可用。
/// 结果只收集「人话 + 下一步」提示，由首页顶部提示条展示；
/// 任何一项失败都不阻断启动，其余功能继续可用。
/// </summary>
public static class StartupSelfCheckService
{
    private static readonly ILogger _logger = AppLog.CreateLogger(typeof(StartupSelfCheckService));

    /// <summary>自检失败提示（已按用户可读文案组织）；全部通过时为空集合。</summary>
    public static IReadOnlyList<string> Messages { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// 执行三项自检。可重复调用，每次覆盖 <see cref="Messages"/>。
    /// 本方法不抛出异常。
    /// </summary>
    public static void Run()
    {
        var messages = new List<string>();

        CheckDatabase(messages);
        CheckArchiveRoot(messages);
        CheckOcrEngine(messages);

        Messages = messages;

        _logger.LogInformation(
            "启动自检完成：数据库/归档根/OCR 引擎，失败项 {Count} 个。", messages.Count);
    }

    /// <summary>用一条 SELECT 1 验证数据库连接可用。</summary>
    private static void CheckDatabase(List<string> messages)
    {
        try
        {
            DatabaseService.Instance.ExecuteScalarCount("SELECT 1;");
            _logger.LogInformation("启动自检：数据库可打开。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动自检：数据库无法打开。");
            messages.Add("数据库无法打开：请确认没有其他程序正在使用数据文件，然后重启软件。");
        }
    }

    /// <summary>在归档根写一个临时探针文件再删除，验证真实可写性。</summary>
    private static void CheckArchiveRoot(List<string> messages)
    {
        try
        {
            var root = FileStorageService.Instance.CurrentArchiveRoot;
            Directory.CreateDirectory(root);

            var probe = Path.Combine(root, ".write_probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            _logger.LogInformation("启动自检：归档根可写。目录={Root}", root);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动自检：归档根不可写。");
            messages.Add("归档目录不可写：请检查设置中的归档根目录是否存在且具有写权限。");
        }
    }

    /// <summary>校验随程序分发的离线 OCR 程序集是否可用。</summary>
    private static void CheckOcrEngine(List<string> messages)
    {
        if (OcrEngineRouter.IsEngineAvailable())
        {
            _logger.LogInformation("启动自检：离线 OCR 引擎可用。");
            return;
        }

        _logger.LogWarning("启动自检：离线 OCR 引擎程序集缺失。");
        messages.Add("OCR 引擎未找到：请确认安装目录完整，或重新安装软件。");
    }
}
