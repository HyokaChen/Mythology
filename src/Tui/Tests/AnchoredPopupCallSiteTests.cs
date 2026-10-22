using System.Reflection;
using Core.Data;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Threading;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// <see cref="AnchoredPopup{TOwner}"/> 另外两个调用方的同源回归。
///
/// MythSelect 的 bug 模式：宿主状态由「弹层 SelectionChanged 差值」驱动，于是弹层初始
/// SelectedIndex=0 而宿主当前值≠0 时，点第一项不产生差值 → 点了没反应。
///
/// 这两个调用方的同步机制不同，需要实证确认它们不受同源问题影响：
/// <list type="bullet">
/// <item>补全（<see cref="MythPromptEditor"/>）：点击经 PointerPressed 读
///       <c>List.SelectedIndex</c> 当前值应用，不依赖差值；且候选列表每次重建，
///       从第一项起是正确语义。</item>
/// <item>行菜单（<c>SetUpPanel.ShowActionMenu</c>）：动作由 ItemActivated 携带的索引
///       直接派发，与任何宿主状态无关。</item>
/// </list>
///
/// 注意：Terminal.Instance / Dispatcher.Current 是全局单例，本文件测试串行执行。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class CompletionPopupCallSiteTests : IAsyncLifetime
{
    private InMemoryTerminalBackend _backend = null!;
    private TerminalApp _app = null!;
    private CancellationTokenSource _cts = null!;
    private Task _runTask = null!;
    private MythPromptEditor _editor = null!;

    public async ValueTask InitializeAsync()
    {
        // 启动屏障：等上一个终端完全退出再顶掉全局实例（否则后续投递会落到别人的 UI 线程）
        await TerminalUiFixture.WaitForPreviousTerminalAsync();

        _backend = new InMemoryTerminalBackend(new TerminalSize(80, 24), null);
        Terminal.Initialize(
            _backend,
            new TerminalOptions { ImplicitStartInput = true },
            force: true
        );

        _editor = new MythPromptEditor();
        var root = new WindowLayer(
            new Padder { Padding = new Thickness(2, 8, 0, 0), Content = _editor }
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
            }
        );

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => _app.RunAsync(_cts.Token));

        await WaitUntilAsync(
            async () => await OnUiAsync(() => _editor.Bounds.Width) > 0,
            "首帧布局"
        );
        await Task.Delay(120);
    }

    public async ValueTask DisposeAsync()
    {
        await TerminalUiFixture.StopAppAsync(_cts, _runTask);
        Terminal.Close();
    }

    /// <summary>点第一项候选必须生效且只生效一次（修复前 MythSelect 点第一项是 no-op）。</summary>
    [Fact]
    public async Task ClickFirstCandidate_AppliesExactlyOnce()
    {
        await OnUiAsync(() =>
        {
            _editor.ShowCompletionPopup(["alpha", "beta"], 0, 0, " ");
            return true;
        });
        await WaitUntilAsync(
            async () => await OnUiAsync(() => FindPopupList() is not null),
            "补全弹层打开"
        );
        await Task.Delay(80);

        var list = await OnUiAsync(() => FindPopupList());
        Assert.NotNull(list);
        Assert.Equal(0, await OnUiAsync(() => list!.SelectedIndex)); // 候选列表从第一项起是正确的

        var (x, y) = await OnUiAsync(() => (list!.Bounds.X + 2, list.Bounds.Y));
        await ClickAsync(x, y);
        await Task.Delay(250);

        // 应用一次：不是 no-op（点第一项没反应），也不是两次（press 应用 + release 激活又应用）
        Assert.Equal("alpha ", await OnUiAsync(() => _editor.Text ?? ""));
    }

    /// <summary>
    /// 点非第一项候选同样只应用一次。补全弹层点击有两条应用路径——PointerPressed
    /// （读当前 SelectedIndex 应用）与 release 时 ActivateIndex → onActivated（再应用一次）。
    /// 若两条都生效会重复插入文本（"beta beta "）。
    /// </summary>
    [Fact]
    public async Task ClickSecondCandidate_AppliesExactlyOnce()
    {
        await OnUiAsync(() =>
        {
            _editor.ShowCompletionPopup(["alpha", "beta"], 0, 0, " ");
            return true;
        });
        await WaitUntilAsync(
            async () => await OnUiAsync(() => FindPopupList() is not null),
            "补全弹层打开"
        );
        await Task.Delay(80);

        var list = await OnUiAsync(() => FindPopupList());
        Assert.NotNull(list);

        // 点第二行（索引 1 = "beta"）
        var (x, y) = await OnUiAsync(() => (list!.Bounds.X + 2, list.Bounds.Y + 1));
        await ClickAsync(x, y);
        await Task.Delay(250);

        Assert.Equal("beta ", await OnUiAsync(() => _editor.Text ?? ""));
    }

    // ── 基础设施 ──

    private OptionList<string>? FindPopupList() =>
        _app.Root.EnumerateVisualsDepthFirst().OfType<OptionList<string>>().FirstOrDefault();

    private async Task ClickAsync(int x, int y)
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
        await Task.Delay(80);
    }

    private static Task<T> OnUiAsync<T>(Func<T> action) => TerminalUiFixture.OnUiAsync(action);

    private static Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string what,
        int timeoutMs = 10000
    ) => TerminalUiFixture.WaitUntilAsync(condition, what, timeoutMs);
}

/// <summary>行尾 ⋯ 菜单调用方的同源回归：点第一项菜单必须真的执行动作。</summary>
[Collection(TerminalUiCollection.Name)]
public sealed class RowMenuPopupCallSiteTests : IAsyncLifetime
{
    private const int W = 120;
    private const int H = 44;

    private InMemoryTerminalBackend _backend = null!;
    private TerminalApp _app = null!;
    private SetUpPanel _panel = null!;
    private CancellationTokenSource _cts = null!;
    private Task _runTask = null!;

    public async ValueTask InitializeAsync()
    {
        var store = new SetUpPanelLayoutTests.FakeStore
        {
            Novel = new Novel
            {
                Id = "n1",
                NovelId = "n1",
                Title = "菜单实机",
                Outline =
                    "全书走向：主角寻找失踪的师父，最终揭示师父主动封印灾厄，由主角继承守护使命。",
            },
        };
        store.Characters.Add(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林长歌",
                Role = CharacterRole.Protagonist,
            }
        );

        _panel = new SetUpPanel(store, PanelThemes.Dark) { CurrentNovelId = "n1" };
        await _panel.RefreshAsync("n1");

        // 启动屏障：等上一个终端完全退出再顶掉全局实例（否则后续投递会落到别人的 UI 线程）
        await TerminalUiFixture.WaitForPreviousTerminalAsync();

        _backend = new InMemoryTerminalBackend(new TerminalSize(W, H), null);
        Terminal.Initialize(
            _backend,
            new TerminalOptions { ImplicitStartInput = true },
            force: true
        );

        var appRoot = new WindowLayer(_panel.Visual);
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
        await Task.Delay(120);
    }

    public async ValueTask DisposeAsync()
    {
        await TerminalUiFixture.StopAppAsync(_cts, _runTask);
        Terminal.Close();
    }

    /// <summary>点第一项菜单必须执行第一项动作（而不是 no-op / 第二项）。</summary>
    [Fact]
    public async Task ClickFirstMenuItem_RunsFirstAction()
    {
        var ran = new List<int>();
        (string Label, Action Action)[] items =
        [
            ("第一项", () => ran.Add(0)),
            ("第二项", () => ran.Add(1)),
            ("第三项", () => ran.Add(2)),
        ];

        await OnUiAsync(() =>
            typeof(SetUpPanel)
                .GetMethod("ShowActionMenu", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_panel, [_panel.Visual, items])
        );

        await WaitUntilAsync(
            async () => await OnUiAsync(() => FindPopupList() is not null),
            "行菜单弹出"
        );
        await Task.Delay(80);

        var list = await OnUiAsync(() => FindPopupList());
        Assert.NotNull(list);
        Assert.Equal(0, await OnUiAsync(() => list!.SelectedIndex)); // 菜单从第一项起是正确的

        var (x, y) = await OnUiAsync(() => (list!.Bounds.X + 2, list.Bounds.Y));
        await ClickAsync(x, y);
        await Task.Delay(200);

        Assert.Equal([0], ran); // 第一项动作执行且仅执行一次
    }

    // ── 基础设施 ──

    private OptionList<string>? FindPopupList() =>
        _app.Root.EnumerateVisualsDepthFirst().OfType<OptionList<string>>().FirstOrDefault();

    private async Task ClickAsync(int x, int y)
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
        await Task.Delay(80);
    }

    private static Task<T> OnUiAsync<T>(Func<T> action) => TerminalUiFixture.OnUiAsync(action);

    private static Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string what,
        int timeoutMs = 10000
    ) => TerminalUiFixture.WaitUntilAsync(condition, what, timeoutMs);
}
