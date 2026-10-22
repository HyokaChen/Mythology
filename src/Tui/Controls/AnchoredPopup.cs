using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 锚定弹出面板（Popup + OptionList）共享实现——收敛三处逐字重复的打开/关闭/焦点恢复逻辑：
/// <list type="bullet">
/// <item>MythPromptEditor：斜杠/实体补全（MatchAnchorWidth、Above、接管 Tab）</item>
/// <item>MythSelect：下拉选项（MatchAnchorWidth、Below）</item>
/// <item>SetUpPanel：行尾 ⋯ 菜单（宽度按内容、Below、Esc 焦点还原到锚）</item>
/// </list>
/// 三处的差异只有：放置方向、是否匹配锚宽、是否接收 Tab、关闭后焦点去处。
/// 其余（构造 Popup / 填 ItemTemplate / SelectedIndex=0 / ItemActivated 先关后派发 /
/// Escape 关闭 / Show 后聚焦列表 / Closed 时回调 Owner 清理）逐字相同。
/// </summary>
/// <typeparam name="TOwner">宿主类型（编辑器 / 下拉 / 面板）——Closed 回调用它清理引用。</typeparam>
internal sealed class AnchoredPopup<TOwner>
{
    private readonly TOwner _owner;
    private readonly Popup _popup;
    private readonly Action<TOwner> _onClosed;

    private AnchoredPopup(TOwner owner, Action<TOwner> onClosed, Popup popup)
    {
        _owner = owner;
        _onClosed = onClosed;
        _popup = popup;
    }

    /// <summary>弹出面板内的选项列表（打开期间由调用方填Items / 接事件）。</summary>
    public OptionList<string> List { get; private init; } = null!;

    /// <summary>
    /// 打开弹出面板并返回句柄；候选项为空时返回 null（调用方可直接 return）。
    /// </summary>
    /// <param name="owner">宿主对象（Closed 回调回传）。</param>
    /// <param name="anchor">锚控件（定位 + 焦点还原目标）。</param>
    /// <param name="items">选项文本（顺序即索引）。</param>
    /// <param name="onActivated">选中回调（先关面板再派发，索引为选项下标）。</param>
    /// <param name="matchAnchorWidth">面板宽度是否匹配锚宽。</param>
    /// <param name="placement">弹出方向。</param>
    /// <param name="closeOnTab">Tab 是否关闭面板（补全 false：自行接管）。</param>
    /// <param name="anchorRect">显式锚矩形（编辑器用；null = 用 Anchor  bounds）。</param>
    /// <param name="onKeyDown">附加按键拦截（可空；Escape 关闭 + 焦点还原由本类负责）。</param>
    /// <param name="onSelectionChanged">选中同步回调（MythSelect 用；可空）。</param>
    /// <param name="onClosed">面板关闭时清理宿主状态。</param>
    /// <param name="restoreFocusToAnchor">Esc 关闭后焦点是否回锚控件。</param>
    public static AnchoredPopup<TOwner>? Open(
        TOwner owner,
        Visual anchor,
        IReadOnlyList<string> items,
        Action<TOwner, int> onActivated,
        bool matchAnchorWidth,
        PopupPlacement placement,
        bool closeOnTab,
        Rectangle? anchorRect,
        Action<TOwner, object?, KeyEventArgs>? onKeyDown,
        Action<TOwner, object?, SelectionChangedEventArgs>? onSelectionChanged,
        Action<TOwner> onClosed,
        bool restoreFocusToAnchor
    )
    {
        if (items.Count == 0)
            return null;

        var list = new OptionList<string>().ActivateOnClick(true);
        foreach (var item in items)
            list.Items.Add(item);
        list.ItemTemplate = new DataTemplate<string>(
            Display: static (value, in _) => new TextBlock(value.GetValue),
            Editor: null
        );
        list.SelectedIndex = 0;

        // 选项行的高亮/悬浮底色必须铺满整个面板宽。OptionList.RenderOverride 的整行填满
        // 用的是自身 Bounds.Width，而它默认不水平拉伸（同族的 ListBox 在构造里就设了
        // Align.Stretch，OptionList 没设）→ 只占内容宽 → 底色只涂到文字那么宽，
        // 弹层右侧露出一截面板底色（用户报障）。MeasureCore 报的是 growX:1，撑得开。
        list.HorizontalAlignment = Align.Stretch;

        // 弹层表面必须显式上底色：框架默认让 Popup / OptionList 透出 app 基础底（夜空灰），
        // 在暖色主题里就是「一块黑」（用户报障：下拉 / ⋯ 菜单展开的黑块）。
        var colors = PanelThemes.Current;
        list.SetStyle(PanelThemes.BuildOptionList(colors));

        // 内容面再包一层自带底色的面板：框架弹层会在内容外留一圈「默认主题色」边缘，
        // 只靠 PopupStyle.SurfaceStyle 盖不住（用户看到的就是那圈黑边）。
        var surface = new BackgroundPanel
        {
            FillStyle = Style
                .None.WithForeground(colors.DialogContentFg)
                .WithBackground(colors.DialogContentBg),
            Content = list,
            HorizontalAlignment = Align.Stretch,
        };

        var popup = new Popup
        {
            Anchor = anchor,
            AnchorRect = anchorRect,
            Content = surface,
            MatchAnchorWidth = matchAnchorWidth,
            Placement = placement,
            CloseOnTab = closeOnTab,
        };
        popup.SetStyle(PanelThemes.BuildPopup(colors));

        var handle = new AnchoredPopup<TOwner>(owner, onClosed, popup) { List = list };

        list.ItemActivated(
            (_, e) =>
            {
                var index = e.Index;
                popup.Close();
                onActivated(owner, index);
            }
        );

        if (onSelectionChanged is not null)
        {
            list.SelectionChanged((s, e) => onSelectionChanged(owner, s, e));
        }

        list.KeyDown(
            (sender, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    popup.Close();
                    if (restoreFocusToAnchor)
                        anchor.App?.Focus(anchor);
                    e.Handled = true;
                    return;
                }
                onKeyDown?.Invoke(owner, sender, e);
            }
        );

        popup.Closed((_, _) => handle.Close());
        popup.Show();
        anchor.App?.Focus(list);
        return handle;
    }

    /// <summary>
    /// 关闭并通知宿主清理（幂等；Popup.Close 在已关闭时静默返回，
    /// 因此 Closed 事件回调恰好触发一次 _onClosed）。
    /// </summary>
    public void Close()
    {
        _popup.Close();
        _onClosed(_owner);
    }

    /// <summary>运行时更新锚矩形（编辑器宽度随窗口变化时复用同一面板）。</summary>
    public void UpdateAnchorRect(Rectangle rect) => _popup.AnchorRect = rect;
}
