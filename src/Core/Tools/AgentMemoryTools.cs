using System.ComponentModel;
using System.Text;
using Core.Data;
using Core.Shared;
using Core.Vector;
using static Core.Tools.AgentMemoryToolHelpers;

namespace Core.Tools;

// ════════════════════════════════════════════════════════════════
//  AgentMemory 工具：18 个 LLM 可调用的记忆操作工具（14 基础 + 4 §3.6 偏好任务导向）
//
//  设计：
//  - OwnerId 固定 "local"（单用户模式）
//  - NovelId 取自 Agent.CurrentNovelId（自动跟随当前小说）
//  - 全部 DeferLoading = true（通过 tool_search 按需发现，不预注入上下文）
//  - 错误时返回 IsError = true 的文本提示
// ════════════════════════════════════════════════════════════════

/// <summary>工具结果详情：通用文本+计数+记忆 ID 列表（用于自动钩子标注有用性）。</summary>
public sealed record AgentMemoryDetails
{
    public string Id { get; init; } = "";
    public int Count { get; init; }

    /// <summary>关联的记忆 ID 列表（memory_recall/memory_find_conflicts 返回，供 AgentMemoryHook 自动 mark_useful）。</summary>
    public List<string>? MemoryIds { get; init; }
}

// ────────────────────────────────────────────────────────────────
//  1. memory_store
// ────────────────────────────────────────────────────────────────

public sealed class StoreMemoryParams
{
    [Description("记忆内容文本")]
    public string Content { get; set; } = "";

    [Description("命名空间（default=偏好/rule=规则），默认 default")]
    public string Ns { get; set; } = "default";

    [Description(
        "作用域：novel（默认，当前小说内长期有效）| session（只在本次会话有效，换会话即失效）| "
            + "global（跨小说长期有效，如用户通用约定）"
    )]
    public string Scope { get; set; } = "novel";

    [Description("重要性 0.0~1.0，默认 0.5")]
    public double Importance { get; set; } = 0.5;

    [Description(
        "记忆类型：0=Explicit,1=ConfirmedInference,2=CandidateInference,3=NarrativeContract,4=CollaborativePattern"
    )]
    public int MemoryKind { get; set; } = 0;

    [Description("置信度 0.0~1.0，默认 1.0")]
    public double Confidence { get; set; } = 1.0;

    [Description("优先级（越大越优先），默认 0")]
    public int Priority { get; set; } = 0;
}

public sealed class StoreMemoryTool(Agent.Agent agent, AgentMemoryStore store)
    : AgentTool<StoreMemoryParams, AgentMemoryDetails>
{
    public override string Name => "memory_store";
    public override string Label => "存储记忆";
    public override string Description =>
        "将一条信息存入 agent 记忆（跨会话、按小说隔离的长期结论：偏好、规则、叙事契约、设定）。"
        + "content 为记忆文本，ns 区分命名空间：plot（情节/伏笔）、character（人物设定/弧光）、"
        + "setting（世界观/地点）、contract（叙事契约/承诺）、style（文风偏好）、"
        + "rule（显式规则）、default（通用偏好）。"
        + "importance 控制衰减权重，priority 控制排序优先级。"
        + "scope 决定作用域：session=只在本次会话有效（放本次会话进行中的结论，换会话即不可见，"
        + "不会污染后续会话）；novel=当前小说内长期有效（默认）；global=跨小说通用约定。"
        + "不要用长作用域记会话瞬态内容；逐步的过程细节留在对话历史里（history_* 可检索）。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        StoreMemoryParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.Content))
            return Err("content 不能为空。");

        var scope = args.Scope.Trim().ToLowerInvariant();
        if (scope is not ("novel" or "session" or "global"))
            return Err("scope 必须是 novel（当前小说）/ session（本次会话）/ global（跨小说）。");

        var novelId = scope == "global" ? null : agent.CurrentNovelId;
        var sessionId = scope == "session" ? agent.SessionId : null;
        if (scope != "global" && string.IsNullOrWhiteSpace(novelId) && sessionId is null)
            return Err("novel/session 作用域需要当前已打开的小说；跨小说结论请用 scope=global。");
        if (scope == "session" && string.IsNullOrWhiteSpace(sessionId))
            return Err("session 作用域需要当前活动会话。");

        var mem = await store.StoreAsync(
            args.Content,
            ownerId: "local",
            novelId: novelId,
            sessionId: sessionId,
            ns: args.Ns,
            importance: args.Importance,
            memoryKind: (AgentMemoryKind)args.MemoryKind,
            confidence: args.Confidence,
            priority: args.Priority,
            ct: ct
        );

        var scopeLabel = scope switch
        {
            "session" => "本次会话",
            "global" => "跨小说",
            _ => "当前小说",
        };
        return Ok(
            $"已存储记忆 [{mem.Id[..8]}]（{args.Ns}／{scopeLabel}）：{Trunc(args.Content, 60)}",
            mem.Id
        );
    }
}

// ────────────────────────────────────────────────────────────────
//  2. memory_recall
// ────────────────────────────────────────────────────────────────

public sealed class RecallMemoryParams
{
    [Description("自然语言查询")]
    public string Query { get; set; } = "";

    [Description("返回结果数上限，默认 5")]
    public int K { get; set; } = 5;

    [Description("命名空间（null=搜索所有），默认 default")]
    public string? Ns { get; set; } = "default";

    [Description("检索模式：hybrid/vector/keyword，默认 hybrid")]
    public string Mode { get; set; } = "hybrid";

    [Description("最低相似度阈值，默认 0.0")]
    public double MinSimilarity { get; set; } = 0.0;
}

public sealed class RecallMemoryTool(Agent.Agent agent, AgentMemoryStore store)
    : AgentTool<RecallMemoryParams, AgentMemoryDetails>
{
    public override string Name => "memory_recall";
    public override string Label => "召回记忆";
    public override string Description =>
        "从 agent 记忆中混合召回相关内容（向量+BM25+评分）。"
        + "query 为自然语言查询，mode 控制检索策略（hybrid=混合，vector=纯向量，keyword=纯关键词）。"
        + "ns 限定命名空间：plot（情节/伏笔）、character（人物）、setting（世界观）、"
        + "contract（叙事契约）、style（文风）、rule（显式规则）；不填搜索所有。"
        + "召回范围 = 全局记忆 + 当前小说（不含其它小说）；返回的是长期结论，"
        + "会话进度请查 todo / 上一页交接说明。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        RecallMemoryParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.Query))
            return Err("query 不能为空。");

        var results = await store.RecallAsync(
            args.Query,
            ownerId: "local",
            k: args.K,
            ns: args.Ns,
            novelId: agent.CurrentNovelId,
            sessionId: agent.SessionId,
            mode: args.Mode,
            minSimilarity: args.MinSimilarity,
            ct: ct
        );

        if (results.Count == 0)
            return Ok("无匹配记忆。", count: 0);

        var sb = new StringBuilder();
        sb.AppendLine($"召回 {results.Count} 条记忆：");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"  [{r.Memory.Id[..8]}] score={r.Score:F3} sim={r.Similarity:F3} "
                    + $"ns={r.Memory.Namespace} | {Trunc(r.Memory.Content, 80)}"
            );
        }
        return Ok(
            sb.ToString(),
            count: results.Count,
            memoryIds: results.ConvertAll(r => r.Memory.Id)
        );
    }
}

// ────────────────────────────────────────────────────────────────
//  3. memory_reinforce
// ────────────────────────────────────────────────────────────────

public sealed class ReinforceMemoryParams
{
    [Description("记忆 ID")]
    public string MemoryId { get; set; } = "";

    [Description("重要性提升量，默认 0.1")]
    public double Boost { get; set; } = 0.1;
}

public sealed class ReinforceMemoryTool(AgentMemoryStore store)
    : AgentTool<ReinforceMemoryParams, AgentMemoryDetails>
{
    public override string Name => "memory_reinforce";
    public override string Label => "强化记忆";
    public override string Description =>
        "强化一条记忆：提升重要性（上限 1.0）并重置衰退时钟（模拟间隔重复）。"
        + "memoryId 为目标记忆 ID，boost 为重要性提升量。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        ReinforceMemoryParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.MemoryId))
            return Err("memoryId 不能为空。");

        var mem = await store.ReinforceAsync(args.MemoryId, args.Boost, ct);
        return mem is null
            ? Err($"未找到记忆 {args.MemoryId}。")
            : Ok($"已强化 [{mem.Id[..8]}]：importance={mem.Importance:F2}", mem.Id);
    }
}

// ────────────────────────────────────────────────────────────────
//  4. memory_forget
// ────────────────────────────────────────────────────────────────

public sealed class ForgetMemoryParams
{
    [Description("记忆 ID（删除指定记忆），不填则按条件批量删除")]
    public string? MemoryId { get; set; }

    [Description("命名空间过滤（批量删除时使用）")]
    public string? Ns { get; set; }

    [Description("删除多少秒前的记忆（不填=不限时间）")]
    public double? OlderThanSeconds { get; set; }
}

public sealed class ForgetMemoryTool(AgentMemoryStore store)
    : AgentTool<ForgetMemoryParams, AgentMemoryDetails>
{
    public override string Name => "memory_forget";
    public override string Label => "遗忘记忆";
    public override string Description =>
        "删除记忆。memoryId 指定单条删除；不填则按 ns（命名空间）或 olderThanSeconds 批量删除。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        ForgetMemoryParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var count = await store.ForgetAsync(
            memoryId: args.MemoryId,
            ownerId: "local",
            ns: args.Ns,
            olderThanSeconds: args.OlderThanSeconds,
            ct: ct
        );
        return Ok($"已删除 {count} 条记忆。", count: count);
    }
}

// ────────────────────────────────────────────────────────────────
//  5. memory_mark_useful
// ────────────────────────────────────────────────────────────────

public sealed class MarkUsefulParams
{
    [Description("记忆 ID")]
    public string MemoryId { get; set; } = "";

    [Description("权重（正向反馈强度），默认 1.0")]
    public double Weight { get; set; } = 1.0;
}

public sealed class MarkUsefulTool(AgentMemoryStore store)
    : AgentTool<MarkUsefulParams, AgentMemoryDetails>
{
    public override string Name => "memory_mark_useful";
    public override string Label => "标记有用";
    public override string Description =>
        "标记一条记忆为有用（在任务中被召回且帮助完成了任务）。"
        + "usefulness 评分提升，未来召回时排序更靠前。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        MarkUsefulParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.MemoryId))
            return Err("memoryId 不能为空。");

        var mem = await store.MarkUsefulAsync(args.MemoryId, args.Weight, ct);
        return mem is null
            ? Err($"未找到记忆 {args.MemoryId}。")
            : Ok($"已标记有用 [{mem.Id[..8]}]：usefulness={mem.Usefulness:F2}", mem.Id);
    }
}

// ────────────────────────────────────────────────────────────────
//  6. memory_mark_useless
// ────────────────────────────────────────────────────────────────

public sealed class MarkUselessTool(AgentMemoryStore store)
    : AgentTool<MarkUsefulParams, AgentMemoryDetails>
{
    public override string Name => "memory_mark_useless";
    public override string Label => "标记无用";
    public override string Description =>
        "标记一条记忆为无用（被召回但未帮助完成任务）。"
        + "usefulness 评分下降，未来召回时排序更靠后。参数同 memory_mark_useful。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        MarkUsefulParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.MemoryId))
            return Err("memoryId 不能为空。");

        var mem = await store.MarkUselessAsync(args.MemoryId, args.Weight, ct);
        return mem is null
            ? Err($"未找到记忆 {args.MemoryId}。")
            : Ok($"已标记无用 [{mem.Id[..8]}]：usefulness={mem.Usefulness:F2}", mem.Id);
    }
}

// ────────────────────────────────────────────────────────────────
//  7. memory_find_conflicts
// ────────────────────────────────────────────────────────────────

public sealed class FindConflictsParams
{
    [Description("查询文本（检测与该查询相关的记忆间冲突），不填则检测全部")]
    public string? Query { get; set; }

    [Description("命名空间，默认 default")]
    public string? Ns { get; set; } = "default";

    [Description("返回结果数上限，默认 50")]
    public int K { get; set; } = 50;
}

public sealed class FindConflictsTool(Agent.Agent agent, AgentMemoryStore store)
    : AgentTool<FindConflictsParams, AgentMemoryDetails>
{
    public override string Name => "memory_find_conflicts";
    public override string Label => "检测冲突";
    public override string Description =>
        "检测记忆间的结构性矛盾（否定矛盾、数值矛盾、值替换矛盾等）。"
        + "返回冲突列表，每条包含矛盾类型、双方 ID 和证据。由 LLM 做最终判断。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        FindConflictsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var conflicts = await store.FindConflictsAsync(
            args.Query,
            ownerId: "local",
            ns: args.Ns,
            novelId: agent.CurrentNovelId,
            k: args.K,
            ct: ct
        );

        if (conflicts.Count == 0)
            return Ok("未检测到冲突。", count: 0);

        var sb = new StringBuilder();
        sb.AppendLine($"检测到 {conflicts.Count} 处冲突：");
        foreach (var c in conflicts)
        {
            sb.AppendLine(
                $"  [{c.Confidence}] {c.MemoryA.Id[..8]} vs {c.MemoryB.Id[..8]}：{c.Reason}"
            );
        }
        return Ok(
            sb.ToString(),
            count: conflicts.Count,
            memoryIds: conflicts
                .SelectMany(c => new[] { c.MemoryA.Id, c.MemoryB.Id })
                .Distinct()
                .ToList()
        );
    }
}

// ────────────────────────────────────────────────────────────────
//  8. memory_resolve_conflict
// ────────────────────────────────────────────────────────────────

public sealed class ResolveConflictParams
{
    [Description("保留的记忆 ID")]
    public string KeepId { get; set; } = "";

    [Description("删除的记忆 ID 列表")]
    public string[] DropIds { get; set; } = [];

    [Description("解决原因（可选）")]
    public string Reason { get; set; } = "";
}

public sealed class ResolveConflictTool(AgentMemoryStore store)
    : AgentTool<ResolveConflictParams, AgentMemoryDetails>
{
    public override string Name => "memory_resolve_conflict";
    public override string Label => "解决冲突";
    public override string Description =>
        "解决检测到的冲突：保留 keepId 指定的记忆，删除 dropIds 中的矛盾记忆。"
        + "解决后写入 journal 日志。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        ResolveConflictParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.KeepId))
            return Err("keepId 不能为空。");
        if (args.DropIds is null || args.DropIds.Length == 0)
            return Err("dropIds 不能为空。");

        await store.ResolveConflictAsync(args.KeepId, args.DropIds, args.Reason, ct);
        return Ok($"冲突已解决：保留 [{args.KeepId[..8]}]，删除 {args.DropIds.Length} 条。");
    }
}

// ────────────────────────────────────────────────────────────────
//  9. memory_snapshot
// ────────────────────────────────────────────────────────────────

public sealed class SnapshotParams
{
    [Description("快照标签名（如 'ch5-before-edit'）")]
    public string Label { get; set; } = "";
}

public sealed class SnapshotTool(AgentMemoryStore store)
    : AgentTool<SnapshotParams, AgentMemoryDetails>
{
    public override string Name => "memory_snapshot";
    public override string Label => "时间快照";
    public override string Description =>
        "记录一个命名时间点快照。之后可用 memory_as_of 查看该时间点的记忆状态，"
        + "或用 memory_diff 对比两个快照之间的变化。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        SnapshotParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.Label))
            return Err("label 不能为空。");

        await store.SnapshotAsync(args.Label, ct);
        return Ok($"已创建快照 [{args.Label}]。", id: args.Label);
    }
}

// ────────────────────────────────────────────────────────────────
//  10. memory_as_of
// ────────────────────────────────────────────────────────────────

public sealed class AsOfParams
{
    [Description("快照标签名")]
    public string Label { get; set; } = "";

    [Description("小说 ID（不填=使用当前小说；null 全局记忆包含在内）")]
    public string? NovelId { get; set; }
}

public sealed class AsOfTool(Agent.Agent agent, AgentMemoryStore store)
    : AgentTool<AsOfParams, AgentMemoryDetails>
{
    public override string Name => "memory_as_of";
    public override string Label => "历史查看";
    public override string Description =>
        "查看某个时间点快照的记忆状态。label 为 memory_snapshot 创建的标签名。"
        + "novelId 不填时限定当前小说（含全局记忆），避免跨小说混入。"
        + "返回该时间点的记忆列表。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        AsOfParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.Label))
            return Err("label 不能为空。");

        var mems = await store.AsOfAsync(args.Label, args.NovelId ?? agent.CurrentNovelId, ct);
        if (mems.Count == 0)
            return Ok($"快照 [{args.Label}] 无记忆记录。", count: 0);

        var sb = new StringBuilder();
        sb.AppendLine($"快照 [{args.Label}]：{mems.Count} 条记忆");
        foreach (var m in mems)
            sb.AppendLine($"  [{m.Id[..8]}] {m.Namespace} | {Trunc(m.Content, 80)}");
        return Ok(sb.ToString(), count: mems.Count);
    }
}

// ────────────────────────────────────────────────────────────────
//  11. memory_diff
// ────────────────────────────────────────────────────────────────

public sealed class DiffParams
{
    [Description("起始快照标签")]
    public string BeforeLabel { get; set; } = "";

    [Description("结束快照标签（不填=当前状态）")]
    public string? AfterLabel { get; set; }

    [Description("小说 ID（不填=使用当前小说；null 全局记忆包含在内）")]
    public string? NovelId { get; set; }
}

public sealed class DiffTool(Agent.Agent agent, AgentMemoryStore store)
    : AgentTool<DiffParams, AgentMemoryDetails>
{
    public override string Name => "memory_diff";
    public override string Label => "时间对比";
    public override string Description =>
        "对比两个时间点之间的记忆变化。返回新增、删除、修改的记忆列表。"
        + "afterLabel 不填时对比到当前状态。novelId 不填时限定当前小说（含全局记忆）。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        DiffParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.BeforeLabel))
            return Err("beforeLabel 不能为空。");

        var diff = await store.DiffAsync(
            args.BeforeLabel,
            args.AfterLabel,
            args.NovelId ?? agent.CurrentNovelId,
            ct
        );
        var totalChanges = diff.Added.Count + diff.Removed.Count + diff.Changed.Count;

        if (totalChanges == 0)
            return Ok("无变化。", count: 0);

        var sb = new StringBuilder();
        sb.AppendLine(
            $"变化：+{diff.Added.Count} 新增 -{diff.Removed.Count} 删除 ~{diff.Changed.Count} 变更"
        );
        foreach (var m in diff.Added)
            sb.AppendLine($"  + [{m.Id[..8]}] {Trunc(m.Content, 70)}");
        foreach (var m in diff.Removed)
            sb.AppendLine($"  - [{m.Id[..8]}] {Trunc(m.Content, 70)}");
        foreach (var ch in diff.Changed)
            sb.AppendLine(
                $"  ~ [{ch.Record.Id[..8]}] imp:{ch.OldImportance:F2}→{ch.Record.Importance:F2} | {Trunc(ch.Record.Content, 60)}"
            );
        return Ok(sb.ToString(), count: totalChanges);
    }
}

// ────────────────────────────────────────────────────────────────
//  12. memory_pack
// ────────────────────────────────────────────────────────────────

public sealed class PackParams
{
    [Description("自然语言查询")]
    public string Query { get; set; } = "";

    [Description("token 预算上限，默认 800")]
    public int BudgetTokens { get; set; } = 800;

    [Description("命名空间，默认 default")]
    public string? Ns { get; set; } = "default";

    [Description("检索模式：hybrid/vector/keyword，默认 hybrid")]
    public string Mode { get; set; } = "hybrid";
}

public sealed class PackTool(Agent.Agent agent, AgentMemoryStore store)
    : AgentTool<PackParams, AgentMemoryDetails>
{
    public override string Name => "memory_pack";
    public override string Label => "预算打包";
    public override string Description =>
        "在 token 预算内打包最相关的记忆（0/1 背包 + 近重复抑制）。"
        + "返回按评分排序的记忆列表，总 token 不超过 budgetTokens。"
        + "用于在有限上下文窗口内最大化信息密度。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        PackParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.Query))
            return Err("query 不能为空。");

        var results = await store.PackAsync(
            args.Query,
            ownerId: "local",
            budgetTokens: args.BudgetTokens,
            ns: args.Ns,
            novelId: agent.CurrentNovelId,
            mode: args.Mode,
            ct: ct
        );

        if (results.Count == 0)
            return Ok("无匹配记忆。", count: 0);

        var totalTokens = results.Sum(r => r.Tokens);
        var sb = new StringBuilder();
        sb.AppendLine(
            $"打包 {results.Count} 条记忆（~{totalTokens} tokens / 预算 {args.BudgetTokens}）："
        );
        foreach (var r in results)
            sb.AppendLine(
                $"  [{r.Memory.Id[..8]}] {r.Tokens}t score={r.Score:F3} | {Trunc(r.Memory.Content, 70)}"
            );
        return Ok(sb.ToString(), count: results.Count);
    }
}

// ────────────────────────────────────────────────────────────────
//  13. memory_consolidate
// ────────────────────────────────────────────────────────────────

public sealed class ConsolidateParams
{
    [Description("命名空间（不填=所有）")]
    public string? Ns { get; set; }

    [Description("余弦相似度阈值，默认 0.92")]
    public double Threshold { get; set; } = 0.92;
}

public sealed class ConsolidateTool(AgentMemoryStore store)
    : AgentTool<ConsolidateParams, AgentMemoryDetails>
{
    public override string Name => "memory_consolidate";
    public override string Label => "合并去重";
    public override string Description =>
        "合并近重复记忆：余弦相似度 ≥ threshold 的两条记忆合并为一条。"
        + "减少冗余存储，保持记忆库整洁。返回合并掉的条数。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        ConsolidateParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var merged = await store.ConsolidateAsync(
            ownerId: "local",
            ns: args.Ns,
            threshold: args.Threshold,
            ct: ct
        );
        return Ok($"合并了 {merged} 条近重复记忆。", count: merged);
    }
}

// ────────────────────────────────────────────────────────────────
//  14. memory_analyze_style（VEC-LOCAL 域三：候选推断）
// ────────────────────────────────────────────────────────────────

public sealed class AnalyzeStyleParams
{
    [Description("分析最近多少章，默认 10，范围 3~30")]
    public int RecentChapters { get; set; } = 10;
}

/// <summary>
/// 分析最近章节的写作模式，生成候选风格推断（VEC-LOCAL 域三）。
/// 候选推断绝不自动注入 prompt（RetrievalKernel 已过滤 CandidateInference）；
/// 必须展示给用户，经确认后用 memory_store 以 memoryKind=ConfirmedInference 保存。
/// </summary>
public sealed class AnalyzeStyleTool(Agent.Agent agent, INovelStore store)
    : AgentTool<AnalyzeStyleParams, AgentMemoryDetails>
{
    private const int MaxChapters = 30;

    public override string Name => "memory_analyze_style";
    public override string Label => "风格候选推断";
    public override string Description =>
        "分析用户最近章节的写作模式（段落/句长偏好、标点习惯、改写痕迹），"
        + "生成候选风格推断供用户确认。每个维度至少 3 章一致观察才会产生，置信度不超过 0.6。"
        + "返回的候选不会自动生效；请逐条展示给用户，确认后用 memory_store 以 memoryKind=1（ConfirmedInference）保存。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        AnalyzeStyleParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return Err("当前没有打开的小说项目。");

        var novel = await store.GetNovel(novelId, ct);
        if (novel is null)
            return Err($"小说项目 {novelId} 不存在。");

        var chapters = await store.GetChapters(novelId, ct);
        ThrowIfCancelled(ct);

        var take = Math.Clamp(args.RecentChapters, 3, MaxChapters);
        var recent = chapters
            .Where(c => !string.IsNullOrEmpty(c.Content))
            .OrderByDescending(c => c.Order)
            .Take(take)
            .ToList();

        if (recent.Count < 3)
            return Err(
                $"有内容的章节不足 3 章（当前 {recent.Count} 章），无法产生可靠的候选推断。"
            );

        var profile = WritingProfile.FromNovel(novel);
        var candidates = new CandidateInferenceAnalyzer().Analyze(profile, recent);
        if (candidates.Count == 0)
            return Ok("未观察到稳定的写作模式，暂无候选推断。", count: 0);

        var lines = candidates.Select(c =>
            $"- [{c.Id}] {c.Text}（置信度 {c.Confidence:0.00}，观察 {c.ObservationCount} 次）\n"
            + $"  证据：{c.EvidenceDescription}"
        );
        return Ok(
            $"发现 {candidates.Count} 条候选风格推断，请逐条向用户确认；确认后用 memory_store 以 memoryKind=1 保存：\n"
                + string.Join("\n", lines),
            count: candidates.Count
        );
    }
}

// ════════════════════════════════════════════════════════════════
//  15. recall_preference — 当前任务偏好召回（VEC-LOCAL §3.4）
// ════════════════════════════════════════════════════════════════

public sealed class RecallPreferenceParams
{
    [Description("可选查询文本；为空时只返回显式规则")]
    public string Query { get; set; } = "";

    [Description("返回结果数上限，默认 10")]
    public int TopK { get; set; } = 10;
}

/// <summary>
/// 召回用户偏好：显式规则（ns=rule，keyword/全量，priorityBonus=2000）优先，
/// 偏好（ns=default，hybrid，过滤候选推断）次之（VEC-LOCAL §3.4）。
/// 候选推断（CandidateInference）绝不注入。
/// </summary>
public sealed class RecallPreferenceTool(Agent.Agent agent, RetrievalKernel kernel)
    : AgentTool<RecallPreferenceParams, AgentMemoryDetails>
{
    public override string Name => "recall_preference";
    public override string Label => "召回偏好";
    public override string Description =>
        "召回用户明确表达的写作偏好与规则（如“偏好短句”“不要用文言文”）。"
        + "规则优先于语义相似度返回；未确认的候选推断不会被注入。"
        + "query 可传当前任务描述以匹配相关偏好，留空则返回全部显式规则。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        RecallPreferenceParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (kernel.AgentMemory is null)
            return Err("记忆存储未初始化（降级模式），无法召回偏好。");

        var results = await kernel.SearchUserMemoryAsync(
            "local",
            agent.CurrentNovelId,
            string.IsNullOrWhiteSpace(args.Query) ? null : args.Query,
            args.TopK,
            agent.SessionId,
            ct
        );

        if (results.Count == 0)
            return Ok("暂无匹配的偏好或规则。", count: 0);

        var sb = new StringBuilder();
        sb.AppendLine($"召回 {results.Count} 条偏好/规则：");
        foreach (var r in results)
        {
            var kind =
                r.MetadataSummary?.TryGetValue("memory-kind", out var k) == true
                    ? k?.ToString()
                    : null;
            var scope = r.NovelId is null ? "全局" : "当前小说";
            sb.AppendLine($"  [{scope}][{kind}] score={r.Score:F1} | {Trunc(r.Text, 80)}");
        }
        return Ok(
            sb.ToString(),
            count: results.Count,
            memoryIds: results.ConvertAll(r => r.SourceEntityId ?? "")
        );
    }
}

// ────────────────────────────────────────────────────────────────
//  16. save_preference — 保存显式偏好（必须指定 scope 与确认语义）
// ────────────────────────────────────────────────────────────────

public sealed class SavePreferenceParams
{
    [Description("偏好内容文本，如“偏好短句”“不用文言文”")]
    public string Content { get; set; } = "";

    [Description("作用域：global=全局（所有小说有效）；novel=仅当前小说有效。默认 global")]
    public string Scope { get; set; } = "global";

    [Description(
        "确认语义：explicit=用户明确表达（默认）；confirmed=用户确认的推断。"
            + "候选推断不得用本工具保存"
    )]
    public string Source { get; set; } = "explicit";

    [Description("优先级（越大越优先，默认 0）")]
    public int Priority { get; set; } = 0;
}

/// <summary>
/// 保存显式偏好：必须指定作用域（global/novel）与确认语义（explicit/confirmed）。
/// 仅保存用户明确表达或经确认的偏好，禁止未经确认的候选推断（§3.1 信任层级）。
/// </summary>
public sealed class SavePreferenceTool(Agent.Agent agent, AgentMemoryStore store)
    : AgentTool<SavePreferenceParams, AgentMemoryDetails>
{
    public override string Name => "save_preference";
    public override string Label => "保存偏好";
    public override string Description =>
        "保存用户明确表达的写作偏好或规则。scope 指定全局/当前小说；"
        + "source=explicit（用户明确表达）或 confirmed（用户确认的推断）。"
        + "未经用户确认的推断不得保存为偏好。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        SavePreferenceParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.Content))
            return Err("content 不能为空。");

        var scope = args.Scope.Trim().ToLowerInvariant();
        if (scope is not ("global" or "novel"))
            return Err("scope 必须是 global（全局）或 novel（当前小说）。");

        var source = args.Source.Trim().ToLowerInvariant();
        if (source is not ("explicit" or "confirmed"))
            return Err("source 必须是 explicit（用户明确表达）或 confirmed（用户确认的推断）。");

        var novelId = scope == "novel" ? agent.CurrentNovelId : null;
        if (scope == "novel" && string.IsNullOrWhiteSpace(novelId))
            return Err("novel 作用域需要当前已打开的小说。");

        var mem = await store.StoreAsync(
            args.Content,
            ownerId: "local",
            novelId: novelId,
            ns: "default",
            memoryKind: source == "explicit"
                ? AgentMemoryKind.Explicit
                : AgentMemoryKind.ConfirmedInference,
            confidence: source == "explicit" ? 1.0 : 0.6,
            priority: args.Priority,
            ct: ct
        );

        return Ok($"已保存偏好 [{mem.Id[..8]}]（{scope}）：{Trunc(args.Content, 60)}", mem.Id);
    }
}

// ────────────────────────────────────────────────────────────────
//  17. list_preferences — 审阅/纠错（可靠枚举）
// ────────────────────────────────────────────────────────────────

public sealed class ListPreferencesParams
{
    [Description("作用域过滤：all=全部；global=仅全局；novel=仅当前小说。默认 all")]
    public string Scope { get; set; } = "all";
}

/// <summary>
/// 列出用户偏好与规则（可靠枚举，按 ID），用于审阅、纠错与撤销。
/// 只列出用户明确表达/确认的条目；候选推断不入偏好清单（§3.1）。
/// </summary>
public sealed class ListPreferencesTool(Agent.Agent agent, AgentMemoryStore store)
    : AgentTool<ListPreferencesParams, AgentMemoryDetails>
{
    public override string Name => "list_preferences";
    public override string Label => "列出偏好";
    public override string Description =>
        "列出所有已保存的偏好与规则（含 ID、作用域、类型），供审阅和纠错。"
        + "delete_preference 可用返回的 ID 删除指定条目。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        ListPreferencesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var scope = args.Scope.Trim().ToLowerInvariant();
        if (scope is not ("all" or "global" or "novel"))
            return Err("scope 必须是 all / global / novel。");

        // 可见集合 = 全局（novel_id NULL）∪ 当前小说：一次查询即可拿到，
        // 且不会像旧实现那样把「其它小说」的记忆一并捞出来（novelId=null 曾表示"不过滤"）。
        var rules = await store.GetAllAsync(
            "local",
            ns: "rule",
            novelId: agent.CurrentNovelId,
            ct: ct
        );
        var prefs = await store.GetAllAsync(
            "local",
            ns: "default",
            novelId: agent.CurrentNovelId,
            ct: ct
        );

        var all = rules
            .Concat(prefs)
            .Where(m =>
                m.MemoryKind is AgentMemoryKind.Explicit or AgentMemoryKind.ConfirmedInference
            )
            .DistinctBy(m => m.Id)
            .Where(m =>
                scope switch
                {
                    "global" => m.NovelId is null,
                    "novel" => m.NovelId is not null,
                    _ => true,
                }
            )
            .OrderByDescending(m => m.NovelId is null)
            .ThenByDescending(m => m.Priority)
            .ThenByDescending(m => m.CreatedAt)
            .ToList();

        if (all.Count == 0)
            return Ok("暂无已保存的偏好或规则。", count: 0);

        var sb = new StringBuilder();
        sb.AppendLine($"共 {all.Count} 条偏好/规则：");
        foreach (var m in all)
        {
            var scopeLabel = m.NovelId is null ? "全局" : "小说";
            var kindLabel = m.MemoryKind == AgentMemoryKind.Explicit ? "规则/偏好" : "已确认推断";
            sb.AppendLine($"  [{m.Id[..8]}] [{scopeLabel}][{kindLabel}] {Trunc(m.Content, 70)}");
        }
        return Ok(sb.ToString(), count: all.Count, memoryIds: all.ConvertAll(m => m.Id));
    }
}

// ────────────────────────────────────────────────────────────────
//  18. delete_preference — 撤销（ID 精确删除）
// ────────────────────────────────────────────────────────────────

public sealed class DeletePreferenceParams
{
    [Description("要删除的偏好/规则 ID（list_preferences 返回的 ID，可用前 8 位）")]
    public string Id { get; set; } = "";
}

/// <summary>
/// 按 ID 删除偏好或规则：撤销后立即不再注入（§4.4 安全红线③）。
/// </summary>
public sealed class DeletePreferenceTool(AgentMemoryStore store)
    : AgentTool<DeletePreferenceParams, AgentMemoryDetails>
{
    public override string Name => "delete_preference";
    public override string Label => "删除偏好";
    public override string Description =>
        "按 ID 删除一条偏好或规则（撤销）。ID 来自 list_preferences。"
        + "删除后该偏好立即不再参与召回与注入。";
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<AgentMemoryDetails>> ExecuteAsync(
        string toolCallId,
        DeletePreferenceParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AgentMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.Id))
            return Err("id 不能为空。");

        // 支持前 8 位短 ID 解析为完整 ID
        var memory = await store.GetAsync(args.Id, ct);
        if (memory is null && args.Id.Length == 8)
        {
            // 短 ID 解析需要跨小说查找（id 全局唯一；用户可能删任意作用域的记忆）
            var all = await store.GetAllAsync(
                "local",
                novelId: null,
                scope: MemoryVisibility.All,
                ct: ct
            );
            var matches = all.Where(m => m.Id.StartsWith(args.Id, StringComparison.Ordinal))
                .ToList();
            switch (matches.Count)
            {
                case 1:
                    memory = matches[0];
                    break;
                case > 1:
                    return Err(
                        $"短 ID {args.Id} 匹配多条记录，请用 list_preferences 确认完整 ID。"
                    );
            }
        }

        if (memory is null)
            return Err($"未找到 ID={args.Id} 的偏好/规则。");

        var deleted = await store.ForgetAsync(memory.Id, ownerId: "local", ct: ct);
        return deleted > 0
            ? Ok(
                $"已删除偏好 [{args.Id[..Math.Min(8, args.Id.Length)]}]：{Trunc(memory.Content, 60)}"
            )
            : Err($"删除失败：ID={args.Id} 不存在。");
    }
}

// ────────────────────────────────────────────────────────────────
//  辅助
// ────────────────────────────────────────────────────────────────

internal static class AgentMemoryToolHelpers
{
    public static AgentToolResult<AgentMemoryDetails> Err(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };

    public static AgentToolResult<AgentMemoryDetails> Ok(
        string text,
        string? id = null,
        int count = 0,
        List<string>? memoryIds = null
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new AgentMemoryDetails
            {
                Id = id ?? "",
                Count = count,
                MemoryIds = memoryIds,
            },
        };

    public static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
