using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// 工点生命周期（四档状态 + 人工完工覆盖 + 复工）与地图中心计算的单元测试。
/// 只校验聚合逻辑，不启动 WinUI，也不触碰真实配置与 WebView2。
/// </summary>
public sealed class SiteLifecycleTests : IDisposable
{
    private readonly string _dbPath;

    private readonly DatabaseService _db;

    private readonly SiteProgressService _service = SiteProgressService.Instance;

    public SiteLifecycleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"site-lifecycle-{Guid.NewGuid():N}.db");
        _db = new DatabaseService(_dbPath);
        _db.Initialize();
    }

    /// <summary>清理临时数据库文件。</summary>
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void Lifecycle_UsesDaysForThreeAlarmLevels()
    {
        var today = new DateTime(2026, 11, 17);
        InsertSite("今日工点", today.ToString("yyyy-MM-dd"), 113.1, 23.1);
        InsertSite("昨日工点", today.AddDays(-1).ToString("yyyy-MM-dd"), 113.2, 23.2);
        InsertSite("三天前工点", today.AddDays(-3).ToString("yyyy-MM-dd"), 113.3, 23.3);

        var sites = _service.BuildMapSites(_db, today).ToDictionary(site => site.SiteName);

        Assert.Equal("working", sites["今日工点"].LifecycleStatus);
        Assert.Equal("#10B981", sites["今日工点"].ColorHex);
        Assert.Equal("stalled", sites["昨日工点"].LifecycleStatus);
        Assert.Equal("#F59E0B", sites["昨日工点"].ColorHex);
        Assert.Equal("interrupted", sites["三天前工点"].LifecycleStatus);
        Assert.Equal("#EF4444", sites["三天前工点"].ColorHex);
    }

    [Fact]
    public void CompletedOverride_WinsOverRedAlarm()
    {
        var today = new DateTime(2026, 11, 17);
        InsertSite("已完工工点", today.AddDays(-10).ToString("yyyy-MM-dd"), 113.4, 23.4);

        _db.SaveSiteStatusOverride(new SiteStatusOverride
        {
            SiteKey = "已完工工点",
            Status = SiteLifecycleStatus.Completed,
            CompletedAt = "2026-11-16 10:00:00",
            Remark = "主体完成",
            UpdatedAt = "2026-11-16 10:00:00"
        });

        var site = Assert.Single(_service.BuildMapSites(_db, today));

        Assert.True(site.IsCompleted);
        Assert.Equal("completed", site.LifecycleStatus);
        Assert.Equal("已完工", site.StatusText);
        Assert.Equal(SiteProgressService.CompletedColorHex, site.ColorHex);
        Assert.Equal(10, site.DaysSinceActivity);
    }

    [Fact]
    public void RevivedToday_AddsBadgeFlag()
    {
        var today = new DateTime(2026, 11, 17);
        InsertSite("复工工点", today.AddDays(-5).ToString("yyyy-MM-dd"), 113.5, 23.5);

        _db.SaveSiteStatusOverride(new SiteStatusOverride
        {
            SiteKey = "复工工点",
            Status = SiteLifecycleStatus.Revived,
            CompletedAt = "2026-11-10 09:00:00",
            UpdatedAt = $"{today:yyyy-MM-dd} 08:30:00"
        });

        var site = Assert.Single(_service.BuildMapSites(_db, today));

        Assert.False(site.IsCompleted);
        Assert.True(site.IsRevived);
        // 恢复在建后按天数判定：5 天无更新 → 中断
        Assert.Equal("interrupted", site.LifecycleStatus);
    }

    [Fact]
    public void RevivedYesterday_DoesNotShowBadge()
    {
        var today = new DateTime(2026, 11, 17);
        InsertSite("复工工点", today.AddDays(-5).ToString("yyyy-MM-dd"), 113.5, 23.5);

        _db.SaveSiteStatusOverride(new SiteStatusOverride
        {
            SiteKey = "复工工点",
            Status = SiteLifecycleStatus.Revived,
            CompletedAt = "2026-11-10 09:00:00",
            UpdatedAt = "2026-11-16 08:30:00"
        });

        var site = Assert.Single(_service.BuildMapSites(_db, today));
        Assert.False(site.IsRevived);
    }

    [Fact]
    public void ComputeCenter_PrefersProjectDeptThenSiteAverageThenNull()
    {
        var dept = new MapFixedPoint("projectDept", "项目部", 113.9, 22.9);
        var warehouse = new MapFixedPoint("warehouse", "仓库", 114.1, 23.2);
        var sites = new List<SiteProgressMapSite>
        {
            new() { SiteName = "A", Lng = 113.0, Lat = 23.0 },
            new() { SiteName = "B", Lng = 113.4, Lat = 23.4 }
        };

        var withDept = SiteProgressService.ComputeCenter(sites, new[] { dept, warehouse });
        Assert.NotNull(withDept);
        Assert.Equal(113.9, withDept!.Lng, 6);
        Assert.Equal(22.9, withDept.Lat, 6);
        Assert.Equal(15, withDept.Zoom);

        var withoutDept = SiteProgressService.ComputeCenter(sites, new[] { warehouse });
        Assert.NotNull(withoutDept);
        Assert.Equal(113.2, withoutDept!.Lng, 6);
        Assert.Equal(23.2, withoutDept.Lat, 6);
        Assert.Equal(11, withoutDept.Zoom);

        Assert.Null(SiteProgressService.ComputeCenter(Array.Empty<SiteProgressMapSite>(), Array.Empty<MapFixedPoint>()));
    }

    [Fact]
    public void BuildFixedPoints_SkipsUnconfiguredAndInvalidCoordinates()
    {
        var empty = SiteProgressService.BuildFixedPoints(new AppSettings());
        Assert.Empty(empty);

        var invalid = SiteProgressService.BuildFixedPoints(new AppSettings
        {
            ProjectDeptName = "项目部",
            ProjectDeptLng = "abc",
            ProjectDeptLat = "23.1"
        });
        Assert.Empty(invalid);

        var valid = SiteProgressService.BuildFixedPoints(new AppSettings
        {
            ProjectDeptName = "项目部",
            ProjectDeptLng = "113.4",
            ProjectDeptLat = "23.1",
            WarehouseLng = "113.5",
            WarehouseLat = "23.2"
        });

        Assert.Equal(2, valid.Count);
        Assert.Equal("projectDept", valid[0].Type);
        Assert.Equal("项目部", valid[0].Name);
        Assert.Equal("warehouse", valid[1].Type);
        Assert.Equal("仓库", valid[1].Name);
    }

    /// <summary>插入一条已定位工点记录。</summary>
    private void InsertSite(string siteName, string date, double lng, double lat)
    {
        _db.InsertSiteProgress(new SiteProgressDaily
        {
            SiteName = siteName,
            Date = date,
            Category = SiteProgressCategory.EarthMaterialInbound,
            Lng = lng,
            Lat = lat,
            PhotoCount = 1,
            FirstShotAt = $"{date} 08:00",
            LastShotAt = $"{date} 09:00",
            SourceFilePath = $"{siteName}.jpg"
        });
    }
}
