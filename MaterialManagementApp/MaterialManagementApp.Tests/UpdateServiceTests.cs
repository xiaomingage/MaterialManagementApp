using System.Net;
using System.Net.Sockets;
using System.Text;
using MaterialManagementApp.Common;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// UpdateService 版本更新的单元测试：版本规整与比较、GitHub release JSON 解析、
/// 检测结果组装、检测结果缓存、下载路径与 API 常量，以及基于本地 HTTP 服务器的
/// 安装包下载行为（进度回报 / 取消 / HTTP 错误）。
/// 真实 GitHub 网络与弹窗交互流程属集成范畴，由 UIA 自动化验证。
/// </summary>
public sealed class UpdateServiceTests
{
    // ==================== 版本号规整 ====================

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(" v1.2.3 ", "1.2.3")]
    [InlineData("v1.2.3+abcdef", "1.2.3")]
    [InlineData("1.2.4-beta", "1.2.4")]
    public void NormalizeVersion_忽略v前缀与后缀_仅保留主次修订(string input, string expected)
    {
        Assert.Equal(expected, AppInfo.NormalizeVersion(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    public void NormalizeVersion_无法解析时返回null(string? input)
    {
        Assert.Null(AppInfo.NormalizeVersion(input));
    }

    [Fact]
    public void CurrentVersion_来自csproj版本且为合法三段式()
    {
        // 主工程 csproj <Version> 编译期注入 InformationalVersion，AppInfo 解析为纯三段式版本
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.CurrentVersion));
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.CurrentVersion);
    }

    // ==================== 版本比较 ====================

    [Theory]
    [InlineData("1.2.2", "1.2.3", -1)]
    [InlineData("1.3.0", "1.2.9", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("v0.9.0", "1.0.0", -1)]
    public void CompareVersions_语义化主次修订比较(string a, string b, int expectedSign)
    {
        var result = UpdateService.CompareVersions(a, b);

        var sign = Math.Sign(result);
        Assert.Equal(expectedSign, sign);
    }

    [Fact]
    public void CompareVersions_垃圾版本号抛FormatException()
    {
        Assert.Throws<FormatException>(() => UpdateService.CompareVersions("1.2.3", "垃圾"));
    }

    // ==================== release JSON 解析 ====================

    [Fact]
    public void ParseLatestRelease_优先选择名字含Setup的exe附件()
    {
        const string json = """
        {
          "tag_name": "v1.3.0",
          "body": "## 更新日志\n- 新功能",
          "assets": [
            { "name": "sources.zip", "browser_download_url": "https://example.com/src.zip", "size": 1024 },
            { "name": "MaterialManagementApp_Setup_1.3.0.exe", "browser_download_url": "https://example.com/setup.exe", "size": 81475584 }
          ]
        }
        """;

        var (tag, body, setupUrl, setupSize) = UpdateService.ParseLatestRelease(json);

        Assert.Equal("v1.3.0", tag);
        Assert.Contains("更新日志", body);
        Assert.Equal("https://example.com/setup.exe", setupUrl);
        Assert.Equal(81475584, setupSize);
    }

    [Fact]
    public void ParseLatestRelease_没有Setup名附件时回退任意exe()
    {
        const string json = """
        {
          "tag_name": "v1.3.0",
          "assets": [
            { "name": "portable.exe", "browser_download_url": "https://example.com/portable.exe", "size": 4096 }
          ]
        }
        """;

        var (tag, _, setupUrl, setupSize) = UpdateService.ParseLatestRelease(json);

        Assert.Equal("https://example.com/portable.exe", setupUrl);
        Assert.Equal(4096, setupSize);
    }

    [Fact]
    public void ParseLatestRelease_无附件或字段缺失时安全返回()
    {
        const string noAssets = """{ "tag_name": "v1.3.0" }""";
        var (tag1, body1, url1, size1) = UpdateService.ParseLatestRelease(noAssets);
        Assert.Equal("v1.3.0", tag1);
        Assert.Null(body1);
        Assert.Null(url1);
        Assert.Equal(0, size1);

        const string emptyAssets = """{ "tag_name": "v1.3.0", "assets": [] }""";
        var (_, _, url2, _) = UpdateService.ParseLatestRelease(emptyAssets);
        Assert.Null(url2);
    }

    // ==================== 检测结果组装 ====================

    [Fact]
    public void BuildCheckResult_远程更新时判定有新版本()
    {
        const string json = """
        {
          "tag_name": "v1.3.0",
          "body": "更新说明",
          "assets": [ { "name": "MaterialManagementApp_Setup_1.3.0.exe", "browser_download_url": "https://example.com/setup.exe", "size": 1000 } ]
        }
        """;

        var result = UpdateService.BuildCheckResult("1.2.2", json);

        Assert.True(result.HasUpdate);
        Assert.Equal("1.2.2", result.CurrentVersion);
        Assert.Equal("1.3.0", result.LatestVersion);
        Assert.Equal("更新说明", result.ReleaseNotes);
        Assert.Equal("https://example.com/setup.exe", result.SetupDownloadUrl);
        Assert.Equal(1000, result.SetupSizeBytes);
    }

    [Fact]
    public void BuildCheckResult_本地已是最新或远程更旧时判定无更新()
    {
        const string same = """{ "tag_name": "v1.3.0" }""";
        Assert.False(UpdateService.BuildCheckResult("1.3.0", same).HasUpdate);

        const string older = """{ "tag_name": "v1.0.0" }""";
        Assert.False(UpdateService.BuildCheckResult("1.3.0", older).HasUpdate);
    }

    [Fact]
    public void BuildCheckResult_远程tag无法解析时抛FormatException()
    {
        const string bad = """{ "tag_name": "no-version-here" }""";

        Assert.Throws<FormatException>(() => UpdateService.BuildCheckResult("1.2.2", bad));
    }

    [Fact]
    public void BuildCheckResult_非法JSON抛异常由调用方提示()
    {
        Assert.ThrowsAny<Exception>(() => UpdateService.BuildCheckResult("1.2.2", "{ 残缺 json"));
    }

    // ==================== 检测结果缓存 ====================

    /// <summary>把取数替换为固定应答的桩服务，并记录请求次数（缓存是否命中全看这个计数）。</summary>
    private sealed class StubUpdateService : UpdateService
    {
        private readonly string _json;

        public StubUpdateService(string json) => _json = json;

        public int FetchCount { get; private set; }

        internal override Task<string> FetchLatestReleaseJsonAsync()
        {
            FetchCount++;
            return Task.FromResult(_json);
        }
    }

    private static StubUpdateService CreateStub(string tag = "v9.9.9") =>
        new($$"""{ "tag_name": "{{tag}}", "body": "stub" }""");

    [Fact]
    public async Task CheckForUpdateAsync_缓存有效期内重复调用不重复请求()
    {
        var svc = CreateStub();

        var first = await svc.CheckForUpdateAsync();
        var second = await svc.CheckForUpdateAsync();

        Assert.Equal(1, svc.FetchCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task CheckForUpdateAsync_清除缓存后重新请求()
    {
        var svc = CreateStub();

        await svc.CheckForUpdateAsync();
        svc.ClearCacheForTest();
        await svc.CheckForUpdateAsync();

        Assert.Equal(2, svc.FetchCount);
    }

    [Fact]
    public async Task CheckForUpdateAsync_返回结果与真实检测流程一致()
    {
        var svc = CreateStub("v9.9.9");

        var result = await svc.CheckForUpdateAsync();

        Assert.True(result.HasUpdate);
        Assert.Equal("9.9.9", result.LatestVersion);
        Assert.Equal(AppInfo.CurrentVersion, result.CurrentVersion);
    }

    // ==================== 下载路径与 API 常量 ====================

    [Fact]
    public void GetDownloadPath_指向TEMP且文件名带版本号()
    {
        var path = UpdateService.GetDownloadPath("1.3.0");

        Assert.StartsWith(Path.GetTempPath(), path);
        Assert.EndsWith($"MaterialManagementApp_Setup_1.3.0.exe", path);
    }

    [Fact]
    public void GetDownloadPath_不同版本生成不同文件名()
    {
        Assert.NotEqual(UpdateService.GetDownloadPath("1.3.0"), UpdateService.GetDownloadPath("1.4.0"));
    }

    [Fact]
    public void LatestReleaseApiUrl_指向本仓库latest接口()
    {
        Assert.Equal(
            $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest",
            AppInfo.LatestReleaseApiUrl);
    }

    // ==================== 安装包下载（本地 HTTP 服务器模拟） ====================

    /// <summary>
    /// 本地 TCP 最小 HTTP 服务器：手写应答避免 HttpListener 的 URL ACL 权限问题。
    /// 请求路径含 missing 时返回 404，否则按 64KB 分块输出负载（可设每块延迟留出取消窗口）。
    /// </summary>
    private sealed class LocalHttpServer : IDisposable
    {
        private const int ChunkSize = 64 * 1024;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public byte[] Payload { get; }
        public string BaseUrl { get; }
        public string SetupUrl => BaseUrl + "setup.exe";
        public string MissingUrl => BaseUrl + "missing.exe";
        private int ChunkDelayMs { get; }

        public LocalHttpServer(byte[] payload, int chunkDelayMs = 0)
        {
            Payload = payload;
            ChunkDelayMs = chunkDelayMs;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/";
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return; // 监听器已停止
                }

                _ = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    // 必须读完整请求头块（到空行为止）：若缓冲区留有未读数据，关闭 socket 会发 RST，
                    // 客户端在读响应体时就会收到「远程主机强迫关闭连接」而非正常读到数据
                    var requestLine = await ReadRequestHeadersAsync(stream);

                    if (requestLine.Contains("missing", StringComparison.OrdinalIgnoreCase))
                    {
                        var notFound = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(notFound, _cts.Token);
                        return;
                    }

                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                        $"Content-Length: {Payload.LongLength}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _cts.Token);

                    for (var offset = 0; offset < Payload.Length; offset += ChunkSize)
                    {
                        var len = Math.Min(ChunkSize, Payload.Length - offset);
                        await stream.WriteAsync(Payload.AsMemory(offset, len), _cts.Token);
                        if (ChunkDelayMs > 0)
                        {
                            await Task.Delay(ChunkDelayMs, _cts.Token);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 客户端提前断开（取消测试）属预期，忽略
            }
        }

        /// <summary>读取完整请求头块（至空行），返回首行（GET /path HTTP/1.1）。</summary>
        private static async Task<string> ReadRequestHeadersAsync(NetworkStream stream)
        {
            var header = new MemoryStream();
            var buffer = new byte[4096];
            while (header.Length < 16 * 1024)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                {
                    break;
                }

                header.Write(buffer, 0, read);
                if (Encoding.ASCII.GetString(header.ToArray()).Contains("\r\n\r\n"))
                {
                    break;
                }
            }

            return Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0];
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* 已停止则忽略 */ }
        }
    }

    /// <summary>同步收集进度报告（Progress&lt;T&gt; 异步派发会给断言带来竞态，这里用同步实现保证确定性）。</summary>
    private sealed class CollectProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"mst_update_test_{Guid.NewGuid():N}.bin");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删除失败不影响测试结论（TEMP 由系统兜底清理）
        }
    }

    [Fact]
    public async Task DownloadSetupAsync_完整下载_内容一致且进度报满100()
    {
        var payload = new byte[3 * 1024 * 1024 + 123];
        Random.Shared.NextBytes(payload);
        using var server = new LocalHttpServer(payload);
        var destPath = NewTempPath();
        var progress = new CollectProgress();

        try
        {
            await UpdateService.Instance.DownloadSetupAsync(
                server.SetupUrl, destPath, progress, CancellationToken.None);

            Assert.Equal(payload, await File.ReadAllBytesAsync(destPath));
            Assert.NotEmpty(progress.Values);
            Assert.All(progress.Values, p => Assert.InRange(p, 0, 100));
            Assert.Equal(100.0, progress.Values[^1], 1);
            Assert.True(progress.Values.Distinct().Count() >= 2, "大文件分块下载应产生多次不同进度");
        }
        finally
        {
            TryDelete(destPath);
        }
    }

    [Fact]
    public async Task DownloadSetupAsync_用户取消时抛OperationCanceledException()
    {
        var payload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        using var server = new LocalHttpServer(payload, chunkDelayMs: 120);
        var destPath = NewTempPath();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => UpdateService.Instance.DownloadSetupAsync(server.SetupUrl, destPath, null, cts.Token));
        }
        finally
        {
            TryDelete(destPath);
        }
    }

    [Fact]
    public async Task DownloadSetupAsync_Http错误时抛异常由调用方提示()
    {
        using var server = new LocalHttpServer(new byte[] { 1, 2, 3 });
        var destPath = NewTempPath();

        try
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => UpdateService.Instance.DownloadSetupAsync(server.MissingUrl, destPath, null, CancellationToken.None));
        }
        finally
        {
            TryDelete(destPath);
        }
    }
}
