using System.ComponentModel;
using System.Text;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.SemanticRetrievalTool;

/// <summary>index_associative_memory 参数。</summary>
public sealed class IndexAssociativeMemoryParams
{
    [Description("小说项目ID，留空则用当前打开的小说")]
    public string NovelId { get; init; } = "";

    [Description("目标章节：如 \"3\" 或 \"1-5\"；留空=最近一章有正文的章节")]
    public string Chapters { get; init; } = "";

    [Description("是否同时用正文块建立索引，默认 true")]
    public bool IncludeBodyChunks { get; init; } = true;

    [Description("每章最多处理正文块数，默认 20（上限 50）")]
    public int MaxChunksPerChapter { get; init; } =
        AssociativeSourceBuilder.DefaultChunksPerChapter;
}

/// <summary>index_associative_memory 结果详情。</summary>
public sealed record IndexAssociativeMemoryDetails
{
    /// <summary>云端实际写入的触发器条数。</summary>
    public int Indexed { get; init; }

    /// <summary>成功提交的章节数。</summary>
    public int ChaptersSubmitted { get; init; }
}

/// <summary>
/// 建立联想索引：把选中章节的来源单元（章节 + 场景 + 正文块）提交云端
/// <c>fiction.associative.link</c>，由云端抽取四象限触发器、嵌入并入库。
/// 本地不保存任何索引（联想记忆整体在云端）。
/// </summary>
public sealed class IndexAssociativeMemoryTool(
    Agent agent,
    INovelStore store,
    IFictionHarnessClient? harness
) : AgentTool<IndexAssociativeMemoryParams, IndexAssociativeMemoryDetails>
{
    public override string Name => "index_associative_memory";
    public override string Label => "建立联想索引";
    public override string Description =>
        "把章节提交云端建立联想索引（写时排练）：云端从梗概/场景/正文块抽取联想触发器（伏笔、人物弧光、"
        + "契诃夫之枪、强关联场景）并入库，供后续章节联想召回使用。"
        + "Chapters 可指定 \"3\" 或 \"1-5\"，留空则处理最近一章有正文的章节。"
        + "需云端连接与 creator 及以上套餐；本地不保存任何索引。";
    public override ToolCategory Category => ToolCategory.SearchTool;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<IndexAssociativeMemoryDetails>> ExecuteAsync(
        string toolCallId,
        IndexAssociativeMemoryParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<IndexAssociativeMemoryDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);

        if (harness is null)
            return Err(
                "⚠️ 联想索引需要云端：未配置云端连接。请使用 `:cloud login` 登录后重试。本地创作不受影响。"
            );

        var novelId = string.IsNullOrWhiteSpace(args.NovelId) ? agent.CurrentNovelId : args.NovelId;
        if (string.IsNullOrEmpty(novelId))
            return Err("当前没有打开的小说项目，请提供 novel_id。");

        var chapters = await store.GetChapters(novelId, ct);
        if (chapters.Count == 0)
            return Err("该小说没有章节。");

        var targets = SelectTargets(args.Chapters, chapters);
        if (targets.Count == 0)
            return Err("未匹配到目标章节（Chapters 需为 \"3\" 或 \"1-5\"，且章节需有正文）。");

        var plotlines = await store.GetPlotlines(novelId, ct);
        var scenesByChapter = (await store.GetScenesByNovel(novelId, ct))
            .GroupBy(s => s.ChapterId ?? "")
            .ToDictionary(g => g.Key, g => g.ToList());
        var changesByChapter = (await store.GetEntityStateChanges(novelId, ct))
            .Where(s => s.Lifecycle == ChangeLifecycle.Committed)
            .GroupBy(s => s.ChapterId ?? "")
            .ToDictionary(g => g.Key, g => g.ToList());
        var locationNames = (await store.GetLocations(novelId, ct)).ToDictionary(
            l => l.Id,
            l => l.Name
        );
        var characters = await store.GetCharacters(novelId, ct);
        var characterNames = characters.ToDictionary(c => c.Id, c => c.Name);
        var plotlineNames = plotlines.ToDictionary(p => p.Id, p => p.Name);
        var knownEntities = string.Join(
            "、",
            characters.Select(c => c.Name).Concat(locationNames.Values)
        );

        var maxChunks = Math.Clamp(
            args.MaxChunksPerChapter,
            1,
            AssociativeSourceBuilder.MaxChunksPerChapter
        );

        var indexed = 0;
        var submitted = 0;
        var warnings = new List<string>();

        foreach (var chapter in targets)
        {
            ct.ThrowIfCancellationRequested();

            var sources = AssociativeSourceBuilder.BuildForChapter(
                chapter,
                scenesByChapter.GetValueOrDefault(chapter.Id) ?? [],
                changesByChapter.GetValueOrDefault(chapter.Id) ?? [],
                plotlines,
                locationNames,
                characterNames,
                plotlineNames,
                knownEntities,
                args.IncludeBodyChunks,
                maxChunks
            );

            // 幂等键含章节版本：同章未变则服务端去重，改写后（UpdatedAt 变化）重新索引
            var key = $"assoc-link-{novelId}-{chapter.Id}-{chapter.UpdatedAt.Ticks}";
            var result = await AssociativeLinkClient.LinkAsync(
                harness,
                novelId,
                sources,
                query: null,
                idempotencyKey: key,
                depth: "standard",
                ct
            );

            if (result is null)
            {
                warnings.Add($"第{chapter.Order}章：云端调用失败");
                continue;
            }

            if (!string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"第{chapter.Order}章：{result.Message ?? "云端未就绪"}");
                continue;
            }

            indexed += result.Indexed;
            submitted++;
        }

        if (submitted == 0)
            return Err("云端未建立索引：" + string.Join("；", warnings));

        var sb = new StringBuilder();
        sb.Append("已提交 ")
            .Append(submitted)
            .Append(" 章到云端，写入触发器 ")
            .Append(indexed)
            .Append(" 条。");
        if (warnings.Count > 0)
            sb.Append("（").Append(string.Join("；", warnings)).Append("）");

        return Ok(sb.ToString(), indexed, submitted);
    }

    /// <summary>解析章节选择字符串 → 目标章节列表。空串 = 最近一章有正文的章节。</summary>
    private static List<Chapter> SelectTargets(string spec, List<Chapter> chapters)
    {
        var withContent = chapters.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();
        if (string.IsNullOrWhiteSpace(spec))
        {
            var latest = withContent.OrderByDescending(c => c.Order).FirstOrDefault();
            return latest is null ? [] : [latest];
        }

        var trimmed = spec.Trim();
        var dash = trimmed.IndexOf('-');
        if (dash > 0)
        {
            if (
                int.TryParse(trimmed[..dash].Trim(), out var lo)
                && int.TryParse(trimmed[(dash + 1)..].Trim(), out var hi)
            )
            {
                if (lo > hi)
                    (lo, hi) = (hi, lo);
                return chapters
                    .Where(c =>
                        c.Order >= lo && c.Order <= hi && !string.IsNullOrWhiteSpace(c.Content)
                    )
                    .OrderBy(c => c.Order)
                    .ToList();
            }
            return [];
        }

        if (int.TryParse(trimmed, out var order))
            return chapters
                .Where(c => c.Order == order && !string.IsNullOrWhiteSpace(c.Content))
                .ToList();

        return [];
    }

    private static AgentToolResult<IndexAssociativeMemoryDetails> Ok(
        string message,
        int indexed,
        int submitted
    ) =>
        new()
        {
            Content = [new TextContent(message)],
            Details = new IndexAssociativeMemoryDetails
            {
                Indexed = indexed,
                ChaptersSubmitted = submitted,
            },
        };

    private static AgentToolResult<IndexAssociativeMemoryDetails> Err(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new IndexAssociativeMemoryDetails(),
        };
}
