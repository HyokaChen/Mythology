using System.Text.Json;
using Core.Data;
using Core.Shared;
using Desktop.Models;
using ChatMessage = Desktop.Models.ChatMessage;

namespace Desktop.Services;

/// <summary>
/// JSONL 会话消息 → Desktop UI 气泡的映射（恢复会话时用）。
/// 渲染语义对齐 TUI <c>Tui/Controls/MessageRestorer.cs</c>：
/// User→用户气泡（跳过 ask_user 回复前缀）；同一 turn 的 Assistant/Tool/回合内事件
/// 合并为一条 Agent 消息（思考步骤 + 工具步骤 + 中间正文 + 最终正文/系统事件），
/// Tool 结果按 ToolCallId 回填对应步骤；compact/非回合事件→dim 系统行。
/// 复制/重试动作条因此每轮只出现在回合末尾一次。
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
        // 一个 turn = 一条 Agent 消息：连续 Assistant/Tool/回合内事件都并进 currentAgent，
        // 直到下一条可见用户消息或会话结束。这样复制/重试只出现在回合末尾一次。
        ChatMessage? currentAgent = null;

        void EnsureAgent()
        {
            if (currentAgent is not null)
                return;
            currentAgent = new ChatMessage(MessageSender.Agent) { IsCompleted = true };
            result.Add(currentAgent);
        }

        void FlushAgent()
        {
            if (currentAgent is { IsEmpty: true })
                result.Remove(currentAgent);
            currentAgent = null;
        }

        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            var text = m.Content ?? "";

            // context_overflow 是自动恢复的内部信号：旧会话里可能已持久化，
            // 但成功 compact 后不应再作为“调用出错”系统行出现。
            if (m.ErrorCode == "context_overflow")
                continue;

            switch (m.Role)
            {
                case MessageRole.User when m.EventType == SessionEventType.Compact:
                case MessageRole.Unknown when m.EventType == SessionEventType.Compact:
                {
                    // compact 摘要：dim 系统行（Active 模式渲染为 Unknown+EventType）
                    var msg = new ChatMessage(MessageSender.System);
                    msg.Content.Append(SessionInternalMessages.FormatCompactSummaryPreview(text));
                    result.Add(msg);
                    break;
                }

                case MessageRole.User when SessionInternalMessages.IsAskUserReply(text):
                    // ask_user 回复：工具卡 Q&A 已展示完整信息，跳过气泡（对齐 TUI），
                    // 也不结束当前 turn。
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

                    FlushAgent();
                    var msg = new ChatMessage(MessageSender.User) { EntryId = m.EntryId };
                    msg.Content.Append(text);
                    result.Add(msg);
                    break;
                }

                case MessageRole.Assistant
                    when SessionInternalMessages.IsSystemAssistantMessage(text):
                {
                    if (string.IsNullOrWhiteSpace(text))
                        break;
                    var normalized = SessionInternalMessages.NormalizeSystemText(text);
                    // 回合内系统提醒：有正文时按时间顺序接到正文后面，
                    // 否则作为 SystemEvent 步骤留在回合时间线里（动作行之前）。
                    if (currentAgent is not null)
                    {
                        if (currentAgent.HasVisibleContent)
                            currentAgent.AppendContent($"\n\n{normalized}");
                        else
                            currentAgent.AgentSteps.Add(AgentStep.CreateSystemEvent(normalized));
                    }
                    else
                    {
                        var msg = new ChatMessage(MessageSender.System);
                        msg.Content.Append(normalized);
                        result.Add(msg);
                    }
                    break;
                }

                case MessageRole.Assistant:
                {
                    EnsureAgent();
                    var agent = currentAgent!;

                    // 旧会话把中断提示写成 Markdown 引用块；历史恢复时还原为独立中断行，
                    // 与实时停止一致，也避免引用块在消息卡里再套一层边框。
                    const string interruptedMarker = "> ⏹ 已中断";
                    var trimmedText = text.TrimEnd();
                    if (trimmedText.EndsWith(interruptedMarker, StringComparison.Ordinal))
                    {
                        text = trimmedText[..^interruptedMarker.Length].TrimEnd();
                        agent.IsInterrupted = true;
                    }

                    if (!string.IsNullOrEmpty(m.ReasoningContent))
                    {
                        var thinking = new AgentStep
                        {
                            Kind = StepKind.Thinking,
                            Title = "思考过程",
                            Status = AgentStepStatus.Completed,
                            IsDetailsExpanded = false,
                        };
                        thinking.ThinkingBuilder.Append(m.ReasoningContent);
                        thinking.NotifyThinkingChanged();
                        agent.AgentSteps.Add(thinking);
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
                            agent.AgentSteps.Add(AgentStep.CreateContent(text.Trim()));
                        else
                        {
                            if (agent.HasVisibleContent)
                                agent.AppendContent("\n\n");
                            agent.AppendContent(text);
                        }
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
                            agent.AgentSteps.Add(step);
                        }
                    }
                    break;
                }

                case MessageRole.Tool when Core.Tools.HiddenTools.IsHidden(m.ToolName):
                    // 上下文接续机制对用户隐藏：工具结果不回填、不降级为独立行
                    break;
                case MessageRole.Tool:
                {
                    // 工具结果：回填到发起它的 Assistant 步骤；找不到归属时降级为回合内系统行
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
                    var summary = ToolDisplayFormatter.Result(text);
                    var line =
                        $"{(m.IsError ? "✗" : "•")} {display.Label(m.ToolName)}"
                        + (summary.Length > 0 ? $"：{summary}" : "");
                    if (currentAgent is not null)
                        currentAgent.AgentSteps.Add(
                            AgentStep.CreateSystemEvent(line, isError: m.IsError)
                        );
                    else
                    {
                        var msg = new ChatMessage(MessageSender.System) { IsError = m.IsError };
                        msg.Content.Append(line);
                        result.Add(msg);
                    }
                    break;
                }

                case MessageRole.Unknown:
                {
                    if (string.IsNullOrEmpty(text) && m.EventType != SessionEventType.Error)
                        break;
                    var prefix = m.EventType switch
                    {
                        SessionEventType.Error => "⚠️ 调用出错：",
                        SessionEventType.Interrupt => "⏹ ",
                        _ => "",
                    };
                    var displayText =
                        m.EventType == SessionEventType.Error
                            ? AgentErrorFormatter.Format(text)
                            : text;

                    if (currentAgent is not null && m.EventType == SessionEventType.Interrupt)
                    {
                        // 中断也属于 turn 结束：复用独立中断行，动作行排在其后。
                        currentAgent.IsInterrupted = true;
                        break;
                    }

                    if (currentAgent is not null && m.EventType == SessionEventType.Error)
                    {
                        var line = prefix + displayText;
                        // 有最终正文时按时间顺序追加；否则作为回合内系统步骤。
                        if (currentAgent.HasVisibleContent)
                            currentAgent.AppendContent($"\n\n{line}");
                        else
                            currentAgent.AgentSteps.Add(
                                AgentStep.CreateSystemEvent(line, isError: true)
                            );
                        break;
                    }

                    // 其他 event（model_change / variant_selected 等）仍为 dim 系统行。
                    var msg = new ChatMessage(MessageSender.System)
                    {
                        IsError = m.EventType == SessionEventType.Error,
                    };
                    msg.Content.Append(prefix + displayText);
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

        FlushAgent();
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
}
