// 功能：微信通道的消息收发（长轮询拉入站 / 发文本 / typing）—— WeChatChannelService 的消息面
//
// 分文件理由：绑定面（TryRestore/BindAsync/Unbind + 单实例锁）与消息面（轮询/发送）是两件事，
// 各自有独立的状态与失败模式，混在一个文件里会让 300 行里找不到重点。

using Microsoft.Extensions.Logging;

namespace Channels.WeChat;

/// <summary>一条入站消息（已从协议 DTO 提炼成宿主能直接用的形状）。</summary>
public sealed record WeChatInboundMessage
{
    /// <summary>消息 ID（uint64 语义，协议层已做无损处理）。</summary>
    public string MessageId { get; init; } = "";

    /// <summary>用户文本。语音消息取服务端转写文本。</summary>
    public string Text { get; init; } = "";

    /// <summary>本条是否带 v1 不支持的媒体（图片/文件/视频）——要回一句提示，而不是静默吞掉。</summary>
    public bool HasUnsupportedMedia { get; init; }

    /// <summary>是否可交给 Agent 处理。</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>一次长轮询的结果。</summary>
public sealed record WeChatPollResult
{
    /// <summary>本轮入站消息（已过滤掉自己的回声与群聊）。</summary>
    public IReadOnlyList<WeChatInboundMessage> Messages { get; init; } = [];

    /// <summary>服务端建议的下一轮长轮询时长（ms）。</summary>
    public int SuggestedLongPollMs { get; init; } = WeChatConstants.DefaultLongPollTimeoutMs;

    /// <summary>服务端宣告 token 失效（-14）。调用方应停止轮询并提示重新扫码。</summary>
    public bool BindingExpired { get; init; }
}

/// <summary>发送结果。</summary>
public enum WeChatSendOutcome
{
    /// <summary>已送达。</summary>
    Sent,

    /// <summary>回复窗口已关闭（-14）：用户需先发一条消息，此条应在下次入站时补发。</summary>
    WindowClosed,

    /// <summary>未绑定，无处可发。</summary>
    NotBound,
}

public sealed partial class WeChatChannelService
{
    /// <summary>
    /// 长轮询拉取入站消息。服务端会挂起（实测约 30s），这是正常的，不是超时。
    /// 游标与上下文令牌都会落盘：游标回退会导致消息重放，令牌丢失会导致回复被拒。
    /// </summary>
    public async Task<WeChatPollResult> PollAsync(
        int longPollMs,
        CancellationToken cancellationToken = default
    )
    {
        var state = Volatile.Read(ref _state);
        if (state.Credential?.IsUsable != true)
            return new WeChatPollResult();

        var response = await _client.GetUpdatesAsync(
            state.Credential,
            state.GetUpdatesBuf,
            longPollMs,
            cancellationToken
        );

        if (response.ErrCode == WeChatConstants.StaleTokenErrcode)
            return new WeChatPollResult { BindingExpired = true };

        var messages = new List<WeChatInboundMessage>();
        var contextToken = state.ContextToken;

        foreach (var raw in response.Msgs ?? [])
        {
            // 上下文令牌按会话更新：回复时必须逐字带回最新那个
            if (raw.ContextToken is { Length: > 0 } token)
                contextToken = token;

            if (ToInbound(raw) is { } inbound)
                messages.Add(inbound);
        }

        Persist(
            state with
            {
                GetUpdatesBuf = string.IsNullOrEmpty(response.GetUpdatesBuf)
                    ? state.GetUpdatesBuf
                    : response.GetUpdatesBuf,
                ContextToken = contextToken,
            }
        );

        return new WeChatPollResult
        {
            Messages = messages,
            SuggestedLongPollMs = response.LongPollingTimeoutMs is > 0 and <= 120_000
                ? response.LongPollingTimeoutMs.Value
                : longPollMs,
        };
    }

    /// <summary>
    /// 发送文本（自动降级 Markdown + 按上限分段连发）。
    /// 超长**不截断**：静默丢回复后半段是最不能接受的失败模式。
    /// </summary>
    public async Task<WeChatSendOutcome> SendTextAsync(
        string text,
        CancellationToken cancellationToken = default
    )
    {
        var state = Volatile.Read(ref _state);
        var credential = state.Credential;
        if (credential?.IsUsable != true)
            return WeChatSendOutcome.NotBound;

        var plain = WeChatMarkdown.ToPlainText(text);
        if (string.IsNullOrWhiteSpace(plain))
            return WeChatSendOutcome.Sent; // 空内容视为已发送：不值得为空气发一条消息

        foreach (var chunk in WeChatMarkdown.SplitForWeChat(plain))
        {
            try
            {
                await _client.SendTextAsync(
                    credential,
                    credential.PeerUserId,
                    chunk,
                    state.ContextToken,
                    cancellationToken
                );
            }
            catch (WeChatSendRefusedException ex)
                when (ex.ErrCode == WeChatConstants.StaleTokenErrcode)
            {
                // 窗口关闭：已发出的分段算送达，剩余部分由调用方在下次入站时补发
                return WeChatSendOutcome.WindowClosed;
            }
        }

        return WeChatSendOutcome.Sent;
    }

    /// <summary>
    /// 发送/取消 typing 指示器。票据过期或取不到就静默跳过
    /// ——typing 只是装饰，任何失败都不该影响正文发送。
    /// </summary>
    public async Task SendTypingAsync(bool typing, CancellationToken cancellationToken = default)
    {
        var state = Volatile.Read(ref _state);
        var credential = state.Credential;
        if (credential?.IsUsable != true)
            return;

        try
        {
            var ticket = await EnsureTypingTicketAsync(state, cancellationToken);
            if (string.IsNullOrEmpty(ticket))
                return;

            await _client.SendTypingAsync(
                credential,
                credential.PeerUserId,
                ticket,
                typing,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "微信 typing 指示器发送失败（不影响正文）");
        }
    }

    /// <summary>取 typing 票据（缓存 + 超期重取）。</summary>
    private async Task<string?> EnsureTypingTicketAsync(
        WeChatBindingState state,
        CancellationToken cancellationToken
    )
    {
        var fresh =
            state.TypingTicket is { Length: > 0 }
            && state.TypingTicketAt is { } at
            && DateTimeOffset.UtcNow - at
                < TimeSpan.FromMilliseconds(WeChatConstants.TypingTicketRefreshMs);

        if (fresh)
            return state.TypingTicket;

        var config = await _client.GetConfigAsync(
            state.Credential!,
            state.Credential!.PeerUserId,
            state.ContextToken,
            cancellationToken
        );

        if (config.TypingTicket is not { Length: > 0 } ticket)
            return null;

        Persist(state with { TypingTicket = ticket, TypingTicketAt = DateTimeOffset.UtcNow });
        return ticket;
    }

    /// <summary>把协议消息提炼成入站消息；非用户消息（自己的回声、群聊）返回 null。</summary>
    private static WeChatInboundMessage? ToInbound(WeChatMessage raw)
    {
        if (raw.MessageType != WeChatConstants.MessageTypeUser)
            return null;
        if (!string.IsNullOrEmpty(raw.GroupId))
            return null; // v1 只处理单聊

        var texts = new List<string>();
        var unsupported = false;

        foreach (var item in raw.ItemList ?? [])
        {
            switch (item.Type)
            {
                case WeChatConstants.ItemTypeText when item.TextItem?.Text is { Length: > 0 } text:
                    texts.Add(text);
                    break;
                case WeChatConstants.ItemTypeVoice
                    when item.VoiceItem?.Text is { Length: > 0 } transcript:
                    // 服务端已给转写就不必下载音频（手机上说话比打字方便，这条路径很实用）
                    texts.Add(transcript);
                    break;
                case WeChatConstants.ItemTypeImage:
                case WeChatConstants.ItemTypeFile:
                case WeChatConstants.ItemTypeVideo:
                    unsupported = true;
                    break;
            }
        }

        return new WeChatInboundMessage
        {
            MessageId = raw.MessageId ?? string.Empty,
            Text = string.Join("\n", texts),
            HasUnsupportedMedia = unsupported,
        };
    }

    /// <summary>写内存态 + 落盘（只在内容真变化时写，避免每轮长轮询都刷盘）。</summary>
    private void Persist(WeChatBindingState next)
    {
        var current = Volatile.Read(ref _state);
        if (current == next)
            return;

        Volatile.Write(ref _state, next);
        _store.Save(next);
    }
}
