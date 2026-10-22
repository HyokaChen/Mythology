using Core.Data;
using Core.Notes;
using Core.Vector;
using LlmTornado.Code;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>
/// 会话随手记捕获：只认用户原话里的触发句、落收件箱候选、去重、单次上限。
/// </summary>
public sealed class NoteCaptureScannerTests : IDisposable
{
    private const string NovelId = "scan-novel";
    private readonly string _tmp;
    private readonly FileNovelStore _store;

    public NoteCaptureScannerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"notescan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tmp, "novels"));
        Directory.CreateDirectory(Path.Combine(_tmp, "mythology"));
        _store = new FileNovelStore(Path.Combine(_tmp, "novels"), Path.Combine(_tmp, "mythology"));
        _store.SaveNovel(new Novel { Id = NovelId, Title = "捕获测试" }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }

    private static LlmChatMessage User(string text) => new(ChatMessageRoles.User, text);

    private static LlmChatMessage Assistant(string text) => new(ChatMessageRoles.Assistant, text);

    [Fact]
    public async Task Scan_UserSuggestionSentence_CreatesInboxCandidate()
    {
        var scanner = new NoteCaptureScanner(_store);

        var added = await scanner.ScanAsync(
            NovelId,
            "sess-a",
            [User("记一下：决战前夜让北境使者递出半块玉")],
            0,
            capturedChapterOrder: 5,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, added);
        var note = Assert.Single(await _store.GetNotes(NovelId));
        Assert.Equal(NoteStatus.Inbox, note.Status);
        Assert.Equal(AuditSource.Ai, note.Source);
        Assert.Equal("sess-a", note.SessionId);
        Assert.Equal(5, note.CapturedChapterOrder);
        Assert.Contains("决战前夜", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_AssistantAndSystemBlocks_AreIgnored()
    {
        var scanner = new NoteCaptureScanner(_store);

        var added = await scanner.ScanAsync(
            NovelId,
            "sess-b",
            [
                Assistant("记一下：这条是模型说的"),
                User("[远期推演] 记一下：系统注入块不是作者原话"),
            ],
            0,
            null,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, added);
        Assert.Empty(await _store.GetNotes(NovelId));
    }

    [Fact]
    public async Task Scan_DuplicateAgainstExistingNote_IsSkipped()
    {
        await _store.SaveNote(
            new AuthorNote
            {
                NovelId = NovelId,
                Title = "决战前夜下雨",
                Body = "记一下：决战前夜下雨",
                Status = NoteStatus.Active,
            },
            TestContext.Current.CancellationToken
        );
        var scanner = new NoteCaptureScanner(_store);

        var added = await scanner.ScanAsync(
            NovelId,
            "sess-c",
            [User("记一下：决战前夜下雨")],
            0,
            null,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, added);
        Assert.Single(await _store.GetNotes(NovelId));
    }

    [Fact]
    public async Task Scan_StartIndex_SkipsAlreadyScannedMessages()
    {
        var scanner = new NoteCaptureScanner(_store);
        LlmChatMessage[] messages =
        [
            User("记一下：青铜钥匙埋在井底"),
            User("备忘：北境使者说话带口音"),
        ];

        var added = await scanner.ScanAsync(
            NovelId,
            "sess-d",
            messages,
            1,
            null,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, added);
        var note = Assert.Single(await _store.GetNotes(NovelId));
        Assert.Contains("北境使者", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_AtMostThreeNotesPerScan()
    {
        var scanner = new NoteCaptureScanner(_store);
        LlmChatMessage[] messages =
        [
            User("记一下：第一件事是井底有钥匙"),
            User("记一下：第二件事是北境有雪"),
            User("记一下：第三件事是老者咳嗽"),
            User("记一下：第四件事是玉碎成两半"),
        ];

        var added = await scanner.ScanAsync(
            NovelId,
            "sess-e",
            messages,
            0,
            null,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(3, added);
        Assert.Equal(3, (await _store.GetNotes(NovelId)).Count);
    }

    [Fact]
    public async Task Scan_WritesIndexDocument_WhenKernelProvided()
    {
        var retrieval = new InMemoryRetrievalStore();
        await using var kernel = new RetrievalKernel(retrieval, new HashEmbedder());
        var scanner = new NoteCaptureScanner(_store, kernel);

        await scanner.ScanAsync(
            NovelId,
            "sess-f",
            [User("记一下：井底埋着青铜钥匙")],
            0,
            null,
            TestContext.Current.CancellationToken
        );

        var ids = await retrieval.GetIndexedDocumentIdsByKindAsync(
            NovelId,
            DocumentKind.AuthorNote,
            TestContext.Current.CancellationToken
        );
        Assert.Single(ids);
    }

    [Fact]
    public async Task MaxWrittenOrderOf_UsesCharCountOnly()
    {
        var structure = new Core.Novels.NovelStructure(
            new Novel { Id = NovelId },
            [],
            [
                new Chapter
                {
                    Id = "c1",
                    Order = 1,
                    Content = "x",
                },
                new Chapter
                {
                    Id = "c2",
                    Order = 2,
                    Content = "",
                },
                new Chapter
                {
                    Id = "c5",
                    Order = 5,
                    Content = "正文",
                },
            ],
            []
        );

        Assert.Equal(5, NoteCaptureScanner.MaxWrittenOrderOf(structure));
        Assert.Null(NoteCaptureScanner.MaxWrittenOrderOf(null));
    }
}
