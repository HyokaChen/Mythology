using LlmTornado.Code;

namespace Core.Data;

/// <summary>
/// <see cref="MessageRole"/>（Core 持久化层）与 <see cref="ChatMessageRoles"/>（LlmTornado）之间的
/// 唯一映射点。所有 role 转换必须通过此类，避免散落在多个文件中重复定义。
/// </summary>
public static class MessageRoleMapping
{
    // ── ChatMessageRoles → MessageRole ──

    public static MessageRole ToMessageRole(ChatMessageRoles role) =>
        role switch
        {
            ChatMessageRoles.System => MessageRole.System,
            ChatMessageRoles.User => MessageRole.User,
            ChatMessageRoles.Assistant => MessageRole.Assistant,
            ChatMessageRoles.Tool => MessageRole.Tool,
            ChatMessageRoles.Unknown => MessageRole.Unknown,
            _ => MessageRole.User,
        };

    // ── MessageRole → ChatMessageRoles ──

    public static ChatMessageRoles ToChatMessageRoles(MessageRole role) =>
        role switch
        {
            MessageRole.System => ChatMessageRoles.System,
            MessageRole.User => ChatMessageRoles.User,
            MessageRole.Assistant => ChatMessageRoles.Assistant,
            MessageRole.Tool => ChatMessageRoles.Tool,
            MessageRole.Unknown => ChatMessageRoles.Unknown,
            _ => ChatMessageRoles.User,
        };

    // ── ChatMessageRoles → string（持久化用） ──

    public static string ToRoleString(ChatMessageRoles role) =>
        role switch
        {
            ChatMessageRoles.System => "system",
            ChatMessageRoles.User => "user",
            ChatMessageRoles.Assistant => "assistant",
            ChatMessageRoles.Tool => "tool",
            ChatMessageRoles.Unknown => "unknown",
            _ => "user",
        };

    // ── string → MessageRole（从持久化恢复） ──

    /// <remarks>
    /// "unknown" 映射为 <see cref="MessageRole.Unknown"/>，语义上表示中断事件。
    /// 旧 JSONL 中 role=unknown 的消息条目和新的 type=event 条目均通过此映射
    /// 转为 Unknown 角色，UI 层统一渲染为中断/事件标记，不进 Conversation。
    /// </remarks>
    public static MessageRole ToMessageRole(string role) =>
        role.ToLowerInvariant() switch
        {
            "system" => MessageRole.System,
            "user" => MessageRole.User,
            "assistant" => MessageRole.Assistant,
            "tool" => MessageRole.Tool,
            "unknown" => MessageRole.Unknown,
            _ => MessageRole.User,
        };
}
