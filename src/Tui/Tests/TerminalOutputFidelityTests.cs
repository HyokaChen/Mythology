using System.Reflection;
using System.Text;
using Core.Data;
using Tui.Controls;
using XenoAtom.Ansi;
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
/// 输出保真度检查（用户实机排查的最后一环）：把应用**实际发往终端的字节流**解码回网格，
/// 与框架自己的渲染缓冲逐格比对。
///
/// 用途：判定「终端里出现黑块/字形错位」的责任方 ——
/// - 解码结果 == 框架缓冲 ⇒ 框架输出忠实，分歧只可能来自终端自身（字体/宽度模型/渲染）；
/// - 解码结果 != 框架缓冲 ⇒ 框架增量输出有错（位置算术/宽字形续格漏写），可在本地复现并上报。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class TerminalOutputFidelityTests : IAsyncLifetime
{
    private const int W = 88;
    private const int H = 44;

    private SetUpPanelLayoutTests.FakeStore _store = null!;
    private InMemoryTerminalBackend _backend = null!;
    private TerminalApp _app = null!;
    private CancellationTokenSource _cts = null!;
    private Task _runTask = null!;
    private PanelColors _previousColors = null!;
    private SetUpPanel _panel = null!;

    public async ValueTask InitializeAsync()
    {
        _previousColors = PanelThemes.Current;
        var store = new SetUpPanelLayoutTests.FakeStore
        {
            Novel = new Novel
            {
                Id = "n1",
                NovelId = "n1",
                Title = "输出保真",
            },
        };
        // 中文实体名（含标点）——宽字形密集，最容易暴露位置算术问题
        foreach (
            var (id, name) in new[]
            {
                ("c1", "宫无吉田"),
                ("c2", "雪彩子"),
                ("c3", "原俊人"),
                ("c4", "木明美"),
                ("c5", "‘弯引号’测试"),
            }
        )
        {
            store.Characters.Add(
                new Character
                {
                    Id = id,
                    NovelId = "n1",
                    Name = name,
                    Gender = "男",
                }
            );
        }

        for (var i = 6; i <= 40; i++)
            store.Characters.Add(
                new Character
                {
                    Id = $"c{i}",
                    NovelId = "n1",
                    Name = $"千山{i}",
                    Gender = i % 2 == 0 ? "男" : "女",
                }
            );
        for (var i = 1; i <= 40; i++)
            store.Plotlines.Add(
                new Plotline
                {
                    Id = $"p{i}",
                    NovelId = "n1",
                    Name = i switch
                    {
                        1 => "亚里克线",
                        2 => "卷一 · 眷恋的幽灵",
                        _ => $"情节线{i}",
                    },
                    Description =
                        i == 2
                            ? string.Concat(
                                Enumerable.Repeat(
                                    "松雪姐妹的故事。姐姐彩子与妹妹的冲突逐渐揭开真相。",
                                    12
                                )
                            )
                            : "",
                }
            );

        _store = store;
        var theme = Tui.Services.ThemeRegistry.Desert;
        PanelThemes.Current = theme.Colors;
        _panel = new SetUpPanel(store, theme.Colors) { CurrentNovelId = "n1" };
        await _panel.RefreshAsync("n1");

        var left = new ThinSplitter(
            new BackgroundPanel
            {
                FillStyle = Style.None.WithBackground(theme.Colors.ConversationBg),
                Content = new Markup("对话"),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            },
            new BackgroundPanel
            {
                FillStyle = Style.None.WithBackground(theme.Colors.PromptBg),
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
                FillStyle = Style.None.WithBackground(theme.Colors.SidePanelBg),
                Content = new Markup("任务"),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
        )
        {
            Ratio = 0.6,
            Direction = SplitDirection.Vertical,
            BackgroundColor = theme.Colors.SidePanelBg,
            LineColor = theme.Colors.Border,
            HorizontalGlyph = new Rune('▄'),
            BarSize = 1,
        };
        var main = new ThinSplitter(left, right)
        {
            Ratio = 0.7,
            Direction = SplitDirection.Horizontal,
            BackgroundColor = theme.Colors.SidePanelBg,
            LineColor = theme.Colors.Border,
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
                    FillStyle = Style.None.WithBackground(theme.Colors.HeaderBg),
                    Content = new Markup("header"),
                    HorizontalAlignment = Align.Stretch,
                },
                0,
                0
            )
            .Cell(main, 1, 0)
            .Cell(
                new BackgroundPanel
                {
                    FillStyle = Style.None.WithBackground(theme.Colors.StatusBg),
                    Content = new Markup("status"),
                    HorizontalAlignment = Align.Stretch,
                },
                2,
                0
            );
        root.Style(theme.UiTheme);
        root.SetStyle(theme.ControlStyles.Dialog);
        root.SetStyle(theme.ControlStyles.TabControl);
        root.SetStyle(theme.ControlStyles.Collapsible);
        root.SetStyle(theme.ControlStyles.Button);
        root.SetStyle(theme.ControlStyles.Switch);
        PanelThemes.InjectOverlayStyles(root, theme.Colors);

        // 能力必须显式给足（生产 Host 强制 COLORTERM=truecolor）：否则框架会按最小能力
        // 输出（实测：整帧 0 个颜色 SGR），本测试也就失去意义。
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        _backend = new InMemoryTerminalBackend(
            new TerminalSize(W, H),
            new TerminalCapabilities
            {
                AnsiEnabled = true,
                ColorLevel = TerminalColorLevel.TrueColor,
                IsInputRedirected = false,
                IsOutputRedirected = false,
            }
        );
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
                DisableInputEcho = true,
                UpdateWaitDuration = TimeSpan.FromMilliseconds(1),
                // 模拟中文字体回退把 Select 箭头 ▾ 实测为双格的终端。
                WideRuneResolver = Tui.Services.TerminalWidthProbe.BuildResolver(
                    new Tui.Services.TerminalWidthProbe.ProbeResult(
                        false,
                        new HashSet<int> { 0x25BE }
                    )
                ),
            }
        );
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => _app.RunAsync(_cts.Token));
        await Task.Delay(700);
        Terminal.Instance.CursorStyle(AnsiCursorStyle.SteadyBar);
    }

    public async ValueTask DisposeAsync()
    {
        await TerminalUiFixture.StopAppAsync(_cts, _runTask);
        Terminal.Close();
        PanelThemes.Current = _previousColors;
    }

    /// <summary>
    /// 未上色格普查：框架只会发「变化过的格子」，**没有任何背景色的格子永远不会被写**，
    /// 终端就用默认底渲染 —— 默认底是黑（或 OSC 11 未生效）时就是界面上一块块黑区
    /// （用户实机：对话框/面板交界处一个字符宽的黑块）。
    /// 本测试要求整帧不得出现未上色格；失败信息给出位置与内容，便于定位是哪个控件漏了底色。
    /// </summary>
    [Fact]
    public async Task Frame_HasNoUnstyledCells()
    {
        await OnUiAsync(() =>
        {
            new Tui.Controls.EntityEditDialog(
                _store,
                Core.Data.EntityType.Character,
                entityId: "c1",
                novelId: "n1"
            ).Show();
            return 0;
        });
        await Task.Delay(900);

        // 关键：指针到面板 + 滚轮滚动 + 悬停（用户实机状态）——滚动 + 悬停重绘是残留格的老配方
        Move(95, 20);
        await Task.Delay(200);
        for (var i = 0; i < 6; i++)
        {
            Wheel(95, 20);
            await Task.Delay(120);
        }
        Move(95, 26);
        await Task.Delay(300);
        Move(60, 14);
        await Task.Delay(300);

        var buffer = await OnUiAsync(() => GetRenderBuffer(_app));
        var (unstyledCount, report) = await OnUiAsync(() => Census(buffer));
        Assert.True(unstyledCount == 0, $"帧内有 {unstyledCount} 个未上色格：\n{report}");
    }

    private static (int Count, string Report) Census(CellBuffer buffer)
    {
        var styles = (Style[])
            typeof(CellBuffer)
                .GetField("_cells", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(buffer)!;
        var lines = buffer.ToMarkupLines();
        var cells = new List<(int X, int Y, char Ch)>();
        for (var y = 0; y < buffer.Height; y++)
        {
            var text = StripMarkup(lines[y]);
            for (var x = 0; x < buffer.Width; x++)
            {
                var st = styles[y * buffer.Width + x];
                if (st.TryGetBackground(out _))
                    continue;
                var ch = x < text.Length ? text[x] : ' ';
                cells.Add((x, y, ch));
            }
        }

        // 按行归并成区间，输出更紧凑
        var sb = new StringBuilder();
        foreach (var g in cells.GroupBy(c => c.Y).OrderBy(g => g.Key))
        {
            var xs = g.Select(c => c.X).OrderBy(v => v).ToArray();
            var spans = new List<string>();
            var start = xs[0];
            var prev = xs[0];
            foreach (var x in xs.Skip(1))
            {
                if (x == prev + 1)
                {
                    prev = x;
                    continue;
                }
                spans.Add(start == prev ? $"{start}" : $"{start}-{prev}");
                start = prev = x;
            }
            spans.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            var sample = string.Concat(g.OrderBy(c => c.X).Take(24).Select(c => c.Ch)).Trim();
            sb.Append($"  y={g.Key, 2} x=[{string.Join(",", spans)}] «{sample}»\n");
        }
        return (cells.Count, sb.ToString());
    }

    [Fact]
    public async Task EmittedBytes_MatchFrameworkBuffer()
    {
        // 再加一次鼠标移动，覆盖「悬停重绘」这条输出路径
        _backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Move,
                Button = TerminalMouseButton.None,
                X = 40,
                Y = 12,
            }
        );
        await Task.Delay(400);

        // 快照必须在 UI 线程上取：测试线程直接读 live CellBuffer 会与 UI 线程的下一帧竞争
        // （脏区先清后画，读到的可能是「已清空、尚未重画」的中间态 → 假失败）。
        var (ansi, snapshot) = await OnUiAsync(() =>
            (_backend.GetOutText(), CaptureSnapshot(GetRenderBuffer(_app)))
        );

        var (decoded, decodedBg) = Decode(ansi, W, H, _app.WideRuneResolver);

        // BCE 语义比对：真实终端在擦除（ESC[J/K）时用「当前 SGR 背景」填格；
        // 若框架在「背景=默认」时擦格，真实终端就用默认底（黑）填 —— 这就是
        // 「一个字符宽的黑块 / 字间空档」的成因，且与终端品牌无关（所有终端都遵循 BCE）。
        // 虚拟终端里框架可能整体走「不打颜色」的路径（实测：整帧 0 个颜色 SGR、仅数 KB），
        // 此时底色比对没有意义，跳过（工具保留：真实终端流导入时可复用同一解码器）。
        if (!ansi.Contains("48;", StringComparison.Ordinal))
            Assert.Skip("harness 未产生颜色输出（框架按最小能力路径渲染），底色比对跳过");

        var (bgMismatch, bgSample) = CompareBackgrounds(snapshot, decodedBg);
        Assert.True(
            bgMismatch == 0,
            $"终端底色与框架缓冲不一致（{bgMismatch} 格）——擦除/写入时的背景色不对，真实终端会留黑块：\n{bgSample}"
        );
        var (mismatches, sample) = Compare(snapshot, decoded);
        Assert.True(
            mismatches == 0,
            $"终端输出与框架缓冲不一致（{mismatches} 格）——框架输出不忠实：\n{sample}"
        );
    }

    [Fact]
    public async Task EntityDialogSelect_EmittedBytesMatchFrameworkBufferAcrossFocusChanges()
    {
        await AssertEmittedMatchesBufferAsync("base before dialog");
        await OnUiAsync(() =>
        {
            new EntityEditDialog(
                _store,
                Core.Data.EntityType.Character,
                entityId: "c1",
                novelId: "n1"
            ).Show();
            return 0;
        });
        await Task.Delay(900);
        var dialogBounds = await OnUiAsync(() =>
            _app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Single().Bounds
        );
        await AssertEmittedMatchesBufferAsync("dialog before pointer move");
        Move(dialogBounds.Right + 1, dialogBounds.Y + dialogBounds.Height / 2);
        await Task.Delay(300);
        await AssertEmittedMatchesBufferAsync("pointer outside dialog");

        await AssertEmittedMatchesBufferAsync("dialog with collapsed Select");

        var nameInputBounds = await OnUiAsync(() =>
            _app
                .Root.EnumerateVisualsDepthFirst()
                .OfType<Dialog>()
                .Single()
                .EnumerateVisualsDepthFirst()
                .OfType<TextBox>()
                .First(input => input.Bounds.Width > 0)
                .Bounds
        );
        Click(nameInputBounds.X + 1, nameInputBounds.Y);
        await Task.Delay(300);
        await AssertEmittedMatchesBufferAsync("TextBox focus");

        var rows = await OnUiAsync(() =>
            GetRenderBuffer(_app).ToMarkupLines().Select(StripMarkup).ToArray()
        );

        var genderRow = Array.FindIndex(
            rows,
            row => row.Contains("性别", StringComparison.Ordinal)
        );
        Assert.True(genderRow >= 0, "对话框中找不到性别下拉项");
        var caretCol = rows[genderRow].LastIndexOf('▾');
        Assert.True(caretCol >= 0, "找不到 Select 下拉箭头");
        Click(caretCol, genderRow);
        await Task.Delay(300);
        var optionsBounds = await OnUiAsync(() =>
            _app
                .Root.EnumerateVisualsDepthFirst()
                .OfType<OptionList<string>>()
                .Single(list => list.Bounds.Width > 0)
                .Bounds
        );
        Move(optionsBounds.X + 1, optionsBounds.Y);
        await Task.Delay(200);
        await AssertEmittedMatchesBufferAsync("popup option hover");

        rows = await OnUiAsync(() =>
            GetRenderBuffer(_app).ToMarkupLines().Select(StripMarkup).ToArray()
        );
        Assert.Contains(rows, row => row.Contains("其他", StringComparison.Ordinal));
        await AssertEmittedMatchesBufferAsync("open Select popup");

        void Click(int x, int y)
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
    }

    [Fact]
    public async Task EntityActionPopupHover_EmittedGridMatchesFrame()
    {
        var sectionButton = await OnUiAsync(() =>
            (
                (List<Button>)
                    typeof(SetUpPanel)
                        .GetField(
                            "_sectionButtons",
                            BindingFlags.Instance | BindingFlags.NonPublic
                        )!
                        .GetValue(_panel)!
            )[4] // 情节 tab（大纲排第一，实体分区后移一位）
        );
        var sectionBounds = await OnUiAsync(() => sectionButton.Bounds);
        Click(sectionBounds.X + 1, sectionBounds.Y);
        await Task.Delay(150);
        Assert.Equal(
            4,
            await OnUiAsync(() =>
                (int)
                    typeof(SetUpPanel)
                        .GetField("_sectionIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(_panel)!
            )
        );

        var item = await OnUiAsync(() =>
        {
            var accordion = (Accordion)
                typeof(SetUpPanel)
                    .GetField("_plotlineAccordion", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(_panel)!;
            return (Collapsible)accordion.Children[1];
        });
        var itemBounds = await OnUiAsync(() => item.Bounds);
        Click(itemBounds.X + 4, itemBounds.Y);
        await Task.Delay(150);
        Assert.True(await OnUiAsync(() => item.IsExpanded), "第二条情节线未展开");
        var listScroll = await OnUiAsync(() =>
        {
            var tabPanel = (
                (List<BackgroundPanel>)
                    typeof(SetUpPanel)
                        .GetField(
                            "_tabContentPanels",
                            BindingFlags.Instance | BindingFlags.NonPublic
                        )!
                        .GetValue(_panel)!
            )[4]; // 情节内容面板（大纲排第一，实体分区后移一位）
            return tabPanel
                .EnumerateVisualsDepthFirst()
                .OfType<ScrollViewer>()
                .Select(view => (Bounds: view.Bounds, Range: SetUpPanel.MaxVerticalOffset(view)))
                .Single(view => view.Range > 0);
        });
        Assert.True(
            listScroll.Bounds.Width > 0 && listScroll.Range > 0,
            $"情节线列表滚动区未溢出/布局：bounds={listScroll.Bounds}, range={listScroll.Range}"
        );

        var menuLabel = await OnUiAsync(() =>
            item.EnumerateVisualsDepthFirst()
                .OfType<Markup>()
                .Single(m => m.Text?.Contains("⋯", StringComparison.Ordinal) == true)
        );
        var labelBounds = await OnUiAsync(() => menuLabel.Bounds);
        Click(labelBounds.X, labelBounds.Y);
        await Task.Delay(250);

        var popup = await OnUiAsync(() =>
            (AnchoredPopup<SetUpPanel>)
                typeof(SetUpPanel)
                    .GetField("_rowMenuPopup", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(_panel)!
        );
        var popupBounds = await OnUiAsync(() => popup.List.Bounds);
        Assert.True(popupBounds.Height >= 2, $"行动菜单未布局：{popupBounds}");
        Move(popupBounds.X + 2, popupBounds.Y + 1);
        await Task.Delay(250);

        var lines = await OnUiAsync(() => GetRenderBuffer(_app).ToMarkupLines());
        var insertReference = lines.Single(line =>
            StripMarkup(line).Contains("插入引用", StringComparison.Ordinal)
        );
        var hoveredBackground =
            $"on #{PanelThemes.Current.TabHoveredBg.R:x2}{PanelThemes.Current.TabHoveredBg.G:x2}{PanelThemes.Current.TabHoveredBg.B:x2}";
        Assert.Contains(hoveredBackground, insertReference, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(RealFrameBlackBlockTests.BlackCells(lines));
        Assert.Empty(RealFrameBlackBlockTests.UndrawnCells(lines));
        await AssertEmittedMatchesBufferAsync("实体菜单悬停「插入引用」");

        Move(0, 0);
        await Task.Delay(250);
        lines = await OnUiAsync(() => GetRenderBuffer(_app).ToMarkupLines());
        Assert.Empty(RealFrameBlackBlockTests.BlackCells(lines));
        Assert.Empty(RealFrameBlackBlockTests.UndrawnCells(lines));
        await AssertEmittedMatchesBufferAsync("实体菜单鼠标移出");

        void Click(int x, int y)
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
    }

    private async Task AssertEmittedMatchesBufferAsync(string state)
    {
        // 快照必须在 UI 线程上取：测试线程直接读 live CellBuffer 会与 UI 线程的下一帧竞争
        // （脏区先清后画，读到的可能是「已清空、尚未重画」的中间态 → 「期望 ' ' 实际 <旧内容>」假失败）。
        var (ansi, snapshot) = await OnUiAsync(() =>
            (_backend.GetOutText(), CaptureSnapshot(GetRenderBuffer(_app)))
        );
        var cursorStyles = System.Text.RegularExpressions.Regex.Matches(ansi, @"\x1b\[(\d+) q");
        Assert.True(cursorStyles.Count > 0, $"{state}: 缺少终端光标形状输出");
        Assert.Equal("6", cursorStyles[cursorStyles.Count - 1].Groups[1].Value);
        var csiFinals = new string(
            System
                .Text.RegularExpressions.Regex.Matches(ansi, @"\x1b\[[0-?]*[ -/]*[@-~]")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(match => match.Value[^1])
                .Distinct()
                .ToArray()
        );
        var (decoded, decodedBg) = Decode(ansi, W, H, _app.WideRuneResolver);
        if (ansi.Contains("48;", StringComparison.Ordinal))
        {
            var (bgMismatch, bgSample) = CompareBackgrounds(snapshot, decodedBg);
            Assert.True(
                bgMismatch == 0,
                $"{state}: 输出底色与框架缓冲不一致（{bgMismatch} 格）：\n{bgSample}"
            );
        }
        var (textMismatch, textSample) = Compare(snapshot, decoded);
        Assert.True(
            textMismatch == 0,
            $"{state}: 输出文字与框架缓冲不一致（{textMismatch} 格）：\n{textSample}\nCSI finals: {csiFinals}"
        );
    }

    /// <summary>
    /// 渲染缓冲的逐格快照。测试线程**必须**用快照，不能读 live <see cref="CellBuffer"/>：
    /// UI 线程的下一帧会并发改写同一份 <c>_scalars/_cells</c>（脏区先 <c>ClearCurrentClip</c>
    /// 清空、再由 <c>RenderTree</c> 重画），逐格读取可能读到「已清空、尚未重画」的中间态，
    /// 表现为「期望 ' ' 实际 &lt;上一帧内容&gt;」的假失败（修复前 class 过滤下约 1/2 概率复现）。
    /// </summary>
    private sealed class BufferSnapshot
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int[] Scalars { get; init; }
        public required Style[] Cells { get; init; }
    }

    /// <summary>取缓冲快照；必须在 UI 线程上调用（见 <see cref="OnUiAsync{T}"/>）。</summary>
    private static BufferSnapshot CaptureSnapshot(CellBuffer buffer) =>
        new()
        {
            Width = buffer.Width,
            Height = buffer.Height,
            Scalars = (int[])
                (
                    (int[])
                        typeof(CellBuffer)
                            .GetField("_scalars", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(buffer)!
                ).Clone(),
            Cells = (Style[])
                (
                    (Style[])
                        typeof(CellBuffer)
                            .GetField("_cells", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(buffer)!
                ).Clone(),
        };

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

    private void Wheel(int x, int y) =>
        _backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Wheel,
                Button = TerminalMouseButton.None,
                WheelDelta = -1,
                X = x,
                Y = y,
            }
        );

    private static string StripMarkup(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"\[[^\[\]]*\]", "");

    private static (int Count, string Sample) Compare(BufferSnapshot buffer, char?[,] decoded)
    {
        // ToMarkupLines is presentation-oriented; comparing it by rune width miscounts
        // continuation cells. Compare the physical glyph stored at each snapshot column.
        var scalars = buffer.Scalars;
        var sb = new StringBuilder();
        var count = 0;
        for (var y = 0; y < buffer.Height; y++)
        for (var x = 0; x < buffer.Width; x++)
        {
            var scalar = scalars[y * buffer.Width + x];
            var want = scalar == 0 ? (char?)null : (char)scalar;
            var got = decoded[y, x];
            if (got == want)
                continue;
            // diff 渲染器不重写「本来就该是空白」的格（终端启动即空白）：未写 = 空白，不算缺字。
            if (got is null && want == ' ')
                continue;
            count++;
            if (count <= 12)
            {
                var expected = want is { } expectedRune ? $"'{expectedRune}'" : "<续格>";
                var actual = got is { } actualRune ? $"'{actualRune}'" : "<未写>";
                sb.Append($"  ({x},{y}) 期望 {expected} 实际 {actual}\n");
            }
        }
        return (count, sb.ToString());
    }

    /// <summary>极简 ANSI 解码：CSI H/f/G 定位、CSI J/K 擦除、其余 CSI/OSC 忽略；文本按框架宽度推进。</summary>
    /// <summary>背景色（null = 终端默认底）。</summary>
    private static (char?[,] Text, int?[,] Bg) Decode(
        string ansi,
        int w,
        int h,
        Func<Rune, bool>? resolver
    )
    {
        var grid = new char?[h, w];
        var bgs = new int?[h, w];
        int? currentBg = null;
        var x = 0;
        var y = 0;
        var i = 0;
        while (i < ansi.Length)
        {
            var c = ansi[i];
            if (c == '\u001b')
            {
                if (i + 1 < ansi.Length && ansi[i + 1] == '[')
                {
                    var j = i + 2;
                    while (
                        j < ansi.Length
                        && (
                            char.IsDigit(ansi[j])
                            || ansi[j] == ';'
                            || ansi[j] == '?'
                            || ansi[j] == ' '
                        )
                    )
                        j++;
                    if (j >= ansi.Length)
                        break;
                    var final = ansi[j];
                    var pars = ansi[(i + 2)..j]
                        .Split([';', ':'], StringSplitOptions.RemoveEmptyEntries);
                    switch (final)
                    {
                        case 'm':
                            currentBg = ParseBackground(pars, currentBg);
                            break;
                        case 'H':
                        case 'f':
                            y = (pars.Length > 0 && int.TryParse(pars[0], out var r) ? r : 1) - 1;
                            x = (pars.Length > 1 && int.TryParse(pars[1], out var cc) ? cc : 1) - 1;
                            break;
                        case 'G':
                            x = (pars.Length > 0 && int.TryParse(pars[0], out var g) ? g : 1) - 1;
                            break;
                        // BCE：擦除用「当前背景色」填格（真实终端语义）
                        case 'J':
                            for (var yy = 0; yy < h; yy++)
                            for (var xx = 0; xx < w; xx++)
                            {
                                grid[yy, xx] = ' ';
                                bgs[yy, xx] = currentBg;
                            }
                            break;
                        case 'K':
                            if (y >= 0 && y < h)
                                for (var xx = x < 0 ? 0 : x; xx < w; xx++)
                                {
                                    grid[y, xx] = ' ';
                                    bgs[y, xx] = currentBg;
                                }
                            break;
                    }
                    i = j + 1;
                    continue;
                }
                if (i + 1 < ansi.Length && ansi[i + 1] == ']')
                {
                    // OSC：跳到 BEL 或 ST
                    var j = i + 2;
                    while (
                        j < ansi.Length
                        && ansi[j] != '\a'
                        && !(ansi[j] == '\u001b' && j + 1 < ansi.Length && ansi[j + 1] == '\\')
                    )
                        j++;
                    i = j + 1;
                    continue;
                }
                i += 2;
                continue;
            }

            if (c == '\r')
            {
                x = 0;
                i++;
                continue;
            }
            if (c == '\n')
            {
                y++;
                i++;
                continue;
            }

            var rune = Rune.GetRuneAt(ansi, i);
            i += rune.Utf16SequenceLength;
            var width = TerminalTextUtility.GetRuneWidth(
                rune,
                resolver,
                TerminalTextUtility.DefaultTabWidth
            );
            if (x >= 0 && x < w && y >= 0 && y < h)
            {
                grid[y, x] = (char)rune.Value;
                bgs[y, x] = currentBg;
                for (var k = 1; k < width && x + k < w; k++)
                {
                    grid[y, x + k] = null; // 宽字形续格
                    bgs[y, x + k] = currentBg;
                }
            }
            x += width;
        }
        return (grid, bgs);
    }

    /// <summary>从 SGR 参数解析当前背景（48;2;R;G;B / 48;5;N / 40-47 / 49=默认）。</summary>
    private static int? ParseBackground(string[] pars, int? current)
    {
        if (pars.Length == 0)
            return current;
        var result = current;
        for (var i = 0; i < pars.Length; i++)
        {
            if (!int.TryParse(pars[i], out var v))
                continue;
            if (v == 49)
                result = null;
            else if (v == 48 && i + 4 < pars.Length && pars[i + 1] == "2")
            {
                if (
                    int.TryParse(pars[i + 2], out var r)
                    && int.TryParse(pars[i + 3], out var g)
                    && int.TryParse(pars[i + 4], out var b)
                )
                    result = (r << 16) | (g << 8) | b;
                i += 4;
            }
            else if (v == 48 && i + 2 < pars.Length && pars[i + 1] == "5")
            {
                if (int.TryParse(pars[i + 2], out var idx))
                    result = Color256(idx);
                i += 2;
            }
            else if (v is >= 40 and <= 47)
                result = v - 40;
            else if (v == 0)
                result = null;
        }
        return result;
    }

    /// <summary>逐格比对底色（框架缓冲快照 vs 终端的 BCE 语义结果）。</summary>
    private static (int Count, string Sample) CompareBackgrounds(
        BufferSnapshot buffer,
        int?[,] decodedBg
    )
    {
        var styles = buffer.Cells;
        var sb = new StringBuilder();
        var count = 0;
        for (var y = 0; y < buffer.Height; y++)
        {
            for (var x = 0; x < buffer.Width; x++)
            {
                var style = styles[y * buffer.Width + x];
                var want = style.TryGetBackground(out var c)
                    ? ((c.R << 16) | (c.G << 8) | c.B)
                    : (int?)null;
                var got = decodedBg[y, x];
                if (want == got)
                    continue;
                count++;
                if (count <= 12)
                    sb.Append($"  ({x},{y}) 期望底 {Hex(want)} 实际(终端 BCE) {Hex(got)}\n");
            }
        }
        return (count, sb.ToString());
    }

    private static string Hex(int? v) => v is null ? "<默认=黑>" : $"#{v:X6}";

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }

    /// <summary>xterm-256 调色板：16 基础色 + 6×6×6 立方 + 24 灰阶。</summary>
    private static int Color256(int idx)
    {
        if (idx < 16)
        {
            int[] base16 =
            [
                0x000000,
                0x800000,
                0x008000,
                0x808000,
                0x000080,
                0x800080,
                0x008080,
                0xC0C0C0,
                0x808080,
                0xFF0000,
                0x00FF00,
                0xFFFF00,
                0x0000FF,
                0xFF00FF,
                0x00FFFF,
                0xFFFFFF,
            ];
            return base16[idx];
        }
        if (idx < 232)
        {
            var n = idx - 16;
            var r = n / 36;
            var g = (n % 36) / 6;
            var b = n % 6;
            static int Lvl(int v) => v == 0 ? 0 : 55 + v * 40;
            return (Lvl(r) << 16) | (Lvl(g) << 8) | Lvl(b);
        }
        var gray = 8 + (idx - 232) * 10;
        return (gray << 16) | (gray << 8) | gray;
    }

    private static CellBuffer GetRenderBuffer(TerminalApp app) =>
        (CellBuffer)
            typeof(TerminalApp)
                .GetField("_renderBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(app)!;

    private static Task<T> OnUiAsync<T>(Func<T> action) => TerminalUiFixture.OnUiAsync(action);
}
