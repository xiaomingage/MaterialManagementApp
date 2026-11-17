using System;

namespace MaterialManagementApp.Common;

/// <summary>
/// 坐标系转换工具。水印相机记录的是 WGS84（GPS 原始坐标），高德地图使用 GCJ-02（火星坐标），
/// 两者直接混用会偏移数百米，因此所有进入地图渲染的坐标必须先经过本类转换。
/// </summary>
public static class CoordConverter
{
    /// <summary>克拉索夫斯基椭球长半轴（GCJ-02 算法固定常量）。</summary>
    private const double EllipsoidA = 6378245.0;

    /// <summary>椭球偏心率平方（GCJ-02 算法固定常量）。</summary>
    private const double EllipsoidEe = 0.00669342162296594323;

    /// <summary>
    /// WGS84 坐标转 GCJ-02。中国大陆境外坐标不做偏移（与高德官方行为一致），
    /// 无效坐标原样返回，避免把 0,0 之类的脏数据偏到海里。
    /// </summary>
    /// <param name="lng">WGS84 经度。</param>
    /// <param name="lat">WGS84 纬度。</param>
    /// <returns>转换后的 GCJ-02 经纬度。</returns>
    public static (double Lng, double Lat) Wgs84ToGcj02(double lng, double lat)
    {
        if (!IsValidCoordinate(lng, lat) || IsOutOfChina(lng, lat))
        {
            return (lng, lat);
        }

        var lngOffset = TransformLng(lng - 105.0, lat - 35.0);
        var latOffset = TransformLat(lng - 105.0, lat - 35.0);

        var radLat = lat / 180.0 * Math.PI;
        var magic = Math.Sin(radLat);
        magic = 1 - (EllipsoidEe * magic * magic);
        var sqrtMagic = Math.Sqrt(magic);

        lngOffset = (lngOffset * 180.0) / (EllipsoidA / sqrtMagic * Math.Cos(radLat) * Math.PI);
        latOffset = (latOffset * 180.0) / ((EllipsoidA * (1 - EllipsoidEe)) / (magic * sqrtMagic) * Math.PI);

        return (lng + lngOffset, lat + latOffset);
    }

    /// <summary>判断经纬度是否为可用的有效值（范围合法且不是 0,0 占位）。</summary>
    /// <param name="lng">经度。</param>
    /// <param name="lat">纬度。</param>
    /// <returns>有效返回 true。</returns>
    public static bool IsValidCoordinate(double lng, double lat) =>
        !double.IsNaN(lng)
        && !double.IsNaN(lat)
        && lng is >= -180 and <= 180
        && lat is >= -90 and <= 90
        && (Math.Abs(lng) > 1e-6 || Math.Abs(lat) > 1e-6);

    /// <summary>中国大陆粗略边界判断，境外坐标不做 GCJ-02 偏移。</summary>
    /// <param name="lng">经度。</param>
    /// <param name="lat">纬度。</param>
    /// <returns>在中国境外返回 true。</returns>
    private static bool IsOutOfChina(double lng, double lat) =>
        lng < 72.004 || lng > 137.8347 || lat < 0.8293 || lat > 55.8271;

    /// <summary>GCJ-02 纬度偏移量。</summary>
    private static double TransformLat(double x, double y)
    {
        var ret = -100.0 + (2.0 * x) + (3.0 * y) + (0.2 * y * y) + (0.1 * x * y) + (0.2 * Math.Sqrt(Math.Abs(x)));
        ret += ((20.0 * Math.Sin(6.0 * x * Math.PI)) + (20.0 * Math.Sin(2.0 * x * Math.PI))) * 2.0 / 3.0;
        ret += ((20.0 * Math.Sin(y * Math.PI)) + (40.0 * Math.Sin(y / 3.0 * Math.PI))) * 2.0 / 3.0;
        ret += ((160.0 * Math.Sin(y / 12.0 * Math.PI)) + (320 * Math.Sin(y * Math.PI / 30.0))) * 2.0 / 3.0;
        return ret;
    }

    /// <summary>GCJ-02 经度偏移量。</summary>
    private static double TransformLng(double x, double y)
    {
        var ret = 300.0 + x + (2.0 * y) + (0.1 * x * x) + (0.1 * x * y) + (0.1 * Math.Sqrt(Math.Abs(x)));
        ret += ((20.0 * Math.Sin(6.0 * x * Math.PI)) + (20.0 * Math.Sin(2.0 * x * Math.PI))) * 2.0 / 3.0;
        ret += ((20.0 * Math.Sin(x * Math.PI)) + (40.0 * Math.Sin(x / 3.0 * Math.PI))) * 2.0 / 3.0;
        ret += ((150.0 * Math.Sin(x / 12.0 * Math.PI)) + (300.0 * Math.Sin(x / 30.0 * Math.PI))) * 2.0 / 3.0;
        return ret;
    }
}
