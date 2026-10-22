using ClientPlatform;
using Microsoft.Extensions.Logging;

namespace CloudDiagnosis;

/// <summary>
/// 云端用量对账：每 N 章（UsageReconcileChapterInterval）低频拉取月度用量摘要
/// （/v1/agent/usage，零价查询）——为「余额预警」与「预算告警附服务端余额」提供
/// 服务端口径。失败/请求超时静默跳过（不影响诊断链路）；用户取消 OCE 上抛（§12 红线 5）。
/// </summary>
public sealed class UsageReconciler(
    UsageRestClient usage,
    CloudDiagnosisOptions options,
    ILogger? logger = null
)
{
    /// <summary>上次尝试拉取的章序（null = 尚未尝试）。失败也占一个间隔，避免对宕机服务每轮重试。</summary>
    private int? _lastAttemptOrder;

    /// <summary>
    /// 低频拉取：章序距上次尝试不足间隔 → 返回 null（不发网络）；force = 预算告警当轮强制刷新。
    /// 返回 null = 未触发/失败/请求超时（静默）；用户取消原样上抛。
    /// </summary>
    public async Task<UsageSnapshot?> TryFetchAsync(
        int chapterOrder,
        bool force,
        CancellationToken ct
    )
    {
        if (
            !force
            && _lastAttemptOrder is { } last
            && chapterOrder - last < Math.Max(1, options.UsageReconcileChapterInterval)
        )
            return null;
        if (!force)
            _lastAttemptOrder = chapterOrder;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds)));
            return await usage.GetUsageAsync(ct: cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogDebug("[云端诊断] 用量对账请求超时，跳过本轮");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("[云端诊断] 用量对账失败：{Reason}", ex.Message);
            return null;
        }
    }
}
