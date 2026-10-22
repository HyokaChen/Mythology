using System.ComponentModel;
using System.Text;
using Core.Agent;
using Core.Data;
using Core.Notes;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>capture_note 参数。</summary>
public sealed class CaptureNoteParams
{
    [Description("小说项目ID，留空则用当前打开的小说")]
    public string NovelId { get; init; } = "";

    [Description("标题（可空，空则取正文前 16 字）")]
    public string Title { get; init; } = "";

    [Description("笔记正文（作者原话，不要改写）")]
    public string Body { get; init; } = "";

    [Description("标签（可空）")]
    public string[] Tags { get; init; } = [];
}

/// <summary>capture_note 结果详情。</summary>
public sealed record CaptureNoteDetails(string NoteId, int? CapturedChapterOrder);

/// <summary>
/// 记一笔：把作者随口说的想法 / 设定 / 桥段记成一条笔记（默认已确认，参与远期推演）。
/// 查看、确认候选、改状态走 <c>manage_note</c>。
/// </summary>
public sealed class CaptureNoteTool(
    INovelStore store,
    RetrievalKernel? kernel = null,
    Agent? agent = null
) : AgentTool<CaptureNoteParams, CaptureNoteDetails>
{
    public override string Name => "capture_note";
    public override string[] Aliases => ["记笔记", "记一下"];
    public override string Label => "记笔记";
    public override string Description =>
        "把作者随口说的想法/设定/桥段记成一条笔记（默认已确认，参与远期推演）；查看、确认候选、修改状态用 manage_note。";
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string? EntityType => "note";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public override async Task<AgentToolResult<CaptureNoteDetails>> ExecuteAsync(
        string toolCallId,
        CaptureNoteParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<CaptureNoteDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);

        var novelId = string.IsNullOrWhiteSpace(args.NovelId)
            ? agent?.CurrentNovelId
            : args.NovelId;
        if (string.IsNullOrEmpty(novelId))
            return Err("当前没有打开的小说项目，请提供 novel_id。");

        var body = (args.Body ?? "").Trim();
        if (body.Length == 0)
            return Err("正文不能为空。");

        var title = (args.Title ?? "").Trim();
        if (title.Length == 0)
            title = body[..Math.Min(16, body.Length)];

        var structure = await store.GetStructure(novelId, ct);
        var capturedOrder = NoteCaptureScanner.MaxWrittenOrderOf(structure);
        var note = new AuthorNote
        {
            NovelId = novelId,
            Title = title,
            Body = body,
            Tags = [.. (args.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0)],
            Status = NoteStatus.Active,
            CapturedChapterOrder = capturedOrder,
            Source = AuditSource.Ai,
        };
        await store.SaveNote(note, ct);

        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildAuthorNote(note);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }

        return Ok($"已记笔记 [{note.Id[..Math.Min(8, note.Id.Length)]}]：{note.Title}", note);
    }

    private static AgentToolResult<CaptureNoteDetails> Ok(string text, AuthorNote note) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new CaptureNoteDetails(note.Id, note.CapturedChapterOrder),
        };

    private static AgentToolResult<CaptureNoteDetails> Err(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

/// <summary>manage_note 参数。</summary>
public sealed class ManageNoteParams
{
    [Description("动作：list | get | update | confirm | done | drop")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则用当前打开的小说")]
    public string NovelId { get; init; } = "";

    [Description("笔记 ID（get/update/confirm/done/drop 必填）")]
    public string NoteId { get; init; } = "";

    [Description("新标题（update）")]
    public string? Title { get; init; }

    [Description("新正文（update）")]
    public string? Body { get; init; }

    [Description("新标签（update，传空数组=清空）")]
    public string[]? Tags { get; init; }

    [Description("列表状态过滤：all | inbox | active | done | dropped（默认 all）")]
    public string StatusFilter { get; init; } = "all";
}

/// <summary>manage_note 结果详情。</summary>
public sealed record ManageNoteDetails(int Total, int Returned, string? NoteId);

/// <summary>
/// 笔记管理：列表 / 查看 / 修改 / 状态流转（收件箱 → 已确认 → 已落地 / 已废弃）。
/// 笔记写入的唯一入口（<c>edit_entities</c> 不接 Note，避免两套写路径）。
/// </summary>
public sealed class ManageNoteTool(
    INovelStore store,
    RetrievalKernel? kernel = null,
    Agent? agent = null
) : AgentTool<ManageNoteParams, ManageNoteDetails>
{
    /// <summary>单次列表最多返回行数。</summary>
    private const int MaxRows = 50;

    public override string Name => "manage_note";
    public override string[] Aliases => ["笔记", "笔记列表"];
    public override string Label => "笔记管理";
    public override string Description =>
        "管理作者随手记：list 列出（收件箱候选优先）、get 查看全文、update 修改标题/正文/标签、"
        + "confirm 确认候选、done 标记已落地、drop 废弃（软删除）。笔记是远期推演的一条线索，"
        + "只有「已确认」会进推演。";
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string? EntityType => "note";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<ManageNoteDetails>> ExecuteAsync(
        string toolCallId,
        ManageNoteParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageNoteDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);

        var novelId = string.IsNullOrWhiteSpace(args.NovelId)
            ? agent?.CurrentNovelId
            : args.NovelId;
        if (string.IsNullOrEmpty(novelId))
            return Err("当前没有打开的小说项目，请提供 novel_id。");

        var notes = await store.GetNotes(novelId, ct);
        var action = (args.Action ?? "list").Trim().ToLowerInvariant();

        return action switch
        {
            "list" => List(notes, args.StatusFilter),
            "get" => Get(notes, args.NoteId),
            "update" => await UpdateAsync(novelId, notes, args, ct),
            "confirm" => await TransitionAsync(novelId, notes, args.NoteId, NoteStatus.Active, ct),
            "done" => await TransitionAsync(novelId, notes, args.NoteId, NoteStatus.Done, ct),
            "drop" => await TransitionAsync(novelId, notes, args.NoteId, NoteStatus.Dropped, ct),
            _ => Err(
                $"未知动作「{args.Action}」，可用：list | get | update | confirm | done | drop。"
            ),
        };
    }

    private static AgentToolResult<ManageNoteDetails> List(
        List<AuthorNote> notes,
        string statusFilter
    )
    {
        var filter = (statusFilter ?? "all").Trim().ToLowerInvariant();
        NoteStatus? status = filter switch
        {
            "inbox" => NoteStatus.Inbox,
            "active" => NoteStatus.Active,
            "done" => NoteStatus.Done,
            "dropped" => NoteStatus.Dropped,
            _ => null,
        };

        var filtered = (status is { } s ? notes.Where(n => n.Status == s) : notes)
            .OrderBy(n => n.Status == NoteStatus.Inbox ? 0 : 1)
            .ThenByDescending(n => n.UpdatedAt)
            .ToList();
        var inboxCount = notes.Count(n => n.Status == NoteStatus.Inbox);

        if (notes.Count == 0)
            return Ok("暂无笔记。", new ManageNoteDetails(0, 0, null));

        if (filtered.Count == 0)
            return Ok($"状态「{filter}」下暂无笔记。", new ManageNoteDetails(0, 0, null));

        var sb = new StringBuilder();
        foreach (var note in filtered.Take(MaxRows))
        {
            sb.Append("- [")
                .Append(StatusLabel(note.Status))
                .Append(']')
                .Append(note.Title.Length > 0 ? note.Title : "（无标题）")
                .Append('｜')
                .AppendLine(Excerpt(note.Body, 40));
        }
        sb.Append("共 ")
            .Append(filtered.Count)
            .Append(" 条（候选 ")
            .Append(inboxCount)
            .Append(" 条需确认）");

        var returned = Math.Min(filtered.Count, MaxRows);
        return Ok(sb.ToString(), new ManageNoteDetails(filtered.Count, returned, null));
    }

    private static AgentToolResult<ManageNoteDetails> Get(List<AuthorNote> notes, string noteId)
    {
        var note = notes.FirstOrDefault(n => n.Id == noteId);
        if (note is null)
            return Err("未找到该笔记。");

        var sb = new StringBuilder();
        sb.Append("《").Append(note.Title).AppendLine("》");
        sb.Append("状态：").Append(StatusLabel(note.Status));
        if (note.CapturedChapterOrder is { } order)
            sb.Append(" · 记录于第").Append(order).Append("章");
        if (note.Tags.Count > 0)
            sb.Append(" · 标签：").Append(string.Join("、", note.Tags));
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(note.Body);

        return Ok(sb.ToString(), new ManageNoteDetails(1, 1, note.Id));
    }

    private async Task<AgentToolResult<ManageNoteDetails>> UpdateAsync(
        string novelId,
        List<AuthorNote> notes,
        ManageNoteParams args,
        CancellationToken ct
    )
    {
        var note = notes.FirstOrDefault(n => n.Id == args.NoteId);
        if (note is null)
            return Err("未找到该笔记。");

        if (args.Body is not null && args.Body.Trim().Length == 0)
            return Err("正文不能为空。");

        if (args.Title is not null)
            note.Title = args.Title.Trim();
        if (args.Body is not null)
            note.Body = args.Body.Trim();
        if (args.Tags is not null)
            note.Tags = [.. args.Tags.Select(t => t.Trim()).Where(t => t.Length > 0)];

        await SaveAndReindexAsync(novelId, note, ct);
        return Ok($"已更新笔记：{note.Title}", new ManageNoteDetails(1, 1, note.Id));
    }

    private async Task<AgentToolResult<ManageNoteDetails>> TransitionAsync(
        string novelId,
        List<AuthorNote> notes,
        string noteId,
        NoteStatus target,
        CancellationToken ct
    )
    {
        var note = notes.FirstOrDefault(n => n.Id == noteId);
        if (note is null)
            return Err("未找到该笔记。");

        note.Status = target;
        await SaveAndReindexAsync(novelId, note, ct);
        return Ok(
            $"笔记「{note.Title}」→ {StatusLabel(target)}",
            new ManageNoteDetails(1, 1, note.Id)
        );
    }

    private async Task SaveAndReindexAsync(string novelId, AuthorNote note, CancellationToken ct)
    {
        await store.SaveNote(note, ct);
        if (kernel is null)
            return;
        var (doc, chunks) = kernel.Builder.BuildAuthorNote(note);
        await kernel.IndexDocumentAsync(doc, chunks, ct);
    }

    private static string StatusLabel(NoteStatus status) =>
        status switch
        {
            NoteStatus.Inbox => "收件箱",
            NoteStatus.Active => "已确认",
            NoteStatus.Done => "已落地",
            _ => "已废弃",
        };

    /// <summary>正文摘要：空白归一后截断（换行会打断列表行）。</summary>
    private static string Excerpt(string body, int max)
    {
        var flat = string.Join(
            ' ',
            (body ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static AgentToolResult<ManageNoteDetails> Ok(string text, ManageNoteDetails details) =>
        new() { Content = [new TextContent(text)], Details = details };

    private static AgentToolResult<ManageNoteDetails> Err(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}
