using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MaterialManagementApp.Services;

/// <summary>
/// 供应商合同 PDF 解析器：把合同 PDF 的前几页渲染成高分辨率图片，
/// 交给在线视觉大模型提取「主表信息 + 材料明细」，返回强类型的解析结果。
/// 供供应商列表页「AI 解析合同 PDF」按钮调用，结果回显到界面由用户核对编辑后手动保存。
/// </summary>
public static class ContractPdfParser
{
    private static readonly ILogger _logger = AppLog.CreateLogger(typeof(ContractPdfParser));

    /// <summary>最多发送给视觉模型的页数：合同关键信息与材料明细通常在前 3 页内。</summary>
    private const int MaxPages = 3;

    /// <summary>渲染图片的目标宽度（像素）。A4 页面原始约 816px，放大到 2000px 保证小字清晰可识别。</summary>
    private const int RenderWidth = 2000;

    /// <summary>
    /// 解析指定合同 PDF：渲染前 <see cref="MaxPages"/> 页为 PNG，调用在线视觉大模型提取结构化信息。
    /// </summary>
    /// <param name="pdfAbsolutePath">合同 PDF 的绝对路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析出的主表信息与材料明细（已过滤无效行）。</returns>
    /// <exception cref="InvalidOperationException">PDF 打不开、AI 未配置、请求失败或返回内容无法解析时抛出，消息可直接展示给用户。</exception>
    public static async Task<ContractParseResult> ParseContractAsync(string pdfAbsolutePath, CancellationToken cancellationToken = default)
    {
        var settings = SettingsService.Instance.Settings;
        if (!settings.IsAiConfigured)
        {
            throw new InvalidOperationException("尚未配置在线 AI，请先到「设置」页填写服务地址、API Key 与模型名称。");
        }

        _logger.LogInformation("开始解析合同 PDF：渲染前 {MaxPages} 页。", MaxPages);
        var images = await RenderPdfPagesAsync(pdfAbsolutePath, MaxPages).ConfigureAwait(false);
        _logger.LogInformation("合同 PDF 渲染完成，共 {Count} 页图片，开始调用视觉模型。", images.Count);

        var raw = await OnlineAiService.Instance.ExtractAsync(
            settings.AiBaseUrl,
            settings.AiApiKey,
            settings.AiModelName,
            images,
            BuildPrompt(images.Count),
            // 豆包 seed 系列默认可能输出深度思考内容，混入正文会破坏 JSON 解析，需显式关闭
            disableThinking: string.Equals(settings.AiProvider, "豆包", StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);

        var result = ParseAiJson(raw);
        _logger.LogInformation(
            "合同解析完成：供应商={Supplier}，合同号={ContractNo}，材料 {Count} 条。",
            result.SupplierName, result.ContractNo, result.Materials.Count);
        return result;
    }

    /// <summary>
    /// 把 PDF 的前 <paramref name="maxPages"/> 页渲染为高分辨率 PNG 字节数组。
    /// 使用系统自带 Windows.Data.Pdf（PDF 渲染引擎），无需引入第三方依赖。
    /// </summary>
    /// <param name="pdfAbsolutePath">PDF 文件绝对路径。</param>
    /// <param name="maxPages">最多渲染的页数。</param>
    /// <returns>按页序排列的 PNG 图片字节数组。</returns>
    /// <exception cref="InvalidOperationException">文件不存在、PDF 损坏或设有密码保护时抛出。</exception>
    public static async Task<IReadOnlyList<byte[]>> RenderPdfPagesAsync(string pdfAbsolutePath, int maxPages)
    {
        if (string.IsNullOrWhiteSpace(pdfAbsolutePath) || !File.Exists(pdfAbsolutePath))
        {
            throw new InvalidOperationException("找不到合同 PDF 文件，请重新上传。");
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(pdfAbsolutePath).AsTask().ConfigureAwait(false);
            var document = await PdfDocument.LoadFromFileAsync(file).AsTask().ConfigureAwait(false);

            if (document.PageCount == 0)
            {
                throw new InvalidOperationException("该 PDF 文件不包含任何页面，无法识别。");
            }

            var pageCount = (uint)Math.Min((int)document.PageCount, maxPages);
            var images = new List<byte[]>((int)pageCount);

            for (uint index = 0; index < pageCount; index++)
            {
                using var page = document.GetPage(index);
                // DestinationWidth 只指定宽度，高度按页面比例自动缩放
                var options = new PdfPageRenderOptions { DestinationWidth = RenderWidth };
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options).AsTask().ConfigureAwait(false);

                images.Add(await ReadStreamBytesAsync(stream).ConfigureAwait(false));
            }

            // 本 SDK 投影的 PdfDocument 未暴露 Close/Dispose，句柄由 COM 引用计数与 GC 回收
            return images;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "渲染合同 PDF 页面失败：{Path}。", pdfAbsolutePath);
            throw new InvalidOperationException("无法打开合同 PDF 文件，文件可能已损坏或设有密码保护。", ex);
        }
    }

    /// <summary>把 WinRT 随机访问流一次性读成字节数组。</summary>
    /// <param name="stream">图片内容流。</param>
    /// <returns>流的完整字节内容。</returns>
    private static async Task<byte[]> ReadStreamBytesAsync(IRandomAccessStream stream)
    {
        var size = (uint)stream.Size;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size).AsTask().ConfigureAwait(false);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// 构造发给视觉大模型的合同解析提示词：
    /// 强制输出严格 JSON（无 Markdown 标记），缺失字段填空字符串，禁止编造。
    /// </summary>
    /// <param name="imageCount">随提示词一起发送的合同页图片数量。</param>
    /// <returns>提示词文本。</returns>
    private static string BuildPrompt(int imageCount)
    {
        return "你是一个专业的合同解析助手。下面同一份合同 PDF 的第 1 到第 " + imageCount +
               " 页已作为图片提供，请提取图片中的合同信息。\n" +
               "输出严格的 JSON 格式，不要包含 Markdown 标记或代码块。JSON 结构如下：\n" +
               "{\"supplier_name\":\"供应商名称\",\"contract_no\":\"合同编号\",\"contact_person\":\"联系人\",\"phone\":\"联系电话\",\"sign_date\":\"YYYY-MM-DD\",\"materials\":[{\"name\":\"材料名称\",\"spec\":\"规格\",\"unit\":\"单位\",\"price\":0}]}\n" +
               "抽取规则：\n" +
               "1. price 为含税单价，必须是数字（如 128.5）；材料明细优先从「材料明细 / 单价表 / 清单」等页提取，按表格逐行提取，不要遗漏。\n" +
               "2. sign_date 统一格式化为 YYYY-MM-DD（如 2026-01-01）。\n" +
               "3. 如果图片中缺失某字段，填入空字符串；materials 中没有材料明细时输出空数组 []。不要编造内容。";
    }

    /// <summary>
    /// 解析模型返回的文本为合同解析结果。
    /// 容错处理：剔除 ```json 围栏等多余标记（直接截取首个 { 到末个 }）；
    /// price 兼容数字与字符串两种输出（AllowReadingFromString）；
    /// sign_date 统一规范成 yyyy-MM-dd；过滤掉名称与单价均为空的无效材料行。
    /// </summary>
    /// <param name="raw">模型返回的原始文本。</param>
    /// <returns>强类型解析结果。</returns>
    /// <exception cref="InvalidOperationException">返回文本中找不到 JSON 结构或反序列化失败时抛出。</exception>
    public static ContractParseResult ParseAiJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("AI 未返回任何内容，无法解析合同信息。");
        }

        // 容错：模型可能输出 ```json ... ``` 或夹带解释文字，截取首个 { 到末个 } 之间内容
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("AI 返回的内容不是有效的 JSON，无法解析合同信息。");
        }

        var json = raw.Substring(start, end - start + 1);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // 模型可能把 price 输出成字符串（如 "128.5"），允许从字符串读数字
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        ContractParseDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ContractParseDto>(json, options)
                  ?? throw new InvalidOperationException("AI 返回的 JSON 内容为空，无法解析合同信息。");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "AI 返回的合同 JSON 无法反序列化：{Json}", json);
            throw new InvalidOperationException("AI 返回的 JSON 格式错误，无法解析合同信息，请重试。", ex);
        }

        // 日期统一规范成 yyyy-MM-dd；解析失败保留原文，交给用户在表格中手动修正
        var signDate = NormalizeDate(dto.SignDate);

        // 过滤无效行：材料名称为空且单价非正数的行属于模型噪声；
        // 同时与表格空白行防呆规则保持一致，避免导入后立刻被自动移除造成困惑
        var materials = dto.Materials
            .Where(m => !string.IsNullOrWhiteSpace(m.Name) || (m.Price ?? 0) > 0)
            .Select(m => new ContractMaterial(m.Name.Trim(), m.Spec.Trim(), m.Unit.Trim(), m.Price ?? 0))
            .ToList();

        return new ContractParseResult(
            dto.SupplierName.Trim(),
            dto.ContractNo.Trim(),
            dto.ContactPerson.Trim(),
            dto.Phone.Trim(),
            signDate,
            materials);
    }

    /// <summary>把模型返回的日期文本规范成 yyyy-MM-dd；无法解析时返回空串。</summary>
    /// <param name="value">原始日期文本（如 2026-01-01、2026/1/1、2026年1月1日）。</param>
    /// <returns>yyyy-MM-dd 格式日期；解析失败返回空字符串。</returns>
    private static string NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return DateTime.TryParse(value.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd")
            : string.Empty;
    }

    /// <summary>合同解析结果：主表信息 + 材料明细列表。</summary>
    /// <param name="SupplierName">供应商名称。</param>
    /// <param name="ContractNo">合同编号。</param>
    /// <param name="ContactPerson">联系人。</param>
    /// <param name="Phone">联系电话。</param>
    /// <param name="SignDate">签订日期（yyyy-MM-dd，解析失败为空）。</param>
    /// <param name="Materials">材料明细（已过滤无效行）。</param>
    public sealed record ContractParseResult(
        string SupplierName,
        string ContractNo,
        string ContactPerson,
        string Phone,
        string SignDate,
        IReadOnlyList<ContractMaterial> Materials);

    /// <summary>合同中的一项材料明细。</summary>
    /// <param name="Name">材料名称。</param>
    /// <param name="Spec">规格。</param>
    /// <param name="Unit">单位。</param>
    /// <param name="Price">含税单价。</param>
    public sealed record ContractMaterial(string Name, string Spec, string Unit, double Price);

    /// <summary>模型 JSON 输出的反序列化载体（snake_case 字段名与模型约定一致）。</summary>
    private sealed class ContractParseDto
    {
        [JsonPropertyName("supplier_name")]
        public string SupplierName { get; set; } = string.Empty;

        [JsonPropertyName("contract_no")]
        public string ContractNo { get; set; } = string.Empty;

        [JsonPropertyName("contact_person")]
        public string ContactPerson { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonPropertyName("sign_date")]
        public string SignDate { get; set; } = string.Empty;

        [JsonPropertyName("materials")]
        public List<MaterialDto> Materials { get; set; } = [];
    }

    /// <summary>模型 JSON 输出的单项材料。</summary>
    private sealed class MaterialDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("spec")]
        public string Spec { get; set; } = string.Empty;

        [JsonPropertyName("unit")]
        public string Unit { get; set; } = string.Empty;

        // 允许 null：模型对缺失单价可能输出 null 而非数字
        [JsonPropertyName("price")]
        public double? Price { get; set; }
    }
}
