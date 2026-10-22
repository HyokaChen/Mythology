using Core.Data;

namespace Core.Evolution;

/// <summary>一次写入的本体来源标注（AI/用户/系统 + 工具 + 会话 + 提案证据 + 变更集上下文）。</summary>
public sealed record OntologyWriteScope(
    AuditSource Source,
    string? ToolName,
    string? SessionId,
    string? Evidence = null,
    string? ProposalId = null,
    string? ChangeSetId = null,
    string? ChapterId = null,
    string? LocationId = null,
    string? SceneId = null,
    IReadOnlyList<string>? PlotlineIds = null,
    int Sequence = 0,
    bool IsCascade = false,
    string? SourceChangeId = null
)
{
    /// <summary>未显式标注时的默认来源（系统）。</summary>
    public static readonly OntologyWriteScope Default = new(AuditSource.System, null, null);
}

/// <summary>
/// 写入来源上下文（AsyncLocal）：FileNovelStore 在记录本体动作日志时读取，
/// 工具执行/UI 保存/系统钩子各自标注，避免把同一笔写入误记成其他来源。
/// 另有四个独立抑制位：日志、章节生命周期门禁、已发布引用守卫、设定变更补记。
/// </summary>
public static class OntologyWriteContext
{
    private static readonly AsyncLocal<OntologyWriteScope?> Scope = new();
    private static readonly AsyncLocal<bool> JournalSuppressed = new();
    private static readonly AsyncLocal<bool> ChapterLifecycleSuppressed = new();
    private static readonly AsyncLocal<bool> PublishedReferenceGuardSuppressed = new();
    private static readonly AsyncLocal<bool> ManualStateChangeSuppressed = new();

    /// <summary>当前作用域；未标注返回 <see cref="OntologyWriteScope.Default"/>。</summary>
    public static OntologyWriteScope Current => Scope.Value ?? OntologyWriteScope.Default;

    /// <summary>是否抑制本体日志写入。</summary>
    public static bool IsJournalSuppressed => JournalSuppressed.Value;

    /// <summary>是否允许绕过章节状态门禁（发布/撤回事务内部使用）。</summary>
    public static bool IsChapterLifecycleSuppressed => ChapterLifecycleSuppressed.Value;

    /// <summary>是否跳过已发布引用守卫（撤回回滚删除创建实体时使用）。</summary>
    public static bool IsPublishedReferenceGuardSuppressed =>
        PublishedReferenceGuardSuppressed.Value;

    /// <summary>是否抑制人工设定变更补记（发布/撤回回滚时使用）。</summary>
    public static bool IsManualStateChangeSuppressed => ManualStateChangeSuppressed.Value;

    /// <summary>进入一个写入作用域；Dispose 恢复旧值（嵌套安全）。</summary>
    public static IDisposable Begin(
        AuditSource source,
        string? toolName = null,
        string? sessionId = null,
        string? evidence = null,
        string? proposalId = null
    ) =>
        new ScopeHandle(
            Scope.Value,
            new OntologyWriteScope(source, toolName, sessionId, evidence, proposalId)
        );

    /// <summary>用户手动编辑的便捷入口。</summary>
    public static IDisposable BeginUser(string? sessionId = null) =>
        Begin(AuditSource.User, sessionId: sessionId);

    /// <summary>变更集写入作用域：一次发布/补记内所有写入共享同一变更集与章节上下文。</summary>
    public static IDisposable BeginForChangeSet(ChapterChangeSet set, Chapter? chapter)
    {
        var current = Current;
        return new ScopeHandle(
            Scope.Value,
            current with
            {
                Source = AuditSource.System,
                ChangeSetId = set.Id,
                ChapterId =
                    chapter?.Id ?? (string.IsNullOrEmpty(set.ChapterId) ? null : set.ChapterId),
            }
        );
    }

    /// <summary>在当前作用域上附加提案来源（不覆盖来源与工具名）。</summary>
    public static IDisposable Attach(string? evidence, string? proposalId)
    {
        if (string.IsNullOrWhiteSpace(evidence) && string.IsNullOrWhiteSpace(proposalId))
        {
            return new NoopHandle();
        }
        var current = Current;
        return new ScopeHandle(
            Scope.Value,
            current with
            {
                Evidence = evidence,
                ProposalId = proposalId,
            }
        );
    }

    /// <summary>附带章节/地点/场景/情节线/序号上下文（发布应用与状态记录日志用）。</summary>
    public static IDisposable AttachContext(
        string? chapterId,
        string? locationId,
        string? sceneId,
        IReadOnlyList<string>? plotlineIds,
        int sequence,
        bool isCascade = false,
        string? sourceChangeId = null
    )
    {
        var current = Current;
        return new ScopeHandle(
            Scope.Value,
            current with
            {
                ChapterId = chapterId ?? current.ChapterId,
                LocationId = locationId ?? current.LocationId,
                SceneId = sceneId ?? current.SceneId,
                PlotlineIds = plotlineIds ?? current.PlotlineIds,
                Sequence = sequence > 0 ? sequence : current.Sequence,
                IsCascade = isCascade || current.IsCascade,
                SourceChangeId = sourceChangeId ?? current.SourceChangeId,
            }
        );
    }

    /// <summary>抑制本体日志写入。</summary>
    public static IDisposable SuppressJournal() => new FlagHandle(JournalSuppressed, true);

    /// <summary>绕过章节状态门禁（发布/撤回事务内部使用）。</summary>
    public static IDisposable SuppressChapterLifecycle() =>
        new FlagHandle(ChapterLifecycleSuppressed, true);

    /// <summary>跳过已发布引用守卫（回滚删除创建实体时使用）。</summary>
    public static IDisposable SuppressPublishedReferenceGuard() =>
        new FlagHandle(PublishedReferenceGuardSuppressed, true);

    /// <summary>抑制人工设定变更补记（发布/撤回回滚时使用）。</summary>
    public static IDisposable SuppressManualStateChange() =>
        new FlagHandle(ManualStateChangeSuppressed, true);

    private sealed class ScopeHandle : IDisposable
    {
        private readonly OntologyWriteScope? _previous;
        private int _disposed;

        public ScopeHandle(OntologyWriteScope? previous, OntologyWriteScope next)
        {
            _previous = previous;
            Scope.Value = next;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Scope.Value = _previous;
            }
        }
    }

    private sealed class FlagHandle : IDisposable
    {
        private readonly AsyncLocal<bool> _flag;
        private readonly bool _previous;
        private int _disposed;

        public FlagHandle(AsyncLocal<bool> flag, bool value)
        {
            _flag = flag;
            _previous = flag.Value;
            flag.Value = value;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _flag.Value = _previous;
            }
        }
    }

    private sealed class NoopHandle : IDisposable
    {
        public void Dispose() { }
    }
}
