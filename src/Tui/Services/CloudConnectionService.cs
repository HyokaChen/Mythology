using ClientPlatform;
using Core.Cloud;
using Microsoft.Extensions.Logging;

namespace Tui.Services;

/// <summary>
/// D5 统一：TUI 云端连接门面（DI 单例）— 持有唯一凭据存储（ClientPlatform.CloudSessionStore）
/// 与连接状态（ConnectionState / IsConfigured），订阅 §13.6 吊销事件总线（与 Host 注册的
/// 云端客户端共享同一容器实例）。
/// 协议（设备流 DeviceAuthorizationClient / 本地回调 OAuthLoopbackClient）在 ClientPlatform，
/// 存储归 ClientPlatform.CloudSessionStore，本类只负责 TUI 侧的连接状态编排；
/// 心跳与 UI 提示（Toast）由 TuiApp 编排，经 <see cref="CredentialsInvalidated"/> 事件衔接。
/// </summary>
public sealed class CloudConnectionService : IDisposable
{
    private readonly ILogger<CloudConnectionService>? _logger;
    private readonly CloudSessionStore _store;
    private readonly CloudAuthInvalidationBus? _invalidationBus;
    private readonly HttpMessageHandler? _handler;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly CloudCapabilityCatalogStore? _capabilityCatalog;

    public CloudConnectionService(
        CloudSessionStore? store = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        HttpMessageHandler? handler = null,
        ILogger<CloudConnectionService>? logger = null,
        IHttpClientFactory? httpClientFactory = null,
        CloudCapabilityCatalogStore? capabilityCatalog = null
    )
    {
        _store = store ?? new CloudSessionStore();
        _invalidationBus = invalidationBus; // §13.6 DI 单例注入；null（测试/未接线）时跳过订阅
        _handler = handler; // §13.7 探活 HTTP 注入（测试用 Stub）；null = 工厂/默认客户端
        _httpClientFactory = httpClientFactory; // C4 收口：DI 注入工厂，handler 缺省时使用池化客户端
        _logger = logger;
        _capabilityCatalog = capabilityCatalog; // 探活成功后写入能力目录（DI 单例；null = 不缓存）
        if (_invalidationBus is not null)
            _invalidationBus.Invalidated += OnCredentialsInvalidated;
    }

    /// <summary>吊销处理事件（凭据已被服务端吊销并清除后触发；TuiApp 订阅以停心跳 + Toast）。</summary>
    public event Action? CredentialsInvalidated;

    /// <summary>当前已加载的凭据（null = 未配置）。</summary>
    public CloudSession? Current { get; private set; }

    /// <summary>当前云端连接状态。</summary>
    public CloudConnectionState ConnectionState { get; private set; } =
        CloudConnectionState.Disabled;

    /// <summary>
    /// §13 Tier 1：是否已配置云端（有 API Key）。
    /// 本地过期不再短路——过期只是提示，由 Probe 后 RenewIfDue 滑动续期，服务端 401/403 才裁决失效。
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    /// <summary>当前 API Key。</summary>
    public string? ApiKey => Current?.ApiKey;

    /// <summary>API Key 的 PublicId。</summary>
    public string? PublicId => Current?.PublicId;

    /// <summary>登录用户名（旧服务端未返回时为空，UI 回退 PublicId）。</summary>
    public string DisplayName => Current?.DisplayName ?? "";

    /// <summary>API Key 过期时间（客户端 UX 提示，非裁决依据）。</summary>
    public DateTimeOffset? ExpiresAt => Current?.ExpiresAt;

    /// <summary>§13 Tier 1 滑动续期窗口（同 Desktop：过期或临近才续期，避免心跳每 5 分钟重写凭据文件）；共享常量 <see cref="CloudTimings.SessionLifetime"/>（Desktop / TUI 共用收口）。</summary>
    private static readonly TimeSpan SessionLifetime = CloudTimings.SessionLifetime;

    /// <summary>续期触发 slack（同 Desktop）：仅当 ExpiresAt 已过或临近（1 天内）才续期；共享常量 <see cref="CloudTimings.RenewalSlack"/>（Desktop / TUI 共用收口）。</summary>
    private static readonly TimeSpan RenewalSlack = CloudTimings.RenewalSlack;

    /// <summary>
    /// §13 Tier 2 续期（与 Desktop 一致）：
    /// - serverExpiresAt 为未来时间 → 采用服务端真值并落盘（服务端是过期/撤销的唯一权威）；
    /// - 缺失或已是过去时间 → 回退滑动：仅当本地 ExpiresAt 已过或临近（RenewalSlack 内）时
    ///   续期为 now + 30d 并落盘。
    /// 手动 Key（ExpiresAt = null）在无服务端真值时永不续期，有效性由服务端 401/403 裁决。
    /// </summary>
    public void RenewIfDue(DateTimeOffset? serverExpiresAt = null)
    {
        if (Current is not { } s)
            return;

        if (serverExpiresAt is { } server && server > DateTimeOffset.UtcNow)
        {
            if (s.ExpiresAt == server)
                return; // 无变化不写盘
            Current = s with { ExpiresAt = server };
            _store.Save(Current);
            return;
        }

        if (s.ExpiresAt is not { } exp)
            return;
        if (exp > DateTimeOffset.UtcNow + RenewalSlack)
            return;
        Current = s with { ExpiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime) };
        _store.Save(Current);
    }

    /// <summary>更新连接状态。</summary>
    public void SetConnectionState(CloudConnectionState state) => ConnectionState = state;

    /// <summary>
    /// §13.7 端到端连通性探活：GET /v1/capabilities + X-API-Key，验证 Key → 头 → 端点 → 服务端裁决整条链路。
    /// 成功 → Connected + RenewIfDue（服务端真值优先，缺失回退滑动）；401/403 → Authenticating（吊销语义，
    /// 由调用方决定是否清除）；限流/配额/不可达 → 对应状态。探活本身不抛异常（失败映射为 Unavailable），
    /// 供启动校验 / 心跳 / 登录后探活（§13.7）共用——与 Desktop CloudAuthService.ProbeAsync 对称。
    /// </summary>
    public async Task<CloudConnectionState> ProbeAsync(CancellationToken ct = default)
    {
        var current = Current;
        var apiKey = current?.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            ConnectionState = CloudConnectionState.Authenticating;
            return ConnectionState;
        }

        var (state, serverExpiresAt, catalog) = await ProbeCoreAsync(apiKey, ct);

        // 探活期间凭据被登出/清除/替换 → 丢弃结果，避免幽灵复活（与 Desktop §14 P6 对齐）
        if (!ReferenceEquals(Current, current))
            return ConnectionState;

        if (state == CloudConnectionState.Connected)
        {
            // 能力目录缓存（last-known-good）：只在「当前会话凭据」的探活成功后写入；
            // ValidateKeyAsync 的纯校验路径不写（避免别的账号/未来按套餐过滤的视图污染缓存）。
            _capabilityCatalog?.Update(catalog);
            RenewIfDue(serverExpiresAt); // §13 Tier 2：服务端真值优先，缺失回退滑动续期
        }
        ConnectionState = state;
        return state;
    }

    /// <summary>
    /// 手动 Key 登录前远程双重校验（§14 P2，与 Desktop LoginWithManualKeyAsync 对齐）：
    /// GET /v1/capabilities 验证指定 Key 有效性，**不触碰 Current/store/ConnectionState**（纯校验）。
    /// Connected=有效；Authenticating=无效或已吊销；其余=云端不可达/限流/配额（调用方决定是否离线暂存）。
    /// </summary>
    public async Task<CloudConnectionState> ValidateKeyAsync(
        string apiKey,
        CancellationToken ct = default
    )
    {
        var (state, _, _) = await ProbeCoreAsync(apiKey, ct);
        return state;
    }

    /// <summary>capabilities 探活核心（启动/心跳/手动 Key 校验共用）。不抛异常。</summary>
    private async Task<(
        CloudConnectionState State,
        DateTimeOffset? ServerExpiresAt,
        CapabilityCatalogSnapshot? Catalog
    )> ProbeCoreAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            using var http = CreateProbeHttp();
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{CloudEndpoints.ResolveCloudBaseUrl().TrimEnd('/')}/v1/capabilities"
            );
            request.Headers.Add("X-API-Key", apiKey);
            using var response = await http.SendAsync(request, ct);

            // §13 Tier 2：成功响应可携带可选服务端 expiresAt（缺失回退滑动续期）
            DateTimeOffset? serverExpiresAt = null;
            CapabilityCatalogSnapshot? catalog = null;
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                serverExpiresAt = CloudContract.TryParseExpiresAt(body);
                // 能力目录快照（分类/阶段/范围/深度/图后端）：由调用方决定是否缓存
                // —— 手动 Key 校验（ValidateKeyAsync）属纯校验路径，不回写共享缓存。
                catalog = CapabilityCatalogSnapshot.Parse(body);
                // 契约版本比对（仅告警不改行为，与 Desktop ProbeAsync 对称）：
                // 服务端能力语义版本 ≠ 客户端基线时留痕；未知（旧服务端）容忍。
                var serverContractVersion = CloudContract.TryParseContractVersion(body);
                if (!CloudContract.IsContractVersionSupported(serverContractVersion))
                {
                    _logger?.LogWarning(
                        "云端契约版本不一致：客户端支持 {ClientVersion}，服务端为 {ServerVersion}（能力行为可能不同）",
                        CloudContract.SupportedContractVersion,
                        serverContractVersion
                    );
                }
            }

            var state = response.IsSuccessStatusCode
                ? CloudConnectionState.Connected
                : CloudErrorMapper.ToConnectionState(
                    CloudErrorMapper.Map(response.StatusCode, "").Code
                );
            return (state, serverExpiresAt, catalog);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
                TuiLogger.OperationFailed(_logger, ex, "probe cloud");
            return (CloudConnectionState.Unavailable, null, null);
        }
    }

    /// <summary>探活客户端（C4）：注入 handler（测试）优先；否则工厂池化客户端；最后回退新建。探活走无重试命名客户端 MythologyAuth（幂等单次 GET，不重试）。</summary>
    private HttpClient CreateProbeHttp()
    {
        if (_handler is not null)
            return new HttpClient(_handler) { Timeout = CloudTimings.ProbeTimeout };
        var client = _httpClientFactory?.CreateClient("MythologyAuth") ?? new HttpClient();
        client.Timeout = CloudTimings.ProbeTimeout; // §13.7 探活超时共享收口（Desktop / TUI 共用）
        return client;
    }

    /// <summary>从磁盘加载凭据并初始化连接状态。</summary>
    public void Load()
    {
        Current = _store.Load();
        ConnectionState = IsConfigured
            ? CloudConnectionState.Authenticating
            : CloudConnectionState.Disabled;
    }

    /// <summary>保存凭据并标记为已连接。</summary>
    public void Save(CloudSession session)
    {
        Current = session;
        ConnectionState = CloudConnectionState.Connected;
        _store.Save(session);
    }

    /// <summary>清除凭据（登出）。</summary>
    public void Clear()
    {
        Current = null;
        ConnectionState = CloudConnectionState.Disabled;
        _store.Clear();
    }

    /// <summary>
    /// §13.6 事件驱动失效检测：云端客户端（Harness/Retrieval REST）真实调用收到 401/403 →
    /// 凭据已被服务端吊销：立即清除并降级（不等心跳），再通知 TuiApp 停心跳 + Toast。
    /// 可能来自后台 HTTP 线程；只改内部状态，UI 由 TuiApp 经 CredentialsInvalidated 自行调度。
    /// </summary>
    private void OnCredentialsInvalidated(string? context)
    {
        if (!IsConfigured)
            return; // 无凭据/已清除 → 幂等
        // 原始 401 详情写入日志（请求路径 + 本地最后记录的到期时间），TUI 只做 Toast 提醒
        _logger?.LogWarning(
            "云端凭据被服务端拒绝（401/403），已清除本地凭据。调用: {Context}；本地最后记录的过期时间: {ExpiresAt:o}",
            context ?? "(unknown)",
            Current?.ExpiresAt
        );
        Clear();
        CredentialsInvalidated?.Invoke();
    }

    public void Dispose()
    {
        if (_invalidationBus is not null)
            _invalidationBus.Invalidated -= OnCredentialsInvalidated;
    }
}
