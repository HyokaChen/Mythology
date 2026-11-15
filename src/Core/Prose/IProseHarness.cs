namespace Core.Prose;

/// <summary>
/// 小说正文的 AI 分隔符决策 harness。
/// 它拿到完整候选正文与邻章上下文，自主决定独立分隔符的保留、新增和删除；
/// 不执行数量、密度、间距、首尾或正则检测规则。
/// </summary>
public interface IProseHarness
{
    Task<ProseHarnessResult> ReviewAsync(ProseHarnessRequest request, CancellationToken ct);
}
