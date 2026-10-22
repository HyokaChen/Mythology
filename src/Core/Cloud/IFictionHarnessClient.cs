namespace Core.Cloud;

/// <summary>
/// Agent Harness 客户端契约 — 面向 Agent Loop，不暴露私有 Analyzer/算法类型。
/// 实现位于 ClientPlatform（HttpFictionHarnessClient），接口保持在 Core 以便
/// PluginCapabilities 权限映射与第三方插件引用。
/// </summary>
public interface IFictionHarnessClient
{
    Task<HarnessResult> ExecuteAsync(
        HarnessRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken
    );

    Task<AgentContextSnapshot> GetContextAsync(
        HarnessRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 设定检查 finding 反馈（CHANGE_10 §4.3 采纳率埋点）。
    /// decision ∈ {adopted, dismissed}；云端按 finding.Id 前两段聚合自动禁用低精度规则。
    /// </summary>
    Task ReportSetupFeedbackAsync(
        IReadOnlyList<(string FindingId, string Decision)> items,
        CancellationToken cancellationToken
    );
}
