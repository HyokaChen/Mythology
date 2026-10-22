using System.Text;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// 流式气泡管理器 — 管理 AI 流式输出的气泡显示。
/// 支持思考内容的三阶段渲染：流式→已完成→最终折叠。
/// </summary>
public sealed class StreamBubbleManager(DocumentFlow flow, PanelColors colors)
{
    /// <summary>思考阶段枚举。</summary>
    private enum ThinkingPhase
    {
        /// <summary>无思考内容。</summary>
        None,

        /// <summary>思考正在流式传输中。</summary>
        Streaming,

        /// <summary>思考已完成（收到 ThinkingEnd 信号）。</summary>
        Done,
    }

    /// <summary>AI 回复文本缓冲区。</summary>
    private readonly StringBuilder _markdownBuffer = new(2048);

    /// <summary>思考内容缓冲区。</summary>
    private readonly StringBuilder _thinkingBuffer = new(512);

    /// <summary>当前流式气泡在 Flow.Items 中的索引。</summary>
    private int _streamingItemIndex = -1;

    /// <summary>当前思考阶段。</summary>
    private ThinkingPhase _thinkingPhase;

    /// <summary>最终化的思考文本（收到 ThinkingEnd 后缓存，等待后续文本增量时使用）。</summary>
    private string? _finalizedThinking;

    /// <summary>
    /// 清空气泡内容。
    /// </summary>
    public void Clear()
    {
        _markdownBuffer.Clear();
        _thinkingBuffer.Clear();
        _streamingItemIndex = -1;
        _thinkingPhase = ThinkingPhase.None;
        _finalizedThinking = null;
    }

    /// <summary>
    /// 更新思考内容（流式阶段）。
    /// </summary>
    /// <param name="accumulatedText">累积的思考文本。</param>
    public void UpdateThinking(string accumulatedText)
    {
        _thinkingPhase = ThinkingPhase.Streaming;
        _thinkingBuffer.Clear();
        _thinkingBuffer.Append(accumulatedText);
        UpdateBubble();
    }

    /// <summary>
    /// 思考内容完成（收到 ThinkingEnd 信号）。
    /// 缓存思考文本但不立即刷新 UI，避免闪烁。
    /// 等待后续文本增量或最终化时再构建折叠视图。
    /// </summary>
    public void FinalizeThinking()
    {
        _thinkingPhase = ThinkingPhase.Done;
        _finalizedThinking = _thinkingBuffer.ToString();
        // 不刷新 UI — 等待 AppendAssistantDelta 或 FinalizeAssistant
    }

    /// <summary>
    /// 追加 AI 增量文本。
    /// </summary>
    /// <param name="delta">增量文本。</param>
    public void AppendAssistantDelta(string delta)
    {
        // 如果思考仍在流式但文本已到达，隐式完成思考
        if (_thinkingPhase == ThinkingPhase.Streaming)
        {
            _thinkingPhase = ThinkingPhase.Done;
            _finalizedThinking = _thinkingBuffer.ToString();
        }
        _markdownBuffer.Append(delta);
        UpdateBubble();
    }

    /// <summary>
    /// 完成 AI 消息的最终化 — 使用折叠后的思考视图。
    /// </summary>
    public void FinalizeAssistant()
    {
        var idx = _streamingItemIndex;
        _streamingItemIndex = -1;

        var doc = BuildFinalDocument();

        var item = new DocumentFlowItem
        {
            Content = doc,
            Alignment = DocumentFlowAlignment.Stretch,
            BackgroundStyle = Style
                .None.WithBackground(colors.AssistantBubbleBg)
                .WithForeground(colors.AssistantBubbleFg),
            Padding = new Thickness(3, 1, 3, 1),
        };

        if (idx >= 0 && idx < flow.Items.Count)
            flow.Items[idx] = item;
        else
            flow.Items.Add(item);

        _markdownBuffer.Clear();
        _thinkingBuffer.Clear();
        _thinkingPhase = ThinkingPhase.None;
        _finalizedThinking = null;
    }

    /// <summary>
    /// 流式更新气泡（实时渲染）。
    /// </summary>
    private void UpdateBubble()
    {
        var doc = BuildAssistantDocument();

        var item = new DocumentFlowItem
        {
            Content = doc,
            Alignment = DocumentFlowAlignment.Stretch,
            BackgroundStyle = Style
                .None.WithBackground(colors.AssistantBubbleBg)
                .WithForeground(colors.AssistantBubbleFg),
            Padding = new Thickness(3, 1, 3, 1),
        };

        if (_streamingItemIndex >= 0 && _streamingItemIndex < flow.Items.Count)
            flow.Items[_streamingItemIndex] = item;
        else
        {
            flow.Items.Add(item);
            _streamingItemIndex = flow.Items.Count - 1;
        }
    }

    /// <summary>
    /// 构建流式阶段的文档（思考内容实时显示）。
    /// 流式阶段：思考高亮显示
    /// 已完成阶段：思考变为 dim 灰色
    /// </summary>
    private FlowDocument BuildAssistantDocument()
    {
        var doc = new FlowDocument();
        var thinkingText = _thinkingBuffer.ToString();
        if (!string.IsNullOrEmpty(thinkingText))
        {
            // Done → dim 灰色, Streaming → 沙色高亮
            var style = _thinkingPhase == ThinkingPhase.Done ? "dim" : "#A09486";
            var thinkingCtrl = new Markup($"[{style}]✨ {thinkingText}[/]").Wrap(true);
            ToolCallRenderer.AttachCopyHandlers(thinkingCtrl);
            doc.Add(new Padder { Content = thinkingCtrl, Padding = new Thickness(2, 0, 0, 0) });
        }

        var md = _markdownBuffer.ToString();
        if (!string.IsNullOrEmpty(md))
        {
            var contentWidth = Math.Max(20, flow.Scroll.ViewportWidth - 8);
            var mdCtrl = new Markup(MarkdownConverter.Convert(md, contentWidth)).Wrap(true);
            ToolCallRenderer.AttachCopyHandlers(mdCtrl);
            doc.Add(mdCtrl);
        }

        return doc;
    }

    /// <summary>
    /// 构建最终化文档 — 思考内容折叠在 Collapsible 中，文本完整显示。
    /// </summary>
    private FlowDocument BuildFinalDocument()
    {
        var doc = new FlowDocument();

        // 思考内容 → 折叠在 Collapsible 中
        var thinkingText = _finalizedThinking ?? _thinkingBuffer.ToString();
        if (!string.IsNullOrEmpty(thinkingText))
        {
            var header = new HStack(
                new Markup("[dim]💭[/]"),
                new Markup("[dim]查看思考过程[/]")
            ).Spacing(1);
            var content = new Markup($"[dim]✨ {thinkingText}[/]").Wrap(true);
            ToolCallRenderer.AttachCopyHandlers(content);
            doc.Add(new Collapsible(header, content) { IsExpanded = false });
        }

        // 文本内容
        var md = _markdownBuffer.ToString();
        if (!string.IsNullOrEmpty(md))
        {
            var contentWidth = Math.Max(20, flow.Scroll.ViewportWidth - 8);
            var mdCtrl = new Markup(MarkdownConverter.Convert(md, contentWidth)).Wrap(true);
            ToolCallRenderer.AttachCopyHandlers(mdCtrl);
            doc.Add(mdCtrl);
        }

        return doc;
    }
}
