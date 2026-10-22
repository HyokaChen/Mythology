using System.Text.Json;
using Core.Agent;
using Core.Data;
using Core.Shared;
using LlmTornado.Code;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>
/// 上下文页（接续换页）存储层测试：window 条目落盘与解析、模型口径页截断、
/// 页边界/页列表查询、MANIFEST 字段保留、与既有 compact 的组合语义。
/// </summary>
[Collection("FileNovelStore")]
public sealed class ContextWindowStoreTests : IAsyncLifetime
{
    private readonly string _tempRoot;
    private readonly string _tempSessionsDir;
    private FileSessionStore _store = null!;
    private const string TestNovelId = "testnovel";
    private const string TestSessionId = "winsess00001";

    public ContextWindowStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mythology-win-{Guid.NewGuid():N}");
        _tempSessionsDir = Path.Combine(_tempRoot, "sessions");
    }

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempSessionsDir);
        _store = CreateStore();
        return default;
    }

    public ValueTask DisposeAsync()
    {
        AtomicFile.ForgetPath(_tempRoot);
        if (Directory.Exists(_tempSessionsDir))
        {
            foreach (
                var file in Directory.GetFiles(_tempSessionsDir, "*", SearchOption.AllDirectories)
            )
                AtomicFile.ForgetPath(file);
        }

        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
        return default;
    }

    private FileSessionStore CreateStore() =>
        new(novelsRoot: Path.Combine(_tempRoot, "novels"), mythologyDir: _tempRoot)
        {
            SkipConflictCheck = true,
        };

    /// <summary>
    /// 追加一批消息。必须像 Host（TuiApp/SessionEngine）一样带上 lastParentEntryId 与真实 startOrder——
    /// 否则 parentId 链在批次间断开，活跃路径回溯会只看到最后一批消息。
    /// </summary>
    private async Task WriteMessagesAsync(params string[] texts)
    {
        var messages = texts
            .Select(
                (t, i) =>
                    new LlmChatMessage(
                        i % 2 == 0
                            ? LlmTornado.Code.ChatMessageRoles.User
                            : LlmTornado.Code.ChatMessageRoles.Assistant,
                        t
                    )
            )
            .ToList();
        var (nextOrder, lastEntryId) = await _store.GetSessionMetadataAsync(TestSessionId);
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            lastParentEntryId: lastEntryId,
            startOrder: nextOrder
        );
    }

    /// <summary>扫描全部分片，按 order 取 entryId。</summary>
    private Dictionary<int, string> EntryIdByOrder()
    {
        var map = new Dictionary<int, string>();
        foreach (
            var file in Directory.GetFiles(_tempSessionsDir, "*.jsonl", SearchOption.AllDirectories)
        )
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (
                    root.TryGetProperty("order", out var order)
                    && root.TryGetProperty("id", out var id)
                )
                    map[order.GetInt32()] = id.GetString() ?? "";
            }
        }
        return map;
    }

    private static List<string> ContextContents(IReadOnlyList<ChatMessage> messages) =>
        messages.Select(m => m.Content ?? "").ToList();

    // ═══════════════════════════════════════════════════════════
    //  1. 页截断：模型口径只留新页，UI 口径全量
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task StartNewContextWindow_TruncatesContextOnly()
    {
        await WriteMessagesAsync("m0", "m1", "m2");
        var (windowId, _, order) = await _store.StartNewContextWindowAsync(
            TestSessionId,
            TestNovelId,
            "model",
            order: 3
        );

        Assert.Equal("w1", windowId);
        Assert.Equal(3, order);
        Assert.Empty(await _store.GetContextMessages(TestSessionId));

        var active = await _store.GetActiveMessages(TestSessionId);
        Assert.Equal(3, ContextContents(active).Count(c => c.StartsWith('m')));

        await WriteMessagesAsync("m3", "m4");
        var context = ContextContents(await _store.GetContextMessages(TestSessionId));
        Assert.Equal(["m3", "m4"], context);
        Assert.Equal(5, ContextContents(await _store.GetActiveMessages(TestSessionId)).Count);
    }

    // ═══════════════════════════════════════════════════════════
    //  2. 页边界与页列表：跨实例（重开 store）稳定
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task WindowBoundaryAndList_SurviveStoreReopen()
    {
        await WriteMessagesAsync("m0", "m1", "m2");
        await _store.StartNewContextWindowAsync(TestSessionId, TestNovelId, "model", order: 3);
        await WriteMessagesAsync("m3", "m4");

        var reopened = CreateStore();

        var boundary = await reopened.GetWindowBoundaryAsync(TestSessionId);
        Assert.Equal("w1", boundary.WindowId);
        Assert.Equal("w0", boundary.PrevWindowId);
        Assert.Equal("w0", boundary.FirstWindowId);
        Assert.Equal(4, boundary.StartOrder); // window 行 order=3 → 本页首条消息 order=4

        var windows = await reopened.ListWindowsAsync(TestSessionId);
        Assert.Equal(2, windows.Count);
        Assert.Equal(("w0", 3), (windows[0].WindowId, windows[0].ItemCount));
        Assert.Equal(("w1", 2), (windows[1].WindowId, windows[1].ItemCount));
        Assert.Equal("w0", windows[1].PrevWindowId);
        Assert.Null(windows[0].EntryId); // 隐式首页无条目
        Assert.NotNull(windows[1].EntryId);
        Assert.Equal("model", windows[1].Reason);
    }

    // ═══════════════════════════════════════════════════════════
    //  3. 旧会话回归：无 window 条目 = 首页且行为不变
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task LegacySession_NoWindowEntries_UnchangedBehavior()
    {
        await WriteMessagesAsync("m0", "m1");

        var boundary = await _store.GetWindowBoundaryAsync(TestSessionId);
        Assert.Equal(ContextWindowState.First, boundary);

        var windows = await _store.ListWindowsAsync(TestSessionId);
        Assert.Single(windows);
        Assert.Equal(("w0", 2), (windows[0].WindowId, windows[0].ItemCount));

        Assert.Equal(["m0", "m1"], ContextContents(await _store.GetContextMessages(TestSessionId)));
    }

    // ═══════════════════════════════════════════════════════════
    //  4. MANIFEST 往返保留 window 字段
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 交接说明落盘在 window 行（JSONL 里可读），且恢复会话（GetWindowBoundaryAsync）时带回来 ——
    /// 否则重启/换会话后新页就丢掉了"做到哪/下一步"。
    /// </summary>
    [Fact]
    public async Task WindowNote_IsPersistedAndRestored()
    {
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            [new LlmChatMessage(ChatMessageRoles.User, "把第三到第五章改完")]
        );

        var (windowId, _, order) = await _store.StartNewContextWindowAsync(
            TestSessionId,
            TestNovelId,
            "model",
            order: 1,
            note: "三~五章已改完；下一步核对第六章时间线"
        );
        Assert.Equal("w1", windowId);

        // JSONL 里看得见（window 行带 note 字段）
        var raw = Directory
            .GetFiles(_tempRoot, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .FirstOrDefault(line => line.Contains("\"type\":\"window\""));
        Assert.NotNull(raw);
        Assert.Contains("三~五章已改完", raw);

        // 恢复：页边界带出交接说明；页列表也带
        var boundary = await _store.GetWindowBoundaryAsync(TestSessionId);
        Assert.Equal("w1", boundary.WindowId);
        Assert.Equal("三~五章已改完；下一步核对第六章时间线", boundary.HandoffNote);
        Assert.Equal("model", boundary.RollReason);

        var windows = await _store.ListWindowsAsync(TestSessionId);
        Assert.Equal("三~五章已改完；下一步核对第六章时间线", windows[^1].Note);
    }

    [Fact]
    public async Task Manifest_RoundTripsWindowFields()
    {
        await WriteMessagesAsync("m0");
        await _store.StartNewContextWindowAsync(TestSessionId, TestNovelId, "overflow", order: 1);
        await WriteMessagesAsync("m1"); // 触发 UpdateManifestSegment 重建

        var manifestPath = Path.Combine(GetSessionDir(), "MANIFEST.json");
        var manifest = JsonSerializer.Deserialize<SessionManifest>(
            await File.ReadAllTextAsync(manifestPath),
            JsonlSerializer.Options
        );

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.WindowCount);
        var windowSegment = Assert.Single(manifest.Segments, s => s.HasWindow);
        Assert.Equal("w1", windowSegment.WindowId);
        // 非 window 分片不得被误标
        Assert.All(
            manifest.Segments.Where(s => s.Seq != windowSegment.Seq),
            s => Assert.False(s.HasWindow)
        );
    }

    // ═══════════════════════════════════════════════════════════
    //  5. 解析入口：window 进入 ParsedEntry、不进树、占用 order
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task WindowEntry_IsParsed_ExcludedFromTree_AndConsumesOrder()
    {
        await WriteMessagesAsync("m0");
        await _store.StartNewContextWindowAsync(TestSessionId, TestNovelId, "model", order: 1);
        await WriteMessagesAsync("m1");

        // 解析入口（若被静默丢弃，页边界会恒为首页）
        var windows = await _store.ListWindowsAsync(TestSessionId);
        Assert.Equal(2, windows.Count);
        Assert.Equal("w1", windows[1].WindowId);

        // 树结构不含 window 行（只含消息节点）
        var tree = await _store.GetTreeStructure(TestSessionId);
        Assert.DoesNotContain(tree.Nodes, n => n.Type == JsonlEntryType.Window);

        // 元数据：window 行占用一个 order，nextOrder 必须跳过它（否则新消息会与页标记同号）
        var (nextOrder, lastEntryId) = await _store.GetSessionMetadataAsync(TestSessionId);
        Assert.Equal(3, nextOrder); // m0=0, window=1, m1=2 → next=3
        Assert.Equal(EntryIdByOrder()[2], lastEntryId);
    }

    // ═══════════════════════════════════════════════════════════
    //  6. 页 + compact 组合：模型口径 = 新页保留消息 + 最新摘要
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task WindowThenCompact_ContextKeepsNewPagePlusLatestSummary()
    {
        await WriteMessagesAsync("old0", "old1");
        await _store.StartNewContextWindowAsync(TestSessionId, TestNovelId, "model", order: 2);
        await WriteMessagesAsync("new0", "new1", "new2");

        var ids = EntryIdByOrder();
        var (_, blockId) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "新页前两句的摘要",
            firstKeptEntryId: ids[5], // new2
            messagesBefore: 3,
            order: 6,
            compressedMessageIds: [ids[3], ids[4]]
        );

        var context = ContextContents(await _store.GetContextMessages(TestSessionId));
        Assert.Contains(context, c => c.Contains("[上下文摘要", StringComparison.Ordinal));
        Assert.Contains("new2", context);
        Assert.DoesNotContain(context, c => c.Contains("new0", StringComparison.Ordinal));
        Assert.DoesNotContain(context, c => c.Contains("old0", StringComparison.Ordinal));
        Assert.DoesNotContain(context, c => c.Contains("old1", StringComparison.Ordinal));

        // Active 口径同样受 compact 截断（既有语义：恢复会话时 UI 只看 compact 之后的消息）
        var active = ContextContents(await _store.GetActiveMessages(TestSessionId));
        Assert.Contains("new2", active);
        Assert.DoesNotContain("old0", active);

        // append-only 不丢数据：被压缩的原文仍可按 blockId 恢复（换页前后的消息都在分片里）
        var recovered = await _store.ReadCompactedMessagesAsync(TestSessionId, blockId);
        Assert.NotNull(recovered);
        Assert.Equal(["new0", "new1"], ContextContents(recovered!));

        var blocks = await _store.ListCompactBlocksAsync(TestSessionId);
        Assert.Single(blocks);
        Assert.Equal(blockId, blocks[0].BlockId);
    }

    private string GetSessionDir()
    {
        var novelDir = Path.Combine(_tempSessionsDir, $"--novels-{TestNovelId}--");
        if (!Directory.Exists(novelDir))
            return Path.Combine(novelDir, TestSessionId);

        foreach (var subDir in Directory.GetDirectories(novelDir))
        {
            var manifestPath = Path.Combine(subDir, "MANIFEST.json");
            if (!File.Exists(manifestPath))
                continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<SessionManifest>(
                    File.ReadAllText(manifestPath),
                    JsonlSerializer.Options
                );
                if (manifest?.SessionId == TestSessionId)
                    return subDir;
            }
            catch
            {
                // 忽略不可解析的 MANIFEST
            }
        }
        return Path.Combine(novelDir, TestSessionId);
    }
}
