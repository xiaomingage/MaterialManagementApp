using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace MaterialManagementApp.Services;

/// <summary>
/// 内嵌离线 OCR 服务：封装 Sdcb.SimdPaddleOCR（纯 C# 实现的 PP-OCRv6 推理，DET + CLS + REC）。
/// 引擎不依赖 Paddle Inference / ONNX Runtime / OpenCV 等原生库，中文模型以嵌入资源随程序集一并分发，
/// 因此安装包无需额外附带模型文件，识别全过程在本机离线完成、不联网。
/// </summary>
public sealed class PaddleOcrService
{
    /// <summary>进程内单例：模型加载开销较大，全应用共用一个引擎实例。</summary>
    private static readonly Lazy<PaddleOcrService> LazyInstance = new(() => new PaddleOcrService());

    /// <summary>获取全局 OCR 服务实例。</summary>
    public static PaddleOcrService Instance => LazyInstance.Value;

    private readonly ILogger _logger = AppLog.CreateLogger<PaddleOcrService>();

    /// <summary>保证模型只被加载一次（多个窗口同时触发识别时也只加载一份）。</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>串行化识别调用：引擎内部复用同一批推理 session，并发调用需要互斥。</summary>
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private PaddleOcrAll? _engine;

    private PaddleOcrService()
    {
    }

    /// <summary>模型是否已加载完成；首次识别会额外包含模型加载耗时。</summary>
    public bool IsEngineLoaded => _engine is not null;

    /// <summary>
    /// 识别一张已解码成 BGRA32 交错像素的图片。模型加载与推理由后台线程执行，不阻塞界面。
    /// </summary>
    /// <param name="bgra">BGRA32 像素数据，行间无额外填充（stride = 宽度 × 4）。</param>
    /// <param name="width">图片宽度（像素）。</param>
    /// <param name="height">图片高度（像素）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>按行拼接的识别全文；无文字时返回空串。</returns>
    public async Task<string> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        var engine = await GetEngineAsync(cancellationToken).ConfigureAwait(false);

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();

            // 推理是纯 CPU 计算，放到线程池执行，避免占用 UI 线程导致界面卡死
            var result = await Task.Run(
                () => engine.Run(bgra, width, height, width * 4, ImagePixelFormat.Bgra32),
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "OCR 识别完成，尺寸 {Width}×{Height}，文本行 {Lines}，耗时 {Elapsed} 毫秒。",
                width, height, result.Lines.Length, stopwatch.ElapsedMilliseconds);

            return result.Text ?? string.Empty;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>惰性加载 PP-OCRv6 tiny 中文模型组（检测 + 方向分类 + 识别），只执行一次。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>可复用的 OCR 引擎。</returns>
    private async Task<PaddleOcrAll> GetEngineAsync(CancellationToken cancellationToken)
    {
        var engine = _engine;
        if (engine is not null)
        {
            return engine;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is null)
            {
                _logger.LogInformation("正在加载内嵌 PaddleOCR 中文模型（PP-OCRv6 tiny）...");
                var stopwatch = Stopwatch.StartNew();

                // 模型解析是纯 CPU 开销，同样放到线程池执行
                var loaded = await Task.Run(
                    async () => await PaddleOcrAll.LoadAsync(ChineseV6TinyModels.Default).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);

                _engine = loaded;
                _logger.LogInformation("PaddleOCR 模型加载完成，耗时 {Elapsed} 毫秒。", stopwatch.ElapsedMilliseconds);
            }

            return _engine;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
