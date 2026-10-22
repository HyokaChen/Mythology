using Core.Novels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// SetUpPanel 行尾 ⋯ 溢出菜单（弹层生命周期）。
///
/// 从 SetUpPanel.cs 拆出：该类 3000+ 行，行菜单是独立关注点
/// （构造菜单项 → 开弹层 → 关弹层）。打开/关闭/焦点还原的通用逻辑在
/// <see cref="AnchoredPopup{TOwner}"/>（与补全、下拉共用）；此处只保留
/// 面板特定的「菜单项构建 + 行对象闭包」。
/// </summary>
public sealed partial class SetUpPanel
{
    /// <summary>
    /// 打开行尾 ⋯ 菜单（Popup + OptionList；同一时刻至多一个）。
    ///
    /// 行动作一律收进菜单——实体行与大纲节点共用同一机制：行右侧只留 `＋`（下一层新建）
    /// 与 `⋯`，详情 / 编辑 / 引用 / 历史 / 删除都在菜单里。行上不再堆一排抽象小图标
    /// （用户报障：图标小、辨认困难、视觉噪声大）。
    /// </summary>
    private void ShowActionMenu(Visual anchor, IReadOnlyList<(string Label, Action Action)> items)
    {
        if (_rowMenuPopup is not null)
            return;
        if (anchor.App is null)
            return;
        if (items.Count == 0)
            return;

        // 打开/关闭/焦点还原生命周期收敛到 AnchoredPopup（与补全、下拉同一实现）。
        // 菜单项动作经闭包捕获（行对象已在 BuildOutlineRowMenuItems 里固化）。
        _rowMenuPopup = AnchoredPopup<SetUpPanel>.Open(
            owner: this,
            anchor: anchor,
            items: [.. items.Select(i => i.Label)],
            onActivated: (_, index) =>
            {
                if (index >= 0 && index < items.Count)
                    items[index].Action();
            },
            matchAnchorWidth: false, // 菜单宽度按内容，不随 ⋯ 按钮
            placement: PopupPlacement.Below,
            closeOnTab: true,
            anchorRect: null,
            onKeyDown: null,
            onSelectionChanged: null,
            onClosed: static owner => owner._rowMenuPopup = null,
            restoreFocusToAnchor: true
        );
    }

    /// <summary>打开大纲节点行尾 ⋯ 菜单（菜单项由行模型决定）。</summary>
    private void ShowOutlineRowMenu(OutlineRow row, Visual anchor) =>
        ShowActionMenu(anchor, BuildOutlineRowMenuItems(row));

    /// <summary>关闭行尾 ⋯ 菜单（未打开时无操作）。</summary>
    private void CloseRowMenu() => _rowMenuPopup?.Close();
}
