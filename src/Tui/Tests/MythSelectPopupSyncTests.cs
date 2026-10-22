using System.Reflection;
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
/// MythSelect 下拉弹层回归（用户报障）：角色性别字段已选「女」，打开下拉却高亮第一项「男」，
/// 点「男」像没反应、字段仍是「女」。
///
/// 根因：<see cref="AnchoredPopup{TOwner}.Open"/> 统一把 OptionList.SelectedIndex 置 0
/// （补全列表 / 行菜单从第一项起是对的，但下拉必须跟随当前值）。不同步时：
/// <list type="number">
/// <item>打开时高亮第一项，与字段值不一致；</item>
/// <item>点第一项时 OptionList.SelectedIndex 没变化 → SelectionChanged 不触发 →
///       宿主 SelectedIndex 不变 → 点了没反应。</item>
/// </list>
/// 修法：MythSelect.OpenMyPopup 在 Open 之后把 List.SelectedIndex 同步到自身 SelectedIndex。
///
/// 注意：Terminal.Instance / Dispatcher.Current 是全局单例，本类测试串行执行。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class MythSelectPopupSyncTests : TerminalUiFixture
{
    private const int W = 60;
    private const int H = 20;

    private MythSelect _select = null!;

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
        // 已选中索引 1（「女」）——复现用户场景。
        // 布局复刻 EntityEditDialog 表单行：Grid(Auto,Star)+ColumnGap(2)，Select 拉宽——
        // 只有这样锚宽才远大于内容宽，才能暴露「弹层撑开但列表不撑」的问题。
        _select = new MythSelect(new[] { "男", "女", "其他" }) { SelectedIndex = 1 };
        _select.HorizontalAlignment = Align.Stretch;
        _select.MaxWidth(int.MaxValue);
        var formRow = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .ColumnGap(2)
            .RowGap(0);
        formRow.HorizontalAlignment = Align.Stretch;
        formRow.Cell(new Markup("[dim]性别[/]"), 0, 0);
        formRow.Cell(_select, 0, 1);

        return new WindowLayer(
            new Padder { Padding = new Thickness(2, 2, 0, 0), Content = formRow }
        );
    }

    protected override async ValueTask OnStartedAsync()
    {
        await WaitUntilAsync(
            async () => await OnUiAsync(() => _select.Bounds.Width) > 0,
            "首帧布局"
        );
        await Task.Delay(120);
    }

    /// <summary>打开下拉：弹层高亮必须跟随当前选中项（而不是硬编码的第一项）。</summary>
    [Fact]
    public async Task OpenPopup_HighlightsCurrentSelection_NotFirstItem()
    {
        await OpenPopupAsync();

        var listSelected = await OnUiAsync(() => FindPopupList()?.SelectedIndex ?? -999);
        Assert.Equal(1, listSelected); // 「女」，不是 0「男」
    }

    /// <summary>点第一项必须真的改值（修复前点第一项是 no-op，字段仍是旧值）。</summary>
    [Fact]
    public async Task ClickFirstItem_ChangesSelection()
    {
        await OpenPopupAsync();
        var list = await OnUiAsync(() => FindPopupList());
        Assert.NotNull(list);

        // 点第一行（索引 0 = 「男」）
        var (x, y) = await OnUiAsync(() => (list!.Bounds.X + 2, list.Bounds.Y));
        await ClickAsync(x, y);
        await Task.Delay(200);

        var selected = await OnUiAsync(() => _select.SelectedIndex);
        Assert.Equal(0, selected); // 「男」
    }

    /// <summary>
    /// 弹层选项行的高亮/悬浮底色必须铺满整个面板宽（用户报障：只涂到文字那么宽，
    /// 面板右侧露出一截面板底色）。
    ///
    /// 根因：OptionList 的 MeasureCore 报的是 growX:1（能拉伸），但控件自己从没设
    /// HorizontalAlignment——同族的 ListBox 在构造里就设了 Align.Stretch，OptionList 没设。
    /// 而 OptionList.RenderOverride 的整行填满用的是<em>自身</em> Bounds.Width，
    /// 于是列表只占内容宽 → 高亮/悬浮底色也只涂到文字那么宽。
    /// </summary>
    [Fact]
    public async Task OpenPopup_ListFillsPopupWidth()
    {
        await OpenPopupAsync();

        var (listWidth, surfaceWidth, selectWidth) = await OnUiAsync(() =>
        {
            var list = FindPopupList()!;
            return (list.Bounds.Width, ((Visual)list.Parent!).Bounds.Width, _select.Bounds.Width);
        });

        // 1) 选项列表铺满弹层内容面（否则高亮/悬浮底色只涂到文字那么宽）
        Assert.Equal(surfaceWidth, listWidth);
        // 2) 弹层内容面与下拉框等长（MatchAnchorWidth 的契约）
        Assert.Equal(selectWidth, surfaceWidth);
    }

    // ── 基础设施 ──

    private async Task OpenPopupAsync()
    {
        var (x, y) = await OnUiAsync(() => (_select.Bounds.X + 2, _select.Bounds.Y));
        await ClickAsync(x, y);
        await WaitUntilAsync(
            async () => await OnUiAsync(() => FindPopupList() is not null),
            "下拉弹层打开"
        );
        await Task.Delay(80);
    }

    /// <summary>在视觉树里找弹层中的 OptionList（Popup 是 WindowLayer 里的窗口）。</summary>
    private OptionList<string>? FindPopupList() =>
        App.Root.EnumerateVisualsDepthFirst().OfType<OptionList<string>>().FirstOrDefault();

    private async Task ClickAsync(int x, int y)
    {
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
        await Task.Delay(80);
    }
}
