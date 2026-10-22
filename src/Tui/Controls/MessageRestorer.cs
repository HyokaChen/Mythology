using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 消息恢复器 — 将 ChatMessage 数组恢复到对话面板。
/// </summary>
/// <param name="_flow">对话面板的文档流。</param>
/// <param name="_registry">工具注册表（用于查找工具分类和实体类型）。</param>
/// <param name="_colors">面板颜色配置。</param>
/// <param name="_onNavigateToEntity">导航到实体的回调（可选）。</param>
public sealed class MessageRestorer
{
    private readonly DocumentFlow _flow;
    private readonly ToolRegistry _registry;
    private PanelColors _colors; // 主题切换时通过 SetColors 更新
    private readonly Action<string, string>? _onNavigateToEntity;

    /// <param name="flow">对话面板的文档流。</param>
    /// <param name="registry">工具注册表（用于查找工具分类和实体类型）。</param>
    /// <param name="colors">面板颜色配置。</param>
    /// <param name="onNavigateToEntity">导航到实体的回调（可选）。</param>
    public MessageRestorer(
        DocumentFlow flow,
        ToolRegistry registry,
        PanelColors colors,
        Action<string, string>? onNavigateToEntity = null
    )
    {
        _flow = flow;
        _registry = registry;
        _colors = colors;
        _onNavigateToEntity = onNavigateToEntity;
    }

    /// <summary>更新主题颜色（重建气泡时用当前主题色）。</summary>
    public void SetColors(PanelColors colors) => _colors = colors;

    /// <summary>
    /// 恢复消息到对话面板。
    /// </summary>
    /// <param name="messages">消息数组。</param>
    public void Restore(ChatMessage[] messages)
    {
        for (var i = 0; i < messages.Length; i++)
        {
            var m = messages[i];
            var text = m.Content ?? "";
            var thinking = m.ReasoningContent ?? "";

            // 多部分消息：从 Parts 中提取文本和思考内容
            if (m.Parts is { } parts)
            {
                text = string.Join(
                    "",
                    parts.Where(p => p.Type == ChatMessageTypes.Text).Select(p => p.Text)
                );
                var reasoningParts = parts.Where(p =>
                    p is { Type: ChatMessageTypes.Reasoning, Reasoning: not null }
                );
                thinking = string.Join(
                    "\n\n",
                    reasoningParts.Select(p => p.Reasoning!.Content ?? "")
                );
            }

            switch (m.Role)
            {
                case ChatMessageRoles.User:
                // ── 用户消息：渲染为蓝色气泡 ──
                {
                    // 跳过 ask_user 回复消息（前缀 "**用户回复:**"），
                    // 因为 tool result 的 Q&A 卡片已经展示了完整信息
                    var isAskUserAnswer = text.StartsWith(
                        "**用户回复:**",
                        StringComparison.Ordinal
                    );

                    if (!isAskUserAnswer)
                    {
                        var userBlock = new TextBlock($"👤 {text}")
                            .TextAlignment(TextAlignment.Left)
                            .Wrap(true)
                            .HorizontalAlignment(Align.Stretch)
                            .Style(
                                new TextBlockStyle
                                {
                                    Foreground = _colors.UserBubbleFg,
                                    Background = _colors.UserBubbleBg,
                                    FillBackground = true,
                                }
                            );
                        _flow.Items.Add(
                            new DocumentFlowItem
                            {
                                Content = new FlowDocument().Add(userBlock),
                                Alignment = DocumentFlowAlignment.Stretch,
                                BackgroundStyle = Style
                                    .None.WithBackground(_colors.UserBubbleBg)
                                    .WithForeground(_colors.UserBubbleFg),
                                Padding = new Thickness(3, 1, 3, 1),
                            }
                        );
                    }
                    break;
                }
                case ChatMessageRoles.Assistant:
                // ── AI 消息：思考内容折叠 + 正文 Markdown ──
                {
                    var doc = new FlowDocument();

                    if (!string.IsNullOrEmpty(thinking))
                    {
                        var header = new HStack(
                            new Markup("[dim]💭[/]"),
                            new Markup("[dim]查看思考过程[/]")
                        ).Spacing(1);
                        var content = new Markup($"[dim]✨ {thinking}[/]").Wrap(true);
                        ToolCallRenderer.AttachCopyHandlers(content);
                        doc.Add(new Collapsible(header, content) { IsExpanded = false });
                        // 减去气泡 Padding(3,1,3,1) 占用的宽度
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        var contentWidth = Math.Max(20, _flow.Scroll.ViewportWidth - 8); // subtract bubble Padding(3,1,3,1)
                        var mdCtrl = new Markup(MarkdownConverter.Convert(text, contentWidth)).Wrap(
                            true
                        );
                        ToolCallRenderer.AttachCopyHandlers(mdCtrl);
                        doc.Add(mdCtrl);
                    }

                    if (!string.IsNullOrEmpty(thinking) || !string.IsNullOrEmpty(text))
                    {
                        _flow.Items.Add(
                            new DocumentFlowItem
                            {
                                Content = doc,
                                Alignment = DocumentFlowAlignment.Stretch,
                                BackgroundStyle = Style
                                    .None.WithBackground(_colors.AssistantBubbleBg)
                                    .WithForeground(_colors.AssistantBubbleFg),
                                Padding = new Thickness(3, 1, 3, 1),
                            }
                        );
                    }
                    break;
                }
                case ChatMessageRoles.Unknown:
                // ── Unknown 消息：变体选择 / 错误 / 中断 ──
                {
                    // 变体选择事件：内容格式 "选择变体 #N: label"
                    if (text.StartsWith("选择变体", StringComparison.Ordinal))
                    {
                        var variantBlock = new TextBlock($"✅ {text}")
                            .TextAlignment(TextAlignment.Left)
                            .HorizontalAlignment(Align.Stretch)
                            .Style(
                                new TextBlockStyle
                                {
                                    Foreground = _colors.ThinkingBubbleFg,
                                    Background = _colors.ThinkingBubbleBg,
                                    FillBackground = true,
                                }
                            );

                        _flow.Items.Add(
                            new DocumentFlowItem
                            {
                                Content = new FlowDocument().Add(variantBlock),
                                Alignment = DocumentFlowAlignment.Stretch,
                                BackgroundStyle = Style
                                    .None.WithBackground(_colors.ThinkingBubbleBg)
                                    .WithForeground(_colors.ThinkingBubbleFg),
                                Padding = new Thickness(3, 1, 3, 1),
                            }
                        );
                    }
                    // 错误事件
                    // error 事件：内容格式 "❌ errorCode content"
                    else if (text.StartsWith('❌'))
                    {
                        var errorBlock = new TextBlock(text)
                            .TextAlignment(TextAlignment.Left)
                            .Wrap(true)
                            .HorizontalAlignment(Align.Stretch)
                            .Style(
                                new TextBlockStyle
                                {
                                    Foreground = _colors.ErrorBubbleFg,
                                    Background = _colors.ErrorBubbleBg,
                                    FillBackground = true,
                                }
                            );

                        _flow.Items.Add(
                            new DocumentFlowItem
                            {
                                Content = new FlowDocument().Add(errorBlock),
                                Alignment = DocumentFlowAlignment.Stretch,
                                BackgroundStyle = Style
                                    .None.WithBackground(_colors.ErrorBubbleBg)
                                    .WithForeground(_colors.ErrorBubbleFg),
                                Padding = new Thickness(3, 1, 3, 1),
                            }
                        );
                    }
                    // 其他 Unknown 消息（中断输出等）
                    else
                    {
                        // 其他 Unknown 消息（中断等）
                        var interruptBlock = new TextBlock(text.Length > 0 ? text : "⛔ 中断输出")
                            .TextAlignment(TextAlignment.Left)
                            .HorizontalAlignment(Align.Stretch)
                            .Style(
                                new TextBlockStyle
                                {
                                    Foreground = _colors.InterruptBubbleFg,
                                    Background = _colors.InterruptBubbleBg,
                                    FillBackground = true,
                                }
                            );

                        _flow.Items.Add(
                            new DocumentFlowItem
                            {
                                Content = new FlowDocument().Add(interruptBlock),
                                Alignment = DocumentFlowAlignment.Stretch,
                                BackgroundStyle = Style
                                    .None.WithBackground(_colors.InterruptBubbleBg)
                                    .WithForeground(_colors.InterruptBubbleFg),
                                Padding = new Thickness(3, 1, 3, 1),
                            }
                        );
                    }
                    break;
                }
                case ChatMessageRoles.Tool when HiddenTools.IsHidden(m.Name):
                    // 上下文接续机制对用户隐藏：工具结果不进会话面板
                    break;
                case ChatMessageRoles.Tool:
                // ── 工具调用结果：查找前一条 Assistant 消息中的 ToolCall 参数 ──
                {
                    var name = m.Name ?? "tool";
                    var isError = m.ToolInvocationSucceeded == false;
                    var toolResultText = m.Content ?? "";

                    var (subtitle, paramDisplay) = ("", "");
                    string? argsJson = null;
                    var toolCallId = m.ToolCallId;
                    for (var j = i - 1; j >= 0; j--)
                    // 向前搜索匹配的 ToolCall（通过 ID 或名称匹配）
                    {
                        if (messages[j].Role != ChatMessageRoles.Assistant)
                            continue;
                        var prevMsg = messages[j];
                        if (prevMsg.ToolCalls is null || prevMsg.ToolCalls.Count == 0)
                            continue;
                        ToolCall? tc = null;
                        if (!string.IsNullOrEmpty(toolCallId))
                            tc = prevMsg.ToolCalls.FirstOrDefault(t => t.Id == toolCallId);
                        tc ??= prevMsg.ToolCalls.FirstOrDefault(t => t.FunctionCall?.Name == name);
                        if (tc?.FunctionCall?.Arguments is { } args)
                        {
                            (subtitle, paramDisplay) = ToolCallRenderer.ParseToolArgs(args);
                            argsJson = args; // preserve full args for ask_user rendering
                        }
                        break;
                    }

                    var tool = _registry.TryGet(name);
                    var category = tool?.Category ?? ToolCategory.Generic;
                    var entityType = tool?.EntityType;

                    _flow.Items.Add(
                        ToolCallRenderer.BuildToolCallResultItem(
                            category,
                            entityType,
                            name,
                            subtitle,
                            paramDisplay,
                            isError,
                            ToolCallRenderer.Truncate(
                                name == "tool_search"
                                    ? HiddenTools.Redact(toolResultText)
                                    : toolResultText,
                                500
                            ),
                            _colors,
                            _onNavigateToEntity,
                            details: null,
                            argsJson: argsJson
                        )
                    );
                    break;
                }
            }
        }
    }
}
