using System.ComponentModel;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Forecast;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>forecast_plot 参数。</summary>
public sealed class ForecastPlotParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("前瞻窗口：未来几章（1-10，默认5）")]
    public int Window { get; init; } = 5;

    [Description("是否附带前文证据与笔记检索")]
    public bool IncludeEvidence { get; init; } = true;
}

/// <summary>forecast_plot 结果详情。</summary>
public sealed record ForecastPlotDetails(
    int TargetOrder,
    int DueCount,
    int OverdueCount,
    int SeedGapCount,
    int NoteCount,
    int EvidenceCount
);

/// <summary>
/// 远期推演：结合总纲/卷纲/章节细纲、情节线转折点与作者笔记，推算未来 N 章的
/// 到期义务、计划转折、播种缺口与前文证据。开写前用它对齐全书走向。
/// </summary>
public sealed class ForecastPlotTool(
    INovelStore store,
    RetrievalKernel? kernel,
    IFictionHarnessClient? harness,
    Agent? agent
) : AgentTool<ForecastPlotParams, ForecastPlotDetails>
{
    private readonly PlotForecastService _service = new(store, kernel, harness);

    public override string Name => "forecast_plot";
    public override string[] Aliases => ["远期推演", "远期预测", "大纲推演"];
    public override string Label => "远期推演";
    public override string Description =>
        "结合总纲/卷纲/章节细纲、情节线转折点与作者笔记，推算未来 N 章的到期义务、计划转折、播种缺口与前文证据；开写前用它对齐全书走向。";
    public override ToolCategory Category => ToolCategory.ChapterManagement;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<ForecastPlotDetails>> ExecuteAsync(
        string toolCallId,
        ForecastPlotParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ForecastPlotDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);

        var novelId = string.IsNullOrWhiteSpace(args.NovelId)
            ? agent?.CurrentNovelId
            : args.NovelId;
        if (string.IsNullOrEmpty(novelId))
            return Err("当前没有打开的小说项目，请提供 novel_id。");

        var forecast = await _service.BuildAsync(novelId, args.Window, ct);
        if (forecast is null)
            return Err("找不到该小说。");

        if (!forecast.HasPlanData)
            return Ok(
                PlotForecastFormatter.Format(forecast, []),
                new ForecastPlotDetails(forecast.TargetOrder, 0, 0, 0, 0, 0)
            );

        IReadOnlyList<ForecastEvidence> evidence = [];
        var notes = new NoteSelection([], 0);
        if (args.IncludeEvidence)
        {
            evidence = await _service.RecallAsync(novelId, forecast, 6, true, ct);
            notes = await _service.SelectNotesAsync(novelId, forecast, 4, ct);
        }

        var text = PlotForecastFormatter.Format(forecast, evidence, notes);
        return Ok(
            text,
            new ForecastPlotDetails(
                forecast.TargetOrder,
                forecast.Obligations.Count,
                forecast.Obligations.Count(o => o.Overdue),
                forecast.SeedGaps.Count,
                notes.Notes.Count,
                evidence.Count
            )
        );
    }

    private static AgentToolResult<ForecastPlotDetails> Ok(
        string text,
        ForecastPlotDetails details
    ) => new() { Content = [new TextContent(text)], Details = details };

    private static AgentToolResult<ForecastPlotDetails> Err(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}
