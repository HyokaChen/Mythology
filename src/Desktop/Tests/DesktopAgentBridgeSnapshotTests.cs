using Core.Agent;
using Desktop.Services;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;

namespace Desktop.Tests;

public sealed class DesktopAgentBridgeSnapshotTests
{
    [Fact]
    public void UserMessageStart_IsSnapshottedBeforeRuntimeRefTagMutation()
    {
        var original = new ChatMessage(ChatMessageRoles.User, "章节[ID:c1]:寒意再起");
        var evt = new MessageStart(original);

        var snapshot = Assert.IsType<MessageStart>(DesktopAgentBridge.SnapshotForUi(evt));
        original.Content = "[m0003] 章节[ID:c1]:寒意再起";

        Assert.Equal("章节[ID:c1]:寒意再起", snapshot.Message.Content);
        Assert.NotSame(original, snapshot.Message);
    }

    [Fact]
    public void UserMessageStart_WithNullContent_SnapshotsAsEmpty()
    {
        // LlmTornado 的 Content 为 string?；User 消息虽正常非空，但快照层不能因
        // 契约外的 null 抛 NRE/CS8604，也不能把 null 带进非空构造参数。
        // 用显式 string? 局部变量 + null-forgiving 故意构造契约外的 null，避免 CS0121/CS8625。
        string? nullContent = null;
        var original = new ChatMessage(ChatMessageRoles.User, nullContent!);
        var evt = new MessageStart(original);

        var snapshot = Assert.IsType<MessageStart>(DesktopAgentBridge.SnapshotForUi(evt));

        Assert.Equal(string.Empty, snapshot.Message.Content);
        Assert.NotSame(original, snapshot.Message);
    }

    [Fact]
    public void OtherEvents_AreForwardedAsIs()
    {
        var evt = new AgentStart();

        Assert.Same(evt, DesktopAgentBridge.SnapshotForUi(evt));
    }
}
