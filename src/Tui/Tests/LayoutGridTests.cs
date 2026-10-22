using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 布局回归测试：验证 FixedRatioPanel 的垂直/水平比例分配。
/// 背景：XenoAtom Grid 的 Star 权重被忽略（所有 Star 行均分空间）——实测
/// Star(0.65)/Star(0.35) 得到 50/50，Star(0.80)/Star(0.20) 得到 50/50。
/// 因此 80/20、65/35 这类比例必须由 FixedRatioPanel 自行计算（本测试锁定该行为）。
/// </summary>
public sealed class LayoutGridTests
{
    [Fact]
    public void LeftColumn_Vertical080_20_Proportional()
    {
        // 左列：对话区 80% / 输入区 20%，50 行 → 40/10
        var conversation = new BackgroundPanel();
        var input = new BackgroundPanel();

        var panel = new FixedRatioPanel(conversation, input)
        {
            Ratio = 0.80,
            Direction = RatioPanelDirection.Vertical,
        };

        panel.Measure(new LayoutConstraints(0, 100, 0, 50));
        panel.Arrange(new Rectangle(0, 0, 100, 50));

        Assert.Equal(40, conversation.Bounds.Height);
        Assert.Equal(10, input.Bounds.Height);
        Assert.Equal(100, conversation.Bounds.Width);
    }

    [Fact]
    public void RightColumn_Vertical065_35_WithDivider_ReservesGapRow()
    {
        // 右列：SetUpPanel 65% / TodoPanel 35%，40 行 + DividerColor →
        // 26 / 分隔线 1 行（#3C3836）/ 13。线必须在两个子面板之间、不被背景覆盖。
        var setup = new BackgroundPanel();
        var todo = new BackgroundPanel();

        var panel = new FixedRatioPanel(setup, todo)
        {
            Ratio = 0.65,
            Direction = RatioPanelDirection.Vertical,
            DividerColor = Color.Rgb(0x3C, 0x38, 0x36),
        };

        panel.Measure(new LayoutConstraints(0, 40, 0, 40));
        panel.Arrange(new Rectangle(0, 0, 40, 40));

        Assert.Equal(26, setup.Bounds.Height);
        Assert.Equal(27, todo.Bounds.Y); // 让出 1 行给分隔线
        Assert.Equal(13, todo.Bounds.Height);
    }

    [Fact]
    public void MainContent_Horizontal070_30_Proportional()
    {
        // 主区：左 70% / 右 30%，100 宽 → 70/30（DividerColor 未设置 → 无 gap）
        var left = new BackgroundPanel();
        var right = new BackgroundPanel();

        var panel = new FixedRatioPanel(left, right)
        {
            Ratio = 0.70,
            Direction = RatioPanelDirection.Horizontal,
        };

        panel.Measure(new LayoutConstraints(0, 100, 0, 40));
        panel.Arrange(new Rectangle(0, 0, 100, 40));

        Assert.Equal(70, left.Bounds.Width);
        Assert.Equal(30, right.Bounds.Width);
        Assert.Equal(40, left.Bounds.Height);
    }

    [Fact]
    public void MainContent_Horizontal070_WithDivider_ReservesGapColumn()
    {
        // 主区 + DividerColor：左 70 / 分隔线 1 列 / 右 29。竖线可见（不被覆盖）。
        var left = new BackgroundPanel();
        var right = new BackgroundPanel();

        var panel = new FixedRatioPanel(left, right)
        {
            Ratio = 0.70,
            Direction = RatioPanelDirection.Horizontal,
            DividerColor = Color.Rgb(0x3C, 0x38, 0x36),
        };

        panel.Measure(new LayoutConstraints(0, 100, 0, 40));
        panel.Arrange(new Rectangle(0, 0, 100, 40));

        Assert.Equal(70, left.Bounds.Width);
        Assert.Equal(71, right.Bounds.X); // 让出 1 列给分隔线
        Assert.Equal(29, right.Bounds.Width);
    }

    [Fact]
    public void Vertical_InputRow_DoesNotExplodeOnTallTerminal()
    {
        // 高终端回归：100 行窗口，输入区 20% = 20 行（不是 Grid 均分的 50 行）
        var conversation = new BackgroundPanel();
        var input = new BackgroundPanel();

        var panel = new FixedRatioPanel(conversation, input)
        {
            Ratio = 0.80,
            Direction = RatioPanelDirection.Vertical,
        };

        panel.Measure(new LayoutConstraints(0, 100, 0, 100));
        panel.Arrange(new Rectangle(0, 0, 100, 100));

        Assert.Equal(20, input.Bounds.Height);
        Assert.Equal(80, conversation.Bounds.Height);
    }
}
