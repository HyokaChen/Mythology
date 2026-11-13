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
    public void OtherEvents_AreForwardedAsIs()
    {
        var evt = new AgentStart();

        Assert.Same(evt, DesktopAgentBridge.SnapshotForUi(evt));
    }
}
