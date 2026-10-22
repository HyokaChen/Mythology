// 功能：微信通道测试的共享夹具 —— 真实通道服务 + 桩 HTTP + 共享 UserQuestionStore
//
// 独立成类而不是塞进某个测试文件：问题回路（ask_user）与消息回路共用同一套装配，
// 各写一份必然漂移。

using Core.Agent;
using Core.Shared;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Http;
using Xunit;

namespace Channels.WeChat.Tests;

/// <summary>一套装配好的通道 + 桥接 + 桩。</summary>
internal sealed record WeChatFixture(
    WeChatChannelService Channel,
    WeChatAgentChannel Bridge,
    PathHandler Handler,
    UserQuestionStore Questions,
    TempDir Dir
);

internal static class WeChatFixtures
{
    /// <summary>
    /// 建夹具。<paramref name="runTurn"/> 收 bridge 作为第一参，避免用例里写"先声明后赋值"的捕获。
    /// </summary>
    public static WeChatFixture Build(
        Func<WeChatAgentChannel, string, CancellationToken, Task> runTurn,
        Func<bool>? isAgentBusy = null,
        UserQuestionStore? questions = null
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
            isAgentBusy,
            questions ?? new UserQuestionStore()
        );
        return new WeChatFixture(
            channel,
            bridge,
            handler,
            questions ?? new UserQuestionStore(),
            dir
        );
    }

    public static void RouteCommon(PathHandler handler)
    {
        handler.RouteTo("getconfig", """{"ret":0,"typing_ticket":"ticket-1"}""", times: 10);
        handler.RouteTo("sendtyping", """{"ret":0}""", times: 20);
        handler.RouteTo("sendmessage", """{"ret":0,"message_id":9001}""", times: 20);
    }

    /// <summary>跑长轮询直到条件满足，随后取消并收尾。</summary>
    public static async Task RunUntilAsync(WeChatFixture f, Func<bool> condition)
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

    public static StreamChunk Text(string delta) => new(delta, delta, StreamContentType.Text);

    /// <summary>"手机上是否已发出含该文本的消息"——用作后续入站消息的送达闸门。</summary>
    public static Func<bool> SentText(WeChatFixture f, string needle) =>
        () =>
            f
                .Handler.BodiesTo("sendmessage")
                .Select(Decoded)
                .Any(b => b.Contains(needle, StringComparison.Ordinal));

    public static MessageEnd EndOf(string text) =>
        new(new ChatMessage(ChatMessageRoles.Assistant, text));

    /// <summary>请求体是 JSON，中文被默认编码器转义成 \uXXXX；断言前先解回原文。</summary>
    public static string Decoded(string jsonBody) =>
        System.Text.RegularExpressions.Regex.Replace(
            jsonBody,
            @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
        );

    /// <summary>拼一条含指定文本的入站 getupdates 响应（普通插值字符串：避开原始字符串的花括号歧义）。</summary>
    public static string Inbound(string text, string seq = "1") =>
        "{\"ret\":0,\"get_updates_buf\":\"buf-"
        + seq
        + "\",\"msgs\":[{\"seq\":"
        + seq
        + ",\"message_id\":"
        + seq
        + "11,\"message_type\":1,\"from_user_id\":\"wxid_me\","
        + "\"context_token\":\"ctx-1\",\"item_list\":[{\"type\":1,\"text_item\":"
        + "{\"text\":\""
        + text
        + "\"}}]}]}";
}
