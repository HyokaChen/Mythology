using System.Text.Json;
using Core.Data;
using Desktop.Models;
using ChatMessage = Desktop.Models.ChatMessage;

namespace Desktop.Services;

/// <summary>
/// JSONL 会话消息 → Desktop UI 气泡的映射（恢复会话时用）。
/// 渲染语义对齐 TUI <c>Tui/Controls/MessageRestorer.cs</c>：
/// User→用户气泡（跳过 ask_user 回复前缀）；Assistant→Agent 气泡 + 思考步骤 +
/// 工具调用步骤（参数摘要）；Tool 结果按 ToolCallId 回填前一条消息的步骤状态；
/// compact/事件条目→dim 系统行。
/// </summary>
public static class SessionMessageMapper
{
    public static List<ChatMessage> MapUi(
        List<Core.Data.ChatMessage> msgs,
        ToolDisplayFormatter? toolDisplay = null
    )
    {
        var display = toolDisplay ?? new ToolDisplayFormatter();
        var result = new List<ChatMessage>(msgs.Count);
        // ToolCallId → 所属步骤（回填结果用）
        var stepsByCallId = new Dictionary<string, AgentStep>(StringComparer.Ordinal);

        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            var text = m.Content ?? "";

            switch (m.Role)
            {
                case MessageRole.User when m.EventType == SessionEventType.Compact:
                case MessageRole.Unknown when m.EventType == SessionEventType.Compact:
                {
                    // compact 摘要：dim 系统行（Active 模式渲染为 Unknown+EventType）
                    var msg = new ChatMessage(MessageSender.System);
                    var firstLine = FirstLine(text, 120);
                    msg.Content.Append($"🗜 上下文摘要：{firstLine}");
                    result.Add(msg);
                    break;
                }

                case MessageRole.User when SessionInternalMessages.IsAskUserReply(text):
                    // ask_user 回复：工具卡 Q&A 已展示完整信息，跳过气泡（对齐 TUI）
                    break;

                case MessageRole.User:
                {
                    // 内部注入（harness/云端诊断/记忆/风格观察/人工裁决等）在实时链路本就静默，
                    // 历史里也不应变成「你」的气泡；空消息同样不占位。
                    if (
                        string.IsNullOrWhiteSpace(text)
                        || SessionInternalMessages.IsInjectedUserMessage(text)
                    )
                        break;

                    var msg = new ChatMessage(MessageSender.User);
                    msg.Content.Append(text);
                    result.Add(msg);
                    break;
                }

                case MessageRole.Assistant
                    when SessionInternalMessages.IsSystemAssistantMessage(text):
                {
                    if (string.IsNullOrWhiteSpace(text))
                        break;
                    var msg = new ChatMessage(MessageSender.System);
                    msg.Content.Append(SessionInternalMessages.NormalizeSystemText(text));
                    result.Add(msg);
                    break;
                }

                case MessageRole.Assistant:
                {
                    var msg = new ChatMessage(MessageSender.Agent);

                    // 旧会话把中断提示写成 Markdown 引用块；历史恢复时还原为独立中断行，
                    // 与实时停止一致，也避免引用块在消息卡里再套一层边框。
                    const string interruptedMarker = "> ⏹ 已中断";
                    var trimmedText = text.TrimEnd();
                    if (trimmedText.EndsWith(interruptedMarker, StringComparison.Ordinal))
                    {
                        text = trimmedText[..^interruptedMarker.Length].TrimEnd();
                        msg.IsInterrupted = true;
                    }

                    if (!string.IsNullOrEmpty(m.ReasoningContent))
                    {
                        var thinking = new AgentStep
                        {
                            Kind = StepKind.Thinking,
                            Title = "思考过程",
                            Status = AgentStepStatus.Completed,
                            IsDetailsExpanded = true,
                        };
                        thinking.ThinkingBuilder.Append(m.ReasoningContent);
                        thinking.NotifyThinkingChanged();
                        msg.AgentSteps.Add(thinking);
                    }
                    var toolCalls = m.ToolCallsJson is { Length: > 0 } json
                        ? DeserializeToolCalls(json)
                        : [];
                    var hasVisibleToolCall = toolCalls.Any(tc =>
                        !Core.Tools.HiddenTools.IsHidden(tc.Name)
                    );

                    // 同一轮既有正文又有可见工具调用：正文发生在工具之前，必须落在
                    // 工具步骤上方，不能留到消息末尾冒充最终答复。只有隐藏工具时不切段，
                    // 与实时流一致（隐藏工具不影响可见时间线）。
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (hasVisibleToolCall)
                            msg.AgentSteps.Add(AgentStep.CreateContent(text.Trim()));
                        else
                            msg.AppendContent(text);
                    }

                    if (toolCalls.Count > 0)
                    {
                        foreach (var tc in toolCalls)
                        {
                            // 上下文接续机制对用户隐藏：隐藏工具不建步骤卡
                            if (Core.Tools.HiddenTools.IsHidden(tc.Name))
                                continue;

                            var step = new AgentStep
                            {
                                Kind = StepKind.ToolCall,
                                ToolName = tc.Name,
                                Title = display.Label(tc.Name),
                                Status = AgentStepStatus.Completed,
                                ArgumentsSummary = ToolDisplayFormatter.Arguments(tc.Arguments),
                                FullArguments = ToolDisplayFormatter.Detail(tc.Arguments),
                            };
                            if (!string.IsNullOrEmpty(tc.Id))
                                stepsByCallId[tc.Id] = step;
                            msg.AgentSteps.Add(step);
                        }
                    }

                    if (!msg.IsEmpty)
                        result.Add(msg);
                    break;
                }

                case MessageRole.Tool when Core.Tools.HiddenTools.IsHidden(m.ToolName):
                    // 上下文接续机制对用户隐藏：工具结果不回填、不降级为独立行
                    break;
                case MessageRole.Tool:
                {
                    // 工具结果：回填到发起它的 Assistant 步骤；找不到归属时降级为独立 dim 行
                    var callId = m.ToolCallId;
                    if (callId is not null && stepsByCallId.TryGetValue(callId, out var owner))
                    {
                        owner.Status = m.IsError
                            ? AgentStepStatus.Failed
                            : AgentStepStatus.Completed;
                        owner.IsError = m.IsError;
                        var rawResult = string.Equals(
                            owner.ToolName,
                            "tool_search",
                            StringComparison.Ordinal
                        )
                            ? Core.Tools.HiddenTools.Redact(text)
                            : text;
                        owner.FullResult = ToolDisplayFormatter.Detail(rawResult);
                        owner.ResultSummary = ToolDisplayFormatter.Result(rawResult);

                        // 历史消息不持久化工具 Details；edit 的 oldText/newText
                        // 仍在参数里，可重建与 TUI 一致的结构化 diff。
                        if (
                            !m.IsError
                            && string.Equals(owner.ToolName, "edit", StringComparison.Ordinal)
                            && ToolDiffBuilder.CanRebuildEditDiff(rawResult)
                        )
                        {
                            var diff = ToolDiffBuilder.BuildFromEditArguments(owner.FullArguments);
                            if (diff is not null)
                            {
                                owner.ApplyDiff(diff.Lines, diff.Summary);
                                owner.ResultSummary = diff.Summary;
                            }
                        }

                        if (m.IsError && string.IsNullOrWhiteSpace(owner.ResultSummary))
                            owner.ResultSummary = "失败";
                        break;
                    }
                    var msg = new ChatMessage(MessageSender.System);
                    var summary = ToolDisplayFormatter.Result(text);
                    msg.Content.Append(
                        $"{(m.IsError ? "✗" : "•")} {display.Label(m.ToolName)}"
                            + (summary.Length > 0 ? $"：{summary}" : "")
                    );
                    result.Add(msg);
                    break;
                }

                case MessageRole.Unknown:
                {
                    // event 条目（Error/Interrupt/ModelChange/VariantSelected）→ dim 系统行
                    if (string.IsNullOrEmpty(text))
                        break;
                    var msg = new ChatMessage(MessageSender.System);
                    var prefix = m.EventType switch
                    {
                        SessionEventType.Error => "⚠️ ",
                        SessionEventType.Interrupt => "⏹ ",
                        _ => "",
                    };
                    msg.Content.Append(prefix + text);
                    result.Add(msg);
                    break;
                }

                default:
                    if (!string.IsNullOrEmpty(text))
                    {
                        var msg = new ChatMessage(MessageSender.System);
                        msg.Content.Append(text);
                        result.Add(msg);
                    }
                    break;
            }
        }

        return result;
    }

    private static List<ToolCallItemDto> DeserializeToolCalls(string json)
    {
        try
        {
            return ToolCallItemDto.DeserializeList(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string FirstLine(string text, int max)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }
}
