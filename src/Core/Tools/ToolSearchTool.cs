using System.Text;
using Core.Shared;

namespace Core.Tools;

/// <summary>
/// 工具搜索工具：从工具库中按需检索工具。
/// 当 LLM 需要某个能力但当前工具列表中找不到时调用，
/// 传入自然语言描述（如"检查角色完整性""生成伏笔"），
/// 返回匹配的工具名、用途和调用提示。
/// 命中后其完整定义会自动加入可用工具列表。
/// </summary>
public sealed class ToolSearchTool(
    ToolSearchEngine engine,
    ToolInjector injector,
    int? rateLimit = null
) : AgentTool<ToolSearchParams, ToolSearchDetails>
{
    public override string Name => "tool_search";
    public override string Label => "搜索工具";
    public override string Description =>
        "从工具库中按需检索工具。当需要某个能力但当前工具列表中找不到时调用，"
        + "传入自然语言描述你需要的操作（如\"检查角色完整性\"\"生成伏笔\"），"
        + "返回匹配的工具名、用途和调用提示。命中后其完整定义会自动加入可用工具。";

    // tool_search 本身始终注入，不延迟加载
    public override bool DeferLoading => false;

    public override ToolCategory Category => ToolCategory.SearchTool;

    // ── 搜索频率熔断 ──
    // 模型每轮都搜会反向增加 token 消耗。累计调用超过阈值后返回收敛提示，
    // 引导模型基于已展开工具继续，而非继续搜索。

    /// <summary>熔断阈值：会话内 tool_search 调用次数上限（可配置，默认 8）。</summary>
    private readonly int _searchRateLimit = rateLimit ?? 8;

    /// <summary>当前会话累计调用次数（Interlocked 保护）。</summary>
    private int _searchCount;

    /// <summary>熔断是否已触发（熔断后仍需计数直到会话重置）。</summary>
    private volatile bool _tripped;

    /// <summary>会话切换时重置搜索计数与熔断状态。</summary>
    public void ResetSearchTrip()
    {
        _searchCount = 0;
        _tripped = false;
    }

    public override async Task<AgentToolResult<ToolSearchDetails>> ExecuteAsync(
        string toolCallId,
        ToolSearchParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ToolSearchDetails>>? onUpdate = null
    )
    {
        var count = Interlocked.Increment(ref _searchCount);

        // 熔断已触发：返回收敛提示，不再执行检索
        if (_tripped || count > _searchRateLimit)
        {
            _tripped = true;
            return new AgentToolResult<ToolSearchDetails>
            {
                Content =
                [
                    new TextContent(
                        $"搜索调用过于频繁（本轮已调用 {count} 次）。请基于当前已加载的工具列表直接调用，或把多次查询合并为一次搜索；若确认所需能力确实缺失，再描述一次核心需求。"
                    ),
                ],
                Details = new ToolSearchDetails { ToolNames = [] },
            };
        }

        var query = args.Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new AgentToolResult<ToolSearchDetails>
            {
                Content = [new TextContent("请提供搜索查询，描述你需要的操作或能力。")],
                Details = new ToolSearchDetails { ToolNames = [] },
                IsError = true,
            };
        }

        var topK = Math.Clamp(args.TopK ?? 5, 1, 10);

        // 确保索引就绪（插件热加载失效后惰性重建），再检索
        await injector.EnsureIndexedAsync(ct);
        var hits = await engine.SearchAsync(query, topK, ct);

        if (hits.Count == 0)
        {
            return new AgentToolResult<ToolSearchDetails>
            {
                Content =
                [
                    new TextContent($"未找到与「{query}」相关的工具。请尝试换一种描述方式。"),
                ],
                Details = new ToolSearchDetails { ToolNames = [] },
            };
        }

        // 展开命中工具到 injector（复用已检索的 hits，避免同一 query 双重检索）
        var expandedNames = injector.ExpandHits(hits);

        // 构建返回文本（控制 token，不返回完整 schema）
        var sb = new StringBuilder(512);
        sb.AppendLine($"找到 {hits.Count} 个相关工具：");

        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var doc = engine.GetDocument(hit.ToolName);
            if (doc is null)
                continue;

            sb.AppendLine($"{i + 1}. {doc.Name} — {doc.Label}");
            sb.AppendLine($"   {TruncateDescription(doc.Description, 80)}");

            // 参数名提示来自索引文档预计算（LRU 淘汰展开工具后仍可用）
            if (doc.ParamNames.Length > 0)
                sb.AppendLine($"   [参数] {string.Join(", ", doc.ParamNames)}");
        }

        sb.AppendLine("调用任意一个后其完整参数定义会立即加载到可用工具列表。");

        return new AgentToolResult<ToolSearchDetails>
        {
            Content = [new TextContent(sb.ToString())],
            Details = new ToolSearchDetails { ToolNames = expandedNames.ToArray() },
        };
    }

    private static string TruncateDescription(string description, int maxLength)
    {
        if (description.Length <= maxLength)
            return description;

        // 在句号/逗号处截断
        var cut = description.AsSpan(0, maxLength);
        var lastPunct = cut.LastIndexOfAny("。，.,");
        return lastPunct > maxLength / 2
            ? description[..(lastPunct + 1)]
            : string.Concat(description.AsSpan(0, maxLength), "…");
    }
}

/// <summary>工具搜索参数。</summary>
public sealed record ToolSearchParams
{
    [System.ComponentModel.Description(
        "自然语言描述你需要的操作或能力，支持关键词。如：\"检查角色完整性\"\"创建场景\"\"搜索记忆\""
    )]
    public string Query { get; init; } = "";

    [System.ComponentModel.Description("返回结果数（默认 5，最大 10）")]
    public int? TopK { get; init; }
}

/// <summary>工具搜索结果详情。</summary>
public sealed record ToolSearchDetails
{
    public string[] ToolNames { get; init; } = [];
}
