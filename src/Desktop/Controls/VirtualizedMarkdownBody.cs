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
    private const double BufferViewports = 1.0;
    private const int InitialSegments = 8;

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
    private MarkdownRenderer? _singleRenderer;
    private ScrollViewer? _scrollViewer;
    private DispatcherTimer? _rebuildTimer;
    private string? _selectionScope;
    private bool _singleMode = true;
    private bool _rebuilding;
    private bool _realizing;
    private (int Start, int End) _realizedRange = (-1, -1);
    private (int Start, int End) _pendingRange;
    private bool _realizeScheduled;
    private bool _measureScheduled;
    private double _lastScrollOffset = double.NaN;

    static VirtualizedMarkdownBody()
    {
        ClipToBoundsProperty.OverrideDefaultValue<VirtualizedMarkdownBody>(true);
    }

    public VirtualizedMarkdownBody()
    {
        AttachedToVisualTree += (_, _) =>
        {
            _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
            if (_scrollViewer is not null)
                _scrollViewer.ScrollChanged += OnScrollChanged;
            QueueRebuild(immediate: true);
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
            var longDocument = text.Length > SingleRendererLimit;
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

            foreach (var segment in SplitSegments(text))
            {
                if (segment.Length == 0)
                    continue;
                _blocks.Add(new Block(segment));
            }
            if (_blocks.Count == 0)
                _blocks.Add(new Block(text));
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

        var range = GetVisibleRange(width);
        ScheduleRealize(range);

        var total = 0.0;
        for (var i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            if (block.Control is { } control && block.MeasurePasses < 2)
            {
                control.Measure(new Size(width, double.PositiveInfinity));
                block.Height = Math.Max(1, control.DesiredSize.Height);
                block.MeasurePasses++;
            }
            else if (block.Height <= 0.5)
            {
                block.Height = EstimateHeight(block.Text);
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

    private (int Start, int End) GetVisibleRange(double width)
    {
        var top = 0.0;
        var viewport = FallbackViewport;
        if (
            _scrollViewer is not null
            && this.TranslatePoint(new Point(0, 0), _scrollViewer) is { } origin
        )
        {
            top = -origin.Y;
            viewport = Math.Max(1, _scrollViewer.Viewport.Height);
        }

        var rangeTop = Math.Max(0, top - viewport * BufferViewports);
        var rangeBottom = top + viewport * (1 + BufferViewports);

        var start = 0;
        var y = 0.0;
        while (start < _blocks.Count)
        {
            var next = y + BlockHeight(_blocks[start]);
            if (next > rangeTop)
                break;
            y = next;
            start++;
        }

        var end = start;
        var bottom = y;
        while (end < _blocks.Count && bottom < rangeBottom)
        {
            bottom += BlockHeight(_blocks[end]);
            end++;
        }

        if (end == start && start < _blocks.Count)
            end = start + 1;
        if (start >= _blocks.Count)
        {
            start = Math.Max(0, _blocks.Count - 1);
            end = _blocks.Count;
        }

        // 估算高度不等于实测高度时，range 可能膨胀；硬上限避免一次 realize 整篇。
        const int MaxRealizedSegments = 32;
        if (end - start > MaxRealizedSegments)
        {
            if (start > _blocks.Count / 2)
                start = Math.Max(0, end - MaxRealizedSegments);
            else
                end = start + MaxRealizedSegments;
        }
        return (start, end);
    }

    private static double BlockHeight(Block block)
    {
        if (block.Height > 0.5)
            return block.Height;
        return EstimateHeight(block.Text);
    }

    private void ScheduleRealize((int Start, int End) range)
    {
        if (range == _realizedRange)
            return;
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
                    block.Height = 0;
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
        _selectionScope ??= $"conversation-body-{Guid.NewGuid():N}";
        var renderer = new MarkdownRenderer
        {
            MarkdownBuilder = builder,
            CodeBlockColorTheme = CodeTheme,
        };
        renderer.Classes.Add("ConversationMarkdown");
        MarkdownRenderer.SetSelectionScopeName(renderer, _selectionScope);
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

    private sealed class Block(string text)
    {
        public string Text { get; } = text;
        public double Height { get; set; }
        public int MeasurePasses { get; set; }
        public MarkdownRenderer? Control { get; set; }
    }
}
