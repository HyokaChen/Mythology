using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Shared;
using Microsoft.Extensions.Logging;

namespace Desktop.Services;

/// <summary>
/// 更新检测/下载服务（抽象成接口供 UpdateState 依赖与单测桩注入）。
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>检查更新（含平台匹配）。任何失败返回 HasUpdate=false，不抛异常（OCE 除外）。</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>下载安装包到本地缓存目录。</summary>
    /// <returns>本地绝对路径；失败返回 null（不留 .part）。</returns>
    Task<string?> DownloadAsync(
        string downloadUrl,
        string assetName,
        IProgress<double>? progress,
        long expectedSizeBytes,
        CancellationToken cancellationToken = default
    );

    /// <summary>下载小文本资产（校验和文件）。失败返回 null。</summary>
    Task<string?> DownloadTextAsync(
        string downloadUrl,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// 更新检测/下载实现。直连 GitHub REST（公开仓库匿名可读）：
/// 检测 = GET repos/HyokaChen/Mythology/releases/latest（按当前 RID 匹配资产 + 版本数值比较）；
/// 下载 = 资产的 browser_download_url（.part 临时文件 + 流式拷贝 + 进度回报）。
/// 任何失败一律静默返回（HasUpdate=false / null）—— 后台探测失败绝不能影响启动与使用。
/// HttpClient 由 DI 注入（具名客户端 "GitHub"，BaseAddress=https://api.github.com/）。
/// </summary>
public sealed class UpdateCheckService(
    HttpClient httpClient,
    ILogger<UpdateCheckService> logger,
    string updatesDirectory,
    Func<string?>? currentVersionProvider = null
) : IUpdateCheckService
{
    private const string Owner = "HyokaChen";
    private const string Repo = "Mythology";

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        // 注入 provider 时以其为准（null = 未烙印版本，直接跳过）；
        // 未注入时读入口程序集元数据（本地裸构建可能缺失，由调用方兜底）。
        var currentVersion = currentVersionProvider is not null
            ? currentVersionProvider() ?? string.Empty
            : AppVersion.Full ?? string.Empty;

        // 未烙印版本号的产物（本地裸构建/旧二进制）：无基准可比，永不提示更新
        if (string.IsNullOrEmpty(currentVersion))
        {
            logger.LogInformation("[UpdateCheckService] 当前产物未烙印版本号，跳过更新检查");
            return NoUpdate(currentVersion);
        }

        try
        {
            using var response = await httpClient
                .GetAsync($"repos/{Owner}/{Repo}/releases/latest", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 = 私有仓库（尚未公开）或无 release；其余为网络/服务异常 —— 一律不提示、不报错
                logger.LogWarning(
                    "[UpdateCheckService] 更新检查失败: HTTP {Status}",
                    (int)response.StatusCode
                );
                return NoUpdate(currentVersion);
            }

            var payload = JsonSerializer.Deserialize<GitHubReleaseDto>(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                JsonHelper.JsonOptions
            );
            var tagName = payload?.TagName;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                logger.LogInformation("[UpdateCheckService] 更新检查未返回有效数据，静默跳过");
                return NoUpdate(currentVersion);
            }

            var latestVersion = UpdateVersion.Normalize(tagName);

            // 版本比较必须剥构建元数据后数值比较（0.0.9+g60ae392 vs v0.0.9 → 相等不误报）：
            // 仅当 current < latest（返回 -1）才算有更新；相等或更新（0/1）都不提示。
            // 注意方向：任何 "Compare <= 0 → 无更新" 的写法都是语义反的（0.0.9 < 0.0.10 返回 -1 会被误判）。
            if (UpdateVersion.Compare(currentVersion, latestVersion) >= 0)
                return NoUpdate(currentVersion, tagName);

            var rid = UpdateAssetMap.CurrentRid();
            if (rid is null)
            {
                logger.LogInformation(
                    "[UpdateCheckService] 当前平台 RID 不在支持列表，跳过: {Rid}",
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
                );
                return NoUpdate(currentVersion, tagName);
            }

            var installerName = UpdateAssetMap.InstallerNameFor(rid, latestVersion);
            var installer = payload!.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, installerName, StringComparison.Ordinal)
            );
            if (installerName is null || installer is null)
            {
                logger.LogInformation(
                    "[UpdateCheckService] 无匹配当前平台的安装包: rid={Rid}, 期望={Asset}",
                    rid,
                    installerName
                );
                return NoUpdate(currentVersion, tagName);
            }

            var checksum = payload.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, installerName + ".sha256", StringComparison.Ordinal)
            );

            return new UpdateCheckResult
            {
                HasUpdate = true,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                TagName = tagName,
                PublishedAt = payload.PublishedAt,
                ReleaseNotes = payload.Body,
                InstallerAssetName = installerName,
                InstallerDownloadUrl = installer.BrowserDownloadUrl,
                InstallerSizeBytes = installer.Size,
                ChecksumAssetName = installerName + ".sha256",
                ChecksumDownloadUrl = checksum?.BrowserDownloadUrl,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UpdateCheckService] 更新检查异常（后台探测失败不影响启动）");
            return NoUpdate(currentVersion);
        }
    }

    /// <inheritdoc />
    public async Task<string?> DownloadAsync(
        string downloadUrl,
        string assetName,
        IProgress<double>? progress,
        long expectedSizeBytes,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(assetName))
            return null;

        var target = Path.Combine(updatesDirectory, assetName);
        // 先落临时文件再改名：中断时不会留下半个文件被当成"已就绪"（与缓存约定一致）
        var partPath = target + ".part";

        try
        {
            Directory.CreateDirectory(updatesDirectory);

            using var response = await httpClient
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "[UpdateCheckService] 安装包下载失败: {Asset}, HTTP {Status}",
                    assetName,
                    (int)response.StatusCode
                );
                return null;
            }

            // 总长优先取 Content-Length，缺失时退回资产元数据大小（进度条兜底）
            var totalBytes = response.Content.Headers.ContentLength ?? expectedSizeBytes;

            long downloaded = 0;
            // 流必须先释放再改名：File.Create 以 FileShare.None 打开 .part，句柄未关时
            // Windows 上 MoveFile 直接抛 IOException「being used by another process」
            // （Unix 允许带句柄 rename，单测在 macOS 上绿、Windows 真机必炸 —— 实测踩坑）。
            // 块作用域写法：先 Dispose 再 Move。
            await using (
                var source = await response
                    .Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            await using (var destination = File.Create(partPath))
            {
                var buffer = new byte[81920];
                int read;
                while (
                    (read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false))
                    > 0
                )
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;
                    if (totalBytes > 0)
                        progress?.Report(Math.Min(100.0, downloaded * 100.0 / totalBytes));
                }
            }

            File.Move(partPath, target, overwrite: true);
            progress?.Report(100);
            logger.LogInformation(
                "[UpdateCheckService] 安装包已下载: {Asset}, Bytes={Bytes}",
                assetName,
                downloaded
            );
            return target;
        }
        catch (OperationCanceledException)
        {
            // 应用退出取消：清掉半成品，让上层按取消语义处理
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UpdateCheckService] 安装包下载异常: {Asset}", assetName);
            TryDelete(partPath);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> DownloadTextAsync(
        string downloadUrl,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            return null;

        try
        {
            using var response = await httpClient
                .GetAsync(downloadUrl, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "[UpdateCheckService] 文本资产下载失败: {Url}, HTTP {Status}",
                    downloadUrl,
                    (int)response.StatusCode
                );
                return null;
            }

            return await response
                .Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UpdateCheckService] 文本资产下载异常: {Url}", downloadUrl);
            return null;
        }
    }

    /// <summary>构造"无更新"结果；携带最新 tag 时顺带填 LatestVersion 供状态行显示「已是最新」。</summary>
    private static UpdateCheckResult NoUpdate(string currentVersion, string? tagName = null) =>
        new()
        {
            CurrentVersion = currentVersion,
            LatestVersion = tagName is null ? string.Empty : UpdateVersion.Normalize(tagName),
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响主流程，下次下载会覆盖
        }
    }

    /// <summary>
    /// GitHub releases/latest 响应（仅提取所需字段）。
    /// 必须显式 [JsonPropertyName]：JsonHelper 的 kebab 命名策略与外网 snake_case 契约不符，
    /// 不显式标注会全部反序列化为空（编译期不报错，运行期静默读空）。
    /// </summary>
    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; init; }
    }

    /// <summary>Release 资产条目（name / browser_download_url / size）。</summary>
    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}
