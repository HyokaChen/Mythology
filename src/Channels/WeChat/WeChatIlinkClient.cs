// 移植自 LittleKingKnows/LittleKingKnows.App/Services/WeChat/WeChatIlinkClient.cs（AGPL-3.0）
// 功能：iLink Bot 协议 HTTP 客户端（二维码登录 + 长轮询收消息 + 发文本 + typing）
//
// 移植时保留了三处易错点（原注释一并保留，因为它们是这份代码最大的价值）：
// ① 请求必须带 local_token_list，否则服务端视为全新客户端，会重新签发并解绑旧绑定；
// ② bot_type=3 必须与官方微信渠道构建一字不差；
// ③ uint64 标识符（message_id / msg_id / svr_id / seq）在 JSON 里是裸数字，必须先加引号再反序列化。
//
// 未移植：CDN 媒体上传/下载与 AES（GetUploadUrlAsync / UploadToCdnAsync / SendMediaItemAsync）。
// 超长回复改为分段发多条，见 WeChatConstants.MaxTextChars。

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Channels.WeChat;

/// <summary>
/// iLink Bot 协议客户端。只负责"把字节收发对"，不含任何会话/重试编排——那些在通道服务里。
/// </summary>
internal sealed partial class WeChatIlinkClient(
    IHttpClientFactory httpClientFactory,
    ILogger<WeChatIlinkClient>? logger = null
)
{
    /// <summary>
    /// 模型按 string 收、但服务端会发裸数字的标识符字段。
    /// message_id / svr_id 是 uint64（19 位裸数字会被静默四舍五入）；seq / msg_id 长度不定（短至 1 位），
    /// 故不设位数门槛：门槛会让短值漏过引号化，反序列化时撞上类型不匹配。
    /// </summary>
    [GeneratedRegex("\"(message_id|msg_id|svr_id|seq)\"\\s*:\\s*(\\d+)")]
    private static partial Regex StringifiedIdRegex();

    /// <summary>
    /// 把数字标识符值改写为字符串，避免 JSON 数字精度丢失（19 位 message id 会被静默四舍五入）。
    /// 只匹配 "字段名":数字 形态的对象属性值，字符串里的数字不受影响。
    /// </summary>
    internal static string ProtectUInt64(string rawJson) =>
        StringifiedIdRegex().Replace(rawJson, "\"$1\":\"$2\"");

    /// <summary>请求二维码。</summary>
    public async Task<WeChatQrCodeResponse> FetchQrCodeAsync(
        string baseUrl,
        IReadOnlyList<string> localTokenList,
        CancellationToken cancellationToken = default
    )
    {
        var body = new Dictionary<string, object>
        {
            ["local_token_list"] = localTokenList ?? (IReadOnlyList<string>)[],
        };
        var result = await CallAsync(
            baseUrl,
            $"ilink/bot/get_bot_qrcode?bot_type={WeChatConstants.DefaultBotType}",
            HttpMethod.Post,
            body,
            token: null,
            timeoutMs: WeChatConstants.QrRequestTimeoutMs,
            cancellationToken: cancellationToken
        );
        return Deserialize<WeChatQrCodeResponse>(result.Body);
    }

    /// <summary>
    /// 轮询一次二维码状态。服务端会挂起请求，客户端超时是被服务端抢先的正常空转，
    /// 此时返回 Status = "wait"（不是错误）。
    /// </summary>
    public async Task<WeChatQrStatusResponse> PollQrStatusAsync(
        string baseUrl,
        string qrCode,
        string? verifyCode,
        int timeoutMs,
        CancellationToken cancellationToken = default
    )
    {
        var endpoint =
            $"ilink/bot/get_qrcode_status?qrcode={Uri.EscapeDataString(qrCode)}"
            + (
                string.IsNullOrWhiteSpace(verifyCode)
                    ? string.Empty
                    : $"&verify_code={Uri.EscapeDataString(verifyCode)}"
            );

        try
        {
            var result = await CallAsync(
                baseUrl,
                endpoint,
                HttpMethod.Get,
                body: null,
                token: null,
                timeoutMs: timeoutMs,
                cancellationToken: cancellationToken
            );
            return Deserialize<WeChatQrStatusResponse>(result.Body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 客户端预算先到：等价于"暂时没有新状态"
            return new WeChatQrStatusResponse { Status = "wait" };
        }
    }

    /// <summary>长轮询拉取入站消息。</summary>
    public async Task<WeChatGetUpdatesResponse> GetUpdatesAsync(
        WeChatCredential credential,
        string syncBuf,
        int longPollMs,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(credential);

        // 客户端预算比服务端建议值多 5s：保证总是服务端先返回，不会因客户端超时丢消息
        var body = new Dictionary<string, object>
        {
            ["get_updates_buf"] = syncBuf,
            ["base_info"] = new Dictionary<string, object>
            {
                ["channel_version"] = WeChatConstants.ChannelVersion,
                ["bot_agent"] = WeChatConstants.BotAgent,
            },
        };

        var result = await CallAsync(
            credential.BaseUrl,
            "ilink/bot/getupdates",
            HttpMethod.Post,
            body,
            token: credential.BotToken,
            timeoutMs: longPollMs + 5_000,
            cancellationToken: cancellationToken
        );
        return Deserialize<WeChatGetUpdatesResponse>(result.Body);
    }

    /// <summary>发送一条文本消息。</summary>
    /// <exception cref="WeChatSendRefusedException">服务端拒绝（errcode 非 0）。</exception>
    public async Task SendTextAsync(
        WeChatCredential credential,
        string toPeerId,
        string text,
        string? contextToken,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(toPeerId);

        var msg = new WeChatMessage
        {
            FromUserId = string.Empty,
            ToUserId = toPeerId,
            ClientId = $"mythology-wechat-{Guid.NewGuid():N}",
            MessageType = WeChatConstants.MessageTypeBot,
            MessageState = 2, // FINISH
            ItemList = string.IsNullOrEmpty(text)
                ? null
                :
                [
                    new WeChatMessageItem
                    {
                        Type = WeChatConstants.ItemTypeText,
                        TextItem = new WeChatTextItem { Text = text },
                    },
                ],
            ContextToken = string.IsNullOrWhiteSpace(contextToken) ? null : contextToken,
        };

        var result = await CallAsync(
            credential.BaseUrl,
            "ilink/bot/sendmessage",
            HttpMethod.Post,
            new WeChatSendRequest { Msg = msg },
            token: credential.BotToken,
            timeoutMs: WeChatConstants.DefaultApiTimeoutMs,
            cancellationToken: cancellationToken
        );

        var response = Deserialize<WeChatSendMessageResponse>(result.Body);
        // 必须先判 errcode 再读 message_id：被拒的响应不带 id，只读 id 会把"被拒"误判成"已送达"
        var errCode = response.ErrCode ?? response.Ret;
        if (errCode is null or 0)
            return;

        if (errCode == WeChatConstants.StaleTokenErrcode)
        {
            throw new WeChatSendRefusedException(
                "微信会话已超时。需要用户先在微信里给机器人发一条消息，之后才能回复。",
                errCode.Value
            );
        }

        throw new WeChatSendRefusedException(
            string.Format(
                CultureInfo.InvariantCulture,
                "发送被微信拒绝：errcode={0} errmsg={1}",
                errCode.Value,
                response.ErrMsg ?? "(无)"
            ),
            errCode.Value
        );
    }

    /// <summary>
    /// 取 typing 票据。
    /// ⚠️ 必须带 `ilink_user_id`：缺它服务端 HTTP 200 回 ret:-2、typing_ticket 为空，
    /// 整个 typing 功能变成**静默空操作**（官方源码里专门记了这次踩坑）。
    /// </summary>
    public async Task<WeChatConfigResponse> GetConfigAsync(
        WeChatCredential credential,
        string peerId,
        string? contextToken,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(credential);

        var result = await CallAsync(
            credential.BaseUrl,
            "ilink/bot/getconfig",
            HttpMethod.Post,
            new WeChatGetConfigRequest
            {
                ILinkUserId = peerId,
                ContextToken = string.IsNullOrWhiteSpace(contextToken) ? null : contextToken,
            },
            token: credential.BotToken,
            timeoutMs: 10_000,
            cancellationToken: cancellationToken
        );
        return Deserialize<WeChatConfigResponse>(result.Body);
    }

    /// <summary>发送/取消 typing 指示器。</summary>
    public async Task SendTypingAsync(
        WeChatCredential credential,
        string peerId,
        string typingTicket,
        bool typing,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(credential);

        await CallAsync(
            credential.BaseUrl,
            "ilink/bot/sendtyping",
            HttpMethod.Post,
            new WeChatSendTypingRequest
            {
                ILinkUserId = peerId,
                TypingTicket = typingTicket,
                Status = typing
                    ? WeChatConstants.TypingStatusTyping
                    : WeChatConstants.TypingStatusCancel,
            },
            token: credential.BotToken,
            timeoutMs: 10_000,
            cancellationToken: cancellationToken
        );
    }

    private static T Deserialize<T>(string rawJson)
        where T : class =>
        JsonSerializer.Deserialize<T>(ProtectUInt64(rawJson))
        ?? throw new InvalidOperationException($"微信协议响应为空或无法解析: {typeof(T).Name}");

    /// <summary>一次调用的原始结果（保留响应头：后续若补 CDN 上传，下载参数只在 x-encrypted-param 里）。</summary>
    internal sealed record WeChatHttpResult(
        int StatusCode,
        string Body,
        string? EncryptedParam,
        string? ErrorMessage
    );

    private async Task<WeChatHttpResult> CallAsync(
        string baseUrl,
        string endpoint,
        HttpMethod method,
        object? body,
        string? token,
        int timeoutMs,
        CancellationToken cancellationToken
    )
    {
        var root = baseUrl.EndsWith('/') ? baseUrl : $"{baseUrl}/";
        var url = new Uri(new Uri(root, UriKind.Absolute), endpoint);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("iLink-App-Id", WeChatConstants.AppId);
        request.Headers.TryAddWithoutValidation(
            "iLink-App-ClientVersion",
            WeChatConstants.AppClientVersion
        );
        request.Headers.TryAddWithoutValidation("X-WECHAT-UIN", RandomWeChatUin());

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("AuthorizationType", "ilink_bot_token");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(
                body,
                new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }
            );
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        var client = httpClientFactory.CreateClient(nameof(WeChatIlinkClient));
        using var response = await client.SendAsync(request, cts.Token);
        var text = await response.Content.ReadAsStringAsync(cts.Token);

        var encryptedParam = response.Headers.TryGetValues("x-encrypted-param", out var ep)
            ? ep.FirstOrDefault()
            : null;
        var errorMessage = response.Headers.TryGetValues("x-error-message", out var em)
            ? em.FirstOrDefault()
            : null;

        if (!response.IsSuccessStatusCode)
        {
            logger?.LogWarning(
                "微信协议调用失败: {Endpoint} HTTP {Status}, Body={Body}, ErrorMessage={ErrorMessage}",
                endpoint,
                (int)response.StatusCode,
                Truncate(text),
                errorMessage ?? "(无)"
            );
            throw new HttpRequestException(
                $"微信协议调用失败: {endpoint} HTTP {(int)response.StatusCode}",
                null,
                response.StatusCode
            );
        }

        return new WeChatHttpResult((int)response.StatusCode, text, encryptedParam, errorMessage);
    }

    /// <summary>X-WECHAT-UIN：随机 uint32 的十进制串再做 base64。</summary>
    private static string RandomWeChatUin()
    {
        var value = Random.Shared.NextInt64(0, (long)uint.MaxValue + 1);
        var bytes = Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(bytes);
    }

    private static string Truncate(string value, int max = 500) =>
        value.Length <= max ? value : value[..max] + "…";
}
