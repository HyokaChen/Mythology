using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 作者随手记的落盘契约：notes/*.yaml per-file（rename 语义）、字段往返、软删除只改状态。
/// </summary>
public sealed class AuthorNoteStoreTests : IDisposable
{
    private const string NovelId = "note-novel";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public AuthorNoteStoreTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"notes-{Guid.NewGuid():N}");
        _root = Path.Combine(tmp, "novels");
        _myth = Path.Combine(tmp, "mythology");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_myth);
        _store = new FileNovelStore(_root, _myth);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private Task SeedNovelAsync() =>
        _store.SaveNovel(new Novel { Id = NovelId, Title = "笔记测试" });

    [Fact]
    public async Task SaveThenGetNotes_RoundTripsAllFields()
    {
        await SeedNovelAsync();
        var note = new AuthorNote
        {
            Id = "note-a",
            NovelId = NovelId,
            Title = "半块玉的递法",
            Body = "第7章 让北境使者递出半块玉",
            Tags = ["伏笔", "第7章"],
            Status = NoteStatus.Inbox,
            CapturedChapterOrder = 5,
            SessionId = "sess-1",
            Source = AuditSource.Ai,
        };

        await _store.SaveNote(note);
        var notes = await _store.GetNotes(NovelId);

        var loaded = Assert.Single(notes);
        Assert.Equal(note.Id, loaded.Id);
        Assert.Equal(NovelId, loaded.NovelId);
        Assert.Equal("半块玉的递法", loaded.Title);
        Assert.Equal("第7章 让北境使者递出半块玉", loaded.Body);
        Assert.Equal(["伏笔", "第7章"], loaded.Tags);
        Assert.Equal(NoteStatus.Inbox, loaded.Status);
        Assert.Equal(5, loaded.CapturedChapterOrder);
        Assert.Equal("sess-1", loaded.SessionId);
        Assert.Equal(AuditSource.Ai, loaded.Source);
    }

    [Fact]
    public async Task SaveNote_TitleChange_ReplacesOldFile()
    {
        await SeedNovelAsync();
        var note = new AuthorNote
        {
            Id = "note-rename",
            NovelId = NovelId,
            Title = "旧标题",
            Body = "正文",
        };
        await _store.SaveNote(note);

        note.Title = "新标题";
        await _store.SaveNote(note);

        var dir = Path.Combine(_root, "笔记测试", "notes");
        var files = Directory.GetFiles(dir, "*.yaml");
        var single = Assert.Single(files);
        Assert.Contains("新标题", Path.GetFileName(single));

        var notes = await _store.GetNotes(NovelId);
        Assert.Equal("新标题", Assert.Single(notes).Title);
    }

    [Fact]
    public async Task SaveNote_SoftDelete_KeepsFileAndStatus()
    {
        await SeedNovelAsync();
        await _store.SaveNote(
            new AuthorNote
            {
                Id = "note-drop",
                NovelId = NovelId,
                Title = "废弃的灵感",
                Body = "正文",
            }
        );

        var note = Assert.Single(await _store.GetNotes(NovelId));
        note.Status = NoteStatus.Dropped;
        await _store.SaveNote(note);

        Assert.Equal(NoteStatus.Dropped, Assert.Single(await _store.GetNotes(NovelId)).Status);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "笔记测试", "notes"), "*.yaml"));
    }

    [Fact]
    public async Task GetNotes_UnknownNovel_ReturnsEmpty()
    {
        Assert.Empty(await _store.GetNotes("no-such-novel"));
    }

    [Fact]
    public async Task SaveNote_NullCapturedOrder_StaysNull()
    {
        await SeedNovelAsync();
        await _store.SaveNote(
            new AuthorNote
            {
                Id = "note-null-order",
                NovelId = NovelId,
                Title = "无章",
                Body = "正文",
                CapturedChapterOrder = null,
            }
        );

        Assert.Null(Assert.Single(await _store.GetNotes(NovelId)).CapturedChapterOrder);
    }
}
