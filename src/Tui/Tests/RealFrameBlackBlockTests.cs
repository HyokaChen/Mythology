using System.Reflection;
using System.Text;
using Core.Data;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Threading;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 真实 App 组合帧的黑块回归（黑块 = 终端默认底透出来的未绘制格）。
///
/// **为什么用真实 TerminalApp**：早前的版本用 <see cref="VisualSnapshotRenderer"/>
/// 直接渲染「生产形状的根视觉」，但离屏渲染里 <c>ThinSplitter</c> 不参与布局 →
/// 整帧只剩 header/status 两行，而空行没有任何字符、检测器不报——守卫形同虚设
/// （2026-10-05 实测：44 行的帧只有 2 行有内容，测试仍然全绿）。现在改为在
/// InMemoryTerminalBackend 上跑真实 TerminalApp，再读它的渲染缓冲，
/// 覆盖「生产布局 + 弹窗叠层 + 鼠标悬停/焦点」这条用户报障路径。
///
/// 判定：帧内不得出现「未绘制格」——既没有字符、也没有背景令牌的格；
/// 整行如此同样算未绘制（旧检测器漏判的那一类）。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class RealFrameBlackBlockTests : IAsyncLifetime
{
    private const int W = 140;
    private const int H = 44;

    private InMemoryTerminalBackend _backend = null!;
    private TerminalApp _app = null!;
    private CancellationTokenSource _cts = null!;
    private Task _runTask = null!;
    private SetUpPanelLayoutTests.FakeStore _store = null!;
    private SetUpPanel _panel = null!;
    private ThinSplitter _main = null!;

    public async ValueTask InitializeAsync()
    {
        var theme = Tui.Services.ThemeRegistry.DesertLight;
        var colors = theme.Colors;
        PanelThemes.Current = colors;

        _store = new SetUpPanelLayoutTests.FakeStore
        {
            Novel = new Novel
            {
                Id = "n1",
                NovelId = "n1",
                Title = "黑块回归",
                Outline = "全书走向：夺宝",
            },
        };
        for (var i = 1; i <= 12; i++)
            _store.Characters.Add(
                new Character
                {
                    Id = $"c{i}",
                    NovelId = "n1",
                    Name = $"角色{i}",
                    Role = CharacterRole.Supporting,
                    Gender = "男",
                }
            );
        var longDetail = string.Concat(
            Enumerable.Repeat("体格健硕、面相忠厚，眼神坚定，待人豪迈仗义。", 24)
        );
        var character = _store.Characters[0];
        character.Personality = longDetail;
        character.Appearance = longDetail;
        character.Habits = longDetail;
        character.Background = longDetail;
        character.Motivation = longDetail;
        character.Weakness = longDetail;

        _panel = new SetUpPanel(_store, colors) { CurrentNovelId = "n1" };
        await _panel.RefreshAsync("n1");

        var left = new ThinSplitter(
            new BackgroundPanel
            {
                FillStyle = Style.None.WithBackground(colors.ConversationBg),
                Content = new Markup("对话"),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            },
            new BackgroundPanel
            {
                FillStyle = Style.None.WithBackground(colors.PromptBg),
                Content = new Markup("输入"),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
        )
        {
            Ratio = 0.7,
            Direction = SplitDirection.Vertical,
            BarSize = 0,
        };
        var right = new ThinSplitter(
            _panel.Visual,
            new BackgroundPanel
            {
                FillStyle = Style.None.WithBackground(colors.SidePanelBg),
                Content = new Markup("任务"),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
        )
        {
            Ratio = 0.6,
            Direction = SplitDirection.Vertical,
            BackgroundColor = colors.SidePanelBg,
            LineColor = colors.ConversationBg,
            HorizontalGlyph = new Rune('▄'),
            BarSize = 1,
        };
        _main = new ThinSplitter(left, right)
        {
            Ratio = 0.70,
            Direction = SplitDirection.Horizontal,
            BackgroundColor = colors.ConversationBg,
            LineColor = colors.ConversationBg,
            BarSize = 1,
        };
        var root = new Grid()
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star() },
                new RowDefinition { Height = GridLength.Auto }
            )
            .Cell(
                new BackgroundPanel
                {
                    FillStyle = Style.None.WithBackground(colors.HeaderBg),
                    Content = new Markup("header"),
                    HorizontalAlignment = Align.Stretch,
                },
                0,
                0
            )
            .Cell(_main, 1, 0)
            .Cell(
                new BackgroundPanel
                {
                    FillStyle = Style.None.WithBackground(colors.StatusBg),
                    Content = new Markup("status"),
                    HorizontalAlignment = Align.Stretch,
                },
                2,
                0
            );

        // 与生产一致：ThemeService 会把 UiTheme + 控件样式注入根视觉（弹层底色等全靠它）——
        // 不注入的话框架用自带默认主题（中性冷灰），测试就不是生产形态了。
        root.Style(theme.UiTheme);
        root.SetStyle(theme.ControlStyles.Dialog);
        root.SetStyle(theme.ControlStyles.TabControl);
        root.SetStyle(theme.ControlStyles.Collapsible);
        root.SetStyle(theme.ControlStyles.Button);
        root.SetStyle(theme.ControlStyles.Switch);
        PanelThemes.InjectOverlayStyles(root, theme.Colors);

        _backend = new InMemoryTerminalBackend(new TerminalSize(W, H), null);
        // 启动屏障：等上一个终端完全退出再顶掉全局实例（否则后续投递会落到别人的 UI 线程）
        await TerminalUiFixture.WaitForPreviousTerminalAsync();

        Terminal.Initialize(
            _backend,
            new TerminalOptions { ImplicitStartInput = true },
            force: true
        );
        _app = new TerminalApp(
            root,
            Terminal.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
                EnableMouse = true,
                MouseMode = TerminalMouseMode.Move,
            }
        );
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => _app.RunAsync(_cts.Token));
        await WaitUntilAsync(async () => await OnUiAsync(() => _main.Bounds.Width) > 0, "app 启动");
        await Task.Delay(200);
    }

    public async ValueTask DisposeAsync()
    {
        await TerminalUiFixture.StopAppAsync(_cts, _runTask);
        Terminal.Close();
    }

    /// <summary>基线：生产布局（分栏 + 面板 + 状态栏）整帧每一格都必须被画到。</summary>
    [Fact]
    public async Task Baseline_LiveApp_NoUndrawnCells()
    {
        var lines = await CaptureFrameAsync();
        var undrawn = UndrawnCells(lines);
        Assert.True(
            undrawn.Count == 0,
            $"基线存在未绘制格（黑块源）{undrawn.Count} 处，首批: {string.Join(", ", undrawn.Take(8))}"
        );
    }

    /// <summary>
    /// 用户复现路径：浅色主题下打开角色「性格描写」页（多段长文本溢出），鼠标沿弹窗右缘内外移动；
    /// 弹窗和右侧 SetUpPanel 区域都不得出现未绘制格或纯黑背景。
    /// </summary>
    [Fact]
    public async Task DialogHover_LiveApp_NoUndrawnCells()
    {
        await OnUiAsync(() =>
        {
            new EntityEditDialog(
                _store,
                EntityType.Character,
                entityId: "c1",
                novelId: "n1"
            ).Show();
            return 0;
        });
        await WaitUntilAsync(
            async () =>
                (await CaptureFrameAsync()).Any(l =>
                    l.Contains("角色编辑", StringComparison.Ordinal)
                ),
            "对话框出现"
        );

        await OnUiAsync(() =>
        {
            _app
                .Root.EnumerateVisualsDepthFirst()
                .OfType<Dialog>()
                .Single()
                .EnumerateVisualsDepthFirst()
                .OfType<TabControl>()
                .Single()
                .SelectedIndex = 1;
            return 0;
        });
        await WaitUntilAsync(
            async () =>
            {
                var text = string.Join("\n", (await CaptureFrameAsync()).Select(Plain));
                return text.Contains("性格描写", StringComparison.Ordinal)
                    && text.Contains("外貌", StringComparison.Ordinal);
            },
            "性格描写页与文本域出现"
        );

        var (dialogBounds, textAreaBounds) = await OnUiAsync(() =>
        {
            var dialog = _app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Single();
            var textArea = dialog
                .EnumerateVisualsDepthFirst()
                .OfType<TextArea>()
                .First(control => control.Bounds.Width > 0);
            return (dialog.Bounds, textArea.Bounds);
        });
        Assert.True(
            dialogBounds.Width > 0 && dialogBounds.Height > 0,
            $"对话框未布局：{dialogBounds}"
        );
        Assert.True(
            textAreaBounds.Width > 0 && textAreaBounds.Height > 0,
            $"文本域未布局：{textAreaBounds}"
        );

        for (var y = dialogBounds.Y + 1; y < dialogBounds.Bottom - 1; y += 2)
            Move(dialogBounds.Right - 1, y);
        Click(textAreaBounds.X + 1, textAreaBounds.Y + 1);
        for (var y = dialogBounds.Bottom - 1; y > dialogBounds.Y; y -= 2)
            Move(dialogBounds.Right + 1, y);
        await Task.Delay(200);

        var lines = await CaptureFrameAsync();
        var visibleText = lines.Select(Plain).ToArray();
        Assert.Contains(visibleText, line => line.Contains("性格描写", StringComparison.Ordinal));
        Assert.Contains(visibleText, line => line.Contains("外貌", StringComparison.Ordinal));
        var undrawn = UndrawnCells(lines);
        Assert.True(
            undrawn.Count == 0,
            $"对话框悬停/聚焦后存在未绘制格（黑块源）{undrawn.Count} 处，首批: {string.Join(", ", undrawn.Take(8))}"
        );
        var black = BlackCells(lines);
        Assert.True(
            black.Count == 0,
            $"浅色弹窗右缘出现纯黑背景格 {black.Count} 处，首批: {string.Join(", ", black.Take(8))}"
        );
    }

    /// <summary>
    /// 实体行 ⋯ 菜单：在「插入引用」上悬停时，弹层左侧的详情滚动条区域不得露出默认黑底。
    /// </summary>
    [Fact]
    public async Task EntityActionPopup_HoverInsertReference_NoBlackScrollbarCells()
    {
        // 大纲排第一个分区了，实体分区要显式切过去（否则折叠项没布局、点不中）
        await OnUiAsync(() =>
        {
            _panel.NavigateToEntity(EntityType.Character, "c1");
            return true;
        });
        await Task.Delay(200);

        var item = await OnUiAsync(() =>
        {
            var accordion = (Accordion)
                typeof(SetUpPanel)
                    .GetField(
                        "_characterAccordion",
                        BindingFlags.Instance | BindingFlags.NonPublic
                    )!
                    .GetValue(_panel)!;
            var first = (Collapsible)accordion.Children[0];
            return first;
        });
        var itemBounds = await OnUiAsync(() => item.Bounds);
        Click(itemBounds.X + 4, itemBounds.Y);
        await WaitUntilAsync(async () => await OnUiAsync(() => item.IsExpanded), "展开角色详情");

        var (menuLabel, scrollingViews) = await OnUiAsync(() =>
        {
            var visuals = item.EnumerateVisualsDepthFirst().ToArray();
            var tabPanel = (
                (List<BackgroundPanel>)
                    typeof(SetUpPanel)
                        .GetField(
                            "_tabContentPanels",
                            BindingFlags.Instance | BindingFlags.NonPublic
                        )!
                        .GetValue(_panel)!
            )[1]; // 大纲排第一个分区，角色内容面板是索引 1
            return (
                visuals
                    .OfType<Markup>()
                    .Single(m => m.Text?.Contains("⋯", StringComparison.Ordinal) == true),
                tabPanel
                    .EnumerateVisualsDepthFirst()
                    .OfType<ScrollViewer>()
                    .Select(s =>
                        (View: s, Bounds: s.Bounds, Range: SetUpPanel.MaxVerticalOffset(s))
                    )
                    .ToArray()
            );
        });
        var scrollingView = scrollingViews.FirstOrDefault(s => s.Range > 0);
        Assert.True(
            scrollingView.View is not null,
            $"角色列表未产生滚动条：{string.Join("; ", scrollingViews.Select(s => $"{s.Bounds}/{s.Range}"))}"
        );
        var labelBounds = await OnUiAsync(() => menuLabel.Bounds);
        Click(labelBounds.X, labelBounds.Y);
        await WaitUntilAsync(
            async () =>
                await OnUiAsync(() =>
                    typeof(SetUpPanel)
                        .GetField("_rowMenuPopup", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(_panel)
                        is not null
                ),
            "打开实体行菜单"
        );

        var popup = await OnUiAsync(() =>
            (AnchoredPopup<SetUpPanel>)
                typeof(SetUpPanel)
                    .GetField("_rowMenuPopup", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(_panel)!
        );
        var popupBounds = await OnUiAsync(() => popup.List.Bounds);
        var scrollBounds = scrollingView.Bounds;
        Move(popupBounds.X + 2, popupBounds.Y + 1); // 「📋 插入引用」
        await Task.Delay(100);
        var lines = await OnUiAsync(() =>
            (
                (CellBuffer)
                    typeof(TerminalApp)
                        .GetField("_renderBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .GetValue(_app)!
            ).ToMarkupLines()
        );
        var insertRow = lines.Single(l => Plain(l).Contains("插入引用", StringComparison.Ordinal));
        var hoveredBackground =
            $"on #{PanelThemes.Current.TabHoveredBg.R:x2}{PanelThemes.Current.TabHoveredBg.G:x2}{PanelThemes.Current.TabHoveredBg.B:x2}";
        Assert.Contains(hoveredBackground, insertRow, StringComparison.OrdinalIgnoreCase);
        var black = BlackCells(lines);
        Assert.True(
            black.Count == 0,
            $"悬停「插入引用」后露出黑底格 {black.Count} 处，首批: {string.Join(", ", black.Take(8))}; "
                + $"popup={popupBounds}, detail-scroll={scrollBounds}"
        );
    }

    /// <summary>
    /// 下拉弹层：点击对话框里的 Select（`MythSelect` → AnchoredPopup + OptionList）打开选项列表，
    /// 帧内同样不得出现未绘制格——弹层表面如果没有背景，展开瞬间就是一块黑。
    /// </summary>
    [Fact]
    public async Task DialogSelectPopup_LiveApp_NoUndrawnCells()
    {
        await OnUiAsync(() =>
        {
            new EntityEditDialog(
                _store,
                EntityType.Character,
                entityId: "c1",
                novelId: "n1"
            ).Show();
            return 0;
        });
        await WaitUntilAsync(
            async () =>
                (await CaptureFrameAsync()).Any(l =>
                    l.Contains("角色编辑", StringComparison.Ordinal)
                ),
            "对话框出现"
        );

        var probe = await CaptureFrameAsync();
        var rows = probe.Select(Plain).ToList();
        var titleRow = rows.FindIndex(r => r.Contains("角色编辑", StringComparison.Ordinal));
        var bottomRow = rows.FindIndex(r => r.Contains("Esc 取消", StringComparison.Ordinal));
        // 「性别」下拉：标签行后一行的右端（▾ 落在对话框内容右侧）
        var genderRow = rows.FindIndex(titleRow, r => r.Contains("性别", StringComparison.Ordinal));
        Assert.True(genderRow > 0 && genderRow < bottomRow, "找不到性别字段行");
        // 点「性别」行右端的 ▾（下拉箭头所在列，随主题/布局自适应）
        var genderText = rows[genderRow];
        var caretCol = genderText.LastIndexOf('▾');
        Assert.True(caretCol > 0, "找不到性别下拉箭头");
        Click(caretCol, genderRow);
        await Task.Delay(300);

        var lines = await CaptureFrameAsync();
        // 弹层确实打开了（选项「其他」只在弹层里出现）——否则本用例会假绿
        Assert.Contains(lines, l => Plain(l).Contains("其他", StringComparison.Ordinal));
        var undrawn = UndrawnCells(lines);
        Assert.True(
            undrawn.Count == 0,
            $"下拉弹层展开后存在未绘制格（黑块源）{undrawn.Count} 处，首批: {string.Join(", ", undrawn.Take(8))}"
        );

        // 弹层整块（含框架自留的边缘）必须是主题浮层色：旧实现弹层没上底色，
        // 表面透出 app 基础底、边缘用框架推导的中性冷灰（Desert 下 #1E2D37）——
        // 在暖色主题里读起来就是一块黑（用户报障）。
        var popupSurface =
            $"#{PanelThemes.Current.DialogContentBg.R:x2}{PanelThemes.Current.DialogContentBg.G:x2}{PanelThemes.Current.DialogContentBg.B:x2}";
        var popupRow = lines.First(l => Plain(l).Contains("其他", StringComparison.Ordinal));
        var longest = LongestColorRun(Backgrounds(popupRow));
        Assert.Equal(popupSurface, longest.Color);
        Assert.True(longest.Length >= 40, $"弹层宽度只有 {longest.Length} 格，不像是整块弹层");

        // 弹层表面必须是显式主题底色，不能是终端默认底（黑）——用户报障：下拉/⋯ 菜单展开一块黑
        var black = BlackCells(lines);
        Assert.True(
            black.Count == 0,
            $"下拉弹层区域出现黑底格 {black.Count} 处（弹层表面没上底色），首批: {string.Join(", ", black.Take(8))}"
        );
    }

    /// <summary>逐字符背景色（markup 行按令牌分段；无背景令牌 → none）。</summary>
    private static List<string> Backgrounds(string line)
    {
        var bgs = new List<string>();
        var bg = "none";
        var inToken = false;
        var token = "";
        foreach (var ch in line)
        {
            if (ch == '[' && !inToken)
            {
                inToken = true;
                token = "";
                continue;
            }
            if (ch == ']' && inToken)
            {
                inToken = false;
                var m = System.Text.RegularExpressions.Regex.Match(token, @"on (#[0-9a-fA-F]{6})");
                bg = m.Success ? m.Groups[1].Value : "none";
                continue;
            }
            if (inToken)
            {
                token += ch;
                continue;
            }
            bgs.Add(bg);
        }
        return bgs;
    }

    /// <summary>行内最长同色段（弹层整块的判定）。</summary>
    private static (string Color, int Length) LongestColorRun(IReadOnlyList<string> bgs)
    {
        var best = ("none", 0);
        var runStart = 0;
        for (var i = 1; i <= bgs.Count; i++)
        {
            if (i < bgs.Count && bgs[i] == bgs[runStart])
                continue;
            var length = i - runStart;
            if (bgs[runStart] != "none" && length > best.Item2)
                best = (bgs[runStart], length);
            runStart = i;
        }
        return best;
    }

    /// <summary>背景为纯黑 / 近黑的格（终端默认底被透出或被显式画成黑）。</summary>
    internal static List<string> BlackCells(IReadOnlyList<string> lines)
    {
        var bad = new List<string>();
        for (var y = 0; y < lines.Count; y++)
        {
            var bg = "";
            var inToken = false;
            var token = "";
            var col = 0;
            foreach (var ch in lines[y])
            {
                if (ch == '[' && !inToken)
                {
                    inToken = true;
                    token = "";
                    continue;
                }
                if (ch == ']' && inToken)
                {
                    inToken = false;
                    var m = System.Text.RegularExpressions.Regex.Match(
                        token,
                        @"on (#[0-9a-fA-F]{6})"
                    );
                    if (m.Success)
                        bg = m.Groups[1].Value.ToLowerInvariant();
                    continue;
                }
                if (inToken)
                {
                    token += ch;
                    continue;
                }
                if (bg is "#000000" or "#0c0c0c")
                    bad.Add($"({col},{y + 1}) bg={bg}");
                col++;
            }
        }
        return bad;
    }

    private static string Plain(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\[[^\[\]]*\]", "");

    private async Task<IReadOnlyList<string>> CaptureFrameAsync()
    {
        await OnUiAsync(() =>
        {
            _app.RequestFullRender();
            return 0;
        });
        await Task.Delay(150);
        return await OnUiAsync(() =>
            (
                (CellBuffer)
                    typeof(TerminalApp)
                        .GetField("_renderBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .GetValue(_app)!
            ).ToMarkupLines()
        );
    }

    private void Click(int x, int y)
    {
        _backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Down,
                Button = TerminalMouseButton.Left,
                X = x,
                Y = y,
            }
        );
        _backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Up,
                Button = TerminalMouseButton.Left,
                X = x,
                Y = y,
            }
        );
    }

    private void Move(int x, int y) =>
        _backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Move,
                Button = TerminalMouseButton.None,
                X = x,
                Y = y,
            }
        );

    private static Task<T> OnUiAsync<T>(Func<T> action) => TerminalUiFixture.OnUiAsync(action);

    private static Task WaitUntilAsync(Func<Task<bool>> condition, string what) =>
        TerminalUiFixture.WaitUntilAsync(condition, what, 15_000);

    /// <summary>
    /// 找出未绘制格。markup 行的合法形式是交替的「样式令牌 / 文本段」；
    /// 令牌形如 <c>[#rrggbb on #rrggbb]</c>（可带 bold 等文本属性）。
    /// 判定：每段文本之前的最近一个令牌必须含 `on #`（背景）——否则该段透出终端默认底；
    /// **整行既无字符也无背景令牌**同样算未绘制（旧版漏判的一类：整行没画）。
    /// </summary>
    internal static List<string> UndrawnCells(IReadOnlyList<string> lines)
    {
        var bad = new List<string>();
        for (var y = 0; y < lines.Count; y++)
        {
            var line = lines[y];
            var inToken = false;
            var tokenText = new StringBuilder();
            var runHasBg = false; // 行首没有前导令牌 → 视为无背景
            var runCharacters = 0;
            var sawBackgroundToken = false;

            void Flush()
            {
                if (runCharacters > 0 && !runHasBg)
                    bad.Add($"row {y + 1}: {runCharacters} 格无背景");
                runCharacters = 0;
            }

            foreach (var ch in line)
            {
                if (ch == '[' && !inToken)
                {
                    Flush();
                    inToken = true;
                    tokenText.Clear();
                    continue;
                }
                if (ch == ']' && inToken)
                {
                    inToken = false;
                    runHasBg = tokenText
                        .ToString()
                        .Contains("on #", StringComparison.OrdinalIgnoreCase);
                    if (runHasBg)
                        sawBackgroundToken = true;
                    continue;
                }
                if (inToken)
                {
                    tokenText.Append(ch);
                    continue;
                }
                runCharacters++;
            }

            Flush();
            if (runCharacters == 0 && !sawBackgroundToken)
                bad.Add($"row {y + 1}: 整行未绘制");
        }
        return bad;
    }
}
