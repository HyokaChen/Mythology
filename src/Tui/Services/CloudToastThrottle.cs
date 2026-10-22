using ClientPlatform;
using Core.Cloud;

namespace Tui.Services;

/// <summary>
/// 云端状态变更 Toast 节流：同一降级原因（CloudConnectionState）在冷却窗口内只提示一次，
/// 避免登录窗口连续重试 / 多次登录 / 心跳与吊销事件并发时重复轰炸同一提示。
/// 覆盖三类提示（共用同一实例）：
/// - 登录后探活降级（ProbeAfterLoginAsync，§13.7）；
/// - 心跳 / 启动校验 401（ProbeCloudAsync）与吊销总线 401（OnCloudCredentialsInvalidated）——同一 Authenticating 键；
/// - 登录失败（CloudLoginDialog「无法连接到云端」）——Unavailable 键。
/// 不同原因（不同状态）不节流；Connected（恢复）重置闸门——恢复后的新降级事件值得重新提示。
/// 线程安全：吊销事件可来自后台 HTTP 线程，内部加锁避免并发竞态。
/// </summary>
internal sealed class CloudToastThrottle(TimeSpan? cooldown = null)
{
    private readonly Lock _gate = new();
    private readonly TimeSpan _cooldown = cooldown ?? CloudTimings.ToastCooldown; // §13.7 节流冷却共享收口（CloudTimings）
    private CloudConnectionState? _lastState;
    private DateTimeOffset _lastAt;

    /// <summary>
    /// 是否应跳过本次 Toast。Connected 会重置闸门并返回 false（成功本身静默，不提示）。
    /// </summary>
    public bool ShouldSuppress(CloudConnectionState state, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (state == CloudConnectionState.Connected)
            {
                _lastState = null; // 恢复后重置：新的降级事件值得重新提示
                return false;
            }

            if (state == _lastState && now - _lastAt < _cooldown)
                return true;

            _lastState = state;
            _lastAt = now;
            return false;
        }
    }
}
