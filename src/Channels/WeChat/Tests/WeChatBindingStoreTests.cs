// 功能：绑定状态落盘测试 —— 往返、损坏容错、local_token 累积、原子写

using Xunit;

namespace Channels.WeChat.Tests;

public class WeChatBindingStoreTests
{
    private static WeChatCredential SampleCredential =>
        new()
        {
            BaseUrl = "https://ilinkai.weixin.qq.com",
            BotToken = "tk-secret",
            AccountId = "abc@im.bot",
            PeerUserId = "wxid_me",
            BoundAt = DateTimeOffset.UnixEpoch,
        };

    [Fact]
    public void RoundTripsCredentialAndCursor()
    {
        using var dir = new TempDir();
        var store = new WeChatBindingStore(dir.Path);

        store.Save(
            new WeChatBindingState
            {
                Credential = SampleCredential,
                GetUpdatesBuf = "buf-7",
                ContextToken = "ctx-7",
                TypingTicket = "ticket-7",
                LocalTokens = ["tk-secret"],
                OwnerLabel = "tui",
            }
        );

        var loaded = new WeChatBindingStore(dir.Path).Load();

        Assert.True(loaded.IsBound);
        Assert.Equal("tk-secret", loaded.Credential!.BotToken);
        Assert.Equal("wxid_me", loaded.Credential.PeerUserId);
        Assert.Equal("buf-7", loaded.GetUpdatesBuf);
        Assert.Equal("ctx-7", loaded.ContextToken);
        Assert.Equal("ticket-7", loaded.TypingTicket);
        Assert.Equal(["tk-secret"], loaded.LocalTokens);
        Assert.Equal("tui", loaded.OwnerLabel);
    }

    [Fact]
    public void MissingFileIsEmptyState()
    {
        using var dir = new TempDir();

        var loaded = new WeChatBindingStore(dir.Path).Load();

        Assert.False(loaded.IsBound);
        Assert.Null(loaded.Credential);
        Assert.Empty(loaded.LocalTokens);
    }

    [Fact]
    public void CorruptFileDegradesToEmptyInsteadOfThrowing()
    {
        // 损坏的文件只该让用户重扫一次码，不该让通道起不来
        using var dir = new TempDir();
        var store = new WeChatBindingStore(dir.Path);
        File.WriteAllBytes(store.FilePath, [0x00, 0x01, 0xFF, 0xFE, 0x7B, 0x7D]);

        var loaded = store.Load();

        Assert.False(loaded.IsBound);
    }

    [Fact]
    public void HalfCredentialIsNotBound()
    {
        using var dir = new TempDir();
        var store = new WeChatBindingStore(dir.Path);

        store.Save(new WeChatBindingState { Credential = SampleCredential with { BotToken = "" } });

        Assert.False(store.Load().IsBound);
    }

    [Fact]
    public void ClearRemovesFileAndIsIdempotent()
    {
        using var dir = new TempDir();
        var store = new WeChatBindingStore(dir.Path);
        store.Save(new WeChatBindingState { Credential = SampleCredential });
        Assert.True(File.Exists(store.FilePath));

        store.Clear();
        store.Clear();

        Assert.False(File.Exists(store.FilePath));
        Assert.False(store.Load().IsBound);
    }

    [Fact]
    public void LocalTokensAccumulateNewestFirstWithoutDuplicates()
    {
        var state = new WeChatBindingState();

        state = state.WithLocalToken("tk-1");
        state = state.WithLocalToken("tk-2");
        state = state.WithLocalToken("tk-1");

        Assert.Equal(["tk-2", "tk-1"], state.LocalTokens);
    }

    [Fact]
    public void LocalTokensAreCappedKeepingNewest()
    {
        var state = new WeChatBindingState();
        for (var i = 0; i < 15; i++)
            state = state.WithLocalToken($"tk-{i}");

        Assert.Equal(10, state.LocalTokens.Count);
        Assert.Equal("tk-14", state.LocalTokens[0]);
        Assert.DoesNotContain("tk-0", state.LocalTokens);
    }

    [Fact]
    public void BlankLocalTokenIsIgnored()
    {
        Assert.Empty(new WeChatBindingState().WithLocalToken("   ").LocalTokens);
    }

    [Fact]
    public void SaveLeavesNoTemporaryFileBehind()
    {
        using var dir = new TempDir();
        var store = new WeChatBindingStore(dir.Path);

        store.Save(new WeChatBindingState { Credential = SampleCredential });

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void SaveOverwritesPreviousState()
    {
        using var dir = new TempDir();
        var store = new WeChatBindingStore(dir.Path);

        store.Save(new WeChatBindingState { Credential = SampleCredential, GetUpdatesBuf = "old" });
        store.Save(new WeChatBindingState { Credential = SampleCredential, GetUpdatesBuf = "new" });

        Assert.Equal("new", store.Load().GetUpdatesBuf);
        Assert.Single(Directory.GetFiles(dir.Path, "binding.json"));
    }
}
