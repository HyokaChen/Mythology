using System.Net;
using System.Reflection;
using System.Text;
using ClientPlatform;
using LlmTornado;
using LlmTornado.Code;
using Tui.Controls;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Threading;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 全部斜杠命令的真终端集成测试：InMemoryTerminalBackend 虚拟终端上运行真实 TerminalApp，
/// 经真实输入路径（设置 _inputText + SubmitInput → HandleSlashCommand）驱动每条命令，
/// 断言基于 ①虚拟终端渲染输出（对话框标题/Toast 文本）②应用状态（主题/会话/云端/Agent 消息/存储）。
///
/// 这是唯一能端到端覆盖弹框命令（/state /history /commands /help /session /tree /tools /
/// /theme /login /cloud）的路径：TerminalApp 绑定 Dispatcher 后 Dialog.Show / ToastService /
/// await 续体（DispatcherSynchronizationContext）全部真实执行。
///
/// 约束：Terminal.Instance 是全局单例——本类所有测试共用类内顺序执行（xunit 单类内串行，
/// 全局 parallelizeTestCollections=false），每个测试独立初始化/关闭终端。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class TuiAppTerminalSlashCommandIntegrationTests : TerminalUiFixture
{
    private TuiSlashTestHarness _harness = null!;
    private string? _previousCloudUrl;

    protected override TerminalSize Size => new(140, 44);

    protected override ValueTask OnPreparingAsync()
    {
        _previousCloudUrl = Environment.GetEnvironmentVariable("MYTHOLOGY_CLOUD_URL");
        // 云端登录测试：指向必然拒绝的端口，避免 OAuth 发现命中真实服务/打开浏览器
        Environment.SetEnvironmentVariable("MYTHOLOGY_CLOUD_URL", "http://127.0.0.1:1");
        return ValueTask.CompletedTask;
    }

    protected override Func<CancellationToken, Task> CreateAppRunner()
    {
        _harness = new TuiSlashTestHarness(
            novelId: TuiSlashTestHarness.NovelId,
            sessionId: TuiSlashTestHarness.SessionId,
            cloudUsage: CreateUsageClient()
        );
        return _harness.App.RunAsync;
    }

    protected override async ValueTask OnReadyToRunAsync()
    {
        // 种子会话（6 条消息）必须在 RunAsync 前完成：startup OpenNovelAsync 恢复该会话
        await _harness.SeedSessionStoreAsync();
        _harness.Registry.Register(new FakeAgentTool("read_file", "读取文件"), "Test Plugin");
        _harness.Registry.Register(new FakeAgentTool("write_file", "写入文件"), "Test Plugin");
    }

    protected override async ValueTask OnStartedAsync()
    {
        await WaitUntilAsync(
            () => DispatcherAttached() && _harness.CurrentSessionId is not null,
            "TerminalApp 启动并完成首帧（Dispatcher 绑定 + 会话恢复）",
            20000
        );
    }

    protected override async ValueTask OnStoppingAsync()
    {
        await _harness.DisposeAsync();
        Environment.SetEnvironmentVariable("MYTHOLOGY_CLOUD_URL", _previousCloudUrl);
    }

    /// <summary>真实输入路径：在 UI 线程设置输入文本并回车（SubmitInput → HandleSlashCommand）。</summary>
    private Task DispatchAsync(string input) =>
        Dispatcher.Current.InvokeAsync(() => _harness.SubmitInput(input));

    /// <summary>轮询虚拟终端渲染输出出现全部子串（对话框标题/Toast 文本）。</summary>
    private async Task WaitForOutputAsync(params string[] substrings)
    {
        await WaitUntilAsync(
            () => substrings.All(s => Backend.GetOutText().Contains(s, StringComparison.Ordinal)),
            $"终端渲染输出未出现: {string.Join(" | ", substrings)}",
            15000
        );
    }

    /// <summary>
    /// 桩用量客户端：:cloud status 的积分余量查询固定回本载荷（不发真实请求；该命令的
    /// 真实 HTTP 副作用由 TuiAppCloudUsageTests 覆盖，此处只验证 Toast 渲染）。
    /// </summary>
    private static UsageRestClient CreateUsageClient() =>
        new(
            "http://localhost",
            () => "mk_test_public-secret",
            http: new HttpClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"period":"2026-09","plan":"creator","included-units":5000,"used-units":87,
                         "reserved-units":0,"remaining-units":4913,"breakdown":{}}
                        """,
                        Encoding.UTF8,
                        "application/json"
                    ),
                })
            )
        );

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(responder(request));
    }

    [Fact]
    public async Task EntityDialogInputSurface_MatchesDialogContentSurface()
    {
        var promptEditor = (MythPromptEditor)
            typeof(TuiApp)
                .GetField("_promptEditor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_harness.App)!;
        var app = promptEditor.App!;

        await Dispatcher.Current.InvokeAsync(() =>
        {
            _harness.ThemeService.SetTheme(ThemeRegistry.Desert.Id);
            new EntityEditDialog(
                _harness.Store,
                Core.Data.EntityType.Character,
                novelId: TuiSlashTestHarness.NovelId
            ).Show();
        });
        await WaitForOutputAsync("角色新建");
        await Task.Delay(100);
        var focusRemainsInsideDialog = await Dispatcher.Current.InvokeAsync(() =>
        {
            var dialog = app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Single();
            var focused = app.FocusedElement;
            return focused is not null
                && dialog
                    .EnumerateVisualsDepthFirst()
                    .Any(visual => ReferenceEquals(visual, focused));
        });
        Assert.True(
            focusRemainsInsideDialog,
            "EntityEditDialog 打开后焦点应留在 dialog 子树内，而不是底层输入框"
        );

        var (dialogBounds, inputBounds, dialogSurface, input) =
            await Dispatcher.Current.InvokeAsync(() =>
            {
                var dialog = app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Single();
                var input = dialog
                    .EnumerateVisualsDepthFirst()
                    .OfType<TextBox>()
                    .First(control => control.Bounds.Width > 0);
                return (
                    dialog.Bounds,
                    input.Bounds,
                    _harness.ThemeService.Current.Colors.DialogContentBg,
                    input
                );
            });

        Assert.True(inputBounds.Width > 1, $"文本输入框宽度异常：{inputBounds}");
        var clickX = inputBounds.X + 1;
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Move,
                Button = TerminalMouseButton.None,
                X = clickX,
                Y = inputBounds.Y,
            }
        );
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Down,
                Button = TerminalMouseButton.Left,
                X = clickX,
                Y = inputBounds.Y,
            }
        );
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Up,
                Button = TerminalMouseButton.Left,
                X = clickX,
                Y = inputBounds.Y,
            }
        );
        await Task.Delay(100);
        Assert.True(
            await Dispatcher.Current.InvokeAsync(() => ReferenceEquals(app.FocusedElement, input)),
            "点击覆盖 SetUpPanel 的输入框后没有获得焦点"
        );
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Move,
                Button = TerminalMouseButton.None,
                X = dialogBounds.Right + 1,
                Y = inputBounds.Y,
            }
        );
        await Task.Delay(50);

        await Dispatcher.Current.InvokeAsync(() => app.RequestFullRender());
        await Task.Delay(100);
        var edgeCells = await Dispatcher.Current.InvokeAsync(() =>
        {
            var buffer = (CellBuffer)
                typeof(TerminalApp)
                    .GetField("_renderBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(app)!;
            var styles = (Style[])
                typeof(CellBuffer)
                    .GetField("_cells", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(buffer)!;
            var row = inputBounds.Y * buffer.Width;
            return Enumerable
                .Range(inputBounds.X, inputBounds.Width)
                .Select(x =>
                {
                    var hasBackground = styles[row + x].TryGetBackground(out var background);
                    return (X: x, HasBackground: hasBackground, Background: background);
                })
                .ToArray();
        });

        Assert.NotEmpty(edgeCells);
        Assert.All(
            edgeCells,
            cell =>
            {
                Assert.True(
                    cell.HasBackground,
                    $"实体输入框 ({cell.X},{inputBounds.Y}) 没有显式背景"
                );
                Assert.Equal(dialogSurface, cell.Background);
            }
        );
    }

    [Fact]
    public async Task EntityLocationSelectPopup_MouseMoves_NoDefaultBlackCells()
    {
        var promptEditor = (MythPromptEditor)
            typeof(TuiApp)
                .GetField("_promptEditor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_harness.App)!;
        var app = promptEditor.App!;
        await Dispatcher.Current.InvokeAsync(() =>
        {
            _harness.ThemeService.SetTheme(ThemeRegistry.Desert.Id);
            new EntityEditDialog(
                _harness.Store,
                Core.Data.EntityType.Location,
                novelId: TuiSlashTestHarness.NovelId
            ).Show();
        });
        await WaitForOutputAsync("地点新建");

        var selectBounds = await Dispatcher.Current.InvokeAsync(() =>
            app.Root.EnumerateVisualsDepthFirst().OfType<MythSelect>().First().Bounds
        );
        Assert.True(
            selectBounds.Width > 1 && selectBounds.Height > 0,
            $"地点类型下拉未布局：{selectBounds}"
        );

        var clickX = selectBounds.X + selectBounds.Width / 2;
        var clickY = selectBounds.Y + selectBounds.Height / 2;
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Down,
                Button = TerminalMouseButton.Left,
                X = clickX,
                Y = clickY,
            }
        );
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Up,
                Button = TerminalMouseButton.Left,
                X = clickX,
                Y = clickY,
            }
        );
        await WaitForOutputAsync("乡村");

        var (dialogBounds, popupBounds, popupList) = await Dispatcher.Current.InvokeAsync(() =>
        {
            var dialog = app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Single();
            var list = app
                .Root.EnumerateVisualsDepthFirst()
                .OfType<OptionList<string>>()
                .Last(candidate => candidate.Bounds.Width > 0);
            return (dialog.Bounds, list.Bounds, list);
        });
        Assert.Equal(76, dialogBounds.Width);
        Assert.True(
            dialogBounds.Right + 1 < Backend.GetSize().Columns,
            "弹窗外鼠标坐标必须仍在终端视口内"
        );
        Assert.True(
            popupBounds.Width > 1 && popupBounds.Height > 0,
            $"地点类型选项弹层未布局：{popupBounds}"
        );
        Assert.Same(popupList, await Dispatcher.Current.InvokeAsync(() => app.FocusedElement));

        foreach (var y in Enumerable.Range(popupBounds.Y, popupBounds.Height))
        {
            foreach (var x in new[] { popupBounds.X + 1, popupBounds.X + popupBounds.Width - 2 })
            {
                Backend.PushEvent(
                    new TerminalMouseEvent
                    {
                        Kind = TerminalMouseKind.Move,
                        Button = TerminalMouseButton.None,
                        X = x,
                        Y = y,
                    }
                );
                await Task.Delay(25);
                Assert.Same(
                    popupList,
                    await Dispatcher.Current.InvokeAsync(() => app.FocusedElement)
                );
                var lines = await Dispatcher.Current.InvokeAsync(() =>
                    (
                        (CellBuffer)
                            typeof(TerminalApp)
                                .GetField(
                                    "_renderBuffer",
                                    BindingFlags.Instance | BindingFlags.NonPublic
                                )!
                                .GetValue(app)!
                    ).ToMarkupLines()
                );
                var black = RealFrameBlackBlockTests.BlackCells(lines);
                Assert.True(
                    black.Count == 0,
                    $"鼠标位于下拉选项 ({x},{y}) 时渲染出黑底格：{string.Join(", ", black.Take(8))}"
                );
            }
        }
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Move,
                Button = TerminalMouseButton.None,
                X = dialogBounds.Right + 1,
                Y = popupBounds.Y,
            }
        );
        await Task.Delay(25);
        Assert.Same(popupList, await Dispatcher.Current.InvokeAsync(() => app.FocusedElement));
        var outsideLines = await Dispatcher.Current.InvokeAsync(() =>
            (
                (CellBuffer)
                    typeof(TerminalApp)
                        .GetField("_renderBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(app)!
            ).ToMarkupLines()
        );
        var edgeBlack = RealFrameBlackBlockTests.BlackCells(outsideLines);
        Assert.True(
            edgeBlack.Count == 0,
            $"鼠标离开对话框时渲染出黑底格：{string.Join(", ", edgeBlack.Take(8))}"
        );
        var undrawn = RealFrameBlackBlockTests.UndrawnCells(outsideLines);
        Assert.True(
            undrawn.Count == 0,
            $"鼠标离开对话框时存在未绘制格：{string.Join(", ", undrawn.Take(8))}"
        );
    }

    [Fact]
    public async Task NovelCreationDialog_SavesEnteredTotalOutline()
    {
        var promptEditor = (MythPromptEditor)
            typeof(TuiApp)
                .GetField("_promptEditor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_harness.App)!;
        var app = promptEditor.App!;

        const string title = "小说总纲入口回归";
        const string outline = "全书走向：主角寻找失踪的师父";
        string? openedNovelId = null;

        await Dispatcher.Current.InvokeAsync(() =>
        {
            _harness.Store.Novels.Clear();
            new NovelProjectDialog(
                _harness.Store,
                _harness.SessionStore,
                (id, _) => openedNovelId = id
            ).Show();
        });
        await WaitForOutputAsync("新建小说项目", "总纲");

        var createBounds = await Dispatcher.Current.InvokeAsync(() =>
        {
            var dialog = app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Single();
            var titleInput = dialog
                .EnumerateVisualsDepthFirst()
                .OfType<TextBox>()
                .First(control => control.Bounds.Width > 0);
            var outlineInput = dialog
                .EnumerateVisualsDepthFirst()
                .OfType<TextArea>()
                .Single(control => control.Bounds.Width > 0);
            titleInput.Text = title;
            outlineInput.Text = outline;

            return dialog
                .EnumerateVisualsDepthFirst()
                .OfType<Button>()
                .Single(button =>
                    button.Content is Markup content
                    && content.Text?.Contains("创建", StringComparison.Ordinal) == true
                )
                .Bounds;
        });
        Assert.True(
            createBounds.Width > 0 && createBounds.Height > 0,
            $"创建按钮未布局：{createBounds}"
        );

        var x = createBounds.X + createBounds.Width / 2;
        var y = createBounds.Y + createBounds.Height / 2;
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Down,
                Button = TerminalMouseButton.Left,
                X = x,
                Y = y,
            }
        );
        Backend.PushEvent(
            new TerminalMouseEvent
            {
                Kind = TerminalMouseKind.Up,
                Button = TerminalMouseButton.Left,
                X = x,
                Y = y,
            }
        );
        await WaitForOutputAsync($"已创建：{title}");

        var created = await Dispatcher.Current.InvokeAsync(() =>
            _harness.Store.Novels.Values.Single(novel => novel.Title == title)
        );
        Assert.Equal(outline, created.Outline);
        Assert.Equal(created.Id, await Dispatcher.Current.InvokeAsync(() => openedNovelId));
    }

    // ── 弹框命令：断言真实渲染的对话框标题/内容 ──

    [Fact]
    public async Task Commands_OpensCommandListDialog()
    {
        await DispatchAsync("/commands");
        await WaitForOutputAsync("命令列表", "可用命令");
    }

    [Fact]
    public async Task Help_OpensAboutDialog()
    {
        await DispatchAsync("/help");
        await WaitForOutputAsync("Mythology");
    }

    [Fact]
    public async Task State_OpensAgentStateDialog()
    {
        await DispatchAsync("/state");
        await WaitForOutputAsync("Agent 状态", "Model");
    }

    [Fact]
    public async Task History_OpensHistoryDialog()
    {
        await DispatchAsync("/history");
        await WaitForOutputAsync("对话历史");
    }

    [Fact]
    public async Task Session_OpensSessionPickerDialog()
    {
        await DispatchAsync("/session");
        await WaitForOutputAsync("会话列表");
    }

    [Fact]
    public async Task Read_OpensChapterReader_WithSynopsisAndContent()
    {
        SeedChaptersForReader();

        await DispatchAsync("/read 1");

        // 阅读器对话框：标题（第 N 章 标题）+ 正文 + 细纲带
        await WaitForOutputAsync("雨夜寄北", "发现密信", "雨落下来");
    }

    [Fact]
    public async Task Chapter_Jump_ShowsToast_AndAmbiguousQueryListsCandidates()
    {
        SeedChaptersForReader();

        await DispatchAsync("/chapter 进城");
        await WaitForOutputAsync("已定位到第 2 章");

        await DispatchAsync("/chapter 雨夜"); // 两章命中 → 列候选，不猜
        await WaitForOutputAsync("匹配到 2 章");
    }

    [Fact]
    public async Task Search_FindsAcrossChapters_AndReportsEmptyResult()
    {
        SeedChaptersForReader();

        await DispatchAsync("/search 密信");

        // 结果对话框：命中章标题 + 字段来源
        await WaitForOutputAsync("密信", "章命中");
    }

    [Fact]
    public async Task Search_NoHit_ShowsActionableToast()
    {
        SeedChaptersForReader();

        await DispatchAsync("/search 完全不存在的内容");

        await WaitForOutputAsync("没有找到包含");
    }

    /// <summary>章节阅读/定位测试的种子数据（含细纲与正文）。</summary>
    private void SeedChaptersForReader()
    {
        _harness.Store.Chapters.Add(
            new Core.Data.Chapter
            {
                Id = "c1",
                NovelId = TuiSlashTestHarness.NovelId,
                Title = "雨夜寄北",
                Order = 1,
                Content = "雨落下来，他把信折好。",
                Summary = "发现密信",
            }
        );
        _harness.Store.Chapters.Add(
            new Core.Data.Chapter
            {
                Id = "c2",
                NovelId = TuiSlashTestHarness.NovelId,
                Title = "进城",
                Order = 2,
                Content = "城门在他面前打开。",
            }
        );
        _harness.Store.Chapters.Add(
            new Core.Data.Chapter
            {
                Id = "c3",
                NovelId = TuiSlashTestHarness.NovelId,
                Title = "雨夜再会",
                Order = 3,
            }
        );
    }

    [Fact]
    public async Task Tree_LinearSession_ShowsToast()
    {
        // 种子会话为线性对话（无分支点）→ 树面板提示而非弹框
        await DispatchAsync("/tree");
        await WaitForOutputAsync("当前会话为线性对话，无分支点");
    }

    [Fact]
    public async Task Theme_NoArg_OpensThemePickerDialog()
    {
        await DispatchAsync("/theme");
        await WaitForOutputAsync("主题");
    }

    [Fact]
    public async Task Notes_OpensNotesDialog()
    {
        _harness.Store.Notes.Add(
            new Core.Data.AuthorNote
            {
                Id = "note-tui-1",
                NovelId = TuiSlashTestHarness.NovelId,
                Title = "半块玉的递法",
                Body = "让北境使者递出半块玉",
                Status = Core.Data.NoteStatus.Active,
            }
        );

        await DispatchAsync("/notes");
        await WaitForOutputAsync("半块玉的递法");
        // 状态动作是按钮（列表聚焦时 OptionList 会吞掉可打印字符，字母快捷键收不到）
        await WaitForOutputAsync("确认");
        await WaitForOutputAsync("废弃");
    }

    [Fact]
    public async Task NoteAlias_OpensNotesDialog()
    {
        await DispatchAsync("/note");
        await WaitForOutputAsync("笔记");
    }

    [Fact]
    public async Task Tools_NoArg_OpensAllToolsDialog()
    {
        await DispatchAsync("/tools");
        await WaitForOutputAsync("工具列表");
    }

    [Fact]
    public async Task Tools_PluginId_OpensPluginDialog()
    {
        await DispatchAsync("/tools test-plugin");
        await WaitForOutputAsync("test-plugin");
    }

    // ── 云端登录命令：断言登录对话框渲染（OAuth 发现指向死端口，不会打开浏览器） ──

    [Fact]
    public async Task Cloud_OpensLoginDialog()
    {
        await DispatchAsync("/cloud");
        await WaitForOutputAsync("Mythology Cloud 登录");
    }

    [Fact]
    public async Task Login_OpensLoginDialog()
    {
        await DispatchAsync("/login");
        await WaitForOutputAsync("Mythology Cloud 登录");
    }

    [Fact]
    public async Task CloudColonLogin_OpensLoginDialog()
    {
        // 回归：修复前 :cloud login 是死代码（路由只比较第一个 token）→ 落入未知命令
        await DispatchAsync(":cloud login");
        await WaitForOutputAsync("Mythology Cloud 登录");
    }

    [Fact]
    public async Task Login_DeviceFlow_UnreachableCloudToast()
    {
        // --device 先请求设备授权（死端口 → 立即拒绝）→ 明确 Toast，不弹框
        await DispatchAsync("/login --device");
        await WaitForOutputAsync("无法连接到云端");
    }

    [Fact]
    public async Task Login_UnknownArg_Toast()
    {
        await DispatchAsync("/login --bogus");
        await WaitForOutputAsync("未知参数");
    }

    // ── 状态命令：断言应用状态（非弹框路径的持久副作用） ──

    [Fact]
    public async Task ThemeDark_SetsDesertTheme()
    {
        await DispatchAsync("/theme dark");
        await TuiSlashTestHarness.WaitUntilAsync(
            () => _harness.ThemeService.Current.Id == ThemeRegistry.Desert.Id,
            "主题未切换为 desert"
        );
    }

    [Fact]
    public async Task ThemeToggle_CyclesTheme()
    {
        var before = _harness.ThemeService.Current.Id;
        await DispatchAsync("/theme toggle");
        await TuiSlashTestHarness.WaitUntilAsync(
            () => _harness.ThemeService.Current.Id != before,
            "主题未循环切换"
        );
    }

    [Fact]
    public async Task ThemeUnknown_KeepsCurrentTheme()
    {
        var before = _harness.ThemeService.Current.Id;
        await DispatchAsync("/theme bogus");
        // 未知主题处理器是同步的（仅 Toast）：直接断言主题未变
        Assert.Equal(before, _harness.ThemeService.Current.Id);
    }

    [Fact]
    public async Task Clear_ResetsAgentMessages()
    {
        Assert.NotEmpty(_harness.Agent.State.Messages); // 种子会话已恢复消息

        await DispatchAsync("/clear");

        await TuiSlashTestHarness.WaitUntilAsync(
            () => _harness.Agent.State.Messages.Count == 0,
            "Agent 消息未清空"
        );
    }

    [Fact]
    public async Task Mode_CyclesEnhanceModeAndPersistsNovel()
    {
        // StatusBar 默认 semi → 首次 /mode → full
        await DispatchAsync("/mode");

        await TuiSlashTestHarness.WaitUntilAsync(
            () =>
                _harness.Store.Novels.TryGetValue(TuiSlashTestHarness.NovelId, out var saved)
                && saved.AutoEnhance == "full",
            "增强模式未持久化为 full"
        );
    }

    // ── 压缩命令：真实压缩落盘 ──

    [Fact]
    public async Task Compact_Plain_CompactsSeededSession()
    {
        await DispatchAsync("/compact 2");

        await TuiSlashTestHarness.WaitUntilAsync(
            () =>
                _harness.Agent.State.Messages.Count == 3
                && _harness
                    .Agent.State.Messages[0]
                    .Content?.StartsWith("[上下文摘要", StringComparison.Ordinal) == true,
            "Agent 消息未压缩为「摘要 + 保留尾段」"
        );

        var blocks = await _harness.SessionStore.ListCompactBlocksAsync(
            TuiSlashTestHarness.SessionId
        );
        Assert.Single(blocks);
        Assert.Equal("b1", blocks[0].BlockId);
        Assert.Equal(4, blocks[0].MessageCount);
    }

    [Fact]
    public async Task Compact_SmartWithoutScorer_FallsBackToPlain()
    {
        Assert.Null(_harness.Agent.MessageScorer);

        await DispatchAsync("/compact smart 2");

        await TuiSlashTestHarness.WaitUntilAsync(
            () => _harness.Agent.State.Messages.Count == 3,
            "smart 未退回普通压缩"
        );
    }

    [Fact]
    public async Task Decompress_WithoutBlockId_ListsRecoverableBlocks()
    {
        // 预置压缩块（b1，3 条被压缩消息）→ /decompress 列出
        await _harness.SeedCompactBlockAsync();

        await DispatchAsync("/decompress");

        await WaitForOutputAsync("可恢复的压缩块", "b1");
    }

    // ── 恢复/工具/变体命令 ──

    [Fact]
    public async Task Resume_NoArg_ShowsUsageToast()
    {
        await DispatchAsync("/resume");
        await WaitForOutputAsync("用法: /resume <novel-id>");
    }

    [Fact]
    public async Task Resume_UnknownNovel_ShowsToast()
    {
        await DispatchAsync("/resume nosuchnovel");
        await WaitForOutputAsync("未找到小说 nosuchnovel 的活跃会话");
    }

    [Fact]
    public async Task Resume_KnownNovel_RestoresLatestSession()
    {
        await DispatchAsync("/resume nv1");
        await TuiSlashTestHarness.WaitUntilAsync(
            () => _harness.CurrentSessionId == TuiSlashTestHarness.SessionId,
            "会话未恢复"
        );
    }

    [Fact]
    public async Task Tools_PluginTool_EmbedsPromptIntoConversation()
    {
        // 注入指向死端口的 Custom 提供商 API：让 Agent 循环真实执行到 LLM 调用前，
        // 用户消息（🔧 调用 …）在请求构建前入上下文；连接拒绝后异常路径同步消息。
        // 无 API 时 CreateConversation 在用户消息入队前就抛异常，消息会丢失——所以必须给 API。
        _harness.Agent.TornadoApi = new TornadoApi([
            new ProviderAuthentication(LLmProviders.Custom, "test-key"),
        ])
        {
            ApiUrlFormat = "http://127.0.0.1:1/{0}/{1}",
        };
        _harness.Agent.SetModelName("test-model");

        await DispatchAsync("/tools test-plugin:read_file");

        await TuiSlashTestHarness.WaitUntilAsync(
            () =>
                _harness.Agent.State.Messages.Any(m =>
                    m.Content?.Contains(
                        "🔧 调用 test-plugin:read_file 工具！",
                        StringComparison.Ordinal
                    ) == true
                ),
            "工具调用提示未嵌入对话"
        );
    }

    [Fact]
    public async Task Tools_UnknownPlugin_ShowsToast()
    {
        await DispatchAsync("/tools nosuchplugin");
        await WaitForOutputAsync("未知插件");
    }

    [Fact]
    public async Task Variants_NoVariant_ShowsToast()
    {
        await DispatchAsync("/variants");
        await WaitForOutputAsync("没有待选择的变体");
    }

    // ── 云端登出/状态命令 ──

    [Fact]
    public async Task Logout_ClearsCloudSession()
    {
        _harness.SeedConfiguredCloudSession();
        Assert.True(_harness.CloudConnection.IsConfigured);

        await DispatchAsync("/logout");

        await TuiSlashTestHarness.WaitUntilAsync(
            () => !_harness.CloudConnection.IsConfigured,
            "云端凭据未清除"
        );
    }

    [Fact]
    public async Task CloudColonLogout_ClearsCloudSession()
    {
        // 回归：修复前 :cloud logout 是死代码
        _harness.SeedConfiguredCloudSession();
        Assert.True(_harness.CloudConnection.IsConfigured);

        await DispatchAsync(":cloud logout");

        await TuiSlashTestHarness.WaitUntilAsync(
            () => !_harness.CloudConnection.IsConfigured,
            "云端凭据未清除"
        );
    }

    [Fact]
    public async Task CloudColonStatus_ShowsCloudStatusToast()
    {
        _harness.SeedConfiguredCloudSession();

        await DispatchAsync(":cloud status");

        // 第一段：连接态；第二段：零价 usage 查询的积分余量（fire-and-forget，轮询等待渲染）
        await WaitForOutputAsync("Cloud:", "已用 87 / 5000");
    }

    [Fact]
    public async Task UnknownCommand_ShowsFuzzyToast()
    {
        await DispatchAsync("/bogus");
        await WaitForOutputAsync("未知命令");
    }
}
