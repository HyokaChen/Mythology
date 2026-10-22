using System.Text;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace Tui.Controls;

/// <summary>分隔方向。</summary>
public enum SplitDirection
{
    /// <summary>水平分隔：左/右两个面板，分隔线为竖线。</summary>
    Horizontal,

    /// <summary>垂直分隔：上/下两个面板，分隔线为横线。</summary>
    Vertical,
}

/// <summary>
/// 不可拖拽的 1px 细线分隔器 — 仅视觉分隔，无交互。
/// 基于 XenoAtom VSplitter 简化：去掉拖拽/焦点/键盘交互，只保留布局 + 渲染。
///
/// 关键设计：线条用 fg-only 渲染（不设 background），cell 其余部分透出两侧面板背景，
/// 视觉上比 fg=bg 实心填充细得多——解决 FixedRatioPanel DividerVisual 的「粗黑条」问题。
/// </summary>
public sealed class ThinSplitter : Visual
{
    private Visual? _first;
    private Visual? _second;

    /// <summary>面板比例（0.0 ~ 1.0），First 占 Ratio，Second 占 1-Ratio。</summary>
    public double Ratio { get; set; } = 0.5;

    /// <summary>分隔方向。</summary>
    public SplitDirection Direction { get; set; } = SplitDirection.Horizontal;

    /// <summary>分隔线前景色。</summary>
    public Color? LineColor { get; set; }

    /// <summary>
    /// 分隔行/列背景色。必须设置——bar 行不分配给任何子元素，
    /// 需要用面板背景色填充以消除 gap（否则 bar 行显示终端默认黑色背景）。
    /// </summary>
    public Color? BackgroundColor { get; set; }

    /// <summary>
    /// 分隔行/列的第二区域背景色（仅 Horizontal 方向使用）。
    /// 当左列（First）内部有上下两个不同背景色的子区域时，
    /// bar 列需要按 y 坐标分区填充背景色：
    /// - First 第一个子区域 → <see cref="BackgroundColor"/>
    /// - First 第二个子区域 → <see cref="SecondBackgroundColor"/>
    /// 分界点自动从 First 子元素的第一个子元素 Bounds 底部计算。
    /// 若不设置，整个 bar 列使用 <see cref="BackgroundColor"/>。
    /// </summary>
    public Color? SecondBackgroundColor { get; set; }

    /// <summary>
    /// 分隔区厚度（行数/列数）。默认 1（仅线条）。
    /// 设为 2 时中间 1 行画线、上下各 1 行画背景色——产生呼吸感的间距；
    /// 设为 0 时不预留分隔区，两个面板直接相邻（用于仅靠颜色区分边界的场景）。
    /// 注意：终端单元格宽高比 ≈ 1:2（列宽约 8px、行高约 17px），
    /// 实心填充时 1 行的视觉高度（≈17px）远大于 1 列（≈8px）。
    /// 两种「细呼吸带」用法（横竖视觉厚度一致 ≈8px）：
    /// - 水平方向（Vertical 分割）：BarSize=1 且 <see cref="HorizontalGlyph"/> 设为半块字形
    ///   （▄ U+2584 / ▀ U+2580），并令 BackgroundColor=上面板色、LineColor=呼吸带色——
    ///   半块字形只染半个 cell（≈8.5px），另一半透出上面板色延伸，不再有 17px 实心色带；
    /// - 垂直方向（Horizontal 分割）：BarSize=1 实心 1 列（≈8px），LineColor 与背景同色即可。
    /// </summary>
    public int BarSize { get; set; } = 1;

    /// <summary>横线字形（垂直分隔时使用）。默认 ─ (U+2500)。</summary>
    public Rune HorizontalGlyph { get; set; } = new('─');

    /// <summary>竖线字形（水平分隔时使用）。默认 │ (U+2502)。</summary>
    public Rune VerticalGlyph { get; set; } = new('│');

    /// <summary>第一个面板。</summary>
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

    /// <summary>第二个面板。</summary>
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
    /// 初始化 ThinSplitter 并设置两个子面板。
    /// </summary>
    public ThinSplitter(Visual first, Visual second)
    {
        First = first;
        Second = second;
        HorizontalAlignment = Align.Stretch;
        VerticalAlignment = Align.Stretch;
        Focusable = false;
    }

    /// <summary>子元素数量（First + Second，不含 bar 区域）。</summary>
    protected override int ChildrenCount =>
        (_first is not null ? 1 : 0) + (_second is not null ? 1 : 0);

    /// <summary>按索引获取子元素：0=First，1=Second。</summary>
    protected override Visual GetChild(int index)
    {
        if (_first is not null)
        {
            if (index == 0)
                return _first;
            index--;
        }

        if (_second is not null)
        {
            if (index == 0)
                return _second;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>
    /// 测量子元素尺寸 — 按比例分配可用空间给 First 和 Second，
    /// bar 区域从可用空间中扣除（不额外增加）。
    /// </summary>
    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
    {
        var first = _first;
        var second = _second;

        if (first is null && second is null)
            return SizeHints.Fixed(default);

        var maxW = constraints.MaxWidth;
        var maxH = constraints.MaxHeight;
        var bar = first is not null && second is not null ? Math.Max(0, BarSize) : 0;

        if (Direction == SplitDirection.Vertical)
        {
            var ratio = ClampRatio();
            var firstHeight =
                maxH < int.MaxValue ? Math.Max(0, (int)Math.Round(maxH * ratio)) : maxH;
            var secondHeight = maxH < int.MaxValue ? Math.Max(0, maxH - firstHeight - bar) : maxH;

            first?.Measure(new LayoutConstraints(0, maxW, 0, firstHeight));
            second?.Measure(new LayoutConstraints(0, maxW, 0, secondHeight));

            // Natural = maxH: ThinSplitter is a flex container that fills available space.
            // Bar rows are subtracted from the available height in ArrangeCore, not added on top.
            return SizeHints.Flex(
                new Size(0, 0),
                new Size(maxW, maxH),
                new Size(maxW, maxH),
                1,
                1,
                1,
                1
            );
        }

        // Horizontal
        {
            var ratio = ClampRatio();
            var firstWidth =
                maxW < int.MaxValue ? Math.Max(0, (int)Math.Round(maxW * ratio)) : maxW;
            var secondWidth = maxW < int.MaxValue ? Math.Max(0, maxW - firstWidth - bar) : maxW;

            first?.Measure(new LayoutConstraints(0, firstWidth, 0, maxH));
            second?.Measure(new LayoutConstraints(0, secondWidth, 0, maxH));

            // Natural = maxW: ThinSplitter is a flex container that fills available space.
            return SizeHints.Flex(
                new Size(0, 0),
                new Size(maxW, maxH),
                new Size(maxW, maxH),
                1,
                1,
                1,
                1
            );
        }
    }

    /// <summary>
    /// 排列子元素 — 按比例分配 finalRect 给 First 和 Second，
    /// bar 行/列不分配给任何子元素，由 RenderOverride 负责渲染。
    /// </summary>
    protected override void ArrangeCore(in Rectangle finalRect)
    {
        var first = _first;
        var second = _second;

        switch (first)
        {
            case null when second is null:
                return;
            case null:
                second.Arrange(finalRect);
                return;
        }

        if (second is null)
        {
            first.Arrange(finalRect);
            return;
        }

        var bar = Math.Max(0, BarSize);
        var ratio = ClampRatio();

        if (Direction == SplitDirection.Vertical)
        {
            var firstHeight = Math.Max(0, (int)Math.Round(finalRect.Height * ratio));
            var secondHeight = Math.Max(0, finalRect.Height - firstHeight - bar);

            first.Arrange(new Rectangle(finalRect.X, finalRect.Y, finalRect.Width, firstHeight));
            // bar row: finalRect.Y + firstHeight (1px, not assigned to any child)
            second.Arrange(
                new Rectangle(
                    finalRect.X,
                    finalRect.Y + firstHeight + bar,
                    finalRect.Width,
                    secondHeight
                )
            );
        }
        else
        {
            var firstWidth = Math.Max(0, (int)Math.Round(finalRect.Width * ratio));
            var secondWidth = Math.Max(0, finalRect.Width - firstWidth - bar);

            first.Arrange(new Rectangle(finalRect.X, finalRect.Y, firstWidth, finalRect.Height));
            // bar column: finalRect.X + firstWidth (1px, not assigned to any child)
            second.Arrange(
                new Rectangle(
                    finalRect.X + firstWidth + bar,
                    finalRect.Y,
                    secondWidth,
                    finalRect.Height
                )
            );
        }
    }

    /// <summary>
    /// 渲染分隔线 — 在 bar 区域填充背景色，中间行/列绘制线条字形。
    /// 水平方向（Vertical 分割）支持 SecondBackgroundColor 双区域背景。
    /// </summary>
    protected override void RenderOverride(CellBuffer buffer)
    {
        _ = GetTheme(); // 订阅主题变化：主题切换时本控件进入脏区重绘（BackgroundColor/LineColor 普通属性更新后需重绘生效）
        var first = _first;
        var second = _second;
        if (first is null || second is null)
            return;

        var rect = Bounds;
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var bar = Math.Max(0, BarSize);
        if (bar <= 0)
            return; // BarSize=0：无分隔区，不渲染
        // 线条字形位于 bar 区域的中间行/列
        var lineOffset = bar / 2;

        if (Direction == SplitDirection.Vertical)
        {
            // 水平 bar 行（First 和 Second 之间）
            var barStartY = first.Bounds.Y + first.Bounds.Height;
            if (barStartY + bar > rect.Y + rect.Height)
                return;

            for (var i = 0; i < bar; i++)
            {
                var y = barStartY + i;
                var isLineRow = i == lineOffset;

                // 填充背景色
                if (BackgroundColor is { } bgColor)
                {
                    var bgStyle = Style.None.WithBackground(bgColor).WithForeground(bgColor);
                    for (var x = rect.X; x < rect.X + rect.Width; x++)
                        buffer.SetCell(x, y, new Rune(' '), bgStyle);
                }

                // 在中间行绘制线条字形
                if (isLineRow && LineColor is { } fgColor)
                {
                    var fgStyle = BackgroundColor is { } bg
                        ? Style.None.WithForeground(fgColor).WithBackground(bg)
                        : Style.None.WithForeground(fgColor);
                    var glyph = HorizontalGlyph;
                    for (var x = rect.X; x < rect.X + rect.Width; x++)
                        buffer.SetCell(x, y, glyph, fgStyle);
                }
            }
        }
        else
        {
            // 垂直 bar 列（First 和 Second 之间）
            var barStartX = first.Bounds.X + first.Bounds.Width;
            if (barStartX + bar > rect.X + rect.Width)
                return;

            // 水平分隔时，确定 First 内部子区域的 y 分界线
            // （如 ConversationPanel vs PromptEditor）。
            // 若 First 也是 ThinSplitter（Vertical），取其 First 子元素的底部作为分界。
            var regionBoundaryY = rect.Y + rect.Height; // 默认：不分隔
            if (
                SecondBackgroundColor is not null
                && first is ThinSplitter { Direction: SplitDirection.Vertical } innerSplitter
            )
            {
                var innerFirst = innerSplitter.First;
                if (innerFirst is not null)
                {
                    var boundary = innerFirst.Bounds.Y + innerFirst.Bounds.Height;
                    if (boundary > rect.Y && boundary < rect.Y + rect.Height)
                        regionBoundaryY = boundary;
                }
            }

            for (var i = 0; i < bar; i++)
            {
                var x = barStartX + i;
                var isLineCol = i == lineOffset;

                // 填充背景色 — 若设置了 SecondBackgroundColor 则按 y 分区
                if (BackgroundColor is { } bgColor)
                {
                    // 上区域：BackgroundColor
                    var upperBgStyle = Style.None.WithBackground(bgColor).WithForeground(bgColor);
                    for (var y = rect.Y; y < regionBoundaryY; y++)
                        buffer.SetCell(x, y, new Rune(' '), upperBgStyle);

                    // 下区域：SecondBackgroundColor（未设置则回退 BackgroundColor）
                    var lowerBgColor = SecondBackgroundColor ?? bgColor;
                    var lowerBgStyle = Style
                        .None.WithBackground(lowerBgColor)
                        .WithForeground(lowerBgColor);
                    for (var y = regionBoundaryY; y < rect.Y + rect.Height; y++)
                        buffer.SetCell(x, y, new Rune(' '), lowerBgStyle);
                }

                // 在中间列绘制线条字形
                if (isLineCol && LineColor is { } fgColor)
                {
                    var glyph = VerticalGlyph;

                    // 上区域线条
                    var upperFgStyle = BackgroundColor is { } bg
                        ? Style.None.WithForeground(fgColor).WithBackground(bg)
                        : Style.None.WithForeground(fgColor);
                    for (var y = rect.Y; y < regionBoundaryY; y++)
                        buffer.SetCell(x, y, glyph, upperFgStyle);

                    // 下区域线条
                    var lowerBg = SecondBackgroundColor ?? BackgroundColor;
                    var lowerFgStyle = lowerBg is not null
                        ? Style.None.WithForeground(fgColor).WithBackground(lowerBg.Value)
                        : Style.None.WithForeground(fgColor);
                    for (var y = regionBoundaryY; y < rect.Y + rect.Height; y++)
                        buffer.SetCell(x, y, glyph, lowerFgStyle);
                }
            }
        }
    }

    /// <summary>将 Ratio 限制在 [0.0, 1.0] 范围内，NaN/Infinity 回退为 0.5。</summary>
    private double ClampRatio() =>
        double.IsNaN(Ratio) || double.IsInfinity(Ratio) ? 0.5 : Math.Clamp(Ratio, 0.0, 1.0);
}
