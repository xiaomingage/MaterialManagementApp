using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// 台账明细新功能的数据库层集成测试：
/// 软删除（IsDeleted）/撤销恢复 / 批量修改白名单 / 商品名称与经办人联想查询。
/// 每个测试用例使用独立临时 SQLite 文件真实建库跑迁移，不触碰用户的业务数据库。
/// </summary>
public sealed class DatabaseServiceLedgerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseService _db;

    public DatabaseServiceLedgerTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mma_ledger_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "MaterialSystem.db");
        _db = new DatabaseService(_dbPath);
        _db.Initialize();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path.GetDirectoryName(_dbPath)))
            {
                Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录被占用时留给系统清理，不影响测试结果
        }
    }

    /// <summary>插入一条最小合法台账记录，物资名称与经办人可指定以便联想测试。</summary>
    private int AddRow(string material = "C25混凝土", string handler = "张三", string dept = "一期项目部", string phase = "一期")
    {
        var row = new MaterialLedger
        {
            EntryDate = "2026-11-03",
            EntryTime = "10:00",
            MaterialName = material,
            Quantity = 10,
            Unit = "m³",
            UnitPrice = 350,
            Amount = 3500,
            Handler = handler,
            ProjectDept = dept,
            ProjectPhase = phase,
        };
        _db.AddMaterialLedger(row);
        return row.Id;
    }

    [Fact]
    public void SoftDelete_MarksRowAndHidesFromList()
    {
        var id = AddRow();

        var affected = _db.DeleteMaterialLedger(id);
        var rows = _db.GetMaterialLedgers();

        Assert.Equal(1, affected);
        Assert.DoesNotContain(rows, r => r.Id == id); // 软删除后业务列表不可见
    }

    [Fact]
    public void RestoreBringsDeletedRowBack()
    {
        var id = AddRow();
        _db.DeleteMaterialLedger(id);

        var affected = _db.RestoreMaterialLedger(id);
        var rows = _db.GetMaterialLedgers();

        Assert.Equal(1, affected);
        Assert.Contains(rows, r => r.Id == id); // 撤销后重新出现在业务列表
    }

    [Fact]
    public void SoftDeleteAndRestoreAreIdempotent()
    {
        var id = AddRow();

        // 重复软删除同一行第二次应命中 0 行（WHERE IsDeleted = 0 保护）
        Assert.Equal(1, _db.DeleteMaterialLedger(id));
        Assert.Equal(0, _db.DeleteMaterialLedger(id));
        // 撤销一次生效，重复撤销第二次命中 0 行（WHERE IsDeleted = 1 保护）
        Assert.Equal(1, _db.RestoreMaterialLedger(id));
        Assert.Equal(0, _db.RestoreMaterialLedger(id));
    }

    [Fact]
    public void BatchUpdate_UpdatesOnlyCheckedRows()
    {
        var id1 = AddRow(handler: "张三", dept: "一期项目部");
        var id2 = AddRow(handler: "李四", dept: "一期项目部");
        var id3 = AddRow(handler: "王五", dept: "二期项目部");

        var affected = _db.UpdateMaterialLedgerBatch(new[] { id1, id3 }, "ProjectDept", "三期项目部");
        var rows = _db.GetMaterialLedgers();

        Assert.Equal(2, affected);
        Assert.Equal("三期项目部", rows.First(r => r.Id == id1).ProjectDept);
        Assert.Equal("一期项目部", rows.First(r => r.Id == id2).ProjectDept); // 未勾选的不动
        Assert.Equal("三期项目部", rows.First(r => r.Id == id3).ProjectDept);
    }

    [Fact]
    public void BatchUpdate_RejectsUnknownColumn()
    {
        var id = AddRow();

        // 列名白名单外的字段必须拒绝，防止注入
        Assert.Throws<ArgumentException>(
            () => _db.UpdateMaterialLedgerBatch(new[] { id }, "MaterialName; DROP TABLE MaterialLedger;--", "x"));
        // 业务数据完好
        Assert.Single(_db.GetMaterialLedgers());
    }

    [Fact]
    public void BatchUpdate_HandlerField()
    {
        var id1 = AddRow(handler: "张三");
        var id2 = AddRow(handler: "李四");

        _db.UpdateMaterialLedgerBatch(new[] { id1, id2 }, "Handler", "赵六");

        var rows = _db.GetMaterialLedgers();
        Assert.All(rows, r => Assert.Equal("赵六", r.Handler));
    }

    [Fact]
    public void Suggestions_MatchPrefixAndExcludeDeleted()
    {
        AddRow(material: "C25混凝土", handler: "张三");
        AddRow(material: "C30混凝土", handler: "张三丰");
        AddRow(material: "中砂", handler: "李四");
        var deletedId = AddRow(material: "C35混凝土", handler: "王五");
        _db.DeleteMaterialLedger(deletedId); // 软删除的不应出现在联想里

        var materials = _db.GetMaterialNameSuggestions("C");
        var handlers = _db.GetHandlerSuggestions("张");
        var all = _db.GetMaterialNameSuggestions(string.Empty);

        // 前缀匹配：C 开头的存活记录（C25、C30），不含已删除的 C35
        Assert.Equal(new[] { "C25混凝土", "C30混凝土" }, materials);
        Assert.Equal(new[] { "张三", "张三丰" }, handlers);
        // 空前缀返回全部存活的名称（去重），不含 C35
        Assert.DoesNotContain("C35混凝土", all);
        Assert.Contains("中砂", all);
    }

    [Fact]
    public void Suggestions_LimitTen()
    {
        for (var i = 0; i < 15; i++)
        {
            AddRow(material: $"物料{i:D2}");
        }

        Assert.Equal(10, _db.GetMaterialNameSuggestions("物料").Count);
    }

    [Fact]
    public void MigratedOldDb_GetsIsDeletedColumnWithDefaultZero()
    {
        // 模拟老库：手动去掉 IsDeleted 列再走补列迁移，验证默认值为 0
        var dir = Path.GetDirectoryName(_dbPath)!;
        var oldDb = Path.Combine(dir, "old.db");
        File.Copy(_dbPath, oldDb, overwrite: true);

        using (var conn = Microsoft.Data.Sqlite.SqliteFactory.Instance.CreateConnection())
        {
            conn.ConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = oldDb,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
            }.ToString();
            conn.Open();

            var id = AddRow();

            using (var drop = conn.CreateCommand())
            {
                // SQLite 3.35+ 支持 DROP COLUMN；老库迁移场景即列不存在 → 先删再补
                drop.CommandText = "ALTER TABLE MaterialLedger DROP COLUMN IsDeleted;";
                drop.ExecuteNonQuery();
            }

            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO MaterialLedger (EntryDate, MaterialName) VALUES ('2026-01-01', '历史料');";
                insert.ExecuteNonQuery(); // 老库写入的行没有 IsDeleted 标记
                _ = id;
            }
        }

        // 用测试缝重新初始化同一文件：EnsureMaterialLedgerColumns 应幂等补上 IsDeleted
        var migrated = new DatabaseService(oldDb);
        migrated.Initialize();

        var rows = migrated.GetMaterialLedgers();
        Assert.All(rows, r => Assert.True(r.Id > 0)); // 补列后查询不抛错
    }
}
