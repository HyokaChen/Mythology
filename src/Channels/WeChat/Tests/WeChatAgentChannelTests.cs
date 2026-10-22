// 功能：微信 ↔ Agent 桥接的端到端测试（桩掉 HTTP，其余全真实）
//
// 断的是完整链路：入站 JSON → 长轮询 → runTurn → Agent 事件 → settle 聚合 → markdown 降级
// → sendmessage 的**请求体**。不 mock 通道服务，因为它就是被测对象的一部分。

using Core.Agent;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Http;
using Xunit;

namespace Channels.WeChat.Tests;

public class WeChatAgentChannelTests
{
    private const string InboundText = """
        {"ret":0,"get_updates_buf":"buf-2","msgs":[{"seq":1,"message_id":111,"message_type":1,
         "from_user_id":"wxid_me","context_token":"ctx-1",
         "item_list":[{"type":1,"text_item":{"text":"续写第三章"}}]}]}
        """;

    private const string InboundVoice = """
        {"ret":0,"get_updates_buf":"buf-3","msgs":[{"seq":2,"message_id":222,"message_type":1,
         "from_user_id":"wxid_me","context_token":"ctx-2",
         "item_list":[{"type":3,"playtime":4,"voice_item":{"text":"语音转写：把主角写死"}}]}]}
        """;

    private const string InboundImage = """
        {"ret":0,"get_updates_buf":"buf-4","msgs":[{"seq":3,"message_id":333,"message_type":1,
         "from_user_id":"wxid_me","item_list":[{"type":2,"msg_id":444}]}]}
        """;

    /// <summary>自己的回声（message_type=2）必须被过滤，否则会自问自答。</summary>
    private const string InboundEcho = """
        {"ret":0,"get_updates_buf":"buf-5","msgs":[{"seq":4,"message_id":555,"message_type":2,
         "from_user_id":"bot@im.bot","item_list":[{"type":1,"text_item":{"text":"我自己的回复"}}]}]}
        """;

    private const string InboundSecond = """
        {"ret":0,"get_updates_buf":"buf-6","msgs":[{"seq":5,"message_id":666,"message_type":1,
         "from_user_id":"wxid_me","context_token":"ctx-3",
         "item_list":[{"type":1,"text_item":{"text":"再改一遍"}}]}]}
        """;

    private sealed record Fixture(
        WeChatChannelService Channel,
        WeChatAgentChannel Bridge,
        PathHandler Handler,
        TempDir Dir
    );

    /// <summary>
    /// 建夹具。<paramref name="runTurn"/> 收 bridge 作为第一参，避免用例里写"先声明后赋值"的捕获。
    /// </summary>
    private static Fixture Build(
        Func<WeChatAgentChannel, string, CancellationToken, Task> runTurn,
        Func<bool>? isAgentBusy = null
    )
    {
        var handler = new PathHandler();
        var dir = new TempDir();
        var channel = new WeChatChannelService(
            dir.Path,
            "test",
            new StubHttpClientFactory(handler)
        );

        // 预置绑定凭证（等价于已扫码完成）
        new WeChatBindingStore(dir.Path).Save(
            new WeChatBindingState
            {
                Credential = new WeChatCredential
                {
                    BaseUrl = "https://ilinkai.weixin.qq.com",
                    BotToken = "tk-1",
                    AccountId = "bot@im.bot",
                    PeerUserId = "wxid_me",
                },
            }
        );
        channel.TryRestore();

        WeChatAgentChannel bridge = null!;
        bridge = new WeChatAgentChannel(
            channel,
            (text, ct) => runTurn(bridge, text, ct),
            isAgentBusy
        );
        return new Fixture(channel, bridge, handler, dir);
    }

    private static void RouteCommon(PathHandler handler)
    {
        handler.RouteTo("getconfig", """{"ret":0,"typing_ticket":"ticket-1"}""", times: 10);
        handler.RouteTo("sendtyping", """{"ret":0}""", times: 20);
        handler.RouteTo("sendmessage", """{"ret":0,"message_id":9001}""", times: 20);
    }

    /// <summary>跑长轮询直到条件满足，随后取消并收尾。</summary>
    private static async Task RunUntilAsync(Fixture f, Func<bool> condition)
    {
        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && !condition())
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();
    }

    /// <summary>文本事件的便捷构造（AccumulatedText 对降级无影响，测试里取同值）。</summary>
    private static StreamChunk Text(string delta) => new(delta, delta, StreamContentType.Text);

    private static MessageEnd EndOf(string text) =>
        new(new ChatMessage(ChatMessageRoles.Assistant, text));

    /// <summary>请求体是 JSON，中文被默认编码器转义成 \uXXXX；断言前先解回原文。</summary>
    private static string Decoded(string jsonBody) =>
        System.Text.RegularExpressions.Regex.Replace(
            jsonBody,
            @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
        );

    [Fact]
    public async Task InboundTextDrivesTurnAndReplyIsSentBack()
    {
        var turnTexts = new List<string>();
        var f = Build(
            (bridge, text, _) =>
            {
                turnTexts.Add(text);
                bridge.OnAgentEvent(Text("第三章"));
                bridge.OnAgentEvent(Text("已写好"));
                bridge.OnAgentEvent(EndOf("第三章已写好"));
                return Task.CompletedTask;
            }
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundText, times: 1);

        await RunUntilAsync(f, () => f.Handler.RequestsTo("sendmessage").Count > 0);

        Assert.Equal(["续写第三章"], turnTexts);

        var body = Assert.Single(f.Handler.BodiesTo("sendmessage"));
        Assert.Contains("\"to_user_id\":\"wxid_me\"", body, StringComparison.Ordinal);
        Assert.Contains("\"message_type\":2", body, StringComparison.Ordinal);
        // 上下文令牌必须逐字带回，否则服务端拒绝
        Assert.Contains("\"context_token\":\"ctx-1\"", body, StringComparison.Ordinal);
        Assert.Contains("第三章已写好", Decoded(body), StringComparison.Ordinal);
        // 游标落盘：丢了会重放消息
        Assert.Equal("buf-2", f.Channel.State.GetUpdatesBuf);
    }

    [Fact]
    public async Task ChunksAreMergedIntoSingleMessageBeforeSending()
    {
        // settle 窗的意义：碎 chunk 逐条发会刷屏 + 撞风控
        var f = Build(
            (bridge, _, _) =>
            {
                foreach (var piece in new[] { "一", "二", "三", "四" })
                    bridge.OnAgentEvent(Text(piece));
                bridge.OnAgentEvent(EndOf("一二三四"));
                return Task.CompletedTask;
            }
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundText, times: 1);

        await RunUntilAsync(f, () => f.Handler.RequestsTo("sendmessage").Count > 0);

        var body = Assert.Single(f.Handler.BodiesTo("sendmessage"));
        Assert.Contains("一二三四", Decoded(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThinkingChunksAreNotForwarded()
    {
        var f = Build(
            (bridge, _, _) =>
            {
                bridge.OnAgentEvent(
                    new StreamChunk("内部推理…", "内部推理…", StreamContentType.Thinking)
                );
                bridge.OnAgentEvent(Text("正文"));
                bridge.OnAgentEvent(EndOf("正文"));
                return Task.CompletedTask;
            }
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundText, times: 1);

        await RunUntilAsync(f, () => f.Handler.RequestsTo("sendmessage").Count > 0);

        var body = Assert.Single(f.Handler.BodiesTo("sendmessage"));
        Assert.DoesNotContain("内部推理", Decoded(body), StringComparison.Ordinal);
        Assert.Contains("正文", Decoded(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdownIsDowngradedBeforeSending()
    {
        // 微信不渲染 Markdown：** 和 # 会原样显示
        var f = Build(
            (bridge, _, _) =>
            {
                bridge.OnAgentEvent(Text("# 第三章\n\n这是**重点**。"));
                bridge.OnAgentEvent(EndOf("# 第三章\n\n这是**重点**。"));
                return Task.CompletedTask;
            }
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundText, times: 1);

        await RunUntilAsync(f, () => f.Handler.RequestsTo("sendmessage").Count > 0);

        var text = Decoded(Assert.Single(f.Handler.BodiesTo("sendmessage")));
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# ", text, StringComparison.Ordinal);
        Assert.Contains("重点", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsOutsideOurTurnAreNotSent()
    {
        // turn 门闩：Agent 同时在服务终端里的对话，那些事件不能发到手机上
        var turnDone = new TaskCompletionSource();
        var f = Build(
            (_, _, _) =>
            {
                turnDone.TrySetResult();
                return Task.CompletedTask;
            }
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundText, times: 1);

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);
        await turnDone.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        // 回合的 finally 跑在独立任务上：等它把 _turnActive 落下再灌事件，
        // 否则测到的是"回合尚未收尾"而不是"门闩生效"。
        var gateClosed = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < gateClosed && f.Bridge.IsTurnActive)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        // 回合已结束：此时进来的事件属于终端里的对话
        f.Bridge.OnAgentEvent(Text("终端里的内容"));
        f.Bridge.OnAgentEvent(EndOf("终端里的内容"));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.Empty(f.Handler.RequestsTo("sendmessage"));
    }

    [Fact]
    public async Task BusyAgentGetsPoliteRefusalInsteadOfCollision()
    {
        var turnCalled = false;
        var f = Build(
            (_, _, _) =>
            {
                turnCalled = true;
                return Task.CompletedTask;
            },
            isAgentBusy: () => true
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundText, times: 1);

        await RunUntilAsync(f, () => f.Handler.RequestsTo("sendmessage").Count > 0);

        Assert.False(turnCalled); // 忙时不进 Agent（避免撞异常）
        var body = Assert.Single(f.Handler.BodiesTo("sendmessage"));
        Assert.Contains("正忙", Decoded(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoiceMessageUsesServerTranscript()
    {
        var turnTexts = new List<string>();
        var f = Build(
            (_, text, _) =>
            {
                turnTexts.Add(text);
                return Task.CompletedTask;
            }
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundVoice, times: 1);

        await RunUntilAsync(f, () => turnTexts.Count > 0);

        Assert.Equal(["语音转写：把主角写死"], turnTexts);
    }

    [Fact]
    public async Task UnsupportedMediaGetsAVisibleNotice()
    {
        // 静默吞掉图片会让用户以为通道坏了
        var turnCalled = false;
        var f = Build(
            (_, _, _) =>
            {
                turnCalled = true;
                return Task.CompletedTask;
            }
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundImage, times: 1);

        await RunUntilAsync(f, () => f.Handler.RequestsTo("sendmessage").Count > 0);

        Assert.False(turnCalled);
        Assert.Contains(
            "暂不支持",
            Decoded(Assert.Single(f.Handler.BodiesTo("sendmessage"))),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task BotEchoIsFiltered()
    {
        var turnCalled = false;
        var f = Build(
            (_, _, _) =>
            {
                turnCalled = true;
                return Task.CompletedTask;
            }
        );
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", InboundEcho, times: 1);

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.False(turnCalled);
        Assert.Empty(f.Handler.RequestsTo("sendmessage"));
    }

    [Fact]
    public async Task StaleTokenOnPollMarksBindingFailed()
    {
        var f = Build((_, _, _) => Task.CompletedTask);
        RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", """{"ret":0,"errcode":-14,"errmsg":"stale"}""", times: 1);

        using var cts = new CancellationTokenSource();
        await f.Bridge.RunAsync(cts.Token); // -14 → 循环自行退出
        await f.Bridge.DisposeAsync();

        Assert.Equal(WeChatPhase.Failed, f.Channel.Status.Snapshot.Phase);
        Assert.Contains("重新扫码", f.Channel.Status.Snapshot.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedReplyIsResentOnNextInbound()
    {
        // 窗口关闭（-14）时静默丢弃 = 用户永远看不到这条回复（官方客户端踩过的坑）
        var turnCount = 0;
        var f = Build(
            (bridge, _, _) =>
            {
                turnCount++;
                if (turnCount == 1)
                {
                    bridge.OnAgentEvent(Text("第一次的回答"));
                    bridge.OnAgentEvent(EndOf("第一次的回答"));
                }
                return Task.CompletedTask;
            }
        );
        f.Handler.RouteTo("getconfig", """{"ret":0,"typing_ticket":"t"}""", times: 10);
        f.Handler.RouteTo("sendtyping", """{"ret":0}""", times: 20);
        // 第一条 sendmessage 被拒（窗口关闭），之后放行
        f.Handler.RouteTo("sendmessage", """{"ret":0,"errcode":-14,"errmsg":"closed"}""", times: 1);
        f.Handler.RouteTo("sendmessage", """{"ret":0,"message_id":1}""", times: 20);
        f.Handler.RouteTo("getupdates", InboundText, times: 1);
        f.Handler.RouteTo("getupdates", InboundSecond, times: 1);

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && f.Handler.RequestsTo("sendmessage").Count < 2)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        var bodies = f.Handler.BodiesTo("sendmessage").Select(Decoded).ToList();
        Assert.True(bodies.Count >= 2, $"只发了 {bodies.Count} 条");
        Assert.Contains("第一次的回答", string.Join("\n", bodies), StringComparison.Ordinal);
        Assert.Equal(2, turnCount);
    }
}
