namespace Core.Cloud;

/// <summary>
/// 设定检查（CHANGE_10 · fiction.setup.check）结果契约 — 云端返回的解析产物。
/// 由 Plugins.NovelTools.CheckSetupTool 解析填充，SetUpPanel / TuiApp 共同消费。
/// 放在 Core.Cloud 使插件与 TUI 两侧共享同一类型（Details 经 object? 传递后安全 cast）。
/// </summary>
public sealed record CheckSetupDetails
{
    public List<SetupFindingEntry> Findings { get; init; } = [];
    public List<string> OutlineSummary { get; init; } = [];
    public List<string> CoverageSummary { get; init; } = [];
    public bool L0Only { get; init; }

    /// <summary>summary 计数（可读性 Header）。</summary>
    public int Conflicts { get; init; }
    public int Anomalies { get; init; }
    public int Undetermined { get; init; }

    /// <summary>结构量摘要（缺口密度/收束度等可读行）。</summary>
    public string? StructureNotes { get; init; }

    /// <summary>
    /// 设定图来源摘要（服务端 `graph` 键，只回摘要不回节点/边全量）。
    /// null = 键缺席（旧服务端 / quick 档）。
    /// </summary>
    public SetupGraphInfo? Graph { get; init; }

    /// <summary>分卷大纲指标（服务端 `outline-metrics.volumes`；未同步卷文档/quick → 空表）。</summary>
    public List<VolumeOutlineEntry> Volumes { get; init; } = [];
}

/// <summary>
/// 设定图来源摘要：source="graph" 表示服务端用同步图（卷/章细纲/卷级大纲检查可用）；
/// 其他（digest）表示回退到客户端设定摘要（卷级检查静默缺席）。
/// </summary>
public sealed record SetupGraphInfo(string Source, int NodeCount, int EdgeCount)
{
    /// <summary>可读来源标签（工具报告与面板共用，避免两处文案漂移）。</summary>
    public string Describe() =>
        Source == "graph"
            ? $"同步图（节点 {NodeCount} / 边 {EdgeCount}）"
            : $"设定摘要（未同步卷/章细纲，卷级大纲检查不可用；节点 {NodeCount} / 边 {EdgeCount}）";
}

/// <summary>分卷大纲指标（SetupOutlineMetrics.Volumes 的客户端镜像）。</summary>
public sealed record VolumeOutlineEntry
{
    public string VolumeId { get; init; } = "";
    public string Name { get; init; } = "";
    public int ChapterCount { get; init; }
    public int BeatCount { get; init; }
    public double TensionMean { get; init; }
    public double TensionMax { get; init; }
    public double TensionMin { get; init; }
    public string ArcShape { get; init; } = "";
    public double HookCoverage { get; init; }
    public bool FlatLine { get; init; }
    public int PeakCount { get; init; }

    /// <summary>单行可读摘要（工具报告与面板共用）。</summary>
    public string Describe()
    {
        var text = $"{Name}：{ChapterCount} 章 · {BeatCount} beats";
        if (BeatCount > 0)
            text += $" · 张力 {TensionMean:F2}（{TensionMin:F2}–{TensionMax:F2}）";
        if (!string.IsNullOrWhiteSpace(ArcShape) && ArcShape != "unknown")
            text += $" · 弧线 {ArcShape}";
        if (HookCoverage > 0)
            text += $" · 峰值覆盖 {HookCoverage:P0}";
        if (FlatLine)
            text += " · ⚠ 平坦（张力差 <0.1）";
        return text;
    }
}

public sealed record SetupFindingEntry
{
    public string Id { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Layer { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Evidence { get; init; }
    public List<string>? FixCandidates { get; init; }
    public List<string>? InvolvedElementIds { get; init; }
    public string? Suggestion { get; init; }
}
