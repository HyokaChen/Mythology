namespace Core.Cloud;

/// <summary>
/// 幻觉扫描（fiction.hallucination.scan）结果契约 — 云端返回的解析产物。
/// 由 Plugins.NovelTools.ScanHallucinationTool 解析填充；放在 Core.Cloud 供插件/UI 共享。
///
/// 语义：层1 自洽性熵（RDriftScore 仅仪表信号）；层2 grounding（Findings 才是判据）——
/// conflict = 正文主导断言与设定图可证矛盾；anomaly = 正文自身断言分裂（建议人工复核）；
/// undetermined = 悬空引用/引用无法解析（宁可不报，也不错报）。
/// </summary>
public sealed record HallucinationScanDetails
{
    /// <summary>漂移总分（仅 status 组观察数加权；≤0.20 low / ≤0.40 medium / &gt;0.40 high）。</summary>
    public double RDriftScore { get; init; }

    /// <summary>风险带（low/medium/high）。</summary>
    public string RiskLevel { get; init; } = "";

    /// <summary>实际扫描章节数（可能因上限裁剪为最近 N 章）。</summary>
    public int ChaptersScanned { get; init; }

    /// <summary>参与统计的 (实体,属性) 组数。</summary>
    public int AttributesChecked { get; init; }

    /// <summary>有正文断言的实体数。</summary>
    public int EntitiesCovered { get; init; }

    /// <summary>grounding 矛盾数（可证）。</summary>
    public int Conflicts { get; init; }

    /// <summary>待定数（悬空引用/无法解析）。</summary>
    public int Undetermined { get; init; }

    /// <summary>高风险 status 组数（产 Conflict 或 Anomaly）。</summary>
    public int HighRisk { get; init; }

    public List<HallucinationFindingEntry> Findings { get; init; } = [];

    public List<HallucinationAttributeGroup> AttributeGroups { get; init; } = [];
}

public sealed record HallucinationFindingEntry
{
    public string Id { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Layer { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Evidence { get; init; }
    public string? Suggestion { get; init; }
    public List<string>? InvolvedElementIds { get; init; }
}

/// <summary>单 (实体,属性) 组的 Spanda 熵组件（逐组展示用）。</summary>
public sealed record HallucinationAttributeGroup
{
    public string EntityId { get; init; } = "";
    public string Attribute { get; init; } = "";
    public double RDrift { get; init; }
    public int ObservationCount { get; init; }
    public List<string> DistinctValues { get; init; } = [];
    public string? DominantValue { get; init; }
}
