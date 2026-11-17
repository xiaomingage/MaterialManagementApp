using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace MaterialManagementApp.Services;

/// <summary>
/// 水印图片扫描服务（单例）。监听设置中的「水印图片根目录」，自动发现「今日水印相机」照片，
/// 后台串行 OCR 后提取经纬度、拍摄时间与业务类别，按「工点 + 日期 + 类别」写入 SiteProgressDaily。
///
/// 关键行为：
/// 1. 启动时做一次全量增量扫描（已处理文件按路径 + 大小 + 修改时间去重，不重复识别）；
/// 2. 运行期用 FileSystemWatcher 监听新文件，落地后延迟 3 秒再读取，避免读到未写完的文件；
/// 3. 全部解析在后台队列中串行执行，不阻塞 UI 线程；
/// 4. 提取不到经纬度的照片进入「待定位池」（IsLocated=false），保留记录而不是丢弃。
/// </summary>
public sealed class WatermarkScannerService : IDisposable
{
    /// <summary>文件落地后的等待时间，避免相册同步过程中读到半张图。</summary>
    private static readonly TimeSpan FileSettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>支持的图片扩展名。</summary>
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png" };

    private static readonly ILogger Logger = AppLog.CreateLogger<WatermarkScannerService>();

    private static readonly Lazy<WatermarkScannerService> LazyInstance = new(() => new WatermarkScannerService());

    /// <summary>全局唯一实例；设备扫描任务全应用范围内只应存在一份。</summary>
    public static WatermarkScannerService Instance => LazyInstance.Value;

    /// <summary>坐标对：水印相机通常输出「经度,纬度」十进制度。</summary>
    private static readonly Regex CoordPairRegex = new(
        @"(?<lng>\d{2,3}\.\d{4,8})\s*[,，]\s*(?<lat>\d{1,2}\.\d{4,8})",
        RegexOptions.Compiled);

    /// <summary>带「经度 / 东经」字样的经度。</summary>
    private static readonly Regex LngKeywordRegex = new(
        @"(?:经度|东经|Longitude|Lng)\s*[:：]?\s*(?<v>\d{2,3}\.\d{4,8})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>带「纬度 / 北纬」字样的纬度。</summary>
    private static readonly Regex LatKeywordRegex = new(
        @"(?:纬度|北纬|Latitude|Lat)\s*[:：]?\s*(?<v>\d{1,2}\.\d{4,8})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>拍摄时间：支持 2026-11-17 08:30 / 2026年11月17日 08:30 两种常见水印格式。</summary>
    private static readonly Regex ShotTimeRegex = new(
        @"(?<y>20\d{2})\s*[-年/.]\s*(?<m>\d{1,2})\s*[-月/.]\s*(?<d>\d{1,2})\s*日?\s*(?<h>\d{1,2})\s*[:：]\s*(?<mi>\d{1,2})",
        RegexOptions.Compiled);

    /// <summary>水印「施工区域」等工点信息。</summary>
    private static readonly Regex SiteNameRegex = new(
        @"(?:施工区域|施工位置|施工地点|工程名称|项目名称|拍摄地点|工地名称|工点名称)\s*[：:]\s*(?<v>[^\r\n]{2,30})",
        RegexOptions.Compiled);

    /// <summary>纯日期目录名（如 2026-11-17 / 20261117），不能作为工点名。</summary>
    private static readonly Regex DateFolderRegex = new(
        @"^\d{4}[-_年]?\d{0,2}[-_月]?\d{0,2}日?$",
        RegexOptions.Compiled);

    private static readonly string[] EarthNameKeywords = { "地材", "进场" };
    private static readonly string[] SpoilNameKeywords = { "渣土", "外运", "转运" };
    private static readonly string[] EarthTextKeywords = { "地材", "砂石", "碎石", "级配", "进场" };
    private static readonly string[] SpoilTextKeywords = { "渣土", "弃土", "外运", "转运", "消纳" };

    private static readonly string[] FileNameNoiseKeywords =
        { "地材", "进场", "渣土", "外运", "转运", "今日水印相机", "水印相机" };

    private readonly object _gate = new();

    private readonly object _pendingLock = new();

    /// <summary>已入队未处理的文件，避免全量扫描与监听事件重复入队。</summary>
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    private Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? _cts;

    private FileSystemWatcher? _watcher;

    private Task? _worker;

    private WatermarkScannerService()
    {
    }

    /// <summary>一条水印图片处理完成（成功入库）后触发，供首页刷新地图；事件在后台线程触发。</summary>
    public event EventHandler? ScanCompleted;

    /// <summary>当前是否已启动监听。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _cts is not null;
            }
        }
    }

    /// <summary>
    /// 启动扫描：未配置根目录或目录不存在时只记录日志并返回，不影响应用启动。
    /// 重复调用无副作用；修改设置后调用 <see cref="Restart"/> 让新目录立即生效。
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            var root = (SettingsService.Instance.Settings.WatermarkRootPath ?? string.Empty).Trim();
            if (root.Length == 0)
            {
                Logger.LogInformation("未配置水印图片根目录，跳过自动扫描。");
                return;
            }

            if (!Directory.Exists(root))
            {
                Logger.LogWarning("水印图片根目录不存在，跳过自动扫描：{Path}", root);
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

            _worker = Task.Run(() => WorkerLoopAsync(cts.Token));

            try
            {
                _watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = "*.*",
                    EnableRaisingEvents = true
                };
                _watcher.Created += OnFileChanged;
                _watcher.Changed += OnFileChanged;
                _watcher.Renamed += OnFileRenamed;
                _watcher.Error += OnWatcherError;
            }
            catch (Exception ex)
            {
                // 监听失败不影响启动时的全量扫描，只是运行期新增文件要等下次启动才处理
                Logger.LogWarning(ex, "水印图片目录监听启动失败：{Path}", root);
                _watcher = null;
            }

            _ = Task.Run(() => FullScan(root, cts.Token), cts.Token);
            Logger.LogInformation("水印图片自动扫描已启动，根目录：{Path}", root);
        }
    }

    /// <summary>停止扫描并释放监听。重复调用无副作用。</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_gate)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;

            if (_watcher is not null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                }
                catch
                {
                    // 监听器已失效，忽略
                }

                _watcher = null;
            }

            if (cts is null)
            {
                return;
            }

            _queue.Writer.TryComplete();
            cts.Cancel();
            lock (_pendingLock)
            {
                _pending.Clear();
            }
        }

        // 等后台循环退出后再释放取消源，避免其仍在注册取消回调时抛 ObjectDisposedException
        _ = Task.Run(async () =>
        {
            try
            {
                if (worker is not null)
                {
                    await worker.ConfigureAwait(false);
                }
            }
            catch
            {
                // 退出期间的异常无需处理
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    /// <summary>按最新设置重启扫描（设置页保存/恢复默认后调用）。</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>停止监听并释放资源。</summary>
    public void Dispose() => Stop();

    /// <summary>文件事件：只处理图片，延迟 3 秒后入队，避免读到未写完的文件。</summary>
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsImageFile(e.FullPath))
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FileSettleDelay, token).ConfigureAwait(false);
                Enqueue(e.FullPath);
            }
            catch (OperationCanceledException)
            {
                // 服务已停止，丢弃等待中的文件
            }
        }, token);
    }

    /// <summary>重命名（含从临时名改成正式名）按新路径处理，同样延迟后入队。</summary>
    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsImageFile(e.FullPath))
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FileSettleDelay, token).ConfigureAwait(false);
                Enqueue(e.FullPath);
            }
            catch (OperationCanceledException)
            {
                // 服务已停止
            }
        }, token);
    }

    /// <summary>监听异常（如目录被删除）只记录日志，不终止已有的后台处理队列。</summary>
    private void OnWatcherError(object sender, ErrorEventArgs e) =>
        Logger.LogWarning(e.GetException(), "水印图片目录监听发生异常，后续新增文件可能不再自动识别。");

    /// <summary>启动时的全量增量扫描：把未处理过的图片依次入队。</summary>
    private void FullScan(string root, CancellationToken token)
    {
        try
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (!IsImageFile(file))
                {
                    continue;
                }

                Enqueue(file);
                count++;
            }

            Logger.LogInformation("水印图片全量扫描完成，共发现 {Count} 个候选文件。", count);
        }
        catch (OperationCanceledException)
        {
            // 服务停止
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "水印图片全量扫描失败：{Path}", root);
        }
    }

    /// <summary>去重后放入队列（去重表只覆盖尚未处理的文件）。</summary>
    private void Enqueue(string path)
    {
        lock (_pendingLock)
        {
            if (!_pending.Add(path))
            {
                return;
            }
        }

        if (!_queue.Writer.TryWrite(path))
        {
            lock (_pendingLock)
            {
                _pending.Remove(path);
            }
        }
    }

    /// <summary>后台串行消费队列：逐张识别、入库，异常不中断后续文件。</summary>
    private async Task WorkerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessFileAsync(path, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "处理水印图片失败：{File}", path);
                    TryMarkFailed(path, ex.Message);
                }
                finally
                {
                    lock (_pendingLock)
                    {
                        _pending.Remove(path);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止
        }
    }

    /// <summary>
    /// 处理单张图片：OCR → 解析坐标/时间/类别/工点 → 坐标转 GCJ-02 → 幂等入库。
    /// </summary>
    private async Task ProcessFileAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return;
        }

        var size = info.Length;
        var ticks = info.LastWriteTimeUtc.Ticks;

        // 全量扫描与监听事件可能同时命中同一文件，真正识别前再判一次
        if (DatabaseService.Instance.IsWatermarkFileProcessed(path, size, ticks))
        {
            return;
        }

        var text = await RecognizeWithRetryAsync(path, token).ConfigureAwait(false);

        var shotAt = TryParseShotTime(text);
        var effectiveTime = shotAt ?? info.LastWriteTime;
        var dateText = effectiveTime.ToString("yyyy-MM-dd");
        var shotText = effectiveTime.ToString("yyyy-MM-dd HH:mm");

        var folderName = Directory.GetParent(Path.GetFullPath(path))?.Name ?? string.Empty;
        var category = DetectCategory(Path.GetFileName(path), folderName, text);

        var coordinates = TryParseCoordinates(text);
        var isLocated = coordinates.HasValue;
        double wgsLng = 0;
        double wgsLat = 0;
        double gcjLng = 0;
        double gcjLat = 0;

        if (coordinates is { } coord)
        {
            wgsLng = coord.Lng;
            wgsLat = coord.Lat;
            (gcjLng, gcjLat) = CoordConverter.Wgs84ToGcj02(wgsLng, wgsLat);
        }

        var siteName = ResolveSiteName(TryParseSiteName(text), path, isLocated, gcjLng, gcjLat);
        var db = DatabaseService.Instance;

        if (!isLocated)
        {
            // 待定位池：没有经纬度就不上图，但保留记录，避免照片「消失」
            if (db.FindUnlocatedSiteProgressByFile(path) is null)
            {
                db.InsertSiteProgress(new SiteProgressDaily
                {
                    SiteName = siteName,
                    Date = dateText,
                    Category = category,
                    Lng = 0,
                    Lat = 0,
                    WgsLng = 0,
                    WgsLat = 0,
                    PhotoCount = 1,
                    FirstShotAt = shotText,
                    LastShotAt = shotText,
                    SourceFilePath = path,
                    IsLocated = false
                });
            }
        }
        else
        {
            var existing = db.FindSiteProgressAggregate(siteName, dateText, category.ToString());
            if (existing is null)
            {
                db.InsertSiteProgress(new SiteProgressDaily
                {
                    SiteName = siteName,
                    Date = dateText,
                    Category = category,
                    Lng = gcjLng,
                    Lat = gcjLat,
                    WgsLng = wgsLng,
                    WgsLat = wgsLat,
                    PhotoCount = 1,
                    FirstShotAt = shotText,
                    LastShotAt = shotText,
                    SourceFilePath = path,
                    IsLocated = true
                });
            }
            else
            {
                var first = string.IsNullOrWhiteSpace(existing.FirstShotAt)
                            || string.CompareOrdinal(shotText, existing.FirstShotAt) < 0
                    ? shotText
                    : existing.FirstShotAt;
                var last = string.IsNullOrWhiteSpace(existing.LastShotAt)
                           || string.CompareOrdinal(shotText, existing.LastShotAt) > 0
                    ? shotText
                    : existing.LastShotAt;

                db.UpdateSiteProgressAggregate(existing.Id, existing.PhotoCount + 1, first, last, path);
            }
        }

        db.SaveWatermarkFileState(path, size, ticks, "Processed", siteName);

        // 复工检测：已完工工点又扫到新照片时自动恢复为在建（服务内部写 Warning 日志），
        // 不做任何静默恢复，状态变化由气泡角标与今日记录黄底提示体现
        SiteStatusService.Instance.DetectRevival(siteName);

        if (!isLocated && !string.IsNullOrWhiteSpace(text))
        {
            // 文本/二维码解析成功但坐标提取失败：单独打 Warning 作为异常信号，处理流程保持不变
            Logger.LogWarning(
                "水印文本解析成功但未提取到经纬度，记录已进入待定位池：{File}", path);
        }

        Logger.LogInformation(
            "水印图片解析完成：{File} → 工点={Site}，日期={Date}，类别={Category}，定位={Located}。",
            Path.GetFileName(path), siteName, dateText, category, isLocated);

        ScanCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>识别失败时尽力写入失败状态（下次启动会重试该文件）。</summary>
    private static void TryMarkFailed(string path, string message)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return;
            }

            DatabaseService.Instance.SaveWatermarkFileState(
                path, info.Length, info.LastWriteTimeUtc.Ticks, "Failed", message);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "记录水印图片失败状态失败：{File}", path);
        }
    }

    /// <summary>读取图片并走现有 OCR 路由；文件尚未写完时短暂重试。</summary>
    private static async Task<string> RecognizeWithRetryAsync(string path, CancellationToken token)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RecognizeOnceAsync(path, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException or UnauthorizedAccessException))
            {
                await Task.Delay(700, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>单次识别：解码为 BGRA32 走 PaddleOCR，同时读取原始字节供在线 AI 使用。</summary>
    private static async Task<string> RecognizeOnceAsync(string path, CancellationToken token)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoded = await OcrImageDecoder.DecodeAsync(stream);

        byte[]? imageBytes = null;
        try
        {
            imageBytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 原始字节读失败只影响在线 AI，离线识别仍可继续
        }

        var result = await OcrEngineRouter.RecognizeAsync(
            imageBytes, decoded.Bgra, decoded.Width, decoded.Height, token).ConfigureAwait(false);

        return result.Text ?? string.Empty;
    }

    /// <summary>从 OCR 全文提取经纬度（WGS84）。优先「经度/纬度」成对写法，其次逗号分隔坐标。</summary>
    private static (double Lng, double Lat)? TryParseCoordinates(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lngMatch = LngKeywordRegex.Match(text);
        var latMatch = LatKeywordRegex.Match(text);
        if (lngMatch.Success && latMatch.Success
            && double.TryParse(lngMatch.Groups["v"].Value, out var keywordLng)
            && double.TryParse(latMatch.Groups["v"].Value, out var keywordLat)
            && IsChinaCoordinate(keywordLng, keywordLat))
        {
            return (keywordLng, keywordLat);
        }

        foreach (Match match in CoordPairRegex.Matches(text))
        {
            if (!double.TryParse(match.Groups["lng"].Value, out var lng)
                || !double.TryParse(match.Groups["lat"].Value, out var lat))
            {
                continue;
            }

            if (IsChinaCoordinate(lng, lat))
            {
                return (lng, lat);
            }

            // 少数水印写成「纬度,经度」，交换后再校验一次
            if (IsChinaCoordinate(lat, lng))
            {
                return (lat, lng);
            }
        }

        return null;
    }

    /// <summary>中国境内经纬度范围校验，避免把无关数字误判为坐标。</summary>
    private static bool IsChinaCoordinate(double lng, double lat) =>
        lng is >= 73 and <= 136 && lat is >= 3 and <= 54;

    /// <summary>从 OCR 全文提取拍摄时间；提取不到返回 null，由调用方回退文件修改时间。</summary>
    private static DateTime? TryParseShotTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ShotTimeRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var year = int.Parse(match.Groups["y"].Value);
            var month = int.Parse(match.Groups["m"].Value);
            var day = int.Parse(match.Groups["d"].Value);
            var hour = int.Parse(match.Groups["h"].Value);
            var minute = int.Parse(match.Groups["mi"].Value);

            return new DateTime(year, month, day, hour, minute, 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>从 OCR 全文提取水印「施工区域」等工点名称。</summary>
    private static string? TryParseSiteName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = SiteNameRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return SanitizeSiteName(match.Groups["v"].Value);
    }

    /// <summary>
    /// 业务类别判定：文件名 / 所在子文件夹优先，未命中再用 OCR 关键词兜底，仍未命中归入「待确认」。
    /// </summary>
    private static SiteProgressCategory DetectCategory(string fileName, string folderName, string ocrText)
    {
        var nameSource = $"{fileName} {folderName}";
        if (ContainsAny(nameSource, EarthNameKeywords))
        {
            return SiteProgressCategory.EarthMaterialInbound;
        }

        if (ContainsAny(nameSource, SpoilNameKeywords))
        {
            return SiteProgressCategory.SpoilTransfer;
        }

        if (ContainsAny(ocrText, EarthTextKeywords))
        {
            return SiteProgressCategory.EarthMaterialInbound;
        }

        if (ContainsAny(ocrText, SpoilTextKeywords))
        {
            return SiteProgressCategory.SpoilTransfer;
        }

        return SiteProgressCategory.Unknown;
    }

    private static bool ContainsAny(string? source, string[] keywords)
    {
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            if (source.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 工点名解析顺序：水印「施工区域」→ 上一级目录名 → 文件名 → 坐标附近最近工点 → 未命名工点。
    /// </summary>
    private static string ResolveSiteName(
        string? ocrSiteName, string filePath, bool isLocated, double gcjLng, double gcjLat)
    {
        if (!string.IsNullOrWhiteSpace(ocrSiteName))
        {
            return ocrSiteName!;
        }

        var folderName = Directory.GetParent(Path.GetFullPath(filePath))?.Name;
        if (!string.IsNullOrWhiteSpace(folderName) && !DateFolderRegex.IsMatch(folderName))
        {
            var fromFolder = SanitizeSiteName(folderName);
            if (!string.IsNullOrWhiteSpace(fromFolder))
            {
                return fromFolder!;
            }
        }

        var fromFile = TrySiteFromFileName(filePath);
        if (!string.IsNullOrWhiteSpace(fromFile))
        {
            return fromFile!;
        }

        if (isLocated)
        {
            var nearest = FindNearestSiteName(gcjLng, gcjLat);
            if (!string.IsNullOrEmpty(nearest))
            {
                return nearest;
            }
        }

        return "未命名工点";
    }

    /// <summary>从文件名中剔除日期、坐标与业务关键词后剩余的可读片段，作为工点名候选。</summary>
    private static string? TrySiteFromFileName(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        stem = CoordPairRegex.Replace(stem, " ");
        stem = ShotTimeRegex.Replace(stem, " ");
        stem = Regex.Replace(stem, @"\d{6,8}", " ");

        foreach (var noise in FileNameNoiseKeywords)
        {
            stem = stem.Replace(noise, " ", StringComparison.Ordinal);
        }

        stem = stem.Replace('_', ' ').Replace('-', ' ').Trim();
        foreach (var part in stem.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length is >= 2 and <= 20)
            {
                return part;
            }
        }

        return null;
    }

    /// <summary>清洗工点名：去掉多余空白与首尾标点，限制长度。</summary>
    private static string? SanitizeSiteName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim();
        var cutIndex = cleaned.IndexOfAny(new[] { '\t', '\r', '\n' });
        if (cutIndex > 0)
        {
            cleaned = cleaned[..cutIndex];
        }

        cleaned = cleaned.Trim(' ', '，', ',', '。', '.', '：', ':', '；', ';', '、');
        if (cleaned.Length > 24)
        {
            cleaned = cleaned[..24];
        }

        return cleaned.Length >= 2 ? cleaned : null;
    }

    /// <summary>在已知工点中找 500 米内最近的一个，用于缺少工点名时把照片归并到既有工点。</summary>
    private static string FindNearestSiteName(double gcjLng, double gcjLat)
    {
        try
        {
            var bestName = string.Empty;
            var bestDistance = double.MaxValue;

            foreach (var anchor in DatabaseService.Instance.GetKnownSiteLocations())
            {
                if (Math.Abs(anchor.Lng) < 1e-6 && Math.Abs(anchor.Lat) < 1e-6)
                {
                    continue;
                }

                var deltaX = (gcjLng - anchor.Lng) * 111320.0 * Math.Cos(gcjLat * Math.PI / 180.0);
                var deltaY = (gcjLat - anchor.Lat) * 110540.0;
                var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestName = anchor.SiteName;
                }
            }

            return bestDistance <= 500 ? bestName : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "查找坐标附近工点失败，使用默认工点名。");
            return string.Empty;
        }
    }

    /// <summary>判断扩展名是否为支持的图片格式。</summary>
    private static bool IsImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var candidate in ImageExtensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
