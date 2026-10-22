using System.ComponentModel;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.WritingTools.Enhance;

/// <summary>
/// 通用维度增强工具 — 取代6个Enhance*Tool子类。
/// 单类多实例：通过构造函数参数配置维度名、描述和提示语。
/// 诊断走云端 Harness（CHANGE_07 Phase C），生成/写回走本地 Agent 与 store。
/// </summary>
public sealed class EnhanceDimensionTool(
    INovelStore store,
    Agent agent,
    IFictionHarnessClient cloudHarness,
    string name,
    string dimension,
    string descriptionDetail,
    string diagnosingMessage,
    string noIssueMessage
) : AgentTool<EnhanceDimensionParams, ExploreDetails>
{
    public override string Name => name;
    public override string Label => $"{dimension}增强";
    public override string Description =>
        $"增强{dimension}：{descriptionDetail}，生成保守版和适度版改写变体（云端诊断）";
    public override ToolCategory Category => ToolCategory.ChapterManagement;
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 全自动模式会写 store：强制串行防并发写冲突

    private readonly EnhancementEngine _engine = new(store, agent, cloudHarness);

    public override async Task<AgentToolResult<ExploreDetails>> ExecuteAsync(
        string toolCallId,
        EnhanceDimensionParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ExploreDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);

        var novelId = args.NovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            var novels = await store.GetAllNovels(ct);
            novelId = novels.FirstOrDefault()?.Id;
            if (novelId is null)
                return ErrorResult("未找到小说项目");
        }

        var novel = await store.GetNovel(novelId, ct);
        if (novel is null)
            return ErrorResult($"小说项目 {novelId} 不存在");

        var chapters = await store.GetChapters(novelId, ct);
        ThrowIfCancelled(ct);
        var targetChapter = args.ChapterId is not null
            ? chapters.FirstOrDefault(c => c.Id == args.ChapterId)
                // 当 LLM 传的是序号而非 GUID 时，按 Order 匹配
                ?? (
                    int.TryParse(args.ChapterId, out var orderNum)
                        ? chapters.FirstOrDefault(c => c.Order == orderNum)
                        : null
                )
            : chapters.LastOrDefault();

        if (targetChapter is null)
            return ErrorResult("未找到目标章节");

        var profile = WritingProfile.FromNovel(novel);

        // 全自动模式
        if (novel.AutoEnhance == "full")
        {
            var autoResult = await _engine.AutoEnhanceAsync(
                novelId,
                targetChapter.Id,
                dimension,
                profile,
                ct
            );
            return new AgentToolResult<ExploreDetails> { Content = [new TextContent(autoResult)] };
        }

        // 半自动模式：诊断→定位→生成→应用
        onUpdate?.Report(
            new AgentToolResult<ExploreDetails> { Content = [new TextContent(diagnosingMessage)] }
        );

        var report = await _engine.DiagnoseAsync(novelId, targetChapter.Id, dimension, ct);
        ThrowIfCancelled(ct);

        var locations = _engine.Locate(report, targetChapter.Content ?? "");
        if (locations.Count == 0)
            return new AgentToolResult<ExploreDetails>
            {
                Content = [new TextContent(noIssueMessage)],
            };
        var location = locations[0] with
        {
            ChapterTitle = targetChapter.Title,
            ChapterOrder = targetChapter.Order,
        };
        onUpdate?.Report(
            new AgentToolResult<ExploreDetails>
            {
                Content =
                [
                    new TextContent($"正在生成增强变体（第{location.ParagraphIndex + 1}段）…"),
                ],
            }
        );

        var candidates = await _engine.GenerateAsync(location, profile, ct);
        if (candidates.Count == 0)
            return ErrorResult("生成增强变体失败");

        return _engine.Apply(candidates, location, novelId);
    }

    private static AgentToolResult<ExploreDetails> ErrorResult(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

/// <summary>
/// 通用维度增强参数 — 所有增强维度共用。
/// </summary>
public sealed record EnhanceDimensionParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("目标章节ID，留空则使用最新章节")]
    public string? ChapterId { get; init; }
}
