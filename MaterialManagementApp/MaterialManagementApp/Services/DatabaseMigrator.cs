using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>迁移流程执行结果。</summary>
/// <param name="Success">是否成功（或无需迁移）。</param>
/// <param name="Message">给用户看的结果说明；失败时用于弹窗正文。</param>
/// <param name="BackupPath">迁移前自动备份文件的完整路径；未产生备份时为 null。</param>
/// <param name="FromVersion">迁移前数据库版本（全新库为 0）。</param>
/// <param name="ToVersion">迁移后数据库版本。</param>
public sealed record DatabaseMigrationResult(
    bool Success,
    string Message,
    string? BackupPath,
    int FromVersion,
    int ToVersion);

/// <summary>
/// 数据库版本迁移器。启动时在 <see cref="DatabaseService.Initialize"/> 之前执行：
/// 校验 DbVersion 表中的版本号，与程序目标版本不一致时先强制自动备份，
/// 再按顺序执行增量迁移脚本（整个迁移包在一个事务里，任何一条失败整体回滚）。
/// 迁移硬性规则：只允许新增表、新增字段、新增索引；禁止 DROP / DELETE / TRUNCATE /
/// 修改已有字段数据；脚本必须幂等，重复执行不报错。
/// </summary>
public sealed class DatabaseMigrator
{
    private static readonly ILogger _logger = AppLog.CreateLogger<DatabaseMigrator>();

    /// <summary>程序当前期望的数据库版本号。每次新增迁移脚本时递增 1。</summary>
    public const int TargetVersion = 1;

    /// <summary>
    /// 增量迁移脚本：键为「起始版本」，值为该版本升级到下一版本需要顺序执行的 SQL 语句。
    /// 例：TargetVersion=2 时补充 [1] = new[] { "CREATE TABLE IF NOT EXISTS ..." }。
    /// </summary>
    private static readonly Dictionary<int, string[]> Migrations = new()
    {
        // 示例（勿直接启用）：
        // [1] = new[] { "ALTER TABLE MaterialLedger ADD COLUMN Remark2 TEXT NOT NULL DEFAULT '';" },
    };

    /// <summary>
    /// 启动闸门：完成旧库搬迁、版本校验、迁移前自动备份与增量迁移。
    /// 任何失败都会返回 <see cref="DatabaseMigrationResult.Success"/> 为 false，
    /// 由 App 启动流程弹窗拦截，禁止进入主界面。
    /// </summary>
    public static DatabaseMigrationResult PrepareAndMigrate()
    {
        var dbPath = SettingsService.Instance.EffectiveDatabasePath;

        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 统一数据库目录后的一次性旧库搬迁（目标已存在时绝不覆盖）
        DatabaseService.MigrateLegacyDatabaseIfNeeded(dbPath);

        var isNewDatabase = !File.Exists(dbPath);

        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared
                }.ToString());
            connection.Open();

            var fromVersion = 0;
            if (!isNewDatabase)
            {
                EnsureDbVersionTable(connection);
                fromVersion = ReadVersion(connection);
            }

            _logger.LogInformation(
                "数据库版本校验：当前版本 {From}，程序目标版本 {Target}，库文件 {Path}（{State}）",
                fromVersion, TargetVersion, dbPath, isNewDatabase ? "全新空库" : "已有数据库");

            if (fromVersion > TargetVersion)
            {
                // 数据库比程序新（例如用户回退安装了旧版程序），拒绝打开以免旧程序写坏新结构
                var downgradeMessage =
                    $"当前数据库版本（{fromVersion}）高于程序支持版本（{TargetVersion}），请更新程序后再使用。";
                _logger.LogCritical(
                    "数据库版本高于程序目标版本，禁止启动。当前={From}，目标={Target}，库文件={Path}",
                    fromVersion, TargetVersion, dbPath);
                return new DatabaseMigrationResult(false, downgradeMessage, null, fromVersion, fromVersion);
            }

            if (fromVersion == TargetVersion)
            {
                _logger.LogInformation("数据库版本已是目标版本，无需迁移。");
                return new DatabaseMigrationResult(true, "数据库版本一致，无需迁移。", null, fromVersion, fromVersion);
            }

            // 版本升级：迁移前强制自动备份（全新空库没有可保护的数据，跳过备份）
            string? backupPath = null;
            if (!isNewDatabase)
            {
                backupPath = DatabaseBackupService.CreateBackup(dbPath, "版本升级前自动备份");
                if (backupPath is null)
                {
                    var backupFailMessage =
                        "数据库迁移失败，请使用备份文件恢复，禁止进入软件主界面。\n\n" +
                        "原因：迁移前自动备份失败，为保护数据已停止升级。";
                    _logger.LogCritical("迁移前自动备份失败，已停止数据库版本升级。库文件={Path}", dbPath);
                    return new DatabaseMigrationResult(false, backupFailMessage, null, fromVersion, fromVersion);
                }
            }

            // 整个迁移包放进一个事务：任何一条脚本失败立即整体回滚，库保持迁移前的原样
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    var executedCount = 0;
                    for (var version = fromVersion; version < TargetVersion; version++)
                    {
                        if (!Migrations.TryGetValue(version, out var scripts))
                        {
                            continue;
                        }

                        _logger.LogInformation("开始执行数据库迁移脚本：{From} -> {To}，共 {Count} 条。",
                            version, version + 1, scripts.Length);

                        foreach (var script in scripts)
                        {
                            using var command = connection.CreateCommand();
                            command.CommandText = script;
                            command.Transaction = transaction;
                            command.ExecuteNonQuery();
                            executedCount++;
                        }

                        _logger.LogInformation("数据库迁移脚本执行成功：{From} -> {To}。", version, version + 1);
                    }

                    StampVersion(connection, TargetVersion, transaction);
                    transaction.Commit();

                    _logger.LogInformation(
                        "数据库迁移完成：{From} -> {To}，执行脚本 {Count} 条，备份文件 {Backup}。",
                        fromVersion, TargetVersion, executedCount, backupPath ?? "无（全新库）");
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }
            }

            return new DatabaseMigrationResult(
                true, "数据库迁移完成。", backupPath, fromVersion, TargetVersion);
        }
        catch (Exception ex)
        {
            var failMessage =
                "数据库迁移失败，请使用备份文件恢复，禁止进入软件主界面。\n\n" +
                $"备份目录：{AppPaths.BackupDir}\n" +
                $"详细原因：{ex.Message}";
            _logger.LogCritical(ex,
                "数据库迁移失败，已终止启动。库版本 {From} -> {Target}，库文件 {Path}。",
                isNewDatabase ? 0 : -1, TargetVersion, dbPath);
            return new DatabaseMigrationResult(false, failMessage, null, isNewDatabase ? 0 : -1, TargetVersion);
        }
    }

    /// <summary>确保旧库中存在 DbVersion 表（只新增、幂等，不触碰已有数据）。</summary>
    private static void EnsureDbVersionTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS DbVersion (
                Id INTEGER PRIMARY KEY,
                VersionNo INTEGER NOT NULL DEFAULT 0,
                UpdateTime TEXT NOT NULL DEFAULT '');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>读取当前数据库版本号；表为空（老库首次打版本标记）时返回 0。</summary>
    private static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT VersionNo FROM DbVersion ORDER BY Id DESC LIMIT 1;";
        var result = command.ExecuteScalar();
        return result is int version ? version : (result is long value ? (int)value : 0);
    }

    /// <summary>把数据库版本号写入 DbVersion 表（单行，Id 固定为 1）。</summary>
    private static void StampVersion(SqliteConnection connection, int version, SqliteTransaction transaction)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE DbVersion SET VersionNo = @version, UpdateTime = @time WHERE Id = 1;";
        update.Parameters.AddWithValue("@version", version);
        update.Parameters.AddWithValue("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var affected = update.ExecuteNonQuery();

        if (affected == 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO DbVersion (Id, VersionNo, UpdateTime) VALUES (1, @version, @time);";
            insert.Parameters.AddWithValue("@version", version);
            insert.Parameters.AddWithValue("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            insert.ExecuteNonQuery();
        }
    }
}
