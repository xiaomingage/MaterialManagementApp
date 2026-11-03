using System;
using System.Threading;
using System.Threading.Tasks;
using MaterialManagementApp.Models;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.Services;

/// <summary>一次识别的结果：识别全文、实际使用的引擎名、回退原因（未回退为 null）。</summary>
public sealed record OcrEngineResult(string Text, string EngineName, string? FallbackNote);

/// <summary>
/// OCR 引擎统一路由。两个识别窗口（单张导入、批量扫描）都从这里取识别结果，
/// 按设置页的引擎选择依次尝试：在线 AI（豆包等视觉大模型）→ 本地 Ollama → 离线 PaddleOCR。
/// 配置的引擎失败（断网、密钥无效、服务未启动等）时自动回退离线识别并在结果中注明原因，
/// 保证识别流程永不中断。
/// </summary>
public static class OcrEngineRouter
{
    /// <summary>路由日志，记录每次实际使用的引擎与回退原因，便于排查识别质量问题。</summary>
    private static readonly ILogger Logger = AppLog.CreateLogger(typeof(OcrEngineRouter));

    /// <summary>
    /// 按当前设置执行一次单据识别。
    /// </summary>
    /// <param name="imageBytes">图片原始字节（在线 AI / Ollama 视觉识别必需；为空时跳过 AI 引擎直接离线识别）。</param>
    /// <param name="bgra">解码后的 BGRA32 像素（离线 PaddleOCR 必需）。</param>
    /// <param name="width">图片宽度（像素）。</param>
    /// <param name="height">图片高度（像素）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>识别结果，包含引擎名与回退说明。</returns>
    public static async Task<OcrEngineResult> RecognizeAsync(
        byte[]? imageBytes,
        byte[] bgra,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        var settings = SettingsService.Instance.Settings;
        var mode = settings.OcrEngineMode ?? string.Empty;
        var hasImage = imageBytes is { Length: > 0 };

        // ---- 在线 AI 档（豆包 / 通义 / Kimi 等 OpenAI 兼容视觉大模型；服务商选 Ollama 时走本地模型）----
        if (string.Equals(mode, "OnlineAi", StringComparison.OrdinalIgnoreCase) && settings.IsAiConfigured)
        {
            if (settings.IsOllamaProvider)
            {
                if (hasImage)
                {
                    var (ollamaHit, ollamaError) = await TryOllamaAsync(settings, imageBytes!, cancellationToken).ConfigureAwait(false);
                    if (ollamaHit is not null)
                    {
                        return ollamaHit;
                    }

                    return await RecognizePaddleAsync(bgra, width, height, cancellationToken,
                        $"本地 AI 识别失败（{ollamaError}），已自动改用离线识别。")
                        .ConfigureAwait(false);
                }
            }
            else if (hasImage)
            {
                try
                {
                    var text = await OnlineAiService.Instance.RecognizeImageAsync(
                        settings.AiBaseUrl, settings.AiApiKey, settings.AiModelName, imageBytes!, cancellationToken)
                        .ConfigureAwait(false);

                    Logger.LogInformation("在线 AI 识别完成，服务商={Provider}，模型={Model}。", settings.AiProvider, settings.AiModelName);
                    return new OcrEngineResult(text, $"在线 AI（{settings.AiProvider}）", null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "在线 AI 识别失败，将回退离线识别。服务商={Provider}。", settings.AiProvider);
                    return await RecognizePaddleAsync(bgra, width, height, cancellationToken,
                        $"在线 AI 识别失败（{ex.Message}），已自动改用离线识别。")
                        .ConfigureAwait(false);
                }
            }
        }

        // ---- 本地 Ollama 档（引擎选了本地离线且具体引擎为 Ollama）----
        if (string.Equals(mode, "LocalOllama", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(settings.OllamaUrl)
            && !string.IsNullOrWhiteSpace(settings.OllamaModel)
            && hasImage)
        {
            var (localHit, localError) = await TryOllamaAsync(settings, imageBytes!, cancellationToken).ConfigureAwait(false);
            if (localHit is not null)
            {
                return localHit;
            }

            return await RecognizePaddleAsync(bgra, width, height, cancellationToken,
                $"本地 AI 识别失败（{localError}），已自动改用离线识别。")
                .ConfigureAwait(false);
        }

        // ---- 离线 PaddleOCR（兜底）----
        return await RecognizePaddleAsync(bgra, width, height, cancellationToken, null).ConfigureAwait(false);
    }

    /// <summary>
    /// 尝试用本地 Ollama 视觉模型识别。
    /// </summary>
    /// <param name="settings">应用设置（读取 Ollama 地址与模型名）。</param>
    /// <param name="imageBytes">图片原始字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>(识别结果, 失败原因)；成功时结果非 null，失败时原因为非空描述。</returns>
    private static async Task<(OcrEngineResult? Result, string? Error)> TryOllamaAsync(
        AppSettings settings, byte[] imageBytes, CancellationToken cancellationToken)
    {
        try
        {
            var text = await OllamaService.Instance.RecognizeImageAsync(
                settings.OllamaUrl, settings.OllamaModel, imageBytes, cancellationToken).ConfigureAwait(false);

            Logger.LogInformation("本地 Ollama 识别完成，模型={Model}。", settings.OllamaModel);
            return (new OcrEngineResult(text, $"本地 AI（{settings.OllamaModel}）", null), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "本地 Ollama 识别失败，将回退离线识别。地址={Url}。", settings.OllamaUrl);
            return (null, ex.Message);
        }
    }

    /// <summary>执行离线 PaddleOCR 识别并包装结果；note 非空时作为回退说明一并返回。</summary>
    private static async Task<OcrEngineResult> RecognizePaddleAsync(
        byte[] bgra, int width, int height, CancellationToken cancellationToken, string? note)
    {
        var text = await PaddleOcrService.Instance.RecognizeAsync(bgra, width, height, cancellationToken).ConfigureAwait(false);
        return new OcrEngineResult(text, "离线识别（PaddleOCR）", note);
    }
}
