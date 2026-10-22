using System.ComponentModel;
using Core.Data;
using Core.Evolution;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

// ── 4.2.1 项目管理（独立工具，未被 manage_novel 吸收） ──

/// <summary>
/// 项目摘要工具：获取项目统计（总字数、章节数、角色数等）
/// </summary>
public sealed class GetProjectSummaryTool(INovelStore store)
    : AgentTool<GetProjectSummaryParams, ProjectSummary>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "get_project_summary";
    public override string[] Aliases => ["项目摘要", "小说概况", "总结"];
    public override string[] ExampleQueries => ["看看小说的整体情况"];

    /// <summary>工具显示名称</summary>
    public override string Label => "项目摘要";
    public override string Description => "获取项目统计（总字数、章节数、角色数等）";
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行项目摘要查询
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">查询参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>项目摘要</returns>
    public override async Task<AgentToolResult<ProjectSummary>> ExecuteAsync(
        string toolCallId,
        GetProjectSummaryParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ProjectSummary>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novel = await store.GetNovelSummary(args.NovelId, ct);
        ThrowIfCancelled(ct);
        if (novel is null)
            return new AgentToolResult<ProjectSummary>
            {
                Content = [new TextContent($"小说 {args.NovelId} 不存在")],
                IsError = true,
            };
        var chapterCountTask = store.GetChapterCount(args.NovelId, ct);
        var characterCountTask = store.GetCharacterCount(args.NovelId, ct);
        var totalWordCountTask = store.GetTotalWordCount(args.NovelId, ct);
        await Task.WhenAll(chapterCountTask, characterCountTask, totalWordCountTask);
        ThrowIfCancelled(ct);
        var summary = new ProjectSummary
        {
            Title = novel.Title,
            ChapterCount = chapterCountTask.Result,
            CharacterCount = characterCountTask.Result,
            TotalWordCount = totalWordCountTask.Result,
        };
        return new AgentToolResult<ProjectSummary>
        {
            Content =
            [
                new TextContent(
                    $"项目「{summary.Title}」: {summary.ChapterCount} 章, "
                        + $"{summary.CharacterCount} 个角色, 共 {summary.TotalWordCount} 字"
                ),
            ],
            Details = summary,
        };
    }
}

public sealed record GetProjectSummaryParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}

public sealed record ProjectSummary
{
    public string Title { get; init; } = "";
    public int ChapterCount { get; init; }
    public int CharacterCount { get; init; }
    public int TotalWordCount { get; init; }
}

// ── 4.2.2 章节与大纲 ──

/// <summary>
/// 演化记录安全包装（ENTITY-EVOLUTION §4.1/§4.2）：L0 扫描失败只丢打底数据、
/// 不阻断写作主流程；OperationCanceledException 照常上抛（用户取消语义优先）。
/// </summary>
internal static class EvolutionHook
{
    public static async Task RecordEvolutionSafeAsync(
        INovelStore store,
        string novelId,
        Chapter chapter,
        CancellationToken ct
    )
    {
        try
        {
            await AppearanceEvolutionRecorder.RecordChapterAsync(store, novelId, chapter, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 演化记录是派生增强（L0 可重建）：静默降级，写作主流程不受影响
        }
    }
}

public sealed class WriteChapterTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<WriteChapterParams, ChapterDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "write_chapter";
    public override string[] Aliases => ["写章节", "写作", "续写", "更新章节"];
    public override string[] ExampleQueries => ["继续写第三章", "把第二章扩展两千字"];

    /// <summary>工具显示名称</summary>
    public override string Label => "写章节";
    public override string Description => "创建或更新章节内容";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.FileEdit;

    /// <summary>
    /// 执行写章节操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">写作参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>写作结果</returns>
    public override async Task<AgentToolResult<ChapterDetails>> ExecuteAsync(
        string toolCallId,
        WriteChapterParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ChapterDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        EditHunk[]? hunks = null;
        var existingChapters = await store.GetChapters(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var existing = !string.IsNullOrWhiteSpace(args.ChapterId)
            ? existingChapters.Find(c => c.Id == args.ChapterId)
            : existingChapters.Find(c => c.Title == args.Title && c.Order == args.Order);

        if (existing is not null)
        {
            // 复用已有章节：保留 ID、CreatedAt、Status，只更新内容和元数据
            hunks = [EditHunk.FromFullContent(existing.Content, args.Content)];
            existing.Content = args.Content;
            await store.SaveChapter(existing, ct);
            ThrowIfCancelled(ct);
            if (kernel is not null)
            {
                var (doc, chunks) = kernel.Builder.BuildChapterBodyChunks(existing);
                await kernel.IndexDocumentAsync(doc, chunks, ct);
            }
            await EvolutionHook.RecordEvolutionSafeAsync(store, args.NovelId, existing, ct);
            ThrowIfCancelled(ct);
            var wordCount = args.Content.Length;
            return new AgentToolResult<ChapterDetails>
            {
                Content = [new TextContent($"章节「{existing.Title}」已保存 ({wordCount} 字)")],
                Details = new ChapterDetails
                {
                    Id = existing.Id,
                    WordCount = wordCount,
                    Hunks = hunks,
                },
            };
        }

        // 新章节才创建
        var chapter = new Chapter
        {
            NovelId = args.NovelId,
            Title = args.Title,
            Order = args.Order,
            Content = args.Content,
            Status = ChapterStatus.Draft,
        };
        await store.SaveChapter(chapter, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildChapterBodyChunks(chapter);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        await EvolutionHook.RecordEvolutionSafeAsync(store, args.NovelId, chapter, ct);
        ThrowIfCancelled(ct);
        var wordCount2 = args.Content.Length;
        return new AgentToolResult<ChapterDetails>
        {
            Content = [new TextContent($"章节「{chapter.Title}」已保存 ({wordCount2} 字)")],
            Details = new ChapterDetails
            {
                Id = chapter.Id,
                WordCount = wordCount2,
                Hunks = hunks,
            },
        };
    }
}

public sealed record WriteChapterParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("章节标题")]
    public string Title { get; init; } = "";

    [Description("要覆盖的章节ID。提供后按ID更新，留空则按标题和序号匹配或创建新章节")]
    public string? ChapterId { get; init; }

    [Description("章节序号（从 1 开始）")]
    public int Order { get; init; }

    [Description(
        "章节正文内容。必须去除 AI 味道：不用「此外/至关重要/充满活力的/标志着/象征着」等词"
    )]
    public string Content { get; init; } = "";
}

public sealed record ChapterDetails : IDiffDetails, IChapterWriteResult
{
    public string Id { get; init; } = "";
    public int WordCount { get; init; }

    /// <summary>
    /// Diff hunks for TUI rendering. Null when creating a new chapter (no prior content).
    /// Uses truncated hunks to avoid storing full chapter content in memory.
    /// </summary>
    public EditHunk[]? Hunks { get; init; }

    // IDiffDetails — ChapterDetails 无 Path 概念，返回 null
    string? IDiffDetails.Path => null;
}

/// <summary>
/// 获取大纲工具：获取当前小说的章节大纲
/// </summary>
public sealed class GetOutlineTool(INovelStore store) : AgentTool<GetOutlineParams, OutlineDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "get_outline";
    public override string[] Aliases => ["查看大纲", "大纲内容"];
    public override string[] ExampleQueries => ["查看当前大纲"];

    /// <summary>工具显示名称</summary>
    public override string Label => "获取大纲";
    public override string Description => "获取当前小说的章节大纲";
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行获取大纲操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">查询参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>大纲详情</returns>
    public override async Task<AgentToolResult<OutlineDetails>> ExecuteAsync(
        string toolCallId,
        GetOutlineParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<OutlineDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var chapters = await store.GetChapters(args.NovelId, ct);
        ThrowIfCancelled(ct);

        var volumes = await store.GetVolumes(args.NovelId, ct);

        // 大纲三层共用本工具：一层=总纲（Novel.Outline）、二层=卷纲（卷行 + 卷梗概）、三层=章节细纲（章行 + 梗概）
        var lines = new List<string>();
        var novel = await store.GetNovel(args.NovelId, ct);
        if (novel is not null)
        {
            lines.Add("📖 总纲（大纲一层）");
            lines.Add(
                string.IsNullOrWhiteSpace(novel.Outline)
                    ? "  （未填写：全书走向/主题/核心冲突/结局方向。可用 manage_novel action=update outline=… 写入）"
                    : novel.Outline.ReplaceLineEndings("\n  ")
            );
            lines.Add("");
        }

        lines.Add("📚 分卷与章节（二层卷纲 / 三层章节细纲）");
        foreach (var volume in volumes.OrderBy(v => v.Order))
        {
            var inVolume = chapters.Where(c => c.VolumeId == volume.Id).OrderBy(c => c.Order);
            var chapterLines = inVolume.Select(FormatChapterLine).ToList();
            lines.Add(
                $"📦 第{volume.Order}卷 {volume.Name}（{chapterLines.Count}章）{FormatVolumeBrief(volume)}"
            );
            lines.AddRange(chapterLines.Select(l => "  " + l));
        }

        var unassigned = chapters
            .Where(c => string.IsNullOrEmpty(c.VolumeId) || volumes.All(v => v.Id != c.VolumeId))
            .OrderBy(c => c.Order)
            .ToList();
        if (volumes.Count > 0 && unassigned.Count > 0)
            lines.Add("（未分卷）");
        lines.AddRange(unassigned.Select(c => FormatChapterLine(c)));

        var text = chapters.Count == 0 ? "暂无章节" : string.Join("\n", lines);
        return new AgentToolResult<OutlineDetails>
        {
            Content = [new TextContent(text)],
            Details = new OutlineDetails
            {
                ChapterCount = chapters.Count,
                VolumeCount = volumes.Count,
            },
        };
    }

    /// <summary>章节行：`第N章 [id]: 标题 (状态, 字数) — 细纲梗概`（梗概非空时追加）。</summary>
    private static string FormatChapterLine(Chapter chapter)
    {
        var line =
            $"第{chapter.Order}章 [{chapter.Id}]: {chapter.Title} ({chapter.Status}, {chapter.Content.Length}字)";
        return string.IsNullOrWhiteSpace(chapter.Summary) ? line : $"{line} — {chapter.Summary}";
    }

    /// <summary>卷行尾部简报：梗概非空时追加。</summary>
    private static string FormatVolumeBrief(Volume volume) =>
        string.IsNullOrWhiteSpace(volume.Summary) ? "" : $" — {volume.Summary}";
}

public sealed record GetOutlineParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}

public sealed record OutlineDetails
{
    public int ChapterCount { get; init; }
    public int VolumeCount { get; init; }
}

/// <summary>
/// 导出章节工具：导出指定章节为 Markdown 文本
/// </summary>
public sealed class ExportChapterTool(INovelStore store)
    : AgentTool<ExportChapterParams, ExportChapterDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "export_chapter";

    /// <summary>工具显示名称</summary>
    public override string Label => "导出章节";
    public override string Description => "导出指定章节为 Markdown 文本";
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行导出章节操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">导出参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>导出结果</returns>
    public override async Task<AgentToolResult<ExportChapterDetails>> ExecuteAsync(
        string toolCallId,
        ExportChapterParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ExportChapterDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var chapter = await store.GetChapter(args.ChapterId, ct);
        ThrowIfCancelled(ct);
        if (chapter is null)
            return new AgentToolResult<ExportChapterDetails>
            {
                Content = [new TextContent($"章节 {args.ChapterId} 不存在")],
                IsError = true,
            };
        var format = args.Format?.ToLowerInvariant() ?? "markdown";
        var output = format is "txt" or "text"
            ? $"{chapter.Title}\n\n{chapter.Content}"
            : $"# {chapter.Title}\n\n{chapter.Content}";
        return new AgentToolResult<ExportChapterDetails>
        {
            Content = [new TextContent(output)],
            Details = new ExportChapterDetails
            {
                Id = chapter.Id,
                Title = chapter.Title,
                WordCount = chapter.Content.Length,
            },
        };
    }
}

public sealed record ExportChapterParams
{
    [Description("章节ID（GUID，如 7b2be66d2f56；也支持章节号如 1、2）")]
    public string ChapterId { get; init; } = "";

    [Description("导出格式，支持 markdown 或 txt")]
    public string? Format { get; init; }
}

public sealed record ExportChapterDetails
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int WordCount { get; init; }
}

/// <summary>
/// 编辑文本工具：对已有章节文本执行插入、替换、删除操作
/// </summary>
public sealed class EditTextTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<EditTextParams, EditTextDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "edit_text";
    public override string[] Aliases => ["修改文本", "改写", "编辑"];
    public override string[] ExampleQueries => ["修改第三章第三段的描写"];

    /// <summary>工具显示名称</summary>
    public override string Label => "编辑文本";
    public override string Description => "对已有章节文本执行插入、替换、删除操作";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.FileEdit;

    /// <summary>
    /// 执行编辑文本操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">编辑参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>编辑结果</returns>
    public override async Task<AgentToolResult<EditTextDetails>> ExecuteAsync(
        string toolCallId,
        EditTextParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<EditTextDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var chapter = await store.GetChapter(args.ChapterId, ct);
        ThrowIfCancelled(ct);
        if (chapter is null)
            return new AgentToolResult<EditTextDetails>
            {
                Content = [new TextContent($"章节 {args.ChapterId} 不存在")],
                IsError = true,
            };

        var content = chapter.Content;
        var operation = args.Operation ?? "replace";
        var oldText = args.OldText ?? "";
        var newText = args.NewText ?? "";
        EditHunk? hunk = null;

        switch (operation)
        {
            case "replace" when !string.IsNullOrEmpty(oldText):
            {
                var idx = content.IndexOf(oldText, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var oldStartLine = content[..idx].Count(c => c == '\n') + 1;
                    hunk = new EditHunk
                    {
                        OldStartLine = oldStartLine,
                        OldText = oldText,
                        NewText = newText,
                    };
                }
                content = content.Replace(oldText, newText);
                break;
            }
            case "insert":
            {
                var pos = Math.Clamp(args.Position ?? content.Length, 0, content.Length);
                var oldStartLine =
                    content[..Math.Min(pos, content.Length)].Count(c => c == '\n') + 1;
                hunk = new EditHunk
                {
                    OldStartLine = oldStartLine,
                    OldText = "",
                    NewText = newText,
                };
                content = string.Concat(content.AsSpan(0, pos), newText, content.AsSpan(pos));
                break;
            }
            case "delete" when !string.IsNullOrEmpty(oldText):
            {
                var idx = content.IndexOf(oldText, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var oldStartLine = content[..idx].Count(c => c == '\n') + 1;
                    hunk = new EditHunk
                    {
                        OldStartLine = oldStartLine,
                        OldText = oldText,
                        NewText = "",
                    };
                }
                content = content.Replace(oldText, "");
                break;
            }
        }

        chapter.Content = content;
        await store.SaveChapter(chapter, ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildChapterBodyChunks(chapter);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        return new AgentToolResult<EditTextDetails>
        {
            Content = [new TextContent($"章节「{chapter.Title}」文本已更新 ({content.Length} 字)")],
            Details = new EditTextDetails
            {
                Id = chapter.Id,
                NewLength = content.Length,
                Hunks = hunk is not null ? [hunk] : null,
            },
        };
    }
}

public sealed record EditTextParams
{
    [Description("章节ID（GUID，如 7b2be66d2f56；也支持章节号如 1、2）")]
    public string ChapterId { get; init; } = "";

    [Description("操作类型：replace（替换）/ insert（插入）/ delete（删除）")]
    public string? Operation { get; init; }

    [Description("要匹配的旧文本（replace/delete 操作时必填）")]
    public string? OldText { get; init; }

    [Description("新文本（replace/insert 操作时必填）")]
    public string? NewText { get; init; }

    [Description("插入位置字符索引（insert 操作时可选，默认末尾）")]
    public int? Position { get; init; }
}

public sealed record EditTextDetails : IDiffDetails
{
    public string Id { get; init; } = "";
    public int NewLength { get; init; }

    /// <summary>
    /// Structured diff data for the edit operation.
    /// Null when no edit was applied.
    /// </summary>
    public EditHunk[]? Hunks { get; init; }

    // IDiffDetails — EditTextDetails 无 Path 概念，返回 null
    string? IDiffDetails.Path => null;
}

/// <summary>
/// 获取字数工具：获取项目或指定章节的字数
/// </summary>
public sealed class GetWordCountTool(INovelStore store)
    : AgentTool<GetWordCountParams, WordCountDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "get_word_count";
    public override string[] Aliases => ["字数", "统计"];
    public override string[] ExampleQueries => ["当前写了多少字"];

    /// <summary>工具显示名称</summary>
    public override string Label => "获取字数";
    public override string Description => "获取项目或指定章节的字数";
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行获取字数操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">查询参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>字数统计结果</returns>
    public override async Task<AgentToolResult<WordCountDetails>> ExecuteAsync(
        string toolCallId,
        GetWordCountParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WordCountDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (!string.IsNullOrEmpty(args.ChapterId))
        {
            var chapter = await store.GetChapter(args.ChapterId, ct);
            ThrowIfCancelled(ct);
            if (chapter is null)
                return new AgentToolResult<WordCountDetails>
                {
                    Content = [new TextContent($"章节 {args.ChapterId} 不存在")],
                    IsError = true,
                };
            return new AgentToolResult<WordCountDetails>
            {
                Content =
                [
                    new TextContent($"章节「{chapter.Title}」共 {chapter.Content.Length} 字"),
                ],
                Details = new WordCountDetails { Count = chapter.Content.Length },
            };
        }

        var total = await store.GetTotalWordCount(args.NovelId, ct);
        ThrowIfCancelled(ct);
        return new AgentToolResult<WordCountDetails>
        {
            Content = [new TextContent($"项目共 {total} 字")],
            Details = new WordCountDetails { Count = total },
        };
    }
}

public sealed record GetWordCountParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("可选：指定章节ID，仅统计该章节字数")]
    public string? ChapterId { get; init; }
}

public sealed record WordCountDetails
{
    public int Count { get; init; }
}

// ── 章节状态管理 ──

/// <summary>
/// 设置章节状态工具（draft/published）——发布/撤回的唯一工具入口。
/// 发布会应用待生效变化与联动；撤回按变更集精确回滚。
/// </summary>
public sealed class SetChapterStatusTool(INovelStore store)
    : AgentTool<SetChapterStatusParams, ChapterStatusDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "set_chapter_status";

    /// <summary>工具显示名称</summary>
    public override string Label => "设置章节状态";
    public override string Description =>
        "设置章节状态：draft（草稿）/ published（发布）。发布会应用待生效变化与联动；撤回自动回滚。";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    public override async Task<AgentToolResult<ChapterStatusDetails>> ExecuteAsync(
        string toolCallId,
        SetChapterStatusParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ChapterStatusDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novelId = args.NovelId;
        var chapter = await store.GetChapter(novelId, args.ChapterId, ct);
        if (chapter is null && int.TryParse(args.ChapterId, out var order))
            chapter = await store.GetChapterByOrder(novelId, order, ct);
        ThrowIfCancelled(ct);
        if (chapter is null)
            return Error($"章节 {args.ChapterId} 不存在");

        var target = args.Status?.Trim().ToLowerInvariant() switch
        {
            "draft" or "草稿" => ChapterStatus.Draft,
            "published" or "publish" or "发布" => ChapterStatus.Published,
            _ => (ChapterStatus?)null,
        };
        if (target is null)
            return Error($"无效状态: {args.Status}。仅支持: draft, published");
        if (chapter.Status == target)
            return Error($"章节「{chapter.Title}」已经是 {chapter.Status}");

        var service = new Core.Evolution.ChapterLifecycleService(store);
        if (target == ChapterStatus.Published)
        {
            var preview = await service.PreviewPublishAsync(novelId, chapter.Id, ct);
            var blocking = preview.ValidationIssues.Where(i => i.BlocksPublish).ToList();
            if (blocking.Count > 0)
            {
                return Error(
                    "发布预检未通过：" + string.Join("；", blocking.Take(5).Select(i => i.Message))
                );
            }
            var result = await service.PublishAsync(
                novelId,
                chapter.Id,
                new Core.Evolution.PublishOptions(),
                ct
            );
            if (!result.Success)
                return Error(result.Error ?? "发布失败");

            var warnings =
                result.Warnings.Count == 0
                    ? ""
                    : "；警告：" + string.Join("；", result.Warnings.Take(5));
            return new AgentToolResult<ChapterStatusDetails>
            {
                Content =
                [
                    new TextContent(
                        $"已发布「{chapter.Title}」：待生效变化 {preview.PendingChanges.Count} 条，"
                            + $"联动 {result.AppliedSuggestionCount} 条，新建纪念地 {result.CreatedEntityCount} 个{warnings}"
                    ),
                ],
                Details = new ChapterStatusDetails
                {
                    Id = chapter.Id,
                    OldStatus = ChapterStatus.Draft,
                    NewStatus = ChapterStatus.Published,
                    PendingCount = preview.PendingChanges.Count,
                    AppliedSuggestionCount = result.AppliedSuggestionCount,
                    ChangeSetId = result.ChangeSetId,
                    Warnings = result.Warnings,
                },
            };
        }

        var unpublish = await service.UnpublishAsync(novelId, chapter.Id, ct);
        if (!unpublish.Success)
            return Error(unpublish.Error ?? "撤回失败");
        return new AgentToolResult<ChapterStatusDetails>
        {
            Content =
            [
                new TextContent(
                    $"已撤回「{chapter.Title}」：回滚 {unpublish.RevertedEntries} 项变更"
                        + (
                            unpublish.Warnings.Count == 0
                                ? ""
                                : "；警告：" + string.Join("；", unpublish.Warnings)
                        )
                ),
            ],
            Details = new ChapterStatusDetails
            {
                Id = chapter.Id,
                OldStatus = ChapterStatus.Published,
                NewStatus = ChapterStatus.Draft,
                RevertedEntries = unpublish.RevertedEntries,
                Warnings = unpublish.Warnings,
            },
        };
    }

    private static AgentToolResult<ChapterStatusDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record SetChapterStatusParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("章节ID（GUID，如 7b2be66d2f56；也支持章节号如 1、2）")]
    public string ChapterId { get; init; } = "";

    [Description("目标状态：draft（草稿）/ published（发布）")]
    public string? Status { get; init; }
}

public sealed record ChapterStatusDetails
{
    public string Id { get; init; } = "";
    public ChapterStatus OldStatus { get; init; }
    public ChapterStatus NewStatus { get; init; }
    public int PendingCount { get; init; }
    public int AppliedSuggestionCount { get; init; }
    public int RevertedEntries { get; init; }
    public string? ChangeSetId { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

// ── save_snapshot ──

/// <summary>
/// 保存快照工具：保存当前所有数据快照（便于回滚）
/// </summary>
public sealed class SaveSnapshotTool(INovelStore store)
    : AgentTool<SaveSnapshotParams, SnapshotDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "save_snapshot";

    /// <summary>工具显示名称</summary>
    public override string Label => "保存快照";
    public override string Description => "保存当前所有数据快照（便于回滚），返回快照摘要";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行保存快照操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">快照参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>快照详情</returns>
    public override async Task<AgentToolResult<SnapshotDetails>> ExecuteAsync(
        string toolCallId,
        SaveSnapshotParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<SnapshotDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novel = await store.GetNovelSummary(args.NovelId, ct);
        ThrowIfCancelled(ct);
        if (novel is null)
            return new AgentToolResult<SnapshotDetails>
            {
                Content = [new TextContent($"小说 {args.NovelId} 不存在")],
                IsError = true,
            };

        var chaptersTask = store.GetChapters(args.NovelId, ct);
        var charactersTask = store.GetCharacters(args.NovelId, ct);
        var plotlinesTask = store.GetPlotlines(args.NovelId, ct);
        var factionsTask = store.GetFactions(args.NovelId, ct);
        var locationsTask = store.GetLocations(args.NovelId, ct);
        var itemsTask = store.GetItems(args.NovelId, ct);
        await Task.WhenAll(
            chaptersTask,
            charactersTask,
            plotlinesTask,
            factionsTask,
            locationsTask,
            itemsTask
        );
        ThrowIfCancelled(ct);

        var chapters = chaptersTask.Result;
        var characters = charactersTask.Result;
        var plotlines = plotlinesTask.Result;
        var factions = factionsTask.Result;
        var locations = locationsTask.Result;
        var items = itemsTask.Result;

        var snapshotId = Guid.NewGuid().ToString("N")[..12];
        var totalWords = chapters.Sum(c => c.Content.Length);

        var summary =
            $"快照 #{snapshotId} 已保存: {chapters.Count}章/{characters.Count}角色/{plotlines.Count}情节线/{factions.Count}组织/{locations.Count}地点/{items.Count}道具, 共{totalWords}字";
        return new AgentToolResult<SnapshotDetails>
        {
            Content = [new TextContent(summary)],
            Details = new SnapshotDetails
            {
                SnapshotId = snapshotId,
                ChapterCount = chapters.Count,
                CharacterCount = characters.Count,
                TotalWords = totalWords,
            },
        };
    }
}

public sealed record SaveSnapshotParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}

public sealed record SnapshotDetails
{
    public string SnapshotId { get; init; } = "";
    public int ChapterCount { get; init; }
    public int CharacterCount { get; init; }
    public int TotalWords { get; init; }
}

// ── reorder_chapters ──

/// <summary>
/// 重排序章节工具：调整章节顺序
/// </summary>
public sealed class ReorderChaptersTool(INovelStore store)
    : AgentTool<ReorderChaptersParams, ReorderChaptersDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "reorder_chapters";

    /// <summary>工具显示名称</summary>
    public override string Label => "重排序章节";
    public override string Description => "调整章节顺序，传入章节ID列表（按新顺序排列）";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行重排序章节操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">排序参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>排序结果</returns>
    public override async Task<AgentToolResult<ReorderChaptersDetails>> ExecuteAsync(
        string toolCallId,
        ReorderChaptersParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ReorderChaptersDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var order = 1;
        var updated = 0;
        foreach (var chapterId in args.ChapterIds)
        {
            var chapter = await store.GetChapter(chapterId, ct);
            ThrowIfCancelled(ct);
            if (chapter is not null)
            {
                chapter.Order = order++;
                await store.SaveChapter(chapter, ct);
                ThrowIfCancelled(ct);
                updated++;
            }
        }
        return new AgentToolResult<ReorderChaptersDetails>
        {
            Content = [new TextContent($"已重排序 {updated} 个章节")],
            Details = new ReorderChaptersDetails { Count = updated },
        };
    }
}

public sealed record ReorderChaptersParams
{
    [Description("按新顺序排列的章节ID列表")]
    public string[] ChapterIds { get; init; } = [];
}

public sealed record ReorderChaptersDetails
{
    public int Count { get; init; }
}

// ── create_outline ──

/// <summary>
/// 创建大纲工具：创建/更新大纲（树状章节结构）
/// </summary>
public sealed class CreateOutlineTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<CreateOutlineParams, OutlineDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "create_outline";
    public override string[] Aliases => ["创建大纲", "大纲", "规划章节"];
    public override string[] ExampleQueries => ["为小说创建二十章大纲"];

    /// <summary>工具显示名称</summary>
    public override string Label => "创建大纲";
    public override string Description =>
        "创建/更新大纲（树状章节结构），传入章节标题列表按顺序创建";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.FileEdit;

    /// <summary>
    /// 执行创建大纲操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">大纲参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>创建结果</returns>
    public override async Task<AgentToolResult<OutlineDetails>> ExecuteAsync(
        string toolCallId,
        CreateOutlineParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<OutlineDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var existingChapters = await store.GetChapters(args.NovelId, ct);
        ThrowIfCancelled(ct);

        var created = 0;
        var skipped = 0;
        var startOrder = existingChapters.Count > 0 ? existingChapters.Max(c => c.Order) + 1 : 1;

        foreach (var title in args.ChapterTitles)
        {
            // 查找是否已有同标题章节，跳过以保留内容和 ID
            var existing = existingChapters.Find(c => c.Title == title);
            if (existing is not null)
            {
                skipped++;
                continue;
            }

            var chapter = new Chapter
            {
                NovelId = args.NovelId,
                Title = title,
                Order = startOrder + created,
                Content = "",
                Status = ChapterStatus.Draft,
                VolumeId = string.IsNullOrWhiteSpace(args.VolumeId) ? null : args.VolumeId,
            };
            await store.SaveChapter(chapter, ct);
            ThrowIfCancelled(ct);
            if (kernel is not null)
            {
                var (doc, chunks) = kernel.Builder.BuildChapterBodyChunks(chapter);
                await kernel.IndexDocumentAsync(doc, chunks, ct);
            }
            ThrowIfCancelled(ct);
            created++;
        }

        var msg =
            skipped > 0
                ? $"已创建 {created} 个新章节大纲（跳过 {skipped} 个已有章节）"
                : $"已创建 {created} 个章节大纲";
        return new AgentToolResult<OutlineDetails>
        {
            Content = [new TextContent(msg)],
            Details = new OutlineDetails { ChapterCount = created },
        };
    }
}

public sealed record CreateOutlineParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("按顺序排列的章节标题列表")]
    public string[] ChapterTitles { get; init; } = [];

    [Description("归属分卷 ID（大纲二层；留空 = 未分卷。可先用 manage_volume 建卷）")]
    public string? VolumeId { get; init; }
}

// ── rewrite_selection ──

/// <summary>
/// 重写段落工具：重写章节中的选中段落
/// </summary>
public sealed class RewriteSelectionTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<RewriteSelectionParams, RewriteSelectionDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "rewrite_selection";
    public override string[] Aliases => ["改写选中", "重写片段"];
    public override string[] ExampleQueries => ["重写第一章开篇五百字"];

    /// <summary>工具显示名称</summary>
    public override string Label => "重写段落";
    public override string Description => "重写章节中的选中段落，用新文本替换旧文本";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.FileEdit;

    /// <summary>
    /// 执行重写段落操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">重写参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>重写结果</returns>
    public override async Task<AgentToolResult<RewriteSelectionDetails>> ExecuteAsync(
        string toolCallId,
        RewriteSelectionParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RewriteSelectionDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var chapter = await store.GetChapter(args.ChapterId, ct);
        ThrowIfCancelled(ct);
        if (chapter is null)
            return new AgentToolResult<RewriteSelectionDetails>
            {
                Content = [new TextContent($"章节 {args.ChapterId} 不存在")],
                IsError = true,
            };

        if (string.IsNullOrEmpty(args.OldText))
            return new AgentToolResult<RewriteSelectionDetails>
            {
                Content = [new TextContent("未提供要替换的旧文本")],
                IsError = true,
            };

        var newContent = chapter.Content.Replace(args.OldText, args.NewText ?? "");
        if (newContent == chapter.Content)
            return new AgentToolResult<RewriteSelectionDetails>
            {
                Content = [new TextContent("未找到匹配的旧文本")],
                IsError = true,
            };

        var oldLength = chapter.Content.Length;
        chapter.Content = newContent;
        await store.SaveChapter(chapter, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildChapterBodyChunks(chapter);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        await EvolutionHook.RecordEvolutionSafeAsync(store, chapter.NovelId, chapter, ct);
        ThrowIfCancelled(ct);
        var diff = newContent.Length - oldLength;
        return new AgentToolResult<RewriteSelectionDetails>
        {
            Content = [new TextContent($"章节「{chapter.Title}」段落已重写 ({diff:+0;-0} 字变化)")],
            Details = new RewriteSelectionDetails
            {
                Id = chapter.Id,
                NewLength = newContent.Length,
            },
        };
    }
}

public sealed record RewriteSelectionParams
{
    [Description("章节ID（GUID，如 7b2be66d2f56；也支持章节号如 1、2）")]
    public string ChapterId { get; init; } = "";

    [Description("要被替换的旧文本")]
    public string OldText { get; init; } = "";

    [Description("替换后的新文本")]
    public string? NewText { get; init; }
}

public sealed record RewriteSelectionDetails : IChapterWriteResult
{
    public string Id { get; init; } = "";
    public int NewLength { get; init; }
    int IChapterWriteResult.WordCount => NewLength;
}

// ── suggest_continuation ──

/// <summary>
/// 建议续写方向工具：基于当前小说状态分析并建议后续发展可能方向
/// </summary>
public sealed class SuggestContinuationTool(INovelStore store)
    : AgentTool<SuggestContinuationParams, SuggestContinuationDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "suggest_continuation";
    public override string[] Aliases => ["续写建议", "接续", "接下来写什么"];
    public override string[] ExampleQueries => ["主角刚离开宗门，接下来写什么"];

    /// <summary>工具显示名称</summary>
    public override string Label => "建议续写方向";
    public override string Description =>
        "基于当前小说状态（大纲、角色、情节线）分析并建议后续发展可能方向";
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行建议续写方向操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">续写参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>续写建议</returns>
    public override async Task<AgentToolResult<SuggestContinuationDetails>> ExecuteAsync(
        string toolCallId,
        SuggestContinuationParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<SuggestContinuationDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var chaptersTask = store.GetChapters(args.NovelId, ct);
        var charactersTask = store.GetCharacters(args.NovelId, ct);
        var openPlotsTask = store.GetOpenPlotlines(args.NovelId, ct);
        await Task.WhenAll(chaptersTask, charactersTask, openPlotsTask);
        ThrowIfCancelled(ct);
        var chapters = chaptersTask.Result;
        var characters = charactersTask.Result;
        var openPlots = openPlotsTask.Result;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("当前小说状态分析：");
        sb.AppendLine($"- 已完成 {chapters.Count} 章");
        sb.AppendLine($"- {characters.Count} 个角色");
        sb.AppendLine($"- {openPlots.Count} 条未解决的情节线/伏笔");

        if (openPlots.Count > 0)
        {
            var chMap = chapters.ToDictionary(c => c.Id, c => $"第{c.Order}章 {c.Title}");
            sb.AppendLine("\n建议优先处理以下情节线：");
            foreach (var p in openPlots.OrderBy(p => p.PlannedRevealChapterId))
            {
                var reveal =
                    p.PlannedRevealChapterId != null
                    && chMap.TryGetValue(p.PlannedRevealChapterId, out var rn)
                        ? rn
                        : "未设定";
                sb.AppendLine($"- {p.Name} (类型: {p.Type}, 计划揭示: {reveal})");
            }
        }

        if (chapters.Count > 0)
        {
            var lastChapter = chapters.MaxBy(c => c.Order);
            if (lastChapter is not null)
                sb.AppendLine(
                    $"\n当前进度: 第{lastChapter.Order}章「{lastChapter.Title}」({lastChapter.Status})"
                );
        }

        sb.AppendLine("\n请基于以上信息，建议接下来 2-3 个可行的续写方向：");

        return new AgentToolResult<SuggestContinuationDetails>
        {
            Content = [new TextContent(sb.ToString())],
            Details = new SuggestContinuationDetails
            {
                ChapterCount = chapters.Count,
                OpenPlotlineCount = openPlots.Count,
            },
        };
    }
}

public sealed record SuggestContinuationParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}

public sealed record SuggestContinuationDetails
{
    public int ChapterCount { get; init; }
    public int OpenPlotlineCount { get; init; }
}

// ── delete_chapter ──

/// <summary>
/// 删除章节工具：删除指定章节
/// </summary>
public sealed class DeleteChapterTool(INovelStore store)
    : AgentTool<DeleteChapterParams, DeleteChapterDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "delete_chapter";

    /// <summary>工具显示名称</summary>
    public override string Label => "删除章节";
    public override string Description => "删除指定章节";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行删除章节操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">删除参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>删除结果</returns>
    public override async Task<AgentToolResult<DeleteChapterDetails>> ExecuteAsync(
        string toolCallId,
        DeleteChapterParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<DeleteChapterDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var chapter = await store.GetChapter(args.ChapterId, ct);
        ThrowIfCancelled(ct);
        if (chapter is null)
            return new AgentToolResult<DeleteChapterDetails>
            {
                Content = [new TextContent($"章节 {args.ChapterId} 不存在")],
                IsError = true,
            };
        var title = chapter.Title;
        // 已发布章必须先撤回（回滚实体与变更集），再删除。
        var service = new Core.Evolution.ChapterLifecycleService(store);
        var deleted = await service.DeleteChapterAsync(chapter.NovelId, chapter.Id, ct);
        if (!deleted.Success)
        {
            return new AgentToolResult<DeleteChapterDetails>
            {
                Content = [new TextContent($"删除失败：{deleted.Error}")],
                IsError = true,
            };
        }
        ThrowIfCancelled(ct);
        return new AgentToolResult<DeleteChapterDetails>
        {
            Content = [new TextContent($"章节「{title}」已删除")],
            Details = new DeleteChapterDetails { Id = args.ChapterId },
        };
    }
}

public sealed record DeleteChapterParams
{
    [Description("要删除的章节ID（GUID，如 7b2be66d2f56；也支持章节号如 1、2）")]
    public string ChapterId { get; init; } = "";
}

public sealed record DeleteChapterDetails
{
    public string Id { get; init; } = "";
}
