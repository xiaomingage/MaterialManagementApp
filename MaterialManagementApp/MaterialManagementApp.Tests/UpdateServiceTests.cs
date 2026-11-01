using MaterialManagementApp.Common;
using MaterialManagementApp.Services;
using Xunit;

namespace MaterialManagementApp.Tests;

/// <summary>
/// UpdateService 版本更新纯逻辑的单元测试：版本规整与比较、GitHub release JSON 解析、
/// 检测结果组装。网络请求与弹窗流程属集成范畴，由 UIA 自动化验证。
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
}
