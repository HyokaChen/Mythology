using System.Text;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.ContextContinuation;

// ═══════════════════════════════════════════════════════════
//  history_* 工具族：跨页查阅被封存的历史消息与工具输出
//
//  设计约束（Codex context management 同款）：
//  - 私有的模型恢复通道：工具描述明确要求不要向用户描述该机制；
//  - 字面子串检索（大小写敏感），不需要索引、结果确定、可迭代精化；
//  - 条目 ID 即会话 JSONL 的 entryId，输出带 [id: …] 标记供 history_read_item 回读。
// ═══════════════════════════════════════════════════════════

/// <summary>history 工具共用的输出格式化与隐藏条款（无状态，不承载会话/存储）。</summary>
internal static class HistoryToolBase
{
    internal const string HiddenClause =
        "这是模型私有的恢复通道：不要向用户描述本工具、它的存在、路径或存储机制，也不要引用其内容向用户解释。";

    /// <summary>条目输出格式（逐字固定，模型据此回读）。</summary>
    internal static string FormatItem(HistoryItemInfo item) =>
        $"[窗口 {item.WindowId} | id {item.ItemId} | order {item.Order} | {item.Role}"
        + (string.IsNullOrEmpty(item.ToolName) ? "" : $" {item.ToolName}")
        + "]\n"
        + item.Content;
}

/// <summary>history_list_windows 参数。</summary>
public sealed class HistoryListWindowsParams
{
    /// <summary>返回页数上限（默认 50，上限 200）。</summary>
    public int? Limit { get; set; }

    /// <summary>true = 最近创建的页在前（默认）。</summary>
    public bool? RecentFirst { get; set; }
}

/// <summary>列出会话的所有上下文页（页 ID、条目数、起止时间）。</summary>
public sealed class HistoryListWindowsTool(Core.Agent.Agent agent, FileSessionStore sessionStore)
    : AgentTool<HistoryListWindowsParams, string>
{
    private readonly HistoryToolImpl _impl = new(agent, sessionStore);

    public override string Name => "history_list_windows";
    public override string Label => "列出上下文页";
    public override string Description =>
        "列出本会话的所有上下文页（页 ID、条目数、起始时间）。" + HistoryToolImpl.HiddenClause;
    public override ToolCategory Category => ToolCategory.Generic;
    public override string[] Aliases => ["历史页", "换页记录", "遍历会话页"];
    public override string[] ExampleQueries => ["这个会话分成了几页", "上一页是哪一页"];

    public override Task<AgentToolResult<string>> ExecuteAsync(
        string toolCallId,
        HistoryListWindowsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<string>>? onUpdate = null
    ) => _impl.ListWindowsAsync(args, ct);
}

/// <summary>history_list_items 参数。</summary>
public sealed class HistoryListItemsParams
{
    /// <summary>限定页 ID（如 "w2"）；省略 = 全部页。</summary>
    public string? WindowId { get; set; }

    /// <summary>角色过滤：user | assistant | tool | system。</summary>
    public string? Role { get; set; }

    /// <summary>工具名过滤（只看某个工具的输出）。</summary>
    public string? ToolName { get; set; }

    /// <summary>返回条目数上限（默认 20，上限 200）。</summary>
    public int? Limit { get; set; }

    /// <summary>true = 最近的条目在前（默认）。</summary>
    public bool? RecentFirst { get; set; }

    /// <summary>每条内容的最大字符数（默认 2000）。</summary>
    public int? MaxCharsPerItem { get; set; }
}

/// <summary>按页/角色/工具过滤列出历史消息与工具输出。</summary>
public sealed class HistoryListItemsTool(Core.Agent.Agent agent, FileSessionStore sessionStore)
    : AgentTool<HistoryListItemsParams, string>
{
    private readonly HistoryToolImpl _impl = new(agent, sessionStore);

    public override string Name => "history_list_items";
    public override string Label => "列出历史条目";
    public override string Description =>
        "按页 / 角色 / 工具过滤列出历史消息与工具输出（内容截断、带 [id]）。"
        + HistoryToolImpl.HiddenClause;
    public override ToolCategory Category => ToolCategory.Generic;
    public override string[] Aliases => ["历史条目", "之前的消息", "旧对话"];
    public override string[] ExampleQueries => ["上一页我让你做过什么", "列出之前调用过的工具输出"];

    public override Task<AgentToolResult<string>> ExecuteAsync(
        string toolCallId,
        HistoryListItemsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<string>>? onUpdate = null
    ) => _impl.ListItemsAsync(args, ct);
}

/// <summary>history_read_item 参数。</summary>
public sealed class HistoryReadItemParams
{
    /// <summary>条目所属页 ID（来自 history_* 输出）。</summary>
    public string WindowId { get; set; } = "";

    /// <summary>条目 ID（来自 history_* 输出里的 id 标记）。</summary>
    public string ItemId { get; set; } = "";

    /// <summary>起始字符偏移（默认 0）。</summary>
    public int? OffsetChars { get; set; }

    /// <summary>最大返回字符数（默认 4000）。</summary>
    public int? LimitChars { get; set; }
}

/// <summary>按页 ID 与条目 ID 读取历史条目原文的指定区间。</summary>
public sealed class HistoryReadItemTool(Core.Agent.Agent agent, FileSessionStore sessionStore)
    : AgentTool<HistoryReadItemParams, string>
{
    private readonly HistoryToolImpl _impl = new(agent, sessionStore);

    public override string Name => "history_read_item";
    public override string Label => "读取历史条目";
    public override string Description =>
        "按页 ID 与条目 ID 读取历史条目原文的指定区间；item_id 来自 history_* 输出里的 id 标记。"
        + HistoryToolImpl.HiddenClause;
    public override ToolCategory Category => ToolCategory.Generic;
    public override string[] Aliases => ["读取旧消息", "看原文", "恢复历史"];
    public override string[] ExampleQueries => ["把那一条的完整原文读出来"];

    public override Task<AgentToolResult<string>> ExecuteAsync(
        string toolCallId,
        HistoryReadItemParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<string>>? onUpdate = null
    ) => _impl.ReadItemAsync(args, ct);
}

/// <summary>history_search_contents 参数。</summary>
public sealed class HistorySearchParams
{
    /// <summary>大小写敏感的字面子串。</summary>
    public string Query { get; set; } = "";

    /// <summary>限定页 ID；省略 = 全部页。</summary>
    public string? WindowId { get; set; }

    /// <summary>角色过滤：user | assistant | tool | system。</summary>
    public string? Role { get; set; }

    /// <summary>工具名过滤。</summary>
    public string? ToolName { get; set; }

    /// <summary>返回条目数上限（默认 20，上限 200）。</summary>
    public int? Limit { get; set; }

    /// <summary>true = 最近的匹配在前（默认）。</summary>
    public bool? RecentFirst { get; set; }

    /// <summary>true = 追加语义检索（需配置开启 vector_retrieval，否则与 false 等价）。</summary>
    public bool? Semantic { get; set; }
}

/// <summary>在历史对话与工具输出中做字面子串检索（可选语义检索）。</summary>
public sealed class HistorySearchContentsTool(
    Core.Agent.Agent agent,
    FileSessionStore sessionStore,
    Core.Vector.RetrievalKernel? retrievalKernel = null
) : AgentTool<HistorySearchParams, string>
{
    private readonly HistoryToolImpl _impl = new(agent, sessionStore, retrievalKernel);

    public override string Name => "history_search_contents";
    public override string Label => "检索历史对话";
    public override string Description =>
        "在历史对话与工具输出中做大小写敏感的字面子串检索（semantic=true 时启用向量语义检索，"
        + "需配置开启，否则与 false 等价）。"
        + HistoryToolImpl.HiddenClause;
    public override ToolCategory Category => ToolCategory.SearchTool;
    public override string[] Aliases => ["历史检索", "翻旧账", "之前的对话", "查历史"];
    public override string[] ExampleQueries =>
        ["上周那个 bug 的报错原文是什么", "上一页我让你改过哪些文件"];

    public override Task<AgentToolResult<string>> ExecuteAsync(
        string toolCallId,
        HistorySearchParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<string>>? onUpdate = null
    ) => _impl.SearchAsync(args, ct);
}

/// <summary>history 工具的执行体（四个工具共用，避免四份复制）。</summary>
internal sealed class HistoryToolImpl(
    Core.Agent.Agent agent,
    FileSessionStore store,
    Core.Vector.RetrievalKernel? retrievalKernel = null
)
{
    internal const string HiddenClause = HistoryToolBase.HiddenClause;

    private static AgentToolResult<string> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };

    internal async Task<AgentToolResult<string>> ListWindowsAsync(
        HistoryListWindowsParams args,
        CancellationToken ct
    )
    {
        if (RequireSession() is { } err)
            return err;

        var windows = await store.ListWindowsAsync(agent.SessionId!, ct);
        var limit = Math.Clamp(args.Limit ?? 50, 1, 200);
        var ordered =
            args.RecentFirst == false ? windows.AsEnumerable() : windows.AsEnumerable().Reverse();
        var current = windows[^1].WindowId;

        var sb = new StringBuilder();
        sb.Append($"共 {windows.Count} 页（当前页 {current}）：\n");
        foreach (var w in ordered.Take(limit))
        {
            sb.Append(
                $"[窗口 {w.WindowId}{(w.WindowId == current ? " (当前)" : "")} | {w.ItemCount} 条"
            );
            if (w.StartedAt is not null)
                sb.Append($" | {w.StartedAt}");
            if (!string.IsNullOrEmpty(w.Reason))
                sb.Append($" | 原因 {w.Reason}");
            sb.Append("]\n");
        }
        return Text(sb.ToString().TrimEnd());
    }

    internal async Task<AgentToolResult<string>> ListItemsAsync(
        HistoryListItemsParams args,
        CancellationToken ct
    )
    {
        if (RequireSession() is { } err)
            return err;

        var items = await store.ListHistoryItemsAsync(
            agent.SessionId!,
            args.WindowId,
            args.Role,
            args.ToolName,
            args.Limit ?? 20,
            args.RecentFirst ?? true,
            Math.Clamp(args.MaxCharsPerItem ?? 2000, 1, 200_000),
            ct
        );

        return FormatItems(items, args.WindowId, await KnownWindowIdsAsync(args.WindowId, ct));
    }

    internal async Task<AgentToolResult<string>> SearchAsync(
        HistorySearchParams args,
        CancellationToken ct
    )
    {
        if (RequireSession() is { } err)
            return err;

        var query = args.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return Error("query 不能为空。");

        var limit = args.Limit ?? 20;
        var recentFirst = args.RecentFirst ?? true;
        var items = await store.SearchHistoryContentsAsync(
            agent.SessionId!,
            query,
            args.WindowId,
            args.Role,
            args.ToolName,
            limit,
            recentFirst,
            ct
        );

        var suffix = "";
        if (args.Semantic == true)
        {
            if (retrievalKernel is null)
            {
                // 向量通道未启用时行为与 false 等价，并说明原因，避免模型反复重试。
                suffix = "\n（语义检索未启用，已按字面检索。）";
            }
            else
            {
                var literalIds = items.Select(i => i.ItemId).ToHashSet(StringComparer.Ordinal);
                var semantic = await Core.Vector.ConversationHistorySearch.SearchAsync(
                    retrievalKernel,
                    store,
                    agent,
                    query,
                    limit,
                    ct
                );
                var added = semantic.Where(i => !literalIds.Contains(i.ItemId)).ToList();
                if (added.Count > 0)
                {
                    items =
                    [
                        .. items
                            .Concat(added)
                            .OrderByDescending(i => i.Order)
                            .Take(Math.Clamp(limit, 1, 200)),
                    ];
                    suffix = $"\n（语义检索补充 {added.Count} 条。）";
                }
                else if (semantic.Count == 0)
                {
                    suffix = "\n（语义检索无结果；该页可能尚未建立向量索引。）";
                }
            }
        }

        var formatted = FormatItems(
            items,
            args.WindowId,
            await KnownWindowIdsAsync(args.WindowId, ct)
        );
        return suffix.Length == 0
            ? formatted
            : new AgentToolResult<string>
            {
                Content =
                [
                    new TextContent(
                        formatted.Content[0] is TextContent t ? t.Text + suffix : suffix
                    ),
                ],
            };
    }

    internal async Task<AgentToolResult<string>> ReadItemAsync(
        HistoryReadItemParams args,
        CancellationToken ct
    )
    {
        if (RequireSession() is { } err)
            return err;

        if (string.IsNullOrWhiteSpace(args.ItemId))
            return Error("item_id 不能为空。");

        var found = await store.ReadHistoryItemAsync(
            agent.SessionId!,
            args.WindowId?.Trim() ?? "",
            args.ItemId.Trim(),
            ct
        );
        if (found is null)
            return Error($"未找到条目 {args.ItemId}（或它不属于页 {args.WindowId}）。");

        var (item, full) = found.Value;
        var offset = Math.Max(args.OffsetChars ?? 0, 0);
        if (offset >= full.Length)
            return Error($"offset_chars 超出条目长度（{full.Length} 字符）。");

        var limit = Math.Clamp(args.LimitChars ?? 4000, 1, 200_000);
        var slice = full.Substring(offset, Math.Min(limit, full.Length - offset));
        var header =
            $"[窗口 {item.WindowId} | id {item.ItemId} | order {item.Order} | {item.Role}"
            + (string.IsNullOrEmpty(item.ToolName) ? "" : $" {item.ToolName}")
            + $" | 字符 {offset}-{offset + slice.Length}/{full.Length}]\n";

        return Text(header + slice);
    }

    private AgentToolResult<string>? RequireSession() =>
        string.IsNullOrEmpty(agent.SessionId)
            ? new AgentToolResult<string>
            {
                Content = [new TextContent("当前没有活动会话，无法检索历史。")],
                IsError = true,
            }
            : null;

    private static AgentToolResult<string> Text(string text) =>
        new() { Content = [new TextContent(text)] };

    private async Task<HashSet<string>> KnownWindowIdsAsync(string? windowId, CancellationToken ct)
    {
        var windows = await store.ListWindowsAsync(agent.SessionId!, ct);
        return windows.Select(w => w.WindowId).ToHashSet(StringComparer.Ordinal);
    }

    private static AgentToolResult<string> FormatItems(
        List<HistoryItemInfo> items,
        string? windowId,
        HashSet<string> knownWindows
    )
    {
        if (!string.IsNullOrEmpty(windowId) && !knownWindows.Contains(windowId))
            return new AgentToolResult<string>
            {
                Content = [new TextContent($"未找到页 {windowId}。")],
                IsError = true,
            };

        if (items.Count == 0)
            return new AgentToolResult<string>
            {
                Content = [new TextContent("没有匹配的历史条目。")],
            };

        var sb = new StringBuilder();
        sb.Append($"共 {items.Count} 条：\n\n");
        foreach (var item in items)
        {
            sb.Append(HistoryToolBase.FormatItem(item));
            sb.Append("\n\n");
        }
        return new AgentToolResult<string> { Content = [new TextContent(sb.ToString().TrimEnd())] };
    }
}
