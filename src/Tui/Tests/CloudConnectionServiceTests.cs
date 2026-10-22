using System.Net;
using ClientPlatform;
using Core.Cloud;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// CloudConnectionService 单元测试（云端登录设计 §13 Tier 1 落地验证）：
/// - 本地过期不再短路 IsConfigured（提示而非硬门，与 Desktop 对齐）；
/// - RenewIfDue：过期 → 滑动续期落盘；未临近 → 不重写；手动 Key（null）→ 永不续期；
/// - 仅服务端 401/403 才裁决失效（Probe 层职责，本类只负责续期提示）。
/// </summary>
public sealed class CloudConnectionServiceTests
{
    private const string ValidKey = "mk_live_dev123_secret";

    [Fact]
    public void IsConfigured_ExpiredSession_StillTrue()
    {
        using var dir = NewDir();
        dir.Store.Save(ExpiredSession());
        var svc = new CloudConnectionService(dir.Store);

        svc.Load();

        // §13 Tier 1：过期只是提示，不再短路 — Probe 成功后由 RenewIfDue 滑动续期
        Assert.True(svc.IsConfigured);
    }

    [Fact]
    public void IsConfigured_NoKey_False()
    {
        using var dir = NewDir();
        var svc = new CloudConnectionService(dir.Store);

        svc.Load();

        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public void RenewIfDue_Expired_SlidesToNowPlus30Days_AndPersists()
    {
        using var dir = NewDir();
        dir.Store.Save(ExpiredSession());
        var svc = new CloudConnectionService(dir.Store);
        svc.Load();

        svc.RenewIfDue();

        Assert.NotNull(svc.Current);
        Assert.True(svc.Current!.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29)); // 已续期为 now + 30d
        var persisted = dir.Store.Load();
        Assert.NotNull(persisted);
        Assert.True(persisted!.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29)); // 续期已落盘
    }

    [Fact]
    public void RenewIfDue_NotDue_DoesNotRewrite()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession() with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(10) });
        var svc = new CloudConnectionService(dir.Store);
        svc.Load();
        var before = dir.Store.Load()!.ExpiresAt!.Value;

        svc.RenewIfDue();

        // 未临近过期 → 不重写凭据文件（心跳不会每 5 分钟触发磁盘写入）
        var after = dir.Store.Load()!.ExpiresAt!.Value;
        Assert.InRange(after, before.AddSeconds(-2), before.AddSeconds(2));
    }

    [Fact]
    public void RenewIfDue_NoExpiry_ManualKey_NeverRenews()
    {
        using var dir = NewDir();
        var session = new CloudSession(ValidKey, "dev123", "ws1", "free", DateTimeOffset.UtcNow);
        dir.Store.Save(session);
        var svc = new CloudConnectionService(dir.Store);
        svc.Load();

        svc.RenewIfDue();

        Assert.Null(svc.ExpiresAt); // 手动 Key 永不本地过期，有效性完全由服务端裁决
        Assert.Null(dir.Store.Load()!.ExpiresAt);
    }

    [Fact]
    public void RenewIfDue_ServerExpiresAtFuture_Adopted()
    {
        using var dir = NewDir();
        dir.Store.Save(ExpiredSession());
        var svc = new CloudConnectionService(dir.Store);
        svc.Load();
        var server = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);

        svc.RenewIfDue(server);

        // §13 Tier 2：capabilities 携带服务端 expiresAt → 采用真值（而非滑动 30 天）
        Assert.Equal(server, svc.ExpiresAt);
        Assert.Equal(server, dir.Store.Load()!.ExpiresAt); // 真值已落盘
    }

    [Fact]
    public void RenewIfDue_ServerExpiresAtPast_FallsBackToSliding()
    {
        using var dir = NewDir();
        dir.Store.Save(ExpiredSession());
        var svc = new CloudConnectionService(dir.Store);
        svc.Load();

        svc.RenewIfDue(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // 服务端返回已过去的 expiresAt（矛盾响应）→ 防御：不采用，回退滑动续期
        Assert.True(svc.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29));
    }

    [Fact]
    public void RenewIfDue_ServerExpiresAtFuture_ManualKey_Adopted()
    {
        using var dir = NewDir();
        var session = new CloudSession(ValidKey, "dev123", "ws1", "free", DateTimeOffset.UtcNow);
        dir.Store.Save(session);
        var svc = new CloudConnectionService(dir.Store);
        svc.Load();
        var server = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);

        svc.RenewIfDue(server);

        // 服务端真值存在时手动 Key 同样采用（服务端说了算）
        Assert.Equal(server, svc.ExpiresAt);
        Assert.Equal(server, dir.Store.Load()!.ExpiresAt);
    }

    // ═══════════════ §13.7 端到端连通性探活（HTTP 收口在门面） ═══════════════

    [Fact]
    public async Task ProbeAsync_200_ReturnsConnected_AndRenews()
    {
        using var dir = NewDir();
        dir.Store.Save(ExpiredSession());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = new CloudConnectionService(dir.Store, handler: handler);
        svc.Load();

        var state = await svc.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloudConnectionState.Connected, state);
        Assert.Equal(CloudConnectionState.Connected, svc.ConnectionState);
        // 成功探活触发滑动续期（与 Desktop CloudAuthService.ProbeAsync 对称）
        Assert.True(svc.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29));
        Assert.True(dir.Store.Load()!.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(29));
        Assert.Equal("/v1/capabilities", handler.LastRequestPath);
    }

    [Fact]
    public async Task ProbeAsync_401_ReturnsAuthenticating_WithoutClearing()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession());
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
        );
        svc.Load();

        var state = await svc.ProbeAsync(TestContext.Current.CancellationToken);

        // 401/403 → Authenticating（吊销语义）；清除由调用方决定（启动/心跳清除，登录后探活保留）
        Assert.Equal(CloudConnectionState.Authenticating, state);
        Assert.Equal(CloudConnectionState.Authenticating, svc.ConnectionState);
        Assert.True(svc.IsConfigured); // 门面不清除凭据
        Assert.NotNull(dir.Store.Load());
    }

    [Fact]
    public async Task ProbeAsync_503_ReturnsUnavailable()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession());
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable
            ))
        );
        svc.Load();

        var state = await svc.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloudConnectionState.Unavailable, state);
        Assert.Equal(CloudConnectionState.Unavailable, svc.ConnectionState);
        Assert.True(svc.IsConfigured); // 服务端不可达不视为失效
    }

    [Fact]
    public async Task ProbeAsync_NetworkError_ReturnsUnavailable()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession());
        var svc = new CloudConnectionService(dir.Store, handler: new ThrowingHandler());
        svc.Load();

        var state = await svc.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloudConnectionState.Unavailable, state);
        Assert.Equal(CloudConnectionState.Unavailable, svc.ConnectionState);
        Assert.True(svc.IsConfigured);
    }

    [Fact]
    public async Task ProbeAsync_NoKey_ReturnsAuthenticating()
    {
        using var dir = NewDir();
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        );
        svc.Load();

        var state = await svc.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloudConnectionState.Authenticating, state);
    }

    // ═══════════════ §14 P2 手动 Key 远程双重校验（ValidateKeyAsync） ═══════════════

    [Fact]
    public async Task ValidateKeyAsync_200_ReturnsConnected_DoesNotTouchState()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession());
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        );
        svc.Load();
        var before = svc.ConnectionState; // Load 后为 Authenticating

        var state = await svc.ValidateKeyAsync(
            "mk_live_other123_secret",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(CloudConnectionState.Connected, state);
        // 纯校验：不触碰 Current/ConnectionState/store（与 Desktop 手动 Key 先 Probe 后落盘语义一致）
        Assert.Equal(before, svc.ConnectionState);
        Assert.NotNull(svc.Current);
        Assert.Equal(ValidSession().ApiKey, svc.ApiKey); // 仍是已加载凭据，非被校验 Key
        Assert.NotNull(dir.Store.Load());
    }

    [Fact]
    public async Task ValidateKeyAsync_401_ReturnsAuthenticating_AndDoesNotSave()
    {
        using var dir = NewDir();
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
        );
        svc.Load(); // 无凭据 → Disabled

        var state = await svc.ValidateKeyAsync(
            "mk_live_dev123_secret",
            TestContext.Current.CancellationToken
        );

        // 服务端拒绝 → 无效/已吊销（对话框据此不落盘，与 Desktop 双重校验对齐）
        Assert.Equal(CloudConnectionState.Authenticating, state);
        Assert.Null(svc.Current);
        Assert.Null(dir.Store.Load());
    }

    [Fact]
    public async Task ValidateKeyAsync_503_ReturnsUnavailable()
    {
        using var dir = NewDir();
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable
            ))
        );
        svc.Load();

        var state = await svc.ValidateKeyAsync(
            "mk_live_dev123_secret",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(CloudConnectionState.Unavailable, state); // 不可达 → 调用方决定离线暂存
    }

    [Fact]
    public async Task ValidateKeyAsync_NetworkError_ReturnsUnavailable()
    {
        using var dir = NewDir();
        var svc = new CloudConnectionService(dir.Store, handler: new ThrowingHandler());
        svc.Load();

        var state = await svc.ValidateKeyAsync(
            "mk_live_dev123_secret",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(CloudConnectionState.Unavailable, state);
    }

    // ═══════════════ §13.6 事件驱动失效检测（DI 单例总线注入） ═══════════════

    [Fact]
    public void Bus_Raise_WhenConfigured_ClearsAndRaisesEvent()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession());
        var bus = new CloudAuthInvalidationBus();
        var svc = new CloudConnectionService(dir.Store, bus);
        svc.Load();
        Assert.True(svc.IsConfigured);

        var raised = 0;
        svc.CredentialsInvalidated += () => raised++;

        bus.Raise("/v1/agent/runs");

        // 云端客户端 401/403 → 立即清除凭据并降级（不等心跳），并通知 TuiApp 编排（停心跳 + Toast）
        Assert.False(svc.IsConfigured);
        Assert.Equal(CloudConnectionState.Disabled, svc.ConnectionState);
        Assert.Null(svc.Current);
        Assert.Null(dir.Store.Load());
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Bus_Raise_WithoutCredentials_IsNoop()
    {
        using var dir = NewDir();
        var bus = new CloudAuthInvalidationBus();
        var svc = new CloudConnectionService(dir.Store, bus);
        svc.Load();

        var raised = 0;
        svc.CredentialsInvalidated += () => raised++;

        bus.Raise();

        Assert.Equal(0, raised); // 无凭据 → 幂等，不触发编排事件
        Assert.Equal(CloudConnectionState.Disabled, svc.ConnectionState);
    }

    [Fact]
    public void Dispose_UnsubscribesFromBus()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession());
        var bus = new CloudAuthInvalidationBus();
        var svc = new CloudConnectionService(dir.Store, bus);
        svc.Load();
        Assert.True(svc.IsConfigured);

        svc.Dispose();
        bus.Raise();

        // 已退订 → 吊销事件不再影响该实例
        Assert.True(svc.IsConfigured);
        Assert.NotNull(dir.Store.Load());
    }

    [Fact]
    public async Task ProbeAsync_CachesCapabilityCatalog_ForSchedulerConsumption()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession());
        var catalog = new CloudCapabilityCatalogStore();
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"contract-version":"1.0","capabilities":[{"id":"fiction.story.check","category":"review","scope":"chapter-window","supported-depths":["quick","standard"]}]}"""
                ),
            }),
            capabilityCatalog: catalog
        );
        svc.Load();

        var state = await svc.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloudConnectionState.Connected, state);
        // 探活成功 = 服务端权威视图：分类/范围/深度进入共享快照（诊断调度据此组装请求）
        var snapshot = Assert.IsType<CapabilityCatalogSnapshot>(catalog.Current);
        Assert.True(snapshot.IsAdvertised("fiction.story.check"));
        Assert.Equal("chapter-window", snapshot.Find("fiction.story.check")!.Scope);
        Assert.True(snapshot.SupportsDepth("fiction.story.check", "standard"));
    }

    [Fact]
    public async Task ProbeAsync_KeepsLastKnownGood_WhenResponseHasNoCatalog()
    {
        using var dir = NewDir();
        dir.Store.Save(ValidSession());
        var catalog = new CloudCapabilityCatalogStore();
        catalog.Update(CapabilityCatalogSnapshot.Parse("""{"capabilities":[{"id":"x"}]}""")!);
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), // 空体（旧服务端/中间层）
            capabilityCatalog: catalog
        );
        svc.Load();

        await svc.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(catalog.Current!.IsAdvertised("x")); // 解析不出快照 → 保留 last-known-good
    }

    [Fact]
    public async Task ValidateKeyAsync_DoesNotWriteCatalog()
    {
        using var dir = NewDir();
        var catalog = new CloudCapabilityCatalogStore();
        var svc = new CloudConnectionService(
            dir.Store,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"capabilities":[{"id":"fiction.story.check"}]}"""),
            }),
            capabilityCatalog: catalog
        );

        var state = await svc.ValidateKeyAsync(ValidKey, TestContext.Current.CancellationToken);

        Assert.Equal(CloudConnectionState.Connected, state);
        // 纯校验路径（可能是别的账号/登录前）不写共享缓存
        Assert.Null(catalog.Current);
    }

    private static CloudSession ValidSession() =>
        new(
            ValidKey,
            "dev123",
            "ws1",
            "free",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(30)
        );

    private static CloudSession ExpiredSession() =>
        new(
            ValidKey,
            "dev123",
            "ws1",
            "free",
            DateTimeOffset.UtcNow.AddDays(-10),
            DateTimeOffset.UtcNow.AddMinutes(-1)
        );

    private static TestStoreDir NewDir() => new();

    /// <summary>响应桩：记录请求路径，返回注入的响应。</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public string? LastRequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
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

    /// <summary>临时凭据目录（测试结束自动清理）。</summary>
    private sealed class TestStoreDir : IDisposable
    {
        public TestStoreDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"myth-tui-cloud-{Guid.NewGuid():N}");
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
