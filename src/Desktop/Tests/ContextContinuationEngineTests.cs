using System.Reflection;
using System.Text.Json;
using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Desktop.Services;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// Desktop 侧上下文管理的两条路径（模式互斥）：
/// - 接续：换页落盘 window 条目 + 清空模型侧 + UI 历史保留 + 恢复会话后页状态一致；不落 compact 条目。
/// - compact：压缩落盘 compact 条目；不落 window 条目。
/// 另覆盖压缩的单点兜底（接续模式直接调用压缩内核必须被拒绝）。
/// </summary>
public sealed class ContextContinuationEngineTests : IDisposable
{
    private const string NovelId = "nv1";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _base = Path.Combine(
        Path.GetTempPath(),
        $"myth-desktop-ctx-{Guid.NewGuid():N}"
    );
    private readonly FileSessionStore _store;
    private readonly SessionEngine _engine;
    private readonly Agent _agent;

    public ContextContinuationEngineTests()
    {
        var root = Path.Combine(_base, "novels");
        var storeDir = Path.Combine(_base, "store");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(storeDir);

        _store = new FileSessionStore(storeDir, root);
        _agent = new Agent(new ToolRegistry(), null);
        _engine = new SessionEngine(
            new DesktopAgentBridge(_agent),
            _store,
            new FileNovelStore(root, storeDir),
            new TodoStore(),
            new ContextTokenCalibrator(),
            new ConfigStore(storeDir),
            NullLogger.Instance
        );
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    // ── 装配 ──

    private void EnableContinuation() =>
        _agent.ApplyContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 0, false)
        );

    /// <summary>预置会话分片并切回该会话（恢复模型侧上下文 + 元数据 + 页状态）。</summary>
    private async Task<string> SeedSessionAsync(int count = 6, int charsPerMessage = 12)
    {
        var sessionId = $"s{Guid.NewGuid():N}"[..12];
        _agent.CurrentNovelId = NovelId;
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
        await _store.SaveSessionMessages(sessionId, NovelId, messages);
        await _engine.SwitchToAsync(sessionId, NovelId);
        return sessionId;
    }

    private async Task<bool> RollAsync(string reason)
    {
        var method = typeof(SessionEngine).GetMethod("RollContextWindowAsync", Flags)!;
        var task = (Task<bool>)method.Invoke(_engine, [reason])!;
        return await task;
    }

    /// <summary>换页时把模型自写的交接说明写进页状态（新页片段据此知道做到哪）。</summary>
    [Fact]
    public async Task Roll_CarriesModelHandoffNote()
    {
        EnableContinuation();
        await SeedSessionAsync();
        _agent.RequestNewContextWindow("model", "三~五章已改完；下一步核对第六章");

        Assert.True(await RollAsync("model"));

        Assert.Equal("三~五章已改完；下一步核对第六章", _agent.ContextWindowState.HandoffNote);
        Assert.Null(_agent.PendingNewContextNote); // 已取走
    }

    private async Task<bool> RelieveOverflowAsync(int keepRecent)
    {
        var method = typeof(SessionEngine).GetMethod("RelieveOverflowAsync", Flags)!;
        var task = (Task<bool>)method.Invoke(_engine, [keepRecent])!;
        return await task;
    }

    /// <summary>压缩内核单点入口（所有触发源汇入处），仅等待完成；断言看落盘与上下文变化。</summary>
    private async Task InvokePrepareAndCompactAsync(int keepRecent)
    {
        var method = typeof(SessionEngine).GetMethod("PrepareAndCompactAsync", Flags)!;
        var task = (Task)
            method.Invoke(_engine, [_agent.State.Messages, keepRecent, "manual", false])!;
        await task;
    }

    /// <summary>压缩内核单点入口，返回拒绝/成功语义（<see cref="CompactResult"/>）。</summary>
    private async Task<CompactResult> InvokePrepareAndCompactCoreAsync(int keepRecent)
    {
        var method = typeof(SessionEngine).GetMethod("PrepareAndCompactAsync", Flags)!;
        var task =
            (Task<CompactResult>)
                method.Invoke(_engine, [_agent.State.Messages, keepRecent, "manual", false])!;
        return await task;
    }

    /// <summary>拒绝原因 → 用户文案（私有助手，反射调用以钉死两种语义不混词）。</summary>
    private static string InvokeRejectionMessage(CompactResult result)
    {
        var method = typeof(SessionEngine).GetMethod(
            "RejectionMessage",
            Flags | BindingFlags.Static
        )!;
        return (string)method.Invoke(null, [result])!;
    }

    /// <summary>扫描会话分片，返回指定类型的原始 JSON 条目。</summary>
    private List<JsonElement> ReadSessionEntries(params string[] types)
    {
        var wanted = types.ToHashSet(StringComparer.Ordinal);
        var result = new List<JsonElement>();
        foreach (var file in Directory.GetFiles(_base, "*.jsonl", SearchOption.AllDirectories))
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
    //  接续：换页落盘 + 模型侧清空 + UI 历史保留 + 恢复页状态
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Roll_WritesWindowEntry_ClearsModelList_AndSurvivesRestore()
    {
        EnableContinuation();
        var sessionId = await SeedSessionAsync();

        Assert.True(await RollAsync("model"));

        var window = Assert.Single(ReadSessionEntries("window"));
        Assert.Equal("w1", window.GetProperty("window-id").GetString());
        Assert.Equal("w0", window.GetProperty("prev-window-id").GetString());
        Assert.Equal("model", window.GetProperty("reason").GetString());

        // 模型侧为空；存储/UI 侧历史完整
        Assert.Empty(_agent.State.Messages);
        Assert.Empty(await _store.GetContextMessages(sessionId));
        Assert.Equal(6, (await _store.GetActiveMessages(sessionId)).Count);
        Assert.Equal("w1", _agent.ContextWindowState.WindowId);
        // 任务锚点 = 封页最后一条用户消息（新页据此知道在做什么，用户说「继续」也有落点）
        Assert.Equal("第 4 条：" + new string('述', 12), _agent.ContextWindowState.TaskAnchor);
        Assert.Equal("model", _agent.ContextWindowState.RollReason);

        // 恢复会话：页状态与页截断口径必须一致；恢复路径不带锚点（当前页消息本身就在上下文里）
        await _engine.SwitchToAsync(sessionId, NovelId);
        Assert.Equal("w1", _agent.ContextWindowState.WindowId);
        Assert.Equal("w0", _agent.ContextWindowState.PrevWindowId);
        Assert.Null(_agent.ContextWindowState.TaskAnchor);
        Assert.Empty(_agent.State.Messages);
    }

    // ═══════════════════════════════════════════════════════════
    //  模式互斥：compact 不换页、接续不压缩
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Roll_InCompactMode_IsRefused()
    {
        await SeedSessionAsync();

        Assert.False(await RollAsync("overflow"));

        Assert.Empty(ReadSessionEntries("window"));
        Assert.Equal(6, _agent.State.Messages.Count); // 模型侧未被清空
        Assert.Equal(0, _agent.ContextWindowState.StartOrder);
    }

    [Fact]
    public async Task CompactCommand_InContinuationMode_IsRejected()
    {
        EnableContinuation();
        await SeedSessionAsync(count: 10, charsPerMessage: 300);

        var outcome = await _engine.CompactAsync(smart: false, keepRecent: 2);

        Assert.Equal(CompactOutcomeKind.Rejected, outcome.Kind);
        Assert.Contains("接续模式", outcome.Error);
        Assert.Empty(ReadSessionEntries("compact"));
        Assert.Empty(ReadSessionEntries("window"));
    }

    [Fact]
    public async Task CompactionCore_InContinuationMode_IsRejected()
    {
        EnableContinuation();
        await SeedSessionAsync();
        var messagesBefore = _agent.State.Messages.Count;

        await InvokePrepareAndCompactAsync(keepRecent: 2);

        Assert.Empty(ReadSessionEntries("compact"));
        Assert.Equal(messagesBefore, _agent.State.Messages.Count);
    }

    /// <summary>
    /// 单点兜底必须返回 <see cref="CompactResult.RejectedWrongMode"/>（而不是防重入的那一档）：
    /// 两种拒绝语义不同，混成一档会让上层把「模式互斥」当成「稍后重试」。
    /// </summary>
    [Fact]
    public async Task CompactionCore_InContinuationMode_ReturnsRejectedWrongMode()
    {
        EnableContinuation();
        await SeedSessionAsync();
        var messagesBefore = _agent.State.Messages.Count;

        var result = await InvokePrepareAndCompactCoreAsync(keepRecent: 2);

        Assert.Equal(CompactResult.RejectedWrongMode, result);
        Assert.Empty(ReadSessionEntries("compact"));
        Assert.Equal(messagesBefore, _agent.State.Messages.Count); // 模型侧未被改动
    }

    /// <summary>compact 模式下同一入口必须正常放行——证明 WrongMode 不是被无条件返回的。</summary>
    [Fact]
    public async Task CompactionCore_InCompactMode_ReturnsSuccess()
    {
        await SeedSessionAsync(count: 10, charsPerMessage: 300);

        var result = await InvokePrepareAndCompactCoreAsync(keepRecent: 2);

        Assert.Equal(CompactResult.Success, result);
        var compact = Assert.Single(ReadSessionEntries("compact"));
        Assert.Equal("manual", compact.GetProperty("reason").GetString());
    }

    /// <summary>两种拒绝的用户文案必须可区分（拆分的最低限度保证）。</summary>
    [Fact]
    public void RejectionMessage_DistinguishesWrongModeFromInProgress()
    {
        var wrongMode = InvokeRejectionMessage(CompactResult.RejectedWrongMode);
        var inProgress = InvokeRejectionMessage(CompactResult.RejectedInProgress);

        Assert.Contains("接续模式", wrongMode);
        Assert.Contains("新页", wrongMode);
        Assert.DoesNotContain("压缩进行中", wrongMode);
        Assert.Contains("压缩进行中", inProgress);
        Assert.DoesNotContain("接续模式", inProgress);
        Assert.NotEqual(wrongMode, inProgress);
    }

    [Fact]
    public async Task RelieveOverflow_ContinuationMode_RollsWithoutCompacting()
    {
        EnableContinuation();
        await SeedSessionAsync();

        Assert.True(await RelieveOverflowAsync(keepRecent: 2));

        var window = Assert.Single(ReadSessionEntries("window"));
        Assert.Equal("overflow", window.GetProperty("reason").GetString());
        Assert.Empty(ReadSessionEntries("compact"));
    }

    [Fact]
    public async Task CompactCommand_InCompactMode_Compacts()
    {
        await SeedSessionAsync(count: 10, charsPerMessage: 300);

        var outcome = await _engine.CompactAsync(smart: false, keepRecent: 2);

        Assert.Equal(CompactOutcomeKind.Done, outcome.Kind);
        var compact = Assert.Single(ReadSessionEntries("compact"));
        Assert.Equal("manual", compact.GetProperty("reason").GetString());
        Assert.Empty(ReadSessionEntries("window"));
        // 压缩后模型侧只剩「摘要 + 保留后缀」
        Assert.True(_agent.State.Messages.Count < outcome.Before);
    }

    [Fact]
    public async Task RelieveOverflow_CompactMode_CompactsWithoutRolling()
    {
        // 长消息才产生「有效压缩」（短消息压完反而更长，会走无效压缩分支返回 false）
        await SeedSessionAsync(count: 10, charsPerMessage: 300);

        Assert.True(await RelieveOverflowAsync(keepRecent: 2));

        var compact = Assert.Single(ReadSessionEntries("compact"));
        Assert.Equal("overflow", compact.GetProperty("reason").GetString());
        Assert.Empty(ReadSessionEntries("window"));
        Assert.Equal(0, _agent.ContextWindowState.StartOrder); // 页状态未推进
    }
}
