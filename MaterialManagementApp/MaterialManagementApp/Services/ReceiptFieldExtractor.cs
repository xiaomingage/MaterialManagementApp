using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>
/// 从识别全文中抽取出的一个单据字段（标签 + 值）。
/// 用普通可写属性而非 record 的 init-only 属性：XAML 类型信息生成器会为该类型生成属性设置代码，
/// init-only 属性无法在反射设置场景下赋值，会导致编译报错 CS8852。
/// </summary>
public sealed class ReceiptField
{
    /// <summary>字段中文名，如「物资名称」。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>抽取到的值；未识别到时为 <see cref="ReceiptFieldExtractor.NotFound"/>。</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>构造一个字段。</summary>
    /// <param name="label">字段中文名。</param>
    /// <param name="value">抽取到的值。</param>
    public ReceiptField(string label, string value)
    {
        Label = label;
        Value = value;
    }
}

/// <summary>
/// 地材单专用 OCR 字段抽取器。
///
/// 按用户提供的真实地材单排版（茂名信宜项目地材单）设计，字段格式一律为「标签: 值」：
///     NO. 0019824
///     车牌: 粤K61896  地址: 北界平周屋
///     类别: 砂   数量: 13方
///     时间: 8时 25分
///     签名: 徐十益
///     日期: 2026年 10月 2日
///
/// 关键设计：
///   1. **类别 = 物资名称**，从「类别: XXX」行取值；同时处理 OCR 常见错别字；
///   2. **单位优先取 OCR 识别结果**（数量字段的后缀如「13方」「33.5吨」），
///      识别不到才按类别推断（水泥→吨、砂/石粉/碎石/石子→方、渣土/渣土车→辆）；
///   3. **单价 / 金额 / 供应商 不识别**，留给合同匹配或人工填写；
///   4. **时间字段**（X时Y分）和 **日期字段** 分开提取，组合成进场日期 yyyy-MM-dd HH:mm。
/// </summary>
public static class ReceiptFieldExtractor
{
    /// <summary>字段未识别到时显示的占位文本。</summary>
    public const string NotFound = "未识别";

    /// <summary>抽取器日志，记录每次抽取的命中情况，便于持续调优识别规则。</summary>
    private static readonly ILogger Logger = AppLog.CreateLogger(typeof(ReceiptFieldExtractor));

    // ---------------------------------------------------------------------
    // 类别 → 单位 推断表（OCR 识别不到单位时才用）
    // 关键词按优先级排序：具体词在前，通用词在后，避免宽泛词抢命中
    // ---------------------------------------------------------------------

    /// <summary>
    /// 地材类别及其推断单位。关键词覆盖常见叫法 + OCR 错别字。
    /// </summary>
    private static readonly (string[] Keywords, string Unit, string CanonicalName)[] CategoryUnits =
    {
        // 水泥 → 吨
        (new[] { "水泥", "p.o", "po", "水泥袋", "袋装水泥" }, "吨", "水泥"),
        // 砂 → 方
        (new[] { "河砂", "机制砂", "细砂", "中砂", "粗砂", "砂子", "沙", "砂", "沙子" }, "方", "砂"),
        // 石子 / 碎石 → 方（地材单里"石子"很常见）
        (new[] { "碎石", "石子", "石于", "石籽", "石仔", "骨料", "碎渣", "石渣" }, "方", "石子"),
        // 石粉 → 方
        (new[] { "石粉", "石屑", "石份" }, "方", "石粉"),
        // 渣土车 / 土方车 → 辆
        (new[] { "渣土车", "土方车", "自卸车", "运输车" }, "辆", "渣土车"),
        // 渣土 / 土方 / 回填土 → 辆（默认按车次计数）
        (new[] { "渣土", "土方", "回填土", "挖运" }, "辆", "渣土"),
        // 其他方量（兜底） → 方
        (new[] { "方量", "立方", "m3", "m³" }, "方", "方量")
    };

    // ---------------------------------------------------------------------
    // 正则（按真实地材单格式重新设计）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 数量+单位一起抓，如「数量: 13方」「数量: 33.5吨」。
    /// 捕获组 1 = 数量数字，捕获组 2 = 单位后缀（方/吨/车/辆...）。
    /// 同时兼容标签写法：数量 / 重量 / 方量 / 净重 / 车次。
    /// </summary>
    private static readonly Regex QuantityLabelRegex = new(
        @"(?:数量|重量|方量|净重|车次)\s*[:：]?\s*(-?\d+(?:\.\d+)?)\s*(方|立方|m3|m³|吨|t|车|辆|袋|件)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>仅抓数量数字（没有标签时的退化场景），要求后面必须跟单位后缀。</summary>
    private static readonly Regex BareQuantityRegex = new(
        @"(?<![0-9])(\d+(?:\.\d+)?)\s*(方|立方|m3|m³|吨|t|车|辆|袋|件)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>单据编号：NO / No / 编号 / 单号 / 单据号 / 单据编号。</summary>
    private static readonly Regex DocNoRegex = new(
        @"(?:单据编号|单据号|单号|编号|[Nn][Oo])\s*[.:：]?\s*([A-Za-z0-9\-]{3,20})",
        RegexOptions.Compiled);

    /// <summary>中国大陆车牌号（OCR 常见漏字/多字用宽松写法）。</summary>
    private static readonly Regex PlateRegex = new(
        @"([京津沪渝冀豫云辽黑湘皖鲁新苏浙赣鄂桂甘晋蒙陕吉闽贵粤青藏川宁琼使领][A-Z][A-Z0-9]{4,6})",
        RegexOptions.Compiled);

    /// <summary>日期：yyyy年M月D日 / yyyy-M-D / yyyy/M/D。</summary>
    private static readonly Regex DateRegex = new(
        @"(\d{4})\s*[-/年.]\s*(\d{1,2})\s*[-/月.]\s*(\d{1,2})",
        RegexOptions.Compiled);

    /// <summary>时间：X时Y分 / HH:MM / X点Y分。</summary>
    private static readonly Regex TimeRegex = new(
        @"(\d{1,2})\s*(?:时|点|:)\s*(\d{1,2})\s*分?",
        RegexOptions.Compiled);

    // ---------------------------------------------------------------------
    // 主入口
    // ---------------------------------------------------------------------

    /// <summary>
    /// 从识别全文中抽取各业务字段。返回顺序即界面展示顺序，未识别到的字段值为 <see cref="NotFound"/>。
    /// </summary>
    /// <param name="text">OCR 识别全文。</param>
    /// <returns>字段列表；<paramref name="text"/> 为空时所有字段均为未识别。</returns>
    public static IReadOnlyList<ReceiptField> Extract(string text)
    {
        var source = text ?? string.Empty;
        var fields = new List<ReceiptField>(11);

        // ---- 物资名称 + 类别 + 单位 ----
        // 先从「类别: XXX」标签行取值；取不到再走全文字典扫描（兼容没有标签的老单据）
        var category = ExtractCategoryLabel(source);
        var (canonicalCategory, inferredUnit) = NormalizeCategory(category);

        // ---- 数量 + 单位后缀 ----
        var (quantity, unitFromOcr) = ExtractQuantityWithUnit(source);

        // 单位：优先 OCR 识别的后缀；没有才用类别推断
        var unit = !string.IsNullOrWhiteSpace(unitFromOcr) ? unitFromOcr : inferredUnit;

        Add(fields, "物资名称", canonicalCategory);
        // 规格：地材单没有"规格"概念，直接留空
        Add(fields, "规格", string.Empty);
        Add(fields, "数量", quantity);
        Add(fields, "单位", unit);

        // ---- 单价 / 金额 / 供应商：不识别，留空给合同匹配 ----
        Add(fields, "单价", string.Empty);
        Add(fields, "金额", string.Empty);
        Add(fields, "供应商", string.Empty);

        // ---- 单据编号 ----
        Add(fields, "单据编号", MatchValue(source, DocNoRegex));

        // ---- 进场日期 = 日期 + 时间 组合 ----
        Add(fields, "进场日期", ExtractEntryDateTime(source));

        // ---- 车牌号 ----
        Add(fields, "车牌号", MatchValue(source, PlateRegex));

        // ---- 经办人 = 地材单的"签名"栏目 ----
        Add(fields, "经办人", ExtractByKeyword(source, "签名", "签字", "经办人", "收货人", "签收人", "验收人"));

        // 记录一次抽取结果，便于后续核对 OCR 准确率与调整规则
        var recognized = fields
            .Where(f => !string.Equals(f.Value, NotFound, StringComparison.Ordinal))
            .Select(f => $"{f.Label}={f.Value}");
        Logger.LogInformation("地材单字段抽取完成，成功 {Hit}/{Total} 项：{Fields}。",
            fields.Count(f => !string.Equals(f.Value, NotFound, StringComparison.Ordinal)),
            fields.Count,
            string.Join("，", recognized));

        return fields;
    }

    // ---------------------------------------------------------------------
    // 抽取辅助
    // ---------------------------------------------------------------------

    /// <summary>从「类别: XXX」标签行取值。找不到返回空串。</summary>
    private static string ExtractCategoryLabel(string text)
    {
        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // 标签必须在行首附近（前 6 字内），且后跟分隔符
            var idx = line.IndexOf("类别", StringComparison.Ordinal);
            if (idx < 0 || idx > 6) continue;

            var rest = line[(idx + "类别".Length)..];
            var sepIdx = IndexOfSeparator(rest);
            if (sepIdx < 0) continue;

            var value = rest[(sepIdx + 1)..].Trim();
            if (value.Length == 0) continue;

            // 类别值后面通常跟着空格+数量字段（"类别: 砂   数量: 13方"），
            // 截断到遇到下一个标签之前
            var truncated = TruncateAtNextField(value);
            return truncated;
        }

        return string.Empty;
    }

    /// <summary>
    /// 把 OCR 识别到的原始类别值规范化为标准类别名，并返回推断单位。
    /// 如果原始值本身就是标准名或包含标准关键词，直接返回；否则返回 (原始值, "").
    /// </summary>
    private static (string Canonical, string Unit) NormalizeCategory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, string.Empty);
        }

        // 去空格小写化
        var normalized = raw.Trim().ToLowerInvariant();

        foreach (var (keywords, unit, canonical) in CategoryUnits)
        {
            foreach (var kw in keywords)
            {
                // 精确匹配或包含匹配（"p.o42.5" 包含 "p.o"）
                if (normalized.Equals(kw, StringComparison.Ordinal)
                    || normalized.Contains(kw, StringComparison.Ordinal))
                {
                    return (canonical, unit);
                }
            }
        }

        // 没命中任何关键词，原样返回，推断单位留空
        return (raw.Trim(), string.Empty);
    }

    /// <summary>
    /// 抽取数量和单位后缀，标签优先，退化到带后缀的纯数字。
    /// 返回 (数量文本, 单位后缀)；都取不到返回 ("", "").
    /// </summary>
    private static (string Quantity, string Unit) ExtractQuantityWithUnit(string text)
    {
        var labeled = QuantityLabelRegex.Match(text);
        if (labeled.Success)
        {
            return (labeled.Groups[1].Value, labeled.Groups[2].Value);
        }

        var bare = BareQuantityRegex.Match(text);
        return bare.Success ? (bare.Groups[1].Value, bare.Groups[2].Value) : (string.Empty, string.Empty);
    }

    /// <summary>
    /// 组合进场日期：从全文找日期和时间，拼成 yyyy-MM-dd HH:mm。
    /// 只有日期时返回 yyyy-MM-dd，都没有返回空串。
    /// </summary>
    private static string ExtractEntryDateTime(string text)
    {
        var dateMatch = DateRegex.Match(text);
        var timeMatch = TimeRegex.Match(text);

        string date = dateMatch.Success
            ? $"{dateMatch.Groups[1].Value}-{int.Parse(dateMatch.Groups[2].Value):D2}-{int.Parse(dateMatch.Groups[3].Value):D2}"
            : string.Empty;

        string time = timeMatch.Success
            ? $"{int.Parse(timeMatch.Groups[1].Value):D2}:{int.Parse(timeMatch.Groups[2].Value):D2}"
            : string.Empty;

        if (date.Length == 0 && time.Length == 0) return string.Empty;
        if (date.Length == 0) return time;
        if (time.Length == 0) return date;
        return $"{date} {time}";
    }

    /// <summary>按正则取值，取第一个捕获组；未匹配返回空串。</summary>
    private static string MatchValue(string text, Regex regex)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>按关键词逐行查找字段值。</summary>
    private static string ExtractByKeyword(string text, params string[] keywords)
    {
        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            foreach (var keyword in keywords)
            {
                var index = line.IndexOf(keyword, StringComparison.Ordinal);
                if (index < 0 || index > 6) continue;

                var rest = line[(index + keyword.Length)..];
                var sepIdx = IndexOfSeparator(rest);
                if (sepIdx < 0) continue;

                var value = rest[(sepIdx + 1)..].Trim();
                if (value.Length == 0 || value.Length > 40) continue;
                if (ContainsTableHeaderNoise(value)) continue;

                // 截断到遇到下一个标签之前
                return TruncateAtNextField(value);
            }
        }

        return string.Empty;
    }

    // ---------------------------------------------------------------------
    // 共享工具
    // ---------------------------------------------------------------------

    private static void Add(List<ReceiptField> fields, string label, string value)
    {
        fields.Add(new ReceiptField(label, string.IsNullOrWhiteSpace(value) ? NotFound : value.Trim()));
    }

    /// <summary>在字符串开头找分隔符位置，返回索引（-1 表示没有）。</summary>
    private static int IndexOfSeparator(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]) || s[i] is ':' or '：' or ';' or '；' or '-' or '—' or '、')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>值里出现表头关键词说明命中了表头行而非数据行，跳过。</summary>
    private static bool ContainsTableHeaderNoise(string value)
    {
        foreach (var kw in new[] { "规格", "数量", "单位", "单价", "金额", "备注", "序号", "类别", "名称", "日期", "时间", "签名" })
        {
            if (value.Contains(kw, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// 地材单一行里经常有多个字段（如「车牌: XXX 地址: YYY」「类别: 砂 数量: 13方」）。
    /// 当前面某个字段值取到之后，要截断到遇到下一个已知字段标签之前，避免把后续字段值吃进来。
    /// </summary>
    private static string TruncateAtNextField(string value)
    {
        // 已知的下一个字段标签（按长度降序排，避免短词抢先命中长词的一部分）
        var nextLabels = new[]
        {
            "单据编号", "车牌号", "车牌", "地址", "类别", "规格", "数量",
            "单位", "单价", "金额", "供应商", "经办人", "签名", "签字",
            "日期", "时间", "NO", "No", "no"
        };

        int cutAt = value.Length;
        foreach (var label in nextLabels)
        {
            var idx = value.IndexOf(label, StringComparison.Ordinal);
            if (idx > 0 && idx < cutAt)
            {
                cutAt = idx;
            }
        }

        return value[..cutAt].Trim();
    }
}
