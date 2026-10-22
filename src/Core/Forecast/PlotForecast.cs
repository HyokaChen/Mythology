using Core.Data;
using Core.Vector;

namespace Core.Forecast;

/// <summary>远期推演发现的计划风险类别。</summary>
public enum ForecastRiskKind
{
    /// <summary>计划揭示章已过（欠账）。</summary>
    Overdue,

    /// <summary>计划揭示章在窗口内，但已写章节从未推进该线。</summary>
    DueWithoutAdvance,

    /// <summary>窗口细纲提到某情节线，但该线没有计划揭示章。</summary>
    NoPlanOnWindowLine,

    /// <summary>转折点排在该线的计划揭示章之后（顺序矛盾）。</summary>
    BeatAfterReveal,

    /// <summary>已写章之间有空号（跳章）。</summary>
    UnwrittenHole,

    /// <summary>本书尚无出场记录：角色播种缺口无法核对。</summary>
    NoAppearanceRecords,
}

/// <summary>窗口章的一句话简报（细纲）。</summary>
public sealed record ForecastChapterBrief(int Order, string Title, string Summary);

/// <summary>计划转折点（来自情节线 Turnpoints 的确定性解析）。</summary>
public sealed record ForecastBeat(
    int ChapterOrder,
    string PlotlineId,
    string PlotlineName,
    string Description,
    string Type,
    double Tension
);

/// <summary>到期/临期义务：某情节线应在第 N 章揭示。</summary>
public sealed record ForecastObligation(
    string PlotlineId,
    string Name,
    PlotlineType Type,
    int PlannedOrder,
    int RemainingChapters,
    bool Overdue,
    IReadOnlyList<int> AdvanceOrders,
    string Turnpoints
);

/// <summary>播种缺口：窗口细纲要用、但前文没有任何出场/推进记录的实体。</summary>
public sealed record ForecastSeedGap(
    string Kind,
    string EntityId,
    string Name,
    int ReferencedOrder
);

/// <summary>计划风险（可直接渲染成一行）。</summary>
public sealed record ForecastRisk(ForecastRiskKind Kind, string Message, int? ChapterOrder);

/// <summary>前文证据（本地混合检索命中 / 云端联想命中）。</summary>
public sealed record ForecastEvidence(
    string Source,
    string? ChapterId,
    int? ChapterOrder,
    string? ChapterTitle,
    DocumentKind? Kind,
    string Text,
    string? Family,
    string? Channel,
    double Score
);

/// <summary>一条笔记线索。<see cref="Source"/>：anchor / near / far / past。</summary>
public sealed record ForecastNote(
    string NoteId,
    string Title,
    string Excerpt,
    int? AnchorOrder,
    string Source
);

/// <summary>笔记选择结果（InboxCount = 全书待确认候选数，供「确认后才参与推演」提示）。</summary>
public sealed record NoteSelection(IReadOnlyList<ForecastNote> Notes, int InboxCount);

/// <summary>
/// 远期推演结果：纯计算产物（大纲/细纲/情节线/出场记录 + 笔记线索），不含任何 I/O。
/// </summary>
public sealed record PlotForecast(
    int CurrentOrder,
    int TargetOrder,
    string? TargetChapterId,
    string? TargetChapterTitle,
    string? VolumeGoal,
    IReadOnlyList<ForecastChapterBrief> Window,
    IReadOnlyList<ForecastBeat> Beats,
    IReadOnlyList<ForecastObligation> Obligations,
    IReadOnlyList<ForecastSeedGap> SeedGaps,
    IReadOnlyList<ForecastRisk> Risks,
    string QueryText,
    string LongRangeQueryText,
    bool HasPlanData,
    int WindowSize
);
