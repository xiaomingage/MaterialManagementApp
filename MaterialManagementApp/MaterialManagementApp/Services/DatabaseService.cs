using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MaterialManagementApp.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>
/// SQLite 数据库访问服务（单例）。负责数据库文件定位、建库建表与通用查询/执行。
/// 所有页面与 ViewModel 均通过 <see cref="Instance"/> 获取唯一实例，禁止直接 new。
/// </summary>
public sealed class DatabaseService
{
    /// <summary>数据库服务日志，记录合同材料明细等数据层的关键变更，便于后续排障。</summary>
    private static readonly ILogger Logger = AppLog.CreateLogger<DatabaseService>();

    /// <summary>全部业务表、维度表与索引的建表脚本。全部使用 IF NOT EXISTS，保证 Initialize 可重复执行。</summary>
    private const string SchemaScript = @"
CREATE TABLE IF NOT EXISTS WorkLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    LogDate TEXT NOT NULL,
    Content TEXT NOT NULL,
    CreatedAt TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS IX_WorkLog_LogDate ON WorkLog(LogDate);

CREATE TABLE IF NOT EXISTS EarthMaterial (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Type TEXT NOT NULL,
    Specification TEXT NOT NULL DEFAULT '',
    Unit TEXT NOT NULL DEFAULT '',
    Quantity REAL NOT NULL DEFAULT 0,
    UnitPrice REAL NOT NULL DEFAULT 0,
    TransportFee REAL NOT NULL DEFAULT 0,
    SupplierName TEXT NOT NULL DEFAULT '',
    RecordDate TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '');

CREATE TABLE IF NOT EXISTS Supplier (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    ContractNo TEXT NOT NULL DEFAULT '',
    ContactPerson TEXT NOT NULL DEFAULT '',
    Phone TEXT NOT NULL DEFAULT '',
    MonthlySettlement REAL NOT NULL DEFAULT 0,
    SignDate TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '',
    ContractFilePath TEXT NOT NULL DEFAULT '');

CREATE TABLE IF NOT EXISTS PartyAMaterial (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    MaterialName TEXT NOT NULL,
    Specification TEXT NOT NULL DEFAULT '',
    Unit TEXT NOT NULL DEFAULT '',
    ApplyQty REAL NOT NULL DEFAULT 0,
    InboundQty REAL NOT NULL DEFAULT 0,
    OutboundQty REAL NOT NULL DEFAULT 0,
    ReturnQty REAL NOT NULL DEFAULT 0,
    RecordDate TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '',
    PhotoPath TEXT NOT NULL DEFAULT '');

CREATE TABLE IF NOT EXISTS SelfPurchasedMaterial (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    MaterialName TEXT NOT NULL,
    Specification TEXT NOT NULL DEFAULT '',
    Unit TEXT NOT NULL DEFAULT '',
    ApplyQty REAL NOT NULL DEFAULT 0,
    PurchaseQty REAL NOT NULL DEFAULT 0,
    UnitPrice REAL NOT NULL DEFAULT 0,
    Amount REAL NOT NULL DEFAULT 0,
    SupplierName TEXT NOT NULL DEFAULT '',
    RecordDate TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '');

CREATE TABLE IF NOT EXISTS Equipment (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Model TEXT NOT NULL DEFAULT '',
    EntryDate TEXT NOT NULL DEFAULT '',
    Owner TEXT NOT NULL DEFAULT '',
    Status TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '');

CREATE TABLE IF NOT EXISTS RentalDevice (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DeviceName TEXT NOT NULL,
    Lessor TEXT NOT NULL DEFAULT '',
    RentStart TEXT NOT NULL DEFAULT '',
    RentEnd TEXT NOT NULL DEFAULT '',
    MonthlyRent REAL NOT NULL DEFAULT 0,
    SettlementAmount REAL NOT NULL DEFAULT 0,
    Remark TEXT NOT NULL DEFAULT '');

CREATE TABLE IF NOT EXISTS MaterialWriteOff (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    MaterialName TEXT NOT NULL,
    WriteOffMonth TEXT NOT NULL DEFAULT '',
    WriteOffQty REAL NOT NULL DEFAULT 0,
    UnitPrice REAL NOT NULL DEFAULT 0,
    Amount REAL NOT NULL DEFAULT 0,
    Reason TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS IX_MaterialWriteOff_Month ON MaterialWriteOff(WriteOffMonth);

CREATE TABLE IF NOT EXISTS ExcavatorEntry (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProjectName TEXT NOT NULL DEFAULT '',
    UsingUnit TEXT NOT NULL DEFAULT '',
    AcceptanceDate TEXT NOT NULL DEFAULT '',
    EquipmentName TEXT NOT NULL DEFAULT '',
    Model TEXT NOT NULL DEFAULT '',
    FactoryCode TEXT NOT NULL DEFAULT '',
    FactoryDate TEXT NOT NULL DEFAULT '',
    Manufacturer TEXT NOT NULL DEFAULT '',
    StorageLocation TEXT NOT NULL DEFAULT '',
    Operator TEXT NOT NULL DEFAULT '',
    EntryDate TEXT NOT NULL DEFAULT '',
    AuxiliaryEquipment TEXT NOT NULL DEFAULT '',
    AcceptanceStatus TEXT NOT NULL DEFAULT '',
    RandomDocuments TEXT NOT NULL DEFAULT '',
    RandomTools TEXT NOT NULL DEFAULT '',
    MachineCondition TEXT NOT NULL DEFAULT '',
    AcceptanceResult TEXT NOT NULL DEFAULT '',
    AcceptancePerson TEXT NOT NULL DEFAULT '');

CREATE TABLE IF NOT EXISTS MaterialLedger (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    EntryDate TEXT NOT NULL DEFAULT '',
    EntryTime TEXT NOT NULL DEFAULT '',
    DocNo TEXT NOT NULL DEFAULT '',
    MaterialName TEXT NOT NULL DEFAULT '',
    Quantity REAL NOT NULL DEFAULT 0,
    Unit TEXT NOT NULL DEFAULT '',
    UnitPrice REAL NOT NULL DEFAULT 0,
    Amount REAL NOT NULL DEFAULT 0,
    Supplier TEXT NOT NULL DEFAULT '',
    ProjectAddress TEXT NOT NULL DEFAULT '',
    Handler TEXT NOT NULL DEFAULT '',
    PlateNumber TEXT NOT NULL DEFAULT '',
    Specification TEXT NOT NULL DEFAULT '',
    SiteSignature TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '',
    LedgerImagePath TEXT NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS IX_MaterialLedger_EntryDate ON MaterialLedger(EntryDate);

CREATE TABLE IF NOT EXISTS SmallEquipment (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Code TEXT NOT NULL DEFAULT '',
    ImagePath TEXT NOT NULL DEFAULT '',
    ProjectShortName TEXT NOT NULL DEFAULT '',
    WorkSite TEXT NOT NULL DEFAULT '',
    Category TEXT NOT NULL DEFAULT '',
    Name TEXT NOT NULL DEFAULT '',
    Specification TEXT NOT NULL DEFAULT '',
    Unit TEXT NOT NULL DEFAULT '',
    Quantity REAL NOT NULL DEFAULT 0,
    ConfigMethod TEXT NOT NULL DEFAULT '',
    AssetOwner TEXT NOT NULL DEFAULT '',
    Manufacturer TEXT NOT NULL DEFAULT '',
    FactoryDate TEXT NOT NULL DEFAULT '',
    PerformanceStatus TEXT NOT NULL DEFAULT '');
-- 注意：IX_SmallEquipment_Code 不能在建表脚本里创建。
-- CREATE TABLE IF NOT EXISTS 不会给已存在的表补列，旧库执行到这里时 Code 列尚不存在，
-- 同批 CREATE INDEX 会抛 ""no such column: Code"" 导致整批建表失败。
-- 该索引改由 EnsureSmallEquipmentColumns 在 ALTER TABLE ADD COLUMN 之后创建。

CREATE TABLE IF NOT EXISTS SlagTransport (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    NoLast4 TEXT NOT NULL DEFAULT '',
    PlateNumber TEXT NOT NULL DEFAULT '',
    UsageAddress TEXT NOT NULL DEFAULT '',
    MaterialName TEXT NOT NULL DEFAULT '',
    Specification TEXT NOT NULL DEFAULT '',
    Quantity REAL NOT NULL DEFAULT 0,
    Unit TEXT NOT NULL DEFAULT '',
    TransportFee REAL NOT NULL DEFAULT 0,
    EntryDate TEXT NOT NULL DEFAULT '',
    EntryTime TEXT NOT NULL DEFAULT '',
    SiteSignature TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS IX_SlagTransport_EntryDate ON SlagTransport(EntryDate);

CREATE TABLE IF NOT EXISTS StockRecord (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    MaterialSource TEXT NOT NULL DEFAULT '甲供',
    MaterialName TEXT NOT NULL,
    Specification TEXT NOT NULL DEFAULT '',
    Unit TEXT NOT NULL DEFAULT '',
    ChangeType TEXT NOT NULL DEFAULT '入库',
    Quantity REAL NOT NULL DEFAULT 0,
    OccurDate TEXT NOT NULL DEFAULT '',
    Handler TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS IX_StockRecord_OccurDate ON StockRecord(OccurDate);
CREATE INDEX IF NOT EXISTS IX_StockRecord_Source ON StockRecord(MaterialSource, MaterialName);

CREATE TABLE IF NOT EXISTS EquipmentMaintenance (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    EquipmentName TEXT NOT NULL,
    MaintainType TEXT NOT NULL DEFAULT '维保',
    MaintainDate TEXT NOT NULL DEFAULT '',
    NextDueDate TEXT NOT NULL DEFAULT '',
    Cost REAL NOT NULL DEFAULT 0,
    Vendor TEXT NOT NULL DEFAULT '',
    Handler TEXT NOT NULL DEFAULT '',
    Remark TEXT NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS IX_EquipmentMaintenance_MaintainDate ON EquipmentMaintenance(MaintainDate);

CREATE TABLE IF NOT EXISTS ProjectDept (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    SortOrder INTEGER NOT NULL DEFAULT 0);

CREATE TABLE IF NOT EXISTS ProjectPhase (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    SortOrder INTEGER NOT NULL DEFAULT 0);

CREATE TABLE IF NOT EXISTS DeptPhaseApplicableTable (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TableName TEXT NOT NULL UNIQUE,
    DisplayName TEXT NOT NULL DEFAULT '',
    IsEnabled INTEGER NOT NULL DEFAULT 0);

CREATE TABLE IF NOT EXISTS SupplierContractItem (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SupplierId INTEGER NOT NULL DEFAULT 0,
    MaterialName TEXT NOT NULL,
    Specification TEXT NOT NULL DEFAULT '',
    Unit TEXT NOT NULL DEFAULT '',
    UnitPrice REAL NOT NULL DEFAULT 0,
    Remark TEXT NOT NULL DEFAULT '',
    FOREIGN KEY (SupplierId) REFERENCES Supplier(Id));
CREATE INDEX IF NOT EXISTS IX_SupplierContractItem_Supplier ON SupplierContractItem(SupplierId);
CREATE INDEX IF NOT EXISTS IX_SupplierContractItem_Material ON SupplierContractItem(MaterialName);

CREATE TABLE IF NOT EXISTS DbVersion (
    Id INTEGER PRIMARY KEY,
    VersionNo INTEGER NOT NULL DEFAULT 0,
    UpdateTime TEXT NOT NULL DEFAULT '');
";

    private static readonly Lazy<DatabaseService> _instance = new(() => new DatabaseService());

    /// <summary>
    /// 反射属性映射缓存，键为实体类型，值为「列名 -> 属性」字典。
    /// 查询是高频操作，缓存可避免每行数据都重复反射；类型数量有限，无需清理。
    /// </summary>
    private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> _propertyCache = new();

    private readonly object _initLock = new();

    private bool _initialized;

    // 初始化进行中标记：Initialize 内部的播种/迁移方法若经 ExecuteScalarCount 等
    // 帮助方法走到 CreateConnection，会因 _initialized 尚为 false 而再次调用 Initialize；
    // 没有此护栏会形成无限递归（曾在安装版造成启动即挂死、窗口无法出现）。
    private bool _initializing;

    private string _databasePath = string.Empty;

    private string _connectionString = string.Empty;

    private DatabaseService()
    {
    }

    /// <summary>全局唯一实例。</summary>
    public static DatabaseService Instance => _instance.Value;

    /// <summary>数据库文件的绝对路径，供界面诊断显示。</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// 台账扫描图片的存储目录：数据库文件同目录下的 LedgerImages 子目录。
    /// 图片与数据库放在一起，换机或迁移时把数据目录整体拷走图片不丢。
    /// </summary>
    public string LedgerImageDir
    {
        get
        {
            var dbDir = Path.GetDirectoryName(_databasePath);
            if (string.IsNullOrWhiteSpace(dbDir))
            {
                dbDir = AppContext.BaseDirectory;
            }

            return Path.Combine(dbDir, "LedgerImages");
        }
    }

    /// <summary>
    /// 把地材单扫描图片复制到 <see cref="LedgerImageDir"/> 持久保存，返回复制后的完整路径。
    /// 文件名加时间戳前缀避免重名覆盖；来源文件已在存储目录内时不重复复制。
    /// </summary>
    /// <param name="sourcePath">扫描图片当前的完整路径（临时目录、用户相册均可）。</param>
    /// <returns>存储目录内的图片完整路径；目录不可写等 IO 异常由调用方捕获提示。</returns>
    public string SaveLedgerImage(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("扫描图片不存在，无法保存。", sourcePath);
        }

        var dir = LedgerImageDir;
        Directory.CreateDirectory(dir);

        var full = Path.GetFullPath(sourcePath);
        if (string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase))
        {
            // 已在存储目录内（如同一张图重复保存），直接沿用不再复制
            return full;
        }

        var destination = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Path.GetFileName(sourcePath)}");
        File.Copy(full, destination, overwrite: false);
        return destination;
    }

    /// <summary>
    /// 把当前数据库文件复制到指定路径，用于设置页的手工备份。
    /// 之所以可以安全直接复制：全部数据库访问均为短连接（using 释放），复制时无长期文件占用。
    /// </summary>
    /// <param name="targetPath">备份目标文件的完整路径，目录不存在时会自动创建。</param>
    /// <exception cref="InvalidOperationException">数据库尚未初始化、文件不存在时抛出。</exception>
    /// <exception cref="IOException">目标目录不可写或磁盘空间不足时抛出。</exception>
    public void BackupTo(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("备份目标路径不能为空。", nameof(targetPath));
        }

        // 未初始化时 DatabasePath 还是空串，直接复制会得到无意义的文件，因此显式拦截
        if (!_initialized)
        {
            Initialize();
        }

        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            throw new InvalidOperationException("数据库文件尚不存在，无法备份。");
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(_databasePath, targetPath, overwrite: true);
    }

    /// <summary>
    /// 幂等初始化：确保数据目录存在、打开数据库并创建 8 张业务表与索引。
    /// 重复调用直接返回，不会重复建表。
    /// </summary>
    /// <exception cref="SqliteException">建表脚本执行失败（如文件被占用、磁盘不可写）时抛出。</exception>
    /// <exception cref="UnauthorizedAccessException">数据目录无写入权限时抛出。</exception>
    public void Initialize()
    {
        lock (_initLock)
        {
            // 已初始化或正在初始化都直接返回：后者用于拦截 Initialize 内部
            // 播种/迁移方法经 CreateConnection 间接发起的重入（否则无限递归）
            if (_initialized || _initializing)
            {
                return;
            }

            _initializing = true;
            try
            {
                _databasePath = ResolveDatabasePath();

                // Cache=Shared 让多个短连接共享同一份页缓存，避免频繁开关连接时的读写冲突
                _connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared
                }.ToString();

                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = SchemaScript;
                    command.ExecuteNonQuery();
                }

                // CREATE TABLE IF NOT EXISTS 不会给已存在的表补列，老库必须显式迁移，否则写入新列会报 no such column
                EnsureMaterialLedgerColumns();

                // 甲供物资的验收照片列是后加的，同样需要对老库做列迁移
                EnsurePartyAMaterialColumns();

                // 小型机具的编号与照片列、以及已废弃的队伍列，同样需要对老库做迁移
                EnsureSmallEquipmentColumns();

                // 供应商合同文件路径列是后加的，老库需要补列迁移
                EnsureSupplierContractFileColumn();

                // 「项目部 / 期数」维度：写入默认项目部、默认期数与适用栏目配置（幂等），
                // 随后给已勾选的栏目表补 ProjectDept / ProjectPhase 两列
                SeedDeptPhaseDimension();
                ApplyDeptPhaseColumnMigrations();

                _initialized = true;
            }
            finally
            {
                // 失败时保持 _initialized=false，下次调用整体重试（建表/迁移均为幂等操作）
                _initializing = false;
            }
        }
    }

    /// <summary>
    /// 创建一个已打开的新连接。调用方需使用 using 及时释放，避免长期占用文件锁。
    /// </summary>
    /// <returns>已打开、可直接执行命令的 <see cref="SqliteConnection"/>。</returns>
    public SqliteConnection CreateConnection()
    {
        // 防御性初始化：页面可能在没有经过 App 启动流程的场景（如单元测试）直接取用服务
        if (!_initialized)
        {
            Initialize();
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>执行非查询命令（INSERT / UPDATE / DELETE / DDL）。</summary>
    /// <param name="sql">SQL 语句，支持以 @ 开头的命名参数占位符。</param>
    /// <param name="parameters">参数名与值的键值对集合，可为 null。</param>
    /// <returns>受影响的行数。</returns>
    /// <exception cref="SqliteException">SQL 语法错误或约束冲突时抛出。</exception>
    public int ExecuteNonQuery(string sql, IDictionary<string, object?>? parameters = null)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>执行返回单个数值的查询（如 COUNT），供首页统计卡片使用。</summary>
    /// <param name="sql">查询 SQL，应返回单行单列数值。</param>
    /// <param name="parameters">参数名与值的键值对集合，可为 null。</param>
    /// <returns>查询结果；无结果或无法转换时返回 0。</returns>
    public long ExecuteScalarCount(string sql, IDictionary<string, object?>? parameters = null)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);

        var result = command.ExecuteScalar();
        return result is null || result is DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>
    /// 轻量迁移：确保 MaterialLedger 表包含 OCR 导入新增的车牌、规格、签字三列。
    /// 这里直接在已初始化的连接串上开连接，不能走 CreateConnection，
    /// 否则会因 _initialized 仍为 false 而递归触发 Initialize。
    /// </summary>
    /// <exception cref="SqliteException">ALTER TABLE 执行失败时抛出。</exception>
    private void EnsureMaterialLedgerColumns()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var infoCommand = connection.CreateCommand())
        {
            infoCommand.CommandText = "PRAGMA table_info(MaterialLedger);";
            using var reader = infoCommand.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info 的列顺序固定，索引 1 为列名
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[] { "PlateNumber", "Specification", "SiteSignature", "Remark", "LedgerImagePath" })
        {
            if (existing.Contains(column))
            {
                continue;
            }

            // SQLite 的 ADD COLUMN 只接受常量默认值，这里统一用空串，与建表脚本保持一致
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE MaterialLedger ADD COLUMN {column} TEXT NOT NULL DEFAULT '';";
            alterCommand.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 轻量迁移：确保 PartyAMaterial 表包含验收照片路径列。
    /// 与 EnsureMaterialLedgerColumns 同理，直接使用已初始化的连接串，
    /// 避免走 CreateConnection 时因 _initialized 仍为 false 而递归调用 Initialize。
    /// </summary>
    /// <exception cref="SqliteException">ALTER TABLE 执行失败时抛出。</exception>
    private void EnsurePartyAMaterialColumns()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var infoCommand = connection.CreateCommand())
        {
            infoCommand.CommandText = "PRAGMA table_info(PartyAMaterial);";
            using var reader = infoCommand.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info 的列顺序固定，索引 1 为列名
                existing.Add(reader.GetString(1));
            }
        }

        if (existing.Contains("PhotoPath"))
        {
            return;
        }

        // SQLite 的 ADD COLUMN 只接受常量默认值，这里用空串表示未附照片，与建表脚本一致
        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE PartyAMaterial ADD COLUMN PhotoPath TEXT NOT NULL DEFAULT '';";
        alterCommand.ExecuteNonQuery();
    }

    /// <summary>
    /// 轻量迁移：为 SmallEquipment 表补齐编号与照片列，并尝试删除已废弃的队伍列。
    /// 与 EnsurePartyAMaterialColumns 同理使用已初始化的连接串，避免递归调用 Initialize。
    /// </summary>
    private void EnsureSmallEquipmentColumns()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var infoCommand = connection.CreateCommand())
        {
            infoCommand.CommandText = "PRAGMA table_info(SmallEquipment);";
            using var reader = infoCommand.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info 的列顺序固定，索引 1 为列名
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[] { "Code", "ImagePath" })
        {
            if (existing.Contains(column))
            {
                continue;
            }

            // SQLite 的 ADD COLUMN 只接受常量默认值，这里统一用空串，与建表脚本保持一致
            using var addCommand = connection.CreateCommand();
            addCommand.CommandText = $"ALTER TABLE SmallEquipment ADD COLUMN {column} TEXT NOT NULL DEFAULT '';";
            addCommand.ExecuteNonQuery();
        }

        if (!existing.Contains("Team"))
        {
            return;
        }

        // 队伍功能已废弃：SQLite 3.35+ 才支持 DROP COLUMN，旧版本会抛异常。
        // 即便删除失败也不能影响启动，因为实体已不再映射该列，代码层不再读写它。
        try
        {
            using var dropCommand = connection.CreateCommand();
            dropCommand.CommandText = "ALTER TABLE SmallEquipment DROP COLUMN Team;";
            dropCommand.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] 删除 SmallEquipment.Team 列失败（SQLite 版本过低），该列保留但不再使用：{ex.Message}");
        }
    }

    /// <summary>
    /// 轻量迁移：为 Supplier 表补齐合同文件路径列（ContractFilePath）。
    /// 与其他 Ensure* 方法同理，使用已初始化的连接串，避免递归调用 Initialize。
    /// </summary>
    private void EnsureSupplierContractFileColumn()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var infoCommand = connection.CreateCommand())
        {
            infoCommand.CommandText = "PRAGMA table_info(Supplier);";
            using var reader = infoCommand.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (existing.Contains("ContractFilePath"))
        {
            return;
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE Supplier ADD COLUMN ContractFilePath TEXT NOT NULL DEFAULT '';";
        alterCommand.ExecuteNonQuery();
        Logger.LogInformation("已为 Supplier 表补齐 ContractFilePath 列。");
    }

    /// <summary>
    /// 执行查询并按「属性名 = 列名」（不区分大小写）反射映射为实体列表。
    /// </summary>
    /// <typeparam name="T">实体类型，需具备公共无参构造函数。</typeparam>
    /// <param name="sql">查询 SQL。</param>
    /// <param name="parameters">参数名与值的键值对集合，可为 null。</param>
    /// <returns>实体列表；无结果时返回空集合，不返回 null。</returns>
    /// <exception cref="SqliteException">SQL 执行失败时抛出。</exception>
    public List<T> Query<T>(string sql, IDictionary<string, object?>? parameters = null)
        where T : new()
    {
        var propertyMap = GetPropertyMap(typeof(T));
        var results = new List<T>();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = new T();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i);
                if (!propertyMap.TryGetValue(columnName, out var property))
                {
                    // 查询里含实体没有的列时跳过，而不是报错，便于复用 JOIN / 聚合查询
                    continue;
                }

                if (reader.IsDBNull(i))
                {
                    // 数据库 NULL 不能直接赋给值类型，保持实体默认值（数值为 0、字符串为 string.Empty）
                    continue;
                }

                var value = ConvertToTargetType(reader.GetValue(i), property.PropertyType);
                if (value is not null)
                {
                    property.SetValue(item, value);
                }
            }

            results.Add(item);
        }

        return results;
    }

    /// <summary>读取全部工作日志，按日期倒序、Id 倒序排列。</summary>
    /// <returns>工作日志列表；无数据时返回空集合。</returns>
    public List<WorkLog> GetWorkLogs()
    {
        return Query<WorkLog>("SELECT Id, LogDate, Content, CreatedAt FROM WorkLog ORDER BY LogDate DESC, Id DESC;");
    }

    /// <summary>新增一条工作日志。</summary>
    /// <param name="log">待新增的日志实体，其 Id 会被忽略；CreatedAt 为空时自动填充当前时间。</param>
    /// <returns>新记录的自增主键。</returns>
    /// <exception cref="SqliteException">插入失败时抛出。</exception>
    public int AddWorkLog(WorkLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        // 创建时间只用于同日期内排序，调用方不需要关心，因此在此兜底填充
        if (string.IsNullOrWhiteSpace(log.CreatedAt))
        {
            log.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO WorkLog (LogDate, Content, CreatedAt) VALUES (@logDate, @content, @createdAt); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@logDate", log.LogDate);
        command.Parameters.AddWithValue("@content", log.Content);
        command.Parameters.AddWithValue("@createdAt", log.CreatedAt);

        var newId = command.ExecuteScalar();
        log.Id = Convert.ToInt32(newId);
        return log.Id;
    }

    /// <summary>按 Id 更新工作日志的日期与内容。</summary>
    /// <param name="log">携带 Id 与新值的日志实体，CreatedAt 不会被修改。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateWorkLog(WorkLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE WorkLog SET LogDate = @logDate, Content = @content WHERE Id = @id;";
        command.Parameters.AddWithValue("@logDate", log.LogDate);
        command.Parameters.AddWithValue("@content", log.Content);
        command.Parameters.AddWithValue("@id", log.Id);
        return command.ExecuteNonQuery();
    }

    /// <summary>按 Id 删除工作日志。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteWorkLog(int id)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WorkLog WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery();
    }

    /// <summary>读取全部挖机进场资料，按 Id 倒序排列。</summary>
    /// <returns>进场资料列表；无数据时返回空集合。</returns>
    public List<ExcavatorEntry> GetExcavatorEntries()
    {
        return Query<ExcavatorEntry>("SELECT * FROM ExcavatorEntry ORDER BY Id DESC;");
    }

    /// <summary>新增一条挖机进场资料。</summary>
    /// <param name="entry">待新增的资料实体，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddExcavatorEntry(ExcavatorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        entry.Id = InsertRow("ExcavatorEntry", new (string Column, object? Value)[]
        {
            ("ProjectName", entry.ProjectName),
            ("UsingUnit", entry.UsingUnit),
            ("AcceptanceDate", entry.AcceptanceDate),
            ("EquipmentName", entry.EquipmentName),
            ("Model", entry.Model),
            ("FactoryCode", entry.FactoryCode),
            ("FactoryDate", entry.FactoryDate),
            ("Manufacturer", entry.Manufacturer),
            ("StorageLocation", entry.StorageLocation),
            ("Operator", entry.Operator),
            ("EntryDate", entry.EntryDate),
            ("AuxiliaryEquipment", entry.AuxiliaryEquipment),
            ("AcceptanceStatus", entry.AcceptanceStatus),
            ("RandomDocuments", entry.RandomDocuments),
            ("RandomTools", entry.RandomTools),
            ("MachineCondition", entry.MachineCondition),
            ("AcceptanceResult", entry.AcceptanceResult),
            ("AcceptancePerson", entry.AcceptancePerson)
        });

        return entry.Id;
    }

    /// <summary>按 Id 更新挖机进场资料的全部业务字段。</summary>
    /// <param name="entry">携带 Id 与新值的资料实体。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateExcavatorEntry(ExcavatorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return UpdateRow("ExcavatorEntry", entry.Id, new (string Column, object? Value)[]
        {
            ("ProjectName", entry.ProjectName),
            ("UsingUnit", entry.UsingUnit),
            ("AcceptanceDate", entry.AcceptanceDate),
            ("EquipmentName", entry.EquipmentName),
            ("Model", entry.Model),
            ("FactoryCode", entry.FactoryCode),
            ("FactoryDate", entry.FactoryDate),
            ("Manufacturer", entry.Manufacturer),
            ("StorageLocation", entry.StorageLocation),
            ("Operator", entry.Operator),
            ("EntryDate", entry.EntryDate),
            ("AuxiliaryEquipment", entry.AuxiliaryEquipment),
            ("AcceptanceStatus", entry.AcceptanceStatus),
            ("RandomDocuments", entry.RandomDocuments),
            ("RandomTools", entry.RandomTools),
            ("MachineCondition", entry.MachineCondition),
            ("AcceptanceResult", entry.AcceptanceResult),
            ("AcceptancePerson", entry.AcceptancePerson)
        });
    }

    /// <summary>按 Id 删除挖机进场资料。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteExcavatorEntry(int id)
    {
        return DeleteRow("ExcavatorEntry", id);
    }

    /// <summary>读取全部物资台账记录，按进场日期倒序、Id 倒序排列。</summary>
    /// <returns>台账列表；无数据时返回空集合。</returns>
    public List<MaterialLedger> GetMaterialLedgers()
    {
        return Query<MaterialLedger>("SELECT * FROM MaterialLedger ORDER BY EntryDate DESC, Id DESC;");
    }

    /// <summary>新增一条物资台账记录。</summary>
    /// <param name="ledger">待新增的台账实体，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddMaterialLedger(MaterialLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        ledger.Id = InsertRow("MaterialLedger", new (string Column, object? Value)[]
        {
            ("EntryDate", ledger.EntryDate),
            ("EntryTime", ledger.EntryTime),
            ("DocNo", ledger.DocNo),
            ("MaterialName", ledger.MaterialName),
            ("Quantity", ledger.Quantity),
            ("Unit", ledger.Unit),
            ("UnitPrice", ledger.UnitPrice),
            ("Amount", ledger.Amount),
            ("Supplier", ledger.Supplier),
            ("ProjectAddress", ledger.ProjectAddress),
            ("Handler", ledger.Handler),
            ("PlateNumber", ledger.PlateNumber),
            ("Specification", ledger.Specification),
            ("SiteSignature", ledger.SiteSignature),
            ("Remark", ledger.Remark),
            ("LedgerImagePath", ledger.LedgerImagePath),
            ("ProjectDept", ledger.ProjectDept),
            ("ProjectPhase", ledger.ProjectPhase)
        });

        return ledger.Id;
    }

    /// <summary>按 Id 更新物资台账记录。</summary>
    /// <param name="ledger">携带 Id 与新值的台账实体。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateMaterialLedger(MaterialLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        return UpdateRow("MaterialLedger", ledger.Id, new (string Column, object? Value)[]
        {
            ("EntryDate", ledger.EntryDate),
            ("EntryTime", ledger.EntryTime),
            ("DocNo", ledger.DocNo),
            ("MaterialName", ledger.MaterialName),
            ("Quantity", ledger.Quantity),
            ("Unit", ledger.Unit),
            ("UnitPrice", ledger.UnitPrice),
            ("Amount", ledger.Amount),
            ("Supplier", ledger.Supplier),
            ("ProjectAddress", ledger.ProjectAddress),
            ("Handler", ledger.Handler),
            ("PlateNumber", ledger.PlateNumber),
            ("Specification", ledger.Specification),
            ("SiteSignature", ledger.SiteSignature),
            ("Remark", ledger.Remark),
            ("LedgerImagePath", ledger.LedgerImagePath),
            ("ProjectDept", ledger.ProjectDept),
            ("ProjectPhase", ledger.ProjectPhase)
        });
    }

    /// <summary>按 Id 删除物资台账记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteMaterialLedger(int id)
    {
        return DeleteRow("MaterialLedger", id);
    }

    /// <summary>读取全部小型机具台账记录，按 Id 倒序排列。</summary>
    /// <returns>小型机具列表；无数据时返回空集合。</returns>
    public List<SmallEquipment> GetSmallEquipments()
    {
        return Query<SmallEquipment>("SELECT * FROM SmallEquipment ORDER BY Id DESC;");
    }

    /// <summary>新增一条小型机具台账记录。</summary>
    /// <param name="equipment">待新增的实体，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddSmallEquipment(SmallEquipment equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        equipment.Id = InsertRow("SmallEquipment", new (string Column, object? Value)[]
        {
            ("Code", equipment.Code),
            ("ImagePath", equipment.ImagePath),
            ("ProjectShortName", equipment.ProjectShortName),
            ("WorkSite", equipment.WorkSite),
            ("Category", equipment.Category),
            ("Name", equipment.Name),
            ("Specification", equipment.Specification),
            ("Unit", equipment.Unit),
            ("Quantity", equipment.Quantity),
            ("ConfigMethod", equipment.ConfigMethod),
            ("AssetOwner", equipment.AssetOwner),
            ("Manufacturer", equipment.Manufacturer),
            ("FactoryDate", equipment.FactoryDate),
            ("PerformanceStatus", equipment.PerformanceStatus)
        });

        return equipment.Id;
    }

    /// <summary>按 Id 更新小型机具台账记录。</summary>
    /// <param name="equipment">携带 Id 与新值的实体。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateSmallEquipment(SmallEquipment equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        return UpdateRow("SmallEquipment", equipment.Id, new (string Column, object? Value)[]
        {
            ("Code", equipment.Code),
            ("ImagePath", equipment.ImagePath),
            ("ProjectShortName", equipment.ProjectShortName),
            ("WorkSite", equipment.WorkSite),
            ("Category", equipment.Category),
            ("Name", equipment.Name),
            ("Specification", equipment.Specification),
            ("Unit", equipment.Unit),
            ("Quantity", equipment.Quantity),
            ("ConfigMethod", equipment.ConfigMethod),
            ("AssetOwner", equipment.AssetOwner),
            ("Manufacturer", equipment.Manufacturer),
            ("FactoryDate", equipment.FactoryDate),
            ("PerformanceStatus", equipment.PerformanceStatus)
        });
    }

    /// <summary>按 Id 删除小型机具台账记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteSmallEquipment(int id)
    {
        return DeleteRow("SmallEquipment", id);
    }

    /// <summary>读取全部渣土运输记录，按票据日期倒序、Id 倒序排列。</summary>
    /// <returns>渣土运输列表；无数据时返回空集合。</returns>
    public List<SlagTransport> GetSlagTransports()
    {
        return Query<SlagTransport>("SELECT * FROM SlagTransport ORDER BY EntryDate DESC, Id DESC;");
    }

    /// <summary>新增一条渣土运输记录。</summary>
    /// <param name="item">待新增的记录，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddSlagTransport(SlagTransport item)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Id = InsertRow("SlagTransport", new (string Column, object? Value)[]
        {
            ("NoLast4", item.NoLast4),
            ("PlateNumber", item.PlateNumber),
            ("UsageAddress", item.UsageAddress),
            ("MaterialName", item.MaterialName),
            ("Specification", item.Specification),
            ("Quantity", item.Quantity),
            ("Unit", item.Unit),
            ("TransportFee", item.TransportFee),
            ("EntryDate", item.EntryDate),
            ("EntryTime", item.EntryTime),
            ("SiteSignature", item.SiteSignature),
            ("Remark", item.Remark),
            ("ProjectDept", item.ProjectDept),
            ("ProjectPhase", item.ProjectPhase)
        });

        return item.Id;
    }

    /// <summary>按 Id 更新渣土运输记录。</summary>
    /// <param name="item">携带 Id 与新值的记录。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateSlagTransport(SlagTransport item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return UpdateRow("SlagTransport", item.Id, new (string Column, object? Value)[]
        {
            ("NoLast4", item.NoLast4),
            ("PlateNumber", item.PlateNumber),
            ("UsageAddress", item.UsageAddress),
            ("MaterialName", item.MaterialName),
            ("Specification", item.Specification),
            ("Quantity", item.Quantity),
            ("Unit", item.Unit),
            ("TransportFee", item.TransportFee),
            ("EntryDate", item.EntryDate),
            ("EntryTime", item.EntryTime),
            ("SiteSignature", item.SiteSignature),
            ("Remark", item.Remark),
            ("ProjectDept", item.ProjectDept),
            ("ProjectPhase", item.ProjectPhase)
        });
    }

    /// <summary>按 Id 删除渣土运输记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteSlagTransport(int id)
    {
        return DeleteRow("SlagTransport", id);
    }

    /// <summary>读取全部供应商，按名称升序排列。</summary>
    /// <returns>供应商列表；无数据时返回空集合。</returns>
    public List<Supplier> GetSuppliers()
    {
        return Query<Supplier>("SELECT * FROM Supplier ORDER BY Name;");
    }

    /// <summary>新增一条供应商记录。</summary>
    /// <param name="supplier">待新增的供应商，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddSupplier(Supplier supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        supplier.Id = InsertRow("Supplier", new (string Column, object? Value)[]
        {
            ("Name", supplier.Name),
            ("ContractNo", supplier.ContractNo),
            ("ContactPerson", supplier.ContactPerson),
            ("Phone", supplier.Phone),
            ("MonthlySettlement", supplier.MonthlySettlement),
            ("SignDate", supplier.SignDate),
            ("Remark", supplier.Remark),
            ("ContractFilePath", supplier.ContractFilePath)
        });

        return supplier.Id;
    }

    /// <summary>按 Id 更新供应商记录。</summary>
    /// <param name="supplier">携带 Id 与新值的供应商。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateSupplier(Supplier supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        return UpdateRow("Supplier", supplier.Id, new (string Column, object? Value)[]
        {
            ("Name", supplier.Name),
            ("ContractNo", supplier.ContractNo),
            ("ContactPerson", supplier.ContactPerson),
            ("Phone", supplier.Phone),
            ("MonthlySettlement", supplier.MonthlySettlement),
            ("SignDate", supplier.SignDate),
            ("Remark", supplier.Remark),
            ("ContractFilePath", supplier.ContractFilePath)
        });
    }

    /// <summary>按 Id 删除供应商记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteSupplier(int id)
    {
        // 删除供应商时级联清理其合同材料明细
        var removedItems = ExecuteNonQuery("DELETE FROM SupplierContractItem WHERE SupplierId = @id", new Dictionary<string, object?> { ["@id"] = id });
        Logger.LogInformation("删除供应商 Id={Id} 时级联清理合同材料明细 {Count} 条。", id, removedItems);
        return DeleteRow("Supplier", id);
    }

    // ---------------------------------------------------------------------
    // 合同材料价格明细（SupplierContractItem） CRUD
    // ---------------------------------------------------------------------

    /// <summary>读取全部合同材料明细，按供应商、材料名称排序。</summary>
    /// <returns>明细列表；含 SupplierName 以便 UI 展示。</returns>
    public List<SupplierContractItem> GetAllContractItems()
    {
        const string sql = @"SELECT ci.*, COALESCE(s.Name, '') AS SupplierName
                              FROM SupplierContractItem ci
                              LEFT JOIN Supplier s ON s.Id = ci.SupplierId
                              ORDER BY s.Name, ci.MaterialName;";
        return QueryContractItems(sql, null);
    }

    /// <summary>读取指定供应商的合同材料明细。</summary>
    /// <param name="supplierId">供应商主键。</param>
    /// <returns>该供应商的合同材料列表。</returns>
    public List<SupplierContractItem> GetContractItemsBySupplier(int supplierId)
    {
        const string sql = @"SELECT ci.*, COALESCE(s.Name, '') AS SupplierName
                              FROM SupplierContractItem ci
                              LEFT JOIN Supplier s ON s.Id = ci.SupplierId
                              WHERE ci.SupplierId = @id
                              ORDER BY ci.MaterialName;";
        return QueryContractItems(sql, new Dictionary<string, object?> { ["@id"] = supplierId });
    }

    /// <summary>新增一条合同材料明细。</summary>
    /// <param name="item">待新增的明细，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddContractItem(SupplierContractItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Id = InsertRow("SupplierContractItem", new (string Column, object? Value)[]
        {
            ("SupplierId", item.SupplierId),
            ("MaterialName", item.MaterialName),
            ("Specification", item.Specification),
            ("Unit", item.Unit),
            ("UnitPrice", item.UnitPrice),
            ("Remark", item.Remark)
        });

        return item.Id;
    }

    /// <summary>按 Id 更新合同材料明细。</summary>
    /// <param name="item">携带 Id 与新值的明细。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateContractItem(SupplierContractItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return UpdateRow("SupplierContractItem", item.Id, new (string Column, object? Value)[]
        {
            ("SupplierId", item.SupplierId),
            ("MaterialName", item.MaterialName),
            ("Specification", item.Specification),
            ("Unit", item.Unit),
            ("UnitPrice", item.UnitPrice),
            ("Remark", item.Remark)
        });
    }

    /// <summary>按 Id 删除合同材料明细。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteContractItem(int id)
    {
        return DeleteRow("SupplierContractItem", id);
    }

    /// <summary>
    /// 合同单价匹配：按「供应商名 + 物资名称（支持模糊）」查找最合适的合同材料明细。
    /// 匹配优先级：
    ///   1. 供应商完全匹配 + 材料名完全匹配
    ///   2. 供应商完全匹配 + 材料名包含（如 "水泥" 匹配 "P.O42.5水泥"）
    ///   3. 供应商名包含 + 材料名包含
    /// </summary>
    /// <param name="supplierName">供应商名称；空串时跳过第 1、2 级优先级。</param>
    /// <param name="materialName">物资名称；不能为空白。</param>
    /// <returns>最佳匹配的合同材料明细；未匹配到返回 null。</returns>
    public SupplierContractItem? MatchContractPrice(string? supplierName, string? materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
        {
            return null;
        }

        var normalizedMaterial = NormalizeMatchKey(materialName);
        var normalizedSupplier = string.IsNullOrWhiteSpace(supplierName) ? null : NormalizeMatchKey(supplierName);

        // 只扫一遍合同材料表，在内存里算优先级命中，避免多次 SQL
        const string sql = @"SELECT ci.*, COALESCE(s.Name, '') AS SupplierName
                              FROM SupplierContractItem ci
                              LEFT JOIN Supplier s ON s.Id = ci.SupplierId;";
        var all = QueryContractItems(sql, null);
        if (all.Count == 0)
        {
            Logger.LogInformation("合同单价匹配跳过：合同材料明细表为空，供应商={Supplier}，材料={Material}。",
                supplierName, materialName);
            return null;
        }

        SupplierContractItem? best = null;
        int bestScore = -1;

        foreach (var item in all)
        {
            var score = MatchScore(item, normalizedSupplier, normalizedMaterial);
            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        // 得分 <= 0 表示没有材料名上的实质命中
        if (bestScore > 0 && best is not null)
        {
            Logger.LogInformation(
                "合同单价匹配命中，得分={Score}，供应商={Supplier}，材料={Material} -> 合同材料={ContractMaterial}，单价={Price}元/{Unit}。",
                bestScore, supplierName, materialName, best.MaterialName, best.UnitPrice, best.Unit);
            return best;
        }

        Logger.LogInformation("合同单价匹配未命中，供应商={Supplier}，材料={Material}，待匹配记录数={Count}。",
            supplierName, materialName, all.Count);
        return null;
    }

    /// <summary>
    /// 计算合同材料明细与给定 supplier/material 的匹配得分。
    /// 得分规则（从高到低）：
    ///   1000 = 供应商完全匹配 + 材料名完全匹配
    ///    900 = 供应商完全匹配 + 材料名包含
    ///    800 = 供应商包含 + 材料名完全匹配
    ///    700 = 供应商包含 + 材料名包含
    ///    500 = 材料名完全匹配（供应商缺失）
    ///    400 = 材料名包含（供应商缺失）
    ///      0 = 材料名也不匹配（仅规格或备注命中不算）
    /// </summary>
    private static int MatchScore(SupplierContractItem item, string? normalizedSupplier, string normalizedMaterial)
    {
        var m = NormalizeMatchKey(item.MaterialName);
        bool materialExact = m.Equals(normalizedMaterial, StringComparison.Ordinal);
        bool materialContains = !materialExact && m.Contains(normalizedMaterial, StringComparison.Ordinal);

        if (!materialExact && !materialContains)
        {
            return 0;
        }

        bool supplierExact = normalizedSupplier is not null
            && NormalizeMatchKey(item.SupplierName).Equals(normalizedSupplier, StringComparison.Ordinal);
        bool supplierContains = !supplierExact
            && normalizedSupplier is not null
            && NormalizeMatchKey(item.SupplierName).Contains(normalizedSupplier, StringComparison.Ordinal);

        return (supplierExact, supplierContains, materialExact, materialContains) switch
        {
            (true, _, true, _) => 1000,
            (true, _, _, true) => 900,
            (_, true, true, _) => 800,
            (_, true, _, true) => 700,
            (false, false, true, _) => 500,
            (false, false, _, true) => 400,
            _ => 0
        };
    }

    /// <summary>
    /// 字符串匹配键归一化：去空格、去标点、小写，减少 OCR 常见的格式偏差影响。
    /// </summary>
    private static string NormalizeMatchKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>执行合同材料明细的 Join 查询，逐行反射填充属性并附加 SupplierName。</summary>
    /// <param name="sql">含 ci.* 与 SupplierName 的 SQL。</param>
    /// <param name="parameters">可选参数。</param>
    /// <returns>明细列表；无结果时返回空集合。</returns>
    private List<SupplierContractItem> QueryContractItems(string sql, IDictionary<string, object?>? parameters)
    {
        var results = new List<SupplierContractItem>();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var kv in parameters)
            {
                command.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            }
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = new SupplierContractItem
            {
                Id = reader.GetInt32(0),
                SupplierId = reader.GetInt32(1),
                MaterialName = reader.GetString(2),
                Specification = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Unit = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                UnitPrice = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                Remark = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                SupplierName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
            };
            results.Add(item);
        }

        return results;
    }

    /// <summary>读取全部甲供物资记录，按记录日期倒序、Id 倒序排列。</summary>
    /// <returns>甲供物资列表；无数据时返回空集合。</returns>
    public List<PartyAMaterial> GetPartyAMaterials()
    {
        return Query<PartyAMaterial>("SELECT * FROM PartyAMaterial ORDER BY RecordDate DESC, Id DESC;");
    }

    /// <summary>新增一条甲供物资记录。</summary>
    /// <param name="material">待新增的物资，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddPartyAMaterial(PartyAMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        material.Id = InsertRow("PartyAMaterial", new (string Column, object? Value)[]
        {
            ("MaterialName", material.MaterialName),
            ("Specification", material.Specification),
            ("Unit", material.Unit),
            ("ApplyQty", material.ApplyQty),
            ("InboundQty", material.InboundQty),
            ("OutboundQty", material.OutboundQty),
            ("ReturnQty", material.ReturnQty),
            ("RecordDate", material.RecordDate),
            ("Remark", material.Remark),
            ("PhotoPath", material.PhotoPath)
        });

        return material.Id;
    }

    /// <summary>按 Id 更新甲供物资记录。</summary>
    /// <param name="material">携带 Id 与新值的物资。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdatePartyAMaterial(PartyAMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        return UpdateRow("PartyAMaterial", material.Id, new (string Column, object? Value)[]
        {
            ("MaterialName", material.MaterialName),
            ("Specification", material.Specification),
            ("Unit", material.Unit),
            ("ApplyQty", material.ApplyQty),
            ("InboundQty", material.InboundQty),
            ("OutboundQty", material.OutboundQty),
            ("ReturnQty", material.ReturnQty),
            ("RecordDate", material.RecordDate),
            ("Remark", material.Remark),
            ("PhotoPath", material.PhotoPath)
        });
    }

    /// <summary>按 Id 删除甲供物资记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeletePartyAMaterial(int id)
    {
        return DeleteRow("PartyAMaterial", id);
    }

    /// <summary>读取全部自购物资记录，按记录日期倒序、Id 倒序排列。</summary>
    /// <returns>自购物资列表；无数据时返回空集合。</returns>
    public List<SelfPurchasedMaterial> GetSelfPurchasedMaterials()
    {
        return Query<SelfPurchasedMaterial>("SELECT * FROM SelfPurchasedMaterial ORDER BY RecordDate DESC, Id DESC;");
    }

    /// <summary>新增一条自购物资记录。</summary>
    /// <param name="material">待新增的物资，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddSelfPurchasedMaterial(SelfPurchasedMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        material.Id = InsertRow("SelfPurchasedMaterial", new (string Column, object? Value)[]
        {
            ("MaterialName", material.MaterialName),
            ("Specification", material.Specification),
            ("Unit", material.Unit),
            ("ApplyQty", material.ApplyQty),
            ("PurchaseQty", material.PurchaseQty),
            ("UnitPrice", material.UnitPrice),
            ("Amount", material.Amount),
            ("SupplierName", material.SupplierName),
            ("RecordDate", material.RecordDate),
            ("Remark", material.Remark),
            ("ProjectDept", material.ProjectDept),
            ("ProjectPhase", material.ProjectPhase)
        });

        return material.Id;
    }

    /// <summary>按 Id 更新自购物资记录。</summary>
    /// <param name="material">携带 Id 与新值的物资。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateSelfPurchasedMaterial(SelfPurchasedMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        return UpdateRow("SelfPurchasedMaterial", material.Id, new (string Column, object? Value)[]
        {
            ("MaterialName", material.MaterialName),
            ("Specification", material.Specification),
            ("Unit", material.Unit),
            ("ApplyQty", material.ApplyQty),
            ("PurchaseQty", material.PurchaseQty),
            ("UnitPrice", material.UnitPrice),
            ("Amount", material.Amount),
            ("SupplierName", material.SupplierName),
            ("RecordDate", material.RecordDate),
            ("Remark", material.Remark),
            ("ProjectDept", material.ProjectDept),
            ("ProjectPhase", material.ProjectPhase)
        });
    }

    /// <summary>按 Id 删除自购物资记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteSelfPurchasedMaterial(int id)
    {
        return DeleteRow("SelfPurchasedMaterial", id);
    }

    /// <summary>读取全部机具设备记录，按名称升序排列。</summary>
    /// <returns>设备列表；无数据时返回空集合。</returns>
    public List<Equipment> GetEquipments()
    {
        return Query<Equipment>("SELECT * FROM Equipment ORDER BY Name;");
    }

    /// <summary>新增一条机具设备记录。</summary>
    /// <param name="equipment">待新增的设备，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddEquipment(Equipment equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        equipment.Id = InsertRow("Equipment", new (string Column, object? Value)[]
        {
            ("Name", equipment.Name),
            ("Model", equipment.Model),
            ("EntryDate", equipment.EntryDate),
            ("Owner", equipment.Owner),
            ("Status", equipment.Status),
            ("Remark", equipment.Remark)
        });

        return equipment.Id;
    }

    /// <summary>按 Id 更新机具设备记录。</summary>
    /// <param name="equipment">携带 Id 与新值的设备。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateEquipment(Equipment equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        return UpdateRow("Equipment", equipment.Id, new (string Column, object? Value)[]
        {
            ("Name", equipment.Name),
            ("Model", equipment.Model),
            ("EntryDate", equipment.EntryDate),
            ("Owner", equipment.Owner),
            ("Status", equipment.Status),
            ("Remark", equipment.Remark)
        });
    }

    /// <summary>按 Id 删除机具设备记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteEquipment(int id)
    {
        return DeleteRow("Equipment", id);
    }

    /// <summary>读取全部设备租赁记录，按起租日期倒序排列。</summary>
    /// <returns>租赁台账列表；无数据时返回空集合。</returns>
    public List<RentalDevice> GetRentalDevices()
    {
        return Query<RentalDevice>("SELECT * FROM RentalDevice ORDER BY RentStart DESC, Id DESC;");
    }

    /// <summary>新增一条设备租赁记录。</summary>
    /// <param name="device">待新增的租赁记录，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddRentalDevice(RentalDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        device.Id = InsertRow("RentalDevice", new (string Column, object? Value)[]
        {
            ("DeviceName", device.DeviceName),
            ("Lessor", device.Lessor),
            ("RentStart", device.RentStart),
            ("RentEnd", device.RentEnd),
            ("MonthlyRent", device.MonthlyRent),
            ("SettlementAmount", device.SettlementAmount),
            ("Remark", device.Remark)
        });

        return device.Id;
    }

    /// <summary>按 Id 更新设备租赁记录。</summary>
    /// <param name="device">携带 Id 与新值的租赁记录。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateRentalDevice(RentalDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        return UpdateRow("RentalDevice", device.Id, new (string Column, object? Value)[]
        {
            ("DeviceName", device.DeviceName),
            ("Lessor", device.Lessor),
            ("RentStart", device.RentStart),
            ("RentEnd", device.RentEnd),
            ("MonthlyRent", device.MonthlyRent),
            ("SettlementAmount", device.SettlementAmount),
            ("Remark", device.Remark)
        });
    }

    /// <summary>按 Id 删除设备租赁记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteRentalDevice(int id)
    {
        return DeleteRow("RentalDevice", id);
    }

    /// <summary>读取全部物资核销记录，按核销月份倒序排列。</summary>
    /// <returns>核销列表；无数据时返回空集合。</returns>
    public List<MaterialWriteOff> GetMaterialWriteOffs()
    {
        return Query<MaterialWriteOff>("SELECT * FROM MaterialWriteOff ORDER BY WriteOffMonth DESC, Id DESC;");
    }

    /// <summary>新增一条物资核销记录。</summary>
    /// <param name="item">待新增的核销记录，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddMaterialWriteOff(MaterialWriteOff item)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Id = InsertRow("MaterialWriteOff", new (string Column, object? Value)[]
        {
            ("MaterialName", item.MaterialName),
            ("WriteOffMonth", item.WriteOffMonth),
            ("WriteOffQty", item.WriteOffQty),
            ("UnitPrice", item.UnitPrice),
            ("Amount", item.Amount),
            ("Reason", item.Reason),
            ("Remark", item.Remark)
        });

        return item.Id;
    }

    /// <summary>按 Id 更新物资核销记录。</summary>
    /// <param name="item">携带 Id 与新值的核销记录。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateMaterialWriteOff(MaterialWriteOff item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return UpdateRow("MaterialWriteOff", item.Id, new (string Column, object? Value)[]
        {
            ("MaterialName", item.MaterialName),
            ("WriteOffMonth", item.WriteOffMonth),
            ("WriteOffQty", item.WriteOffQty),
            ("UnitPrice", item.UnitPrice),
            ("Amount", item.Amount),
            ("Reason", item.Reason),
            ("Remark", item.Remark)
        });
    }

    /// <summary>按 Id 删除物资核销记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteMaterialWriteOff(int id)
    {
        return DeleteRow("MaterialWriteOff", id);
    }

    /// <summary>读取全部地材记录，按记录日期倒序排列。</summary>
    /// <returns>地材列表；无数据时返回空集合。</returns>
    public List<EarthMaterial> GetEarthMaterials()
    {
        return Query<EarthMaterial>("SELECT * FROM EarthMaterial ORDER BY RecordDate DESC, Id DESC;");
    }

    /// <summary>新增一条地材记录。</summary>
    /// <param name="item">待新增的地材记录，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddEarthMaterial(EarthMaterial item)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Id = InsertRow("EarthMaterial", new (string Column, object? Value)[]
        {
            ("Type", item.Type),
            ("Specification", item.Specification),
            ("Unit", item.Unit),
            ("Quantity", item.Quantity),
            ("UnitPrice", item.UnitPrice),
            ("TransportFee", item.TransportFee),
            ("SupplierName", item.SupplierName),
            ("RecordDate", item.RecordDate),
            ("Remark", item.Remark)
        });

        return item.Id;
    }

    /// <summary>按 Id 更新地材记录。</summary>
    /// <param name="item">携带 Id 与新值的地材记录。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateEarthMaterial(EarthMaterial item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return UpdateRow("EarthMaterial", item.Id, new (string Column, object? Value)[]
        {
            ("Type", item.Type),
            ("Specification", item.Specification),
            ("Unit", item.Unit),
            ("Quantity", item.Quantity),
            ("UnitPrice", item.UnitPrice),
            ("TransportFee", item.TransportFee),
            ("SupplierName", item.SupplierName),
            ("RecordDate", item.RecordDate),
            ("Remark", item.Remark)
        });
    }

    /// <summary>按 Id 删除地材记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteEarthMaterial(int id)
    {
        return DeleteRow("EarthMaterial", id);
    }

    /// <summary>读取全部库存流水，按发生日期倒序、Id 倒序排列。</summary>
    /// <returns>库存流水列表；无数据时返回空集合。</returns>
    public List<StockRecord> GetStockRecords()
    {
        return GetStockRecords(null, null, null);
    }

    /// <summary>按来源与日期区间筛选库存流水。</summary>
    /// <param name="materialSource">物资来源，为 null 或空表示不限。</param>
    /// <param name="dateFrom">起始日期（含），格式 yyyy-MM-dd，为 null 或空表示不限。</param>
    /// <param name="dateTo">结束日期（含），格式 yyyy-MM-dd，为 null 或空表示不限。</param>
    /// <returns>符合条件的流水列表。</returns>
    public List<StockRecord> GetStockRecords(string? materialSource, string? dateFrom, string? dateTo)
    {
        // 拼入 SQL 的只有固定的条件片段，用户输入一律走参数，不存在注入风险
        var sql = "SELECT * FROM StockRecord WHERE 1 = 1";
        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(materialSource))
        {
            sql += " AND MaterialSource = @source";
            parameters["@source"] = materialSource;
        }

        if (!string.IsNullOrWhiteSpace(dateFrom))
        {
            sql += " AND OccurDate >= @from";
            parameters["@from"] = dateFrom;
        }

        if (!string.IsNullOrWhiteSpace(dateTo))
        {
            sql += " AND OccurDate <= @to";
            parameters["@to"] = dateTo;
        }

        sql += " ORDER BY OccurDate DESC, Id DESC;";
        return Query<StockRecord>(sql, parameters);
    }

    /// <summary>新增一条库存流水。</summary>
    /// <param name="record">待新增的流水，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddStockRecord(StockRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        record.Id = InsertRow("StockRecord", new (string Column, object? Value)[]
        {
            ("MaterialSource", record.MaterialSource),
            ("MaterialName", record.MaterialName),
            ("Specification", record.Specification),
            ("Unit", record.Unit),
            ("ChangeType", record.ChangeType),
            ("Quantity", record.Quantity),
            ("OccurDate", record.OccurDate),
            ("Handler", record.Handler),
            ("Remark", record.Remark)
        });

        return record.Id;
    }

    /// <summary>按 Id 更新库存流水。</summary>
    /// <param name="record">携带 Id 与新值的流水。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateStockRecord(StockRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return UpdateRow("StockRecord", record.Id, new (string Column, object? Value)[]
        {
            ("MaterialSource", record.MaterialSource),
            ("MaterialName", record.MaterialName),
            ("Specification", record.Specification),
            ("Unit", record.Unit),
            ("ChangeType", record.ChangeType),
            ("Quantity", record.Quantity),
            ("OccurDate", record.OccurDate),
            ("Handler", record.Handler),
            ("Remark", record.Remark)
        });
    }

    /// <summary>按 Id 删除库存流水。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteStockRecord(int id)
    {
        return DeleteRow("StockRecord", id);
    }

    /// <summary>读取全部维保与年检记录，按维保日期倒序排列。</summary>
    /// <returns>维保记录列表；无数据时返回空集合。</returns>
    public List<EquipmentMaintenance> GetEquipmentMaintenances()
    {
        return Query<EquipmentMaintenance>("SELECT * FROM EquipmentMaintenance ORDER BY MaintainDate DESC, Id DESC;");
    }

    /// <summary>新增一条维保/年检记录。</summary>
    /// <param name="item">待新增的记录，其 Id 会被忽略并回填为自增主键。</param>
    /// <returns>新记录的自增主键。</returns>
    public int AddEquipmentMaintenance(EquipmentMaintenance item)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Id = InsertRow("EquipmentMaintenance", new (string Column, object? Value)[]
        {
            ("EquipmentName", item.EquipmentName),
            ("MaintainType", item.MaintainType),
            ("MaintainDate", item.MaintainDate),
            ("NextDueDate", item.NextDueDate),
            ("Cost", item.Cost),
            ("Vendor", item.Vendor),
            ("Handler", item.Handler),
            ("Remark", item.Remark)
        });

        return item.Id;
    }

    /// <summary>按 Id 更新维保/年检记录。</summary>
    /// <param name="item">携带 Id 与新值的记录。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateEquipmentMaintenance(EquipmentMaintenance item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return UpdateRow("EquipmentMaintenance", item.Id, new (string Column, object? Value)[]
        {
            ("EquipmentName", item.EquipmentName),
            ("MaintainType", item.MaintainType),
            ("MaintainDate", item.MaintainDate),
            ("NextDueDate", item.NextDueDate),
            ("Cost", item.Cost),
            ("Vendor", item.Vendor),
            ("Handler", item.Handler),
            ("Remark", item.Remark)
        });
    }

    /// <summary>按 Id 删除维保/年检记录。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteEquipmentMaintenance(int id)
    {
        return DeleteRow("EquipmentMaintenance", id);
    }

    /// <summary>
    /// 按物资名称/规格/单位汇总指定来源的库存结存。
    /// 结存口径：入库 + 退库 - 出库 + 盘点差异（盘点记录的数量本身即为差异值）。
    /// </summary>
    /// <param name="materialSource">物资来源，取值「甲供」或「自购」。</param>
    /// <returns>汇总行列表；无数据时返回空集合。</returns>
    public List<StockSummaryItem> GetStockSummary(string materialSource)
    {
        const string sql = @"
SELECT MaterialName, Specification, Unit,
       IFNULL(SUM(CASE WHEN ChangeType = '入库' THEN Quantity ELSE 0 END), 0) AS InQty,
       IFNULL(SUM(CASE WHEN ChangeType = '出库' THEN Quantity ELSE 0 END), 0) AS OutQty,
       IFNULL(SUM(CASE WHEN ChangeType = '退库' THEN Quantity ELSE 0 END), 0) AS ReturnQty,
       IFNULL(SUM(CASE WHEN ChangeType = '入库' THEN Quantity
                       WHEN ChangeType = '退库' THEN Quantity
                       WHEN ChangeType = '出库' THEN -Quantity
                       ELSE Quantity END), 0) AS Balance
FROM StockRecord
WHERE MaterialSource = @source
GROUP BY MaterialName, Specification, Unit
ORDER BY MaterialName;";

        return Query<StockSummaryItem>(sql, new Dictionary<string, object?> { ["@source"] = materialSource });
    }

    /// <summary>
    /// 返回结存低于阈值的物资清单。甲供与自购两个来源分开汇总后再合并，
    /// 同名同规格的物资跨来源累加，避免同一物资出现两行。
    /// </summary>
    /// <param name="threshold">低库存阈值，结存严格小于该值即视为预警。</param>
    /// <returns>预警清单，按结存升序；无预警时返回空集合。</returns>
    public List<StockWarningItem> GetStockWarnings(int threshold)
    {
        // 用「名称|规格|单位」拼键做内存合并：SQL 端跨来源聚合需要 UNION 子查询，可读性差且难维护
        var merged = new Dictionary<string, StockSummaryItem>(StringComparer.Ordinal);

        foreach (var source in new[] { "甲供", "自购" })
        {
            foreach (var item in GetStockSummary(source))
            {
                var key = $"{item.MaterialName}|{item.Specification}|{item.Unit}";

                if (merged.TryGetValue(key, out var existing))
                {
                    existing.InQty += item.InQty;
                    existing.OutQty += item.OutQty;
                    existing.ReturnQty += item.ReturnQty;
                    existing.Balance += item.Balance;
                }
                else
                {
                    merged[key] = new StockSummaryItem
                    {
                        MaterialName = item.MaterialName,
                        Specification = item.Specification,
                        Unit = item.Unit,
                        InQty = item.InQty,
                        OutQty = item.OutQty,
                        ReturnQty = item.ReturnQty,
                        Balance = item.Balance
                    };
                }
            }
        }

        return merged.Values
            .Where(x => x.Balance < threshold)
            .OrderBy(x => x.Balance)
            .Select(x => new StockWarningItem
            {
                MaterialName = x.MaterialName,
                Specification = x.Specification,
                Unit = x.Unit,
                Balance = x.Balance
            })
            .ToList();
    }

    /// <summary>按月份汇总核销数量与金额。</summary>
    /// <param name="month">核销月份，格式 yyyy-MM；为 null 或空表示汇总全部月份。</param>
    /// <returns>按月分组的汇总列表，按月份倒序。</returns>
    public List<WriteOffSummaryItem> GetMonthlyWriteOffSummary(string? month)
    {
        const string sql = @"
SELECT WriteOffMonth,
       COUNT(1) AS ItemCount,
       IFNULL(SUM(WriteOffQty), 0) AS TotalQty,
       IFNULL(SUM(Amount), 0) AS TotalAmount
FROM MaterialWriteOff
WHERE (@month IS NULL OR WriteOffMonth = @month)
GROUP BY WriteOffMonth
ORDER BY WriteOffMonth DESC;";

        // 传 DBNull 时 SQL 中的 @month IS NULL 成立，从而不附加月份过滤
        var parameters = new Dictionary<string, object?>
        {
            ["@month"] = string.IsNullOrWhiteSpace(month) ? null : month
        };

        return Query<WriteOffSummaryItem>(sql, parameters);
    }

    /// <summary>获取供应商合同与月度结算金额汇总，按结算金额降序。</summary>
    /// <returns>供应商汇总列表；无数据时返回空集合。</returns>
    public List<SupplierSettlementItem> GetSupplierSettlementSummary()
    {
        return Query<SupplierSettlementItem>(
            "SELECT Name, ContractNo, MonthlySettlement, SignDate FROM Supplier ORDER BY MonthlySettlement DESC;");
    }

    // ============================ 「项目部 / 期数」维度 ============================
    // 维度设计为通用可配置：项目部与期数都由用户在设置页自由维护，二者不绑定；
    // 哪些业务表启用该维度由 DeptPhaseApplicableTable 配置决定，勾选时通过 ALTER TABLE
    // 给目标表补 ProjectDept / ProjectPhase 两列（取消勾选只停用界面，列与历史数据保留）。
    // 记录上的维度值就是普通文本：期数为空串表示该记录无期数（如梧州项目），
    // 删除某个项目部/期数候选项不影响已落库的记录文本。

    /// <summary>默认项目部，首次初始化时写入。</summary>
    private static readonly string[] DefaultProjectDepts = { "信宜", "梧州" };

    /// <summary>默认期数，首次初始化时写入；期数只是候选项，不与任何项目部绑定。</summary>
    private static readonly string[] DefaultProjectPhases = { "一期", "二期", "三期" };

    /// <summary>
    /// 可启用维度的候选业务表清单（表名, 显示名, 是否默认启用）。
    /// 当前仅物资台账、渣土运输、自购物资三张表完成了界面接入并默认启用；
    /// 其余表勾选后只完成数据层列迁移，界面接入需另行开发。
    /// </summary>
    private static readonly (string Table, string Display, bool Enabled)[] DeptPhaseTableCandidates =
    {
        ("MaterialLedger", "物资台账", true),
        ("SlagTransport", "渣土运输", true),
        ("SelfPurchasedMaterial", "自购物资", true),
        ("PartyAMaterial", "甲供物资", false),
        ("StockRecord", "库存流水", false),
        ("MaterialWriteOff", "物资核销", false),
        ("Supplier", "供应商管理", false),
        ("Equipment", "机具设备", false),
        ("EquipmentMaintenance", "设备维保", false),
        ("RentalDevice", "租赁设备", false),
        ("SmallEquipment", "小型机具", false),
        ("ExcavatorEntry", "挖机进场", false),
        ("EarthMaterial", "地材", false),
        ("WorkLog", "工作日志", false)
    };

    /// <summary>
    /// 幂等写入维度种子数据：默认项目部、默认期数与适用栏目配置。
    /// 已存在的同名项跳过（INSERT OR IGNORE），用户改过的配置不会被覆盖。
    /// 与 EnsureXxxColumns 同理使用已初始化的连接串，避免递归触发 Initialize。
    /// </summary>
    private void SeedDeptPhaseDimension()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        for (var i = 0; i < DefaultProjectDepts.Length; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO ProjectDept (Name, SortOrder) VALUES (@name, @sortOrder);";
            command.Parameters.AddWithValue("@name", DefaultProjectDepts[i]);
            command.Parameters.AddWithValue("@sortOrder", i + 1);
            command.ExecuteNonQuery();
        }

        for (var i = 0; i < DefaultProjectPhases.Length; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO ProjectPhase (Name, SortOrder) VALUES (@name, @sortOrder);";
            command.Parameters.AddWithValue("@name", DefaultProjectPhases[i]);
            command.Parameters.AddWithValue("@sortOrder", i + 1);
            command.ExecuteNonQuery();
        }

        foreach (var (table, display, enabled) in DeptPhaseTableCandidates)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR IGNORE INTO DeptPhaseApplicableTable (TableName, DisplayName, IsEnabled) VALUES (@tableName, @displayName, @isEnabled);";
            command.Parameters.AddWithValue("@tableName", table);
            command.Parameters.AddWithValue("@displayName", display);
            command.Parameters.AddWithValue("@isEnabled", enabled ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 启动时按适用栏目配置给已启用的业务表补 ProjectDept / ProjectPhase 两列。
    /// 老库升级后默认启用的三张表（物资台账、渣土运输、自购物资）在这里自动完成列迁移。
    /// </summary>
    private void ApplyDeptPhaseColumnMigrations()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var enabledTables = new List<string>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT TableName FROM DeptPhaseApplicableTable WHERE IsEnabled = 1;";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                enabledTables.Add(reader.GetString(0));
            }
        }

        foreach (var table in enabledTables)
        {
            EnsureDeptPhaseColumns(connection, table);
        }
    }

    /// <summary>
    /// 确保指定业务表包含 ProjectDept / ProjectPhase 两列，缺列时执行 ALTER TABLE 补齐。
    /// 表名只可能来自代码常量（候选清单或配置表种子数据），不存在注入风险。
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="tableName">业务表名。</param>
    private static void EnsureDeptPhaseColumns(SqliteConnection connection, string tableName)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var infoCommand = connection.CreateCommand())
        {
            infoCommand.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = infoCommand.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info 的列顺序固定，索引 1 为列名
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[] { "ProjectDept", "ProjectPhase" })
        {
            if (existing.Contains(column))
            {
                continue;
            }

            // SQLite 的 ADD COLUMN 只接受常量默认值，统一用空串：空串即「未指定 / 无期数」
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {column} TEXT NOT NULL DEFAULT '';";
            alterCommand.ExecuteNonQuery();
        }
    }

    /// <summary>读取全部项目部，按显示顺序排列。</summary>
    /// <returns>项目部列表；无数据时返回空集合。</returns>
    public List<ProjectDept> GetProjectDepts()
    {
        return Query<ProjectDept>("SELECT * FROM ProjectDept ORDER BY SortOrder, Id;");
    }

    /// <summary>新增一个项目部，显示顺序自动排在最后。</summary>
    /// <param name="name">项目部名称（调用方已去空白）。</param>
    /// <returns>新记录的自增主键。</returns>
    /// <exception cref="SqliteException">名称重复（UNIQUE 约束）时抛出。</exception>
    public int AddProjectDept(string name)
    {
        return InsertRow("ProjectDept", new (string Column, object? Value)[]
        {
            ("Name", name),
            ("SortOrder", NextSortOrder("ProjectDept"))
        });
    }

    /// <summary>按 Id 更新项目部名称与显示顺序。</summary>
    /// <param name="dept">携带 Id 与新值的项目部实体。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateProjectDept(ProjectDept dept)
    {
        ArgumentNullException.ThrowIfNull(dept);

        return UpdateRow("ProjectDept", dept.Id, new (string Column, object? Value)[]
        {
            ("Name", dept.Name),
            ("SortOrder", dept.SortOrder)
        });
    }

    /// <summary>按 Id 删除项目部。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteProjectDept(int id)
    {
        return DeleteRow("ProjectDept", id);
    }

    /// <summary>读取全部期数，按显示顺序排列。</summary>
    /// <returns>期数列表；无数据时返回空集合。</returns>
    public List<ProjectPhase> GetProjectPhases()
    {
        return Query<ProjectPhase>("SELECT * FROM ProjectPhase ORDER BY SortOrder, Id;");
    }

    /// <summary>新增一个期数，显示顺序自动排在最后。</summary>
    /// <param name="name">期数名称（调用方已去空白）。</param>
    /// <returns>新记录的自增主键。</returns>
    /// <exception cref="SqliteException">名称重复（UNIQUE 约束）时抛出。</exception>
    public int AddProjectPhase(string name)
    {
        return InsertRow("ProjectPhase", new (string Column, object? Value)[]
        {
            ("Name", name),
            ("SortOrder", NextSortOrder("ProjectPhase"))
        });
    }

    /// <summary>按 Id 更新期数名称与显示顺序。</summary>
    /// <param name="phase">携带 Id 与新值的期数实体。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int UpdateProjectPhase(ProjectPhase phase)
    {
        ArgumentNullException.ThrowIfNull(phase);

        return UpdateRow("ProjectPhase", phase.Id, new (string Column, object? Value)[]
        {
            ("Name", phase.Name),
            ("SortOrder", phase.SortOrder)
        });
    }

    /// <summary>按 Id 删除期数。</summary>
    /// <param name="id">要删除的记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    public int DeleteProjectPhase(int id)
    {
        return DeleteRow("ProjectPhase", id);
    }

    /// <summary>读取全部适用栏目配置，按 Id 排列（与候选清单顺序一致）。</summary>
    /// <returns>配置列表；无数据时返回空集合。</returns>
    public List<DeptPhaseApplicableTable> GetDeptPhaseApplicableTables()
    {
        return Query<DeptPhaseApplicableTable>("SELECT * FROM DeptPhaseApplicableTable ORDER BY Id;");
    }

    /// <summary>判断指定业务表是否已启用「项目部 / 期数」维度。</summary>
    /// <param name="tableName">业务表名（代码常量）。</param>
    /// <returns>已启用返回 true。</returns>
    public bool IsDeptPhaseTableEnabled(string tableName)
    {
        return ExecuteScalarCount(
            "SELECT COUNT(1) FROM DeptPhaseApplicableTable WHERE TableName = @tableName AND IsEnabled = 1;",
            new Dictionary<string, object?> { ["@tableName"] = tableName }) > 0;
    }

    /// <summary>
    /// 设置指定业务表的维度启用状态。启用时立即执行 ALTER TABLE 补列迁移；
    /// 停用时仅更新配置，列与历史数据保留，避免误操作丢数据。
    /// </summary>
    /// <param name="tableName">业务表名。</param>
    /// <param name="enabled">是否启用。</param>
    public void SetDeptPhaseTableEnabled(string tableName, bool enabled)
    {
        ExecuteNonQuery(
            "UPDATE DeptPhaseApplicableTable SET IsEnabled = @isEnabled WHERE TableName = @tableName;",
            new Dictionary<string, object?>
            {
                ["@isEnabled"] = enabled ? 1 : 0,
                ["@tableName"] = tableName
            });

        if (!enabled)
        {
            return;
        }

        // 启用即迁移：此时 _initialized 已为 true，走 CreateConnection 安全
        using var connection = CreateConnection();
        EnsureDeptPhaseColumns(connection, tableName);
    }

    /// <summary>统计指定项目部名称在已启用维度表中的使用次数（用于删除前拦截）。</summary>
    /// <param name="name">项目部名称。</param>
    /// <returns>使用该名称的记录总数。</returns>
    public long CountProjectDeptUsage(string name)
    {
        return CountDeptPhaseValueUsage("ProjectDept", name);
    }

    /// <summary>统计指定期数名称在已启用维度表中的使用次数（用于删除前拦截）。</summary>
    /// <param name="name">期数名称。</param>
    /// <returns>使用该名称的记录总数。</returns>
    public long CountProjectPhaseUsage(string name)
    {
        return CountDeptPhaseValueUsage("ProjectPhase", name);
    }

    /// <summary>统计维度取值在所有已启用表中的引用总数（内部通用实现）。</summary>
    /// <param name="column">维度列名（代码常量，ProjectDept 或 ProjectPhase）。</param>
    /// <param name="name">维度取值。</param>
    /// <returns>引用总数。</returns>
    private long CountDeptPhaseValueUsage(string column, string name)
    {
        var enabledTables = GetDeptPhaseApplicableTables()
            .Where(x => x.IsEnabled)
            .Select(x => x.TableName)
            .ToList();

        long total = 0;
        foreach (var table in enabledTables)
        {
            total += ExecuteScalarCount(
                $"SELECT COUNT(1) FROM {table} WHERE {column} = @name;",
                new Dictionary<string, object?> { ["@name"] = name });
        }

        return total;
    }

    /// <summary>把已启用维度表中引用旧项目部名的业务记录统一改为新名（设置页重命名项目部时级联调用）。</summary>
    /// <param name="oldName">旧项目部名称。</param>
    /// <param name="newName">新项目部名称。</param>
    /// <returns>被更新的业务记录总数。</returns>
    public int RenameProjectDeptEverywhere(string oldName, string newName)
    {
        return RenameDeptPhaseValueEverywhere("ProjectDept", oldName, newName);
    }

    /// <summary>把已启用维度表中引用旧期数名的业务记录统一改为新名（设置页重命名期数时级联调用）。</summary>
    /// <param name="oldName">旧期数名称。</param>
    /// <param name="newName">新期数名称。</param>
    /// <returns>被更新的业务记录总数。</returns>
    public int RenameProjectPhaseEverywhere(string oldName, string newName)
    {
        return RenameDeptPhaseValueEverywhere("ProjectPhase", oldName, newName);
    }

    /// <summary>在所有已启用维度表中批量改写维度取值（内部通用实现）。</summary>
    /// <param name="column">维度列名（代码常量，ProjectDept 或 ProjectPhase）。</param>
    /// <param name="oldName">旧取值。</param>
    /// <param name="newName">新取值。</param>
    /// <returns>被更新的记录总数。</returns>
    private int RenameDeptPhaseValueEverywhere(string column, string oldName, string newName)
    {
        var enabledTables = GetDeptPhaseApplicableTables()
            .Where(x => x.IsEnabled)
            .Select(x => x.TableName)
            .ToList();

        var total = 0;
        foreach (var table in enabledTables)
        {
            total += ExecuteNonQuery(
                $"UPDATE {table} SET {column} = @newName WHERE {column} = @oldName;",
                new Dictionary<string, object?>
                {
                    ["@newName"] = newName,
                    ["@oldName"] = oldName
                });
        }

        return total;
    }

    /// <summary>取指定维度表的下一个显示顺序值（当前最大值 + 1）。</summary>
    /// <param name="tableName">维度表名（代码常量）。</param>
    /// <returns>下一个显示顺序值。</returns>
    private long NextSortOrder(string tableName)
    {
        return ExecuteScalarCount($"SELECT IFNULL(MAX(SortOrder), 0) FROM {tableName};") + 1;
    }

    /// <summary>
    /// 在既有筛选条件上追加「项目部 / 期数」条件（内部通用实现）。
    /// 项目部：为空白表示不限制，否则精确相等；
    /// 期数：null 表示不限制，空串表示「仅无期数」，其余值精确相等。
    /// </summary>
    /// <param name="baseFilter">既有筛选构造器返回的 WHERE 子句与参数。</param>
    /// <param name="projectDept">项目部筛选值。</param>
    /// <param name="projectPhase">期数筛选值（三态，见上）。</param>
    /// <returns>合并后的 WHERE 子句与参数集合。</returns>
    private static (string Where, Dictionary<string, object?> Parameters) AppendDeptPhaseFilter(
        (string Where, Dictionary<string, object?> Parameters) baseFilter,
        string? projectDept,
        string? projectPhase)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseFilter.Where))
        {
            parts.Add(baseFilter.Where);
        }

        if (!string.IsNullOrWhiteSpace(projectDept))
        {
            parts.Add("ProjectDept = @projectDept");
            baseFilter.Parameters["@projectDept"] = projectDept;
        }

        if (projectPhase is not null)
        {
            if (projectPhase.Length == 0)
            {
                // 「仅无期数」：空串与历史 NULL 都算无期数
                parts.Add("(ProjectPhase IS NULL OR ProjectPhase = '')");
            }
            else
            {
                parts.Add("ProjectPhase = @projectPhase");
                baseFilter.Parameters["@projectPhase"] = projectPhase;
            }
        }

        return (string.Join(" AND ", parts), baseFilter.Parameters);
    }

    // ============================ 分页查询（LIMIT / OFFSET） ============================
    // 背景：表格控件 CommunityToolkit DataGrid 7.1.2 不提供行虚拟化，行数多时会把全部行都实例化，
    // 因此所有明细类页面统一改为分页加载。下面每个表都提供「取一页数据」与「统计总数」两个方法，
    // 二者共用同一个筛选构造器，保证页内数据与总条数的口径完全一致。
    // 日期筛选统一用 SQLite 的 date(列) 解析成 yyyy-MM-dd 后再比较：无法解析的脏数据 date() 返回 NULL，
    // 比较结果为假而被排除，与旧版内存筛选（DateTime.TryParse 失败即排除）行为一致。
    // 原有的全量 GetXxx() 方法继续保留，供导出等需要完整数据的场景使用。

    /// <summary>按表名与筛选条件读取一页数据（内部通用实现）。</summary>
    /// <typeparam name="T">实体类型。</typeparam>
    /// <param name="tableName">表名（代码常量）。</param>
    /// <param name="orderBy">排序子句（代码常量）。</param>
    /// <param name="whereSql">WHERE 子句（不含关键字），可为空表示不限。</param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="parameters">筛选参数。</param>
    /// <returns>当前页的实体列表；无数据时返回空集合。</returns>
    private List<T> QueryPagedByTable<T>(
        string tableName,
        string orderBy,
        string? whereSql,
        int page,
        int pageSize,
        IDictionary<string, object?>? parameters = null)
        where T : new()
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > 500 ? 20 : pageSize;
        var offset = (safePage - 1) * safeSize;

        var merged = parameters is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
        merged["@__limit"] = safeSize;
        merged["@__offset"] = offset;

        var sql = new StringBuilder("SELECT * FROM ").Append(tableName);
        if (!string.IsNullOrWhiteSpace(whereSql))
        {
            sql.Append(" WHERE ").Append(whereSql);
        }

        sql.Append(" ORDER BY ").Append(orderBy).Append(" LIMIT @__limit OFFSET @__offset;");
        return Query<T>(sql.ToString(), merged);
    }

    /// <summary>按表名与筛选条件统计总记录数（内部通用实现）。</summary>
    /// <param name="tableName">表名（代码常量）。</param>
    /// <param name="whereSql">与分页查询一致的 WHERE 子句。</param>
    /// <param name="parameters">筛选参数。</param>
    /// <returns>满足条件的记录总数。</returns>
    private long CountByTable(string tableName, string? whereSql, IDictionary<string, object?>? parameters = null)
    {
        var sql = string.IsNullOrWhiteSpace(whereSql)
            ? string.Concat("SELECT COUNT(1) FROM ", tableName, ";")
            : string.Concat("SELECT COUNT(1) FROM ", tableName, " WHERE ", whereSql, ";");

        return ExecuteScalarCount(sql, parameters);
    }

    /// <summary>
    /// 组装分页查询的 WHERE 子句与参数字典（内部通用实现）。
    /// 列名与参数名均来自代码常量，用户输入一律走参数，不存在注入风险。
    /// </summary>
    /// <param name="dateRange">日期区间：(列名, 起始日期, 结束日期)，两端可各自为 null 表示不限。</param>
    /// <param name="equalities">精确相等条件：(列名, 参数名, 值)，值为空时跳过。</param>
    /// <param name="keywords">模糊包含条件：(列名, 参数名, 值)，值为空时跳过，通配符由代码拼接。</param>
    /// <returns>WHERE 子句与参数集合；无任何条件时 WHERE 为空串。</returns>
    private static (string Where, Dictionary<string, object?> Parameters) BuildPagedFilter(
        (string Column, string? From, string? To)? dateRange,
        IReadOnlyList<(string Column, string Parameter, string? Value)> equalities,
        IReadOnlyList<(string Column, string Parameter, string? Value)> keywords)
    {
        var parts = new List<string>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (dateRange is { } range)
        {
            if (!string.IsNullOrWhiteSpace(range.From))
            {
                var name = "@" + range.Column + "From";
                parts.Add(string.Concat("date(", range.Column, ") >= ", name));
                parameters[name] = range.From;
            }

            if (!string.IsNullOrWhiteSpace(range.To))
            {
                var name = "@" + range.Column + "To";
                parts.Add(string.Concat("date(", range.Column, ") <= ", name));
                parameters[name] = range.To;
            }
        }

        foreach (var (column, parameter, value) in equalities)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parts.Add(string.Concat(column, " = ", parameter));
            parameters[parameter] = value;
        }

        foreach (var (column, parameter, value) in keywords)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parts.Add(string.Concat(column, " LIKE ", parameter));
            parameters[parameter] = "%" + value.Trim() + "%";
        }

        return (string.Join(" AND ", parts), parameters);
    }

    /// <summary>读取指定列的取值清单（去重、非空），用于筛选下拉框的候选项。</summary>
    /// <param name="tableName">表名（代码常量）。</param>
    /// <param name="column">列名（代码常量）。</param>
    /// <returns>按字典序排列的去重取值；无数据时返回空集合。</returns>
    public List<string> GetDistinctValues(string tableName, string column)
    {
        var results = new List<string>();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(
            "SELECT DISTINCT ", column, " FROM ", tableName,
            " WHERE ", column, " IS NOT NULL AND ", column, " <> '' ORDER BY ", column, ";");

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    /// <summary>物资台账（地材）：按进场日期区间、物资名称与项目部/期数筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="materialName">物资名称，为 null 表示全部。</param>
    /// <param name="projectDept">项目部，为 null 或空白表示全部。</param>
    /// <param name="projectPhase">期数：null 表示全部，空串表示仅无期数，其余精确匹配。</param>
    /// <returns>当前页的台账列表。</returns>
    public List<MaterialLedger> GetMaterialLedgersPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? materialName, string? projectDept = null, string? projectPhase = null)
    {
        var (where, parameters) = MaterialLedgerFilter(dateFrom, dateTo, materialName, projectDept, projectPhase);
        return QueryPagedByTable<MaterialLedger>("MaterialLedger", "EntryDate DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计物资台账（地材）在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="materialName">物资名称，为 null 表示全部。</param>
    /// <param name="projectDept">项目部，为 null 或空白表示全部。</param>
    /// <param name="projectPhase">期数：null 表示全部，空串表示仅无期数，其余精确匹配。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountMaterialLedgers(string? dateFrom, string? dateTo, string? materialName, string? projectDept = null, string? projectPhase = null)
    {
        var (where, parameters) = MaterialLedgerFilter(dateFrom, dateTo, materialName, projectDept, projectPhase);
        return CountByTable("MaterialLedger", where, parameters);
    }

    /// <summary>构造物资台账的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) MaterialLedgerFilter(string? dateFrom, string? dateTo, string? materialName, string? projectDept, string? projectPhase)
    {
        var baseFilter = BuildPagedFilter(
            ("EntryDate", dateFrom, dateTo),
            new[] { ("MaterialName", "@materialName", materialName) },
            Array.Empty<(string, string, string?)>());
        return AppendDeptPhaseFilter(baseFilter, projectDept, projectPhase);
    }

    /// <summary>甲供物资：按记录日期区间与物资名称筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始记录日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束记录日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="materialName">物资名称，为 null 表示全部。</param>
    /// <returns>当前页的甲供物资列表。</returns>
    public List<PartyAMaterial> GetPartyAMaterialsPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? materialName)
    {
        var (where, parameters) = PartyAMaterialFilter(dateFrom, dateTo, materialName);
        return QueryPagedByTable<PartyAMaterial>("PartyAMaterial", "RecordDate DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计甲供物资在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始记录日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束记录日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="materialName">物资名称，为 null 表示全部。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountPartyAMaterials(string? dateFrom, string? dateTo, string? materialName)
    {
        var (where, parameters) = PartyAMaterialFilter(dateFrom, dateTo, materialName);
        return CountByTable("PartyAMaterial", where, parameters);
    }

    /// <summary>构造甲供物资的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) PartyAMaterialFilter(string? dateFrom, string? dateTo, string? materialName)
    {
        return BuildPagedFilter(
            ("RecordDate", dateFrom, dateTo),
            new[] { ("MaterialName", "@materialName", materialName) },
            Array.Empty<(string, string, string?)>());
    }

    /// <summary>渣土运输：按进场日期区间、车牌号关键字与项目部/期数筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="plateKeyword">车牌号关键字，为 null 表示不限。</param>
    /// <param name="projectDept">项目部，为 null 或空白表示全部。</param>
    /// <param name="projectPhase">期数：null 表示全部，空串表示仅无期数，其余精确匹配。</param>
    /// <returns>当前页的运输记录列表。</returns>
    public List<SlagTransport> GetSlagTransportsPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? plateKeyword, string? projectDept = null, string? projectPhase = null)
    {
        var (where, parameters) = SlagTransportFilter(dateFrom, dateTo, plateKeyword, projectDept, projectPhase);
        return QueryPagedByTable<SlagTransport>("SlagTransport", "EntryDate DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计渣土运输在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="plateKeyword">车牌号关键字，为 null 表示不限。</param>
    /// <param name="projectDept">项目部，为 null 或空白表示全部。</param>
    /// <param name="projectPhase">期数：null 表示全部，空串表示仅无期数，其余精确匹配。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountSlagTransports(string? dateFrom, string? dateTo, string? plateKeyword, string? projectDept = null, string? projectPhase = null)
    {
        var (where, parameters) = SlagTransportFilter(dateFrom, dateTo, plateKeyword, projectDept, projectPhase);
        return CountByTable("SlagTransport", where, parameters);
    }

    /// <summary>构造渣土运输的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) SlagTransportFilter(string? dateFrom, string? dateTo, string? plateKeyword, string? projectDept, string? projectPhase)
    {
        var baseFilter = BuildPagedFilter(
            ("EntryDate", dateFrom, dateTo),
            Array.Empty<(string, string, string?)>(),
            new[] { ("PlateNumber", "@plateKeyword", plateKeyword) });
        return AppendDeptPhaseFilter(baseFilter, projectDept, projectPhase);
    }

    /// <summary>自购物资：按记录日期区间、物资名称与项目部/期数筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始记录日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束记录日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="materialName">物资名称，为 null 表示全部。</param>
    /// <param name="projectDept">项目部，为 null 或空白表示全部。</param>
    /// <param name="projectPhase">期数：null 表示全部，空串表示仅无期数，其余精确匹配。</param>
    /// <returns>当前页的自购物资列表。</returns>
    public List<SelfPurchasedMaterial> GetSelfPurchasedMaterialsPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? materialName, string? projectDept = null, string? projectPhase = null)
    {
        var (where, parameters) = SelfPurchasedMaterialFilter(dateFrom, dateTo, materialName, projectDept, projectPhase);
        return QueryPagedByTable<SelfPurchasedMaterial>("SelfPurchasedMaterial", "RecordDate DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计自购物资在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始记录日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束记录日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="materialName">物资名称，为 null 表示全部。</param>
    /// <param name="projectDept">项目部，为 null 或空白表示全部。</param>
    /// <param name="projectPhase">期数：null 表示全部，空串表示仅无期数，其余精确匹配。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountSelfPurchasedMaterials(string? dateFrom, string? dateTo, string? materialName, string? projectDept = null, string? projectPhase = null)
    {
        var (where, parameters) = SelfPurchasedMaterialFilter(dateFrom, dateTo, materialName, projectDept, projectPhase);
        return CountByTable("SelfPurchasedMaterial", where, parameters);
    }

    /// <summary>构造自购物资的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) SelfPurchasedMaterialFilter(string? dateFrom, string? dateTo, string? materialName, string? projectDept, string? projectPhase)
    {
        var baseFilter = BuildPagedFilter(
            ("RecordDate", dateFrom, dateTo),
            new[] { ("MaterialName", "@materialName", materialName) },
            Array.Empty<(string, string, string?)>());
        return AppendDeptPhaseFilter(baseFilter, projectDept, projectPhase);
    }

    /// <summary>供应商：按名称与合同编号关键字筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="nameKeyword">供应商名称关键字，为 null 表示不限。</param>
    /// <param name="contractNoKeyword">合同编号关键字，为 null 表示不限。</param>
    /// <returns>当前页的供应商列表。</returns>
    public List<Supplier> GetSuppliersPaged(int page, int pageSize, string? nameKeyword, string? contractNoKeyword)
    {
        var (where, parameters) = SupplierFilter(nameKeyword, contractNoKeyword);
        return QueryPagedByTable<Supplier>("Supplier", "Name", where, page, pageSize, parameters);
    }

    /// <summary>统计供应商在筛选条件下的总条数。</summary>
    /// <param name="nameKeyword">供应商名称关键字，为 null 表示不限。</param>
    /// <param name="contractNoKeyword">合同编号关键字，为 null 表示不限。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountSuppliers(string? nameKeyword, string? contractNoKeyword)
    {
        var (where, parameters) = SupplierFilter(nameKeyword, contractNoKeyword);
        return CountByTable("Supplier", where, parameters);
    }

    /// <summary>构造供应商的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) SupplierFilter(string? nameKeyword, string? contractNoKeyword)
    {
        return BuildPagedFilter(
            null,
            Array.Empty<(string, string, string?)>(),
            new[]
            {
                ("Name", "@nameKeyword", nameKeyword),
                ("ContractNo", "@contractNoKeyword", contractNoKeyword)
            });
    }

    /// <summary>物资核销：按物资名称关键字与核销月份筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="materialNameKeyword">物资名称关键字，为 null 表示不限。</param>
    /// <param name="month">核销月份 yyyy-MM，为 null 表示全部月份。</param>
    /// <returns>当前页的核销记录列表。</returns>
    public List<MaterialWriteOff> GetMaterialWriteOffsPaged(int page, int pageSize, string? materialNameKeyword, string? month)
    {
        var (where, parameters) = MaterialWriteOffFilter(materialNameKeyword, month);
        return QueryPagedByTable<MaterialWriteOff>("MaterialWriteOff", "WriteOffMonth DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计物资核销在筛选条件下的总条数。</summary>
    /// <param name="materialNameKeyword">物资名称关键字，为 null 表示不限。</param>
    /// <param name="month">核销月份 yyyy-MM，为 null 表示全部月份。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountMaterialWriteOffs(string? materialNameKeyword, string? month)
    {
        var (where, parameters) = MaterialWriteOffFilter(materialNameKeyword, month);
        return CountByTable("MaterialWriteOff", where, parameters);
    }

    /// <summary>构造物资核销的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) MaterialWriteOffFilter(string? materialNameKeyword, string? month)
    {
        return BuildPagedFilter(
            null,
            new[] { ("WriteOffMonth", "@writeOffMonth", month) },
            new[] { ("MaterialName", "@materialNameKeyword", materialNameKeyword) });
    }

    /// <summary>库存流水：按发生日期区间、物资来源与物资名称关键字筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始发生日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束发生日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="materialSource">物资来源（甲供 / 自购），为 null 表示全部。</param>
    /// <param name="nameKeyword">物资名称关键字，为 null 表示不限。</param>
    /// <returns>当前页的库存流水列表。</returns>
    public List<StockRecord> GetStockRecordsPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? materialSource, string? nameKeyword)
    {
        var (where, parameters) = StockRecordFilter(dateFrom, dateTo, materialSource, nameKeyword);
        return QueryPagedByTable<StockRecord>("StockRecord", "OccurDate DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计库存流水在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始发生日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束发生日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="materialSource">物资来源（甲供 / 自购），为 null 表示全部。</param>
    /// <param name="nameKeyword">物资名称关键字，为 null 表示不限。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountStockRecords(string? dateFrom, string? dateTo, string? materialSource, string? nameKeyword)
    {
        var (where, parameters) = StockRecordFilter(dateFrom, dateTo, materialSource, nameKeyword);
        return CountByTable("StockRecord", where, parameters);
    }

    /// <summary>构造库存流水的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) StockRecordFilter(string? dateFrom, string? dateTo, string? materialSource, string? nameKeyword)
    {
        return BuildPagedFilter(
            ("OccurDate", dateFrom, dateTo),
            new[] { ("MaterialSource", "@materialSource", materialSource) },
            new[] { ("MaterialName", "@nameKeyword", nameKeyword) });
    }

    /// <summary>机具设备：按进场日期区间与设备名称关键字筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="nameKeyword">设备名称关键字，为 null 表示不限。</param>
    /// <returns>当前页的设备列表。</returns>
    public List<Equipment> GetEquipmentsPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? nameKeyword)
    {
        var (where, parameters) = EquipmentFilter(dateFrom, dateTo, nameKeyword);
        return QueryPagedByTable<Equipment>("Equipment", "Name", where, page, pageSize, parameters);
    }

    /// <summary>统计机具设备在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="nameKeyword">设备名称关键字，为 null 表示不限。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountEquipments(string? dateFrom, string? dateTo, string? nameKeyword)
    {
        var (where, parameters) = EquipmentFilter(dateFrom, dateTo, nameKeyword);
        return CountByTable("Equipment", where, parameters);
    }

    /// <summary>构造机具设备的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) EquipmentFilter(string? dateFrom, string? dateTo, string? nameKeyword)
    {
        return BuildPagedFilter(
            ("EntryDate", dateFrom, dateTo),
            Array.Empty<(string, string, string?)>(),
            new[] { ("Name", "@nameKeyword", nameKeyword) });
    }

    /// <summary>设备维保：按维保日期区间、设备名称关键字与维保类型筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始维保日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束维保日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="nameKeyword">设备名称关键字，为 null 表示不限。</param>
    /// <param name="maintainType">维保类型，为 null 表示全部。</param>
    /// <returns>当前页的维保记录列表。</returns>
    public List<EquipmentMaintenance> GetEquipmentMaintenancesPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? nameKeyword, string? maintainType)
    {
        var (where, parameters) = EquipmentMaintenanceFilter(dateFrom, dateTo, nameKeyword, maintainType);
        return QueryPagedByTable<EquipmentMaintenance>("EquipmentMaintenance", "MaintainDate DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计设备维保在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始维保日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束维保日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="nameKeyword">设备名称关键字，为 null 表示不限。</param>
    /// <param name="maintainType">维保类型，为 null 表示全部。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountEquipmentMaintenances(string? dateFrom, string? dateTo, string? nameKeyword, string? maintainType)
    {
        var (where, parameters) = EquipmentMaintenanceFilter(dateFrom, dateTo, nameKeyword, maintainType);
        return CountByTable("EquipmentMaintenance", where, parameters);
    }

    /// <summary>构建设备维保的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) EquipmentMaintenanceFilter(string? dateFrom, string? dateTo, string? nameKeyword, string? maintainType)
    {
        return BuildPagedFilter(
            ("MaintainDate", dateFrom, dateTo),
            new[] { ("MaintainType", "@maintainType", maintainType) },
            new[] { ("EquipmentName", "@nameKeyword", nameKeyword) });
    }

    /// <summary>设备租赁：按起租日期区间、设备名称与出租方关键字筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始起租日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束起租日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="nameKeyword">设备名称关键字，为 null 表示不限。</param>
    /// <param name="lessorKeyword">出租方关键字，为 null 表示不限。</param>
    /// <returns>当前页的租赁记录列表。</returns>
    public List<RentalDevice> GetRentalDevicesPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? nameKeyword, string? lessorKeyword)
    {
        var (where, parameters) = RentalDeviceFilter(dateFrom, dateTo, nameKeyword, lessorKeyword);
        return QueryPagedByTable<RentalDevice>("RentalDevice", "RentStart DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计设备租赁在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始起租日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束起租日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="nameKeyword">设备名称关键字，为 null 表示不限。</param>
    /// <param name="lessorKeyword">出租方关键字，为 null 表示不限。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountRentalDevices(string? dateFrom, string? dateTo, string? nameKeyword, string? lessorKeyword)
    {
        var (where, parameters) = RentalDeviceFilter(dateFrom, dateTo, nameKeyword, lessorKeyword);
        return CountByTable("RentalDevice", where, parameters);
    }

    /// <summary>构建设备租赁的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) RentalDeviceFilter(string? dateFrom, string? dateTo, string? nameKeyword, string? lessorKeyword)
    {
        return BuildPagedFilter(
            ("RentStart", dateFrom, dateTo),
            Array.Empty<(string, string, string?)>(),
            new[]
            {
                ("DeviceName", "@nameKeyword", nameKeyword),
                ("Lessor", "@lessorKeyword", lessorKeyword)
            });
    }

    /// <summary>小型机具：按名称关键字、编号关键字与性能状态筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="nameKeyword">机具名称关键字，为 null 表示不限。</param>
    /// <param name="codeKeyword">机具编号关键字，为 null 表示不限。</param>
    /// <param name="performanceStatus">性能状态，为 null 表示全部。</param>
    /// <returns>当前页的小型机具列表。</returns>
    public List<SmallEquipment> GetSmallEquipmentsPaged(int page, int pageSize, string? nameKeyword, string? codeKeyword, string? performanceStatus)
    {
        var (where, parameters) = SmallEquipmentFilter(nameKeyword, codeKeyword, performanceStatus);
        return QueryPagedByTable<SmallEquipment>("SmallEquipment", "Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计小型机具在筛选条件下的总条数。</summary>
    /// <param name="nameKeyword">机具名称关键字，为 null 表示不限。</param>
    /// <param name="codeKeyword">机具编号关键字，为 null 表示不限。</param>
    /// <param name="performanceStatus">性能状态，为 null 表示全部。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountSmallEquipments(string? nameKeyword, string? codeKeyword, string? performanceStatus)
    {
        var (where, parameters) = SmallEquipmentFilter(nameKeyword, codeKeyword, performanceStatus);
        return CountByTable("SmallEquipment", where, parameters);
    }

    /// <summary>构造小型机具的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) SmallEquipmentFilter(string? nameKeyword, string? codeKeyword, string? performanceStatus)
    {
        return BuildPagedFilter(
            null,
            new[] { ("PerformanceStatus", "@performanceStatus", performanceStatus) },
            new[]
            {
                ("Name", "@nameKeyword", nameKeyword),
                ("Code", "@codeKeyword", codeKeyword)
            });
    }

    /// <summary>挖机进场资料：按进场日期区间与设备名称关键字筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="nameKeyword">设备名称关键字，为 null 表示不限。</param>
    /// <returns>当前页的进场资料列表。</returns>
    public List<ExcavatorEntry> GetExcavatorEntriesPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? nameKeyword)
    {
        var (where, parameters) = ExcavatorEntryFilter(dateFrom, dateTo, nameKeyword);
        return QueryPagedByTable<ExcavatorEntry>("ExcavatorEntry", "Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计挖机进场资料在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束进场日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="nameKeyword">设备名称关键字，为 null 表示不限。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountExcavatorEntries(string? dateFrom, string? dateTo, string? nameKeyword)
    {
        var (where, parameters) = ExcavatorEntryFilter(dateFrom, dateTo, nameKeyword);
        return CountByTable("ExcavatorEntry", where, parameters);
    }

    /// <summary>构造挖机进场资料的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) ExcavatorEntryFilter(string? dateFrom, string? dateTo, string? nameKeyword)
    {
        return BuildPagedFilter(
            ("EntryDate", dateFrom, dateTo),
            Array.Empty<(string, string, string?)>(),
            new[] { ("EquipmentName", "@nameKeyword", nameKeyword) });
    }

    /// <summary>工作日志：按日志日期区间与内容关键字筛选后分页读取。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="dateFrom">起始日志日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束日志日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="contentKeyword">日志内容关键字，为 null 表示不限。</param>
    /// <returns>当前页的日志列表。</returns>
    public List<WorkLog> GetWorkLogsPaged(int page, int pageSize, string? dateFrom, string? dateTo, string? contentKeyword)
    {
        var (where, parameters) = WorkLogFilter(dateFrom, dateTo, contentKeyword);
        return QueryPagedByTable<WorkLog>("WorkLog", "LogDate DESC, Id DESC", where, page, pageSize, parameters);
    }

    /// <summary>统计工作日志在筛选条件下的总条数。</summary>
    /// <param name="dateFrom">起始日志日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="dateTo">结束日志日期 yyyy-MM-dd，为 null 表示不限。</param>
    /// <param name="contentKeyword">日志内容关键字，为 null 表示不限。</param>
    /// <returns>满足条件的记录总数。</returns>
    public long CountWorkLogs(string? dateFrom, string? dateTo, string? contentKeyword)
    {
        var (where, parameters) = WorkLogFilter(dateFrom, dateTo, contentKeyword);
        return CountByTable("WorkLog", where, parameters);
    }

    /// <summary>构造工作日志的筛选条件。</summary>
    private static (string Where, Dictionary<string, object?> Parameters) WorkLogFilter(string? dateFrom, string? dateTo, string? contentKeyword)
    {
        return BuildPagedFilter(
            ("LogDate", dateFrom, dateTo),
            Array.Empty<(string, string, string?)>(),
            new[] { ("Content", "@contentKeyword", contentKeyword) });
    }

    /// <summary>
    /// 通用插入辅助方法。表名与列名均来自代码内常量（非用户输入），可安全拼接进 SQL；
    /// 取值一律走参数化，杜绝注入。
    /// </summary>
    /// <param name="table">目标表名。</param>
    /// <param name="columns">列名与对应值的集合，不含自增主键 Id。</param>
    /// <returns>新记录的自增主键。</returns>
    private int InsertRow(string table, IReadOnlyList<(string Column, object? Value)> columns)
    {
        var names = string.Join(", ", columns.Select(c => c.Column));
        var placeholders = string.Join(", ", columns.Select(c => "@" + c.Column));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {table} ({names}) VALUES ({placeholders}); SELECT last_insert_rowid();";

        foreach (var column in columns)
        {
            command.Parameters.AddWithValue("@" + column.Column, column.Value ?? DBNull.Value);
        }

        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>通用更新辅助方法。表名、列名均为代码内常量，取值参数化。</summary>
    /// <param name="table">目标表名。</param>
    /// <param name="id">记录主键。</param>
    /// <param name="columns">列名与对应新值的集合，不含 Id。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    private int UpdateRow(string table, int id, IReadOnlyList<(string Column, object? Value)> columns)
    {
        var assignments = string.Join(", ", columns.Select(c => $"{c.Column} = @{c.Column}"));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET {assignments} WHERE Id = @Id;";
        command.Parameters.AddWithValue("@Id", id);

        foreach (var column in columns)
        {
            command.Parameters.AddWithValue("@" + column.Column, column.Value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>通用删除辅助方法。</summary>
    /// <param name="table">目标表名。</param>
    /// <param name="id">记录主键。</param>
    /// <returns>受影响行数，0 表示记录不存在。</returns>
    private int DeleteRow(string table, int id)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table} WHERE Id = @Id;";
        command.Parameters.AddWithValue("@Id", id);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// 解析数据库文件路径：取配置文件中的 DatabasePath；为空则用程序目录下 Data\MaterialSystem.db。
    /// 确保目标目录存在，并在首次使用时把旧版本留在 %LOCALAPPDATA% 的数据库迁移过来。
    /// </summary>
    /// <returns>数据库文件的绝对路径。</returns>
    private static string ResolveDatabasePath()
    {
        // 默认值由 SettingsService 统一决定，避免路径规则在两处各写一份导致不一致
        var target = SettingsService.Instance.EffectiveDatabasePath;

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            // 用户可能把数据库配到尚不存在的目录，这里先建好，否则 SQLite 打开会失败
            Directory.CreateDirectory(directory);
        }

        MigrateLegacyDatabaseIfNeeded(target);
        return target;
    }

    /// <summary>
    /// 兼容历史版本的数据位置：
    /// 1) 最早版本把数据库放在 %LOCALAPPDATA%\MaterialManagementApp\app.db；
    /// 2) 后续版本放在程序目录下的 Data\MaterialSystem.db；
    /// 3) v1.1.0 默认放在 %LOCALAPPDATA%\MaterialSystem\Data\MaterialSystem.db
    ///    （安装向导指定的数据目录 dataRoot\Data 下也可能有库）；
    /// 4) 当前版本默认放回程序目录下的 Data\MaterialSystem.db（绿色便携，数据随软件走）。
    /// 新位置没有文件而旧位置有时复制一次，避免用户升级或换数据目录后看起来「数据丢了」。
    /// </summary>
    /// <param name="targetPath">新位置的数据库绝对路径。</param>
    internal static void MigrateLegacyDatabaseIfNeeded(string targetPath)
    {
        try
        {
            // 新位置已有数据时绝不覆盖，否则每次启动都会把用户新录入的内容冲掉
            if (File.Exists(targetPath))
            {
                return;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 按「越新的版本越优先」的顺序尝试，命中第一个存在的就停下
            var candidates = new List<string>
            {
                // v1.1.0 的默认位置（本轮改动前数据库实际落在这里）
                Path.Combine(localAppData, "MaterialSystem", "Data", "MaterialSystem.db"),
                Path.Combine(localAppData, "MaterialManagementApp", "app.db"),
                Path.Combine(AppContext.BaseDirectory, "Data", "MaterialSystem.db")
            };

            // 安装向导指定数据目录时的位置（install-config.json 的 dataRoot）
            if (AppPaths.DataRoot is { } dataRoot)
            {
                candidates.Insert(1, Path.Combine(dataRoot, "Data", "MaterialSystem.db"));
            }

            foreach (var legacyPath in candidates)
            {
                // 目标与候选路径重合（如未配置数据目录时）无迁移必要
                if (string.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(legacyPath))
                {
                    continue;
                }

                File.Copy(legacyPath, targetPath, overwrite: false);
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] 已迁移旧数据库：{legacyPath} -> {targetPath}");
                return;
            }
        }
        catch (Exception ex)
        {
            // 迁移失败不能让应用启动失败：旧库仍在原处，用户可手工拷贝过来
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] 旧数据库迁移失败：{ex.Message}");
        }
    }

    /// <summary>获取指定实体类型的「列名 -> 属性」映射，首次调用时构建并缓存。</summary>
    /// <param name="type">实体类型。</param>
    /// <returns>键为列名（忽略大小写）、值为属性的字典。</returns>
    private static Dictionary<string, PropertyInfo> GetPropertyMap(Type type)
    {
        lock (_propertyCache)
        {
            if (_propertyCache.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite)
                {
                    continue;
                }

                map[property.Name] = property;
            }

            _propertyCache[type] = map;
            return map;
        }
    }

    /// <summary>
    /// 把数据库返回的值转换为属性类型。SQLite 中 INTEGER 读出来是 long、REAL 是 double，
    /// 与实体声明类型不一定一致，必须显式转换，否则 SetValue 会抛异常。
    /// </summary>
    /// <param name="value">数据库原始值。</param>
    /// <param name="targetType">属性声明类型。</param>
    /// <returns>可直接赋给属性的值；无法转换时返回 null。</returns>
    private static object? ConvertToTargetType(object value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            if (underlying == typeof(string))
            {
                return value.ToString() ?? string.Empty;
            }

            return Convert.ChangeType(value, underlying);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] 列值转换失败：{value} -> {underlying.Name}，{ex.Message}");
            return null;
        }
    }

    /// <summary>把参数字典逐项绑定到命令上。</summary>
    /// <param name="command">目标命令。</param>
    /// <param name="parameters">参数名与值的键值对，可为 null。</param>
    private static void AddParameters(SqliteCommand command, IDictionary<string, object?>? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var pair in parameters)
        {
            // null 值必须显式转成 DBNull，AddWithValue 传 null 会抛异常
            command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        }
    }

}
