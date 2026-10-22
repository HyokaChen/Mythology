// 移植自 LittleKingKnows/LittleKingKnows.App/Services/WeChat/WeChatProtocol.cs（AGPL-3.0）
// 功能：微信（iLink Bot）协议层常量与 DTO —— 零依赖，可独立编译与测试
//
// 协议来源：腾讯官方实现 @tencent-weixin/openclaw-weixin（MIT）。
// 本文件只保留 v1 需要的端点：二维码登录 / 长轮询收消息 / 发文本 / typing。
// 媒体（CDN 上传下载 + AES）未移植——超长回复改为分段发多条，见 WeChatConstants.MaxTextChars。

using System.Text.Json.Serialization;

namespace Channels.WeChat;

/// <summary>
/// iLink Bot 协议常量。服务端在登录响应里返回的主机（baseurl / redirect_host）不硬编码。
/// </summary>
internal static class WeChatConstants
{
    /// <summary>二维码请求固定入口；登录后可能 redirect 到其它 IDC 主机。</summary>
    public const string FixedBaseUrl = "https://ilinkai.weixin.qq.com";

    /// <summary>租户/应用 ID，请求头 iLink-App-Id。</summary>
    public const string AppId = "bot";

    /// <summary>客户端构建号，请求头 iLink-App-ClientVersion（uint32 编码为 0x00MMNNPP）。</summary>
    public const string AppClientVersion = "204009";

    /// <summary>通道版本，base_info.channel_version。</summary>
    public const string ChannelVersion = "2.4.9";

    /// <summary>bot 身份提示（仅观测用，不参与鉴权与路由）。</summary>
    public const string BotAgent = "mythology-wechat/1.0.0";

    /// <summary>bot_type 查询参数。官方微信渠道构建值为 3，服务端可能按客户端类型分支，保持一字不差。</summary>
    public const string DefaultBotType = "3";

    /// <summary>服务器建议的长轮询挂起时长（ms），服务端下发自身值之前用它。实测服务端挂起约 30s。</summary>
    public const int DefaultLongPollTimeoutMs = 35_000;

    /// <summary>普通调用（sendmessage 等）超时（ms）。</summary>
    public const int DefaultApiTimeoutMs = 15_000;

    /// <summary>二维码状态轮询的单次长轮询客户端预算（ms）；必须大于服务端 30s 挂起。</summary>
    public const int QrPollTimeoutMs = 35_000;

    /// <summary>二维码请求超时（ms）。</summary>
    public const int QrRequestTimeoutMs = 20_000;

    /// <summary>登录握手总时限（ms）。</summary>
    public const int LoginTimeoutMs = 5 * 60_000;

    /// <summary>一次登录会话内二维码过期后的最大刷新次数。</summary>
    public const int MaxQrRefresh = 3;

    /// <summary>连续失败达到该次数后进入长退避。</summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>可恢复失败的基础重试间隔（ms）。</summary>
    public const int RetryDelayMs = 2_000;

    /// <summary>连续失败后的退避间隔（ms）。</summary>
    public const int BackoffDelayMs = 30_000;

    /// <summary>服务端错误码：bot token 失效。官方客户端此时对本账号静默一小时再重新鉴权。</summary>
    public const int StaleTokenErrcode = -14;

    /// <summary>token 失效后的冷却时长（ms），复刻官方客户端行为，避免无意义地打服务端。</summary>
    public const int SessionPauseMs = 60 * 60_000;

    /// <summary>
    /// 单条微信文本上限（字符）。超出**不截断**——静默丢答案后半段是最不能接受的失败模式。
    /// v1 改为按此长度分段连发多条（未移植 CDN，故不发 .md 文件）。
    /// </summary>
    public const int MaxTextChars = 4_000;

    /// <summary>
    /// 回复合并窗（ms）：agent 停顿这么久就把已累积文本发一条微信。
    /// 对齐官方 REPLY_SETTLE_MS=1200；回合结束事件不等窗口，立即冲刷。
    /// </summary>
    public const int SettleMs = 1_200;

    /// <summary>回合运行中超过这么久无新内容就发一条"仍在处理中"（ms）。</summary>
    public const int HeartbeatSilenceMs = 60_000;

    /// <summary>typing 指示器重发间隔（ms）——客户端几秒后会自动消失（官方 TYPING_REFRESH_MS=5000）。</summary>
    public const int TypingRefreshMs = 5_000;

    /// <summary>typing 票据刷新间隔（ms）：提前于服务端过期重取，保障长任务期间指示器不断。</summary>
    public const int TypingTicketRefreshMs = 240_000;

    /// <summary>typing 状态（官方 TypingStatus）。</summary>
    public const int TypingStatusTyping = 1;
    public const int TypingStatusCancel = 2;

    /// <summary>消息方向：1 = USER（用户发来）；2 = BOT（自己发的回声，天然防循环）。</summary>
    public const int MessageTypeUser = 1;
    public const int MessageTypeBot = 2;

    /// <summary>消息元素类型：1=文本 2=图 3=语音 4=文件 5=视频。</summary>
    public const int ItemTypeText = 1;
    public const int ItemTypeImage = 2;
    public const int ItemTypeVoice = 3;
    public const int ItemTypeFile = 4;
    public const int ItemTypeVideo = 5;
}

/// <summary>二维码请求响应。</summary>
internal sealed class WeChatQrCodeResponse
{
    /// <summary>会话句柄，回传给状态端点。</summary>
    [JsonPropertyName("qrcode")]
    public string? Qrcode { get; set; }

    /// <summary>二维码内容（实测是 liteapp.weixin.qq.com 链接），交给宿主渲染成码。</summary>
    [JsonPropertyName("qrcode_img_content")]
    public string? QrcodeImgContent { get; set; }

    [JsonPropertyName("ret")]
    public int? Ret { get; set; }
}

/// <summary>
/// 二维码状态轮询响应。
/// Status 取值：wait / scaned / confirmed / expired / scaned_but_redirect /
/// need_verifycode / verify_code_blocked / binded_redirect。
/// </summary>
internal sealed class WeChatQrStatusResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>confirmed 时签发。</summary>
    [JsonPropertyName("bot_token")]
    public string? BotToken { get; set; }

    /// <summary>bot 身份，形如 xxxx@im.bot，用作 accountId。</summary>
    [JsonPropertyName("ilink_bot_id")]
    public string? ILinkBotId { get; set; }

    /// <summary>登录后应使用的 API 主机。</summary>
    [JsonPropertyName("baseurl")]
    public string? BaseUrl { get; set; }

    /// <summary>扫码的微信用户 ID，绑定校验用。</summary>
    [JsonPropertyName("ilink_user_id")]
    public string? ILinkUserId { get; set; }

    /// <summary>scaned_but_redirect 时要求切换的轮询主机。</summary>
    [JsonPropertyName("redirect_host")]
    public string? RedirectHost { get; set; }

    [JsonPropertyName("ret")]
    public int? Ret { get; set; }

    [JsonPropertyName("errcode")]
    public int? ErrCode { get; set; }

    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; set; }
}

/// <summary>getupdates 长轮询响应。</summary>
internal sealed class WeChatGetUpdatesResponse
{
    [JsonPropertyName("ret")]
    public int? Ret { get; set; }

    [JsonPropertyName("errcode")]
    public int? ErrCode { get; set; }

    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; set; }

    [JsonPropertyName("msgs")]
    public List<WeChatMessage>? Msgs { get; set; }

    /// <summary>增量游标，必须缓存并在下一轮回传。</summary>
    [JsonPropertyName("get_updates_buf")]
    public string? GetUpdatesBuf { get; set; }

    /// <summary>服务端建议的下一轮长轮询时长（ms）。</summary>
    [JsonPropertyName("longpolling_timeout_ms")]
    public int? LongPollingTimeoutMs { get; set; }
}

/// <summary>
/// 统一消息体。
/// MessageId / Seq 按 string 接收：协议定义是 uint64，裸 JSON 数字会静默失真
/// （19 位整数超出 double 精度，会被四舍五入）——见 <see cref="WeChatIlinkClient.ProtectUInt64"/>。
/// </summary>
internal sealed class WeChatMessage
{
    [JsonPropertyName("seq")]
    public string? Seq { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("from_user_id")]
    public string? FromUserId { get; set; }

    [JsonPropertyName("to_user_id")]
    public string? ToUserId { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    /// <summary>群聊时非空。v1 只处理单聊。</summary>
    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }

    /// <summary>1 = USER（用户发来）；2 = BOT（自己发的回声，天然防循环）。</summary>
    [JsonPropertyName("message_type")]
    public int? MessageType { get; set; }

    [JsonPropertyName("message_state")]
    public int? MessageState { get; set; }

    [JsonPropertyName("item_list")]
    public List<WeChatMessageItem>? ItemList { get; set; }

    /// <summary>回复时必须逐字带回的令牌，按会话缓存。</summary>
    [JsonPropertyName("context_token")]
    public string? ContextToken { get; set; }
}

/// <summary>消息内容元素。type：1=文本 2=图 3=语音 4=文件 5=视频。</summary>
internal sealed class WeChatMessageItem
{
    [JsonPropertyName("type")]
    public int? Type { get; set; }

    /// <summary>uint64 语义，同 message_id，故按 string 收。</summary>
    [JsonPropertyName("msg_id")]
    public string? MsgId { get; set; }

    [JsonPropertyName("create_time_ms")]
    public long? CreateTimeMs { get; set; }

    [JsonPropertyName("text_item")]
    public WeChatTextItem? TextItem { get; set; }

    [JsonPropertyName("voice_item")]
    public WeChatVoiceItem? VoiceItem { get; set; }
}

/// <summary>文本元素。</summary>
internal sealed class WeChatTextItem
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>语音元素。服务端可能已给转写文本，有它就不必下载音频（v1 只取文本）。</summary>
internal sealed class WeChatVoiceItem
{
    [JsonPropertyName("encode_type")]
    public int? EncodeType { get; set; }

    [JsonPropertyName("playtime")]
    public int? PlayTime { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>getconfig 响应（typing 票据）。</summary>
internal sealed class WeChatConfigResponse
{
    [JsonPropertyName("ret")]
    public int? Ret { get; set; }

    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; set; }

    /// <summary>typing 票据；缺它 sendtyping 是静默空操作。</summary>
    [JsonPropertyName("typing_ticket")]
    public string? TypingTicket { get; set; }
}

/// <summary>
/// getconfig 请求体。
/// ⚠️ `ilink_user_id` 必填——缺它服务端 HTTP 200 回 ret:-2，票据静默为空。
/// </summary>
internal sealed class WeChatGetConfigRequest
{
    [JsonPropertyName("ilink_user_id")]
    public string? ILinkUserId { get; set; }

    [JsonPropertyName("context_token")]
    public string? ContextToken { get; set; }
}

/// <summary>sendtyping 请求体。</summary>
internal sealed class WeChatSendTypingRequest
{
    [JsonPropertyName("ilink_user_id")]
    public string? ILinkUserId { get; set; }

    [JsonPropertyName("typing_ticket")]
    public string? TypingTicket { get; set; }

    /// <summary>1=打字中 2=取消。</summary>
    [JsonPropertyName("status")]
    public int? Status { get; set; }
}

/// <summary>sendmessage 响应。errcode 非 0 = 被拒（且不带 message_id）。</summary>
internal sealed class WeChatSendMessageResponse
{
    [JsonPropertyName("ret")]
    public int? Ret { get; set; }

    [JsonPropertyName("errcode")]
    public int? ErrCode { get; set; }

    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }
}

/// <summary>sendmessage 请求体。</summary>
internal sealed class WeChatSendRequest
{
    [JsonPropertyName("msg")]
    public WeChatMessage? Msg { get; set; }
}

/// <summary>
/// 发送被微信拒绝。与其它失败分开：这类失败换个时间（用户再发一条消息打开回复窗口）
/// 就可能成功，不能与"网络不通"混为一谈。
/// </summary>
internal sealed class WeChatSendRefusedException : Exception
{
    public WeChatSendRefusedException(string message, int errCode)
        : base(message)
    {
        ErrCode = errCode;
    }

    /// <summary>服务端给出的拒绝码。</summary>
    public int ErrCode { get; }
}
