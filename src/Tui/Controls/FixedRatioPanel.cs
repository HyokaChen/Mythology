using System.Text;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace Tui.Controls;

/// <summary>固定比例面板的分割方向。</summary>
public enum RatioPanelDirection
{
    /// <summary>水平分割：左/右两个子面板，比例作用于宽度。</summary>
    Horizontal,

    /// <summary>垂直分割：上/下两个子面板，比例作用于高度。</summary>
    Vertical,
}

/// <summary>
/// 固定比例面板 — 将空间按固定比例分配给两个子面板。
/// 注意：XenoAtom Grid 的 Star 权重会被忽略（所有 Star 行均分空间），
/// 因此不能依赖 Grid 实现 80/20、65/35 这类比例——本控件自行计算比例。
/// </summary>
public sealed class FixedRatioPanel : Visual
{
    private Visual? _first; // 第一个子面板
    private Visual? _second; // 第二个子面板
    private readonly DividerVisual _dividerVisual; // 分隔线视觉元素（始终作为子元素，渲染顺序在面板之后）

    /// <summary>面板比例（0.0 ~ 1.0）。</summary>
    public double Ratio { get; set; } = 0.7;

    /// <summary>分割方向（默认水平）。</summary>
    public RatioPanelDirection Direction { get; set; } = RatioPanelDirection.Horizontal;

    /// <summary>
    /// 如果设置，在两个面板之间渲染一条 1px 分隔线。
    /// </summary>
    public Color? DividerColor
    {
        get => _dividerVisual.Color;
        set => _dividerVisual.Color = value;
    }

    /// <summary>第一个子面板。</summary>
    public Visual? First
    {
        get => _first;
        set
        {
            if (!ReferenceEquals(_first, value))
            {
                if (_first is not null && _first.Parent == this)
                    DetachChild(_first);
                _first = value;
                if (value is not null && value.Parent != this)
                    AttachChild(value);
            }
        }
    }

    /// <summary>第二个子面板。</summary>
    public Visual? Second
    {
        get => _second;
        set
        {
            if (!ReferenceEquals(_second, value))
            {
                if (_second is not null && _second.Parent == this)
                    DetachChild(_second);
                _second = value;
                if (value is not null && value.Parent != this)
                    AttachChild(value);
            }
        }
    }

    /// <summary>
    /// 初始化 FixedRatioPanel 实例并设置子面板。
    /// </summary>
    /// <param name="first">第一个子面板。</param>
    /// <param name="second">第二个子面板。</param>
    public FixedRatioPanel(Visual first, Visual second)
    {
        _dividerVisual = new DividerVisual(this);
        AttachChild(_dividerVisual);
        First = first;
        Second = second;
        HorizontalAlignment = Align.Stretch;
        VerticalAlignment = Align.Stretch;
    }

    /// <summary>子元素数量（First + Second + DividerVisual）。</summary>
    protected override int ChildrenCount =>
        (_first is not null ? 1 : 0) + (_second is not null ? 1 : 0) + 1; // +1 for divider

    /// <summary>按索引获取子元素：渲染顺序 first → second → divider（divider 最后绘制覆盖溢出）。</summary>
    protected override Visual GetChild(int index)
    {
        // Render order: first → second → divider (last, so divider draws on top)
        if (_first is not null)
        {
            switch (index)
            {
                case 0:
                    return _first;
                case 1 when _second is not null:
                    return _second;
                case 1 when _second is null:
                case 2 when _second is not null:
                    return _dividerVisual;
            }
        }
        else if (_second is not null)
        {
            switch (index)
            {
                case 0:
                    return _second;
                case 1:
                    return _dividerVisual;
            }
        }
        else if (index == 0)
        {
            return _dividerVisual;
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>测量子元素尺寸 — 按比例分配可用空间，gap 从可用空间中扣除。</summary>
    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
    {
        var first = _first;
        var second = _second;

        if (first is null && second is null)
            return SizeHints.Fixed(default);

        var maxW = constraints.MaxWidth;
        var maxH = constraints.MaxHeight;

        if (first is not null && second is not null)
        {
            var ratio =
                double.IsNaN(Ratio) || double.IsInfinity(Ratio) ? 0.5 : Math.Clamp(Ratio, 0.0, 1.0);

            if (Direction == RatioPanelDirection.Vertical)
            {
                var firstHeight =
                    maxH < int.MaxValue ? Math.Max(0, (int)Math.Round(maxH * ratio)) : maxH;
                var secondHeight = maxH < int.MaxValue ? Math.Max(0, maxH - firstHeight) : maxH;

                var vFirstHints = first.Measure(new LayoutConstraints(0, maxW, 0, firstHeight));
                var vSecondHints = second.Measure(new LayoutConstraints(0, maxW, 0, secondHeight));

                var vW = Math.Max(vFirstHints.Natural.Width, vSecondHints.Natural.Width);
                var vH = vFirstHints.Natural.Height + vSecondHints.Natural.Height;
                return SizeHints.Flex(
                    new Size(0, 0),
                    new Size(vW, vH),
                    new Size(maxW, maxH),
                    1,
                    1,
                    1,
                    1
                );
            }

            var firstWidth =
                maxW < int.MaxValue ? Math.Max(0, (int)Math.Round(maxW * ratio)) : maxW;
            var secondWidth = maxW < int.MaxValue ? Math.Max(0, maxW - firstWidth) : maxW;

            var firstHints = first.Measure(new LayoutConstraints(0, firstWidth, 0, maxH));
            var secondHints = second.Measure(new LayoutConstraints(0, secondWidth, 0, maxH));

            var w = firstHints.Natural.Width + secondHints.Natural.Width;
            var h = Math.Max(firstHints.Natural.Height, secondHints.Natural.Height);
            return SizeHints.Flex(new Size(0, 0), new Size(w, h), new Size(maxW, maxH), 1, 1, 1, 1);
        }

        if (first is not null)
        {
            var hints = first.Measure(constraints);
            return SizeHints.Flex(hints.Min, hints.Natural, hints.Max, 1, 1, 1, 1);
        }

        var sHints = second!.Measure(constraints);
        return SizeHints.Flex(sHints.Min, sHints.Natural, sHints.Max, 1, 1, 1, 1);
    }

    /// <summary>排列子元素 — 按比例分配 finalRect，gap 区域分配给 DividerVisual。</summary>
    protected override void ArrangeCore(in Rectangle finalRect)
    {
        var first = _first;
        var second = _second;

        if (first is null && second is null)
            return;

        var ratio =
            double.IsNaN(Ratio) || double.IsInfinity(Ratio) ? 0.5 : Math.Clamp(Ratio, 0.0, 1.0);

        if (first is not null && second is not null)
        {
            var gap = DividerColor is not null ? 1 : 0;

            if (Direction == RatioPanelDirection.Vertical)
            {
                var firstHeight = Math.Max(0, (int)Math.Round(finalRect.Height * ratio));
                var secondHeight = Math.Max(0, finalRect.Height - firstHeight - gap);
                first.Arrange(
                    new Rectangle(finalRect.X, finalRect.Y, finalRect.Width, firstHeight)
                );
                second.Arrange(
                    new Rectangle(
                        finalRect.X,
                        finalRect.Y + firstHeight + gap,
                        finalRect.Width,
                        secondHeight
                    )
                );
                // 分隔线视觉元素占据 gap 行
                _dividerVisual.Arrange(
                    new Rectangle(finalRect.X, finalRect.Y + firstHeight, finalRect.Width, gap)
                );
            }
            else
            {
                var firstWidth = Math.Max(0, (int)Math.Round(finalRect.Width * ratio));
                var secondWidth = Math.Max(0, finalRect.Width - firstWidth - gap);
                first.Arrange(
                    new Rectangle(finalRect.X, finalRect.Y, firstWidth, finalRect.Height)
                );
                second.Arrange(
                    new Rectangle(
                        finalRect.X + firstWidth + gap,
                        finalRect.Y,
                        secondWidth,
                        finalRect.Height
                    )
                );
                // 分隔线视觉元素占据 gap 列
                _dividerVisual.Arrange(
                    new Rectangle(finalRect.X + firstWidth, finalRect.Y, gap, finalRect.Height)
                );
            }
        }
        else if (first is not null)
        {
            first.Arrange(finalRect);
        }
        else
        {
            second!.Arrange(finalRect);
        }
    }

    /// <summary>
    /// 内部分隔线视觉元素 — 作为 FixedRatioPanel 的最后一个子元素渲染，
    /// 确保分隔线在两个面板之后绘制，覆盖任何溢出到 gap 区域的内容。
    /// </summary>
    private sealed class DividerVisual : Visual
    {
        private readonly FixedRatioPanel _owner; // 所属面板

        /// <summary>分隔线颜色。</summary>
        public Color? Color;

        /// <summary>初始化 DividerVisual。</summary>
        public DividerVisual(FixedRatioPanel owner)
        {
            _owner = owner;
            HorizontalAlignment = Align.Stretch;
            VerticalAlignment = Align.Stretch;
        }

        /// <summary>渲染分隔线 — 垂直方向画横线 ─，水平方向画竖线 │。</summary>
        protected override void RenderOverride(CellBuffer buffer)
        {
            _ = GetTheme(); // 订阅主题变化：主题切换时本控件进入脏区重绘（Color 普通属性更新后需重绘生效）
            if (Color is not { } color)
                return;

            var rect = Bounds;
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            var style = Style.None.WithForeground(color).WithBackground(color);
            if (_owner.Direction == RatioPanelDirection.Vertical)
            {
                // 水平分隔线 ─
                for (var x = rect.X; x < rect.X + rect.Width; x++)
                    buffer.SetCell(x, rect.Y, new Rune('─'), style);
            }
            else
            {
                // 垂直分隔线 │
                for (var y = rect.Y; y < rect.Y + rect.Height; y++)
                    buffer.SetCell(rect.X, y, new Rune('│'), style);
            }
        }
    }
}
