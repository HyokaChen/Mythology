using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Syntax;
using TextMateSharp.Grammars;

namespace Desktop.Controls;

/// <summary>
/// LiveMarkdown 正文的 block 级虚拟化。
///
/// 普通消息仍走一个 <see cref="MarkdownRenderer"/>（保留流式增量、选择、链接行为）；
/// 只有超过阈值的长消息才按 Markdig 顶层块拆成多个 segment，并且只实例化视口附近的
/// segment。极大消息不会再生成一整棵巨大的 Markdown 视觉树。
/// </summary>
public sealed class VirtualizedMarkdownBody : Panel
{
    /// <summary>单 Renderer 的正文上限；超过后才启用 block 拆分。</summary>
    private const int SingleRendererLimit = 16_000;

    /// <summary>单个 segment 的软上限；避免一个超长段落再次变成巨型 Renderer。</summary>
    private const int MaxSegmentChars = 8_000;

    private const double FallbackViewport = 600;
    private const int MinLiveSegments = 16;
    private const int MaxLiveSegments = 40;
    private const int LiveBufferSegments = 8;
    private const int HeavyDeferCharacters = 12_000;

    private static readonly ThemeName CodeTheme = ThemeName.LightPlus;

    public static readonly StyledProperty<ObservableStringBuilder?> MarkdownBuilderProperty =
        AvaloniaProperty.Register<VirtualizedMarkdownBody, ObservableStringBuilder?>(
            nameof(MarkdownBuilder)
        );

    public ObservableStringBuilder? MarkdownBuilder
    {
        get => GetValue(MarkdownBuilderProperty);
        set => SetValue(MarkdownBuilderProperty, value);
    }

    private readonly List<Block> _blocks = [];
    private readonly HeightIndex<Block> _heightIndex = new();
    private MarkdownRenderer? _singleRenderer;
    private ScrollViewer? _scrollViewer;
    private DispatcherTimer? _rebuildTimer;
    private bool _singleMode = true;
    private bool _rebuilding;
    private bool _realizing;
    private (int Start, int End) _realizedRange = (-1, -1);
    private (int Start, int End) _pendingRange;
    private long _realizeSignature;
    private bool _realizeScheduled;
    private bool _measureScheduled;
    private double _lastScrollOffset = double.NaN;
    private double _visibleTop;
    private double _visibleBottom;
    private double _estimatedWidth = 640;

    static VirtualizedMarkdownBody()
    {
        ClipToBoundsProperty.OverrideDefaultValue<VirtualizedMarkdownBody>(true);
    }

    public VirtualizedMarkdownBody()
    {
        // All realized block renderers share this body as one selection scope.
        MarkdownTextBlock.SetIsSelectionScope(this, true);
        AttachedToVisualTree += (_, _) =>
        {
            _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
            if (_scrollViewer is not null)
                _scrollViewer.ScrollChanged += OnScrollChanged;
            QueueRebuild(immediate: true);
            // First measure may happen before Arrange, so TranslatePoint cannot yet
            // map this body into the scroll viewport. Recompute once after the
            // first arranged frame; otherwise a bottom-pinned long message starts
            // at block 0.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!_singleMode)
                        InvalidateMeasure();
                },
                DispatcherPriority.Loaded
            );
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_scrollViewer is not null)
                _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
            StopRebuildTimer();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MarkdownBuilderProperty)
            return;

        if (change.OldValue is ObservableStringBuilder oldBuilder)
            oldBuilder.Changed -= OnBuilderChanged;
        if (change.NewValue is ObservableStringBuilder newBuilder)
            newBuilder.Changed += OnBuilderChanged;

        _singleMode = true;
        _singleRenderer = null;
        _blocks.Clear();
        Children.Clear();
        InvalidateMeasure();
        QueueRebuild(immediate: true);
    }

    private void OnBuilderChanged(in ObservableStringBuilderChangedEventArgs e)
    {
        if (MarkdownBuilder is null)
            return;
        if (MarkdownBuilder.Length <= SingleRendererLimit && _singleMode)
            return;

        QueueRebuild(immediate: false);
    }

    private void QueueRebuild(bool immediate)
    {
        if (_rebuilding)
            return;

        if (immediate)
        {
            StopRebuildTimer();
            Rebuild();
            return;
        }

        if (_rebuildTimer is not null)
            return;

        _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _rebuildTimer.Tick += (_, _) =>
        {
            StopRebuildTimer();
            Rebuild();
        };
        _rebuildTimer.Start();
    }

    private void StopRebuildTimer()
    {
        if (_rebuildTimer is null)
            return;
        _rebuildTimer.Stop();
        _rebuildTimer = null;
    }

    private void Rebuild()
    {
        if (_rebuilding || MarkdownBuilder is null)
            return;
        _rebuilding = true;
        try
        {
            var text = MarkdownBuilder.ToString();
            var longDocument = ShouldUseBlocks(text);
            if (!longDocument)
            {
                _singleMode = true;
                _blocks.Clear();
                Children.Clear();
                if (_singleRenderer is not null)
                    Children.Add(_singleRenderer);
                InvalidateMeasure();
                return;
            }

            _singleMode = false;
            _singleRenderer = null;
            _blocks.Clear();
            Children.Clear();
            _realizedRange = (-1, -1);
            _realizeSignature = 0;

            foreach (var segment in SplitSegments(text))
            {
                if (segment.Length == 0)
                    continue;
                _blocks.Add(new Block(segment, IsHeavySegment(segment)));
            }
            if (_blocks.Count == 0)
                _blocks.Add(new Block(text, IsHeavySegment(text)));
            _estimatedWidth = Bounds.Width > 0 ? Bounds.Width : 640;
            foreach (var block in _blocks)
                block.Height = EstimateBlockHeight(block.Text, _estimatedWidth);
            _heightIndex.Rebuild(_blocks, BlockHeight);
            InvalidateMeasure();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_singleMode || _measureScheduled || _scrollViewer is null)
            return;

        // 外层跟随时会在每次 layout 重复 ScrollToEnd；offset 没变时不要
        // 再触发一次 measure，否则会形成 ScrollChanged → Measure → ScrollChanged 循环。
        var offset = _scrollViewer.Offset.Y;
        if (Math.Abs(offset - _lastScrollOffset) < 0.5)
            return;
        _lastScrollOffset = offset;
        // ScrollChanged 会在布局/realize 过程中再次触发；同步 InvalidateMeasure
        // 会和子控件 measure 形成反馈环，投递到 Background 合并。
        _measureScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _measureScheduled = false;
                if (!_singleMode)
                    InvalidateMeasure();
            },
            DispatcherPriority.Background
        );
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : Math.Max(320, Bounds.Width);
        if (width <= 0)
            width = 320;

        if (MarkdownBuilder is null)
            return default;

        if (_singleMode && _singleRenderer is null)
        {
            _singleRenderer = CreateRenderer(MarkdownBuilder);
            Children.Add(_singleRenderer);
        }

        if (_singleMode)
        {
            _singleRenderer!.Measure(new Size(width, double.PositiveInfinity));
            return new Size(width, _singleRenderer.DesiredSize.Height);
        }

        if (_blocks.Count == 0)
            Rebuild();

        if (_blocks.Count == 0)
            return default;

        EnsureEstimatedWidth(width);
        var range = GetVisibleRange();
        ScheduleRealize(range);

        var total = 0.0;
        for (var i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            if (block.Control is { } control && block.MeasurePasses < 2)
            {
                control.Measure(new Size(width, double.PositiveInfinity));
                if (control.DesiredSize.Height > 1)
                {
                    block.Height = control.DesiredSize.Height;
                    _heightIndex.Update(block, block.Height);
                    block.MeasurePasses++;
                }
                else
                {
                    block.Height = EstimateBlockHeight(block.Text, width);
                    block.MeasurePasses = 0;
                }
            }
            else if (block.Height <= 0.5)
            {
                block.Height = EstimateBlockHeight(block.Text, width);
            }
            total += block.Height;
        }
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_singleMode)
        {
            _singleRenderer?.Arrange(
                new Rect(0, 0, finalSize.Width, _singleRenderer.DesiredSize.Height)
            );
            return finalSize;
        }

        var y = 0.0;
        foreach (var block in _blocks)
        {
            if (block.Control is { } control)
                control.Arrange(new Rect(0, y, finalSize.Width, block.Height));
            y += block.Height;
        }
        return finalSize;
    }

    private (int Start, int End) GetVisibleRange()
    {
        var top = 0.0;
        var viewport = FallbackViewport;
        if (
            _scrollViewer is not null
            && this.TranslatePoint(new Point(0, 0), _scrollViewer) is { } origin
            && double.IsFinite(origin.Y)
        )
        {
            top = -origin.Y;
            if (double.IsFinite(_scrollViewer.Viewport.Height) && _scrollViewer.Viewport.Height > 0)
                viewport = _scrollViewer.Viewport.Height;
        }
        if (!double.IsFinite(top))
            top = 0;

        _visibleTop = top;
        _visibleBottom = top + viewport;

        var focus = _heightIndex.IndexAtOffset(top + viewport * 0.5);
        var maxLive = GetMaxLiveSegments(viewport);
        var start = Math.Clamp(focus - LiveBufferSegments, 0, Math.Max(0, _blocks.Count - maxLive));
        var end = Math.Min(_blocks.Count, start + maxLive);
        return (start, end);
    }

    private int GetMaxLiveSegments(double viewport)
    {
        if (_blocks.Count == 0)
            return MinLiveSegments;
        var average = Math.Max(24, _heightIndex.Total / _blocks.Count);
        var target = (int)Math.Ceiling(viewport * 2 / average);
        return Math.Clamp(target, MinLiveSegments, MaxLiveSegments);
    }

    private bool ShouldRealizeHeavy(int index)
    {
        if (index < 0 || index >= _blocks.Count || !_blocks[index].IsHeavy)
            return true;

        var top = _heightIndex.OffsetOf(index);
        var bottom = top + BlockHeight(_blocks[index]);
        var margin = Math.Max(120, (_visibleBottom - _visibleTop) * 0.5);
        return bottom >= _visibleTop - margin && top <= _visibleBottom + margin;
    }

    private static bool ShouldUseBlocks(string text) => text.Length > SingleRendererLimit;

    private static bool IsHeavySegment(string text) => text.Length >= HeavyDeferCharacters;

    private double BlockHeight(Block block)
    {
        if (block.Height > 0.5)
            return block.Height;
        return EstimateBlockHeight(block.Text, _estimatedWidth);
    }

    private void EnsureEstimatedWidth(double width)
    {
        if (Math.Abs(width - _estimatedWidth) <= 1)
            return;

        _estimatedWidth = width;
        foreach (var block in _blocks)
        {
            block.Height = EstimateBlockHeight(block.Text, width);
            block.MeasurePasses = 0;
        }
        _heightIndex.Rebuild(_blocks, BlockHeight);
    }

    private static double EstimateBlockHeight(string text, double width)
    {
        try
        {
            return MarkdownHeightEstimator.EstimateBlock(
                text,
                width,
                MarkdownHeightEstimator.Conversation
            );
        }
        catch
        {
            return EstimateHeight(text);
        }
    }

    private void ScheduleRealize((int Start, int End) range)
    {
        var signature = HashCode.Combine(range.Start, range.End, _visibleTop, _visibleBottom);
        if (signature == _realizeSignature)
            return;

        _realizeSignature = signature;
        _pendingRange = range;
        if (_realizeScheduled)
            return;
        _realizeScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _realizeScheduled = false;
                if (_singleMode)
                    return;
                RealizeRange(_pendingRange);
            },
            DispatcherPriority.Background
        );
    }

    private void RealizeRange((int Start, int End) range)
    {
        if (_realizing)
            return;
        _realizing = true;
        try
        {
            var desired = new HashSet<MarkdownRenderer>();
            for (var i = range.Start; i < range.End && i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (!ShouldRealizeHeavy(i))
                    continue;
                block.Control ??= CreateRenderer(new ObservableStringBuilder(block.Text));
                desired.Add(block.Control);
            }

            for (var i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block.Control is null)
                    continue;
                if (desired.Contains(block.Control))
                {
                    if (!Children.Contains(block.Control))
                        Children.Add(block.Control);
                }
                else
                {
                    Children.Remove(block.Control);
                    block.Control = null;
                    // 保留已测高度：这里只是离开可视窗口，不是文本变短。
                    // 用粗估覆盖会让 block 边界处总高度突跳，外层 anchor 把视口
                    // 拉去另一段，表现为滚动到边界时闪到页面尾部。
                    block.MeasurePasses = 0;
                }
            }

            // 防御性清理：Rebuild/旧 range 遗留的 renderer 不能继续吃视觉树。
            foreach (var child in Children.OfType<MarkdownRenderer>().ToArray())
                if (!desired.Contains(child))
                    Children.Remove(child);

            _realizedRange = range;
        }
        finally
        {
            _realizing = false;
        }
        InvalidateMeasure();
    }

    private MarkdownRenderer CreateRenderer(ObservableStringBuilder builder)
    {
        var renderer = new MarkdownRenderer
        {
            MarkdownBuilder = builder,
            CodeBlockColorTheme = CodeTheme,
        };
        renderer.Classes.Add("ConversationMarkdown");
        return renderer;
    }

    private static double EstimateHeight(string text)
    {
        // 只用于未实例化 block 的 spacer；实测值在进入视口后覆盖它。
        var lines = 1;
        foreach (var c in text)
            if (c == '\n')
                lines++;
        return Math.Clamp(lines * 24 + text.Length * 0.08, 24, 12_000);
    }

    private static IEnumerable<string> SplitSegments(string text)
    {
        var segments = new List<string>();
        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(text);
        }
        catch
        {
            foreach (var chunk in SplitRaw(text))
                segments.Add(chunk);
            return segments;
        }

        foreach (var block in document)
        {
            var span = block.Span;
            if (span.Start < 0 || span.End < span.Start || span.End >= text.Length)
                continue;
            var segment = text.Substring(span.Start, span.End - span.Start + 1);
            if (segment.Trim().Length == 0)
                continue;
            if (segment.Length <= MaxSegmentChars)
            {
                segments.Add(segment);
                continue;
            }
            foreach (var chunk in SplitLargeBlock(segment))
                segments.Add(chunk);
        }

        if (segments.Count == 0)
            foreach (var chunk in SplitRaw(text))
                segments.Add(chunk);
        return segments;
    }

    private static IEnumerable<string> SplitLargeBlock(string segment)
    {
        var lines = segment.Replace("\r\n", "\n").Split('\n');
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.TrimStart() ?? "";
        var fenced =
            first.StartsWith("```", StringComparison.Ordinal)
            || first.StartsWith("~~~", StringComparison.Ordinal);
        if (fenced)
        {
            var fence = first[..3];
            var language = first.Length > 3 ? first[3..].Trim() : "";
            var body = lines.Skip(1).Take(Math.Max(0, lines.Length - 2)).ToList();
            return ChunkLines(body, MaxSegmentChars)
                .Select(chunk => fence + language + "\n" + chunk + "\n" + fence);
        }

        return ChunkLines(lines, MaxSegmentChars);
    }

    private static IEnumerable<string> ChunkLines(IReadOnlyList<string> lines, int maxChars)
    {
        var current = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > maxChars)
            {
                yield return current.ToString();
                current.Clear();
            }
            if (line.Length > maxChars)
            {
                // 极端单行：按字符切，优先在空白处断开。
                var start = 0;
                while (start < line.Length)
                {
                    var take = Math.Min(maxChars, line.Length - start);
                    var cut = take;
                    if (start + take < line.Length)
                    {
                        var space = line.LastIndexOf(' ', start + take, take);
                        if (space > start + maxChars / 2)
                            cut = space - start;
                    }
                    current.AppendLine(line.Substring(start, cut));
                    start += cut;
                    if (current.Length >= maxChars)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }
                }
                continue;
            }
            current.AppendLine(line);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    private static IEnumerable<string> SplitRaw(string text)
    {
        var current = new System.Text.StringBuilder();
        var inFence = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (
                trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal)
            )
                inFence = !inFence;
            if (
                !inFence
                && current.Length + line.Length + 1 > MaxSegmentChars
                && current.Length > 0
            )
            {
                yield return current.ToString();
                current.Clear();
            }
            current.AppendLine(line);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    private sealed class Block(string text, bool isHeavy)
    {
        public string Text { get; } = text;
        public bool IsHeavy { get; } = isHeavy;
        public double Height { get; set; }
        public int MeasurePasses { get; set; }
        public MarkdownRenderer? Control { get; set; }
    }
}
