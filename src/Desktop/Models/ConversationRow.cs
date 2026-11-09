using Avalonia;

namespace Desktop.Models;

/// <summary>对话区扁平化后的行类型。一个 ChatMessage 拆成多行，行高度小且可独立增量插入。</summary>
public enum ConversationRowKind
{
    User,
    System,
    Waiting,
    Step,
    Content,
    Interrupted,
    Sources,
    Process,
}

/// <summary>
/// 扁平化对话行。只引用源消息/步骤，不复制内容；模板直接绑定源对象，
/// 因此流式内容、步骤状态和展开状态仍然实时更新。
/// </summary>
public sealed class ConversationRow
{
    public required ConversationRowKind Kind { get; init; }

    public required ChatMessage Message { get; init; }

    public AgentStep? Step { get; init; }

    /// <summary>是否是一条消息的最后一行（最后一行留 12px，其余 4px）。</summary>
    public bool IsLastInMessage { get; set; }

    public Thickness Margin => new(0, 0, 0, IsLastInMessage ? 12 : 4);

    /// <summary>把一条 ChatMessage 拆成当前状态下需要显示的行（视图与测试共用）。</summary>
    public static List<ConversationRow> FromMessage(ChatMessage message)
    {
        var rows = new List<ConversationRow>();
        switch (message.Sender)
        {
            case MessageSender.User:
                rows.Add(
                    new ConversationRow { Kind = ConversationRowKind.User, Message = message }
                );
                break;
            case MessageSender.System:
                rows.Add(
                    new ConversationRow { Kind = ConversationRowKind.System, Message = message }
                );
                break;
            case MessageSender.Agent:
                if (message.IsWaiting)
                    rows.Add(
                        new ConversationRow
                        {
                            Kind = ConversationRowKind.Waiting,
                            Message = message,
                        }
                    );
                if (message.IsProcessExpanded)
                {
                    foreach (var step in message.AgentSteps)
                        rows.Add(
                            new ConversationRow
                            {
                                Kind = ConversationRowKind.Step,
                                Message = message,
                                Step = step,
                            }
                        );
                }
                else if (message.AgentSteps.Count > 0)
                {
                    rows.Add(
                        new ConversationRow
                        {
                            Kind = ConversationRowKind.Process,
                            Message = message,
                        }
                    );
                }
                if (message.HasVisibleContent)
                    rows.Add(
                        new ConversationRow
                        {
                            Kind = ConversationRowKind.Content,
                            Message = message,
                        }
                    );
                if (message.IsInterrupted)
                    rows.Add(
                        new ConversationRow
                        {
                            Kind = ConversationRowKind.Interrupted,
                            Message = message,
                        }
                    );
                if (message.Sources.Count > 0)
                    rows.Add(
                        new ConversationRow
                        {
                            Kind = ConversationRowKind.Sources,
                            Message = message,
                        }
                    );
                break;
        }

        if (rows.Count > 0)
            rows[^1].IsLastInMessage = true;
        return rows;
    }

    public static List<ConversationRow> FromMessages(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(FromMessage).ToList();
}
