// 功能：微信绑定凭证（登录成功后由二维码状态响应签发，落盘由宿主的绑定存储负责）
//
// 与 LKK 的 WeChatBindingDto 对应。凭证是可丢缓存的反面——它是长期有效的身份，
// 丢了就要重新扫码，所以落盘要加密（见 WeChatBindingStore）。
// 上下文令牌不在凭证里：它每条入站消息都会变，属于会话态，存在 WeChatBindingState。

namespace Channels.WeChat;

/// <summary>iLink bot 绑定凭证。一旦签发即长期有效，除服务端以 errcode -14 宣告失效。</summary>
public sealed record WeChatCredential
{
    /// <summary>登录后服务端下发的主机；未下发时回落到固定入口。</summary>
    public string BaseUrl { get; init; } = WeChatConstants.FixedBaseUrl;

    /// <summary>bot token。请求头 Authorization: Bearer。</summary>
    public string BotToken { get; init; } = "";

    /// <summary>bot 身份（ilink_bot_id，形如 xxxx@im.bot）。</summary>
    public string AccountId { get; init; } = "";

    /// <summary>扫码绑定的微信用户 ID（ilink_user_id）。回复消息时作为 to_user_id。</summary>
    public string PeerUserId { get; init; } = "";

    /// <summary>绑定时间（展示与排障用）。</summary>
    public DateTimeOffset BoundAt { get; init; }

    /// <summary>是否齐备到可以发起长轮询。</summary>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(PeerUserId);
}
