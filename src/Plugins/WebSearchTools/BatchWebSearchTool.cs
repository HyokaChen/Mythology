using System.ComponentModel;
using System.Text;
using Core.Shared;
using Core.Tools;

namespace Plugins.WebSearchTools;

/// <summary>
/// 批量网络搜索工具：一次 tool call 执行多个查询。
/// 适合需要同时研究多个主题的场景。
/// </summary>
public sealed class BatchWebSearchTool(IWebSearchProvider provider)
    : AgentTool<BatchWebSearchParams, BatchWebSearchDetails>
{
    public override string Name => "batch_web_search";
    public override string Label => "批量网络搜索";
    public override string Description =>
        "一次执行多个网络搜索查询。每个查询独立返回结果。适合同时研究多个主题";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<BatchWebSearchDetails>> ExecuteAsync(
        string toolCallId,
        BatchWebSearchParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchWebSearchDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 检查查询列表是否为空
        if (args.Queries is null || args.Queries.Count == 0)
        {
            return new AgentToolResult<BatchWebSearchDetails>
            {
                Content = [new TextContent("请提供至少一个查询")],
                IsError = true,
            };
        }

        var sb = new StringBuilder();
        var totalResults = 0;
        var executed = 0;

        // 逐个处理查询
        for (var i = 0; i < args.Queries.Count; i++)
        {
            var query = args.Queries[i];
            if (string.IsNullOrWhiteSpace(query))
                continue;

            // 执行搜索
            var results = await provider.SearchAsync(query, args.MaxResults, ct);
            ThrowIfCancelled(ct);
            totalResults += results.Count;
            executed++;

            // 格式化当前查询结果
            sb.AppendLine($"## 查询 {i + 1}: {query}");
            if (results.Count == 0)
            {
                sb.AppendLine("(无结果)");
            }
            else
            {
                foreach (var r in results)
                    sb.AppendLine($"- {r.Title}\n  {r.Url}\n  {r.Snippet}");
            }
            sb.AppendLine();
        }

        // 构建汇总信息
        var summary =
            executed == 0
                ? "所有查询均为空"
                : $"共执行 {executed} 个查询，返回 {totalResults} 条结果\n\n{sb}";

        return new AgentToolResult<BatchWebSearchDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchWebSearchDetails
            {
                QueryCount = executed,
                TotalResultCount = totalResults,
            },
        };
    }
}

/// <summary>
/// 批量网络搜索工具的参数。
/// </summary>
public sealed record BatchWebSearchParams
{
    [Description("搜索关键词列表（每个元素是一个独立的搜索查询）")]
    public List<string>? Queries { get; init; }

    [Description("每个查询最大返回结果数量（默认 5）")]
    public int MaxResults { get; init; } = 5;
}

/// <summary>
/// 批量网络搜索工具的返回详情。
/// </summary>
public sealed record BatchWebSearchDetails
{
    public int QueryCount { get; init; }
    public int TotalResultCount { get; init; }
}
