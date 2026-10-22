namespace ClientPlatform;

/// <summary>
/// 云端时序唯一收口（Desktop / TUI / 共享客户端单点共用）——全部云端时序此前在两端
/// 与各客户端内各自内联，收口为单点可配常量，避免默认值再次分叉
/// （与 <see cref="CloudEndpoints"/> 的地址收口同一模式）。
/// 调优任意时序只需改此处一处，两端与所有客户端自动生效。
///
/// | 常量 | 值 | 语义 | 引用点 |
/// |------|-----|------|--------|
/// | <see cref="ProbeTimeout"/> | 10s | 探活/校验 HTTP 超时（§13.7 登录后探活、心跳 Probe、手动 Key 远程校验）——超过视为云端不可达 | Desktop `CloudAuthService` 探活 HttpClient · TUI `CloudConnectionService.ProbeCoreAsync` |
/// | <see cref="HeartbeatInterval"/> | 5min | 心跳间隔（§9/§13.4）：GET /v1/capabilities 刷新连接状态，事件驱动吊销（401/403 立即）之外的兜底 | Desktop `CloudAuthService.HeartbeatInterval` 默认值 · TUI `TuiApp.HeartbeatLoopAsync` |
/// | <see cref="SessionLifetime"/> | 30d | 滑动续期默认有效期（§13 Tier 1/2）：服务端未携带 expiresAt 时续期为 now+30d；仅 UX 提示，有效性由 Probe 裁决 | Desktop/TUI `SessionLifetime` 字段 · TUI `CloudLoginDialog.CreateServerSession` 缺失回退 |
/// | <see cref="RenewalSlack"/> | 1d | 续期触发 slack（§13 Tier 1）：过期或临近 1 天内才续期落盘，避免心跳每 5 分钟重写凭据文件 | Desktop/TUI `RenewalSlack` 字段 |
/// | <see cref="ToastCooldown"/> | 60s | Toast 节流冷却（§13.7）：同一降级原因冷却窗口内只提示一次 | TUI `CloudToastThrottle` 默认冷却 |
/// | <see cref="LoginHttpTimeout"/> | 无限 | 登录协议 HTTP 超时（OAuth/设备流 token 交换）：浏览器授权可长时挂起，探活超时不适用；由用户取消（CancellationToken）控制 | Desktop `CloudAuthService.CreateLoginHttpClient` · TUI `CloudLoginDialog` OAuth + 设备流客户端 |
/// | <see cref="OAuthCallbackTimeout"/> | 120s | OAuth 本地回调等待窗口：浏览器授权后回调超时视为放弃，调用方回退设备流。**与服务端 `OAuthSupport.CodeTtlSeconds` 对齐（同为 120s）**——客户端在 code 过期前即放弃 | `OAuthLoopbackClient.CallbackTimeout` |
/// | <see cref="CloudApiTimeout"/> | 60s | 非流式云端 API 单请求超时（按请求链接 CTS，覆盖到响应体读完） | `CloudRestTransport` 对非流式数据面请求统一施加；流式 SSE 不走它（传 `requestTimeout: false`，见 <see cref="StreamingClientTimeout"/>） |
/// | <see cref="StreamingClientTimeout"/> | 无限 | 流式 SSE 客户端超时：SSE 长连接不能被 HttpClient.Timeout 掐断，也不施加 <see cref="CloudApiTimeout"/> 安全网（会掐断长连接）；由调用方截止 CTS 兜底（Harness `WaitForResultAsync` 2 分钟等待截止 · 设备流登录服务端 `ExpiresInSeconds` 窗口） | `CloudRestTransport(streamingClient: true)` · `DeviceAuthorizationClient.ConfigureTimeout` |
/// | <see cref="SseAttemptTimeout"/> | 45s | Harness 结果流单次尝试上限：到点弃流回退轮询（轮询承接剩余 2 分钟预算），防连接挂死吃满整体预算 | `HttpFictionHarnessClient.TryReadSseResultAsync` |
/// </summary>
public static class CloudTimings
{
    /// <summary>
    /// 探活/校验 HTTP 超时（§13.7 登录后探活、心跳 Probe、手动 Key 远程校验共用）：
    /// capabilities 请求超过此时间视为云端不可达。
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 云端心跳间隔（§9 / §13.4）：每 N 分钟 GET /v1/capabilities 刷新连接状态，
    /// 作为事件驱动失效检测（真实 API 调用 401/403 立即吊销）之外的兜底。
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 滑动续期默认有效期（§13 Tier 1/2）：服务端未携带 expiresAt 时，凭据续期为 now + 30 天。
    /// 仅为客户端 UX 提示——过期不再硬门，有效性由服务端 Probe 裁决（200 → 续期；401/403 → 吊销）。
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// 续期触发 slack（§13 Tier 1）：仅当本地 ExpiresAt 已过或临近（1 天内）时才续期落盘，
    /// 避免心跳每 5 分钟重写凭据文件。
    /// </summary>
    public static readonly TimeSpan RenewalSlack = TimeSpan.FromDays(1);

    /// <summary>
    /// 云端状态变更 Toast 节流冷却窗口（§13.7）：同一降级原因在 60s 内只提示一次，
    /// 避免登录窗口连续重试 / 多次登录 / 心跳与吊销事件并发时重复轰炸同一提示。
    /// </summary>
    public static readonly TimeSpan ToastCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 登录协议 HTTP 超时（OAuth 本地回调 / 设备流 token 交换）：无限——浏览器授权/用户确认可能
    /// 长时间挂起，探活超时（<see cref="ProbeTimeout"/>）不适用于登录流；生命周期由用户取消
    /// （CancellationToken）控制。Desktop 登录客户端与 TUI CloudLoginDialog 共用。
    /// </summary>
    public static readonly TimeSpan LoginHttpTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// OAuth 本地回调等待窗口（OAuthLoopbackClient）：浏览器授权后回调必须在 120s 内收回，
    /// 超时视为用户放弃/浏览器失败，调用方回退设备流。
    /// 与服务端授权码 TTL 对齐（同为 120s）：
    /// 客户端放弃时刻不晚于任何已铸造 code 的过期时刻，两侧共享同一窗口语义。
    /// </summary>
    public static readonly TimeSpan OAuthCallbackTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 非流式云端 API 单请求默认超时：由 <c>CloudRestTransport</c> 对**所有**非流式数据面请求
    /// 统一施加（按请求链接 CTS 安全网，覆盖到响应体读完）；流式 SSE 端点不走它
    /// （传 <c>requestTimeout: false</c>，由调用方截止 CTS 兜底，见 <see cref="StreamingClientTimeout"/>）。
    /// 调用方（工具载荷）自带的 timeout 必须小于本上限才有意义。
    /// </summary>
    public static readonly TimeSpan CloudApiTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// SSE 单次尝试上限（Harness 结果流）：服务端对单次执行有硬超时（默认 30s），但排队时间不设限；
    /// 连接挂死 / 半开时不能把 2 分钟整体等待预算全耗在一个死流上——到点弃流回退轮询
    /// （轮询承接剩余预算，语义等价）。
    /// </summary>
    public static readonly TimeSpan SseAttemptTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 流式 SSE 客户端超时（Harness / DeviceAuthorization）：客户端 Timeout 必须无限——
    /// SSE 长连接不能被 HttpClient.Timeout 掐断，也不能施加按请求的 <see cref="CloudApiTimeout"/>
    /// 安全网（会把长连接掐断）；生命周期由调用方截止 CTS 兜底：Harness 为
    /// <c>WaitForResultAsync</c> 的 2 分钟等待截止，设备流登录为服务端 <c>ExpiresInSeconds</c> 窗口。
    /// Harness 侧由 <c>CloudRestTransport(streamingClient: true)</c> 施加（注入实例仅此模式改 Timeout）。
    /// </summary>
    public static readonly TimeSpan StreamingClientTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// 启动期检索索引同步预算：VectorSyncService.SyncAsync 会对变更实体逐块调 embedding——
    /// 远端不可达时每个调用都可能等到提供商超时。启动等待不能无限；超时 → 警告 + 转后台
    /// 增量（kernel.StartWorker）补齐。
    /// </summary>
    public static readonly TimeSpan StartupIndexSyncTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 启动引导前置连通性预检预算：新用户自动弹登录前快速探测（TuiApp.RunStartupOnboardingAsync）。
    /// 云端不可达时不弹挂起登录框，直接 Toast + 进入项目选择——启动不能被登录握手拖住。
    /// </summary>
    public static readonly TimeSpan StartupLoginPrecheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 登录协议「连通性」请求预算（OAuth 预检 GET /v1/oauth/authorize、设备流 POST /v1/device/authorize）：
    /// 与 <see cref="LoginHttpTimeout"/>（无限，供浏览器授权长挂起）不同——这两步是纯 HTTP 握手，
    /// 远端不可达时必须快速失败并 Toast/回退，不能把登录对话框挂起。交互期（浏览器授权等待、
    /// SSE 轮询）仍由各自窗口/用户取消控制。
    /// </summary>
    public static readonly TimeSpan LoginConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 辅助 LLM 流式调用超时（LlmExtractionBridge / ExploreTool / EnhancementEngine / TuiApp.Session）：
    /// 这些嵌套调用无 HttpClient.Timeout 覆盖（SSE body 读取阶段不生效），provider 停流会永久阻塞工具。
    /// 超时后降级返回（提取→空结果、变体→失败、增强→空列表、摘要→失败文案），不重抛（用户取消才重抛）。
    /// </summary>
    public static readonly TimeSpan AuxiliaryLlmTimeout = TimeSpan.FromSeconds(60);
}
