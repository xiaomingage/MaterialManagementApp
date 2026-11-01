using System;
using System.Reflection;

namespace MaterialManagementApp.Common;

/// <summary>
/// 应用元信息唯一来源。版本号不在代码里写死字面量：csproj 的 &lt;Version&gt; 编译期注入
/// AssemblyInformationalVersion（形如 1.2.2+commit），界面显示与更新检测统一从这里取，
/// 改版本号只需同步 csproj 与 setup.iss 两处，代码零改动。
/// </summary>
public static class AppInfo
{
    /// <summary>GitHub 仓库所有者（版本更新源）。</summary>
    public const string RepoOwner = "xiaomingage";

    /// <summary>GitHub 仓库名（版本更新源）。</summary>
    public const string RepoName = "MaterialManagementApp";

    /// <summary>GitHub Releases latest API 地址：读取最新正式发布（不含草稿与预发布）。</summary>
    public static string LatestReleaseApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>当前版本号（主.次.修订，如 1.2.2），供界面显示与更新比较统一使用。</summary>
    public static string CurrentVersion { get; } = ParseCurrentVersion();

    /// <summary>
    /// 从程序集 InformationalVersion 提取纯版本号：剥离 +commit 与 v/V 前缀，
    /// 解析失败兜底 0.0.0（保证界面永远有值可显示，不会因元数据缺失崩溃）。
    /// </summary>
    private static string ParseCurrentVersion()
    {
        // 用 typeof(AppInfo).Assembly 而非 GetExecutingAssembly()：单元测试工程中后者指向测试程序集
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var version = NormalizeVersion(informational);
        return version ?? "0.0.0";
    }

    /// <summary>
    /// 规整版本文本：忽略 v/V 前缀、+commit 后缀与 -预发布后缀，仅保留 主.次.修订；
    /// 无法解析时返回 null（交给调用方决定兜底）。
    /// </summary>
    /// <param name="text">版本文本，如 v1.2.3、1.2.4+abcdef、1.2.5-beta。</param>
    public static string? NormalizeVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim().TrimStart('v', 'V');

        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        // 预发布后缀（如 1.2.3-beta）不参与语义化比较，按对应正式版处理
        var minus = trimmed.IndexOf('-');
        if (minus >= 0)
        {
            trimmed = trimmed[..minus];
        }

        return Version.TryParse(trimmed, out var parsed) ? parsed.ToString() : null;
    }
}
