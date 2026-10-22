using Core.Data;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 笔记的检索文档契约：文本含标题/正文/标签，metadata 与过滤口径（PascalCase 状态）一致。
/// </summary>
public sealed class AuthorNoteDocumentTests
{
    private static AuthorNote Note() =>
        new()
        {
            Id = "note-doc",
            NovelId = "novel-doc",
            Title = "半块玉的递法",
            Body = "第7章 让北境使者递出半块玉",
            Tags = ["伏笔", "信物"],
            Status = NoteStatus.Active,
            CapturedChapterOrder = 7,
            SessionId = "sess-9",
        };

    [Fact]
    public void BuildAuthorNote_TextContainsTitleBodyTags()
    {
        var (doc, chunks) = new DocumentBuilder().BuildAuthorNote(Note());
        var text = Assert.Single(chunks).Text;

        Assert.Contains("半块玉的递法", text, StringComparison.Ordinal);
        Assert.Contains("第7章 让北境使者递出半块玉", text, StringComparison.Ordinal);
        Assert.Contains("伏笔", text, StringComparison.Ordinal);
        Assert.Equal(DocumentKind.AuthorNote, doc.DocumentKind);
        Assert.Equal("Note", doc.SourceEntityType);
        Assert.Equal("note-doc", doc.SourceEntityId);
        Assert.Equal(RetrievalDomain.NovelKnowledge, doc.Domain);
    }

    [Fact]
    public void BuildAuthorNote_MetadataUsesPascalCaseStatusAndNoteFields()
    {
        var (doc, _) = new DocumentBuilder().BuildAuthorNote(Note());
        using var json = System.Text.Json.JsonDocument.Parse(doc.MetadataJson);
        var root = json.RootElement;

        Assert.Equal("Active", root.GetProperty("entity-status").GetString());
        Assert.Equal("半块玉的递法", root.GetProperty("note-title").GetString());
        Assert.Equal(7, root.GetProperty("captured-chapter-order").GetInt32());
        Assert.Equal("sess-9", root.GetProperty("note-session-id").GetString());
    }

    [Fact]
    public void AuthorNoteKind_ChineseLabel()
    {
        Assert.Equal("笔记", DocumentKind.AuthorNote.ToChineseLabel());
    }
}
