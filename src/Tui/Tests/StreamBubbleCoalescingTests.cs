using Tui.Controls;
using XenoAtom.Terminal.UI.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 流式气泡合并回归：assistant 增量按 16 字符批量重建，
/// thinking 保持全量、不截断；最终化必须无条件冲刷短缓冲。
/// </summary>
public sealed class StreamBubbleCoalescingTests
{
    [Fact]
    public void AssistantUnder16Chars_DoesNotCreateBubble_ThenCreatesAt16()
    {
        var flow = new DocumentFlow();
        var manager = new StreamBubbleManager(flow, PanelThemes.Dark);

        manager.AppendAssistantDelta(new string('a', 15));
        Assert.Empty(flow.Items);

        manager.AppendAssistantDelta("b");
        Assert.Single(flow.Items);

        manager.AppendAssistantDelta(new string('c', 15));
        Assert.Single(flow.Items);

        manager.AppendAssistantDelta("d");
        Assert.Single(flow.Items);
    }

    [Fact]
    public void FinalizeAssistant_FlushesShortBuffer()
    {
        var flow = new DocumentFlow();
        var manager = new StreamBubbleManager(flow, PanelThemes.Dark);

        manager.AppendAssistantDelta("short");
        Assert.Empty(flow.Items);

        manager.FinalizeAssistant();

        var item = Assert.Single(flow.Items);
        var doc = Assert.IsType<FlowDocument>(item.Content);
        var block = Assert.Single(Enumerable.Range(0, doc.BlockCount).Select(doc.GetBlock));
        var markup = Assert.IsType<Markup>(block.CreateVisual());
        Assert.Contains("short", markup.Text);
    }

    [Fact]
    public void LongThinking_IsRenderedInFullAfterFinalization()
    {
        var flow = new DocumentFlow();
        var manager = new StreamBubbleManager(flow, PanelThemes.Dark);

        var lines = Enumerable.Range(0, 200).Select(i => $"thinking {i}").ToArray();
        manager.UpdateThinking(string.Join('\n', lines));
        manager.AppendAssistantDelta("done");
        manager.FinalizeAssistant();

        var item = Assert.Single(flow.Items);
        var doc = Assert.IsType<FlowDocument>(item.Content);
        var collapsible = Assert.IsType<Collapsible>(doc.GetBlock(0).CreateVisual());
        var markup = Assert.IsType<Markup>(collapsible.Content);

        Assert.Contains("thinking 0", markup.Text);
        Assert.Contains("thinking 199", markup.Text);
        Assert.Equal(200, markup.Text!.Split('\n').Length);
    }
}
