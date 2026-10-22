namespace Core.Vector;

/// <summary>用户记忆来源类型。</summary>
public enum MemorySourceKind
{
    /// <summary>用户明确表达。</summary>
    Explicit = 0,

    /// <summary>经确认的推断。</summary>
    ConfirmedInference = 1,

    /// <summary>候选推断（未确认）。</summary>
    CandidateInference = 2,
}

/// <summary>用户记忆作用域。</summary>
public enum MemoryScope
{
    /// <summary>全局用户偏好。</summary>
    GlobalUser = 0,

    /// <summary>小说级覆盖。</summary>
    NovelOverride = 1,
}

/// <summary>用户记忆记录。</summary>
public sealed record UserMemoryRecord
{
    /// <summary>稳定 ID。</summary>
    public required string Id { get; init; }

    /// <summary>用户归属。</summary>
    public required string OwnerId { get; init; }

    /// <summary>小说覆盖时非空。</summary>
    public string? NovelId { get; init; }

    /// <summary>来源类型。</summary>
    public MemorySourceKind SourceKind { get; init; } = MemorySourceKind.Explicit;

    /// <summary>作用域。</summary>
    public MemoryScope Scope { get; init; } = MemoryScope.GlobalUser;

    /// <summary>记忆文本。</summary>
    public required string Text { get; init; }

    /// <summary>置信度（0~1）。</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>优先级：冲突时显式规则高于推断。</summary>
    public int Priority { get; init; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>过期时间（可选）。</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>来源引用。</summary>
    public List<string>? EvidenceRefs { get; init; }
}
