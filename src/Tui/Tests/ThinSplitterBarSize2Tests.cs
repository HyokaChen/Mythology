using System.Reflection;
using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;
using Xunit;

namespace Tui.Tests;

public sealed class ThinSplitterBarSize2Tests
{
    private static readonly Color ConvBg = Color.Rgb(0x1F, 0x1F, 0x1F);
    private static readonly Color SideBg = Color.Rgb(0x5B, 0x3A, 0x29);

    private static Style GetCellStyle(CellBuffer buffer, int x, int y)
    {
        var cellsField = typeof(CellBuffer).GetField(
            "_cells",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var cells = (Style[])cellsField.GetValue(buffer)!;
        return cells[y * buffer.Width + x];
    }

    private static CellBuffer RenderToBuffer(
        Visual visual,
        int width,
        int height,
        bool clearWithConvBg = false
    )
    {
        var constraints = new LayoutConstraints(0, width, 0, 9999);
        visual.Measure(constraints);
        visual.Arrange(new Rectangle(0, 0, width, height));

        var buffer = new CellBuffer(width, height);
        var clearStyle = clearWithConvBg ? Style.None.WithBackground(ConvBg) : Style.None;
        buffer.Clear(clearStyle);
        var renderTreeMethod = typeof(Visual).GetMethod(
            "RenderTree",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        renderTreeMethod.Invoke(visual, [buffer]);
        return buffer;
    }

    private static string BgHex(Style style)
    {
        if (!style.TryGetBackground(out var bg))
            return "none";
        var rgb = bg.ToRgb();
        return $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
    }

    [Fact]
    public void Horizontal_BarSize2_ExactlyTwoBarColumns()
    {
        // Test: Horizontal splitter with BarSize=2
        // Left child: 70% width, BackgroundPanel with ConvBg fill
        // Right child: 30% width, BackgroundPanel with SideBg fill
        var leftPanel = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(ConvBg),
            Content = new Markup("Left"),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        var rightPanel = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(SideBg),
            Content = new Markup("Right"),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var splitter = new ThinSplitter(leftPanel, rightPanel)
        {
            Ratio = 0.70,
            Direction = SplitDirection.Horizontal,
            BackgroundColor = ConvBg,
            BarSize = 2,
        };

        var width = 100;
        var height = 50;
        var buffer = RenderToBuffer(splitter, width, height, clearWithConvBg: true);

        // Verify layout
        var barStartX = leftPanel.Bounds.Right;
        var barEndX = rightPanel.Bounds.Left - 1;
        Assert.Equal(2, barEndX - barStartX + 1);

        // Dump bg colors around the bar area
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== HORIZONTAL SPLITTER LAYOUT ===");
        sb.AppendLine($"LeftPanel: {leftPanel.Bounds}");
        sb.AppendLine($"Bar: x={barStartX}..{barEndX} ({barEndX - barStartX + 1} cols)");
        sb.AppendLine($"RightPanel: {rightPanel.Bounds}");
        sb.AppendLine();

        // For each bar column, dump bg colors along the entire height
        for (var x = barStartX - 1; x <= barEndX + 1; x++)
        {
            if (x < 0 || x >= width)
                continue;
            var colors = new HashSet<string>();
            for (var y = 0; y < height; y++)
                colors.Add(BgHex(GetCellStyle(buffer, x, y)));
            var marker = x >= barStartX && x <= barEndX ? " <<< BAR" : "";
            sb.AppendLine($"x={x:D2}: [{string.Join(", ", colors)}]{marker}");
        }

        // Verify: bar columns should be ALL ConversationBg
        for (var x = barStartX; x <= barEndX; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var bg = BgHex(GetCellStyle(buffer, x, y));
                Assert.True(bg == "#1F1F1F", $"Bar cell ({x},{y}) has bg={bg}, expected #1F1F1F");
            }
        }

        // Verify: left panel area should be ALL ConversationBg
        for (var y = 0; y < height; y++)
        for (var x = 0; x < leftPanel.Bounds.Width; x++)
        {
            var bg = BgHex(GetCellStyle(buffer, x, y));
            Assert.True(
                bg == "#1F1F1F",
                $"Left panel cell ({x},{y}) has bg={bg}, expected #1F1F1F"
            );
        }

        // Verify: right panel area should be ALL SidePanelBg
        for (var y = 0; y < height; y++)
        for (var x = rightPanel.Bounds.X; x < width; x++)
        {
            var bg = BgHex(GetCellStyle(buffer, x, y));
            Assert.True(
                bg == "#5B3A29",
                $"Right panel cell ({x},{y}) has bg={bg}, expected #5B3A29"
            );
        }

        Console.Error.WriteLine(sb.ToString());
    }
}
