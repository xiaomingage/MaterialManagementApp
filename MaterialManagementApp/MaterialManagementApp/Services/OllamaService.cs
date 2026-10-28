using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialManagementApp.Services;

/// <summary>
/// 本地 AI（Ollama）客户端。仅访问用户自行配置的本机服务，不向任何云端发送数据。
/// 提供两项能力：探测连通性（/api/tags）与图片文字识别（/api/generate + images）。
/// </summary>
public sealed class OllamaService
{
    /// <summary>单例。HttpClient 为长生命周期对象，全应用共用一个实例。</summary>
    private static readonly Lazy<OllamaService> _instance = new(() => new OllamaService());

    /// <summary>
    /// 共用的 HttpClient。本地大模型首包与推理耗时较长，
    /// 因此超时放宽到 10 分钟，避免小图识别被误判为超时。
    /// </summary>
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private OllamaService()
    {
    }

    /// <summary>全局唯一实例。</summary>
    public static OllamaService Instance => _instance.Value;

    /// <summary>
    /// 视觉识别提示词。要求模型只回吐单据文字原文，因为后续字段抽取仍走既有的正则规则，
    /// 让模型额外解释或改写成表格反而会干扰抽取。
    /// </summary>
    private const string VisionPrompt =
        "你是一个 OCR 文字识别引擎。请识别这张单据图片中的所有可见文字，" +
        "按原始排版逐行输出，保留「字段名：值」的对应关系与换行。" +
        "只输出识别到的文字本身，不要添加任何解释、标题或多余说明。";

    /// <summary>
    /// 探测 Ollama 是否连通，并返回可用模型列表。
    /// </summary>
    /// <param name="baseUrl">服务地址，如 http://localhost:11434。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可用模型名称列表。</returns>
    /// <exception cref="InvalidOperationException">地址为空或服务不可达时抛出，异常消息可直接展示给用户。</exception>
    public async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var url = BuildEndpoint(baseUrl, "/api/tags");

        string json;
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"服务返回 {(int)response.StatusCode} {response.ReasonPhrase}，请确认该地址是 Ollama 服务。");
            }

            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"无法连接 Ollama（{baseUrl}），请确认服务已启动：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"连接 Ollama（{baseUrl}）超时，请确认服务已启动。", ex);
        }

        var models = new List<string>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("models", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                // Ollama 的模型项字段名历史上出现过 name 与 model 两种，这里两者都兼容
                if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    models.Add(name.GetString()!);
                }
                else if (item.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                {
                    models.Add(model.GetString()!);
                }
            }
        }

        return models;
    }

    /// <summary>
    /// 调用本地视觉模型识别图片中的文字。
    /// </summary>
    /// <param name="baseUrl">Ollama 服务地址。</param>
    /// <param name="model">模型名称，必须是支持图片输入的视觉模型。</param>
    /// <param name="imageBytes">图片原始字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>模型返回的识别全文。</returns>
    /// <exception cref="InvalidOperationException">参数缺失、请求失败或返回体无法解析时抛出。</exception>
    public async Task<string> RecognizeImageAsync(
        string baseUrl,
        string model,
        byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            throw new InvalidOperationException("图片内容为空，无法识别。");
        }

        var url = BuildEndpoint(baseUrl, "/api/generate");

        var payload = new
        {
            model,
            prompt = VisionPrompt,
            images = new[] { Convert.ToBase64String(imageBytes) },
            // stream=false：一次性返回完整结果，便于直接取 response 字段
            stream = false
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        string json;
        try
        {
            using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Ollama 失败时会返回 {"error":"..."}，把该消息透出比只报状态码更有诊断价值
                var detail = TryReadError(json) ?? $"{response.ReasonPhrase}";
                throw new InvalidOperationException($"本地 AI 识别失败（{(int)response.StatusCode}）：{detail}");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"调用本地 AI 失败：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("本地 AI 识别超时，请检查模型是否已加载或改用更小的模型。", ex);
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("response", out var responseText) && responseText.ValueKind == JsonValueKind.String)
        {
            return responseText.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("本地 AI 返回内容格式无法解析。");
    }

    /// <summary>把用户填写的地址与接口路径拼接成完整 URL，兼容带/不带结尾斜杠的写法。</summary>
    /// <param name="baseUrl">用户填写的服务地址。</param>
    /// <param name="path">接口路径，必须以 / 开头。</param>
    /// <returns>完整请求地址。</returns>
    private static string BuildEndpoint(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Ollama 服务地址为空，请先在设置中填写。");
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 用户常只填 localhost:11434，这里补全协议头，避免 HttpClient 直接抛格式异常
            trimmed = "http://" + trimmed;
        }

        return trimmed + path;
    }

    /// <summary>尝试从 Ollama 的错误响应体中取出 error 字段。</summary>
    /// <param name="json">响应体文本。</param>
    /// <returns>错误描述；解析失败返回 null。</returns>
    private static string? TryReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应（如反向代理返回的 HTML）无需处理，返回 null 由调用方用状态码描述
        }

        return null;
    }
}
