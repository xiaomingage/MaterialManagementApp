using System.Text.Json.Serialization;

namespace MaterialManagementApp.Models;

/// <summary>
/// 施工活动业务类别。数据库以枚举名（EarthMaterialInbound / SpoilTransfer / Unknown）存储，
/// 便于后续统计与人工排查；无法判定的照片统一归入 Unknown，不强行塞进两个业务类别。
/// </summary>
public enum SiteProgressCategory
{
    /// <summary>待确认：文件名、目录与 OCR 文本均未命中业务关键词。</summary>
    Unknown = 0,

    /// <summary>地材进场。</summary>
    EarthMaterialInbound = 1,

    /// <summary>渣土转运（含外运、消纳）。</summary>
    SpoilTransfer = 2
}

/// <summary>
/// 每日施工活动汇总，对应数据库 SiteProgressDaily 表。
/// 按「工点 + 日期 + 类别」聚合水印照片：照片数量、首末拍摄时间、代表坐标与来源文件。
/// 经纬度入库时已由 WGS84 转换为 GCJ-02（高德地图坐标系），原始 WGS84 坐标另存两列便于回溯。
/// </summary>
public sealed class SiteProgressDaily
{
    /// <summary>主键，自增。</summary>
    public int Id { get; set; }

    /// <summary>工点名，取自水印「施工区域」、目录名、文件名或坐标附近最近工点。</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>记录日期，格式 yyyy-MM-dd。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>业务类别。</summary>
    public SiteProgressCategory Category { get; set; } = SiteProgressCategory.Unknown;

    /// <summary>经度（GCJ-02，供高德地图直接使用）。</summary>
    public double Lng { get; set; }

    /// <summary>纬度（GCJ-02，供高德地图直接使用）。</summary>
    public double Lat { get; set; }

    /// <summary>原始经度（WGS84，水印相机记录值）。</summary>
    public double WgsLng { get; set; }

    /// <summary>原始纬度（WGS84，水印相机记录值）。</summary>
    public double WgsLat { get; set; }

    /// <summary>当日该类别的照片张数。</summary>
    public int PhotoCount { get; set; }

    /// <summary>当日最早拍摄时间，格式 yyyy-MM-dd HH:mm。</summary>
    public string FirstShotAt { get; set; } = string.Empty;

    /// <summary>当日最晚拍摄时间，格式 yyyy-MM-dd HH:mm。</summary>
    public string LastShotAt { get; set; } = string.Empty;

    /// <summary>最近一次来源图片的完整路径，便于回溯原图。</summary>
    public string SourceFilePath { get; set; } = string.Empty;

    /// <summary>是否已定位（提取到有效经纬度）。false 表示进入「待定位池」，不上图但保留记录。</summary>
    public bool IsLocated { get; set; } = true;

    /// <summary>最后更新时间，格式 yyyy-MM-dd HH:mm:ss。</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>类别中文名，供界面与气泡展示。</summary>
    [JsonIgnore]
    public string CategoryText => Category switch
    {
        SiteProgressCategory.EarthMaterialInbound => "地材进场",
        SiteProgressCategory.SpoilTransfer => "渣土转运",
        _ => "待确认"
    };

    /// <summary>类别展示文本：未定位的记录追加「待定位」标识，避免与已上图记录混淆。</summary>
    [JsonIgnore]
    public string DisplayCategory => IsLocated ? CategoryText : $"{CategoryText}（待定位）";

    /// <summary>照片张数展示文本。</summary>
    [JsonIgnore]
    public string PhotoCountText => $"{PhotoCount} 张";

    /// <summary>最近拍摄时间展示文本；缺失时退回日期。</summary>
    [JsonIgnore]
    public string ShotAtText => string.IsNullOrWhiteSpace(LastShotAt) ? Date : LastShotAt;
}

/// <summary>
/// 地图上一个工点的气泡数据。由 <c>SiteProgressService</c> 按数据库记录实时聚合，
/// 不伪造任何进度百分比：语义只来自「当日有无作业 + 连续无更新天数」。
/// </summary>
public sealed class SiteProgressMapSite
{
    /// <summary>工点名。</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>经度（GCJ-02）。</summary>
    public double Lng { get; set; }

    /// <summary>纬度（GCJ-02）。</summary>
    public double Lat { get; set; }

    /// <summary>当日进度语义，如「今日地材进场 3 张」「今日无作业记录」。</summary>
    public string ProgressText { get; set; } = string.Empty;

    /// <summary>状态语义：有作业 / 停滞 N 天 / 中断 N 天。</summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>状态色：绿色有作业、黄色停滞 1–2 天、红色中断 3 天及以上。</summary>
    public string ColorHex { get; set; } = "#10B981";

    /// <summary>距最近一次有记录的天数（0 表示当天有作业）。</summary>
    public int DaysSinceActivity { get; set; }

    /// <summary>当日照片总张数。</summary>
    public int TodayPhotoCount { get; set; }

    /// <summary>最近一次拍摄时间，供气泡副标题使用。</summary>
    public string LastShotAt { get; set; } = string.Empty;
}
