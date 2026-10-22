using System.Reflection;
using Core.Data;
using Core.Novels;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Threading;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 真实 App 级指针测试：InMemoryTerminalBackend 上跑真实 TerminalApp + 鼠标事件注入，
/// 走生产输入管线（DispatchMouseEvent → HitTest → routed press/release）。
///
/// 回归锁（用户报障）：
/// 1. 除角色 tab（启动时的活动子树）外，其余 tab 折叠项点不开——根因是 ContentSwitcher
///    只把活动子项暴露给树遍历，非活动子树永远不 AttachToApp（App == null），app 侧
///    绑定依赖（Collapsible.IsExpanded → PrepareChildren/Measure）全部失效。
///    修法：分区宿主改为「全子面板常驻 + 仅切 IsVisible」。
/// 2. 展开项后鼠标移过边缘出现的黑块（无背景格）。
///
/// 注意：Terminal.Instance / Dispatcher.Current 是全局单例 —— 本类测试串行执行。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class SetUpPanelLivePointerTests : IAsyncLifetime
{
    private InMemoryTerminalBackend _backend = null!;
    private SetUpPanel _panel = null!;
    private TerminalApp _app = null!;
    private CancellationTokenSource _cts = null!;
    private Task _runTask = null!;
    private SetUpPanelLayoutTests.FakeStore _store = null!;

    private const int W = 120;
    private const int H = 44;

    public async ValueTask InitializeAsync()
    {
        _store = new SetUpPanelLayoutTests.FakeStore
        {
            Novel = new Novel
            {
                Id = "n1",
                NovelId = "n1",
                Title = "指针实机",
                Outline =
                    "全书走向：主角寻找失踪的师父，最终揭示师父主动封印灾厄，由主角继承守护使命。",
            },
        };
        _store.Characters.Add(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林长歌",
                Role = CharacterRole.Protagonist,
            }
        );
        _store.Factions.Add(
            new Faction
            {
                Id = "f1",
                NovelId = "n1",
                Name = "夜巡司",
            }
        );
        _store.Locations.Add(
            new Location
            {
                Id = "l1",
                NovelId = "n1",
                Name = "落雁城",
            }
        );
        _store.Plotlines.Add(
            new Plotline
            {
                Id = "p1",
                NovelId = "n1",
                Name = "夺宝线",
            }
        );
        _store.Items.Add(
            new Item
            {
                Id = "i1",
                NovelId = "n1",
                Name = "青铜镜",
            }
        );
        _store.Relationships.Add(
            new Relationship
            {
                Id = "r1",
                NovelId = "n1",
                CharacterAId = "c1",
                CharacterBId = "c2",
                Type = RelationshipType.Friend,
            }
        );
        _store.Scenes.Add(
            new SceneDefinition
            {
                Id = "s1",
                NovelId = "n1",
                Title = "雨夜破庙",
            }
        );
        for (var i = 2; i <= 70; i++)
            _store.Scenes.Add(
                new SceneDefinition
                {
                    Id = $"s{i}",
                    NovelId = "n1",
                    Title = $"场景{i}号",
                }
            );

        _panel = new SetUpPanel(_store, PanelThemes.Dark) { CurrentNovelId = "n1" };
        await _panel.RefreshAsync("n1");

        // 启动屏障：等上一个终端完全退出再顶掉全局实例（否则后续投递会落到别人的 UI 线程）
        await TerminalUiFixture.WaitForPreviousTerminalAsync();

        _backend = new InMemoryTerminalBackend(new TerminalSize(W, H), null);
        Terminal.Initialize(
            _backend,
            new TerminalOptions { ImplicitStartInput = true },
            force: true
        );

        var conversation = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(PanelThemes.Dark.ConversationBg),
            Content = new Markup(""),
        };
        var appRoot = new ThinSplitter(conversation, _panel.Visual)
        {
            Ratio = 0.70,
            Direction = SplitDirection.Horizontal,
            BackgroundColor = PanelThemes.Dark.SidePanelBg,
            LineColor = PanelThemes.Dark.ConversationBg,
            BarSize = 1,
        };
        _app = new TerminalApp(
            appRoot,
            Terminal.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
                EnableMouse = true,
                MouseMode = TerminalMouseMode.Move,
                DisableInputEcho = true,
                UpdateWaitDuration = TimeSpan.FromMilliseconds(1),
            }
        );
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => _app.RunAsync(_cts.Token));

        await WaitUntilAsync(
            async () => await OnUiAsync(() => _panel.Visual.Bounds.Width) > 0,
            "首帧渲染完成（面板获得布局）"
        );
    }

    public async ValueTask DisposeAsync()
    {
        await TerminalUiFixture.StopAppAsync(_cts, _runTask);
        Terminal.Close();
    }

    /// <summary>
    /// 用户流程：逐个 tab 点击分区按钮 → 点击首个折叠项 → 必须真展开
    /// （IsExpanded + 内容已 Attach + 条目高度增长，三者缺一即「点了没反应」）。
    /// </summary>
    [Fact]
    public async Task Click_Expands_Item_In_Every_Entity_Tab()
    {
        for (var section = 1; section <= 7; section++)
        {
            var button = await OnUiAsync(() =>
                GetPrivate<List<Button>>(_panel, "_sectionButtons")[section]
            );
            Assert.True(
                await OnUiAsync(() => button.Bounds.Width) > 0,
                $"section {section} 按钮未布局"
            );
            var (bx, by) = await OnUiAsync(() => (button.Bounds.X + 1, button.Bounds.Y));
            Click(bx, by);
            await WaitUntilAsync(
                async () =>
                    await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == section,
                $"切换到 section {section}"
            );

            var item = await OnUiAsync(() =>
                (Collapsible)GetAccordion(_panel, section).Children[0]
            );
            var before = await OnUiAsync(() => item.Bounds);
            Assert.True(
                before.Width > 0 && before.Height > 0,
                $"section {section} 切 tab 后折叠项未布局（Bounds={before}）"
            );

            Click(before.X + 4, before.Y);
            await WaitUntilAsync(
                async () => await OnUiAsync(() => item.IsExpanded),
                $"section {section} 点击折叠项后展开"
            );
            await WaitUntilAsync(
                async () => await OnUiAsync(() => item.Bounds.Height) > before.Height,
                $"section {section} 展开后条目高度增长"
            );

            var after = await OnUiAsync(() =>
                (item.Bounds, Attached: item.Content?.Parent is not null, App: item.App is not null)
            );
            Assert.True(
                after.Attached,
                $"section {section} 展开后内容未 Attach（content_parent=null）"
            );
            Assert.True(after.App, $"section {section} 折叠项未 Attach 到 App（绑定依赖失效）");
        }
    }

    /// <summary>
    /// 大数据量场景：模型 tab 有 70 个条目，确认列表中的第 6 条仍可点击展开。
    /// </summary>
    [Fact]
    public async Task Click_Expands_Item_With_Many_Entries()
    {
        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[5].Bounds.X + 1, buttons[5].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 5,
            "切换到场景 tab"
        );

        var item = await OnUiAsync(() => (Collapsible)GetAccordion(_panel, 5).Children[5]);
        var before = await OnUiAsync(() => item.Bounds);
        Assert.True(
            before.Height > 0,
            $"第 6 条未布局（Bounds={before}），accordion={await OnUiAsync(() => GetAccordion(_panel, 4).Bounds)}"
        );

        Click(before.X + 4, before.Y);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => item.IsExpanded),
            "点击第 6 条后展开"
        );
        await WaitUntilAsync(
            async () => await OnUiAsync(() => item.Bounds.Height) > before.Height,
            "第 6 条展开后高度增长"
        );
    }

    /// <summary>
    /// 黑块场景（用户报障 1）：展开关系 tab 一项、鼠标移过展开区边缘后，
    /// 真实帧内不得出现无背景格（终端默认黑底漏出 = 黑块）。
    /// </summary>
    [Fact]
    public async Task HoverOverExpandedItem_Leaves_No_Undrawn_Cells()
    {
        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[7].Bounds.X + 1, buttons[7].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 7,
            "切换到关系 tab"
        );

        var item = await OnUiAsync(() => (Collapsible)GetAccordion(_panel, 7).Children[0]);
        var bounds = await OnUiAsync(() => item.Bounds);
        Click(bounds.X + 4, bounds.Y);
        await WaitUntilAsync(async () => await OnUiAsync(() => item.IsExpanded), "展开关系项");
        var expanded = await WaitUntilSizeAsync(item, bounds.Height);

        // 鼠标移过展开区右缘与底部
        Move(expanded.Right - 1, expanded.Y);
        await Task.Delay(80);
        Move(expanded.X + 2, expanded.Bottom - 1);
        await Task.Delay(150);

        var lines = await CaptureFrameAsync();

        var bad = RealFrameBlackBlockTests.UndrawnCells(lines);
        Assert.True(
            bad.Count == 0,
            $"展开+悬停后存在无背景格 {bad.Count} 个，首批: {string.Join(", ", bad.Take(8))}"
        );
    }

    /// <summary>
    /// 实体列表溢出（场景 tab 70 条 → 列表出现滚动条）时，真实帧仍不得出现无背景格（黑块防线）。
    /// </summary>
    [Fact]
    public async Task Overflowing_EntityList_Leaves_No_Undrawn_Cells()
    {
        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[4].Bounds.X + 1, buttons[4].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 4,
            "切换到场景 tab"
        );
        await Task.Delay(200);
        var lines = await CaptureFrameAsync();
        var bad = RealFrameBlackBlockTests.UndrawnCells(lines);
        Assert.True(
            bad.Count == 0,
            $"存在无背景格 {bad.Count} 个，首批: {string.Join(", ", bad.Take(8))}"
        );
    }

    /// <summary>真实 TerminalApp 输入管线中的鼠标滚轮必须驱动场景列表滚动，并保持在可滚范围内。</summary>
    [Fact]
    public async Task WheelOverSceneList_ScrollsDownWithinBounds()
    {
        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[5].Bounds.X + 1, buttons[5].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 5,
            "切换到场景 tab"
        );

        var scroll = await OnUiAsync(() =>
            GetPrivate<List<BackgroundPanel>>(_panel, "_tabContentPanels")[5]
                .EnumerateVisualsDepthFirst()
                .OfType<ScrollViewer>()
                .Single()
        );
        var (bounds, initialOffset, max) = await OnUiAsync(() =>
            (scroll.Bounds, scroll.VerticalOffset, SetUpPanel.MaxVerticalOffset(scroll))
        );
        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"场景滚动区未布局：{bounds}");
        Assert.True(max > 0, "70 个场景应超出当前视口");
        Assert.Equal(0, initialOffset);

        _backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Wheel,
                Button = TerminalMouseButton.None,
                WheelDelta = -1,
                X = bounds.X + bounds.Width / 2,
                Y = bounds.Y + bounds.Height / 2,
            }
        );
        await WaitUntilAsync(
            async () => await OnUiAsync(() => scroll.VerticalOffset > initialOffset),
            "场景列表响应鼠标滚轮"
        );

        var (offset, maxAfter) = await OnUiAsync(() =>
            (scroll.VerticalOffset, SetUpPanel.MaxVerticalOffset(scroll))
        );
        Assert.Equal(3, offset);
        Assert.InRange(offset, 0, maxAfter);
    }

    /// <summary>
    /// 搜索框真机路径（位于内容区、tab 下方）：点击搜索框 → 键入文本 → 当前 tab 列表即时过滤；
    /// 分区栏保持可见（搜索在内容区，不替换 tab 视图）；未命中给出空态行。
    /// </summary>
    [Fact]
    public async Task SearchBox_Typing_Filters_CurrentTab()
    {
        var box = await OnUiAsync(() => GetPrivate<TextBox>(_panel, "_searchBox"));
        var bounds = await OnUiAsync(() => box.Bounds);
        Assert.True(bounds.Width > 0, $"搜索框未布局：{bounds}");
        Click(bounds.X + 2, bounds.Y);

        _backend.PushEvent(new TerminalTextEvent { Text = "林" });
        await WaitUntilAsync(async () => await OnUiAsync(() => box.Text) == "林", "搜索框收到键入");
        await WaitUntilAsync(
            async () =>
            {
                var (count, texts, barVisible) = await OnUiAsync(() =>
                {
                    var children = GetAccordion(_panel, 0).Children.ToList();
                    var texts =
                        children.Count == 1 ? SetUpPanelLayoutTests.CollectTexts(children[0]) : [];
                    return (
                        children.Count,
                        texts,
                        GetPrivate<WrapHStack>(_panel, "_sectionBar").IsVisible
                    );
                });
                return count == 1 && texts.Any(t => t.Contains("林长歌")) && barVisible;
            },
            "角色 tab 过滤到唯一命中，且分区栏仍可见（搜索在内容区）"
        );

        _backend.PushEvent(new TerminalTextEvent { Text = "zzz" });
        await WaitUntilAsync(
            async () =>
            {
                var header = await OnUiAsync(() =>
                {
                    var children = GetAccordion(_panel, 0).Children.ToList();
                    return
                        children.Count == 1 && children[0] is Collapsible c && c.Header is Markup m
                        ? m.Text
                        : "";
                });
                return header.Contains("未命中");
            },
            "未命中查询显示空态行"
        );
    }

    /// <summary>
    /// 大纲 tab 真实帧：层级根「小说行」（书名 + 行尾 ＋ / ⋯）渲染在最上面；
    /// 点 ⋯ 弹出行动作菜单（详情 · 检查 · 删除）。
    /// </summary>
    [Fact]
    public async Task OutlineTab_Renders_NovelRow_WithMenuEntry()
    {
        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[0].Bounds.X + 1, buttons[0].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 0,
            "切换到大纲 tab"
        );

        var lines = await CaptureFrameAsync();
        var text = string.Join(
            "\n",
            lines.Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\[[^\[\]]*\]", ""))
        );
        Assert.Contains("指针实机", text, StringComparison.Ordinal); // 小说行标题 = 当前小说
        Assert.Contains("⋯", text, StringComparison.Ordinal); // 行尾菜单入口

        // 点 ⋯ → 菜单弹出（详情 / 检查 / 删除都在菜单里，行上不再有抽象小图标）
        var menuButton = await OnUiAsync(() =>
        {
            var novel = GetPrivate<TreeView>(_panel, "_outlineTree").Roots[0];
            return novel
                .RightVisuals.Select(rv => rv.Visual)
                .OfType<Button>()
                .Single(b => ((Markup?)b.Content)?.Text.Contains("⋯") == true);
        });
        var (mx, my) = await OnUiAsync(() => (menuButton.Bounds.X + 1, menuButton.Bounds.Y));
        Click(mx, my);
        await WaitUntilAsync(
            async () =>
                await OnUiAsync(() => GetPrivate<object?>(_panel, "_rowMenuPopup") is not null),
            "⋯ 菜单弹出"
        );
        var menuText = string.Join(
            "\n",
            (await CaptureFrameAsync()).Select(l =>
                System.Text.RegularExpressions.Regex.Replace(l, @"\[[^\[\]]*\]", "")
            )
        );
        Assert.Contains("详情", menuText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 书级 ⋯ 菜单（真实指针走完「点 ⋯ → 弹层 → 点菜单项」）：只剩「📄 详情 / ❌ 删除」，
    /// 点「📄 详情」必须触发 OnEditNovel（宿主据此开小说信息编辑器）。
    /// 「🔍 检查」入口已按用户要求从书/卷菜单移除，这里一并钉住它不再出现。
    /// </summary>
    [Fact]
    public async Task OutlineMenu_DetailEntry_FiresEditNovel_AndNoCheckEntry()
    {
        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[0].Bounds.X + 1, buttons[0].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 0,
            "切换到大纲 tab"
        );

        var editNovelFired = 0;
        await OnUiAsync(() =>
        {
            _panel.OnEditNovel += (_, _) => editNovelFired++;
            return true;
        });

        var novel = await OnUiAsync(() => GetPrivate<TreeView>(_panel, "_outlineTree").Roots[0]);
        await OnUiAsync(() =>
        {
            novel.IsExpanded = false;
            return 0;
        });
        var menuButton = await OnUiAsync(() =>
            novel
                .RightVisuals.Select(rv => rv.Visual)
                .OfType<Button>()
                .Single(b => ((Markup?)b.Content)?.Text.Contains("⋯") == true)
        );
        var (mx, my) = await OnUiAsync(() => (menuButton.Bounds.X + 1, menuButton.Bounds.Y));
        Click(mx, my);
        await WaitUntilAsync(
            async () =>
                await OnUiAsync(() =>
                    GetPrivate<AnchoredPopup<SetUpPanel>>(_panel, "_rowMenuPopup").List.Bounds.Width
                    > 0
                ),
            "小说行菜单布局"
        );

        var (menuBounds, detailIndex, labels) = await OnUiAsync(() =>
        {
            var list = GetPrivate<AnchoredPopup<SetUpPanel>>(_panel, "_rowMenuPopup").List;
            return (list.Bounds, list.Items.IndexOf("📄 详情"), list.Items.ToArray());
        });
        Assert.DoesNotContain("🔍 检查", labels); // 用户要求：书级菜单不再有「检查」
        Assert.True(detailIndex >= 0 && menuBounds.Width > 4, "详情菜单项未布局");
        Click(menuBounds.X + 4, menuBounds.Y + detailIndex);

        await WaitUntilAsync(
            async () => await OnUiAsync(() => editNovelFired) > 0,
            "OnEditNovel 触发"
        );
    }

    /// <summary>
    /// 用真实 FileNovelStore + 仓库里的真实小说数据复现用户报障：
    /// 未分卷行显示「11 章 · 36 字」，展开却看不到章行。
    /// </summary>
    [Fact]
    public async Task UnassignedGroup_RealData_ShowsChapterRows()
    {
        var novelsRoot = Path.GetFullPath(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "..",
                "..",
                "..",
                "..",
                "..",
                "..",
                "novels"
            )
        );

        // 该用例回归的是本机真实小说数据（novels/2c2410d54e08，未入库）：
        // 数据缺失时跳过而不是硬失败（与 TerminalOutputFidelityTests 的 Skip 同模式）。
        if (!Directory.Exists(Path.Combine(novelsRoot, "2c2410d54e08")))
            Assert.Skip("缺少本机真实小说数据（novels/2c2410d54e08），跳过真实数据回归");

        var dump = await OnUiAsync<
            Task<(string header, int childCount, string before, string after)>
        >(async () =>
            {
                var store = new FileNovelStore(novelsRoot);
                var panel = new SetUpPanel(store, PanelThemes.Dark)
                {
                    CurrentNovelId = "2c2410d54e08",
                };
                await panel.RefreshAsync("2c2410d54e08");

                var tree = GetPrivate<TreeView>(panel, "_outlineTree");
                tree.Measure(new LayoutConstraints(0, 40, 0, 60));
                tree.Arrange(new Rectangle(0, 0, 40, 60));

                var unassigned = tree.Roots[0]
                    .Children.First(n =>
                        n.Data is OutlineRow { Kind: OutlineRowKind.UnassignedHeader }
                    );
                var header = ((Markup)unassigned.Header).Text ?? "";
                var childCount = unassigned.Children.Count;
                var before = string.Join(
                    " | ",
                    unassigned.Children.Select(c => ((Markup)c.Header).Text)
                );

                unassigned.IsExpanded = true;
                tree.Measure(new LayoutConstraints(0, 40, 0, 60));
                tree.Arrange(new Rectangle(0, 0, 40, 60));
                var after = string.Join(
                    " | ",
                    unassigned.Children.Select(c => ((Markup)c.Header).Text)
                );

                return (header, childCount, before, after);
            })
            .Unwrap();

        // 表头计数与真实章数一致，且章行确实挂在未分卷节点下
        Assert.Contains("11 章", dump.header);
        Assert.Equal(11, dump.childCount);
        Assert.Contains("第 1 章", dump.before);
        Assert.Contains("第 11 章", dump.before);
        Assert.Contains("三魂归一", dump.before);
    }

    /// <summary>
    /// 用户报障：点「未分卷」行无效果、不展开。
    /// 库缺陷：TreeView 只在点击 X 精确等于展开符那一格时才切换（contentX == expanderX），
    /// 点图标/标题只算选中。应用侧已把整行（除右侧动作按钮）变成展开开关——这里锁住它：
    /// 点行中段的文字也必须展开。
    /// </summary>
    [Fact]
    public async Task UnassignedRow_ClickOnText_TogglesExpansion()
    {
        await OnUiAsync(() =>
        {
            _store.Chapters.Add(
                new Chapter
                {
                    Id = "t1",
                    NovelId = "n1",
                    Title = "点行展开章",
                    Order = 1,
                    Content = "正文",
                }
            );
            return _panel.RefreshAsync("n1");
        });
        await Task.Delay(300);

        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[0].Bounds.X + 1, buttons[0].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 0,
            "切换到大纲 tab"
        );
        await Task.Delay(200);

        var unassigned = await OnUiAsync(() =>
            GetPrivate<TreeView>(_panel, "_outlineTree")
                .Roots[0]
                .Children.First(n => n.Data is OutlineRow { Kind: OutlineRowKind.UnassignedHeader })
        );
        Assert.False(await OnUiAsync(() => unassigned.IsExpanded), "初始应为折叠");

        // 点行的文字中段（远离左侧展开符、远离右侧 ＋ 按钮）
        var (hx, hy) = await OnUiAsync(() =>
        {
            var header = unassigned.Header;
            return (header.Bounds.X + header.Bounds.Width / 2, header.Bounds.Y);
        });
        Click(hx, hy);
        await Task.Delay(250);

        Assert.True(
            await OnUiAsync(() => unassigned.IsExpanded),
            "点未分卷行的文字也必须展开（库只在展开符那一格响应）"
        );
        // 侧栏窄，章标题会被截断（「点行展开章」→「点行展开」），用前缀匹配
        var frame = await CaptureFrameAsync();
        Assert.Contains(frame, l => l.Contains("点行展开"));

        // 再点一次 → 收起（开关）。坐标要重取：展开后行布局变了
        var (hx2, hy2) = await OnUiAsync(() =>
        {
            var header = unassigned.Header;
            return (header.Bounds.X + header.Bounds.Width / 2, header.Bounds.Y);
        });
        Click(hx2, hy2);
        await Task.Delay(250);
        Assert.False(await OnUiAsync(() => unassigned.IsExpanded), "再点一次应收起");
    }

    /// <summary>
    /// 点库自己那一格展开符，行为必须和库一致（只切换一次）——
    /// 应用侧绕行不能和库的 ToggleExpand 叠加成「切两次互相抵消」。
    /// </summary>
    [Fact]
    public async Task UnassignedRow_ClickOnExpanderGlyph_TogglesOnce()
    {
        await OnUiAsync(() =>
        {
            _store.Chapters.Add(
                new Chapter
                {
                    Id = "g1",
                    NovelId = "n1",
                    Title = "展开符章",
                    Order = 1,
                    Content = "正文",
                }
            );
            return _panel.RefreshAsync("n1");
        });
        await Task.Delay(300);

        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[0].Bounds.X + 1, buttons[0].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 0,
            "切换到大纲 tab"
        );
        await Task.Delay(200);

        var unassigned = await OnUiAsync(() =>
            GetPrivate<TreeView>(_panel, "_outlineTree")
                .Roots[0]
                .Children.First(n => n.Data is OutlineRow { Kind: OutlineRowKind.UnassignedHeader })
        );
        Assert.False(await OnUiAsync(() => unassigned.IsExpanded), "初始应为折叠");

        // 命中库公式算出的展开符那一格：markerWidth(1) + depth(2) * indentSize(2) = 5（相对树左上角）
        var (ex, ey) = await OnUiAsync(() =>
        {
            var tree = GetPrivate<TreeView>(_panel, "_outlineTree");
            var header = unassigned.Header;
            return (tree.Bounds.X + 5, header.Bounds.Y);
        });
        Click(ex, ey);
        await Task.Delay(250);

        Assert.True(
            await OnUiAsync(() => unassigned.IsExpanded),
            "点展开符那一格必须正好切换一次（应用绕行不能和库叠加抵消）"
        );
    }

    /// <summary>
    /// 用户报障：改完章节状态，展开的「未分卷」组自动缩回去了。
    /// 根因：BuildUnassignedNode 没把节点登记进 _volumeNodes，而 CaptureExpansionState
    /// 只遍历那张表回收展开态 → 重建（保存→刷新）时展开态丢失。
    /// </summary>
    [Fact]
    public async Task UnassignedGroup_StaysExpanded_AfterRebuild()
    {
        await OnUiAsync(() =>
        {
            _store.Chapters.Add(
                new Chapter
                {
                    Id = "k1",
                    NovelId = "n1",
                    Title = "状态章",
                    Order = 1,
                    Content = "正文",
                }
            );
            return _panel.RefreshAsync("n1");
        });
        await Task.Delay(300);

        var buttons = await OnUiAsync(() => GetPrivate<List<Button>>(_panel, "_sectionButtons"));
        var (bx, by) = await OnUiAsync(() => (buttons[0].Bounds.X + 1, buttons[0].Bounds.Y));
        Click(bx, by);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => GetPrivate<int>(_panel, "_sectionIndex")) == 0,
            "切换到大纲 tab"
        );
        await Task.Delay(200);

        var unassigned = await OnUiAsync(() =>
            GetPrivate<TreeView>(_panel, "_outlineTree")
                .Roots[0]
                .Children.First(n => n.Data is OutlineRow { Kind: OutlineRowKind.UnassignedHeader })
        );
        await OnUiAsync(() =>
        {
            unassigned.IsExpanded = true;
            return true;
        });
        await Task.Delay(150);
        Assert.True(await OnUiAsync(() => unassigned.IsExpanded), "先展开");

        // 改章节状态 → 触发保存 → 面板刷新 → 大纲树重建
        await OnUiAsync(() =>
        {
            var ch = _store.Chapters.First(c => c.Id == "k1");
            ch.Status = ChapterStatus.Published;
            return _store.SaveChapter(ch);
        });
        await OnUiAsync(() => _panel.RefreshAsync("n1"));
        await Task.Delay(400);

        var stillExpanded = await OnUiAsync(() =>
            GetPrivate<TreeView>(_panel, "_outlineTree")
                .Roots[0]
                .Children.First(n => n.Data is OutlineRow { Kind: OutlineRowKind.UnassignedHeader })
                .IsExpanded
        );
        Assert.True(stillExpanded, "改章节状态后未分卷组必须保持展开（重建丢了展开态）");
    }

    private async Task<IReadOnlyList<string>> CaptureFrameAsync()
    {
        await OnUiAsync(() =>
        {
            _app.RequestFullRender();
            return 0;
        });
        await Task.Delay(120);
        return await OnUiAsync(() => GetRenderBuffer(_app).ToMarkupLines());
    }

    private async Task<Rectangle> WaitUntilSizeAsync(Collapsible item, int minHeight)
    {
        Rectangle bounds = default;
        await WaitUntilAsync(
            async () =>
            {
                bounds = await OnUiAsync(() => item.Bounds);
                return bounds.Height > minHeight;
            },
            "展开区获得布局"
        );
        return bounds;
    }

    /// <summary>取真实 TerminalApp 的当前帧渲染缓冲（库内测试同款反射）。</summary>
    private static CellBuffer GetRenderBuffer(TerminalApp app) =>
        (CellBuffer)
            typeof(TerminalApp)
                .GetField("_renderBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(app)!;

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

    private static Accordion GetAccordion(SetUpPanel panel, int section) =>
        GetPrivate<Accordion>(
            panel,
            section switch
            {
                1 => "_characterAccordion",
                2 => "_factionAccordion",
                3 => "_locationAccordion",
                4 => "_plotlineAccordion",
                5 => "_sceneAccordion",
                6 => "_itemAccordion",
                _ => "_relationshipAccordion",
            }
        );

    private static T GetPrivate<T>(SetUpPanel panel, string field) =>
        (T)
            typeof(SetUpPanel)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel)!;

    /// <summary>在 UI 线程上执行并取回结果（TerminalApp 线程亲和）。</summary>
    private static Task<T> OnUiAsync<T>(Func<T> action) => TerminalUiFixture.OnUiAsync(action);

    private static Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string what,
        int timeoutMs = 10000
    ) => TerminalUiFixture.WaitUntilAsync(condition, what, timeoutMs);
}
