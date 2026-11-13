using System.Text.Json;
using Core.Agent;
using Core.Data;
using Core.Shared;
using LlmTornado.Code;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>
/// A2-lite：append-only retry 所依赖的读取语义。
/// checkpoint 只作用于 active path；window 只在当前页；leaf 只在物理末尾有效。
/// </summary>
[Collection("FileNovelStore")]
public sealed class SessionA2CheckpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _sessionsDir;
    private FileSessionStore _store = null!;

    private const string NovelId = "a2novel";
    private const string SessionId = "a2session";

    public SessionA2CheckpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"a2-session-{Guid.NewGuid():N}");
        _sessionsDir = Path.Combine(_root, "sessions");
    }

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_sessionsDir);
        _store = new FileSessionStore(
            novelsRoot: Path.Combine(_root, "novels"),
            mythologyDir: _root
        )
        {
            SkipConflictCheck = true,
        };
        return default;
    }

    public ValueTask DisposeAsync()
    {
        AtomicFile.ForgetPath(_root);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { }
        return default;
    }

    private async Task<string> SaveAsync(
        string content,
        string? parent = null,
        int startOrder = 0
    ) =>
        (
            await _store.SaveSessionMessages(
                SessionId,
                NovelId,
                [new LlmChatMessage(ChatMessageRoles.User, content)],
                lastParentEntryId: parent,
                startOrder: startOrder
            )
        )!;

    private async Task<string?> GetEntryIdAsync(string content) =>
        (await _store.GetActiveMessages(SessionId))
            .FirstOrDefault(m => m.Content == content)
            ?.EntryId;

    [Fact]
    public async Task Window_AsOfBeforeWindow_DoesNotHideTargetPrefix()
    {
        var u1 = await SaveAsync("u1");
        var a1 = await SaveAsync("a1", u1, 1);
        var (windowId, windowEntryId, windowOrder) = await _store.StartNewContextWindowAsync(
            SessionId,
            NovelId,
            "model",
            order: 2
        );
        Assert.Equal("w1", windowId);
        await SaveAsync("u2", windowEntryId, windowOrder + 1);
        await SaveAsync("a2", await GetEntryIdAsync("u2"), windowOrder + 2);
        var current = await _store.GetContextMessages(SessionId);
        Assert.Contains(current, m => m.Content == "u2");
        Assert.DoesNotContain(current, m => m.Content == "u1");

        var asOf = await _store.GetContextMessages(SessionId, asOfEntryId: a1);
        Assert.Contains(asOf, m => m.Content == "u1");
        Assert.Contains(asOf, m => m.Content == "a1");
        Assert.DoesNotContain(asOf, m => m.Content == "u2");

        var currentBoundary = await _store.GetWindowBoundaryAsync(SessionId);
        Assert.Equal("w1", currentBoundary.WindowId);
        var asOfBoundary = await _store.GetWindowBoundaryAsync(SessionId, asOfEntryId: a1);
        Assert.Equal(ContextWindowState.First, asOfBoundary);
    }

    [Fact]
    public async Task Compact_AsOfBeforeFirstKept_DoesNotApplySummary()
    {
        var u1 = await SaveAsync("u1");
        var a1 = await SaveAsync("a1", u1, 1);
        var u2 = await SaveAsync("u2", a1, 2);
        var a2 = await SaveAsync("a2", u2, 3);
        await _store.CompactSession(
            SessionId,
            NovelId,
            summary: "old summary",
            firstKeptEntryId: u2,
            messagesBefore: 4,
            parentEntryId: a2,
            order: 4
        );

        var compacted = await _store.GetContextMessages(SessionId);
        Assert.Contains(compacted, m =>
            m.Content?.Contains("[上下文摘要", StringComparison.Ordinal) == true
        );
        Assert.DoesNotContain(compacted, m => m.Content == "u1");

        var asOf = await _store.GetContextMessages(SessionId, asOfEntryId: u1);
        Assert.Contains(asOf, m => m.Content == "u1");
        Assert.Contains(asOf, m => m.Content == "a1");
        Assert.DoesNotContain(asOf, m => m.EventType == SessionEventType.Compact);
    }

    [Fact]
    public async Task AsOf_AfterCompact_IncludesSummaryBeforeFirstKept_ButNotFuture()
    {
        var u1 = await SaveAsync("u1");
        var a1 = await SaveAsync("a1", u1, 1);
        var u2 = await SaveAsync("u2", a1, 2);
        var a2 = await SaveAsync("a2", u2, 3);
        var (compactId, _) = await _store.CompactSession(
            SessionId,
            NovelId,
            summary: "compact summary",
            firstKeptEntryId: u1,
            messagesBefore: 4,
            parentEntryId: a2,
            order: 4
        );
        // compact 之后的新分支：不能被 as-of 到 a2 的旧目标带进上下文。
        await SaveAsync("future", compactId, 5);

        var asOf = await _store.GetContextMessages(SessionId, asOfEntryId: a2);

        Assert.Contains(asOf, m =>
            m.Content?.Contains("[上下文摘要", StringComparison.Ordinal) == true
        );
        Assert.Contains(asOf, m => m.Content == "u1");
        Assert.Contains(asOf, m => m.Content == "a2");
        Assert.DoesNotContain(asOf, m => m.Content == "future");
        var summaryIndex = asOf.FindIndex(m =>
            m.Content?.Contains("[上下文摘要", StringComparison.Ordinal) == true
        );
        var keptIndex = asOf.FindIndex(m => m.Content == "u1");
        Assert.True(summaryIndex >= 0 && summaryIndex < keptIndex);
    }

    [Fact]
    public async Task Compact_NewSegmentContainsOnlyCompactEntry()
    {
        var u1 = await SaveAsync("u1");
        var a1 = await SaveAsync("a1", u1, 1);
        var u2 = await SaveAsync("u2", a1, 2);
        await SaveAsync("a2", u2, 3);
        var oldPath = Directory
            .GetFiles(_sessionsDir, "000000.jsonl", SearchOption.AllDirectories)
            .Single();
        var oldLines = await File.ReadAllLinesAsync(oldPath);

        await _store.CompactSession(
            SessionId,
            NovelId,
            summary: "compact summary",
            firstKeptEntryId: u2,
            messagesBefore: 4,
            order: 4
        );

        var newPath = Directory
            .GetFiles(_sessionsDir, "000001.jsonl", SearchOption.AllDirectories)
            .Single();
        var newLines = (await File.ReadAllLinesAsync(newPath))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var compactLine = Assert.Single(newLines);
        using (var doc = JsonDocument.Parse(compactLine))
        {
            Assert.Equal("compact", doc.RootElement.GetProperty("type").GetString());
        }

        // 旧分片 append-only：保留消息仍只在旧分片，不被复制进新分片。
        Assert.Equal(4, oldLines.Length);
        Assert.Equal(oldLines, await File.ReadAllLinesAsync(oldPath));

        var active = await _store.GetActiveMessages(SessionId);
        Assert.Contains(active, m => m.Content == "u2");
        Assert.Contains(active, m => m.Content == "a2");
        Assert.DoesNotContain(active, m => m.Content == "u1");
        Assert.Single(active, m => m.EventType == SessionEventType.Compact);

        var context = await _store.GetContextMessages(SessionId);
        var summaryIndex = context.FindIndex(m =>
            m.Content?.Contains("[上下文摘要", StringComparison.Ordinal) == true
        );
        var keptIndex = context.FindIndex(m => m.Content == "u2");
        Assert.True(summaryIndex >= 0 && summaryIndex < keptIndex);
    }

    [Fact]
    public async Task LegacyCompactSegmentWithRetainedCopies_RemainsReadable()
    {
        var u1 = await SaveAsync("u1");
        var a1 = await SaveAsync("a1", u1, 1);
        var u2 = await SaveAsync("u2", a1, 2);
        await SaveAsync("a2", u2, 3);
        await _store.CompactSession(
            SessionId,
            NovelId,
            summary: "legacy summary",
            firstKeptEntryId: u2,
            messagesBefore: 4,
            order: 4
        );

        // 模拟旧格式：新分片 = compact 行 + 保留消息副本（当前实现只写 compact 行）。
        var oldPath = Directory
            .GetFiles(_sessionsDir, "000000.jsonl", SearchOption.AllDirectories)
            .Single();
        var newPath = Directory
            .GetFiles(_sessionsDir, "000001.jsonl", SearchOption.AllDirectories)
            .Single();
        var compactLine = (await File.ReadAllLinesAsync(newPath)).Single(line =>
            !string.IsNullOrWhiteSpace(line)
        );
        var retained = (await File.ReadAllLinesAsync(oldPath))
            .SkipWhile(line => !line.Contains($"\"id\":\"{u2}\"", StringComparison.Ordinal))
            .ToArray();
        await File.WriteAllLinesAsync(newPath, [compactLine, .. retained]);

        // 新 store 实例绕过 parse cache，验证读取端仍兼容旧分片格式。
        var reloaded = new FileSessionStore(
            novelsRoot: Path.Combine(_root, "novels"),
            mythologyDir: _root
        )
        {
            SkipConflictCheck = true,
        };
        var active = await reloaded.GetActiveMessages(SessionId);
        Assert.Contains(active, m => m.Content == "u2");
        Assert.Contains(active, m => m.Content == "a2");
        Assert.DoesNotContain(active, m => m.Content == "u1");
        Assert.Single(active, m => m.EventType == SessionEventType.Compact);

        var context = await reloaded.GetContextMessages(SessionId);
        Assert.Contains(context, m => m.Content?.Contains("legacy summary") == true);
        Assert.Contains(context, m => m.Content == "u2");
    }

    [Fact]
    public async Task LeafFollowedByMessage_LastMessageWins()
    {
        var u1 = await SaveAsync("u1");
        await _store.SaveSessionLeaf(SessionId, NovelId, new SessionLeaf { TargetId = u1 });
        var (nextOrder, _) = await _store.GetSessionMetadataAsync(SessionId);
        await SaveAsync("u2", u1, nextOrder);

        var active = await _store.GetActiveMessages(SessionId);
        Assert.Contains(active, m => m.Content == "u2");
        Assert.Equal("u2", active[^1].Content);
    }

    [Fact]
    public async Task TornTail_NextAppendStillVisible()
    {
        var u1 = await SaveAsync("u1");
        var sessionPath = Directory
            .GetFiles(_sessionsDir, "*.jsonl", SearchOption.AllDirectories)
            .Single();
        await File.AppendAllTextAsync(sessionPath, "{\"type\":\"message\"");

        var (nextOrder, _) = await _store.GetSessionMetadataAsync(SessionId);
        await SaveAsync("u2", u1, nextOrder);

        var active = await _store.GetActiveMessages(SessionId);
        Assert.Contains(active, m => m.Content == "u2");
        Assert.Contains(active, m => m.Content == "u1");
    }

    [Fact]
    public async Task GarbageCollection_RemovesOnlyStaleTempFiles()
    {
        var oldTemp = Path.Combine(_sessionsDir, "old.tmp");
        var freshTemp = Path.Combine(_sessionsDir, "fresh.tmp");
        await File.WriteAllTextAsync(oldTemp, "old");
        await File.WriteAllTextAsync(freshTemp, "fresh");
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddHours(-2));

        var removed = await _store.CollectGarbageAsync();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(freshTemp));
    }
}
