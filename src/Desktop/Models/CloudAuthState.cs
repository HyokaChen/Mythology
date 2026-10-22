namespace Desktop.Models;

/// <summary>
/// 云端账号登录状态 — 替代 bool IsCloudLoggedIn 的表达力（docs/DESKTOP-LOGIN-FLOW.md §3）：
/// 区分 未登录 / 校验中 / 已登录 / 无效 / 离线 / 限流 / 配额。
/// 任何状态都不阻断本地创作（云端是可选项）。
/// 注意：无独立的 Expired 状态——§13 Tier 1 后本地过期只是提示，
/// 有效性由服务端 Probe 裁决（200 → 续期 SignedIn；401/403 → Invalid）。
/// </summary>
public enum CloudAuthState
{
    /// <summary>启动中，尚未检查本地凭据。</summary>
    Unknown,

    /// <summary>无本地凭据（新用户或已登出）。</summary>
    SignedOut,

    /// <summary>本地有凭据，正在远程校验。</summary>
    Validating,

    /// <summary>凭据有效（远程 Probe 200）。</summary>
    SignedIn,

    /// <summary>服务端拒绝（401/403，Key 已吊销）。</summary>
    Invalid,

    /// <summary>无法连接服务端，凭据暂保留。</summary>
    Offline,

    /// <summary>云端限流（429）。</summary>
    RateLimited,

    /// <summary>云端配额不足（402）。</summary>
    QuotaExceeded,
}
