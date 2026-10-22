// 功能：扫码绑定编排测试 —— 全状态机分支、local_token_list 回归、异常与取消语义

using Xunit;

namespace Channels.WeChat.Tests;

public class WeChatBindingFlowTests
{
    private const string QrJson =
        """{"qrcode":"qr-1","qrcode_img_content":"https://liteapp.weixin.qq.com/q/abc?qrcode=qr-1&bot_type=3","ret":0}""";

    private const string ConfirmedJson =
        """{"ret":0,"status":"confirmed","bot_token":"tk-1","ilink_bot_id":"abc@im.bot","baseurl":"https://ilinkai2.weixin.qq.com","ilink_user_id":"wxid_me"}""";

    private sealed record Fixture(
        WeChatBindingFlow Flow,
        WeChatChannelStatus Status,
        WeChatBindingStore Store,
        ScriptedHandler Handler
    );

    private static Fixture Build(TempDir dir)
    {
        var handler = new ScriptedHandler();
        var client = new WeChatIlinkClient(new StubHttpClientFactory(handler));
        var store = new WeChatBindingStore(dir.Path);
        var status = new WeChatChannelStatus();
        return new Fixture(new WeChatBindingFlow(client, store, status), status, store, handler);
    }

    [Fact]
    public async Task HappyPathBindsAndPersists()
    {
        using var dir = new TempDir();
        var f = Build(dir);
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson("""{"ret":0,"status":"wait"}""");
        f.Handler.EnqueueJson("""{"ret":0,"status":"scaned"}""");
        f.Handler.EnqueueJson(ConfirmedJson);

        var bound = await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.True(bound);
        Assert.Equal(WeChatPhase.Polling, f.Status.Snapshot.Phase);
        Assert.Equal("abc@im.bot", f.Status.Snapshot.AccountId);
        Assert.Equal("wxid_me", f.Status.Snapshot.PeerUserId);
        Assert.False(f.Status.Snapshot.IsQrVisible);

        var saved = f.Store.Load();
        Assert.True(saved.IsBound);
        Assert.Equal("tk-1", saved.Credential!.BotToken);
        Assert.Equal("https://ilinkai2.weixin.qq.com", saved.Credential.BaseUrl);
        Assert.Equal(["tk-1"], saved.LocalTokens);
        Assert.Equal("tui", saved.OwnerLabel);
    }

    [Fact]
    public async Task AlreadyStoredLocalTokensAreSentBack()
    {
        // 回归：不带 local_token_list 时服务端视本机为全新客户端，会重新签发并解绑旧绑定
        using var dir = new TempDir();
        var f = Build(dir);
        f.Store.Save(new WeChatBindingState { LocalTokens = ["old-token-a", "old-token-b"] });
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson(ConfirmedJson);

        var bound = await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.True(bound);
        Assert.Contains("old-token-a", f.Handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("old-token-b", f.Handler.Bodies[0], StringComparison.Ordinal);
        // 新 token 追加在前，旧的不丢
        Assert.Equal(["tk-1", "old-token-a", "old-token-b"], f.Store.Load().LocalTokens);
    }

    [Fact]
    public async Task ScanProgressIsReported()
    {
        using var dir = new TempDir();
        var f = Build(dir);
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson("""{"ret":0,"status":"scaned"}""");
        f.Handler.EnqueueJson(ConfirmedJson);

        var phases = new List<WeChatPhase>();
        f.Status.Changed += () => phases.Add(f.Status.Snapshot.Phase);

        await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.Contains(WeChatPhase.WaitingScan, phases);
        Assert.Contains(WeChatPhase.Scanned, phases);
        Assert.Contains(WeChatPhase.Polling, phases);
    }

    [Fact]
    public async Task ExpiredQrIsRefreshedWithFreshTokenList()
    {
        using var dir = new TempDir();
        var f = Build(dir);
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson("""{"ret":0,"status":"expired"}""");
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson(ConfirmedJson);

        var bound = await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.True(bound);
        Assert.Equal(
            2,
            f.Handler.Requests.Count(r =>
                r.RequestUri!.AbsoluteUri.Contains("get_bot_qrcode", StringComparison.Ordinal)
            )
        );
    }

    [Fact]
    public async Task RedirectHostIsUsedForSubsequentPolling()
    {
        using var dir = new TempDir();
        var f = Build(dir);
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson(
            """{"ret":0,"status":"scaned_but_redirect","redirect_host":"ilinkai2.weixin.qq.com"}"""
        );
        f.Handler.EnqueueJson(ConfirmedJson);

        var phases = new List<WeChatPhase>();
        f.Status.Changed += () => phases.Add(f.Status.Snapshot.Phase);

        var bound = await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.True(bound);
        // 切主机期间相位必须是 Connecting（宿主据此显示"正在连接"而不是"等待扫码"）
        Assert.Contains(WeChatPhase.Connecting, phases);
        Assert.Equal("ilinkai2.weixin.qq.com", f.Handler.Requests[2].RequestUri!.Host);
    }

    [Fact]
    public async Task VerifyCodeRequirementFailsWithClearMessage()
    {
        using var dir = new TempDir();
        var f = Build(dir);
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson("""{"ret":0,"status":"need_verifycode"}""");

        var bound = await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.False(bound);
        Assert.Equal(WeChatPhase.Failed, f.Status.Snapshot.Phase);
        Assert.Contains("验证码", f.Status.Snapshot.Error!, StringComparison.Ordinal);
        Assert.False(f.Store.Load().IsBound);
    }

    [Fact]
    public async Task UnknownStatusKeepsPolling()
    {
        // 服务端新增中间态时不该误判失败
        using var dir = new TempDir();
        var f = Build(dir);
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson("""{"ret":0,"status":"some-new-state"}""");
        f.Handler.EnqueueJson(ConfirmedJson);

        var bound = await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.True(bound);
    }

    [Fact]
    public async Task IncompleteConfirmedResponseIsRejectedAndNothingIsSaved()
    {
        using var dir = new TempDir();
        var f = Build(dir);
        f.Store.Save(new WeChatBindingState { GetUpdatesBuf = "keep-me" });
        f.Handler.EnqueueJson(QrJson);
        f.Handler.EnqueueJson("""{"ret":0,"status":"confirmed","bot_token":"tk-1"}""");

        var bound = await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.False(bound);
        Assert.Equal(WeChatPhase.Failed, f.Status.Snapshot.Phase);
        // 半份凭证落盘会让下次启动永远卡在"恢复中"；同时不能破坏已有状态
        var state = f.Store.Load();
        Assert.Null(state.Credential);
        Assert.Equal("keep-me", state.GetUpdatesBuf);
    }

    [Fact]
    public async Task NetworkFailureBecomesFailedPhaseNotException()
    {
        using var dir = new TempDir();
        var f = Build(dir);
        f.Handler.EnqueueThrow(new HttpRequestException("connection refused"));

        var bound = await f.Flow.BindAsync("tui", TestContext.Current.CancellationToken);

        Assert.False(bound);
        Assert.Equal(WeChatPhase.Failed, f.Status.Snapshot.Phase);
        Assert.Contains("connection refused", f.Status.Snapshot.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationRethrowsAndResetsToIdle()
    {
        using var dir = new TempDir();
        var f = Build(dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Flow.BindAsync("tui", cts.Token)
        );

        Assert.Equal(WeChatPhase.Idle, f.Status.Snapshot.Phase);
    }
}
