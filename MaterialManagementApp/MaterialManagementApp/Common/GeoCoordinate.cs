using System;
using System.Globalization;

namespace MaterialManagementApp.Common;

/// <summary>
/// 经纬度文本解析与校验。项目部 / 仓库固定点坐标以字符串保存在 AppSettings 中
/// （空串表示未配置），保存前统一走本类校验，避免非法值落盘后在绘制地图时静默失效。
/// 坐标一律按 GCJ-02（高德坐标系）理解，不做任何坐标转换。
/// </summary>
public static class GeoCoordinate
{
    /// <summary>
    /// 解析一对经纬度文本。
    /// 返回值语义：true 表示坐标有效；false 且 <paramref name="error"/> 为空表示「两边都留空（未配置）」；
    /// false 且 error 非空表示填写有误，调用方应拒绝保存并展示原因。
    /// </summary>
    /// <param name="lngText">经度文本。</param>
    /// <param name="latText">纬度文本。</param>
    /// <param name="lng">解析出的经度（未配置时为 0）。</param>
    /// <param name="lat">解析出的纬度（未配置时为 0）。</param>
    /// <param name="error">校验失败原因；未配置或有效时为空串。</param>
    /// <returns>坐标有效返回 true。</returns>
    public static bool TryParsePair(
        string? lngText, string? latText, out double lng, out double lat, out string error)
    {
        lng = 0;
        lat = 0;
        error = string.Empty;

        var lngValue = (lngText ?? string.Empty).Trim();
        var latValue = (latText ?? string.Empty).Trim();
        var lngEmpty = lngValue.Length == 0;
        var latEmpty = latValue.Length == 0;

        // 两边都空：视为未配置，属于合法状态
        if (lngEmpty && latEmpty)
        {
            return false;
        }

        if (lngEmpty || latEmpty)
        {
            error = "经度与纬度必须同时填写，或同时留空。";
            return false;
        }

        if (!double.TryParse(lngValue, NumberStyles.Float, CultureInfo.InvariantCulture, out lng)
            || !double.TryParse(latValue, NumberStyles.Float, CultureInfo.InvariantCulture, out lat))
        {
            error = "经纬度必须是数字（小数点为半角 .）。";
            return false;
        }

        if (lng < -180 || lng > 180)
        {
            error = "经度需在 -180 到 180 之间。";
            return false;
        }

        if (lat < -90 || lat > 90)
        {
            error = "纬度需在 -90 到 90 之间。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 判断已保存的坐标文本是否构成有效坐标（供地图侧计算中心点，不产生提示文案）。
    /// </summary>
    /// <param name="lngText">经度文本。</param>
    /// <param name="latText">纬度文本。</param>
    /// <param name="lng">解析出的经度。</param>
    /// <param name="lat">解析出的纬度。</param>
    /// <returns>坐标有效返回 true。</returns>
    public static bool TryParseConfigured(string? lngText, string? latText, out double lng, out double lat) =>
        TryParsePair(lngText, latText, out lng, out lat, out _);
}
