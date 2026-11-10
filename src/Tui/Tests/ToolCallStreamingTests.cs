using System.Reflection;
using Core.Agent;
using Core.Tools;
using Tui.Controls;
using Tui.Services;
using Xunit;
using XenoAtom.Terminal.UI.Controls;

namespace Tui.Tests;

/// <summary>
/// 工具参数流式阶段：pending 卡必须先出现、执行开始时原地复用，
/// 并在 AgentEnd / 中断 / 隐藏工具路径上正确收口，避免永久 spinner。
/// </summary>
public sealed class ToolCallStreamingTests
{
    [Fact]
    public void PendingToolCard_IsReusedAtExecutionStart_AndClosedAtEnd()
    {
        var panel = CreatePanel(out var adapter);
        var flow = GetFlow(panel);
        var before = flow.Items.Count;

        adapter.OnAgentEvent(new ToolCallStreamStarted("call_1", "edit_entities", 0));
        adapter.DispatchAll();

        Assert.Equal(before + 1, flow.Items.Count);
        Assert.Contains("call_1", GetPendingKeys(panel));

        // provider 的真实 ToolCallId 与流式 key 不同；必须靠 ToolCallKey 复用同一张卡。
        adapter.OnAgentEvent(
            new ToolExecutionStart("provider-id", "edit_entities", """{"id":"林月如"}""")
            {
                ToolCallKey = "call_1",
            }
        );
        adapter.DispatchAll();

        Assert.Equal(before + 1, flow.Items.Count);
        Assert.DoesNotContain("call_1", GetPendingKeys(panel));
        Assert.True(GetToolCallIndices(panel).ContainsKey("call_1"));

        adapter.OnAgentEvent(
            new ToolExecutionEnd("provider-id", false, "ok") { ToolCallKey = "call_1" }
        );
        adapter.DispatchAll();

        Assert.Equal(before + 1, flow.Items.Count);
        Assert.False(GetToolCallIndices(panel).ContainsKey("call_1"));
    }

    [Fact]
    public void AgentEnd_FinalizesOrphanPendingCard()
    {
        var panel = CreatePanel(out var adapter);
        var flow = GetFlow(panel);
        var before = flow.Items.Count;

        adapter.OnAgentEvent(new ToolCallStreamStarted("call_2", "write_chapter", 0));
        adapter.DispatchAll();
        Assert.Contains("call_2", GetPendingKeys(panel));

        adapter.OnAgentEvent(new AgentEnd([]));
        adapter.DispatchAll();

        Assert.Equal(before + 1, flow.Items.Count); // 卡片保留，但已变成失败/未执行态
        Assert.DoesNotContain("call_2", GetPendingKeys(panel));
        Assert.False(GetToolCallIndices(panel).ContainsKey("call_2"));
    }

    [Fact]
    public void Interrupted_FinalizesOrphanPendingCard()
    {
        var panel = CreatePanel(out var adapter);

        adapter.OnAgentEvent(new ToolCallStreamStarted("call_3", "write_chapter", 0));
        adapter.DispatchAll();
        Assert.Contains("call_3", GetPendingKeys(panel));

        adapter.OnAgentEvent(new AgentInterrupted());
        adapter.DispatchAll();

        Assert.DoesNotContain("call_3", GetPendingKeys(panel));
        Assert.False(GetToolCallIndices(panel).ContainsKey("call_3"));
    }

    [Fact]
    public void HiddenTool_DoesNotCreatePendingCard()
    {
        var panel = CreatePanel(out var adapter);
        var flow = GetFlow(panel);
        var before = flow.Items.Count;

        adapter.OnAgentEvent(new ToolCallStreamStarted("hidden", "new_context", 0));
        adapter.DispatchAll();

        Assert.Equal(before, flow.Items.Count);
        Assert.Empty(GetPendingKeys(panel));
        Assert.False(GetToolCallIndices(panel).ContainsKey("hidden"));
    }

    private static ConversationPanel CreatePanel(out AgentEventAdapter adapter)
    {
        adapter = new AgentEventAdapter();
        return new ConversationPanel(adapter, new ToolRegistry(), PanelThemes.Dark);
    }

    private static DocumentFlow GetFlow(ConversationPanel panel)
    {
        var property = typeof(ConversationPanel).GetProperty(
            "Flow",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        return (DocumentFlow)(
            property?.GetValue(panel)
            ?? throw new InvalidOperationException("ConversationPanel.Flow not found")
        );
    }

    private static HashSet<string> GetPendingKeys(ConversationPanel panel)
    {
        var field = typeof(ConversationPanel).GetField(
            "_pendingToolKeys",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        return (HashSet<string>)(
            field?.GetValue(panel)
            ?? throw new InvalidOperationException("ConversationPanel._pendingToolKeys not found")
        );
    }

    private static Dictionary<string, int> GetToolCallIndices(ConversationPanel panel)
    {
        var field = typeof(ConversationPanel).GetField(
            "_toolCallItemIndices",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        return (Dictionary<string, int>)(
            field?.GetValue(panel)
            ?? throw new InvalidOperationException(
                "ConversationPanel._toolCallItemIndices not found"
            )
        );
    }
}
