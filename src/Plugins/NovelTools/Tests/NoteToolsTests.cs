using System.Text.Json;
using Core.Data;
using Core.Vector;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 笔记工具：capture_note 记一笔（默认已确认）；manage_note 列表/查看/改/状态流转。
/// </summary>
public sealed class NoteToolsTests
{
    private static FakeNovelStore CreateStore()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(new Novel { Id = "n1", Title = "笔记工具测试" });
        store.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = "n1",
                Order = 1,
                Content = "正文",
            }
        );
        store.SaveChapter(
            new Chapter
            {
                Id = "c2",
                NovelId = "n1",
                Order = 2,
                Content = "",
            }
        );
        return store;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task CaptureNote_WithoutNovel_ReturnsError()
    {
        var tool = new CaptureNoteTool(new FakeNovelStore());

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"body":"记一下"}"""),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Contains("没有打开的小说项目", ToolText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureNote_EmptyBody_ReturnsError()
    {
        var tool = new CaptureNoteTool(CreateStore());

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","body":"   "}"""),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Contains("正文不能为空", ToolText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureNote_SavesActiveNoteWithWrittenOrderAndIndexes()
    {
        var store = CreateStore();
        var retrieval = new InMemoryRetrievalStore();
        await using var kernel = new RetrievalKernel(retrieval, new HashEmbedder());
        var tool = new CaptureNoteTool(store, kernel);

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","body":"决战前夜让北境使者递出半块玉，并让雨一直下"}"""),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var note = Assert.Single(await store.GetNotes("n1"));
        Assert.Equal(NoteStatus.Active, note.Status);
        Assert.Equal(AuditSource.Ai, note.Source);
        Assert.Equal(1, note.CapturedChapterOrder); // 只有第1章有正文
        Assert.Equal(note.Body[..16], note.Title);

        var ids = await retrieval.GetIndexedDocumentIdsByKindAsync(
            "n1",
            DocumentKind.AuthorNote,
            TestContext.Current.CancellationToken
        );
        Assert.Contains(note.Id, ids);
    }

    [Fact]
    public async Task ManageNote_List_PutsInboxFirstAndReportsCounts()
    {
        var store = CreateStore();
        await store.SaveNote(
            new AuthorNote
            {
                NovelId = "n1",
                Title = "已确认的",
                Body = "正文",
                Status = NoteStatus.Active,
                UpdatedAt = DateTime.UtcNow,
            }
        );
        await store.SaveNote(
            new AuthorNote
            {
                NovelId = "n1",
                Title = "待确认的",
                Body = "正文",
                Status = NoteStatus.Inbox,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
            }
        );
        var tool = new ManageNoteTool(store);

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","action":"list"}"""),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = ToolText(result);
        Assert.StartsWith("- [收件箱]待确认的", text, StringComparison.Ordinal);
        Assert.Contains("共 2 条（候选 1 条需确认）", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManageNote_List_StatusFilterAndEmptyStates()
    {
        var store = CreateStore();
        var tool = new ManageNoteTool(store);

        var empty = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","action":"list"}"""),
            TestContext.Current.CancellationToken
        );
        Assert.Equal("暂无笔记。", ToolText(empty));

        await store.SaveNote(
            new AuthorNote
            {
                NovelId = "n1",
                Title = "甲",
                Body = "正文",
                Status = NoteStatus.Done,
            }
        );
        var filtered = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","action":"list","status-filter":"inbox"}"""),
            TestContext.Current.CancellationToken
        );
        Assert.Contains("暂无笔记", ToolText(filtered), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManageNote_Transitions_MoveStatus()
    {
        var store = CreateStore();
        await store.SaveNote(
            new AuthorNote
            {
                NovelId = "n1",
                Title = "候选",
                Body = "正文",
                Status = NoteStatus.Inbox,
            }
        );
        var id = (await store.GetNotes("n1")).Single().Id;
        var tool = new ManageNoteTool(store);

        await tool.ExecuteAsync(
            "call",
            Json($$"""{"novel-id":"n1","action":"confirm","note-id":"{{id}}"}"""),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(NoteStatus.Active, (await store.GetNotes("n1")).Single().Status);

        await tool.ExecuteAsync(
            "call",
            Json($$"""{"novel-id":"n1","action":"done","note-id":"{{id}}"}"""),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(NoteStatus.Done, (await store.GetNotes("n1")).Single().Status);

        await tool.ExecuteAsync(
            "call",
            Json($$"""{"novel-id":"n1","action":"drop","note-id":"{{id}}"}"""),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(NoteStatus.Dropped, (await store.GetNotes("n1")).Single().Status);
    }

    [Fact]
    public async Task ManageNote_Update_OnlyOverwritesProvidedFields()
    {
        var store = CreateStore();
        await store.SaveNote(
            new AuthorNote
            {
                NovelId = "n1",
                Title = "原标题",
                Body = "原正文",
                Tags = ["旧标签"],
                Status = NoteStatus.Active,
            }
        );
        var id = (await store.GetNotes("n1")).Single().Id;
        var tool = new ManageNoteTool(store);

        await tool.ExecuteAsync(
            "call",
            Json($$"""{"novel-id":"n1","action":"update","note-id":"{{id}}","title":"新标题"}"""),
            TestContext.Current.CancellationToken
        );

        var note = (await store.GetNotes("n1")).Single();
        Assert.Equal("新标题", note.Title);
        Assert.Equal("原正文", note.Body);
        Assert.Equal(["旧标签"], note.Tags);
    }

    [Fact]
    public async Task ManageNote_Get_ReturnsFullBody_AndErrorsWhenMissing()
    {
        var store = CreateStore();
        await store.SaveNote(
            new AuthorNote
            {
                NovelId = "n1",
                Title = "全文",
                Body = "一整段完整正文",
                Tags = ["伏笔"],
                CapturedChapterOrder = 4,
                Status = NoteStatus.Active,
            }
        );
        var id = (await store.GetNotes("n1")).Single().Id;
        var tool = new ManageNoteTool(store);

        var found = await tool.ExecuteAsync(
            "call",
            Json($$"""{"novel-id":"n1","action":"get","note-id":"{{id}}"}"""),
            TestContext.Current.CancellationToken
        );
        var text = ToolText(found);
        Assert.Contains("一整段完整正文", text, StringComparison.Ordinal);
        Assert.Contains("记录于第4章", text, StringComparison.Ordinal);

        var missing = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","action":"get","note-id":"nope"}"""),
            TestContext.Current.CancellationToken
        );
        Assert.True(missing.IsError);
        Assert.Contains("未找到该笔记", ToolText(missing), StringComparison.Ordinal);
    }

    private static string ToolText(Core.Tools.AgentToolResult result) =>
        string.Concat(result.Content.OfType<Core.Shared.TextContent>().Select(c => c.Text));
}
