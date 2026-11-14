namespace Core.Shared;

/// <summary>
/// 诊断深度 — 公共结果契约（CHANGE_07 Phase C）。
/// 原定义于 WritingTools/CompositeDiagnoser.cs（已删除本地实现），作为公共结果类型保留。
/// </summary>
public enum DiagnosticDepth
{
    /// <summary>快速：低延迟、少维度，适合过渡章</summary>
    Quick,

    /// <summary>标准：默认综合诊断</summary>
    Standard,

    /// <summary>深度：更多维度和更高成本</summary>
    Deep,

    /// <summary>超深：跨章节或高成本分析</summary>
    Ultra,
}

/// <summary>
/// 诊断问题 — 段落级定位的具体问题（公共结果契约）。
/// </summary>
public sealed record DiagnosticIssue
{
    public string Dimension { get; init; } = "";

    /// <summary>问题子类型（如 humanizer 的 formulaic_pattern / ai_buzzword）。</summary>
    public string? SubType { get; init; }

    /// <summary>内部增强分层；只用于增强选择，不进入作者报告。</summary>
    public string? Level { get; init; }

    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string? Suggestion { get; init; }
    public string ChapterId { get; init; } = "";
    public int ParagraphIndex { get; init; } = -1;
    public string ProblematicText { get; init; } = "";
    public double ImpactScore { get; init; }
}

/// <summary>
/// 诊断报告 — 一次 diagnose_writing 调用的完整结果（公共结果契约）。
/// 只承载公共字段；特征详情由云端 WritingFeatureSet 承载。
/// </summary>
public sealed record DiagnosticReport
{
    public DiagnosticDepth Depth { get; init; }
    public double OverallScore { get; init; }
    public Dictionary<string, double> DimensionScores { get; init; } = [];
    public List<DiagnosticIssue> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];

    /// <summary>去 AI 味审计结果（仅 diagnose_writing(mode=deai) 填充；其他模式为 null）。</summary>
    public DeAiAudit? DeAi { get; init; }
}

/// <summary>去 AI 味审计结果 — 云端文字检查的报告视图。</summary>
public sealed record DeAiAudit
{
    public List<DeAiFinding> Findings { get; init; } = [];
    public int SuppressedCount { get; init; }
    public int NoChangeCount { get; init; }
    public bool FullReport { get; init; }
    public bool CloudAuditAvailable { get; init; }
    public string RiskLevel { get; init; } = "none";
    public List<string> Notes { get; init; } = [];
}

/// <summary>单条去 AI 味 finding — 保留子类型、证据、建议与段号。</summary>
public sealed record DeAiFinding
{
    public string SubType { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string? Suggestion { get; init; }
    public int ParagraphIndex { get; init; } = -1;
    public double ImpactScore { get; init; }
}
