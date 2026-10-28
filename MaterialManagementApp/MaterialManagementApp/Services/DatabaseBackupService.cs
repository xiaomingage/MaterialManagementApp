using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>
/// 数据库备份与恢复服务。
/// 备份统一存放于 %LOCALAPPDATA%\MaterialSystem\Backup\，文件命名 MaterialSystem_yyyyMMdd_HHmmss.db；
/// 采用 SQLite 在线备份 API（BackupDatabase）生成一致性快照，避免复制正在写入的库文件导致备份损坏。
/// 备份保留份数由设置项 BackupRetentionCount 控制（默认 15 份），超出后自动清理最旧的备份。
/// </summary>
public sealed class DatabaseBackupService
{
    private static readonly ILogger _logger = AppLog.CreateLogger<DatabaseBackupService>();

    /// <summary>备份文件匹配模式。</summary>
    private const string BackupFilePattern = "MaterialSystem_*.db";

    /// <summary>
    /// 创建一次数据库备份。
    /// </summary>
    /// <param name="dbPath">要备份的数据库文件完整路径。</param>
    /// <param name="reason">备份原因（写入日志，便于排查），如「版本升级前自动备份」「手动备份」。</param>
    /// <returns>备份文件完整路径；失败时返回 null（失败原因已写入日志，由调用方决定如何提示）。</returns>
    public static string? CreateBackup(string dbPath, string reason)
    {
        try
        {
            if (!File.Exists(dbPath))
            {
                _logger.LogWarning("数据库备份被跳过：库文件不存在。路径={Path}，原因={Reason}", dbPath, reason);
                return null;
            }

            Directory.CreateDirectory(AppPaths.BackupDir);

            var fileName = $"MaterialSystem_{DateTime.Now:yyyyMMdd_HHmmss}.db";
            var destination = Path.Combine(AppPaths.BackupDir, fileName);

            // 在线备份 API：源连接只读打开，逐页复制成一致性快照，不要求应用停机
            using (var source = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            using (var target = new SqliteConnection($"Data Source={destination}"))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }

            _logger.LogInformation("数据库备份完成（{Reason}）：{Source} -> {Destination}", reason, dbPath, destination);

            CleanupOldBackups();
            return destination;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库备份失败（{Reason}）：{Path}", reason, dbPath);
            return null;
        }
    }

    /// <summary>按时间倒序列出全部备份文件（最新在前）。</summary>
    public static IReadOnlyList<string> GetBackups()
    {
        if (!Directory.Exists(AppPaths.BackupDir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(AppPaths.BackupDir, BackupFilePattern)
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
    }

    /// <summary>按设置的保留份数清理最旧的备份，保留份数取设置值（最小 1）。</summary>
    public static void CleanupOldBackups()
    {
        try
        {
            var keepCount = Math.Max(1, SettingsService.Instance.Settings.BackupRetentionCount);
            var backups = GetBackups();

            for (var i = keepCount; i < backups.Count; i++)
            {
                try
                {
                    File.Delete(backups[i]);
                    _logger.LogInformation("已清理过期数据库备份：{Path}（保留最近 {Keep} 份）。", backups[i], keepCount);
                }
                catch (Exception ex)
                {
                    // 单个备份清理失败（被占用等）不影响其余清理，也不影响备份流程
                    _logger.LogWarning(ex, "清理过期数据库备份失败：{Path}", backups[i]);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理过期数据库备份时发生异常。");
        }
    }

    /// <summary>
    /// 从指定备份文件恢复数据库：先把当前库再做一次安全备份，然后用备份内容覆盖。
    /// </summary>
    /// <param name="backupPath">备份文件完整路径。</param>
    /// <param name="dbPath">要被覆盖的数据库文件完整路径。</param>
    /// <param name="error">失败时的错误说明；成功时为 null。</param>
    /// <returns>是否恢复成功。</returns>
    public static bool Restore(string backupPath, string dbPath, out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(backupPath))
            {
                error = "所选备份文件不存在，可能已被清理。";
                _logger.LogError("数据库恢复失败：备份文件不存在。{Path}", backupPath);
                return false;
            }

            // 恢复前给当前库做一次安全备份，保证恢复操作本身可回退
            var safetyBackup = CreateBackup(dbPath, "恢复前安全备份");

            // 清掉可能存在的 WAL / SHM 残留，避免旧预写日志污染恢复出来的库
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sideFile = dbPath + suffix;
                if (File.Exists(sideFile))
                {
                    File.Delete(sideFile);
                }
            }

            File.Copy(backupPath, dbPath, overwrite: true);

            _logger.LogInformation(
                "数据库恢复完成：{Backup} -> {Database}（恢复前安全备份：{SafetyBackup}）。",
                backupPath, dbPath, safetyBackup ?? "无（原库不存在）");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, "数据库恢复失败：{Backup} -> {Database}", backupPath, dbPath);
            return false;
        }
    }
}
