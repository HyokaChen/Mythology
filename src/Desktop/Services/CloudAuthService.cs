using ClientPlatform;
using Core.Cloud;
using Desktop.Models;
using Microsoft.Extensions.Logging;

namespace Desktop.Services;

/// <summary>
/// 云端账号门面 — Desktop 云端登录状态的唯一来源（云端登录设计 §4）：
/// - 持有唯一 CloudSessionStore（DI 单例）与连接状态（CloudAuthState）；
/// - 协议（OAuth 本地回调 / 设备流 / 手动 Key）复用 ClientPlatform 客户端，不重写；
/// - Entrypoint 1：启动自动登录校验（远程 Probe；本地过期只是提示，由服务端裁决，见 §13）；
/// - Entrypoint 2：登录协议收口（LoginWithOAuth / LoginWithDeviceFlow / LoginWithManualKey）；
/// - Entrypoint 3：StateChanged 全局广播，驱动 Header ☁️ / 状态栏 / 设置页同步；
/// - 5 分钟心跳，Key 被吊销时自动清除凭据并降级。
/// </summary>
public class CloudAuthService : IDisposable
{
    private readonly CloudSessionStore _store;
    private readonly HttpClient _probeHttp;
    private readonly HttpMessageHandler? _handler;
    private readonly CloudAuthInvalidationBus _invalidationBus;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    /// <summary>§13 Tier 1 滑动续期：过期或临近过期的凭据在 Probe 成功后续期（服务端无过期字段，30 天仅为客户端 UX 提示）；共享常量 <see cref="CloudTimings.SessionLifetime"/>（Desktop / TUI 共用收口）。</summary>
    private static readonly TimeSpan SessionLifetime = CloudTimings.SessionLifetime;

    /// <summary>续期触发窗口：仅当 ExpiresAt 已过或在此窗口内时才滑动续期，避免心跳每 5 分钟重写凭据文件；共享常量 <see cref="CloudTimings.RenewalSlack"/>（Desktop / TUI 共用收口）。</summary>
    private static readonly TimeSpan RenewalSlack = CloudTimings.RenewalSlack;
    private readonly ILogger<CloudAuthService>? _log;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly CloudCapabilityCatalogStore? _capabilityCatalog;

    /// <summary>探活客户端（C4）：注入 handler（测试）优先；否则工厂池化客户端；最后回退新建。探活走无重试命名客户端 MythologyAuth（幂等单次 GET，不重试）。</summary>
    private HttpClient CreateProbeHttp()
    {
        if (_handler is not null)
            return new HttpClient(_handler) { Timeout = CloudTimings.ProbeTimeout };
        var client = _httpClientFactory?.CreateClient("MythologyAuth") ?? new HttpClient();
        client.Timeout = CloudTimings.ProbeTimeout; // §13.7 探活超时共享收口（Desktop / TUI 共用）
        return client;
    }

    /// <summary>
    /// 构造云端账号门面。
    /// </summary>
    /// <param name="store">凭据存储（缺省 ~/.mythology/cloud-session.json）。</param>
    /// <param name="handler">HTTP 处理器（测试注入 StubHandler；缺省系统 HttpClientHandler）。</param>
    /// <param name="baseUrl">云端地址覆盖（测试用；缺省读 MYTHOLOGY_CLOUD_URL）。</param>
    /// <param name="invalidationBus">§13.6 吊销事件总线（DI 单例注入；缺省新建独立实例，便于测试隔离）。</param>
    /// <param name="logger"></param>
    /// <param name="httpClientFactory">C4 收口：HTTP 工厂（DI 注入；缺省 null 时回退 new HttpClient，测试/独立构造不受影响）。</param>
    /// <param name="capabilityCatalog">能力目录缓存（DI 单例；探活成功写入 last-known-good 快照，供诊断调度消费；null = 不缓存）。</param>
    public CloudAuthService(
        CloudSessionStore? store = null,
        HttpMessageHandler? handler = null,
        string? baseUrl = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger<CloudAuthService>? logger = null,
        IHttpClientFactory? httpClientFactory = null,
        CloudCapabilityCatalogStore? capabilityCatalog = null
    )
    {
        _store = store ?? new CloudSessionStore();
        _handler = handler;
        BaseUrl = baseUrl;
        _invalidationBus = invalidationBus ?? new CloudAuthInvalidationBus();
        _httpClientFactory = httpClientFactory;
        _probeHttp = CreateProbeHttp();
        _log = logger;
        _capabilityCatalog = capabilityCatalog;
        // §13.4 事件驱动失效检测：真实 API 调用（Harness/Retrieval REST）收到 401/403 时立即吊销本地凭据
        _invalidationBus.Invalidated += OnCredentialsInvalidated;
    }

    /// <summary>当前连接状态。</summary>
    public CloudAuthState State { get; private set; } = CloudAuthState.Unknown;

    /// <summary>当前已加载的凭据（null = 未配置）。</summary>
    public CloudSession? Session { get; private set; }

    /// <summary>是否已登录且凭据有效（唯一"完全信任"态）。</summary>
    public bool IsSignedIn => State == CloudAuthState.SignedIn;

    public string? PublicId => Session?.PublicId;

    public string Plan => Session?.Plan ?? "";

    /// <summary>登录用户名（旧服务端未返回时为空，UI 回退 PublicId）。</summary>
    public string DisplayName => Session?.DisplayName ?? "";

    /// <summary>状态变更广播（可能来自后台心跳线程，订阅方需自行调度到 UI 线程）。</summary>
    public event Action<CloudAuthState>? StateChanged;

    // ── 登录流程 UI 状态（CloudLoginWindow / 设置页绑定用） ──

    public string LoginStatus
    {
        get;
        private set
        {
            field = value;
            LoginUiChanged?.Invoke();
        }
    } = "";

    public bool IsDeviceFlowVisible
    {
        get;
        private set
        {
            field = value;
            LoginUiChanged?.Invoke();
        }
    }

    public string DeviceUserCode
    {
        get;
        private set
        {
            field = value;
            LoginUiChanged?.Invoke();
        }
    } = "";

    public string DeviceVerificationUri
    {
        get;
        private set
        {
            field = value;
            LoginUiChanged?.Invoke();
        }
    } = "";

    public string DeviceStatus
    {
        get;
        private set
        {
            field = value;
            LoginUiChanged?.Invoke();
        }
    } = "";

    /// <summary>登录流程 UI 状态变化（OAuth 状态行 / 设备流进度）。</summary>
    public event Action? LoginUiChanged;

    /// <summary>
    /// 云端地址统一收口——委托共享实现 <see cref="ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl"/>
    /// （环境变量 MYTHOLOGY_CLOUD_URL，默认 http://localhost:5080），与 Host/TUI 保持同一默认值。
    /// </summary>
    public static string ResolveCloudBaseUrl() => CloudEndpoints.ResolveCloudBaseUrl();

    /// <summary>实例级云端地址（测试注入优先，其次环境变量/默认）。</summary>
    private string BaseUrl => field ?? ResolveCloudBaseUrl();

    /// <summary>登录协议客户端（OAuth/设备流）：注入 handler 时复用（测试）；否则工厂池化客户端；
    /// 最后回退新建。走无重试命名客户端 MythologyAuth——OAuth/设备码一次性凭证交换非幂等，不重试
    /// （共享常量 <see cref="CloudTimings.LoginHttpTimeout"/>——登录流由用户取消控制生命周期）。</summary>
    private HttpClient CreateLoginHttpClient()
    {
        if (_handler is not null)
            return new HttpClient(_handler) { Timeout = CloudTimings.LoginHttpTimeout };
        var client = _httpClientFactory?.CreateClient("MythologyAuth") ?? new HttpClient();
        client.Timeout = CloudTimings.LoginHttpTimeout;
        return client;
    }

    // ── Entrypoint 1：启动自动登录校验 ──

    /// <summary>
    /// 启动校验（§13 Tier 1：过期是提示而非硬门）：无凭据 → SignedOut；有凭据 → 远程 Probe，
    /// 由服务端裁决有效性——200 且本地过期/临近 → 滑动续期并静默登录；401/403 → 清凭据 Invalid；
    /// 离线/限流/配额 → 保留凭据（不打扰创作）。
    /// 任一非 Invalid 结果都启动心跳（§14 P1 修复）：启动时离线/限流/配额也会每 5 分钟自动重试，
    /// 云端恢复后无需重启即回到 SignedIn（与 TUI 及登录路径 ApplySession 的语义对齐）。
    /// </summary>
    public async Task<CloudAuthState> InitializeAsync(CancellationToken ct)
    {
        try
        {
            Session = _store.Load();
            if (Session is null)
            {
                SetState(CloudAuthState.SignedOut);
                return State;
            }

            SetState(CloudAuthState.Validating);

            var state = await ProbeAsync(ct);
            if (state == CloudAuthState.Invalid)
            {
                // 服务端吊销 → 清除本地凭据（心跳不启动，Invalid 分支自停），等待重新登录
                _store.Clear();
                Session = null;
                SetState(CloudAuthState.Invalid);
                return State;
            }

            // SignedIn / Offline / RateLimited / QuotaExceeded 均启动心跳（§14 P1）：
            // 心跳内部在 Invalid 时自停，其余状态持续重试探活 → 自动恢复
            StartHeartbeat();
            return state;
        }
        catch (OperationCanceledException)
        {
            return State;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "云端账号初始化失败");
            SetState(CloudAuthState.Offline);
            return State;
        }
    }

    /// <summary>
    /// 远程 Probe：GET /v1/capabilities + X-API-Key，映射为连接状态。
    /// 成功时按 §13 Tier 2 续期：优先采用响应携带的服务端 expiresAt，缺失回退滑动续期（Tier 1）。
    /// </summary>
    public async Task<CloudAuthState> ProbeAsync(CancellationToken ct)
    {
        var session = Session;
        if (session is null || string.IsNullOrEmpty(session.ApiKey))
        {
            SetState(CloudAuthState.SignedOut);
            return State;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl.TrimEnd('/')}/v1/capabilities" // §14 P5：实例 BaseUrl（测试注入优先，与登录客户端一致）
            );
            request.Headers.Add("X-API-Key", session.ApiKey);
            using var response = await _probeHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );

            // §14 P6：探活期间会话被登出/清除/替换 → 丢弃结果，避免幽灵复活
            if (!ReferenceEquals(Session, session))
                return State;

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                // §16 审计补漏：守卫必须覆盖最后一次 await——Logout 可能恰在 body 读取期间发生，
                // 早前守卫（SendAsync 后）无法覆盖此窗口，避免 SetState(SignedIn) 复活幽灵状态
                if (!ReferenceEquals(Session, session))
                    return State;
                // 契约版本比对（仅告警不改行为）：服务端能力语义版本 ≠ 客户端基线时留痕，
                // 便于定位「advertised-but-rejected」类问题；未知（旧服务端）容忍。
                var serverContractVersion = CloudContract.TryParseContractVersion(body);
                if (!CloudContract.IsContractVersionSupported(serverContractVersion))
                {
                    _log?.LogWarning(
                        "云端契约版本不一致：客户端支持 {ClientVersion}，服务端为 {ServerVersion}（能力行为可能不同）",
                        CloudContract.SupportedContractVersion,
                        serverContractVersion
                    );
                }
                RenewExpiryIfDue(CloudContract.TryParseExpiresAt(body)); // 服务端真值优先，缺失回退滑动
                // 能力目录缓存（last-known-good）：探活 2xx = 该 Key 的权威视图，供诊断调度消费
                _capabilityCatalog?.Update(CapabilityCatalogSnapshot.Parse(body));
                SetState(CloudAuthState.SignedIn);
                return State;
            }

            var connState = CloudErrorMapper.ToConnectionState(
                CloudErrorMapper.Map(response.StatusCode, "").Code
            );
            switch (connState)
            {
                case CloudConnectionState.Authenticating:
                    SetState(CloudAuthState.Invalid);
                    break;
                case CloudConnectionState.RateLimited:
                    SetState(CloudAuthState.RateLimited);
                    break;
                case CloudConnectionState.QuotaExceeded:
                    SetState(CloudAuthState.QuotaExceeded);
                    break;
                default:
                    SetState(CloudAuthState.Offline);
                    break;
            }
            return State;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 探活失败但会话已变 → 结果属于旧会话，丢弃
            if (ReferenceEquals(Session, session))
                SetState(CloudAuthState.Offline);
            return State;
        }
    }

    /// <summary>
    /// 登录成功后的端到端连通性探活（§13.7）：登录协议（OAuth/设备流）的 token 端点已确认 Key 有效，
    /// 但客户端/心跳实际消费的是 GET /v1/capabilities——登录后立即用真实调用验证整条链路
    /// （Key → X-API-Key 头 → 端点 → 服务端裁决）。
    /// 失败不否定登录（凭据已由 token 端点签发并落盘），仅降级状态 + 提示；心跳继续每 5 分钟重试，
    /// 连通性恢复后自动回到 SignedIn。
    /// </summary>
    private async Task<CloudAuthState> ProbeAfterLoginAsync(CancellationToken ct)
    {
        CloudAuthState probeState;
        try
        {
            probeState = await ProbeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            probeState = CloudAuthState.Offline; // 取消/超时 → 按连通性降级（登录已完成，凭据已落盘）
        }

        LoginStatus = probeState switch
        {
            CloudAuthState.SignedIn => "✅ 登录成功",
            CloudAuthState.Invalid =>
            // 签发后立即被拒（服务端不一致的极端情况）→ 保留既有 Invalid 语义（横幅引导重新登录）
            "✅ 登录成功（凭据校验异常，请点击 ☁️ 重新登录）",
            CloudAuthState.RateLimited => "✅ 登录成功（云端限流，稍后自动重试）",
            CloudAuthState.QuotaExceeded => "✅ 登录成功（云端配额已用尽）",
            _ => "✅ 登录成功（云端暂不可达，稍后自动重试）",
        };
        return probeState;
    }

    // ── Entrypoint 2：登录协议 ──

    /// <summary>OAuth 浏览器授权（PKCE 本地回调）；SSH 环境或失败自动回退设备流。</summary>
    public async Task<bool> LoginWithOAuthAsync(CancellationToken ct)
    {
        if (OAuthLoopbackClient.IsSshSession())
            return await LoginWithDeviceFlowAsync(ct);

        LoginStatus = "🔄 正在启动浏览器授权…";
        try
        {
            var oauthClient = new OAuthLoopbackClient(CreateLoginHttpClient(), BaseUrl);
            var result = await oauthClient.AuthorizeAsync(ct);
            ApplySession(
                new CloudSession(
                    result.ApiKey,
                    result.PublicId,
                    result.WorkspaceId,
                    result.Plan,
                    DateTimeOffset.UtcNow,
                    ResolveExpiry(result.ExpiresAt), // §13 Tier 2：优先服务端真值，缺失回退 +30d
                    result.DisplayName
                )
            );
            LoginStatus = "✅ 登录成功"; // §14 P6：同步置成功态（探活后台执行，降级时覆盖提示）
            _ = ProbeAfterLoginAsync(CancellationToken.None); // §13.7 fire-and-forget：不阻塞登录窗口
            return true;
        }
        catch (OAuthLoopbackException ex)
        {
            LoginStatus = $"⚠️ {ex.Message}，自动切换到设备流…";
            return await LoginWithDeviceFlowAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消 → 不回退（避免取消后意外弹出设备流）
            LoginStatus = "登录已取消";
            return false;
        }
        catch (Exception ex)
        {
            // §14 P3：与 TUI 对齐——任意异常（除用户取消）都回退设备流（无需本地监听，更健壮）
            LoginStatus = $"⚠️ 本地回调失败（{ex.Message}），自动切换到设备流…";
            return await LoginWithDeviceFlowAsync(ct);
        }
    }

    /// <summary>设备授权登录（二维码 + 用户码 + 轮询）。</summary>
    public async Task<bool> LoginWithDeviceFlowAsync(CancellationToken ct)
    {
        try
        {
            IsDeviceFlowVisible = true;
            DeviceUserCode = "";
            DeviceVerificationUri = "";
            DeviceStatus = "⏳ 正在请求设备授权…";

            var deviceClient = new DeviceAuthorizationClient(CreateLoginHttpClient(), BaseUrl);
            var auth = await deviceClient.AuthorizeAsync(Environment.MachineName, ct);

            DeviceUserCode = auth.UserCode;
            DeviceVerificationUri = auth.VerificationUri;
            DeviceStatus = "⏳ 等待扫码确认…";
            OpenBrowser(auth.VerificationUri);

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pollCts.CancelAfter(TimeSpan.FromSeconds(auth.ExpiresInSeconds));
            DeviceToken? token = null;
            try
            {
                while (!pollCts.IsCancellationRequested)
                {
                    token = await deviceClient.PollTokenAsync(auth.DeviceCode, pollCts.Token);
                    if (token is not null)
                        break;
                    await Task.Delay(auth.PollIntervalSeconds * 1000, pollCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 超时
            }

            if (token is not null)
            {
                ApplySession(
                    new CloudSession(
                        token.ApiKey,
                        token.PublicId,
                        token.WorkspaceId,
                        token.Plan,
                        DateTimeOffset.UtcNow,
                        ResolveExpiry(token.ExpiresAt), // §13 Tier 2：优先服务端真值，缺失回退 +30d
                        token.DisplayName
                    )
                );
                DeviceStatus = "✅ 登录成功";
                IsDeviceFlowVisible = false;
                LoginStatus = "✅ 登录成功"; // §14 P6：同步置成功态（探活后台执行，降级时覆盖提示）
                _ = ProbeAfterLoginAsync(CancellationToken.None); // §13.7 fire-and-forget：不阻塞登录窗口
                return true;
            }

            DeviceStatus = "授权超时，请重新发起";
            return false;
        }
        catch (Exception ex)
        {
            DeviceStatus = $"⚠️ 设备流登录失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 手动 API Key 登录（格式校验 + 远程有效性双重校验）；
    /// 云端不可达时允许离线暂存，稍后由心跳自动校验。
    /// </summary>
    public async Task<bool> LoginWithManualKeyAsync(string apiKey, CancellationToken ct)
    {
        var publicId = ApiKeyParser.TryGetPublicId(apiKey);
        if (publicId is null)
        {
            LoginStatus = "API Key 格式无效，应以 mk_live_ 开头";
            return false;
        }

        var session = new CloudSession(apiKey.Trim(), publicId, "", "free", DateTimeOffset.UtcNow);
        Session = session; // 临时挂载供 ProbeAsync 使用
        SetState(CloudAuthState.Validating);

        var probeState = await ProbeAsync(ct);
        if (probeState == CloudAuthState.Invalid)
        {
            Session = null;
            SetState(CloudAuthState.SignedOut);
            LoginStatus = "⚠️ API Key 无效或已吊销";
            return false;
        }

        // SignedIn / Offline / 限流 / 配额 均落盘；用 Session（而非局部 session）——
        // ProbeAsync 成功时可能已按服务端 expiresAt 续期过，落盘必须保留续期结果。
        // 极端并发下事件驱动失效检测可能已清空 Session → 防御性中止登录。
        // §16 审计：捕获局部引用后再判空落盘，关闭「判空 → Save」窗口（避免 Save(null) 写入 "null"）
        var toSave = Session;
        if (toSave is null)
        {
            SetState(CloudAuthState.SignedOut);
            LoginStatus = "⚠️ 登录未完成（凭据已失效）";
            return false;
        }
        _store.Save(toSave);
        SetState(
            probeState == CloudAuthState.SignedIn ? CloudAuthState.SignedIn : CloudAuthState.Offline
        );
        if (probeState == CloudAuthState.SignedIn)
            StartHeartbeat();
        LoginStatus =
            probeState == CloudAuthState.SignedIn
                ? "✅ 登录成功"
                : "✅ 已保存（云端暂不可达，稍后自动校验）";
        return true;
    }

    /// <summary>
    /// §13 Tier 2 续期：
    /// - serverExpiresAt 为未来时间 → 采用服务端真值并落盘（服务端是过期/撤销的唯一权威）；
    /// - 缺失或已是过去时间 → 回退 Tier 1 滑动：仅当本地 ExpiresAt 已过或临近（RenewalSlack 内）时
    ///   续期为 now + SessionLifetime 并落盘。
    /// 手动 Key（ExpiresAt = null）在无服务端真值时永不续期，有效性由服务端 401/403 裁决；
    /// 服务端真值存在时同样采用（服务端说了算）。
    /// </summary>
    private void RenewExpiryIfDue(DateTimeOffset? serverExpiresAt = null)
    {
        if (Session is not { } session)
            return;

        if (serverExpiresAt is { } server && server > DateTimeOffset.UtcNow)
        {
            if (session.ExpiresAt == server)
                return; // 无变化不写盘
            Session = session with { ExpiresAt = server };
            _store.Save(Session);
            return;
        }

        if (session.ExpiresAt is not { } exp)
            return;
        if (exp > DateTimeOffset.UtcNow + RenewalSlack)
            return;
        Session = session with { ExpiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime) };
        _store.Save(Session);
    }

    /// <summary>§13 Tier 2：登录过期时间 — 优先服务端真值，缺失回退客户端滑动默认（30 天 UX 提示）。</summary>
    private static DateTimeOffset ResolveExpiry(DateTimeOffset? serverExpiresAt) =>
        serverExpiresAt ?? DateTimeOffset.UtcNow.Add(SessionLifetime);

    /// <summary>退出登录：清除凭据 + 停止心跳 + 广播。</summary>
    public void Logout()
    {
        StopHeartbeat();
        _store.Clear();
        Session = null;
        IsDeviceFlowVisible = false;
        LoginStatus = "已退出登录";
        SetState(CloudAuthState.SignedOut);
    }

    /// <summary>
    /// §13.4 事件驱动失效检测：真实 API 调用（Harness）收到 401/403 → 凭据已被服务端吊销，
    /// 立即清除并降级（不等 5 分钟心跳）。可能来自后台 HTTP 线程：只改内部状态，
    /// UI 更新由 StateChanged 订阅方自行调度（MainWindowViewModel 用 Dispatcher.UIThread.Post）。
    /// </summary>
    private void OnCredentialsInvalidated(string? context)
    {
        if (Session is null || State == CloudAuthState.SignedOut)
            return; // 无凭据/已登出 → 幂等
        StopHeartbeat();
        _store.Clear();
        Session = null;
        IsDeviceFlowVisible = false;
        LoginStatus = "云端登录已失效，请重新登录";
        SetState(CloudAuthState.Invalid);
    }

    // ── 心跳 ──

    /// <summary>
    /// 心跳间隔（internal 测试缝：测试收窄以验证自动恢复；生产取共享常量
    /// <see cref="CloudTimings.HeartbeatInterval"/>——与 TUI 心跳一致，随 §13.4
    /// 事件驱动失效检测只是兜底）。
    /// </summary>
    internal TimeSpan HeartbeatInterval { get; set; } = CloudTimings.HeartbeatInterval;

    /// <summary>每 5 分钟 Probe 一次；Key 被吊销时清除凭据并停止。</summary>
    public void StartHeartbeat()
    {
        StopHeartbeat();
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTask = HeartbeatLoopAsync(_heartbeatCts.Token);
    }

    public void StopHeartbeat()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
        _heartbeatTask = null;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);
                var state = await ProbeAsync(ct);
                if (state == CloudAuthState.Invalid)
                {
                    // 凭据被吊销 → 清除并停止心跳
                    _store.Clear();
                    Session = null;
                    StopHeartbeat();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "云端心跳失败");
            }
        }
    }

    public void Dispose()
    {
        _invalidationBus.Invalidated -= OnCredentialsInvalidated;
        StopHeartbeat();
        GC.SuppressFinalize(this);
    }

    // ── 内部 ──

    private void ApplySession(CloudSession session)
    {
        Session = session;
        _store.Save(session);
        SetState(CloudAuthState.SignedIn);
        StartHeartbeat();
    }

    private void SetState(CloudAuthState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>跨平台打开浏览器。失败静默。virtual 供测试子类覆写为 no-op。</summary>
    protected virtual void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
                );
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", url);
            }
            else
            {
                System.Diagnostics.Process.Start("xdg-open", url);
            }
        }
        catch
        {
            // 无默认浏览器/受限环境 → 忽略
        }
    }
}
