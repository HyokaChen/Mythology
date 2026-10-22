using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 高智商角色与创意激发 — 天才角色类型识别+陈词滥调检测+创意建议
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class AnalyzeGeniusCharacterTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<AnalyzeGeniusCharacterParams, GeniusCharacterDetails>(store, cloudHarness)
{
    public override string Name => "analyze_genius_character";
    public override string Label => "高智商角色与创意激发";
    public override string Description => "天才角色类型识别+陈词滥调检测+创意建议";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "天才角色";

    public override async Task<AgentToolResult<GeniusCharacterDetails>> ExecuteAsync(
        string toolCallId,
        AnalyzeGeniusCharacterParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<GeniusCharacterDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<GeniusCharacterDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<GeniusCharacterDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static GeniusCharacterDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "genius");
        return new GeniusCharacterDetails
        {
            GeniusType = GetDimensionString(dim, "genius-type"),
            GeniusLabel = GetDimensionString(dim, "genius-label"),
            GeniusConfidence = GetDimensionScore(dim, "genius-confidence") * 100,
            ClichéScore = GetDimensionScore(dim, "cliché-score"),
            CreativeSuggestions = ParseStringList(dim, "creative-suggestions"),
            Strengths = ParseStringList(dim, "strengths"),
        };
    }

    private static string BuildReportText(GeniusCharacterDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 高智商角色与创意激发报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.GeniusConfidence:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record AnalyzeGeniusCharacterParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";

    [Description("角色ID")]
    public string CharacterId { get; init; } = "";
}

public sealed record GeniusCharacterDetails
{
    public string GeniusType { get; init; } = "";
    public string GeniusLabel { get; init; } = "";
    public double GeniusConfidence { get; init; }
    public double ClichéScore { get; init; }
    public List<string> CreativeSuggestions { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}
