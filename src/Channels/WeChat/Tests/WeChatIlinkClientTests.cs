// 功能：协议层单测 —— uint64 无损改写、真实响应样本解析、请求头与请求体形状
//
// 两处 fixture 是 2026-10-16 对 https://ilinkai.weixin.qq.com 的真实抓包，
// 不是手写构造：协议形状变了它们会先失败。

using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Channels.WeChat.Tests;

public class ProtectUInt64Tests
{
    [Fact]
    public void QuotesBareNineteenDigitMessageId()
    {
        // 1234567890123456789 超出 double 精度，裸解析会被静默四舍五入成 …768
        const string raw = """{"message_id":1234567890123456789}""";

        var protectedJson = WeChatIlinkClient.ProtectUInt64(raw);

        Assert.Equal("""{"message_id":"1234567890123456789"}""", protectedJson);
    }

    [Fact]
    public void QuotesShortSeqToo()
    {
        // 不设位数门槛：短值漏过引号化会在反序列化时撞类型不匹配（seq 长度不定）
        Assert.Equal("""{"seq":"7"}""", WeChatIlinkClient.ProtectUInt64("""{"seq":7}"""));
    }

    [Fact]
    public void QuotesAllFourIdFields()
    {
        var raw =
            """{"message_id":1,"msg_id":2,"svr_id":3,"seq":4,"other_id":5,"ret":0,"errcode":-14}""";

        Assert.Equal(
            """{"message_id":"1","msg_id":"2","svr_id":"3","seq":"4","other_id":5,"ret":0,"errcode":-14}""",
            WeChatIlinkClient.ProtectUInt64(raw)
        );
    }

    [Fact]
    public void LeavesNonIdNumbersAlone()
    {
        // create_time_ms / playtime / ret / errcode 必须保持数字：被引号化后按 long/int 解析会失败
        var raw =
            """{"create_time_ms":1750000000000,"playtime":12,"ret":0,"errcode":-14,"text":"message_id:9"}""";

        var protectedJson = WeChatIlinkClient.ProtectUInt64(raw);

        Assert.Contains(
            "\"create_time_ms\":1750000000000",
            protectedJson,
            StringComparison.Ordinal
        );
        Assert.Contains("\"playtime\":12", protectedJson, StringComparison.Ordinal);
        Assert.Contains("\"ret\":0", protectedJson, StringComparison.Ordinal);
        Assert.Contains("\"errcode\":-14", protectedJson, StringComparison.Ordinal);
        // 字符串值里的 "message_id:9" 不受影响
        Assert.Contains("\"text\":\"message_id:9\"", protectedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void IsIdempotent()
    {
        const string raw = """{"message_id":1234567890123456789,"seq":7}""";

        var once = WeChatIlinkClient.ProtectUInt64(raw);

        Assert.Equal(once, WeChatIlinkClient.ProtectUInt64(once));
    }
}

public class QrCodeTests
{
    /// <summary>2026-10-16 真实抓包（ret:0）。</summary>
    private const string RealQrCodeResponse =
        """{"qrcode":"a6c329f83d2f068527a771dfcff74cc4","qrcode_img_content":"https://liteapp.weixin.qq.com/q/7GiQu1?qrcode=a6c329f83d2f068527a771dfcff74cc4&bot_type=3","ret":0}""";

    [Fact]
    public async Task ParsesRealQrCodeResponse()
    {
        var client = Stubs.Client(RealQrCodeResponse);

        var response = await client.FetchQrCodeAsync(
            "https://ilinkai.weixin.qq.com",
            [],
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, response.Ret);
        Assert.Equal("a6c329f83d2f068527a771dfcff74cc4", response.Qrcode);
        Assert.StartsWith(
            "https://liteapp.weixin.qq.com/",
            response.QrcodeImgContent,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task FetchQrCodeSendsRequiredHeadersAndTokenList()
    {
        var client = Stubs.Client(RealQrCodeResponse, out var handler);

        await client.FetchQrCodeAsync(
            "https://ilinkai.weixin.qq.com",
            ["old-token-1", "old-token-2"],
            TestContext.Current.CancellationToken
        );

        var request = handler.LastRequest;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("bot_type=3", request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal("bot", request.Headers.GetValues("iLink-App-Id").Single());
        Assert.Equal("204009", request.Headers.GetValues("iLink-App-ClientVersion").Single());
        // X-WECHAT-UIN = base64(十进制随机数)，必须存在且可解
        var uin = request.Headers.GetValues("X-WECHAT-UIN").Single();
        Assert.True(long.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(uin)), out _));

        // local_token_list 缺了服务端会重新签发并解绑旧绑定 —— 必须逐字带上
        Assert.Contains("old-token-1", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("old-token-2", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchQrCodeSendsEmptyTokenListWhenNone()
    {
        var client = Stubs.Client(RealQrCodeResponse, out var handler);

        await client.FetchQrCodeAsync(
            "https://ilinkai.weixin.qq.com",
            [],
            TestContext.Current.CancellationToken
        );

        Assert.Contains("local_token_list", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("[]", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollQrStatusParsesRealWaitResponse()
    {
        var client = Stubs.Client("""{"ret":0,"status":"wait"}""");

        var status = await client.PollQrStatusAsync(
            "https://ilinkai.weixin.qq.com",
            "a6c329f83d2f068527a771dfcff74cc4",
            verifyCode: null,
            timeoutMs: 1_000,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("wait", status.Status);
    }

    [Fact]
    public async Task PollQrStatusParsesConfirmedFields()
    {
        const string confirmed =
            """{"ret":0,"status":"confirmed","bot_token":"tk","ilink_bot_id":"abc@im.bot","baseurl":"https://ilinkai2.weixin.qq.com","ilink_user_id":"wxid_me"}""";
        var client = Stubs.Client(confirmed);

        var status = await client.PollQrStatusAsync(
            "https://ilinkai.weixin.qq.com",
            "qr",
            verifyCode: null,
            timeoutMs: 1_000,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("confirmed", status.Status);
        Assert.Equal("tk", status.BotToken);
        Assert.Equal("abc@im.bot", status.ILinkBotId);
        Assert.Equal("https://ilinkai2.weixin.qq.com", status.BaseUrl);
        Assert.Equal("wxid_me", status.ILinkUserId);
    }

    [Fact]
    public async Task ClientTimeoutIsReportedAsWaitNotFailure()
    {
        // 服务端挂起是常态；客户端预算先到时必须是 wait，不能当故障（否则会误判掉线）
        var client = Stubs.HangingClient(out _);

        var status = await client.PollQrStatusAsync(
            "https://ilinkai.weixin.qq.com",
            "qr",
            verifyCode: null,
            timeoutMs: 30,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("wait", status.Status);
    }

    [Fact]
    public async Task PollQrStatusIncludesVerifyCodeWhenPresent()
    {
        var client = Stubs.Client("""{"ret":0,"status":"wait"}""", out var handler);

        await client.PollQrStatusAsync(
            "https://ilinkai.weixin.qq.com",
            "qr code",
            verifyCode: "12 34",
            timeoutMs: 1_000,
            TestContext.Current.CancellationToken
        );

        var uri = handler.LastRequest.RequestUri!.OriginalString;
        Assert.Contains("qrcode=qr%20code", uri, StringComparison.Ordinal);
        Assert.Contains("verify_code=12%2034", uri, StringComparison.Ordinal);
    }
}

public class GetUpdatesTests
{
    /// <summary>19 位 message_id（超出 double 精度）+ 短 seq + 语音转写文本。</summary>
    private const string Inbound = """
        {"ret":0,"get_updates_buf":"buf-2","longpolling_timeout_ms":30000,"msgs":[
          {"seq":7,"message_id":1234567890123456789,"from_user_id":"wxid_me","to_user_id":"bot@im.bot",
           "message_type":1,"context_token":"ctx-9","item_list":[
             {"type":1,"msg_id":9876543210987654321,"create_time_ms":1750000000000,
              "text_item":{"text":"续写第三章"}},
             {"type":3,"playtime":5,"voice_item":{"text":"语音转写内容"}}
           ]}
        ]}
        """;

    [Fact]
    public async Task KeepsNineteenDigitIdsExact()
    {
        var client = Stubs.Client(Inbound);

        var updates = await client.GetUpdatesAsync(
            Stubs.Credential,
            syncBuf: "buf-1",
            longPollMs: 30_000,
            TestContext.Current.CancellationToken
        );

        var message = Assert.Single(updates.Msgs!);
        Assert.Equal("1234567890123456789", message.MessageId);
        Assert.Equal("7", message.Seq);
        Assert.Equal("9876543210987654321", message.ItemList![0].MsgId);
        // create_time_ms 未被引号化 → 仍按 long 解析
        Assert.Equal(1750000000000L, message.ItemList![0].CreateTimeMs);
    }

    [Fact]
    public async Task ParsesTextAndVoiceTranscript()
    {
        var client = Stubs.Client(Inbound);

        var updates = await client.GetUpdatesAsync(
            Stubs.Credential,
            syncBuf: "buf-1",
            longPollMs: 30_000,
            TestContext.Current.CancellationToken
        );

        var items = updates.Msgs![0].ItemList!;
        Assert.Equal("续写第三章", items[0].TextItem!.Text);
        Assert.Equal("语音转写内容", items[1].VoiceItem!.Text);
        Assert.Equal("ctx-9", updates.Msgs![0].ContextToken);
        Assert.Equal("buf-2", updates.GetUpdatesBuf);
        Assert.Equal(30_000, updates.LongPollingTimeoutMs);
    }

    [Fact]
    public async Task SendsBearerTokenAndBaseInfo()
    {
        var client = Stubs.Client("""{"ret":0,"msgs":[]}""", out var handler);

        await client.GetUpdatesAsync(
            Stubs.Credential,
            syncBuf: "buf-1",
            longPollMs: 30_000,
            TestContext.Current.CancellationToken
        );

        var request = handler.LastRequest;
        Assert.Equal("Bearer token-abc", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("ilink_bot_token", request.Headers.GetValues("AuthorizationType").Single());
        Assert.Contains(
            "ilink/bot/getupdates",
            request.RequestUri!.ToString(),
            StringComparison.Ordinal
        );
        // 游标必须回传，否则服务端会重放或丢消息
        Assert.Contains("buf-1", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains(WeChatConstants.ChannelVersion, handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PropagatesStaleTokenErrCode()
    {
        // -14 由调用方决定是否进入一小时冷却；协议层只负责如实上报
        var client = Stubs.Client("""{"ret":0,"errcode":-14,"errmsg":"token expired"}""");

        var updates = await client.GetUpdatesAsync(
            Stubs.Credential,
            syncBuf: "",
            longPollMs: 30_000,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WeChatConstants.StaleTokenErrcode, updates.ErrCode);
    }
}

public class SendTextTests
{
    [Fact]
    public async Task SendsExpectedMessageShape()
    {
        var client = Stubs.Client("""{"ret":0,"message_id":42}""", out var handler);

        await client.SendTextAsync(
            Stubs.Credential,
            "wxid_me",
            "第三章已写好",
            "ctx-9",
            TestContext.Current.CancellationToken
        );

        var body = handler.LastBody;
        Assert.Contains("\"to_user_id\":\"wxid_me\"", body, StringComparison.Ordinal);
        Assert.Contains("\"message_type\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"context_token\":\"ctx-9\"", body, StringComparison.Ordinal);
        Assert.Contains("mythology-wechat-", body, StringComparison.Ordinal);

        // 中文会被默认编码器转义成 \uXXXX（LKK 同款行为，未被本次移植改动）。
        // 只断言语义：解析后必须还原成原文，否则服务端收到的是转义码点而非文本。
        using var doc = JsonDocument.Parse(body);
        var text = doc
            .RootElement.GetProperty("msg")
            .GetProperty("item_list")[0]
            .GetProperty("text_item")
            .GetProperty("text")
            .GetString();
        Assert.Equal("第三章已写好", text);

        Assert.Contains(
            "ilink/bot/sendmessage",
            handler.LastRequest.RequestUri!.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StaleTokenBecomesRefusedException()
    {
        var client = Stubs.Client("""{"ret":0,"errcode":-14,"errmsg":"window closed"}""");

        var ex = await Assert.ThrowsAsync<WeChatSendRefusedException>(() =>
            client.SendTextAsync(
                Stubs.Credential,
                "wxid_me",
                "hi",
                null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(WeChatConstants.StaleTokenErrcode, ex.ErrCode);
        Assert.Contains("先", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonZeroErrCodeBecomesRefusedException()
    {
        // 被拒的响应不带 message_id；只看 id 会把"被拒"误判成"已送达"
        var client = Stubs.Client("""{"ret":0,"errcode":5,"errmsg":"rate limited"}""");

        var ex = await Assert.ThrowsAsync<WeChatSendRefusedException>(() =>
            client.SendTextAsync(
                Stubs.Credential,
                "wxid_me",
                "hi",
                null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(5, ex.ErrCode);
        Assert.Contains("rate limited", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpErrorThrows()
    {
        var handler = new RecordingHandler(
            (_, _) =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("nope", Encoding.UTF8),
                }
        );
        var client = new WeChatIlinkClient(new StubHttpClientFactory(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendTextAsync(
                Stubs.Credential,
                "wxid_me",
                "hi",
                null,
                TestContext.Current.CancellationToken
            )
        );
    }
}

public class TypingTests
{
    [Fact]
    public async Task GetConfigSendsRequiredILinkUserId()
    {
        // 缺 ilink_user_id：服务端 HTTP 200 回 ret:-2，票据静默为空 —— typing 变静默空操作
        var client = Stubs.Client("""{"ret":0,"typing_ticket":"ticket-1"}""", out var handler);

        var config = await client.GetConfigAsync(
            Stubs.Credential,
            "wxid_me",
            "ctx-9",
            TestContext.Current.CancellationToken
        );

        Assert.Equal("ticket-1", config.TypingTicket);
        Assert.Contains(
            "\"ilink_user_id\":\"wxid_me\"",
            handler.LastBody,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ilink/bot/getconfig",
            handler.LastRequest.RequestUri!.ToString(),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(true, WeChatConstants.TypingStatusTyping)]
    [InlineData(false, WeChatConstants.TypingStatusCancel)]
    public async Task SendTypingMapsStatus(bool typing, int expected)
    {
        var client = Stubs.Client("""{"ret":0}""", out var handler);

        await client.SendTypingAsync(
            Stubs.Credential,
            "wxid_me",
            "ticket-1",
            typing,
            TestContext.Current.CancellationToken
        );

        Assert.Contains($"\"status\":{expected}", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains(
            "\"typing_ticket\":\"ticket-1\"",
            handler.LastBody,
            StringComparison.Ordinal
        );
    }
}
