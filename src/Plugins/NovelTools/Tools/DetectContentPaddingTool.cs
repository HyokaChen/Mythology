using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 注水检测 — 检测内容注水：重复描写/无意义对话/过度内心独白/冗余过渡
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class DetectContentPaddingTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<DetectContentPaddingParams, ContentPaddingDetails>(store, cloudHarness)
{
    public override string Name => "detect_content_padding";
    public override string Label => "注水检测";
    public override string Description => "检测内容注水：重复描写/无意义对话/过度内心独白/冗余过渡";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "注水";

    public override async Task<AgentToolResult<ContentPaddingDetails>> ExecuteAsync(
        string toolCallId,
        DetectContentPaddingParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ContentPaddingDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<ContentPaddingDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<ContentPaddingDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static ContentPaddingDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "padding");
        return new ContentPaddingDetails { CleanScore = GetDimensionScore(dim, "clean-score") };
    }

    private static string BuildReportText(ContentPaddingDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 注水检测报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.CleanScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record DetectContentPaddingParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record ContentPaddingDetails
{
    public double PaddingScore { get; init; }
    public double CleanScore { get; init; }
    public List<PaddingTypeEntry> PaddingTypes { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record PaddingTypeEntry
{
    public string Type { get; init; } = "";
    public string Label { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
