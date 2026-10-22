using System.ComponentModel;
using System.Text;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.SemanticRetrievalTool;

/// <summary>recall_associative 参数。</summary>
public sealed class RecallAssociativeParams
{
    [Description("查询文本：传「下一步要写的情节 / 本段草稿 / 当前场景计划」")]
    public string Query { get; init; } = "";

    [Description("小说项目ID，留空则用当前打开的小说")]
    public string NovelId { get; init; } = "";

    [Description("返回结果数上限（服务端另有门限与上限）")]
    public int TopK { get; init; } = 8;
}

/// <summary>recall_associative 结果详情。</summary>
public sealed record RecallAssociativeDetails
{
    /// <summary>命中条数。</summary>
    public int Count { get; init; }

    /// <summary>命中项的源实体 ID（供后续工具引用）。</summary>
    public List<string> SourceEntityIds { get; init; } = [];
}

/// <summary>
/// 联想召回：把当前要写的内容作为查询，向云端召回字面无重叠但语义弧相连的既有内容触发器
/// （伏笔 / 契诃夫之枪 / 角色弧光 / 情节线回收）。命中项带触发短语与「为什么」，
/// 模型据此再用既有工具读取源章节原文，判断如何呼应。
/// 索引在云端——本地不查询、不保存任何索引。
/// </summary>
public sealed class RecallAssociativeTool(
    Agent agent,
    INovelStore store,
    IFictionHarnessClient? harness
) : AgentTool<RecallAssociativeParams, RecallAssociativeDetails>
{
    public override string Name => "recall_associative";
    public override string Label => "联想召回";
    public override string Description =>
        "联想召回：给定「下一步要写的情节/本段草稿」，从云端召回字面无重叠但语义弧相连的旧内容"
        + "（伏笔、契诃夫之枪、角色弧光、情节线回收）。返回触发短语、为什么与源章节指针；"
        + "拿到结果后请用读章节/召回场景工具读取原文再判断如何呼应。"
        + "需先建立联想索引（index_associative_memory），且需云端连接。";
    public override ToolCategory Category => ToolCategory.SearchTool;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<RecallAssociativeDetails>> ExecuteAsync(
        string toolCallId,
        RecallAssociativeParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecallAssociativeDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);

        if (harness is null)
            return Err("⚠️ 联想召回需要云端：未配置云端连接。请使用 `:cloud login` 登录后重试。");

        var novelId = string.IsNullOrWhiteSpace(args.NovelId) ? agent.CurrentNovelId : args.NovelId;
        if (string.IsNullOrEmpty(novelId))
            return Err("当前没有打开的小说项目，请提供 novel_id。");

        if (string.IsNullOrWhiteSpace(args.Query))
            return Err("请提供 query（下一步要写的情节 / 本段草稿 / 场景计划）。");

        var key = $"assoc-recall-{novelId}-{QueryKey(args.Query)}";
        var result = await AssociativeLinkClient.LinkAsync(
            harness,
            novelId,
            [],
            args.Query,
            key,
            depth: "quick",
            ct
        );
        ThrowIfCancelled(ct);

        if (result is null)
            return Err("云端联想召回失败（网络或云端不可用）。");

        if (!string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase))
            return Err($"云端联想召回未就绪：{result.Message ?? result.Status}");

        var hits = result.Hits ?? [];
        if (hits.Count == 0)
            return Ok("未命中联想条目（可能尚未建立索引，或本次内容与前文无强关联）。", [], 0);

        var chapters = await store.GetChapters(novelId, ct);
        var titleById = chapters.ToDictionary(c => c.Id, c => (c.Order, c.Title));

        var sb = new StringBuilder();
        sb.Append("联想命中 ").Append(hits.Count).Append(" 条：").AppendLine();

        var sourceIds = new List<string>(hits.Count);
        foreach (var hit in hits)
        {
            sb.Append('[')
                .Append(AssociativeLinkClient.FamilyLabel(hit.Family))
                .Append("] ")
                .Append("相似度 ")
                .Append(hit.Cosine.ToString("F3"))
                .Append(" · 置信 ")
                .Append(hit.Confidence.ToString("F2"))
                .Append(" · 源：")
                .Append(SourceLabel(hit, titleById));
            sb.AppendLine();
            sb.Append("  触发：").AppendLine(hit.Concept);
            if (!string.IsNullOrWhiteSpace(hit.Bridge))
                sb.Append("  为什么：").AppendLine(hit.Bridge);
            if (hit.Channel is { Length: > 0 } channel)
                sb.Append("  前瞻通道：").AppendLine(AssociativeLinkClient.ChannelLabel(channel));
            if (!string.IsNullOrEmpty(hit.SourceEntityId))
                sourceIds.Add(hit.SourceEntityId);
        }

        sb.Append("（请用读章节/召回场景工具按源指针读取原文后，判断如何呼应或改写。）");

        return Ok(sb.ToString(), sourceIds, hits.Count);
    }

    private static string QueryKey(string query)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(query));
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }

    private static string SourceLabel(
        AssociativeHitDto hit,
        Dictionary<string, (int Order, string Title)> titleById
    )
    {
        var entityWord = hit.SourceEntityType switch
        {
            "SceneDefinition" => "场景",
            "Plotline" => "情节线",
            "Character" => "角色",
            "Item" => "道具",
            _ => "章节",
        };

        if (hit.SourceEntityId is { Length: > 0 } id && titleById.TryGetValue(id, out var info))
            return $"第{info.Order}章《{info.Title}》·{entityWord}";
        if (hit.SourceChapterOrder is { } order)
            return $"第{order}章·{entityWord}";
        return entityWord;
    }

    private static AgentToolResult<RecallAssociativeDetails> Ok(
        string message,
        List<string> sourceEntityIds,
        int count
    ) =>
        new()
        {
            Content = [new TextContent(message)],
            Details = new RecallAssociativeDetails
            {
                Count = count,
                SourceEntityIds = sourceEntityIds,
            },
        };

    private static AgentToolResult<RecallAssociativeDetails> Err(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new RecallAssociativeDetails(),
        };
}
