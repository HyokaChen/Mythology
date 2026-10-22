using System.Reflection;
using Core.Data;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Threading;
using Xunit;
using ChapterEditScope = Tui.Controls.ChapterEditScope;

namespace Tui.Tests;

/// <summary>
/// 框架渲染语义表征锁（实机排查结论，必须钉住，避免再走弯路）：
///
/// <c>TerminalApp.RequestFullRender()</c> 走 <c>FullscreenHost.Reset()</c> ⇒
/// <c>CellBufferDiffRenderer.Reset()</c> 把 <c>_lastScalars/_lastCells/_lastHyperlinks</c>
/// 全部置空，下一帧 <c>forceFull=true</c>，差分渲染器拿不到「上次状态」⇒ **整屏重发**。
/// 实测（196×44 = 8624 格）：连续两次调用、中间不做任何改变，第二次仍输出 22021 字节
/// ≈ 2.55 字节/格——差分-only 的话第二次应该接近 0。
///
/// 应用**故意不**在热路径调它：整屏重发会覆盖 Apple Terminal 正在绘制的内联组词文字，
/// 终端下一帧再画回来，双方抢画面就是闪烁（用户实测不可接受）。库没有「只重发指定区域」
/// 的能力，所以终端侧擦除类损伤（Terminal.app 输入法）不做自动补偿。
///
/// 首选仍是消除分歧源本身——让每个字形的记账宽度与终端一致
/// （见 TerminalWidthProbe：源码清单 + 数据字形收集补测）。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class FullRenderEmitSemanticsTests : IAsyncLifetime
{
    /// <summary>
    /// 用户报障（截图 2.png）：侧栏右侧出现黑色块，且 header / tab 条整片缺失、
    /// 只有提示行有字——典型的「局部重绘」特征（终端尺寸变化后只重画了部分区域）。
    /// 复现：起小终端 → 拉宽 → 检查整帧不得有未上色格。
    /// </summary>
    [Fact]
    public async Task TerminalResize_NoUndrawnCells()
    {
        await TerminalUiFixture.StopAppAsync(_cts, _runTask);
        Terminal.Close();

        _backend = new InMemoryTerminalBackend(new TerminalSize(120, 30), null);
        Terminal.Initialize(
            _backend,
            new TerminalOptions { ImplicitStartInput = true },
            force: true
        );
        _harness = new TuiSlashTestHarness(novelId: null, sessionId: null);
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => _harness.App.RunAsync(_cts.Token));
        await Task.Delay(1200);

        // 拉宽 + 拉高（用户现场：终端很宽）
        await OnUiAsync(() =>
        {
            _backend.SetWindowSize(new TerminalSize(196, 44));
            return true;
        });
        await Task.Delay(1200);

        var frame = await OnUiAsync(() =>
        {
            var root = (Visual)
                typeof(Tui.TuiApp)
                    .GetField("_rootVisual", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(_harness.App)!;
            root.App?.RequestFullRender();
            var buffer = (CellBuffer)
                typeof(TerminalApp)
                    .GetField("_renderBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(root.App!)!;
            return buffer.ToMarkupLines();
        });

        var undrawn = RealFrameBlackBlockTests.UndrawnCells(frame);
        Assert.True(
            undrawn.Count == 0,
            $"终端拉宽后存在未绘制格 {undrawn.Count} 处，首批: {string.Join(", ", undrawn.Take(8))}"
        );
    }

    /// <summary>
    /// 章节「内容」编辑器必须是整块铺满（用户报障：不该是「内容」标签 + 文本框的键值对形式）。
    /// 断言两条：帧里不出现「内容」标签行；正文编辑器的高度远大于默认的 3 行。
    /// </summary>
    [Fact]
    public async Task ChapterContentEditor_FillsDialog_WithoutLabelRow()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var novelId = TuiSlashTestHarness.NovelId;

        await OnUiAsync(() =>
        {
            _harness.Store.Chapters.Add(
                new Chapter
                {
                    Id = "cc1",
                    NovelId = novelId,
                    Title = "内容章",
                    Order = 1,
                    Content = string.Join(
                        "\n",
                        Enumerable.Range(1, 30).Select(i => $"第{i}行正文")
                    ),
                }
            );
            return true;
        });

        await OnUiAsync(() =>
        {
            new EntityEditDialog(
                _harness.Store,
                EntityType.Chapter,
                entityId: "cc1",
                novelId: novelId,
                editScope: ChapterEditScope.Content
            ).Show();
            return true;
        });
        await Task.Delay(500);

        var (frame, editorHeight) = await OnUiAsync(() =>
        {
            var root = (Visual)
                typeof(Tui.TuiApp).GetField("_rootVisual", flags)!.GetValue(_harness.App)!;
            root.App?.RequestFullRender();
            var buffer = (CellBuffer)
                typeof(TerminalApp).GetField("_renderBuffer", flags)!.GetValue(root.App!)!;
            var text = string.Join(
                "\n",
                buffer
                    .ToMarkupLines()
                    .Select(l =>
                        System.Text.RegularExpressions.Regex.Replace(l, @"\[[^\[\]]*\]", "")
                    )
            );
            var editor = root.App!.Root.EnumerateVisualsDepthFirst()
                .OfType<TextArea>()
                .FirstOrDefault();
            return (text, editor?.Bounds.Height ?? -1);
        });

        // 1) 不出现「内容」标签行（键值对形式的遗留）
        Assert.DoesNotContain(frame.Split("\n"), l => l.Trim() == "内容");
        // 2) 编辑器撑满：对话框内容区有十几行高，不是默认的 3 行
        Assert.True(editorHeight >= 10, $"正文编辑器高度={editorHeight}，应撑满对话框内容区");
        // 3) 正文确实在帧里
        Assert.Contains("第1行正文", frame);
    }

    /// <summary>
    /// 章详情对话框的只读信息（用户报障）：必须展示「第 X 章」卷内序号、字数、更新时间。
    /// 卷内序号在同一卷下自增、换卷从 1 重新计数——这里造两卷各两章，
    /// 打开第二卷的第一章，编号必须回到「第 1 章」而不是延续全书全局序。
    /// </summary>
    [Fact]
    public async Task ChapterDetail_ShowsVolumeLocalNumber_WordCount_AndUpdatedAt()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var novelId = TuiSlashTestHarness.NovelId;

        // 两卷各两章：全局 Order 1..4，卷内序号各自 1..2
        await Dispatcher.Current.InvokeAsync(() =>
        {
            _harness.Store.Volumes.Add(
                new Volume
                {
                    Id = "v1",
                    NovelId = novelId,
                    Name = "风起",
                    Order = 1,
                }
            );
            _harness.Store.Volumes.Add(
                new Volume
                {
                    Id = "v2",
                    NovelId = novelId,
                    Name = "云涌",
                    Order = 2,
                }
            );
            _harness.Store.Chapters.Add(
                new Chapter
                {
                    Id = "c1",
                    NovelId = novelId,
                    Title = "雨夜",
                    Order = 1,
                    VolumeId = "v1",
                    Content = new string('字', 1234),
                    CharCount = 1234,
                }
            );
            _harness.Store.Chapters.Add(
                new Chapter
                {
                    Id = "c2",
                    NovelId = novelId,
                    Title = "破晓",
                    Order = 2,
                    VolumeId = "v1",
                    CharCount = 10,
                }
            );
            _harness.Store.Chapters.Add(
                new Chapter
                {
                    Id = "c3",
                    NovelId = novelId,
                    Title = "新卷首章",
                    Order = 3,
                    VolumeId = "v2",
                    CharCount = 20,
                    UpdatedAt = new DateTime(2026, 10, 9, 13, 30, 0, DateTimeKind.Utc),
                }
            );
            _harness.Store.Chapters.Add(
                new Chapter
                {
                    Id = "c4",
                    NovelId = novelId,
                    Title = "新卷次章",
                    Order = 4,
                    VolumeId = "v2",
                    CharCount = 30,
                }
            );
            return true;
        });

        var frame = await Dispatcher.Current.InvokeAsync(async () =>
        {
            new EntityEditDialog(
                _harness.Store,
                EntityType.Chapter,
                entityId: "c3",
                novelId: novelId
            ).Show();
            await Task.Delay(400);
            var root = (Visual)
                typeof(Tui.TuiApp).GetField("_rootVisual", flags)!.GetValue(_harness.App)!;
            root.App?.RequestFullRender();
            var buffer = (CellBuffer)
                typeof(TerminalApp).GetField("_renderBuffer", flags)!.GetValue(root.App!)!;
            return string.Join(
                "\n",
                buffer
                    .ToMarkupLines()
                    .Select(l =>
                        System.Text.RegularExpressions.Regex.Replace(l, @"\[[^\[\]]*\]", "")
                    )
            );
        });

        // 卷内序号：c3 是第二卷的第一章 → 必须回到「第 1 章」（不是全书第 3 章）
        Assert.Contains("第 1 章", frame);
        Assert.DoesNotContain("第 3 章", frame);
        // 字数：FormatWords(20) = "20" → 「20 字」
        Assert.Contains("20 字", frame);
        // 更新时间：UTC 13:30 → 本地时间（测试机 UTC+8 → 21:30）
        Assert.Contains("2026-10-09", frame);
    }

    private const int ScreenWidth = 140;
    private const int ScreenHeight = 44;

    private InMemoryTerminalBackend _backend = null!;
    private TuiSlashTestHarness _harness = null!;
    private CancellationTokenSource _cts = null!;
    private Task _runTask = null!;

    public async ValueTask InitializeAsync()
    {
        // 启动屏障：等上一个终端完全退出再顶掉全局实例（否则后续投递会落到别人的 UI 线程）
        await TerminalUiFixture.WaitForPreviousTerminalAsync();

        _backend = new InMemoryTerminalBackend(new TerminalSize(ScreenWidth, ScreenHeight), null);
        Terminal.Initialize(_backend, options: null, force: true);
        _harness = new TuiSlashTestHarness(
            novelId: TuiSlashTestHarness.NovelId,
            sessionId: TuiSlashTestHarness.SessionId,
            cloudUsage: null
        );
        await _harness.SeedSessionStoreAsync();
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => _harness.App.RunAsync(_cts.Token));
        await TuiSlashTestHarness.WaitUntilAsync(
            () => Dispatcher.Current is not null,
            "TerminalApp 启动",
            20000
        );
        await Task.Delay(800);
    }

    private static Task<T> OnUiAsync<T>(Func<T> action) => TerminalUiFixture.OnUiAsync(action);

    public async ValueTask DisposeAsync()
    {
        await TerminalUiFixture.StopAppAsync(_cts, _runTask);
        Terminal.Close();
        await _harness.DisposeAsync();
    }

    /// <summary>
    /// 光标形状必须是细竖线（DECSCUSR 6 = steady bar）。
    /// 块光标在部分终端（实机：Ghostty）整格盖住文字 —— 用户看到的「恰好一个字符宽的黑块」，
    /// 悬浮/焦点一动就跟着走、点别处消失。
    /// </summary>
    [Fact]
    public void Startup_SetsBarCursorShape()
    {
        var outText = _backend.GetOutText();
        Assert.True(
            outText.Contains("\u001b[6 q", StringComparison.Ordinal),
            "启动输出没有设置细竖线光标（DECSCUSR 6）；块光标会在部分终端整格盖住文字"
        );
    }

    /// <summary>
    /// 启动把终端默认底色对齐到应用的对话区底色（OSC 11）。
    /// 未绘制格与 BCE 擦除格使用终端默认底；对话框和输入框由独立控件样式着色。
    /// </summary>
    [Fact]
    public void Startup_AlignsTerminalDefaultBackground()
    {
        var outText = _backend.GetOutText();
        var bg = Tui.Controls.PanelThemes.Current.ConversationBg;
        var spec = $"\u001b]11;rgb:{bg.R:X2}/{bg.G:X2}/{bg.B:X2}";
        Assert.True(
            outText.Contains(spec, StringComparison.Ordinal),
            $"启动输出没有把终端默认底设为应用对话区底色（期望含 {spec.Replace("\u001b", "ESC")}）——"
                + "未绘制格 / BCE 擦除格会显示终端默认底"
        );
    }

    // 注：「主题切换 ⇒ 整屏重发」这条在虚拟终端里无法验证——本 harness 下框架走
    // 「不打颜色」路径（整帧 0 个颜色 SGR），样式变化不进 diff，故不设断言。
    // 真实终端（24 位色）下该机制成立，由用户侧 /theme 切换实测确认。

    /// <summary>
    /// 分隔条底色回归锁（用户实机：对话框右缘一条「黑块/白块」，颜色随主题在夜空/暖奶油之间变化）。
    /// bar 底色必须取它合并区域的颜色：主分隔条曾用 <c>ConversationBg</c>（= 对话区底色），
    /// 在面板一侧留下纯对比色竖带；对话框打开时被遮成残段，看起来像渲染错误的黑块。
    /// </summary>
    [Fact]
    public void SplitterBars_NeverUseConversationBackground()
    {
        var colors = Tui.Controls.PanelThemes.Current;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (var field in new[] { "_leftColumn", "_rightColumn", "_mainContent" })
        {
            var splitter = _harness.App.GetType().GetField(field, flags)?.GetValue(_harness.App);
            Assert.NotNull(splitter);

            var barSize = (int)splitter!.GetType().GetProperty("BarSize")!.GetValue(splitter)!;
            if (barSize == 0)
                continue;

            var bg = (XenoAtom.Terminal.UI.Color?)
                splitter.GetType().GetProperty("BackgroundColor")!.GetValue(splitter);
            Assert.True(
                bg == colors.SidePanelBg,
                $"{field} 的 bar 底色应取面板色，实际 {bg}——对话区底色会在面板一侧留下纯对比色竖带"
            );
        }
    }

    /// <summary>
    /// 主题切换会重新应用主分隔条样式；真实实体弹窗打开时，分隔条仍不得变成整列黑/白底。
    /// </summary>
    [Fact]
    public async Task ThemeChange_KeepsMainSplitterBackgroundOnSidePanel()
    {
        var originalThemeId = _harness.ThemeService.Current.Id;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var expectedBackground = await Dispatcher.Current.InvokeAsync(() =>
            {
                _harness.ThemeService.SetTheme("gruvbox");
                new EntityEditDialog(
                    _harness.Store,
                    EntityType.Character,
                    novelId: TuiSlashTestHarness.NovelId
                ).Show();
                return _harness.ThemeService.Current.Colors.SidePanelBg;
            });

            var root = (Visual)
                typeof(Tui.TuiApp).GetField("_rootVisual", flags)!.GetValue(_harness.App)!;
            var dialogVisible = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!dialogVisible && DateTime.UtcNow < deadline)
            {
                dialogVisible = await Dispatcher.Current.InvokeAsync(() =>
                {
                    root.App?.RequestFullRender();
                    var app = root.App!;
                    var buffer = (CellBuffer)
                        typeof(TerminalApp).GetField("_renderBuffer", flags)!.GetValue(app)!;
                    return buffer
                        .ToMarkupLines()
                        .Any(line => line.Contains("角色新建", StringComparison.Ordinal));
                });
                if (!dialogVisible)
                    await Task.Delay(25);
            }

            Assert.True(dialogVisible, "真实 EntityEditDialog 未显示");
            var splitter = _harness
                .App.GetType()
                .GetField("_mainContent", flags)!
                .GetValue(_harness.App)!;
            var actualBackground = (Color?)
                splitter.GetType().GetProperty("BackgroundColor")!.GetValue(splitter);

            Assert.True(
                actualBackground == expectedBackground,
                $"实体弹窗显示期间主分隔条底色应保持面板色，实际 {actualBackground}，期望 {expectedBackground}"
            );
        }
        finally
        {
            await Dispatcher.Current.InvokeAsync(() =>
            {
                _harness.ThemeService.SetTheme(originalThemeId);
                return 0;
            });
        }
    }

    [Fact]
    public async Task NovelDetail_ShowsDefinitionAndWorldRulesTab()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var novelId = TuiSlashTestHarness.NovelId;

        await Dispatcher.Current.InvokeAsync(() =>
        {
            new EntityEditDialog(
                _harness.Store,
                EntityType.Novel,
                entityId: novelId,
                novelId: novelId
            ).Show();
            return 0;
        });

        var root = (Visual)
            typeof(Tui.TuiApp).GetField("_rootVisual", flags)!.GetValue(_harness.App)!;
        string? frame = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            frame = await Dispatcher.Current.InvokeAsync(() =>
            {
                root.App?.RequestFullRender();
                var buffer = (CellBuffer)
                    typeof(TerminalApp).GetField("_renderBuffer", flags)!.GetValue(root.App!)!;
                return string.Join("\n", buffer.ToMarkupLines());
            });
            if (
                frame.Contains("小说定义", StringComparison.Ordinal)
                && frame.Contains("世界规则", StringComparison.Ordinal)
            )
            {
                break;
            }
            await Task.Delay(25);
        }

        Assert.Contains("小说定义", frame, StringComparison.Ordinal);
        Assert.Contains("世界规则", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("📜 世界规则", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestFullRender_ResendsWholeScreen()
    {
        var root = (Visual)
            typeof(Tui.TuiApp)
                .GetField("_rootVisual", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(_harness.App)!;

        // 先做一次，让「本次输出」只包含这一次全量重发的效果
        await Dispatcher.Current.InvokeAsync(() =>
        {
            root.App?.RequestFullRender();
            return 0;
        });
        await Task.Delay(600);

        var before = _backend.GetOutText().Length;
        await Dispatcher.Current.InvokeAsync(() =>
        {
            root.App?.RequestFullRender();
            return 0;
        });
        await Task.Delay(600);
        var growth = _backend.GetOutText().Length - before;

        // 两次调用之间没有任何状态改变：若是差分-only，第二次应该接近 0 字节。
        // 实测整屏重发 ≈ 2.5 字节/格；140×44 = 6160 格 ⇒ 约 15 KB。
        // 这条锁是 RequestRegionRender 能修好「终端侧擦除」的前提，回归即失败。
        var cells = ScreenWidth * ScreenHeight;
        Assert.True(
            growth > cells,
            $"RequestFullRender 没有整屏重发（{cells} 格仅新增 {growth} 字节，"
                + $"约 {growth / (double)cells:F2} 字节/格）——区域重发修终端侧擦除会失效"
        );
    }
}
