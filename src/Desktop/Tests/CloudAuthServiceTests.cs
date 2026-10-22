using System.Net;
using System.Text;
using ClientPlatform;
using Desktop.Models;
using Desktop.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// CloudAuthService 单元测试（docs/DESKTOP-LOGIN-FLOW.md §7 校验逻辑落地验证）：
/// - 启动校验：无凭据 / 本地过期（§13 提示而非硬门：200 续期 / 401 清除 / 离线保留）/ 远程 200 / 401 / 403 / 429 / 402 / 网络异常 / 503；
/// - 手动 Key：格式校验 + 远程有效性双重校验 + 离线暂存；
/// - 设备流：批准成功 / 轮询超时 / 授权失败；
/// - §13.7 登录后探活：capabilities 200 → 静默成功；503 → 降级 Offline + 提示（登录不否定）；401 → Invalid（横幅引导重登）；
/// - OAuth：预检失败自动回退设备流；SSH 环境直连设备流；
/// - 登出 / StateChanged 广播 / X-API-Key 请求头。
/// 所有登录成功路径都会启动 5 分钟心跳，测试结束时 Dispose 停止。
/// </summary>
public sealed class CloudAuthServiceTests
{
    private const string BaseUrl = "http://cloud.test";

    private const string ValidKey = "mk_live_dev123_secret";
    private const string PublicId = "dev123";

    // ═══════════════ InitializeAsync：启动自动登录校验 ═══════════════

    [Fact]
    public async Task Initialize_NoSession_SetsSignedOut()
    {
        using var store = NewStore();
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.SignedOut, state);
        Assert.Equal(CloudAuthState.SignedOut, svc.State);
        Assert.Null(svc.Session);
        Assert.False(svc.IsSignedIn);
    }

    [Fact]
    public async Task Initialize_NoSession_DoesNotHitNetwork()
    {
        using var store = NewStore();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = NewService(store, handler);

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(0, handler.CallCount); // 无凭据 → 零网络请求
    }

    [Fact]
    public async Task Initialize_ExpiredSession_Probe200_RenewsAndSignsIn()
    {
        using var store = NewStore();
        store.Store.Save(ExpiredSession());
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        // §13 Tier 1：本地过期只是提示 — 服务端仍有效 → 静默登录 + 滑动续期，不再自造强制登出
        Assert.Equal(CloudAuthState.SignedIn, state);
        Assert.True(svc.IsSignedIn);
        Assert.NotNull(svc.Session);
        Assert.True(svc.Session!.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29)); // 已续期为 now + 30d
        var persisted = store.Store.Load();
        Assert.NotNull(persisted);
        Assert.True(persisted!.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29)); // 续期已落盘
    }

    [Fact]
    public async Task Initialize_ExpiredSession_Probe401_ClearsStore_AndSetsInvalid()
    {
        using var store = NewStore();
        store.Store.Save(ExpiredSession());
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        // 本地过期 + 服务端吊销 → 清除凭据（服务端裁决优先于本地提示）
        Assert.Equal(CloudAuthState.Invalid, state);
        Assert.Null(svc.Session);
        Assert.Null(store.Store.Load());
    }

    [Fact]
    public async Task Initialize_ExpiredSession_ProbeOffline_KeepsCredentials()
    {
        using var store = NewStore();
        store.Store.Save(ExpiredSession());
        var svc = NewService(store, new ThrowingHandler());

        var state = await svc.InitializeAsync(CancellationToken.None);

        // 本地过期 + 云端不可达 → Offline 保留凭据（不打扰、不强制登出），ExpiresAt 原样保留
        Assert.Equal(CloudAuthState.Offline, state);
        Assert.NotNull(svc.Session);
        var persisted = store.Store.Load();
        Assert.NotNull(persisted);
        Assert.True(persisted!.ExpiresAt <= DateTimeOffset.UtcNow); // 未续期（无网络裁决）
    }

    [Fact]
    public async Task Initialize_ValidSession_Probe200_NoRenewal_WhenNotDue()
    {
        using var store = NewStore();
        var session = ValidSession() with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(10) };
        store.Store.Save(session);
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        );

        var before = store.Store.Load()!.ExpiresAt!.Value;
        var state = await svc.InitializeAsync(CancellationToken.None);
        var after = store.Store.Load()!.ExpiresAt!.Value;

        // 未临近过期 → 不重写凭据文件（心跳不会每 5 分钟触发磁盘写入）
        Assert.Equal(CloudAuthState.SignedIn, state);
        Assert.InRange(after, before.AddSeconds(-2), before.AddSeconds(2));
    }

    [Fact]
    public async Task Initialize_ExpiredSession_Probe200_ServerExpiresAt_AdoptsServerTruth()
    {
        using var store = NewStore();
        store.Store.Save(ExpiredSession());
        var server = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var svc = NewService(
            store,
            new StubHandler(_ => Json("""{"expires-at":"2026-12-31T00:00:00Z"}"""))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        // §13 Tier 2：capabilities 携带服务端 expiresAt → 采用服务端真值（而非客户端滑动 30 天）
        Assert.Equal(CloudAuthState.SignedIn, state);
        Assert.Equal(server, svc.Session!.ExpiresAt);
        Assert.Equal(server, store.Store.Load()!.ExpiresAt); // 真值已落盘
    }

    [Fact]
    public async Task Initialize_ExpiredSession_Probe200_PastServerExpiresAt_FallsBackToSliding()
    {
        using var store = NewStore();
        store.Store.Save(ExpiredSession());
        // 服务端返回已过去的 expiresAt（矛盾响应）→ 防御：不采用，回退滑动续期
        var svc = NewService(
            store,
            new StubHandler(_ => Json("""{"expires-at":"2020-01-01T00:00:00Z"}"""))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.SignedIn, state);
        Assert.True(svc.Session!.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29));
        Assert.True(store.Store.Load()!.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task Initialize_Probe200_ContractVersionMismatch_WarnsButStaysSignedIn()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var logger = new CaptureLogger();
        var svc = NewService(
            store,
            new StubHandler(_ => Json("""{"contract-version":"2.0","capabilities":[]}""")),
            logger: logger
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        // 契约版本不一致仅留痕告警，不改变登录结果（旧/新服务端兼容由能力键容忍策略兜底）
        Assert.Equal(CloudAuthState.SignedIn, state);
        Assert.True(svc.IsSignedIn);
        Assert.Contains(logger.Warnings, w => w.Contains("契约版本", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, w => w.Contains("2.0", StringComparison.Ordinal));
        svc.Dispose();
    }

    [Fact]
    public async Task Initialize_ValidSession_Probe200_SignedIn_KeepsSession()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = NewService(store, handler);

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.SignedIn, state);
        Assert.True(svc.IsSignedIn);
        Assert.Equal(PublicId, svc.PublicId);
        Assert.NotNull(store.Store.Load());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Initialize_Probe401_ClearsStore_AndSetsInvalid()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.Invalid, state);
        Assert.Null(svc.Session);
        Assert.Null(store.Store.Load()); // 服务端吊销 → 本地凭据清除
    }

    [Fact]
    public async Task Initialize_Probe403_SetsInvalid_ClearsStore()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.Invalid, state);
        Assert.Null(store.Store.Load());
    }

    [Fact]
    public async Task Initialize_Probe429_RateLimited_KeepsCredentials()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.RateLimited, state);
        Assert.NotNull(svc.Session); // 限流不视为失效
        Assert.NotNull(store.Store.Load()); // 凭据保留
    }

    [Fact]
    public async Task Initialize_Probe402_QuotaExceeded_KeepsCredentials()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.QuotaExceeded, state);
        Assert.NotNull(store.Store.Load());
    }

    [Fact]
    public async Task Initialize_Probe503_Offline_KeepsCredentials()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.Offline, state);
        Assert.NotNull(svc.Session); // 服务端不可用 → 凭据暂保留，不打扰
        Assert.NotNull(store.Store.Load());
    }

    [Fact]
    public async Task Initialize_Offline_StartsHeartbeat_AndRecoversWhenCloudReturns()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var first = 1;
        var handler = new StubHandler(_ =>
        {
            // 启动探活 → 503（离线）；心跳后续探活 → 200（云端恢复）
            var isFirst = Interlocked.Exchange(ref first, 0) == 1;
            return isFirst
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        var svc = NewService(store, handler);
        svc.HeartbeatInterval = TimeSpan.FromMilliseconds(50); // 测试缝：收窄心跳间隔

        var state = await svc.InitializeAsync(CancellationToken.None);

        // §14 P1：启动离线 → 降级 Offline，但心跳已无条件启动（不再卡死到重启）
        Assert.Equal(CloudAuthState.Offline, state);

        // 心跳自动重试 → 云端恢复后回到 SignedIn，无需重启（轮询等待，防时序抖动）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.State != CloudAuthState.SignedIn && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(CloudAuthState.SignedIn, svc.State);
        Assert.True(svc.IsSignedIn);
        Assert.True(handler.CallCount >= 2); // 启动探活 + 至少一次心跳探活
    }

    [Fact]
    public async Task Initialize_NetworkError_Offline_KeepsCredentials()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var svc = NewService(store, new ThrowingHandler());

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.Offline, state);
        Assert.NotNull(svc.Session);
        Assert.NotNull(store.Store.Load());
    }

    [Fact]
    public async Task Initialize_Probe_SendsXApiKeyHeader()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        string? sentKey = null;
        var handler = new StubHandler(request =>
        {
            sentKey = request.Headers.TryGetValues("X-API-Key", out var values)
                ? values.FirstOrDefault()
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var svc = NewService(store, handler);

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(ValidKey, sentKey);
        Assert.Equal("/v1/capabilities", handler.LastRequestPath);
    }

    [Fact]
    public async Task Initialize_RaisesStateChanged_WithTerminalState()
    {
        using var store = NewStore();
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        );
        var states = new List<CloudAuthState>();
        svc.StateChanged += s => states.Add(s);

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Contains(CloudAuthState.SignedOut, states);
        Assert.Equal(CloudAuthState.SignedOut, states[^1]);
    }

    // ═══════════════ LoginWithManualKeyAsync：手动 Key（格式 + 远程双重校验） ═══════════════

    [Fact]
    public async Task ManualKey_InvalidFormat_ReturnsFalse_WithoutSaving()
    {
        using var store = NewStore();
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        );

        var ok = await svc.LoginWithManualKeyAsync("sk-live-abc", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("格式无效", svc.LoginStatus);
        Assert.Null(store.Store.Load());
        // 格式校验失败不触碰状态（未初始化的服务保持 Unknown）
        Assert.Equal(CloudAuthState.Unknown, svc.State);
    }

    [Fact]
    public async Task ManualKey_ValidKey_Probe200_SavesSession_AndSignsIn()
    {
        using var store = NewStore();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithManualKeyAsync(ValidKey, CancellationToken.None);

        Assert.True(ok);
        Assert.True(svc.IsSignedIn);
        Assert.Equal(PublicId, svc.PublicId);
        Assert.NotNull(store.Store.Load());
        Assert.Equal(PublicId, store.Store.Load()!.PublicId);
        Assert.Contains("登录成功", svc.LoginStatus);
    }

    [Fact]
    public async Task ManualKey_Probe200_ServerExpiresAt_Adopted()
    {
        using var store = NewStore();
        var server = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var svc = NewService(
            store,
            new StubHandler(_ => Json("""{"expires-at":"2026-12-31T00:00:00Z"}"""))
        );

        var ok = await svc.LoginWithManualKeyAsync(ValidKey, CancellationToken.None);

        // §13 Tier 2：服务端真值存在时手动 Key 同样采用（服务端说了算），且续期结果不被落盘覆盖
        Assert.True(ok);
        Assert.Equal(server, svc.Session!.ExpiresAt);
        Assert.Equal(server, store.Store.Load()!.ExpiresAt);
    }

    [Fact]
    public async Task ManualKey_ValidKey_Probe401_ReturnsFalse_WithoutSaving()
    {
        using var store = NewStore();
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
        );

        var ok = await svc.LoginWithManualKeyAsync(ValidKey, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("无效或已吊销", svc.LoginStatus);
        Assert.Null(store.Store.Load()); // 服务端拒绝 → 不落盘
        Assert.Equal(CloudAuthState.SignedOut, svc.State);
    }

    [Fact]
    public async Task ManualKey_ValidKey_NetworkError_OfflineFallback_SavesSession()
    {
        using var store = NewStore();
        var svc = NewService(store, new ThrowingHandler());

        var ok = await svc.LoginWithManualKeyAsync(ValidKey, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains("已保存", svc.LoginStatus); // 云端不可达 → 离线暂存
        Assert.Equal(CloudAuthState.Offline, svc.State);
        Assert.NotNull(store.Store.Load()); // 凭据落盘，稍后心跳自动校验
    }

    // ═══════════════ LoginWithDeviceFlowAsync：设备流 ═══════════════

    [Fact]
    public async Task DeviceFlow_Approved_SavesSession_AndSignsIn()
    {
        using var store = NewStore();
        var handler = new StubHandler(DeviceFlowRouter());
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.True(svc.IsSignedIn);
        Assert.Equal(PublicId, svc.PublicId);
        Assert.NotNull(store.Store.Load());
        Assert.Equal("ws1", store.Store.Load()!.WorkspaceId);
        Assert.False(svc.IsDeviceFlowVisible); // 成功后隐藏设备流面板
        Assert.Equal("✅ 登录成功", svc.LoginStatus);
    }

    [Fact]
    public async Task DeviceFlow_ServerExpiresAt_IsAdopted()
    {
        using var store = NewStore();
        // 动态未来时间：服务端真值须为未来才被采用（过去值按 §13 设计回退滑动续期）
        var server = DateTimeOffset.UtcNow.AddDays(20);
        var handler = new StubHandler(
            DeviceFlowRouter(tokenResponder: _ =>
                Json(
                    "{\"api-key\":\"mk_live_dev123_secret\",\"public-id\":\"dev123\",\"workspace-id\":\"ws1\",\"plan\":\"free\",\"expires-at\":\""
                        + server.ToString("O")
                        + "\"}"
                )
            )
        );
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);

        // §13 Tier 2：登录响应携带服务端 expiresAt → 优先采用真值（而非客户端 30 天默认）
        Assert.True(ok);
        Assert.Equal(server, svc.Session!.ExpiresAt);
        Assert.Equal(server, store.Store.Load()!.ExpiresAt);
    }

    [Fact]
    public async Task DeviceFlow_NoServerExpiresAt_FallsBackToSliding30Days()
    {
        using var store = NewStore();
        var svc = NewService(store, new StubHandler(DeviceFlowRouter()));

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);

        // 旧服务端不返回 expiresAt → 回退 Tier 1 滑动（now + 30d，向后兼容）
        Assert.True(ok);
        Assert.InRange(
            svc.Session!.ExpiresAt!.Value,
            DateTimeOffset.UtcNow.AddDays(30).AddSeconds(-5),
            DateTimeOffset.UtcNow.AddDays(30).AddSeconds(5)
        );
    }

    [Fact]
    public async Task DeviceFlow_PendingThenTimeout_ReturnsFalse_WithoutSaving()
    {
        using var store = NewStore();
        // expires-in-seconds=1 → 轮询 1 秒后自动超时退出
        const string shortAuthJson =
            """{"device-code":"dc1","user-code":"ABCDEF","verification-uri":"https://cloud.test/app/device?user_code=ABCDEF","expires-in-seconds":1,"poll-interval-seconds":1}""";
        var handler = new StubHandler(
            DeviceFlowRouter(
                authorizeJson: shortAuthJson,
                tokenResponder: _ => new HttpResponseMessage(HttpStatusCode.Conflict) // 一直 pending
            )
        );
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("超时", svc.DeviceStatus);
        Assert.Null(store.Store.Load());
        Assert.False(svc.IsSignedIn);
    }

    [Fact]
    public async Task DeviceFlow_AuthorizeFails_ReturnsFalse()
    {
        using var store = NewStore();
        var handler = new StubHandler(
            DeviceFlowRouter(authorizeStatus: HttpStatusCode.InternalServerError)
        );
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("失败", svc.DeviceStatus);
        Assert.Null(store.Store.Load());
    }

    [Fact]
    public async Task DeviceFlow_Success_PostLoginProbe503_DegradesToOffline_WithHint()
    {
        using var store = NewStore();
        var handler = new StubHandler(
            DeviceFlowRouter(capabilitiesStatus: HttpStatusCode.ServiceUnavailable)
        );
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);

        // §13.7：token 端点已确认登录，但 capabilities 端到端探活失败 → 登录不否定，仅降级 + 提示
        Assert.True(ok);
        Assert.NotNull(svc.Session); // 同步（ApplySession）——凭据保留（心跳继续重试，恢复后自动回 SignedIn）
        Assert.NotNull(store.Store.Load());

        // §14 P6：探活 fire-and-forget → 降级结果异步落地，轮询等待（防时序抖动）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.State != CloudAuthState.Offline && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(CloudAuthState.Offline, svc.State);
        Assert.Contains("暂不可达", svc.LoginStatus); // 降级提示
    }

    [Fact]
    public async Task DeviceFlow_Success_PostLoginProbe401_SetsInvalid_WithHint()
    {
        using var store = NewStore();
        var handler = new StubHandler(
            DeviceFlowRouter(capabilitiesStatus: HttpStatusCode.Unauthorized)
        );
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);

        // §13.7 极端情况：签发后立即被 /v1/capabilities 拒绝（服务端不一致）→ Invalid 语义，横幅引导重登
        Assert.True(ok);
        Assert.NotNull(svc.Session);

        // §14 P6：探活 fire-and-forget → Invalid 异步落地，轮询等待
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.State != CloudAuthState.Invalid && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(CloudAuthState.Invalid, svc.State);
        Assert.Contains("校验异常", svc.LoginStatus);
    }

    [Fact]
    public async Task PostLoginProbe_LogoutDuringProbe_DoesNotResurrectState()
    {
        using var store = NewStore();
        var handler = new DelayedCapabilitiesHandler(
            DeviceFlowRouter(),
            TimeSpan.FromMilliseconds(300)
        );
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);
        Assert.True(ok);

        // 登录后探活（300ms 延迟）仍在途 → 立即登出（§14 P6：探活不阻塞登录，但登出必须不被探活推翻）
        svc.Logout();
        await Task.Delay(600); // 等探活完成

        Assert.Equal(CloudAuthState.SignedOut, svc.State); // 探活结果属于旧会话 → 丢弃，不复活
        Assert.Null(svc.Session);
        Assert.Null(store.Store.Load());
    }

    [Fact]
    public async Task PostLoginProbe_LogoutDuringBodyRead_DoesNotResurrectState()
    {
        using var store = NewStore();
        var handler = new StubHandler(request =>
        {
            // 头部立即就绪，body 延迟 300ms（ResponseHeadersRead → ReadAsStringAsync 真实让步）
            if (request.RequestUri!.AbsolutePath == "/v1/capabilities")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new DelayedContent("{}", TimeSpan.FromMilliseconds(300)),
                };
            return DeviceFlowRouter().Invoke(request);
        });
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithDeviceFlowAsync(CancellationToken.None);
        Assert.True(ok);

        // §16 审计补漏：探活已过 SendAsync 守卫、正在读取 body（300ms 延迟）→ 此刻登出
        svc.Logout();
        await Task.Delay(600); // 等 body 读取完成

        // 守卫覆盖最后一次 await → body 读取期间的登出同样被丢弃，不复活 SignedIn
        Assert.Equal(CloudAuthState.SignedOut, svc.State);
        Assert.Null(svc.Session);
        Assert.Null(store.Store.Load());
    }

    [Fact]
    public async Task DeviceFlow_Success_RaisesStateChanged_ToSignedIn()
    {
        using var store = NewStore();
        var svc = NewService(store, new StubHandler(DeviceFlowRouter()));
        var states = new List<CloudAuthState>();
        svc.StateChanged += s => states.Add(s);

        await svc.LoginWithDeviceFlowAsync(CancellationToken.None);

        Assert.Contains(CloudAuthState.SignedIn, states);
    }

    // ═══════════════ LoginWithOAuthAsync：本地回调 + 回退 ═══════════════

    [Fact]
    public async Task OAuth_PreflightRejected_FallsBackToDeviceFlow()
    {
        using var store = NewStore();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/oauth/authorize")
                return new HttpResponseMessage(HttpStatusCode.BadRequest); // 服务端不接受回调参数
            return DeviceFlowRouter().Invoke(request);
        });
        var svc = NewService(store, handler);

        var ok = await svc.LoginWithOAuthAsync(CancellationToken.None);

        Assert.True(ok); // 自动回退设备流成功
        Assert.True(svc.IsSignedIn);
        Assert.NotNull(store.Store.Load());
        // 回退提示是中间态，最终被设备流成功态覆盖
        Assert.Equal("✅ 登录成功", svc.LoginStatus);
    }

    [Fact]
    public async Task OAuth_SshSession_SkipsLoopback_GoesDirectlyToDeviceFlow()
    {
        Environment.SetEnvironmentVariable("SSH_CONNECTION", "1");
        try
        {
            using var store = NewStore();
            var handler = new StubHandler(DeviceFlowRouter());
            var svc = NewService(store, handler);

            var ok = await svc.LoginWithOAuthAsync(CancellationToken.None);

            Assert.True(ok);
            Assert.True(svc.IsSignedIn);
            Assert.NotNull(store.Store.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SSH_CONNECTION", null);
        }
    }

    // ═══════════════ Logout ═══════════════

    [Fact]
    public async Task Logout_ClearsStore_StopsSession_AndSetsSignedOut()
    {
        using var store = NewStore();
        var svc = NewService(store, new StubHandler(DeviceFlowRouter()));
        await svc.LoginWithDeviceFlowAsync(CancellationToken.None);
        Assert.True(svc.IsSignedIn);

        var states = new List<CloudAuthState>();
        svc.StateChanged += s => states.Add(s);
        svc.Logout();

        Assert.Equal(CloudAuthState.SignedOut, svc.State);
        Assert.Null(svc.Session);
        Assert.Null(store.Store.Load());
        Assert.Contains(CloudAuthState.SignedOut, states);
    }

    [Fact]
    public async Task Logout_WithoutSession_IsIdempotent()
    {
        using var store = NewStore();
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        );

        svc.Logout();

        Assert.Equal(CloudAuthState.SignedOut, svc.State);
        Assert.Null(svc.Session);
    }

    // ═══════════════ 事件驱动失效检测（§13.4） ═══════════════

    [Fact]
    public async Task InvalidationBus_Raise_WhenSignedIn_ClearsStore_AndSetsInvalid()
    {
        using var store = NewStore();
        var bus = new CloudAuthInvalidationBus();
        var svc = NewService(store, new StubHandler(DeviceFlowRouter()), bus);
        await svc.LoginWithDeviceFlowAsync(CancellationToken.None);
        Assert.True(svc.IsSignedIn);

        var states = new List<CloudAuthState>();
        svc.StateChanged += s => states.Add(s);

        bus.Raise("/v1/agent/runs");

        // §13.4：真实 API 调用 401/403 → 立即清除凭据并降级（不等 5 分钟心跳）
        Assert.Equal(CloudAuthState.Invalid, svc.State);
        Assert.False(svc.IsSignedIn);
        Assert.Null(svc.Session);
        Assert.Null(store.Store.Load());
        Assert.Contains(CloudAuthState.Invalid, states); // 已全局广播（Header/状态栏同步降级）
    }

    [Fact]
    public async Task InvalidationBus_Raise_WithoutSession_IsNoop()
    {
        using var store = NewStore();
        var bus = new CloudAuthInvalidationBus();
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            bus
        );

        bus.Raise();

        Assert.Equal(CloudAuthState.Unknown, svc.State); // 无凭据 → 幂等，不触碰状态
        Assert.Null(store.Store.Load());
    }

    [Fact]
    public async Task InvalidationBus_Disposed_Unsubscribes()
    {
        using var store = NewStore();
        var bus = new CloudAuthInvalidationBus();
        var svc = NewService(store, new StubHandler(DeviceFlowRouter()), bus);
        await svc.LoginWithDeviceFlowAsync(CancellationToken.None);
        Assert.True(svc.IsSignedIn);
        svc.Dispose();

        bus.Raise();

        // 已退订 → 吊销事件不再影响该实例（幂等退订，Dispose 可重复调用）
        Assert.True(svc.IsSignedIn);
        Assert.NotNull(store.Store.Load());
    }

    // ═══════════════ 能力目录缓存（2026-10-18：探活 → 诊断调度消费）═══════════════

    [Fact]
    public async Task Initialize_Probe200_WritesCapabilityCatalogSnapshot()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var catalog = new CloudCapabilityCatalogStore();
        var svc = NewService(
            store,
            new StubHandler(_ =>
                Json(
                    """{"contract-version":"1.0","capabilities":[{"id":"fiction.story.check","category":"review","scope":"chapter-window","supported-depths":["quick","standard","deep"]}]}"""
                )
            ),
            catalog: catalog
        );

        var state = await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(CloudAuthState.SignedIn, state);
        // 探活 2xx = 该 Key 的权威视图：分类/范围/深度进入共享快照（诊断调度据此组装请求）
        var snapshot = Assert.IsType<CapabilityCatalogSnapshot>(catalog.Current);
        Assert.True(snapshot.IsAdvertised("fiction.story.check"));
        Assert.Equal("review", snapshot.Find("fiction.story.check")!.Category);
        Assert.True(snapshot.SupportsDepth("fiction.story.check", "standard"));
    }

    [Fact]
    public async Task Initialize_Probe200WithoutCatalog_KeepsLastKnownGood()
    {
        using var store = NewStore();
        store.Store.Save(ValidSession());
        var catalog = new CloudCapabilityCatalogStore();
        catalog.Update(CapabilityCatalogSnapshot.Parse("""{"capabilities":[{"id":"x"}]}""")!);
        var svc = NewService(
            store,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), // 空体（旧服务端/中间层）
            catalog: catalog
        );

        await svc.InitializeAsync(CancellationToken.None);

        Assert.True(catalog.Current!.IsAdvertised("x")); // 解析不出快照 → 保留 last-known-good
    }

    // ═══════════════ 工具 ═══════════════

    private static CloudAuthService NewService(
        TestStoreDir store,
        HttpMessageHandler handler,
        CloudAuthInvalidationBus? bus = null,
        ILogger<CloudAuthService>? logger = null,
        CloudCapabilityCatalogStore? catalog = null
    ) => new TestableCloudAuthService(store.Store, handler, BaseUrl, bus, logger, catalog);

    private static TestStoreDir NewStore() => new();

    private static CloudSession ValidSession() =>
        new(
            ValidKey,
            PublicId,
            "ws1",
            "free",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(30)
        );

    private static CloudSession ExpiredSession() =>
        new(
            ValidKey,
            PublicId,
            "ws1",
            "free",
            DateTimeOffset.UtcNow.AddDays(-10),
            DateTimeOffset.UtcNow.AddMinutes(-1)
        );

    /// <summary>
    /// 设备流协议路由：discovery 404 → authorize → /token/stream（非 SSE）→ legacy /token；
    /// /v1/capabilities 为登录后探活端点（§13.7），默认 200，可注入失败状态。
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> DeviceFlowRouter(
        string? authorizeJson = null,
        Func<HttpRequestMessage, HttpResponseMessage>? tokenResponder = null,
        HttpStatusCode authorizeStatus = HttpStatusCode.OK,
        HttpStatusCode capabilitiesStatus = HttpStatusCode.OK
    ) =>
        request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/oauth-authorization-server")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path == "/v1/device/authorize")
                return authorizeStatus == HttpStatusCode.OK
                    ? Json(authorizeJson ?? DeviceAuthorizeJson)
                    : new HttpResponseMessage(authorizeStatus);
            if (path == "/v1/capabilities")
                return capabilitiesStatus == HttpStatusCode.OK
                    ? Json("{}")
                    : new HttpResponseMessage(capabilitiesStatus);
            if (path == "/v1/device/token/stream")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    // 非 SSE Content-Type → 触发 PollTokenLegacyAsync 回退到 /v1/device/token
                    Content = new StringContent("legacy", Encoding.UTF8, "text/plain"),
                };
            if (path == "/v1/device/token")
                return tokenResponder?.Invoke(request) ?? Json(DeviceTokenJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    private const string DeviceAuthorizeJson =
        """{"device-code":"dc1","user-code":"ABCDEF","verification-uri":"https://cloud.test/app/device?user_code=ABCDEF","expires-in-seconds":600,"poll-interval-seconds":1}""";

    private const string DeviceTokenJson =
        """{"api-key":"mk_live_dev123_secret","public-id":"dev123","workspace-id":"ws1","plan":"free"}""";

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode status = HttpStatusCode.OK
    ) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>测试专用子类：覆写 OpenBrowser 为 no-op，避免测试中真的打开浏览器。</summary>
    private sealed class TestableCloudAuthService(
        CloudSessionStore store,
        HttpMessageHandler handler,
        string baseUrl,
        CloudAuthInvalidationBus? bus = null,
        ILogger<CloudAuthService>? logger = null,
        CloudCapabilityCatalogStore? capabilityCatalog = null
    ) : CloudAuthService(store, handler, baseUrl, bus, logger, capabilityCatalog: capabilityCatalog)
    {
        protected override void OpenBrowser(string url)
        {
            // 测试环境不打开浏览器
        }
    }

    /// <summary>捕获 Warning 日志的测试 logger（契约版本比对告警断言用）。</summary>
    private sealed class CaptureLogger : ILogger<CloudAuthService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        private int _callCount;

        /// <summary>总请求数（并发安全）。</summary>
        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>最近一次请求的路径（无请求时为 null）。</summary>
        public string? LastRequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _callCount);
            LastRequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>模拟网络不可达（DNS/连接失败）。</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("network unreachable")
            );
    }

    /// <summary>capabilities 请求延迟响应（模拟慢网络/在途探活），其余走设备流路由。</summary>
    private sealed class DelayedCapabilitiesHandler(
        Func<HttpRequestMessage, HttpResponseMessage> router,
        TimeSpan delay
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri?.AbsolutePath == "/v1/capabilities")
                await Task.Delay(delay, cancellationToken);
            return router(request);
        }
    }

    /// <summary>响应头立即就绪、body 延迟的内容（ResponseHeadersRead 下 ReadAsStringAsync 真实让步）。</summary>
    private sealed class DelayedContent(string body, TimeSpan delay) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(body);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context
        )
        {
            await Task.Delay(delay);
            await stream.WriteAsync(_bytes);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }

    /// <summary>临时凭据目录（测试结束自动清理）。</summary>
    private sealed class TestStoreDir : IDisposable
    {
        public TestStoreDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"myth-cloudauth-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Dir);
            Store = new CloudSessionStore(Dir);
        }

        public string Dir { get; }
        public CloudSessionStore Store { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Dir))
                    Directory.Delete(Dir, true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }
}
