using System.Reflection;
using Core.Agent;
using Core.Tools;
using LlmTornado.Code;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI.Threading;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui.Tests;

/// <summary>
/// 用户侧隐藏（Codex context management 语义）：接续工具族注册后，
/// ① 真实工具调用卡不渲染；② /tools 与 /state 列表不含隐藏工具名；
/// ③ 会话面板的正常内容不受影响（只隐藏机制，不隐藏对话）。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class ContextContinuationHidingTests : TerminalUiFixture
{
    private TuiSlashTestHarness _harness = null!;

    protected override TerminalSize Size => new(140, 44);

    protected override Func<CancellationToken, Task> CreateAppRunner()
    {
        _harness = new TuiSlashTestHarness(
            novelId: TuiSlashTestHarness.NovelId,
            sessionId: TuiSlashTestHarness.SessionId
        );
        return _harness.App.RunAsync;
    }

    protected override async ValueTask OnReadyToRunAsync()
    {
        // 接续工具族（真实装配在 context-continuation 插件里）在注册表中必须存在，
        // 但不得对用户可见。此处用同名假工具占位——渲染过滤只依赖工具名（HiddenTools）。
        foreach (var name in HiddenTools.Names)
            _harness.Registry.Register(new FakeAgentTool(name, "私有工具"), "Core");
        await _harness.SeedSessionStoreAsync();
        await AppendHiddenToolCallAsync();
    }

    protected override async ValueTask OnStartedAsync()
    {
        await WaitUntilAsync(
            () => DispatcherAttached() && _harness.CurrentSessionId is not null,
            "TerminalApp 启动并完成首帧（Dispatcher 绑定 + 会话恢复）",
            20000
        );
    }

    protected override async ValueTask OnStoppingAsync() => await _harness.DisposeAsync();

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>追加一组「隐藏工具调用 + 结果 + 正常回复」，模拟会话中真实发生过的换页/笔记调用。</summary>
    private async Task AppendHiddenToolCallAsync()
    {
        var novelId = TuiSlashTestHarness.NovelId;
        var sessionId = TuiSlashTestHarness.SessionId;
        var (nextOrder, lastEntryId) = await _harness.SessionStore.GetSessionMetadataAsync(
            sessionId
        );

        var callId = "call_hidden_1";
        var assistant = new LlmChatMessage(ChatMessageRoles.Assistant, "")
        {
            ToolCalls =
            [
                new LlmTornado.ChatFunctions.ToolCall
                {
                    Id = callId,
                    Type = "function",
                    FunctionCall = new LlmTornado.ChatFunctions.FunctionCall
                    {
                        Name = "history_read_item",
                        Arguments = """{"path":"plan.md","text":"私有笔记内容"}""",
                    },
                },
            ],
        };
        var toolResult = new LlmChatMessage(ChatMessageRoles.Tool, "已写入笔记 plan.md。")
        {
            ToolCallId = callId,
            Name = "history_read_item",
        };
        var normal = new LlmChatMessage(ChatMessageRoles.Assistant, "正常回复可见");

        var lastId = await _harness.SessionStore.SaveSessionMessages(
            sessionId,
            novelId,
            [assistant, toolResult, normal],
            lastParentEntryId: lastEntryId,
            startOrder: nextOrder
        );

        // 与 Harness 的种子保持一致：Agent 内存只有 6 条（新 3 条只在存储里，用于验证恢复渲染）
        typeof(TuiApp).GetField("_nextOrder", Flags)!.SetValue(_harness.App, nextOrder + 3);
        typeof(TuiApp).GetField("_persistedMessageCount", Flags)!.SetValue(_harness.App, 6);
        typeof(TuiApp).GetField("_lastParentEntryId", Flags)!.SetValue(_harness.App, lastId);
    }

    private Task DispatchAsync(string input) =>
        Dispatcher.Current.InvokeAsync(() => _harness.SubmitInput(input));

    private async Task WaitForOutputAsync(params string[] substrings)
    {
        await WaitUntilAsync(
            () => substrings.All(s => Backend.GetOutText().Contains(s, StringComparison.Ordinal)),
            $"终端渲染输出未出现: {string.Join(" | ", substrings)}",
            15000
        );
    }

    [Fact]
    public async Task HiddenToolCall_NotRendered_WhileNormalConversationIs()
    {
        // 正常内容必须渲染（隐藏机制不能顺带吞掉普通对话）
        await WaitForOutputAsync("正常回复可见");

        var screen = Backend.GetOutText();
        Assert.DoesNotContain("history_read_item", screen);
        Assert.DoesNotContain("私有笔记内容", screen);
        Assert.DoesNotContain("已写入笔记", screen);
    }

    [Fact]
    public async Task ToolsDialog_DoesNotListContinuationTools()
    {
        await DispatchAsync("/tools");
        await WaitForOutputAsync("工具列表");

        var screen = Backend.GetOutText();
        foreach (var name in HiddenTools.Names)
            Assert.DoesNotContain(name, screen);
    }

    [Fact]
    public async Task StateDialog_DoesNotListContinuationTools()
    {
        await DispatchAsync("/state");
        await WaitForOutputAsync("Agent 状态");

        var screen = Backend.GetOutText();
        foreach (var name in HiddenTools.Names)
            Assert.DoesNotContain(name, screen);
    }

    [Fact]
    public async Task HistoryDialog_SkipsHiddenToolMessages()
    {
        await DispatchAsync("/history");
        await WaitForOutputAsync("对话历史");

        var screen = Backend.GetOutText();
        Assert.DoesNotContain("history_read_item", screen);
    }
}
