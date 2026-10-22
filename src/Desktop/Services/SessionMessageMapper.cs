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
    public static List<ChatMessage> MapUi(List<Core.Data.ChatMessage> msgs)
    {
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

                case MessageRole.User
                    when text.StartsWith("**用户回复:**", StringComparison.Ordinal):
                    // ask_user 回复：工具卡 Q&A 已展示完整信息，跳过气泡（对齐 TUI）
                    break;

                case MessageRole.User:
                {
                    var msg = new ChatMessage(MessageSender.User);
                    msg.Content.Append(text);
                    result.Add(msg);
                    break;
                }

                case MessageRole.Assistant:
                {
                    var msg = new ChatMessage(MessageSender.Agent);
                    if (!string.IsNullOrEmpty(m.ReasoningContent))
                    {
                        var thinking = new AgentStep
                        {
                            Kind = StepKind.Thinking,
                            Title = "思考过程",
                            Status = AgentStepStatus.Completed,
                        };
                        thinking.ThinkingBuilder.Append(m.ReasoningContent);
                        thinking.NotifyThinkingChanged();
                        msg.AgentSteps.Add(thinking);
                    }
                    if (!string.IsNullOrEmpty(text))
                        msg.Content.Append(text);

                    if (m.ToolCallsJson is { Length: > 0 } json)
                    {
                        foreach (var tc in DeserializeToolCalls(json))
                        {
                            // 上下文接续机制对用户隐藏：隐藏工具不建步骤卡
                            if (Core.Tools.HiddenTools.IsHidden(tc.Name))
                                continue;

                            var step = new AgentStep
                            {
                                Kind = StepKind.ToolCall,
                                Title = tc.Name ?? "tool",
                                Status = AgentStepStatus.Completed,
                                ArgumentsSummary = Summarize(tc.Arguments, 60),
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
                        owner.ResultSummary = Summarize(
                            string.Equals(owner.Title, "tool_search", StringComparison.Ordinal)
                                ? Core.Tools.HiddenTools.Redact(text)
                                : text,
                            120
                        );
                        break;
                    }
                    var msg = new ChatMessage(MessageSender.System);
                    msg.Content.Append(
                        $"{(m.IsError ? "✗" : "•")} {m.ToolName ?? "tool"}：{Summarize(text, 120)}"
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

    private static string Summarize(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var single = text.Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }
}
