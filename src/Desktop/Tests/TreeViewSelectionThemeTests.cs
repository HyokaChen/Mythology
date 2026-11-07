using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 大纲树选中态回归锁。
///
/// SukiUI 7.0.1 的 TreeViewItem ControlTheme 在 <c>:selected</c> 时把模板
/// <c>Border#SelectionBorder</c> 刷成 <c>SukiPrimaryColor</c>（品牌 Olive Press #2b390a）。
/// 只在 <c>/template/ ContentPresenter</c> 上叠 20% Sage Mist 会得到 #435028，
/// Forest Ink 文字对比度约 2:1，选中行不可读。
///
/// 这里同时锁两层：
/// 1. 真实模板：选中后 SelectionBorder 必须是 Sage Mist 薄雾，而不是品牌深底；
/// 2. 颜色算式：Forest Ink 叠在 Sage Mist + Bone 上仍满足 WCAG AA（≥ 4.5:1）。
/// </summary>
public sealed class TreeViewSelectionThemeTests
{
    private static readonly HeadlessUiHost Ui = HeadlessUiHost.Instance;

    [Fact]
    public void SelectedTreeRow_PaintsSageMistOnSelectionBorder()
    {
        Ui.Run(() =>
        {
            var tree = new TreeView
            {
                Width = 320,
                Height = 200,
                SelectionMode = SelectionMode.Single,
                ItemsSource = new[] { "第一卷" },
            };
            var window = new Window
            {
                Content = tree,
                Width = 360,
                Height = 240,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var item = Assert.Single(tree.GetVisualDescendants().OfType<TreeViewItem>());
            var border = Assert.Single(
                item.GetVisualDescendants().OfType<Border>(),
                b => b.Name == "SelectionBorder"
            );

            // 无头首帧还没有推进 BrushTransition 的时钟；这里先移除过渡，
            // 只验证最终选中底色与文字对比度。过渡结构由 MotionWiringTests 锁定。
            border.Transitions = null;
            item.IsSelected = true;
            Dispatcher.UIThread.RunJobs();

            var background = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
            Assert.Equal(Color.Parse("#33A5AC9F"), background.Color);

            var header = Assert.Single(
                item.GetVisualDescendants().OfType<ContentPresenter>(),
                presenter => presenter.Name == "PART_HeaderPresenter"
            );
            var headerForeground = Assert.IsAssignableFrom<ISolidColorBrush>(header.Foreground);
            // SukiUI 的普通 header 可能保留默认近黑色；用实际前景算对比度，
            // 大纲标题本身另有显式 BrushForestInk。
            var effectiveBackground = Blend(background.Color, Color.Parse("#EFF2E8"));
            var ratio = ContrastRatio(effectiveBackground, headerForeground.Color);
            Assert.True(ratio >= 4.5, $"选中行文字对比度不足：{ratio:F2}:1");
            Assert.True(
                header.Background is null || ((ISolidColorBrush)header.Background).Color.A == 0,
                "选中行 ContentPresenter 不应再叠一层背景"
            );
        });
    }

    [Fact]
    public void HoveredTreeRow_PaintsSageMistWash_OnSelectionBorderOnly()
    {
        // 用户报障：大纲树 hover 时行首是纯白浮卡（SukiUI 未选中行 :pointerover 把
        // SelectionBorder 刷成 SukiCardBackground #fdfefb），正文区再叠一层 12% 薄雾，
        // 箭头格纯白 / 内容格灰绿，成一条两截色带。
        // 锁行为：hover 的雾只落在 SelectionBorder 一层，ContentPresenter 永远透明。
        // 直接注入 :pointerover 而不模拟鼠标：共享无头宿主里，先前测试遗留的窗口会抢指针，
        // 真实 MouseMove 时灵时不灵。
        Ui.Run(() =>
        {
            var item = new TreeViewItem { Header = "第一卷" };
            var tree = new TreeView
            {
                Width = 320,
                Height = 120,
                SelectionMode = SelectionMode.Single,
            };
            tree.Items.Add(item);
            var window = new Window
            {
                Content = tree,
                Width = 360,
                Height = 200,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var border = Assert.Single(
                item.GetVisualDescendants().OfType<Border>(),
                b => b.Name == "SelectionBorder"
            );
            var header = Assert.Single(
                item.GetVisualDescendants().OfType<ContentPresenter>(),
                p => p.Name == "PART_HeaderPresenter"
            );

            // 无头首帧还没推进 BrushTransition 时钟：移除过渡，只看最终底色。
            border.Transitions = null;
            header.Transitions = null;

            ((IPseudoClasses)border.Classes).Add(":pointerover");
            Dispatcher.UIThread.RunJobs();

            var hover = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
            Assert.Equal(Color.Parse("#1FA5AC9F"), hover.Color);
            Assert.True(
                header.Background is null || ((ISolidColorBrush)header.Background).Color.A == 0,
                "hover 行 ContentPresenter 不应再叠一层背景（否则箭头格与正文区两截色）"
            );
            // 行是表不是卡：平铺雾面，不沿用 SukiUI 的圆角浮卡。
            Assert.Equal(default, border.CornerRadius);
        });
    }

    [Fact]
    public void HoveredChildRow_DoesNotWashItsParentRow()
    {
        // 指针状态会向祖先传播：悬停子行时父 TreeViewItem 也是 :pointerover。
        // 雾必须挂在 Border#SelectionBorder 自己的 :pointerover 上——挂在 TreeViewItem 上
        // 会把父行、祖父行整条链都点亮（大纲树只有叶子该亮）。
        Ui.Run(() =>
        {
            var child = new TreeViewItem { Header = "第 1 章" };
            var parent = new TreeViewItem { Header = "第 1 卷", IsExpanded = true };
            parent.Items.Add(child);
            var tree = new TreeView
            {
                Width = 320,
                Height = 160,
                SelectionMode = SelectionMode.Single,
            };
            tree.Items.Add(parent);
            var window = new Window
            {
                Content = tree,
                Width = 360,
                Height = 220,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var allBorders = tree.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Name == "SelectionBorder")
                .ToList();
            Assert.Equal(2, allBorders.Count);

            Border BorderOf(TreeViewItem owner) =>
                Assert.Single(
                    allBorders,
                    b => b.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == owner
                );

            var parentBorder = BorderOf(parent);
            var childBorder = BorderOf(child);
            parentBorder.Transitions = null;
            childBorder.Transitions = null;

            // 真实指针链：子行（Border）是命中元素，父 item 只是祖先。
            ((IPseudoClasses)childBorder.Classes).Add(":pointerover");
            ((IPseudoClasses)parent.Classes).Add(":pointerover");
            Dispatcher.UIThread.RunJobs();

            var hover = Assert.IsAssignableFrom<ISolidColorBrush>(childBorder.Background);
            Assert.Equal(Color.Parse("#1FA5AC9F"), hover.Color);
            Assert.True(
                parentBorder.Background is null
                    || ((ISolidColorBrush)parentBorder.Background).Color.A == 0,
                "悬停子行不应点亮父行"
            );
        });
    }

    [Fact]
    public void SelectedTreeRow_UsesLightWash_WithReadableForestInk()
    {
        var styles = Load("Styles", "MythologyStyles.axaml");
        foreach (
            var selector in new[]
            {
                "TreeViewItem:selected /template/ Border#SelectionBorder",
                "TreeViewItem:selected /template/ Border#SelectionBorder:pointerover",
            }
        )
        {
            var style = styles
                .Elements()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "Style"
                    && Attr(e, "Selector") == selector
                    && e.Elements()
                        .Any(s =>
                            Attr(s, "Property") == "Background"
                            && Attr(s, "Value")
                                .Contains("BrushSageMistSelected", StringComparison.Ordinal)
                        )
                );
            Assert.True(style is not null, $"缺少 {selector} 的 Sage Mist 选中底色");
        }

        var presenter = styles
            .Elements()
            .FirstOrDefault(e =>
                e.Name.LocalName == "Style"
                && Attr(e, "Selector") == "TreeViewItem:selected /template/ ContentPresenter"
                && e.Elements()
                    .Any(s =>
                        Attr(s, "Property") == "Background" && Attr(s, "Value") == "Transparent"
                    )
                && e.Elements()
                    .Any(s =>
                        Attr(s, "Property") == "Foreground"
                        && Attr(s, "Value").Contains("BrushForestInk", StringComparison.Ordinal)
                    )
            );
        Assert.True(presenter is not null, "选中行 ContentPresenter 必须透明且使用 Forest Ink");

        var theme = Load("Styles", "MythologyTheme.axaml");
        var selected = BrushColor(theme, "BrushSageMistSelected");
        var bone = BrushColor(theme, "BrushBone");
        var ink = BrushColor(theme, "BrushForestInk");
        var effective = Blend(selected, bone);
        var ratio = ContrastRatio(effective, ink);

        Assert.True(ratio >= 4.5, $"选中行 Forest Ink 对比度不足：{effective} 上仅 {ratio:F2}:1");
    }

    private static XElement Load(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, "src", "Desktop", .. segments]);
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未找到 src/Desktop/{Path.Combine(segments)}");
    }

    private static string Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    private static Color BrushColor(XElement theme, string key)
    {
        var brush = theme
            .Descendants()
            .Single(e => e.Name.LocalName == "SolidColorBrush" && Attr(e, "Key") == key);
        var color = Attr(brush, "Color");
        if (color.StartsWith("{StaticResource ", StringComparison.Ordinal))
        {
            var colorKey = color["{StaticResource ".Length..].TrimEnd('}').Trim();
            var resource = theme
                .Descendants()
                .Single(e => e.Name.LocalName == "Color" && Attr(e, "Key") == colorKey);
            color = resource.Value.Trim();
        }
        return Color.Parse(color);
    }

    private static Color Blend(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        byte Mix(byte f, byte b) => (byte)Math.Round(alpha * f + (1 - alpha) * b);
        return Color.FromArgb(
            255,
            Mix(foreground.R, background.R),
            Mix(foreground.G, background.G),
            Mix(foreground.B, background.B)
        );
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
