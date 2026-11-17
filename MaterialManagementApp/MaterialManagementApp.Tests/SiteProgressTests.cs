using System;
using System.IO;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// 施工进度（水印图片扫描）数据层与坐标转换的单元测试：
/// 校验建表、汇总累加、待定位池保留以及 WGS84 → GCJ-02 偏移方向。
/// </summary>
public sealed class SiteProgressTests : IDisposable
{
    private readonly string _dbPath;

    private readonly DatabaseService _db;

    public SiteProgressTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"site-progress-{Guid.NewGuid():N}.db");
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
    public void Wgs84ToGcj02_ProducesChinaOffset()
    {
        var (lng, lat) = CoordConverter.Wgs84ToGcj02(113.324520, 23.106140);

        // 广州附近 GCJ-02 相对 WGS84 的偏移约数百米（经纬度差约 0.002~0.006）
        Assert.InRange(lng, 113.325, 113.335);
        Assert.InRange(lat, 23.102, 23.112);
        Assert.NotEqual(113.324520, lng);
        Assert.NotEqual(23.106140, lat);
    }

    [Fact]
    public void Wgs84ToGcj02_KeepsOutOfChinaCoordinate()
    {
        var (lng, lat) = CoordConverter.Wgs84ToGcj02(-122.4194, 37.7749);

        Assert.Equal(-122.4194, lng, 6);
        Assert.Equal(37.7749, lat, 6);
    }

    [Fact]
    public void Aggregate_AccumulatesPhotoCountAndShotRange()
    {
        _db.InsertSiteProgress(new SiteProgressDaily
        {
            SiteName = "信宜服务区",
            Date = "2026-11-17",
            Category = SiteProgressCategory.EarthMaterialInbound,
            Lng = 111.3,
            Lat = 22.3,
            WgsLng = 111.297,
            WgsLat = 22.297,
            PhotoCount = 1,
            FirstShotAt = "2026-11-17 08:10",
            LastShotAt = "2026-11-17 08:10",
            SourceFilePath = "a.jpg"
        });

        var found = _db.FindSiteProgressAggregate("信宜服务区", "2026-11-17", "EarthMaterialInbound");
        Assert.NotNull(found);
        Assert.Equal(SiteProgressCategory.EarthMaterialInbound, found!.Category);

        _db.UpdateSiteProgressAggregate(found.Id, 2, "2026-11-17 07:30", "2026-11-17 09:20", "b.jpg");

        var updated = _db.FindSiteProgressAggregate("信宜服务区", "2026-11-17", "EarthMaterialInbound");
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.PhotoCount);
        Assert.Equal("2026-11-17 07:30", updated.FirstShotAt);
        Assert.Equal("2026-11-17 09:20", updated.LastShotAt);
        Assert.Equal("b.jpg", updated.SourceFilePath);
    }

    [Fact]
    public void UnlocatedPhoto_IsKeptInPendingPool()
    {
        _db.InsertSiteProgress(new SiteProgressDaily
        {
            SiteName = "未命名工点",
            Date = "2026-11-17",
            Category = SiteProgressCategory.Unknown,
            PhotoCount = 1,
            FirstShotAt = "2026-11-17 10:00",
            LastShotAt = "2026-11-17 10:00",
            SourceFilePath = @"C:\photos\no-coord.jpg",
            IsLocated = false
        });

        Assert.Equal(1, _db.CountPendingLocationProgress());

        var byFile = _db.FindUnlocatedSiteProgressByFile(@"C:\photos\no-coord.jpg");
        Assert.NotNull(byFile);
        Assert.False(byFile!.IsLocated);
        Assert.Equal("待确认（待定位）", byFile.DisplayCategory);

        // 未定位记录不参与地图点位查询，但仍能在当日记录中看到
        Assert.Empty(_db.GetLatestSiteProgress());
        Assert.Single(_db.GetSiteProgressForDate("2026-11-17"));
    }

    [Fact]
    public void LatestSiteProgress_ReturnsNewestRowPerSiteCategory()
    {
        _db.InsertSiteProgress(new SiteProgressDaily
        {
            SiteName = "梧州工点",
            Date = "2026-11-14",
            Category = SiteProgressCategory.SpoilTransfer,
            Lng = 111.3,
            Lat = 23.4,
            PhotoCount = 1,
            FirstShotAt = "2026-11-14 09:00",
            LastShotAt = "2026-11-14 09:00",
            SourceFilePath = "old.jpg"
        });

        _db.InsertSiteProgress(new SiteProgressDaily
        {
            SiteName = "梧州工点",
            Date = "2026-11-16",
            Category = SiteProgressCategory.SpoilTransfer,
            Lng = 111.3,
            Lat = 23.4,
            PhotoCount = 2,
            FirstShotAt = "2026-11-16 09:00",
            LastShotAt = "2026-11-16 10:00",
            SourceFilePath = "new.jpg"
        });

        var latest = _db.GetLatestSiteProgress();

        Assert.Single(latest);
        Assert.Equal("2026-11-16", latest[0].Date);
        Assert.Equal(2, latest[0].PhotoCount);
    }

    [Fact]
    public void WatermarkFileState_RequiresProcessedStatusForDedup()
    {
        const string path = @"C:\photos\watermark.jpg";

        Assert.False(_db.IsWatermarkFileProcessed(path, 100, 123));

        _db.SaveWatermarkFileState(path, 100, 123, "Failed", "识别失败");
        Assert.False(_db.IsWatermarkFileProcessed(path, 100, 123));

        _db.SaveWatermarkFileState(path, 100, 123, "Processed", "信宜服务区");
        Assert.True(_db.IsWatermarkFileProcessed(path, 100, 123));

        // 文件大小或修改时间变化后需要重新处理
        Assert.False(_db.IsWatermarkFileProcessed(path, 200, 123));
        Assert.False(_db.IsWatermarkFileProcessed(path, 100, 456));
    }
}
