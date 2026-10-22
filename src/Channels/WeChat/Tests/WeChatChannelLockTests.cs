// 功能：单实例锁测试 —— 独占、释放后可再取、诊断信息写入

using Xunit;

namespace Channels.WeChat.Tests;

public class WeChatChannelLockTests
{
    [Fact]
    public void SecondAcquireIsRefused()
    {
        // 同一个 bot 双进程轮询 = 消息重复或丢失。第二次必须拿不到，且是"返回 null"而不是抛异常
        using var dir = new TempDir();

        using var first = WeChatChannelLock.TryAcquire(dir.Path, "tui");
        var second = WeChatChannelLock.TryAcquire(dir.Path, "desktop");

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void AcquireSucceedsAfterRelease()
    {
        using var dir = new TempDir();

        var first = WeChatChannelLock.TryAcquire(dir.Path, "tui");
        Assert.NotNull(first);
        first.Dispose();

        using var second = WeChatChannelLock.TryAcquire(dir.Path, "desktop");
        Assert.NotNull(second);
    }

    [Fact]
    public void DoubleDisposeIsSafe()
    {
        using var dir = new TempDir();

        var handle = WeChatChannelLock.TryAcquire(dir.Path, "tui");
        handle!.Dispose();
        handle.Dispose();

        using var next = WeChatChannelLock.TryAcquire(dir.Path, "desktop");
        Assert.NotNull(next);
    }

    [Fact]
    public void LockFileRecordsOwnerForDiagnostics()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "channel.lock");

        WeChatChannelLock.TryAcquire(dir.Path, "desktop")!.Dispose();

        // 必须释放后再读：持锁期间连本进程的第二个句柄都打不开（这正是锁生效的证据）
        var content = File.ReadAllText(path);
        Assert.Contains("\"owner\":\"desktop\"", content, StringComparison.Ordinal);
        Assert.Contains(Environment.ProcessId.ToString(), content, StringComparison.Ordinal);
    }

    [Fact]
    public void HoldingLockBlocksReadFromAnotherHandle()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "channel.lock");

        using var handle = WeChatChannelLock.TryAcquire(dir.Path, "tui");

        Assert.Throws<IOException>(() => File.ReadAllText(path));
    }

    [Fact]
    public void CreatesDirectoryWhenMissing()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "wechat");

        using var handle = WeChatChannelLock.TryAcquire(nested, "tui");

        Assert.NotNull(handle);
        Assert.True(File.Exists(Path.Combine(nested, "channel.lock")));
    }

    [Fact]
    public void LockFileSurvivesRelease()
    {
        // 不删锁文件是有意的：删除与加锁之间存在竞态，留着更安全
        using var dir = new TempDir();

        WeChatChannelLock.TryAcquire(dir.Path, "tui")!.Dispose();

        Assert.True(File.Exists(Path.Combine(dir.Path, "channel.lock")));
    }
}
