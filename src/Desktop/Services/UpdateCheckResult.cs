namespace Desktop.Services;

/// <summary>
/// 更新检查结果（CheckAsync 的输出，已按当前平台过滤）。
/// </summary>
public sealed class UpdateCheckResult
{
    /// <summary>是否发现可用更新（含平台资产匹配）。</summary>
    public bool HasUpdate { get; init; }

    /// <summary>本地运行时版本（原始，含 +sha），如 "0.0.2-alpha.1+0367…"。</summary>
    public string CurrentVersion { get; init; } = string.Empty;

    /// <summary>远端最新版本（已归一化，无 v 前缀/构建元数据），如 "0.0.2-alpha.1"。</summary>
    public string LatestVersion { get; init; } = string.Empty;

    /// <summary>远端 Release 的原始 tag，如 "v0.0.2-alpha.1"。</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>发布时间（UTC，展示侧再转本地时区）。</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Release 说明（Markdown 原文）。</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>当前平台安装包资产名（Mythology-Desktop-0.0.3-win-x64-setup.exe 等）。</summary>
    public string? InstallerAssetName { get; init; }

    /// <summary>安装包下载地址（GitHub 资产 browser_download_url）。</summary>
    public string? InstallerDownloadUrl { get; init; }

    /// <summary>安装包大小（字节，来自 GitHub 资产元数据；进度条兜底用）。</summary>
    public long InstallerSizeBytes { get; init; }

    /// <summary>校验和资产名（安装包名 + .sha256）；空 = 发布未提供。</summary>
    public string? ChecksumAssetName { get; init; }

    /// <summary>校验和资产下载地址；空 = 发布未提供。</summary>
    public string? ChecksumDownloadUrl { get; init; }
}
