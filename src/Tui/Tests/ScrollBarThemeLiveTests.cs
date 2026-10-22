using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Threading;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 滚动条上色回归锁（用户实机报障：「鼠标从对话框移到背板就出现黑条、切 tab 换位置」）。
///
/// 根因：框架 <c>ScrollBarStyle.Default</c> / <c>ScrollViewerStyle.Default</c> 的
/// <c>TrackStyle</c>/<c>ThumbStyle</c> 都是 <c>null</c>，滚动条的轨道/滑块格落到终端默认样式，
/// 在深色主题下就是一条黑竖条；内容超长的页面（对话框 tab、多行输入框）滚动条一出现就能看到，
/// hover 显隐 + 切换 tab 换字段 ⇒ 位置看起来在变。
/// 实测（未注入样式）：滚动条 6 个字形格（█/░）fg/bg 全为 none。
///
/// 锁法：按生产顺序注入 <see cref="PanelThemes.InjectOverlayStyles"/>，断言帧内**滚动条字形格**
/// 一律带调色板显式前景与底色（<see cref="PanelColors.ScrollbarTrack"/> /
/// <see cref="PanelColors.ScrollbarThumb"/>），不得有终端默认样式格。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class ScrollBarThemeLiveTests : TerminalUiFixture
{
    private const int W = 60;
    private const int H = 16;

    private PanelColors _previousTheme = null!;

    protected override TerminalSize Size => new(W, H);

    protected override TerminalAppOptions CreateOptions() =>
        new()
        {
            HostKind = TerminalHostKind.Fullscreen,
            EnableMouse = true,
            MouseMode = TerminalMouseMode.Move,
            DisableInputEcho = true,
            UpdateWaitDuration = TimeSpan.FromMilliseconds(1),
        };

    protected override Visual CreateRoot()
    {
        // 主题是全局静态态：先存旧值，退出时还原（避免污染后续测试类）。
        _previousTheme = PanelThemes.Current;
        PanelThemes.Current = PanelThemes.Dark;
        var root = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(PanelThemes.Dark.ConversationBg),
            HorizontalAlignment = Align.Stretch,
        };
        // 生产顺序：主题样式先注入（ThemeService.Apply → InjectOverlayStyles），滚动视图后建
        PanelThemes.InjectOverlayStyles(root, PanelThemes.Dark);

        var lines = Enumerable
            .Range(1, 40)
            .Select(i => $"第 {i} 行内容 —— 滚动条应当上色")
            .ToArray();
        root.Content = new ScrollViewer(
            new VStack(lines.Select(l => (Visual)new TextBlock(l)).ToArray())
        )
        {
            MaxHeight = 6,
        };
        return root;
    }

    protected override async ValueTask OnStartedAsync() => await Task.Delay(500);

    protected override ValueTask OnStoppingAsync()
    {
        PanelThemes.Current = _previousTheme;
        return ValueTask.CompletedTask;
    }

    /// <summary>框架默认无滚动条样式 ⇒ 未注入时滚动条格必然是终端默认（黑）。</summary>
    [Fact]
    public void FrameworkDefaults_LeaveScrollBarUnstyled()
    {
        Assert.Null(ScrollBarStyle.Default.TrackStyle);
        Assert.Null(ScrollBarStyle.Default.ThumbStyle);
        Assert.Null(ScrollViewerStyle.Default.TrackStyle);
        Assert.Null(ScrollViewerStyle.Default.ThumbStyle);
    }

    [Fact]
    public async Task ScrollBar_CellsCarryThemeColors()
    {
        var frame = await FrameAsync();
        var (unstyled, seen, dump) = InspectScrollBar(frame);

        Assert.True(seen > 0, $"帧内没有滚动条字形格（█/░），测试前提不成立：\n{dump}");
        Assert.True(
            unstyled == 0,
            $"滚动条有 {unstyled}/{seen} 个字形格落到终端默认样式（黑条）：\n{dump}"
        );
    }

    private (int Unstyled, int Seen, string Dump) InspectScrollBar(IReadOnlyList<string> frame)
    {
        var p = PanelThemes.Dark;
        var allowed = new HashSet<string> { Format(p.ScrollbarTrack), Format(p.ScrollbarThumb) };
        var sb = new StringBuilder();
        var unstyled = 0;
        var seen = 0;
        for (var y = 0; y < frame.Count; y++)
        {
            var (bgs, fgs, text) = Cells(frame[y]);
            for (var x = 0; x < text.Length && x < bgs.Count && x < fgs.Count; x++)
            {
                if (text[x] is not ('█' or '░' or '│' or '▌' or '▐'))
                    continue;
                seen++;
                var ok = bgs[x] != "none" && fgs[x] != "none" && allowed.Contains(bgs[x]);
                if (!ok)
                    unstyled++;
                sb.Append(
                    $"  ({x},{y}) '{text[x]}' fg={fgs[x]} bg={bgs[x]} {(ok ? "✓" : "✗ 非主题色")}\n"
                );
            }
        }
        return (unstyled, seen, sb.ToString());
    }

    private static string Format(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}".ToLowerInvariant();

    private static (List<string> Bg, List<string> Fg, string Text) Cells(string line)
    {
        var bgs = new List<string>();
        var fgs = new List<string>();
        var text = new StringBuilder();
        var bg = "none";
        var fg = "none";
        var inTok = false;
        var tok = "";
        foreach (var ch in line)
        {
            if (ch == '[' && !inTok)
            {
                inTok = true;
                tok = "";
                continue;
            }
            if (ch == ']' && inTok)
            {
                inTok = false;
                var mb = Regex.Match(tok, @"on (#[0-9a-fA-F]{6})");
                bg = mb.Success ? mb.Groups[1].Value : "none";
                var mf = Regex.Match(tok, @"^(#[0-9a-fA-F]{6})");
                fg = mf.Success ? mf.Groups[1].Value : "none";
                continue;
            }
            if (inTok)
            {
                tok += ch;
                continue;
            }
            bgs.Add(bg);
            fgs.Add(fg);
            text.Append(ch);
        }
        return (bgs, fgs, text.ToString());
    }

    private async Task<IReadOnlyList<string>> FrameAsync() =>
        await OnUiAsync(() => GetRenderBuffer(App).ToMarkupLines());

    private static CellBuffer GetRenderBuffer(TerminalApp app) =>
        (CellBuffer)
            typeof(TerminalApp)
                .GetField("_renderBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(app)!;
}
