using System.Reflection;
using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Styling;
using Xunit;

namespace Tui.Tests;

public sealed class FixedRatioPanelDividerTests
{
    private static Style GetCellStyle(CellBuffer buffer, int x, int y)
    {
        var cellsField = typeof(CellBuffer).GetField(
            "_cells",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var cells = (Style[])cellsField.GetValue(buffer)!;
        return cells[y * buffer.Width + x];
    }

    /// <summary>
    /// Renders a visual into a CellBuffer of the given size, bypassing
    /// VisualSnapshotRenderer's auto-height (which uses DesiredSize.Natural
    /// and may produce a tiny buffer for flex panels).
    /// </summary>
    private static CellBuffer RenderToBuffer(Visual visual, int width, int height)
    {
        var constraints = new LayoutConstraints(0, width, 0, 9999);
        visual.Measure(constraints);
        visual.Arrange(new Rectangle(0, 0, width, height));

        var buffer = new CellBuffer(width, height);
        buffer.Clear(Style.None);
        var renderTreeMethod = typeof(Visual).GetMethod(
            "RenderTree",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        renderTreeMethod.Invoke(visual, [buffer]);
        return buffer;
    }

    [Fact]
    public void Horizontal_Divider_NotOverwrittenByChildBackgrounds()
    {
        var dividerColor = Color.Rgb(0x1F, 0x1F, 0x1F);
        var leftBg = Color.Rgb(0x2D, 0x24, 0x24);
        var rightBg = Color.Rgb(0x5B, 0x3A, 0x29);

        var leftPanel = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(leftBg),
            Content = new Markup("Left panel content with some text"),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        var rightPanel = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(rightBg),
            Content = new Markup("🎭 角色 🏰 组织 📍 地点"),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var panel = new FixedRatioPanel(leftPanel, rightPanel)
        {
            Ratio = 0.70,
            DividerColor = dividerColor,
        };

        var buffer = RenderToBuffer(panel, width: 200, height: 48);

        // Divider at column 140 — verify it has divider color, not panel colors
        for (var y = 0; y < 48; y++)
        {
            var style = GetCellStyle(buffer, 140, y);
            Assert.True(
                style.TryGetBackground(out var bg),
                $"Row {y}: divider col 140 should have a background"
            );
            var bgRgb = bg.ToRgb();

            Assert.True(
                bgRgb is { R: 0x1F, G: 0x1F, B: 0x1F },
                $"Row {y}: divider col 140 has bg=({bgRgb.R},{bgRgb.G},{bgRgb.B}), "
                    + $"expected divider color (0x1F,0x1F,0x1F). "
                    + $"Left panel? {bgRgb is { R: 0x2D, G: 0x24, B: 0x24 }} "
                    + $"Right panel? {bgRgb is { R: 0x5B, G: 0x3A, B: 0x29 }}"
            );
        }
    }

    [Fact]
    public void NoBrownLeak_IntoDividerColumn()
    {
        var dividerColor = Color.Rgb(0x1F, 0x1F, 0x1F);
        var rightBg = Color.Rgb(0x5B, 0x3A, 0x29);

        var leftPanel = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(Color.Rgb(0x2D, 0x24, 0x24)),
            Content = new Markup("Left"),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        var rightPanel = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(rightBg),
            Content = new Markup("🎭角色🏰组织📍地点📜情节🎬场"),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var panel = new FixedRatioPanel(leftPanel, rightPanel)
        {
            Ratio = 0.70,
            DividerColor = dividerColor,
        };

        var buffer = RenderToBuffer(panel, width: 200, height: 48);

        for (var y = 0; y < 48; y++)
        {
            var style = GetCellStyle(buffer, 140, y);
            Assert.True(
                style.TryGetBackground(out var bg),
                $"Row {y}: divider col 140 should have a background"
            );
            Assert.NotEqual(rightBg, bg);
        }
    }

    /// <summary>
    /// Simulates the right panel structure (FixedRatioPanel V 0.65 with SetUpPanel + TodoPanel)
    /// and checks that the entire area has proper background — no black (no-background) cells.
    /// </summary>
    [Fact]
    public void VerticalSplit_BothPanelsFillEntireArea()
    {
        var sidePanelBg = Color.Rgb(0x5B, 0x3A, 0x29); // SidePanelBg
        var conversationBg = Color.Rgb(0x1F, 0x1F, 0x1F); // ConversationBg
        var tabSelectedBg = Color.Rgb(0x6E, 0x4A, 0x36); // TabSelectedBg

        // Simulate SetUpPanel structure: outer ConversationBg panel → inner SidePanelBg panel → TabControl
        var tabContent = new BackgroundPanel
        {
            FillStyle = Style
                .None.WithBackground(sidePanelBg)
                .WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)),
            Content = new Padder
            {
                Content = new VStack(
                    new Markup("toolbar"),
                    new ScrollViewer
                    {
                        Content = new Accordion
                        {
                            new Collapsible(new Markup("item1"), new Markup("detail1"))
                            {
                                IsExpanded = true,
                            },
                        },
                        VerticalAlignment = Align.Stretch,
                        HorizontalScrollEnabled = false,
                    }
                ).Spacing(2).VerticalAlignment(Align.Stretch),
                Padding = new Thickness(1, 1, 1, 0),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            },
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var tab = new TabPage("🎭 角色", tabContent);
        var tabs = new TabControl(tab).Style(
            TabControlStyle.NoBorder with
            {
                StripStyle = Style
                    .None.WithBackground(sidePanelBg)
                    .WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)),
                TabStyle = Style
                    .None.WithBackground(sidePanelBg)
                    .WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)),
                TabSelectedStyle =
                    Style
                        .None.WithBackground(tabSelectedBg)
                        .WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) | TextStyle.Bold,
            }
        );
        tabs.HorizontalAlignment = Align.Stretch;
        tabs.VerticalAlignment = Align.Stretch;

        var sideCardPanel = new BackgroundPanel
        {
            FillStyle = Style
                .None.WithBackground(sidePanelBg)
                .WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)),
            Content = tabs,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var setUpPanel = new BackgroundPanel
        {
            FillStyle = Style
                .None.WithBackground(conversationBg)
                .WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)),
            Content = sideCardPanel,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        // Simulate TodoPanel
        var todoPanel = new BackgroundPanel
        {
            FillStyle = Style
                .None.WithBackground(sidePanelBg)
                .WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)),
            Content = new Collapsible(new Markup("📋 暂无任务"), new Markup(""))
            {
                IsExpanded = false,
                VerticalAlignment = Align.Stretch,
            },
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        // Right column: FixedRatioPanel V 0.65
        var rightColumn = new FixedRatioPanel(setUpPanel, todoPanel)
        {
            Ratio = 0.65,
            Direction = RatioPanelDirection.Vertical,
            DividerColor = conversationBg,
        };

        const int width = 59;
        const int height = 31;
        var buffer = RenderToBuffer(rightColumn, width, height);

        // Check: no cell should have no background (black/transparent)
        var blackCells = new List<(int x, int y)>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var style = GetCellStyle(buffer, x, y);
                if (!style.TryGetBackground(out _))
                {
                    blackCells.Add((x, y));
                }
            }
        }

        if (blackCells.Count > 0)
        {
            var sample = blackCells.Take(30).Select(c => $"({c.x},{c.y})");
            Assert.Fail(
                $"Found {blackCells.Count} cells with no background in right panel. "
                    + $"Sample: {string.Join(", ", sample)}"
            );
        }
    }
}
