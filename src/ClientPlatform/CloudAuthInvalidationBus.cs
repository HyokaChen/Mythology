using System.Net;

namespace ClientPlatform;

/// <summary>
/// §13.4 事件驱动失效检测 — 云端凭据被服务端吊销（401/403）时的事件总线（DI 单例注入）。
///
/// 动机：心跳（每 5 分钟 Probe）是吊销检测的下限；真实 API 调用（如 HttpFictionHarnessClient
/// 的 /v1/agent/runs、RetrievalRestClient 的 /v1/retrieval/scrape）收到 401/403 时，应立即通知
/// 认证门面清除凭据并降级 UI，而非等下一轮心跳。
///
/// 生命周期：以 DI 单例注册（Host 注册云端客户端处 / Desktop 注册认证门面处），由宿主容器注入——
/// 同一进程内，云端客户端（Raise 方）与认证门面（订阅方）共享同一实例。进程间不共享
/// （TUI/Host 与 Desktop 是独立进程，各自持有实例，凭据文件 ~/.mythology/cloud-session.json 是唯一
/// 跨进程共识）。无静态可变状态，便于测试隔离（每个测试注入独立实例）。
///
/// 订阅方约束：事件可能来自后台 HTTP 线程，处理器只应修改内部状态并通过既有 StateChanged/
/// 状态栏机制广播；UI 更新由订阅方自行调度（Desktop 用 Dispatcher.UIThread.Post）。
/// </summary>
public sealed class CloudAuthInvalidationBus
{
    /// <summary>凭据吊销事件。参数为失败调用的路径（可为 null，仅诊断用）。</summary>
    public event Action<string?>? Invalidated;

    /// <summary>触发吊销事件（云端客户端收到 401/403 时调用）。</summary>
    public void Raise(string? context = null) => Invalidated?.Invoke(context);

    /// <summary>
    /// 统一吊销判定 — 供所有云端凭据消费者复用（Harness / Retrieval REST / 未来的 CloudEmbeddingClient）：
    /// 响应为 401/403 → 触发吊销事件并返回 true；其余状态返回 false（凭据仍有效，调用方按自身错误处理）。
    /// </summary>
    public bool IsCredentialInvalid(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            return false;
        Raise(response.RequestMessage?.RequestUri?.AbsolutePath);
        return true;
    }
}
