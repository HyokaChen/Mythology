// 功能：通道相位与对外快照 —— 宿主（TUI / Desktop）的状态唯一数据源
//
// 与 LKK 的 WeChatChannelSnapshot 对应，两处刻意不同：
// ① 二维码给的是**内容字符串**（实测是 liteapp.weixin.qq.com 链接），不是 base64 PNG：
//    画成图片还是画成半块字符是宿主的事，共享层不该替它决定；
// ② 相位用 enum 而不是字符串：字符串相位写错只是静默不匹配，enum 是编译错误。

namespace Channels.WeChat;

/// <summary>通道相位。</summary>
public enum WeChatPhase
{
    /// <summary>未绑定。</summary>
    Idle,

    /// <summary>启动时正在恢复已落盘的绑定。</summary>
    Restoring,

    /// <summary>已出二维码，等待扫码。</summary>
    WaitingScan,

    /// <summary>已扫码，等待手机上的确认。</summary>
    Scanned,

    /// <summary>服务端要求换主机 / 正在保存绑定。</summary>
    Connecting,

    /// <summary>已绑定，长轮询中。</summary>
    Polling,

    /// <summary>失败（Error 非空）。</summary>
    Failed,
}

/// <summary>通道对外可见的状态快照。</summary>
public sealed record WeChatChannelSnapshot
{
    /// <summary>当前相位。</summary>
    public WeChatPhase Phase { get; init; } = WeChatPhase.Idle;

    /// <summary>待渲染的二维码内容（通常是 liteapp.weixin.qq.com 链接）；无码时为空。</summary>
    public string? QrContent { get; init; }

    /// <summary>已绑定的 bot 身份（ilink_bot_id，形如 xxxx@im.bot）。</summary>
    public string? AccountId { get; init; }

    /// <summary>扫码绑定的微信用户。</summary>
    public string? PeerUserId { get; init; }

    /// <summary>失败原因（Phase = Failed 时非空）。</summary>
    public string? Error { get; init; }

    /// <summary>本次登录会话内二维码刷新次数。</summary>
    public int Refreshed { get; init; }

    /// <summary>
    /// 是否已绑定（长轮询中或已确认；失败相位不算）。
    /// 已知边界：已绑定状态下重新扫码失败，会显示成"未绑定 + 错误文本"，但磁盘上的旧凭证仍有效
    /// （重启后照常恢复轮询）。这是有意对齐 LKK 的语义——失败优先告知，重启自会回到 Polling。
    /// </summary>
    public bool IsBound =>
        Phase is WeChatPhase.Polling or WeChatPhase.Connecting
        || (!string.IsNullOrEmpty(AccountId) && Phase != WeChatPhase.Failed);

    /// <summary>二维码是否应展示。</summary>
    public bool IsQrVisible =>
        Phase is WeChatPhase.WaitingScan or WeChatPhase.Scanned && !string.IsNullOrEmpty(QrContent);

    /// <summary>握手/恢复进行中（绑定按钮应禁用）。</summary>
    public bool IsBusy =>
        Phase
            is WeChatPhase.WaitingScan
                or WeChatPhase.Scanned
                or WeChatPhase.Connecting
                or WeChatPhase.Restoring;
}

/// <summary>
/// 相位状态的载体。所有变更集中在 Apply，保证每次变更都触发一次 Changed
/// （宿主据此刷新卡片/对话框，不需要轮询）。
/// </summary>
public sealed class WeChatChannelStatus
{
    /// <summary>状态变化通知（可能来自任意线程，宿主自行调度到 UI 线程）。</summary>
    public event Action? Changed;

    /// <summary>当前快照。</summary>
    public WeChatChannelSnapshot Snapshot { get; private set; } = new();

    /// <summary>展示二维码（并在需要时递增刷新计数）。保持已有的 AccountId/PeerUserId：
    /// 已绑定状态下重新扫码时，卡片要同时显示"已绑定"和"新二维码"（对齐 LKK 的 IsBound 语义）。</summary>
    public void ShowQr(string qrContent, int refreshed) =>
        Apply(
            Snapshot with
            {
                Phase = WeChatPhase.WaitingScan,
                QrContent = qrContent,
                Refreshed = refreshed,
                Error = null,
            }
        );

    /// <summary>已扫码，等待手机确认。</summary>
    public void MarkScanned() => Apply(Snapshot with { Phase = WeChatPhase.Scanned, Error = null });

    /// <summary>切换连接主机 / 正在保存绑定。</summary>
    public void MarkConnecting() =>
        Apply(Snapshot with { Phase = WeChatPhase.Connecting, Error = null });

    /// <summary>启动时恢复已有绑定。</summary>
    public void MarkRestoring() =>
        Apply(Snapshot with { Phase = WeChatPhase.Restoring, Error = null });

    /// <summary>绑定成功 / 恢复完成，进入长轮询。</summary>
    public void MarkPolling(WeChatCredential? credential) =>
        Apply(
            Snapshot with
            {
                Phase = WeChatPhase.Polling,
                QrContent = null,
                AccountId = credential?.AccountId ?? Snapshot.AccountId,
                PeerUserId = credential?.PeerUserId ?? Snapshot.PeerUserId,
                Error = null,
            }
        );

    /// <summary>未绑定。</summary>
    public void MarkIdle() => Apply(new WeChatChannelSnapshot());

    /// <summary>失败。Error 必须非空——空错误文本会让宿主显示"异常："。</summary>
    /// <summary>失败。Error 必须非空——空错误文本会让宿主显示"异常："。</summary>
    /// <param name="credential">失败时仍应展示的绑定身份（例如"已被另一个入口占用"——
    /// 用户需要看到"绑的是哪个号"，才知道该去关哪一个）。</param>
    public void MarkFailed(string error, WeChatCredential? credential = null) =>
        Apply(
            Snapshot with
            {
                Phase = WeChatPhase.Failed,
                QrContent = null,
                Error = error,
                AccountId = credential?.AccountId ?? Snapshot.AccountId,
                PeerUserId = credential?.PeerUserId ?? Snapshot.PeerUserId,
            }
        );

    private void Apply(WeChatChannelSnapshot next)
    {
        Snapshot = next;
        Changed?.Invoke();
    }
}
