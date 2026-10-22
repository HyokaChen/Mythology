using System.Reflection;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Threading;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 锁死「区域重发只碰侧栏、绝不碰对话区」——这是修 Apple Terminal 输入法擦色块
/// 而不引入闪烁的关键：组词文字画在对话区，重发它会覆盖终端正在绘制的文字、
/// 双方抢画面就是闪烁（全屏重发方案已被用户实测否决）。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class SidebarRegionRepairTests : TerminalUiFixture
{
    private TuiSlashTestHarness _harness = null!;
    private readonly CancellationToken _timeout = new(false);

    protected override TerminalSize Size => new(140, 30);

    protected override Func<CancellationToken, Task> CreateAppRunner()
    {
        _harness = new TuiSlashTestHarness(novelId: null, sessionId: null);
        return _harness.App.RunAsync;
    }

    protected override async ValueTask OnStartedAsync() => await Task.Delay(1000);

    /// <summary>
    /// 侧栏区域重发后，输出里不得出现对话区（左侧）的定位序列。
    /// </summary>
    [Fact]
    public async Task SidebarRegionRepair_NeverWritesConversationArea()
    {
        var app = _harness.App;
        var rightColumn = (Visual)
            typeof(Tui.TuiApp)
                .GetField("_rightColumn", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(app)!;
        var rootVisual = (Visual)
            typeof(Tui.TuiApp)
                .GetField("_rootVisual", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(app)!;
        var terminalApp = rootVisual.App!;

        // 先等应用静止：启动后云端连接失败会连续弹提示条（动画逐帧重绘对话区），
        // 它的帧与区域重发混进同一段 delta，就会让下面「只重发侧栏」的判据假失败。
        await WaitUntilOutputQuietAsync();

        var before = Backend.GetOutText().Length;

        await Dispatcher.Current.InvokeAsync(() =>
        {
            terminalApp.RequestRegionRender(rightColumn.Bounds);
            return 0;
        });
        // 窗口以「再次静止」为终点（不再是无条件 600ms 定时）：delta 里只有这次重发。
        await WaitUntilOutputQuietAsync();
        var delta = Backend.GetOutText().Substring(before);
        Assert.False(string.IsNullOrEmpty(delta), "区域重发应有输出");

        var sidebarLeft = await Dispatcher.Current.InvokeAsync(() => rightColumn.Bounds.X);
        // 允许写入的下界 = 右侧栏左界 − 2（左右分栏的分隔条本身就是 2 列）− 1（宽字形脏矩形会
        // 扩展到字形边界，最多再向左侧连带 1 格）。区域重发覆盖分隔条属正常，仍禁止写进对话区。
        var minColumn = sidebarLeft - 2;
        // 诊断用数据必须在 UI 线程上取（Visual.Bounds 经 BindingManager，有线程亲和）
        var sidebarBounds = await Dispatcher.Current.InvokeAsync(() => rightColumn.Bounds);
        var themeName =
            ReferenceEquals(PanelThemes.Current, PanelThemes.Dark) ? "dark"
            : ReferenceEquals(PanelThemes.Current, PanelThemes.Light) ? "light"
            : "other";
        var terminalSize = Terminal.Instance.Size;
        // 所有光标定位都不得落在侧栏左界以左（对话区）
        var positions = System.Text.RegularExpressions.Regex.Matches(
            delta,
            "\u001b\\[(\\d+);(\\d+)H"
        );
        Assert.NotEmpty(positions);
        foreach (System.Text.RegularExpressions.Match m in positions)
        {
            var col = int.Parse(m.Groups[2].Value);
            Assert.True(
                col >= minColumn,
                $"区域重发写到了对话区：列 {col} < 允许下界 {minColumn}（侧栏左界 {sidebarLeft}）"
                    + $"；theme={themeName}；rightColumn={sidebarBounds}；terminal={terminalSize}"
                    + $"。delta={delta}"
            );
        }
    }

    /// <summary>
    /// 侧栏区域重发不得清屏（区域重发与全屏重发的行为分界）。
    /// </summary>
    [Fact]
    public async Task SidebarRegionRepair_DoesNotClearScreen()
    {
        var app = _harness.App;
        var rightColumn = (Visual)
            typeof(Tui.TuiApp)
                .GetField("_rightColumn", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(app)!;
        var rootVisual = (Visual)
            typeof(Tui.TuiApp)
                .GetField("_rootVisual", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(app)!;
        var terminalApp = rootVisual.App!;

        var before = Backend.GetOutText().Length;
        await Dispatcher.Current.InvokeAsync(() =>
        {
            terminalApp.RequestRegionRender(rightColumn.Bounds);
            return 0;
        });
        await Task.Delay(600);

        var delta = Backend.GetOutText().Substring(before);
        Assert.DoesNotContain("\u001b[2J", delta);
    }

    /// <summary>
    /// 等到应用停止产出字节（提示条动画、动画 tick 都跑完）。
    /// 用途：测量区域重发时窗口必须只含重发本身——否则其它 UI（提示条、光标闪烁）
    /// 在对话区的重绘会混进 delta，与「绝不重发对话区」的判据同形，形成假失败。
    /// </summary>
    private async Task WaitUntilOutputQuietAsync(int quietMs = 500, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var lastLength = -1;
        var lastChange = Environment.TickCount64;
        while (Environment.TickCount64 < deadline)
        {
            // 长度在 UI 线程上读：输出由 UI 线程追加，跨线程读同一个 writer 会读到撕裂状态
            var length = await OnUiAsync(() => Backend.GetOutText().Length);
            if (length != lastLength)
            {
                lastLength = length;
                lastChange = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - lastChange >= quietMs)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"等待超时：应用未停止输出（需静默 {quietMs}ms，说明动画/提示条一直在刷）");
    }

    protected override async ValueTask OnStoppingAsync() => await _harness.DisposeAsync();
}
