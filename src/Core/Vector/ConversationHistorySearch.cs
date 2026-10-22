using Core.Agent;
using Core.Data;

namespace Core.Vector;

/// <summary>
/// 会话历史的向量检索通道（接续换页的可选增强，默认关闭）。
///
/// 与字面子串检索的分工：字面通道零索引、确定、可迭代精化，是默认路径；
/// 本通道在封页时把条目索引进 vectors.db，供 history_search_contents(semantic: true)
/// 做语义召回（中文自然语言询问下召回更好），失败只记日志、不影响换页结果。
///
/// 索引粒度 = 单条消息：命中的 SourceEntityId 即 entryId，可直接用 history_read_item 读回原文。
/// </summary>
public static class ConversationHistorySearch
{
    /// <summary>索引一条被封页的会话条目（已索引则按内容哈希判定为无变化）。</summary>
    public static async Task IndexItemAsync(
        RetrievalKernel kernel,
        string? novelId,
        string sessionId,
        HistoryItemInfo item,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(item.ItemId) || string.IsNullOrWhiteSpace(item.Content))
            return;

        var (doc, chunks) = kernel.Builder.BuildSessionItem(
            novelId,
            item.WindowId,
            item.ItemId,
            item.Role,
            item.ToolName,
            item.Content
        );
        if (chunks.Count == 0)
            return;

        // ★ 会话隔离靠 owner = sessionId（SqliteRetrievalStore 按 owner_id 过滤：
        //   `owner_id = @owner OR owner_id IS NULL`）。不设 owner 时同一本小说的多个会话
        //   会互相命中（windowId 在不同会话里会重名，页存在性检查拦不住）。
        await kernel.IndexDocumentAsync(doc with { OwnerId = sessionId }, chunks, ct);
    }

    /// <summary>
    /// 语义检索：返回命中的历史条目（按 entryId 解析回完整条目）。
    /// 通道不可用 / 无命中时返回空列表。
    /// </summary>
    public static async Task<List<HistoryItemInfo>> SearchAsync(
        RetrievalKernel kernel,
        FileSessionStore store,
        Core.Agent.Agent agent,
        string query,
        int limit,
        CancellationToken ct = default
    )
    {
        var sessionId = agent.SessionId;
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrWhiteSpace(query))
            return [];

        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.ConversationHistory,
            // 只查本会话的页历史（索引时 owner = sessionId）
            OwnerId = sessionId,
            NovelId = agent.CurrentNovelId,
            Query = query,
            DocumentKinds = [DocumentKind.SessionWindow],
            DenseTopK = 20,
            LexicalTopK = 20,
            FinalTopK = Math.Clamp(limit, 1, 50),
        };

        var hits = await kernel.SearchAsync(request, ct);
        if (hits.Count == 0)
            return [];

        var results = new List<HistoryItemInfo>(hits.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            var itemId = hit.SourceEntityId;
            if (string.IsNullOrEmpty(itemId) || !seen.Add(itemId))
                continue;

            // ★ 归属校验：只认能从「本会话」解析出来的条目。
            // 作用有两层——① InMemoryRetrievalStore 不实现 owner 过滤（测试/局部场景），
            // ② 索引里可能残留已删除会话的条目；两者都靠这一步挡住跨会话泄漏。
            var found = await store.ReadHistoryItemAsync(sessionId, "", itemId, ct);
            if (found is null)
                continue;

            results.Add(found.Value.Item);
        }

        return results;
    }
}
