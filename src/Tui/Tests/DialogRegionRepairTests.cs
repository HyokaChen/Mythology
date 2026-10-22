using System.Reflection;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 对话框开/关边界的补帧范围锁（用户实机截图：输入框右上角残留 1 格对话框底色）。
///
/// 根因：对话框边界变化（开窗首次布局 / 移动 / 内容撑高 / 终端 resize）后，框架脏区
/// 只按新边界重画；旧边界外紧邻的格子不再参与布局，可能保留上一帧的对话框底色。
/// 差分渲染器认为这些格子自己已经发过，永远不会补。
///
/// 修法：边界变化时只补「旧边界 ∪ 新边界」并各向外扩 1 格（覆盖紧邻残格），
/// 走 RequestRegionRender，不清屏、不 Reset 差分状态；不整屏重发。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class DialogRegionRepairTests : TerminalUiFixture
{
    private TuiSlashTestHarness _harness = null!;

    protected override TerminalSize Size => new(140, 44);

    protected override Func<CancellationToken, Task> CreateAppRunner()
    {
        _harness = new TuiSlashTestHarness(
            novelId: TuiSlashTestHarness.NovelId,
            sessionId: TuiSlashTestHarness.SessionId
        );
        return _harness.App.RunAsync;
    }

    protected override async ValueTask OnReadyToRunAsync() =>
        await _harness.SeedSessionStoreAsync();

    protected override async ValueTask OnStartedAsync() =>
        await WaitUntilAsync(
            () => DispatcherAttached() && _harness.CurrentSessionId is not null,
            "启动并恢复会话",
            20000
        );

    protected override async ValueTask OnStoppingAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task OpeningDialog_RepairsDialogEdgeOnly_AndKeepsFocus()
    {
        await OnUiAsync(() =>
        {
            ResetRepairRegion(_harness.App); // 排除启动期其它对话框留下的区域
            new SettingsDialog(_harness.SettingsService, _harness.Registry).Show();
            return 0;
        });

        await WaitUntilAsync(
            () =>
                OnUiAsync(() =>
                {
                    var rootVisual = (Visual)
                        typeof(TuiApp)
                            .GetField(
                                "_rootVisual",
                                BindingFlags.NonPublic | BindingFlags.Instance
                            )!
                            .GetValue(_harness.App)!;
                    var app = rootVisual.App!;
                    var dialog = app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().First();
                    var region = GetRepairRegion(_harness.App);
                    return region.Width > 0 && region.Width <= dialog.Bounds.Width + 2;
                }),
            "对话框打开并请求边界补帧（halo 范围）",
            15000
        );

        var (dialogBounds, repairRegion) = await OnUiAsync(() =>
        {
            var rootVisual = (Visual)
                typeof(TuiApp)
                    .GetField("_rootVisual", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(_harness.App)!;
            var app = rootVisual.App!;
            var dialog = app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().First();
            return (dialog.Bounds, GetRepairRegion(_harness.App));
        });

        // 补帧范围 = 对话框边界四边各扩 1 格：覆盖新边界紧邻格与旧边界，区域远小于整屏。
        Assert.True(
            repairRegion.Contains(Math.Max(0, dialogBounds.X - 1), dialogBounds.Y),
            $"补帧区域必须覆盖对话框左缘外 1 格；dialog={dialogBounds}, repair={repairRegion}"
        );
        Assert.True(
            repairRegion.Width <= dialogBounds.Width + 2
                && repairRegion.Height <= dialogBounds.Height + 2,
            $"补帧区域只能是对话框边界 + 1 格外扩；dialog={dialogBounds}, repair={repairRegion}"
        );
        Assert.True(
            repairRegion.Width * repairRegion.Height < Size.Columns * Size.Rows / 2,
            $"补帧区域不应接近整屏：repair={repairRegion}，屏幕={Size.Columns}x{Size.Rows}"
        );

        var focusInsideDialog = await OnUiAsync(() =>
        {
            var rootVisual = (Visual)
                typeof(TuiApp)
                    .GetField("_rootVisual", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(_harness.App)!;
            var app = rootVisual.App!;
            var dialog = app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().FirstOrDefault();
            if (dialog is null)
                return false;
            for (var v = app.FocusedElement; v is not null; v = v.Parent)
            {
                if (ReferenceEquals(v, dialog))
                    return true;
            }
            return false;
        });
        Assert.True(focusInsideDialog, "对话框打开后焦点必须留在对话框内");
    }

    private static void ResetRepairRegion(TuiApp app) =>
        typeof(TuiApp)
            .GetProperty("LastDialogRepairRegion", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(app, default(Rectangle));

    private static Rectangle GetRepairRegion(TuiApp app) =>
        (Rectangle)
            typeof(TuiApp)
                .GetProperty(
                    "LastDialogRepairRegion",
                    BindingFlags.NonPublic | BindingFlags.Instance
                )!
                .GetValue(app)!;
}
