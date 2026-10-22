// 功能：通道服务级测试 —— 双入口冲突、解绑权限、恢复语义
//
// 这些断的是"跨进程/跨入口"的行为：同一个 bot 只允许一个进程轮询，
// 违反它的后果（消息重复或丢失）在单进程测试里完全看不出来。

using Microsoft.Extensions.Http;
using Xunit;

namespace Channels.WeChat.Tests;

public class WeChatChannelServiceTests
{
    private static WeChatCredential Credential =>
        new()
        {
            BaseUrl = "https://ilinkai.weixin.qq.com",
            BotToken = "tk-1",
            AccountId = "bot@im.bot",
            PeerUserId = "wxid_me",
        };

    private static WeChatChannelService Build(TempDir dir, string owner) =>
        new(dir.Path, owner, new StubHttpClientFactory(new PathHandler()));

    private static void SeedBinding(TempDir dir) =>
        new WeChatBindingStore(dir.Path).Save(new WeChatBindingState { Credential = Credential });

    [Fact]
    public void TryRestoreWithoutBindingGoesIdle()
    {
        using var dir = new TempDir();

        var restored = Build(dir, "tui").TryRestore();

        Assert.False(restored);
    }

    [Fact]
    public void TryRestoreWithBindingTakesOwnership()
    {
        using var dir = new TempDir();
        SeedBinding(dir);
        var channel = Build(dir, "tui");

        var restored = channel.TryRestore();

        Assert.True(restored);
        Assert.True(channel.HasOwnership);
        Assert.Equal(WeChatPhase.Polling, channel.Status.Snapshot.Phase);
        Assert.Equal("bot@im.bot", channel.Status.Snapshot.AccountId);
    }

    [Fact]
    public void SecondEntryCannotTakeOverWhileFirstHoldsLock()
    {
        // 同一 bot 双进程轮询 = 消息重复或丢失
        using var dir = new TempDir();
        SeedBinding(dir);
        using var first = Build(dir, "tui");
        Assert.True(first.TryRestore());

        var second = Build(dir, "desktop");
        var restored = second.TryRestore();

        Assert.False(restored);
        Assert.False(second.HasOwnership);
        // 已绑定但抢不到权：报可操作的原因，而不是假装正常
        Assert.Equal(WeChatPhase.Failed, second.Status.Snapshot.Phase);
        Assert.Contains("另一个入口", second.Status.Snapshot.Error!, StringComparison.Ordinal);
        // 绑定信息仍然可读（卡片要能显示"已绑定"）
        Assert.Equal("bot@im.bot", second.Status.Snapshot.AccountId);
    }

    [Fact]
    public void SecondEntryTakesOverAfterFirstReleases()
    {
        using var dir = new TempDir();
        SeedBinding(dir);
        var first = Build(dir, "tui");
        first.TryRestore();
        first.Unbind(); // 释放锁 + 清凭证

        // 重新绑定后第二个入口必须能接管（锁已随 Unbind 释放）
        SeedBinding(dir);
        var second = Build(dir, "desktop");

        Assert.True(second.TryRestore());
        Assert.True(second.HasOwnership);
    }

    [Fact]
    public async Task BindFailsWhenAnotherEntryHoldsTheLock()
    {
        using var dir = new TempDir();
        SeedBinding(dir);
        using var first = Build(dir, "tui");
        first.TryRestore();

        var second = Build(dir, "desktop");
        var bound = await second.BindAsync(TestContext.Current.CancellationToken);

        Assert.False(bound);
        Assert.Contains("另一个入口", second.Status.Snapshot.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnbindWithoutOwnershipChangesNothing()
    {
        // 防止宿主忘检 HasOwnership 就删凭证，把另一个入口留在悬挂态
        using var dir = new TempDir();
        SeedBinding(dir);
        using var first = Build(dir, "tui");
        first.TryRestore();

        var second = Build(dir, "desktop");
        var unbound = second.Unbind();

        Assert.False(unbound);
        // 凭证文件必须原样留着（另一个入口还在用）
        Assert.True(File.Exists(Path.Combine(dir.Path, "binding.json")));
        Assert.True(new WeChatBindingStore(dir.Path).Load().IsBound);
    }

    [Fact]
    public void UnbindWithOwnershipClearsEverything()
    {
        using var dir = new TempDir();
        SeedBinding(dir);
        var channel = Build(dir, "tui");
        channel.TryRestore();

        var unbound = channel.Unbind();

        Assert.True(unbound);
        Assert.False(channel.State.IsBound);
        Assert.False(channel.HasOwnership);
        Assert.Equal(WeChatPhase.Idle, channel.Status.Snapshot.Phase);
        Assert.False(File.Exists(Path.Combine(dir.Path, "binding.json")));
    }

    [Fact]
    public void DisposeReleasesTheLock()
    {
        using var dir = new TempDir();
        SeedBinding(dir);
        var first = Build(dir, "tui");
        first.TryRestore();

        first.Dispose();

        // 进程退出（= Dispose）后另一个入口必须能接管
        var second = Build(dir, "desktop");
        Assert.True(second.TryRestore());
    }

    [Fact]
    public async Task SendWithoutBindingReportsNotBound()
    {
        using var dir = new TempDir();
        var channel = Build(dir, "tui");

        var outcome = await channel.SendTextAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(WeChatSendOutcome.NotBound, outcome);
    }

    [Fact]
    public async Task PollWithoutBindingReturnsEmpty()
    {
        using var dir = new TempDir();
        var channel = Build(dir, "tui");

        var poll = await channel.PollAsync(1_000, TestContext.Current.CancellationToken);

        Assert.Empty(poll.Messages);
        Assert.False(poll.BindingExpired);
    }

    [Fact]
    public async Task StaleTokenOnPollIsReported()
    {
        using var dir = new TempDir();
        SeedBinding(dir);
        var handler = new PathHandler();
        handler.FallbackJson = """{"ret":0,"errcode":-14,"errmsg":"stale"}""";
        var channel = new WeChatChannelService(dir.Path, "tui", new StubHttpClientFactory(handler));
        channel.TryRestore();

        var poll = await channel.PollAsync(1_000, TestContext.Current.CancellationToken);

        Assert.True(poll.BindingExpired);

        channel.ReportBindingExpired();
        Assert.Equal(WeChatPhase.Failed, channel.Status.Snapshot.Phase);
    }
}
