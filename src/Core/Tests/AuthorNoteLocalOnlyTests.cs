using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 笔记只在本机：云同步枚举必须跳过 notes/（否则「随手记」会被推到云端）。
/// </summary>
public sealed class AuthorNoteLocalOnlyTests : IDisposable
{
    private readonly string _root;
    private readonly FileNovelStore _store;

    public AuthorNoteLocalOnlyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"notes-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "novels"));
        Directory.CreateDirectory(Path.Combine(_root, "mythology"));
        _store = new FileNovelStore(
            Path.Combine(_root, "novels"),
            Path.Combine(_root, "mythology")
        );
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }

    [Fact]
    public async Task EnumerateAsync_SkipsNotesDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SaveNovel(new Novel { Id = "nov-notes", Title = "本机笔记" }, ct);
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "nov-notes",
                Order = 1,
                Title = "第一章",
                Content = "正文",
            },
            ct
        );
        await _store.SaveNote(
            new AuthorNote
            {
                Id = "note-loc",
                NovelId = "nov-notes",
                Title = "只在本地",
                Body = "不该上云",
                Status = NoteStatus.Active,
            },
            ct
        );

        var docs = await SyncDocumentEnumerator.EnumerateAsync(
            Path.Combine(_root, "novels"),
            "nov-notes",
            ct
        );

        Assert.NotEmpty(docs);
        Assert.DoesNotContain(
            docs,
            d => d.RelativePath.Contains("/notes/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(docs, d => d.DocumentType == "note");
        // 章节仍照常同步
        Assert.Contains(docs, d => d.DocumentType == "chapter");
        // 笔记本身仍可读
        Assert.Single(await _store.GetNotes("nov-notes", ct));
    }
}
