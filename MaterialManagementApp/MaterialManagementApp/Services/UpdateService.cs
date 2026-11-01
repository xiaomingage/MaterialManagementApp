using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaterialManagementApp.Common;

namespace MaterialManagementApp.Services;

/// <summary>一次版本检测的结果。</summary>
/// <param name="HasUpdate">远程版本是否比当前版本新。</param>
/// <param name="CurrentVersion">本地当前版本（主.次.修订）。</param>
/// <param name="LatestVersion">远程最新版本（已剥离 v 前缀）。</param>
/// <param name="ReleaseNotes">release 的更新日志原文（body 字段）。</param>
/// <param name="SetupDownloadUrl">安装包附件下载地址；null 表示 release 未上传安装包。</param>
/// <param name="SetupSizeBytes">安装包大小（字节），用于下载进度与文案。</param>
public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseNotes,
    string? SetupDownloadUrl,
    long SetupSizeBytes);

/// <summary>
/// 版本更新服务：对接 GitHub Releases 检测新版本、下载安装包。
/// 所有网络操作均为异步，不阻塞 UI；检测结果带 5 分钟缓存，短时间反复点击版本号不会重复请求。
/// 更新全程由用户确认（检测 → 确认弹窗 → 下载进度 → 拉起安装向导），绝不静默安装。
/// </summary>
public sealed class UpdateService
{
    private static readonly Lazy<UpdateService> _instance = new(() => new UpdateService());

    /// <summary>懒加载单例。缓存挂在实例字段上，应用进程重启后自然失效。</summary>
    public static UpdateService Instance => _instance.Value;

    /// <summary>检测结果缓存时长：短时间反复点击版本号不重复请求 GitHub（避免触发 API 限流）。</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>检测专用客户端：短超时快速失败，网络异常立刻反馈给用户。</summary>
    private static readonly HttpClient _checkClient = CreateClient(TimeSpan.FromSeconds(30));

    /// <summary>
    /// 下载专用客户端：不限整体超时（安装包较大、网速不一），改由取消令牌与用户操作控制结束。
    /// </summary>
    private static readonly HttpClient _downloadClient = CreateClient(Timeout.InfiniteTimeSpan);

    private UpdateCheckResult? _cachedResult;
    private DateTimeOffset _cachedAtUtc;

    private UpdateService()
    {
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        // GitHub API 强制要求 User-Agent，缺失直接 403
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MaterialManagementApp/{AppInfo.CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// 检查更新。缓存命中时直接返回，否则请求 GitHub Releases latest 接口；
    /// 网络/解析异常原样上抛，由调用方统一弹窗提示（不吞异常、不卡 UI）。
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        if (_cachedResult is { } cached && DateTimeOffset.UtcNow - _cachedAtUtc < CacheTtl)
        {
            return cached;
        }

        using var response = await _checkClient.GetAsync(AppInfo.LatestReleaseApiUrl).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        var result = BuildCheckResult(AppInfo.CurrentVersion, json);
        _cachedResult = result;
        _cachedAtUtc = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>清除检测结果缓存（供单元测试使用；应用内正常路径靠 TTL 过期）。</summary>
    internal void ClearCacheForTest()
    {
        _cachedResult = null;
        _cachedAtUtc = DateTimeOffset.MinValue;
    }

    /// <summary>清空缓存（应用内不使用；单元测试隔离用例状态）。</summary>
    public static void ResetCacheForTest() => Instance.ClearCacheForTest();

    // ==================== 纯逻辑（单元测试覆盖） ====================

    /// <summary>
    /// 解析 releases/latest 返回的 JSON 并与当前版本比较，得到检测结果。
    /// tag 缺失或无法解析时抛异常（FormatException / JsonException），由调用方提示。
    /// </summary>
    /// <param name="currentVersion">本地当前版本（主.次.修订）。</param>
    /// <param name="releaseJson">GitHub API 返回的 JSON 原文。</param>
    public static UpdateCheckResult BuildCheckResult(string currentVersion, string releaseJson)
    {
        var (tag, body, setupUrl, setupSize) = ParseLatestRelease(releaseJson);
        var latest = AppInfo.NormalizeVersion(tag)
            ?? throw new FormatException($"无法识别的远程版本号：{tag ?? "(null)"}");
        var hasUpdate = CompareVersions(latest, currentVersion) > 0;
        return new UpdateCheckResult(hasUpdate, currentVersion, latest, body ?? string.Empty, setupUrl, setupSize);
    }

    /// <summary>
    /// 解析 releases/latest 的 JSON：tag_name、body 与安装包附件（名字含 Setup 的 exe，退而求其次任意 exe）。
    /// </summary>
    public static (string? Tag, string? Body, string? SetupUrl, long SetupSize) ParseLatestRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String
            ? tagEl.GetString()
            : null;
        var body = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
            ? bodyEl.GetString()
            : null;

        string? setupUrl = null;
        long setupSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            string? fallbackUrl = null;
            long fallbackSize = 0;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrEmpty(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                var size = asset.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s) ? s : 0;
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                {
                    setupUrl = url;
                    setupSize = size;
                    break;
                }

                fallbackUrl ??= url;
                if (fallbackSize == 0)
                {
                    fallbackSize = size;
                }
            }

            setupUrl ??= fallbackUrl;
            if (setupSize == 0 && !ReferenceEquals(setupUrl, null) && setupUrl == fallbackUrl)
            {
                setupSize = fallbackSize;
            }
        }

        return (tag, body, setupUrl, setupSize);
    }

    /// <summary>
    /// 语义化版本比较（主.次.修订）：&gt;0 表示 a 更新，==0 相同，&lt;0 表示 b 更新；
    /// 自动忽略 v/V 前缀，无法解析时抛 FormatException。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var va = Version.Parse(AppInfo.NormalizeVersion(a)
            ?? throw new FormatException($"无法识别的版本号：{a}"));
        var vb = Version.Parse(AppInfo.NormalizeVersion(b)
            ?? throw new FormatException($"无法识别的版本号：{b}"));
        return va.CompareTo(vb);
    }

    // ==================== 下载与安装 ====================

    /// <summary>下载安装包到指定路径，按下载百分比回报进度；取消/失败时由调用方清理临时文件。</summary>
    /// <param name="url">安装包下载地址（release assets 的 browser_download_url）。</param>
    /// <param name="destPath">目标临时文件完整路径。</param>
    /// <param name="progress">进度回调（0-100，UI 线程由 Progress&lt;T&gt; 自动调度）。</param>
    /// <param name="cancellationToken">用户取消令牌；取消时抛 OperationCanceledException。</param>
    public async Task DownloadSetupAsync(
        string url,
        string destPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _downloadClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        var directory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (progress is not null && total > 0)
            {
                progress.Report(written * 100.0 / total);
            }
        }

        // 服务端未返回总大小时无法计算百分比，结束时直接报满
        if (progress is not null && total <= 0)
        {
            progress.Report(100);
        }
    }

    /// <summary>下载中安装包的临时存放路径（%TEMP%，按目标版本命名；取消/失败由调用方删除）。</summary>
    public static string GetDownloadPath(string latestVersion) =>
        Path.Combine(Path.GetTempPath(), $"MaterialManagementApp_Setup_{latestVersion}.exe");

    /// <summary>
    /// 用系统默认方式启动安装程序向导（Inno Setup 覆盖安装，用户手动完成各步骤，绝不静默安装）。
    /// </summary>
    public static void LaunchInstaller(string setupPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setupPath)
        {
            UseShellExecute = true
        });
    }
}
