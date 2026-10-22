using System.Reflection;
using System.Text.Json;
using Core.Agent;
using Core.Data;
using LlmTornado.Code;
using Xunit;
using AgentType = Core.Agent.Agent;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui.Tests;

/// <summary>
/// 上下文接续（换页）在 TUI 侧的端到端行为：
/// 换页落盘 window 条目 + 清空模型侧消息（UI 历史保留）+ 游标/页状态正确，
/// 以及换页后的 compact 必须按页取 id（否则重载会话时压缩被回滚）。
/// </summary>
[Collection("TuiSlashCommand")]
public sealed class ContextContinuationTests : IAsyncDisposable
{
    private readonly TuiSlashTestHarness _harness;

    public ContextContinuationTests()
    {
        _harness = new TuiSlashTestHarness(
            novelId: TuiSlashTestHarness.NovelId,
            sessionId: TuiSlashTestHarness.SessionId
        );
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    // 模式判据只有一个来源：Agent.ContextManagement（Host 侧不再缓存副本——
    // 设置页/斜杠命令可在运行期热切换，缓存副本会立刻过期）
    private void EnableContinuation() =>
        _harness.Agent.ApplyContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 0, false) // 0 = 按模型窗口推导
        );

    /// <summary>切回 compact 模式（模拟用户改配置后重启 / 跨模式会话）。</summary>
    private void DisableContinuation() => _harness.Agent.ApplyContextManagement(null);

    private async Task<bool> RollAsync(string reason)
    {
        var method = typeof(TuiApp).GetMethod("RollContextWindowAsync", Flags)!;
        var task = (Task<bool>)method.Invoke(_harness.App, [reason])!;
        return await task;
    }

    /// <summary>溢出恢复分支（模式互斥的唯一入口）。</summary>
    private async Task<bool> RelieveOverflowAsync(int keepRecent)
    {
        var method = typeof(TuiApp).GetMethod("RelieveOverflowAsync", Flags)!;
        var task = (Task<bool>)method.Invoke(_harness.App, [keepRecent])!;
        return await task;
    }

    /// <summary>压缩内核单点入口（所有触发源汇入处），仅等待完成；断言看落盘与上下文变化。</summary>
    private async Task InvokePrepareAndCompactAsync(int keepRecent)
    {
        var method = typeof(TuiApp).GetMethod("PrepareAndCompactAsync", Flags)!;
        var task = (Task)
            method.Invoke(
                _harness.App,
                [_harness.Agent.State.Messages, keepRecent, "manual", false]
            )!;
        await task;
    }

    /// <summary>
    /// 追加一批长消息到内存 + 分片（镜像每轮落盘后的状态），供「压缩必须真正降低 token」的用例使用。
    /// </summary>
    private async Task SeedLongPageMessagesAsync(int count, int charsPerMessage)
    {
        var novelId = TuiSlashTestHarness.NovelId;
        var sessionId = TuiSlashTestHarness.SessionId;
        var body = new string('述', charsPerMessage);
        var messages = new List<LlmChatMessage>(count);
        for (var i = 0; i < count; i++)
        {
            messages.Add(
                new LlmChatMessage(
                    i % 2 == 0 ? ChatMessageRoles.User : ChatMessageRoles.Assistant,
                    $"第 {i} 条：{body}"
                )
            );
        }

        foreach (var m in messages)
            _harness.Agent.AppendMessage(m);

        var (nextOrder, lastEntryId) = await _harness.SessionStore.GetSessionMetadataAsync(
            sessionId
        );
        var lastId = await _harness.SessionStore.SaveSessionMessages(
            sessionId,
            novelId,
            messages,
            lastParentEntryId: lastEntryId,
            startOrder: nextOrder
        );
        typeof(TuiApp).GetField("_nextOrder", Flags)!.SetValue(_harness.App, nextOrder + count);
        typeof(TuiApp)
            .GetField("_persistedMessageCount", Flags)!
            .SetValue(_harness.App, _harness.Agent.State.Messages.Count);
        typeof(TuiApp).GetField("_lastParentEntryId", Flags)!.SetValue(_harness.App, lastId);
    }

    private string? GetField(string name) =>
        typeof(TuiApp).GetField(name, Flags)!.GetValue(_harness.App) as string;

    private int GetIntField(string name) =>
        (int)typeof(TuiApp).GetField(name, Flags)!.GetValue(_harness.App)!;

    /// <summary>扫描会话分片，返回 window / compact 条目的原始 JSON 行。</summary>
    private List<JsonElement> ReadSessionEntries(params string[] types)
    {
        var wanted = types.ToHashSet(StringComparer.Ordinal);
        var result = new List<JsonElement>();
        foreach (
            var file in Directory.GetFiles(_harness.TempDir, "*.jsonl", SearchOption.AllDirectories)
        )
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is not null && wanted.Contains(type))
                    result.Add(doc.RootElement.Clone());
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    //  换页：落盘 + 清空模型侧 + 保留 UI 历史 + 游标正确
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Roll_WritesWindowEntry_ClearsModelList_KeepsHistory()
    {
        await _harness.SeedSessionStoreAsync();
        EnableContinuation();

        var rolled = await RollAsync("model");
        Assert.True(rolled);

        // 分片里出现 window 条目（w1，父链指向换页前最后一条消息）
        var windows = ReadSessionEntries("window");
        var window = Assert.Single(windows);
        Assert.Equal("w1", window.GetProperty("window-id").GetString());
        Assert.Equal("w0", window.GetProperty("prev-window-id").GetString());
        Assert.Equal("model", window.GetProperty("reason").GetString());
        Assert.Equal(6, window.GetProperty("order").GetInt32());

        // 模型侧清空；UI/存储侧历史完整；页状态与游标推进
        Assert.Empty(_harness.Agent.State.Messages);
        Assert.Empty(await _harness.SessionStore.GetContextMessages(TuiSlashTestHarness.SessionId));
        Assert.Equal(
            6,
            (await _harness.SessionStore.GetActiveMessages(TuiSlashTestHarness.SessionId)).Count
        );
        Assert.Equal("w1", _harness.Agent.ContextWindowState.WindowId);
        Assert.Equal("w0", _harness.Agent.ContextWindowState.PrevWindowId);
        Assert.Equal(7, _harness.Agent.ContextWindowState.StartOrder);
        // 任务锚点 = 封页最后一条用户消息（换页后模型据此知道在做什么）
        Assert.Equal("接下来写第二章，场景是雨夜。", _harness.Agent.ContextWindowState.TaskAnchor);
        // 换页原因随页状态下发（片段据此说明「被迫切断 / 计划内切换」）
        Assert.Equal("model", _harness.Agent.ContextWindowState.RollReason);
        Assert.Equal(7, GetIntField("_nextOrder"));
        Assert.Equal(0, GetIntField("_persistedMessageCount"));
        Assert.Equal(window.GetProperty("id").GetString(), GetField("_lastParentEntryId"));
    }

    /// <summary>
    /// 模型自主换页的完整链路：new_context 置位 → 循环迭代顶部消费 → Host 执行换页。
    /// （无需真实 LLM：直接调用循环的消费入口，覆盖 Agent ↔ Loop ↔ Host 三者的接线。）
    /// </summary>
    [Fact]
    public async Task ModelRequestedRoll_GoesThroughLoopToHost()
    {
        await _harness.SeedSessionStoreAsync();
        EnableContinuation();

        _harness.Agent.RequestNewContextWindow("model", "三~五章已改完；下一步核对第六章");
        Assert.Equal("model", _harness.Agent.PendingNewContextReason);
        var before = _harness.Agent.State.Messages.Count;

        // 循环是惰性创建的，且需要 TornadoApi（不在线调用，仅用于构造）
        _harness.Agent.TornadoApi = new LlmTornado.TornadoApi([
            new LlmTornado.Code.ProviderAuthentication(LLmProviders.OpenAi, "test-key"),
        ]);
        _harness.Agent.SetModelName("gpt-test");
        var loop = typeof(AgentType)
            .GetMethod("GetOrCreateConversationLoop", Flags)!
            .Invoke(_harness.Agent, null);
        Assert.NotNull(loop);
        // 迭代顶部只登记，run 边界才真正换页（否则会清掉本轮用户消息）
        loop!.GetType().GetMethod("ScheduleContextRollIfNeeded", Flags)!.Invoke(loop, null);
        Assert.Null(_harness.Agent.PendingNewContextReason);
        Assert.Equal("w0", _harness.Agent.ContextWindowState.WindowId); // 尚未换页
        Assert.Equal(before, _harness.Agent.State.Messages.Count); // 本轮上下文完好

        await (Task)
            loop.GetType()
                .GetMethod("FlushScheduledContextRollAsync", Flags)!
                .Invoke(loop, [CancellationToken.None])!;

        Assert.Equal("w1", _harness.Agent.ContextWindowState.WindowId);
        Assert.Equal(
            "三~五章已改完；下一步核对第六章",
            _harness.Agent.ContextWindowState.HandoffNote
        );
        Assert.Empty(_harness.Agent.State.Messages);
        Assert.Single(ReadSessionEntries("window"));
    }

    [Fact]
    public async Task Roll_WithoutSession_ReturnsFalse()
    {
        EnableContinuation();
        Assert.False(await RollAsync("model")); // 未绑定会话
    }

    // ═══════════════════════════════════════════════════════════
    //  模式互斥：compact 不换页、接续不压缩（两个方向都被硬拦）
    // ═══════════════════════════════════════════════════════════

    /// <summary>compact 模式下即使被直接调用，换页也必须空操作——绝不写出 window 条目、绝不清空模型侧。</summary>
    [Fact]
    public async Task Roll_InCompactMode_IsRefused()
    {
        await _harness.SeedSessionStoreAsync();

        Assert.False(await RollAsync("overflow"));

        Assert.Empty(ReadSessionEntries("window"));
        Assert.NotEmpty(_harness.Agent.State.Messages); // 模型侧未被清空
        Assert.Equal(0, _harness.Agent.ContextWindowState.StartOrder); // 页状态未推进
    }

    /// <summary>接续模式下 /compact 被拒绝：不产生 compact 条目，也不产生 window 条目。</summary>
    [Fact]
    public async Task CompactCommand_InContinuationMode_IsRefused()
    {
        await _harness.SeedSessionStoreAsync();
        EnableContinuation();

        _harness.InvokeSlashCommand("/compact 2"); // 消息数 6 > 2：若未被拦截就会真的压缩
        await Task.Delay(500); // 命令是 fire-and-forget；给未被拦截的路径留出落盘时间

        Assert.Empty(ReadSessionEntries("compact"));
        Assert.Empty(ReadSessionEntries("window"));
    }

    /// <summary>接续模式下压缩内核单点兜底：直接调用也不落任何条目、不动上下文（防新增触发源漏判模式）。</summary>
    [Fact]
    public async Task CompactionCore_InContinuationMode_IsRejected()
    {
        await _harness.SeedSessionStoreAsync();
        EnableContinuation();
        var messagesBefore = _harness.Agent.State.Messages.Count;

        await InvokePrepareAndCompactAsync(keepRecent: 2);

        Assert.Empty(ReadSessionEntries("compact"));
        Assert.Equal(messagesBefore, _harness.Agent.State.Messages.Count);
    }

    /// <summary>溢出恢复分支（模式互斥）：接续模式只换页，绝不落 compact 条目。</summary>
    [Fact]
    public async Task RelieveOverflow_ContinuationMode_RollsWithoutCompacting()
    {
        await _harness.SeedSessionStoreAsync();
        EnableContinuation();

        Assert.True(await RelieveOverflowAsync(keepRecent: 2));

        var window = Assert.Single(ReadSessionEntries("window"));
        Assert.Equal("overflow", window.GetProperty("reason").GetString());
        Assert.Empty(ReadSessionEntries("compact"));
    }

    /// <summary>溢出恢复分支（模式互斥）：compact 模式只压缩，绝不落 window 条目。</summary>
    [Fact]
    public async Task RelieveOverflow_CompactMode_CompactsWithoutRolling()
    {
        await _harness.SeedSessionStoreAsync();
        // 种入足以产生「有效压缩」的长消息（短消息压完反而更长，会走无效压缩分支）
        await SeedLongPageMessagesAsync(count: 10, charsPerMessage: 300);

        Assert.True(await RelieveOverflowAsync(keepRecent: 2));

        var compact = Assert.Single(ReadSessionEntries("compact"));
        Assert.Equal("overflow", compact.GetProperty("reason").GetString());
        Assert.Empty(ReadSessionEntries("window"));
        Assert.Equal(0, _harness.Agent.ContextWindowState.StartOrder); // 页状态未推进
    }

    // ═══════════════════════════════════════════════════════════
    //  换页后的 compact：id 必须取自当前页
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task CompactAfterRoll_TakesIdsFromCurrentPage_AndSurvivesReload()
    {
        await _harness.SeedSessionStoreAsync();
        EnableContinuation();
        Assert.True(await RollAsync("model"));

        // 模拟换页后的新一页消息（Host 落盘路径）
        var novelId = TuiSlashTestHarness.NovelId;
        var sessionId = TuiSlashTestHarness.SessionId;
        // 页内 8 条：cutIndex 要落在 ≥3 的位置才不会被 FindCutPoint 的防御分支退化为 0
        var pageMessages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "换页后第一条"),
            new(ChatMessageRoles.Assistant, "换页后第二条"),
            new(ChatMessageRoles.User, "换页后第三条"),
            new(ChatMessageRoles.Assistant, "换页后第四条"),
            new(ChatMessageRoles.User, "换页后第五条"),
            new(ChatMessageRoles.Assistant, "换页后第六条"),
            new(ChatMessageRoles.User, "换页后第七条"),
            new(ChatMessageRoles.Assistant, "换页后第八条"),
        };
        foreach (var m in pageMessages)
            _harness.Agent.AppendMessage(m);

        var (nextOrder, lastEntryId) = await _harness.SessionStore.GetSessionMetadataAsync(
            sessionId
        );
        var lastId = await _harness.SessionStore.SaveSessionMessages(
            sessionId,
            novelId,
            pageMessages,
            lastParentEntryId: lastEntryId,
            startOrder: nextOrder
        );
        typeof(TuiApp)
            .GetField("_nextOrder", Flags)!
            .SetValue(_harness.App, nextOrder + pageMessages.Count);
        typeof(TuiApp)
            .GetField("_persistedMessageCount", Flags)!
            .SetValue(_harness.App, _harness.Agent.State.Messages.Count);
        typeof(TuiApp).GetField("_lastParentEntryId", Flags)!.SetValue(_harness.App, lastId);

        // 模式互斥：接续模式不再压缩 —— 本用例保护的是「换过页的会话切回 compact 后，
        // 压缩取 id 必须落在当前页」这条跨模式数据语义（否则重载会话时页内消息会被复原）。
        DisableContinuation();

        // 页内 compact（保留最近 2 条）
        _harness.InvokeSlashCommand("/compact 2");
        await TuiSlashTestHarness.WaitUntilAsync(
            () =>
                _harness
                    .SessionStore.ListCompactBlocksAsync(sessionId)
                    .GetAwaiter()
                    .GetResult()
                    .Count > 0,
            "compact 未落盘"
        );

        var compact = Assert.Single(ReadSessionEntries("compact"));
        var firstKept = compact.GetProperty("first-kept-entry-id").GetString()!;
        var compressed = compact
            .GetProperty("compressed-message-ids")
            .EnumerateArray()
            .Select(x => x.GetString()!)
            .ToList();

        // 换页前 6 条消息的 order 为 0..5，window 行 order=6 → 页内消息 order ≥ 7
        var pageOrderFloor = 7;
        // compact 会把保留行复制进新分片 → 同一 entryId 可能出现两次（order 相同），按首个取值
        var allIds = ReadSessionEntries("message")
            .GroupBy(e => e.GetProperty("id").GetString()!)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("order").GetInt32());

        Assert.True(
            allIds[firstKept] >= pageOrderFloor,
            $"first-kept-entry-id 落在旧页: order={allIds[firstKept]}"
        );
        Assert.NotEmpty(compressed);
        Assert.All(
            compressed,
            id =>
                Assert.True(
                    allIds[id] >= pageOrderFloor,
                    $"compressedId 落在旧页: order={allIds[id]}"
                )
        );

        // 重载会话：模型上下文 = 摘要 + 页内保留消息，且不含任何换页前消息
        var reloaded = await _harness.SessionStore.GetContextMessages(sessionId);
        Assert.Contains(
            reloaded,
            m => m.Content!.Contains("[上下文摘要", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            reloaded,
            m => m.Content!.Contains("第一章开始", StringComparison.Ordinal)
        );
        Assert.Contains(reloaded, m => m.Content == "换页后第八条");
        Assert.Contains(reloaded, m => m.Content == "换页后第七条");
    }
}
