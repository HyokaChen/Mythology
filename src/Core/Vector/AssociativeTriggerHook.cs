using System.Collections.Concurrent;
using System.Text;
using Core.Cloud;
using Core.Data;
using Core.Tools;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Vector;

/// <summary>
/// 联想记忆触发钩子（本地唯一新增件）：在内容写入类工具成功后，主动把该章来源单元推给云端
/// <c>fiction.associative.link</c>（写后索引 + 召回），并把命中的联想条目静默注入
/// <see cref="Core.Agent.Agent.InjectQueue"/>，让模型在下一轮据此呼应——不依赖模型记得去调 deferred 工具。
///
/// 两个触发点：
/// <list type="bullet">
///   <item>T1 写后：<c>write_chapter</c> / <c>rewrite_selection</c> 成功 → standard 档（召回 + 索引）。</item>
///   <item>T2 写前：场景规划产物（<c>build_scene</c> / <c>manage_scene</c> / <c>batch_create_scenes</c>）成功
///         → quick 档（仅召回，用工具入参＝场景计划作查询）。</item>
/// </list>
/// 索引与召回都在云端；本地不做索引、不做向量计算。云端不可用时静默跳过（不阻塞本地创作）。
/// </summary>
public sealed class AssociativeTriggerHook(
    Core.Agent.Agent agent,
    INovelStore store,
    IFictionHarnessClient? harness,
    ILogger<AssociativeTriggerHook>? logger = null
) : IToolHook
{
    /// <summary>写后触发（与 WritingFlowHook.WriteTools 同集合）。</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "write_chapter",
        "rewrite_selection",
    };

    /// <summary>写前触发：场景规划产物即"即将写的场景计划"。</summary>
    private static readonly HashSet<string> RecallTools = new(StringComparer.Ordinal)
    {
        "build_scene",
        "manage_scene",
        "batch_create_scenes",
    };

    /// <summary>所有注入消息的识别锚点（测试与 Prompt 层依赖此前缀）。</summary>
    public const string Prefix = "[联想记忆]";

    /// <summary>单次注入条数上限（防噪）。</summary>
    private const int MaxInjectHits = 4;

    /// <summary>已注入过的 (作用域, 源, 触发短语) —— 避免重复注入同一联想。</summary>
    private readonly ConcurrentDictionary<string, byte> _injected = new(StringComparer.Ordinal);

    public Task<ToolCallBeforeResult> BeforeToolCallAsync(
        ToolHookContext context,
        CancellationToken ct
    ) => Task.FromResult(ToolCallBeforeResult.Allow);

    public async Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct)
    {
        try
        {
            await RunAsync(context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 用户取消：上抛
        }
        catch (Exception ex)
        {
            // 钩子失败绝不阻塞工具调用流程
            logger?.LogDebug(ex, "联想记忆钩子失败（已忽略）: {Tool}", context.ToolName);
        }
    }

    private async Task RunAsync(ToolHookContext context, CancellationToken ct)
    {
        if (harness is null)
            return;
        if (context.Result is null or { IsError: true })
            return;

        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return;

        if (WriteTools.Contains(context.ToolName))
            await RunPostWriteAsync(context, novelId, ct);
        else if (RecallTools.Contains(context.ToolName))
            await RunPreWriteAsync(context, novelId, ct);
    }

    /// <summary>T1 写后：standard 档（先召回既有索引，再把本章写入索引）。</summary>
    private async Task RunPostWriteAsync(
        ToolHookContext context,
        string novelId,
        CancellationToken ct
    )
    {
        if (
            context.Result!.Details is not IChapterWriteResult written
            || string.IsNullOrEmpty(written.Id)
        )
            return;

        var chapter = await store.GetChapter(novelId, written.Id, ct);
        if (chapter is null || string.IsNullOrWhiteSpace(chapter.Content))
            return;

        var sources = await BuildSourcesAsync(novelId, chapter, ct);
        // 幂等键含章节版本：同章未变服务端去重，改写后重新索引
        var key = $"assoc-link-{novelId}-{chapter.Id}-{chapter.UpdatedAt.Ticks}";
        var result = await AssociativeLinkClient.LinkAsync(
            harness!,
            novelId,
            sources,
            query: null,
            idempotencyKey: key,
            depth: "standard",
            ct
        );

        if (result?.Hits is not { Count: > 0 } hits)
            return;

        await InjectAsync(novelId, chapter, hits, isPreWrite: false, context.Arguments, ct);
    }

    /// <summary>T2 写前：quick 档（仅召回），查询＝刚生成的场景计划。</summary>
    private async Task RunPreWriteAsync(
        ToolHookContext context,
        string novelId,
        CancellationToken ct
    )
    {
        var query = context.Arguments;
        if (string.IsNullOrWhiteSpace(query))
            return;

        var key = $"assoc-recall-{novelId}-{QueryKey(query)}";
        var result = await AssociativeLinkClient.LinkAsync(
            harness!,
            novelId,
            [],
            query,
            key,
            depth: "quick",
            ct
        );

        if (result?.Hits is not { Count: > 0 } hits)
            return;

        await InjectAsync(novelId, chapter: null, hits, isPreWrite: true, query, ct);
    }

    private async Task InjectAsync(
        string novelId,
        Chapter? chapter,
        IReadOnlyList<AssociativeHitDto> hits,
        bool isPreWrite,
        string arguments,
        CancellationToken ct
    )
    {
        var scope = chapter?.Id ?? QueryKey(arguments);
        var fresh = new List<AssociativeHitDto>(MaxInjectHits);
        foreach (var hit in hits)
        {
            if (fresh.Count >= MaxInjectHits)
                break;
            if (_injected.TryAdd($"{scope}:{hit.SourceEntityId}:{hit.Concept}", 0))
                fresh.Add(hit);
        }

        if (fresh.Count == 0)
            return;

        var chapters = await store.GetChapters(novelId, ct);
        var titles = chapters.ToDictionary(c => c.Id, c => (c.Order, c.Title));

        agent.InjectQueue.Enqueue(
            new LlmChatMessage(
                ChatMessageRoles.User,
                BuildMessage(chapter, fresh, isPreWrite, titles)
            )
        );
    }

    private async Task<List<AssociativeSourceInput>> BuildSourcesAsync(
        string novelId,
        Chapter chapter,
        CancellationToken ct
    )
    {
        var plotlines = await store.GetPlotlines(novelId, ct);
        var scenes = (await store.GetScenesByNovel(novelId, ct))
            .Where(s => s.ChapterId == chapter.Id)
            .ToList();
        var changes = (await store.GetEntityStateChanges(novelId, ct))
            .Where(s => s.ChapterId == chapter.Id && s.Lifecycle == ChangeLifecycle.Committed)
            .ToList();
        var locationNames = (await store.GetLocations(novelId, ct)).ToDictionary(
            l => l.Id,
            l => l.Name
        );
        var characters = await store.GetCharacters(novelId, ct);
        var characterNames = characters.ToDictionary(c => c.Id, c => c.Name);
        var plotlineNames = plotlines.ToDictionary(p => p.Id, p => p.Name);

        return AssociativeSourceBuilder.BuildForChapter(
            chapter,
            scenes,
            changes,
            plotlines,
            locationNames,
            characterNames,
            plotlineNames,
            knownEntities: string.Join("、", characters.Select(c => c.Name)),
            includeBodyChunks: true,
            maxChunks: AssociativeSourceBuilder.DefaultChunksPerChapter
        );
    }

    private static string BuildMessage(
        Chapter? chapter,
        IReadOnlyList<AssociativeHitDto> hits,
        bool isPreWrite,
        IReadOnlyDictionary<string, (int Order, string Title)> titles
    )
    {
        var sb = new StringBuilder();
        if (isPreWrite)
        {
            sb.Append(Prefix).Append(" 本场景与前文以下内容语义弧相连，落笔时可考虑呼应/回收：");
        }
        else
        {
            sb.Append(Prefix)
                .Append(" 《第")
                .Append(chapter!.Order)
                .Append("章 ")
                .Append(chapter.Title)
                .Append("》与以下前文语义弧相连（字面无重叠），定稿/改写时建议考虑呼应：");
        }
        sb.AppendLine();

        foreach (var hit in hits)
        {
            sb.Append("- [").Append(AssociativeLinkClient.FamilyLabel(hit.Family));
            if (hit.Channel is { Length: > 0 } channel)
                sb.Append('·').Append(AssociativeLinkClient.ChannelLabel(channel));
            sb.Append("] ").Append(SourceLabel(hit, titles));
            if (!string.IsNullOrWhiteSpace(hit.Concept))
                sb.Append(" · ").Append(hit.Concept);
            if (!string.IsNullOrWhiteSpace(hit.Bridge))
                sb.Append("（").Append(hit.Bridge).Append('）');
            sb.AppendLine();
        }

        sb.Append("（需细读可调用 recall_associative，或直接读对应章节。）");
        return sb.ToString();
    }

    private static string SourceLabel(
        AssociativeHitDto hit,
        IReadOnlyDictionary<string, (int Order, string Title)> titles
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

        if (hit.SourceEntityId is { Length: > 0 } id && titles.TryGetValue(id, out var info))
            return $"第{info.Order}章《{info.Title}》·{entityWord}";
        if (hit.SourceChapterOrder is { } order)
            return $"第{order}章·{entityWord}";
        return entityWord;
    }

    private static string QueryKey(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text)
        );
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }
}
