using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.Code;
using Tui.Controls;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 用户展开对话中的思考/工具块时，必须像 Desktop 一样立即中断尾部跟随；
/// 自动展开发生在创建时，不触发该路径。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class TuiConversationExpansionFollowTests : TerminalUiFixture
{
    private ConversationPanel _panel = null!;
    private DocumentFlow _flow = null!;

    protected override TerminalAppOptions CreateOptions() =>
        new() { HostKind = TerminalHostKind.Fullscreen, EnableMouse = true };

    protected override Visual CreateRoot()
    {
        _panel = new ConversationPanel(
            new AgentEventAdapter(),
            new ToolRegistry(),
            PanelThemes.Dark
        );

        for (var i = 0; i < 60; i++)
            _panel.AddSystemMessage($"history {i} " + new string('x', 60));

        _panel.RestoreMessages([
            new ChatMessage
            {
                Role = ChatMessageRoles.Assistant,
                Content = "answer",
                ReasoningContent = "thinking " + new string('y', 400),
            },
        ]);

        return _panel.Visual;
    }

    [Fact]
    public async Task UserExpand_PausesFollowTail()
    {
        _flow = await OnUiAsync(() =>
        {
            var flow = _panel.Visual.EnumerateVisualsDepthFirst().OfType<DocumentFlow>().Single();
            flow.ScrollToTail();
            return flow;
        });
        await WaitUntilAsync(
            async () => await OnUiAsync(() => _flow.Scroll.OffsetY > 0),
            "对话滚到尾部"
        );

        var collapsible = await OnUiAsync(() =>
            _panel
                .Visual.EnumerateVisualsDepthFirst()
                .OfType<Collapsible>()
                .FirstOrDefault(c => !c.IsExpanded)
        );
        Assert.NotNull(collapsible);
        Assert.True(await OnUiAsync(() => _flow.FollowTail));

        var (x, y) = await OnUiAsync(() => (collapsible!.Bounds.X + 1, collapsible.Bounds.Y));
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Down,
                Button = TerminalMouseButton.Left,
                X = x,
                Y = y,
            }
        );
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Up,
                Button = TerminalMouseButton.Left,
                X = x,
                Y = y,
            }
        );

        await WaitUntilAsync(
            async () => await OnUiAsync(() => collapsible!.IsExpanded),
            "用户展开思考块"
        );
        Assert.False(await OnUiAsync(() => _flow.FollowTail), "用户展开对话块后不得继续自动贴底");
    }
}
