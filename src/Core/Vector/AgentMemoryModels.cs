using System.Text.Json;

namespace Core.Vector;

/// <summary>Agent 记忆来源类型（复用 UserMemoryRecord 的 MemorySourceKind 枚举但扩展）。</summary>
public enum AgentMemoryKind
{
    /// <summary>用户明确表达的规则或偏好。</summary>
    Explicit = 0,

    /// <summary>经确认的推断。</summary>
    ConfirmedInference = 1,

    /// <summary>候选推断（未确认，待验证）。</summary>
    CandidateInference = 2,

    /// <summary>叙事契约：伏笔、角色弧光、主题承诺等。（Phase 3 启用）</summary>
    NarrativeContract = 3,

    /// <summary>协作记忆：AI 与作者的共创模式。（Phase 3 启用）</summary>
    CollaborativePattern = 4,
}

/// <summary>Agent 记忆作用域。</summary>
public enum AgentMemoryScope
{
    /// <summary>全局，适用于所有小说。</summary>
    Global = 0,

    /// <summary>仅适用于特定小说。</summary>
    PerNovel = 1,

    /// <summary>仅适用于特定会话。</summary>
    PerSession = 2,
}

/// <summary>Agent 记忆的一条记录。</summary>
public sealed record MemoryRecord
{
    /// <summary>稳定唯一 ID（UUID hex）。</summary>
    public required string Id { get; init; }

    /// <summary>用户归属。</summary>
    public required string OwnerId { get; init; }

    /// <summary>小说覆盖时非空。</summary>
    public string? NovelId { get; init; }

    /// <summary>
    /// 会话级记忆非空（scope=session）：只在写入它的那个会话可见，换会话即不可见，
    /// 用来放"本次会话进行中的结论"——跨会话长期结论不要用 session 作用域。
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>逻辑分组（如 "style", "plot", "character", "setting", "default"）。</summary>
    public string Namespace { get; init; } = "default";

    /// <summary>记忆文本内容。</summary>
    public required string Content { get; init; }

    /// <summary>重要性 [0, 1]，写入时设定，可通过 Reinforce 提升。</summary>
    public double Importance { get; init; } = 0.5;

    /// <summary>结构化 metadata JSON。</summary>
    public string MetadataJson { get; init; } = "{}";

    /// <summary>创建时间（Unix 时间戳）。</summary>
    public double CreatedAt { get; init; }

    /// <summary>最后访问时间（Unix 时间戳，用于 recency 衰退计算）。</summary>
    public double? LastAccessedAt { get; init; }

    /// <summary>召回次数。</summary>
    public int AccessCount { get; init; }

    /// <summary>有用性有符号累加器（sigmoid 压缩到 [0,1] 后参与评分）。</summary>
    public double Usefulness { get; init; }

    /// <summary>记忆类型。</summary>
    public AgentMemoryKind MemoryKind { get; init; } = AgentMemoryKind.Explicit;

    /// <summary>置信度 [0, 1]。</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>优先级覆盖（显式规则高于推断）。</summary>
    public int Priority { get; init; }

    /// <summary>过期时间（Unix 时间戳，可选 TTL）。</summary>
    public double? ExpiresAt { get; init; }

    /// <summary>来源引用列表（JSON）。</summary>
    public string? EvidenceRefsJson { get; init; }

    /// <summary>解析 metadata 为字典。</summary>
    public Dictionary<string, object?>? ParseMetadata() =>
        string.IsNullOrEmpty(MetadataJson) || MetadataJson == "{}"
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(MetadataJson);

    /// <summary>当前是否已过期。</summary>
    public bool IsExpired(double now) => now >= ExpiresAt;
}

/// <summary>召回结果：带完整评分明细。</summary>
public sealed record RecallResult
{
    /// <summary>命中的记忆记录。</summary>
    public required MemoryRecord Memory { get; init; }

    /// <summary>最终综合评分（有权重加权和）。</summary>
    public double Score { get; init; }

    /// <summary>文本相似度分量（余弦 + BM25 混合）。</summary>
    public double Similarity { get; init; }

    /// <summary>时间衰退因子 [0, 1]。</summary>
    public double Recency { get; init; }

    /// <summary>向量余弦相似度分量（纯）。</summary>
    public double VectorScore { get; init; }

    /// <summary>BM25 词法相似度分量（归一化）。</summary>
    public double KeywordScore { get; init; }

    /// <summary>预估 token 数（由 Pack 填充）。</summary>
    public int Tokens { get; init; }

    /// <summary>返回评分明细的文本解释。</summary>
    public string ExplainText() =>
        $"score {Score:F4} = similarity {Similarity:F4} (vector {VectorScore:F4}, keyword {KeywordScore:F4})"
        + $" + recency {Recency:F4} + importance {Memory.Importance:F2}"
        + $" | recalled {Memory.AccessCount}x";
}

/// <summary>Agent 记忆评分配置。</summary>
public sealed record AgentMemoryScoringConfig
{
    /// <summary>相似度权重（默认 0.65）。</summary>
    public double WeightSimilarity { get; init; } = 0.65;

    /// <summary>时间衰退权重（默认 0.15）。</summary>
    public double WeightRecency { get; init; } = 0.15;

    /// <summary>重要性权重（默认 0.10）。</summary>
    public double WeightImportance { get; init; } = 0.10;

    /// <summary>有用性权重（默认 0.10）。</summary>
    public double WeightUsefulness { get; init; } = 0.10;

    /// <summary>衰退半衰期（秒，默认 7 天）。</summary>
    public double HalfLifeSeconds { get; init; } = 7.0 * 24 * 3600;

    /// <summary>混合模式中 BM25 的权重（默认 0.35，余弦占 0.65）。</summary>
    public double KeywordWeight { get; init; } = 0.35;

    /// <summary>获取归一化后的权重（确保总和为 1）。</summary>
    public (double Sim, double Rec, double Imp, double Use) NormalizedWeights
    {
        get
        {
            var total = WeightSimilarity + WeightRecency + WeightImportance + WeightUsefulness;
            if (total <= 0)
                return (0.25, 0.25, 0.25, 0.25);
            return (
                WeightSimilarity / total,
                WeightRecency / total,
                WeightImportance / total,
                WeightUsefulness / total
            );
        }
    }
}

/// <summary>日志条目（用于时间旅行 replay）。</summary>
public sealed record JournalEntry
{
    /// <summary>自增序号。</summary>
    public long Seq { get; init; }

    /// <summary>操作时间戳。</summary>
    public double Timestamp { get; init; }

    /// <summary>操作类型：store / forget / reinforce / feedback / resolve。</summary>
    public required string Op { get; init; }

    /// <summary>操作目标记忆 ID。</summary>
    public required string MemoryId { get; init; }

    /// <summary>命名空间。</summary>
    public string Namespace { get; init; } = "default";

    /// <summary>操作载荷 JSON。</summary>
    public required string PayloadJson { get; init; }
}

/// <summary>冲突检测结果（Phase 2 启用）。</summary>
public sealed record Conflict
{
    /// <summary>冲突置信度。</summary>
    public required string Confidence { get; init; } // "possible" | "likely"

    /// <summary>记忆 A。</summary>
    public required MemoryRecord MemoryA { get; init; }

    /// <summary>记忆 B。</summary>
    public required MemoryRecord MemoryB { get; init; }

    /// <summary>冲突原因描述。</summary>
    public string? Reason { get; init; }

    /// <summary>冲突标记词列表。</summary>
    public List<string> ChangeMarkers { get; init; } = [];

    /// <summary>本地判定结论：contradiction / refinement / duplicate / unrelated；未判定为 null。</summary>
    public string? JudgeVerdict { get; init; }

    /// <summary>本地判定概率（选中项 probabilities[selected]，缺则 confidence）；未判定为 null。</summary>
    public double? JudgeProbability { get; init; }

    /// <summary>可读描述。</summary>
    public string Describe()
    {
        var a = MemoryA.Content.Length > 30 ? MemoryA.Content[..30] + "..." : MemoryA.Content;
        var b = MemoryB.Content.Length > 30 ? MemoryB.Content[..30] + "..." : MemoryB.Content;
        return $"Conflict({Confidence}): '{a}' vs '{b}'";
    }
}

/// <summary>Diff 结果（Phase 2 启用）。</summary>
public sealed record DiffResult
{
    /// <summary>新增的记忆。</summary>
    public List<MemoryRecord> Added { get; init; } = [];

    /// <summary>移除的记忆。</summary>
    public List<MemoryRecord> Removed { get; init; } = [];

    /// <summary>发生变化的记忆（重要性变更）。</summary>
    public List<(MemoryRecord Record, double OldImportance)> Changed { get; init; } = [];
}

/// <summary>记忆统计。</summary>
public sealed record MemoryStats
{
    /// <summary>记忆总数。</summary>
    public int TotalCount { get; init; }

    /// <summary>按命名空间的分布。</summary>
    public Dictionary<string, int> PerNamespace { get; init; } = [];

    /// <summary>按类型的分布。</summary>
    public Dictionary<string, int> PerKind { get; init; } = [];

    /// <summary>平均重要性。</summary>
    public double AvgImportance { get; init; }

    /// <summary>平均有用性累加器。</summary>
    public double AvgUsefulness { get; init; }

    /// <summary>已过期的记忆数。</summary>
    public int ExpiredCount { get; init; }
}
