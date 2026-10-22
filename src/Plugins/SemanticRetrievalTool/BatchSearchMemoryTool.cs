using System.ComponentModel;
using System.Text;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.SemanticRetrievalTool;

/// <summary>
/// 批量语义检索工具：一次 tool call 执行多个查询。
/// 适合需要同时收集多个上下文的场景（如同时检索多个角色/情节）。
/// </summary>
public sealed class BatchSearchMemoryTool(RetrievalKernel kernel)
    : AgentTool<BatchSearchMemoryParams, BatchSearchMemoryDetails>
{
    public override string Name => "batch_search_memory";
    public override string Label => "批量搜索记忆";
    public override string Description =>
        "一次执行多个语义检索查询。每个查询独立返回结果。适合同时收集多个上下文";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<BatchSearchMemoryDetails>> ExecuteAsync(
        string toolCallId,
        BatchSearchMemoryParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchSearchMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (args.Queries is null || args.Queries.Count == 0)
        {
            return new AgentToolResult<BatchSearchMemoryDetails>
            {
                Content = [new TextContent("请提供至少一个查询")],
                IsError = true,
            };
        }

        var documentKinds = args.TypeFilter switch
        {
            "character" => new List<DocumentKind> { DocumentKind.CharacterProfile },
            "chapter" => new List<DocumentKind>
            {
                DocumentKind.ChapterSummary,
                DocumentKind.ChapterBodyChunk,
            },
            "plotline" => new List<DocumentKind> { DocumentKind.PlotlineProfile },
            "faction" => new List<DocumentKind> { DocumentKind.FactionProfile },
            "location" => new List<DocumentKind> { DocumentKind.LocationProfile },
            "item" => new List<DocumentKind> { DocumentKind.ItemProfile },
            _ => null,
        };

        var sb = new StringBuilder();
        var totalResults = 0;
        var executed = 0;

        for (var i = 0; i < args.Queries.Count; i++)
        {
            var query = args.Queries[i];
            if (string.IsNullOrWhiteSpace(query))
                continue;

            var request = new RetrievalSearchRequest
            {
                Domain = RetrievalDomain.NovelKnowledge,
                OwnerId = "local",
                NovelId = args.NovelId,
                Query = query,
                DocumentKinds = documentKinds,
                FinalTopK = args.TopK,
            };
            var results = await kernel.SearchAsync(request, ct);
            totalResults += results.Count;
            executed++;

            sb.AppendLine($"## 查询 {i + 1}: {query}");
            if (results.Count == 0)
            {
                sb.AppendLine("(无结果)");
            }
            else
            {
                foreach (var r in results)
                    sb.AppendLine(
                        $"- [{r.DocumentKind.ToChineseLabel()}] (score: {r.Score:F2}) {r.Text}"
                    );
            }
            sb.AppendLine();
        }

        var summary =
            executed == 0
                ? "所有查询均为空"
                : $"共执行 {executed} 个查询，返回 {totalResults} 条结果\n\n{sb}";

        return new AgentToolResult<BatchSearchMemoryDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchSearchMemoryDetails
            {
                QueryCount = executed,
                TotalResultCount = totalResults,
            },
        };
    }
}

/// <summary>
/// 批量搜索记忆工具的参数。
/// </summary>
public sealed record BatchSearchMemoryParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("查询文本列表（每个元素是一个独立的语义检索查询）")]
    public List<string>? Queries { get; init; }

    [Description(
        "类型过滤：character / chapter / plotline / faction / location / item，留空则搜索全部"
    )]
    public string? TypeFilter { get; init; }

    [Description("每个查询返回的结果数量（默认 5）")]
    public int TopK { get; init; } = 5;
}

/// <summary>
/// 批量搜索记忆工具的返回详情。
/// </summary>
public sealed record BatchSearchMemoryDetails
{
    public int QueryCount { get; init; }
    public int TotalResultCount { get; init; }
}
