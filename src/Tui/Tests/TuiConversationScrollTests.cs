using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// ConversationPanel 不再自行拦截滚轮；DocumentFlow 自己的 WheelScrollStep/follow-tail
/// 必须在真实终端输入管线下生效。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class TuiConversationScrollTests : TerminalUiFixture
{
    private DocumentFlow _flow = null!;

    protected override TerminalAppOptions CreateOptions() =>
        new() { HostKind = TerminalHostKind.Fullscreen, EnableMouse = true };

    protected override Visual CreateRoot()
    {
        _flow = new DocumentFlow
        {
            FollowTail = true,
            ItemSpacing = 0,
            WheelScrollStep = 4,
        };

        for (var i = 0; i < 80; i++)
        {
            _flow.Items.Add(
                new DocumentFlowItem
                {
                    Content = new FlowDocument().Add(new Markup($"item {i}")),
                    Alignment = DocumentFlowAlignment.Stretch,
                }
            );
        }

        return _flow;
    }

    [Fact]
    public async Task WheelUp_DisablesFollowTail_AndAppendKeepsOffset()
    {
        await OnUiAsync(() =>
        {
            _flow.ScrollToTail();
            return true;
        });
        await WaitUntilAsync(
            async () => await OnUiAsync(() => _flow.Scroll.OffsetY > 0),
            "滚动内容产生尾部 offset"
        );

        var tailOffset = await OnUiAsync(() => _flow.Scroll.OffsetY);
        var (x, y) = await OnUiAsync(() => (_flow.Bounds.X + 2, _flow.Bounds.Y + 2));

        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Wheel,
                Button = TerminalMouseButton.None,
                WheelDelta = 1,
                X = x,
                Y = y,
            }
        );

        await WaitUntilAsync(
            async () => await OnUiAsync(() => !_flow.FollowTail),
            "上滚后停止跟随"
        );

        var detachedOffset = await OnUiAsync(() => _flow.Scroll.OffsetY);
        Assert.True(detachedOffset < tailOffset);

        await OnUiAsync(() =>
        {
            _flow.Items.Add(
                new DocumentFlowItem
                {
                    Content = new FlowDocument().Add(new Markup("appended")),
                    Alignment = DocumentFlowAlignment.Stretch,
                }
            );
            return true;
        });

        var afterAppendOffset = await OnUiAsync(() => _flow.Scroll.OffsetY);
        Assert.InRange(afterAppendOffset, detachedOffset - 1, detachedOffset + 1);
        Assert.False(await OnUiAsync(() => _flow.FollowTail));
    }
}
