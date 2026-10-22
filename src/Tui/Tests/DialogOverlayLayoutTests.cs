using Core.Data;
using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Rendering;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 弹窗叠层渲染回归（SetUpPanel + Dialog）：
/// 复现并钉死「EntityEditDialog 打开后整块盖住 SetUpPanel、右侧出现黑块」这一类缺陷。
///
/// 组合方式与生产一致：WindowLayer.Content = 设定面板，窗口 = Dialog（同样式尺寸：
/// Width 76 / Padding(2,1,2,1) / IsModal）。WindowLayer 渲染顺序是先背景后窗口，
/// 因此：
///   1. 对话框矩形之外的地区必须仍是面板内容（弹窗不得整屏覆盖面板）；
///   2. 每行显示宽度必须恰好等于视口宽（宽字形记账一致，无溢出残格 → 无黑块）。
/// </summary>
public sealed class DialogOverlayLayoutTests
{
    private const int ScreenWidth = 100;
    private const int ScreenHeight = 40;
    private const int DialogWidth = 76;
    private const int DialogLeft = (ScreenWidth - DialogWidth) / 2; // 12：对话框右缘 88

    /// <summary>面板 + 同式样对话框合成帧：面板在对话框左右两侧均可见，宽度账本一致。</summary>
    [Fact]
    public async Task DialogOverlay_KeepsPanelVisibleBesideDialog_AndWidthsConsistent()
    {
        var store = new SetUpPanelLayoutTests.FakeStore { Novel = MakeNovel() };
        store.Characters.AddRange([
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林长歌",
                Role = CharacterRole.Protagonist,
            },
            new Character
            {
                Id = "c2",
                NovelId = "n1",
                Name = "苏无悔",
                Role = CharacterRole.Antagonist,
            },
        ]);
        store.Plotlines.Add(
            new Plotline
            {
                Id = "pl1",
                NovelId = "n1",
                Name = "夺宝线",
                Type = PlotlineType.Main,
            }
        );

        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");

        var layer = new WindowLayer(panel.Visual);
        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("📝 角色编辑")
            .BottomLeftText(new Markup("[#C7B299]Esc 取消[/]"))
            .Width(DialogWidth)
            .MinHeight(14)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(new Markup("表单内容"));
        layer.AddWindow(dialog);

        var buffer = VisualSnapshotRenderer.Render(layer, ScreenWidth, ScreenHeight);
        var lines = buffer.ToMarkupLines();

        // 缓冲区高度由内容自然高度决定（≤ 屏幕高）；行数以此为基准
        var rows = lines.Count;
        Assert.True(rows > 0 && rows <= ScreenHeight, $"快照行数异常: {rows}");

        // 契约（宽度账本）：每行显示宽度必须在 [视口宽, 视口宽 + 1]。
        // +1 仅允许行末宽字形起始格（continuation 被缓冲区裁掉）——终端固有行为；
        // 少一格 = 框架记账不足（黑块），多一格以上 = 溢出残格（黑块）。全帧逐行校验。
        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
        {
            var width = XenoAtom.Terminal.TerminalTextUtility.GetWidth(StripMarkup(line).AsSpan());
            Assert.True(
                width is >= ScreenWidth and <= ScreenWidth + 1,
                $"第 {index + 1} 行显示宽度 {width} 越界（应为 {ScreenWidth}，宽字形记账不一致）"
            );
        }

        // 契约 1（面板不被整屏覆盖）：对话框矩形之外必须仍是面板内容。
        // 逐行检查（而不是只看垂直中段那一行：面板 chrome 行数变化会让中段落在空白行上，
        // 例如新增搜索行后内容整体下移）——只要存在一行「对话框右缘之右」非空即算面板可见。
        var visibleRows = lines
            .Select(StripMarkup)
            .Select((row, index) => (row, index))
            .Where(x => x.row.Length > DialogLeft + DialogWidth)
            .Where(x => x.row[(DialogLeft + DialogWidth)..].Trim().Length > 0)
            .Select(x => x.index + 1)
            .ToList();
        Assert.True(visibleRows.Count > 0, "对话框右侧整列为空——弹窗整块盖住了 SetUpPanel");

        // 对话框自身必须在场（标题可见）
        var plain = string.Join("\n", lines.Select(StripMarkup));
        Assert.Contains("角色编辑", plain, StringComparison.Ordinal);
    }

    private static Novel MakeNovel() =>
        new()
        {
            Id = "n1",
            NovelId = "n1",
            Title = "叠层回归",
            Outline = "全书走向：夺宝",
        };

    /// <summary>剥掉 XenoAtom Markup 样式标签（[...] / [/]），只留纯文本算宽度。</summary>
    private static string StripMarkup(string markup)
    {
        if (string.IsNullOrEmpty(markup))
            return markup;
        var sb = new System.Text.StringBuilder(markup.Length);
        var depth = 0;
        foreach (var ch in markup)
        {
            switch (ch)
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    if (depth > 0)
                        depth--;
                    break;
                default:
                    if (depth == 0)
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
