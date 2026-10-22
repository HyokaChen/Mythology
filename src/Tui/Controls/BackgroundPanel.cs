using System.Text;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Rendering;

namespace Tui.Controls;

/// <summary>
/// 背景面板 — 使用背景样式填充 Bounds，然后在上面渲染子内容。
/// 推入显式裁剪以防止背景填充溢出父级裁剪区域（例如对话框边框区域）。
/// </summary>
public sealed class BackgroundPanel : ContentVisual
{
    /// <summary>背景填充样式。</summary>
    public Style? FillStyle { get; set; }

    /// <summary>
    /// 初始化 BackgroundPanel 实例。
    /// </summary>
    public BackgroundPanel()
    {
        HorizontalAlignment = Align.Stretch;
        VerticalAlignment = Align.Stretch;
    }

    /// <summary>
    /// 强制本面板立即重绘。
    /// XenoAtom 渲染是惰性的（_renderRequested 标志），普通属性 set（FillStyle）不触发渲染；
    /// 而 AttachChild（子内容 detach/reattach）必然触发渲染并使本面板进入脏区。
    /// 主题切换等需要 FillStyle 新值立即生效的场景必须显式调用。
    /// </summary>
    public void Refresh()
    {
        var content = Content;
        Content = null;
        Content = content;
    }

    /// <summary>
    /// 重写渲染逻辑，先填充背景再渲染子内容。
    /// </summary>
    /// <param name="buffer">单元格缓冲区。</param>
    protected override void RenderOverride(CellBuffer buffer)
    {
        // ★ 关键：订阅主题变化 —— 渲染时读取 Theme 使本面板进入渲染依赖索引，
        // 主题切换（SetStyle/State 变化）时本面板被标记脏区重绘，FillStyle 新值随之生效。
        // 否则 FillStyle 是普通属性，set 不触发渲染；局部脏矩形渲染下面板区域永远不重绘，
        // 导致切换主题后面板背景保持旧色（ConversationPanel/SetUpPanel/HeaderBar 均受影响）。
        _ = GetTheme();

        if (FillStyle is { } fillStyle)
        {
            buffer.PushClip(Bounds);
            var rect = Bounds;
            for (var y = rect.Y; y < rect.Bottom; y++)
            for (var x = rect.X; x < rect.Right; x++)
                buffer.SetCell(x, y, new Rune(' '), fillStyle);
            buffer.PopClip();
        }
        base.RenderOverride(buffer);
    }
}
