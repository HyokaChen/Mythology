using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 状态栏一次性提示：只提示一次、点击可提前关闭、计时器晚到不能覆盖后续正式状态。
/// </summary>
public sealed class TransientStatusHintTests
{
    [Fact]
    public void TryShow_OnlyOncePerBusyCycle_AndResetAllowsAgain()
    {
        var hint = new TransientStatusHint();

        Assert.True(hint.TryShow("旧诗", "旧诗全文", out var firstVersion));
        Assert.True(hint.IsVisible);
        Assert.False(hint.TryShow("新诗", null, out _));

        Assert.True(hint.Dismiss(out var text, out var tooltip));
        Assert.Equal("旧诗", text);
        Assert.Equal("旧诗全文", tooltip);
        Assert.False(hint.IsVisible);
        Assert.False(hint.TryShow("新诗", null, out _));

        hint.Reset();
        Assert.True(hint.TryShow("新诗", null, out var secondVersion));
        Assert.False(hint.TryRestore(firstVersion, out _, out _));
        Assert.True(hint.TryRestore(secondVersion, out var restored, out var restoredTooltip));
        Assert.Equal("新诗", restored);
        Assert.Null(restoredTooltip);
    }

    [Fact]
    public void Cancel_InvalidatesPendingTimer_ButStillConsumesTheBusyCycle()
    {
        var hint = new TransientStatusHint();

        Assert.True(hint.TryShow("诗", "全诗", out var version));
        hint.Cancel();

        Assert.False(hint.IsVisible);
        Assert.False(hint.TryRestore(version, out _, out _));
        Assert.False(hint.TryShow("诗", null, out _));

        hint.Reset();
        Assert.True(hint.TryShow("诗", null, out _));
    }

    [Fact]
    public void Dismiss_WhenNothingVisible_IsNoOp()
    {
        var hint = new TransientStatusHint();

        Assert.False(hint.Dismiss(out var text, out var tooltip));
        Assert.Equal("", text);
        Assert.Null(tooltip);
    }
}
