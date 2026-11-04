using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialManagementApp.Services;

/// <summary>
/// 在线 AI（OpenAI 兼容协议）视觉识别客户端，支持豆包（火山方舟）、通义千问、Kimi 等服务商。
/// 与传统「OCR 出全文 → 正则抽字段」的两步走不同，视觉大模型直接看图抽取字段，
/// 按约定的 JSON 协议返回结构化结果与整理后的原文，识别准确率显著更高。
/// </summary>
public sealed class OnlineAiService
{
    /// <summary>单例。HttpClient 为长生命周期对象，全应用共用一个实例。</summary>
    private static readonly Lazy<OnlineAiService> _instance = new(() => new OnlineAiService());

    /// <summary>
    /// 共用的 HttpClient。视觉大模型推理耗时可能在分钟级，超时放宽到 5 分钟，
    /// 避免大图识别被误判为超时。
    /// </summary>
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private OnlineAiService()
    {
    }

    /// <summary>全局唯一实例。</summary>
    public static OnlineAiService Instance => _instance.Value;

    /// <summary>
    /// 视觉识别提示词。要求模型直接按固定 JSON 协议输出结构化字段，
    /// 由 <see cref="ReceiptFieldExtractor.ParseAiStructured"/> 解析；
    /// 解析失败时窗口会把全文交给既有正则抽取兜底。
    /// </summary>
    private const string VisionPrompt =
        "你是工程地材单据识别引擎。请仔细识别这张单据图片中的全部文字，并抽取以下字段。\n" +
        "严格只输出一个 JSON 对象，禁止输出 JSON 以外的任何解释或 markdown 代码块标记，格式：\n" +
        "{\"fields\":{\"物资名称\":\"\",\"规格\":\"\",\"数量\":\"\",\"单位\":\"\",\"进场日期\":\"\",\"单据编号\":\"\",\"供应商\":\"\",\"经办人\":\"\",\"车牌号\":\"\"},\"raw\":\"按单据原版式逐行整理的全部文字，换行用\\n表示\"}\n" +
        "抽取规则：\n" +
        "1. 数量必须包含数字（如 13、33.5）；计量单位单独放「单位」字段（如 方、吨、车、辆）；数字与单位同时出现时拆开填（如「13方」→ 数量 13，单位 方）。\n" +
        "2. 进场日期统一格式化为 yyyy-MM-dd HH:mm（如 2026-10-02 08:25）；只有日期则只写日期。\n" +
        "3. 「类别」即物资名称（如 砂、石子、水泥）。「签名」即经办人。\n" +
        "4. 单据上没有的字段一律填空字符串，禁止编造内容；车牌号保持原样（如 粤K61896）。\n" +
        "5. raw 字段输出识别到的原文，便于人工核对。";

    /// <summary>
    /// 调用在线视觉模型识别图片中的文字并抽取字段。
    /// </summary>
    /// <param name="baseUrl">服务地址（如 https://ark.cn-beijing.volces.com/api/v3）。</param>
    /// <param name="apiKey">API Key。</param>
    /// <param name="model">模型名称（须为支持图片输入的视觉模型，也兼容火山方舟的 ep-xxx 接入点）。</param>
    /// <param name="imageBytes">图片原始字节（jpg/png 等均可）。</param>
    /// <param name="disableThinking">是否携带「关闭深度思考」参数（豆包 seed 系列需要，思考内容会混入输出污染 JSON 解析；其他服务商传 false）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>模型返回的识别结果全文（约定的 JSON 协议文本）。</returns>
    /// <exception cref="InvalidOperationException">参数缺失、请求失败或返回体无法解析时抛出，消息可直接展示给用户。</exception>
    public async Task<string> RecognizeImageAsync(
        string baseUrl,
        string apiKey,
        string model,
        byte[] imageBytes,
        bool disableThinking = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("在线 AI 服务地址为空，请先在设置中填写。");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("在线 AI 的 API Key 为空，请先在设置中填写。");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("在线 AI 模型名称为空，请先在设置中填写（须为支持识图的视觉模型）。");
        }

        if (imageBytes is null || imageBytes.Length == 0)
        {
            throw new InvalidOperationException("图片内容为空，无法识别。");
        }

        var url = BuildEndpoint(baseUrl, "/chat/completions");

        // 用 JsonObject 构建 payload：豆包需要附加 thinking 参数关闭深度思考，
        // 其他服务商不携带该字段（未知字段虽多数会被忽略，但显式不传最稳）
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = model,
            ["messages"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = VisionPrompt
                        },
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["url"] = $"data:{DetectImageMime(imageBytes)};base64,{Convert.ToBase64String(imageBytes)}"
                            }
                        }
                    }
                }
            },
            // 温度取 0：识别任务要的是确定性输出，越低越稳
            ["temperature"] = 0.0,
            ["stream"] = false
        };

        if (disableThinking)
        {
            payload["thinking"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "disabled"
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        string json;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = TryReadError(json) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                throw new InvalidOperationException($"在线 AI 识别失败：{detail}");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"无法连接在线 AI 服务：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("在线 AI 识别超时，请检查网络后重试。", ex);
        }

        // OpenAI 兼容协议：choices[0].message.content；部分服务会返回数组片段，此处仅处理纯文本
        return ExtractChatContent(json);
    }

    /// <summary>
    /// 用自定义提示词调用在线视觉模型识别多张图片（OpenAI 兼容协议）。
    /// 与 <see cref="RecognizeImageAsync"/> 的区别：提示词由调用方指定、支持一次发送多张图片，
    /// 供合同 PDF 多页解析等场景复用；响应结构与错误处理保持一致。
    /// </summary>
    /// <param name="baseUrl">服务地址（如 https://ark.cn-beijing.volces.com/api/v3）。</param>
    /// <param name="apiKey">API Key。</param>
    /// <param name="model">模型名称（须为支持图片输入的视觉模型，也兼容火山方舟的 ep-xxx 接入点）。</param>
    /// <param name="images">图片原始字节列表（按页序排列，jpg/png 均可）。</param>
    /// <param name="prompt">调用方自定义的提示词（约定模型输出严格 JSON）。</param>
    /// <param name="disableThinking">是否携带「关闭深度思考」参数（豆包 seed 系列需要；其他服务商传 false）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>模型返回的全文文本。</returns>
    /// <exception cref="InvalidOperationException">参数缺失、请求失败或返回体无法解析时抛出，消息可直接展示给用户。</exception>
    public async Task<string> ExtractAsync(
        string baseUrl,
        string apiKey,
        string model,
        IReadOnlyList<byte[]> images,
        string prompt,
        bool disableThinking = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("在线 AI 服务地址为空，请先在设置中填写。");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("在线 AI 的 API Key 为空，请先在设置中填写。");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("在线 AI 模型名称为空，请先在设置中填写（须为支持识图的视觉模型）。");
        }

        if (images is null || images.Count == 0 || images.Any(i => i is null || i.Length == 0))
        {
            throw new InvalidOperationException("图片内容为空，无法识别。");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("识别提示词为空，无法调用在线 AI。");
        }

        var url = BuildEndpoint(baseUrl, "/chat/completions");

        // content = 1 个文本节点 + N 个图片节点（按页序），模型会综合多页内容作答
        var content = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "text",
                ["text"] = prompt
            }
        };
        foreach (var image in images)
        {
            content.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["url"] = $"data:{DetectImageMime(image)};base64,{Convert.ToBase64String(image)}"
                }
            });
        }

        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = model,
            ["messages"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            },
            // 温度取 0：识别任务要的是确定性输出，越低越稳
            ["temperature"] = 0.0,
            ["stream"] = false
        };

        if (disableThinking)
        {
            payload["thinking"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "disabled"
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        string json;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = TryReadError(json) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                throw new InvalidOperationException($"在线 AI 请求失败：{detail}");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"无法连接在线 AI 服务：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("在线 AI 请求超时，请检查网络后重试。", ex);
        }

        return ExtractChatContent(json);
    }

    /// <summary>
    /// 从 chat/completions 响应体提取 choices[0].message.content 的文本。
    /// 部分兼容服务会把 content 拆成多段数组，这里把文本片段拼接回来。
    /// </summary>
    /// <param name="json">响应体 JSON 文本。</param>
    /// <returns>模型输出的全文。</returns>
    /// <exception cref="InvalidOperationException">响应缺少关键字段或不是合法 JSON 时抛出。</exception>
    private static string ExtractChatContent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString() ?? string.Empty;
                }

                // 部分兼容服务把 content 拆成多段，这里把文本片段拼接回来
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var builder = new StringBuilder();
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.String)
                        {
                            builder.Append(part.GetString());
                        }
                        else if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            builder.Append(text.GetString());
                        }
                    }

                    return builder.ToString();
                }
            }

            throw new InvalidOperationException("在线 AI 返回内容缺少 choices[0].message.content 字段，无法解析。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("在线 AI 返回内容格式无法解析。", ex);
        }
    }

    /// <summary>把用户填写的地址与接口路径拼接成完整 URL，兼容带/不带结尾斜杠与 v1 子路径的写法。</summary>
    /// <param name="baseUrl">用户填写的服务地址。</param>
    /// <param name="path">接口路径，必须以 / 开头。</param>
    /// <returns>完整请求地址。</returns>
    private static string BuildEndpoint(string baseUrl, string path)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed + path;
    }

    /// <summary>按文件魔数判断图片 MIME 类型；非 JPEG/PNG 一律按 JPEG 处理（服务端会自行嗅探）。</summary>
    /// <param name="bytes">图片原始字节。</param>
    /// <returns>MIME 类型字符串。</returns>
    private static string DetectImageMime(byte[] bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }

            if (bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                return "image/jpeg";
            }
        }

        return "image/jpeg";
    }

    /// <summary>尝试从服务商的错误响应体中提取可读的错误描述。</summary>
    /// <param name="json">响应体文本。</param>
    /// <returns>错误描述；解析失败返回 null。</returns>
    private static string? TryReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应（如网关返回的 HTML）无需处理，返回 null 由调用方用状态码描述
        }

        return null;
    }
}
