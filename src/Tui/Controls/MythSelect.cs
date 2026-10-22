using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace Tui.Controls;

/// <summary>
/// 自定义 Select 控件 — 使用 OptionList 替代默认 ListBox 作为弹出面板。
/// OptionList 具有 FlexGrowX=1，可自动拉伸填满弹出面板宽度，
/// 使选项行宽度与 Select 控件宽度一致。
/// 弹出面板生命周期（打开/关闭/焦点还原）收敛到 <see cref="AnchoredPopup{TOwner}"/>。
/// </summary>
public sealed class MythSelect : Select<string>
{
    /// <summary>弹出面板句柄（null = 未打开）。</summary>
    private AnchoredPopup<MythSelect>? _myPopup;

    /// <summary>默认构造函数。</summary>
    public MythSelect() { }

    /// <summary>带初始项的构造函数。</summary>
    public MythSelect(IEnumerable<string> items)
        : base(items) { }

    /// <summary>带初始项和选中索引的构造函数。</summary>
    public MythSelect(IEnumerable<string> items, int selectedIndex)
        : base(items, selectedIndex) { }

    /// <summary>鼠标点击时打开弹出面板。</summary>
    protected override void OnPointerPressed(PointerEventArgs e)
    {
        if (e.Button != TerminalMouseButton.Left)
            return;

        OpenMyPopup();
        e.Handled = true;
    }

    /// <summary>键盘操作：Enter/Space 打开面板，Up/Down 快速切换。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is TerminalKey.Enter or TerminalKey.Space)
        {
            OpenMyPopup();
            e.Handled = true;
            return;
        }

        // Up/Down 快速选择 — 不打开面板，直接切换选中项
        if (Items.Count > 0 && e.Key is TerminalKey.Up or TerminalKey.Down)
        {
            var selected = Math.Clamp(SelectedIndex, 0, Items.Count - 1);
            SelectedIndex =
                e.Key == TerminalKey.Up
                    ? Math.Max(0, selected - 1)
                    : Math.Min(Items.Count - 1, selected + 1);
            e.Handled = true;
        }
    }

    /// <summary>打开弹出面板（使用 OptionList 替代默认 ListBox）。</summary>
    private void OpenMyPopup()
    {
        if (_myPopup is not null)
            return;

        var app = App;
        if (app is null)
            return;

        var items = Items.ToArray();
        _myPopup = AnchoredPopup<MythSelect>.Open(
            owner: this,
            anchor: this,
            items: items,
            onActivated: static (owner, index) =>
            {
                // 选中即生效：SelectedIndex 已由 SelectionChanged 同步，这里只负责关面板
                _ = index;
            },
            matchAnchorWidth: true, // 弹出面板宽度与 Select 一致
            placement: PopupPlacement.Below,
            closeOnTab: true,
            anchorRect: null,
            onKeyDown: null,
            onSelectionChanged: static (owner, s, e) =>
            {
                if (s is OptionList<string> ol)
                    owner.SelectedIndex = ol.SelectedIndex;
            },
            onClosed: static owner => owner._myPopup = null,
            restoreFocusToAnchor: true
        );

        // 弹层高亮必须跟随当前选中项：AnchoredPopup 统一把 OptionList.SelectedIndex 置 0
        // （补全列表 / 行菜单从第一项起是对的，但下拉不行）。不同步会有两个后果：
        //   1. 打开时高亮第一项，与字段里已选中的值不一致（用户报障：性别字段选「女」，
        //      下拉打开却高亮「男」）；
        //   2. 点第一项时 OptionList.SelectedIndex 没变化 → SelectionChanged 不触发 →
        //      宿主 SelectedIndex 不变，点了像没反应，字段还是旧值。
        // 这里在 Open 之后补同步（Open 内部已挂好 SelectionChanged，同值赋值不会引发事件）。
        _myPopup?.List.SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, items.Length - 1));
    }

    /// <summary>关闭弹出面板。</summary>
    private void CloseMyPopup()
    {
        _myPopup?.Close();
    }
}
