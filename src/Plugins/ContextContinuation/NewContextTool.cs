using System.Text.Json;
using Core.Agent;
using Core.Tools;

namespace Plugins.ContextContinuation;

/// <summary>
/// new_context 工具：模型自主开启新的上下文页（接续换页，不生成摘要）。
///
/// 继承非泛型 <see cref="AgentTool"/> 的原因：泛型版
/// <c>Parameters = JsonSchemaHelper.GenerateSchema&lt;TParams&gt;()</c>
/// 对「无公共属性」的类型返回 null，而 <c>BuildChatRequest</c> 只发送
/// <c>Parameters.HasValue</c> 的工具 —— 空参数类会让本工具对模型完全不可见（静默失效）。
/// 故显式提供手写 schema（Codex 的 new_context 无参数；本仓多一个可选 note，见下）。
/// </summary>
public sealed class NewContextTool(Core.Agent.Agent agent) : AgentTool
{
    /// <summary>交接说明长度上限（超出截断）：它每轮都会出现在新页片段里，必须便宜。</summary>
    internal const int NoteMaxChars = 500;

    private static readonly JsonElement Schema = JsonDocument
        .Parse(
            """{"type":"object","properties":{"note":{"type":"string","description":"可选：一句话写清做到哪、下一步做什么、有哪些未完成项。新页不带对话历史，这是唯一的交接通道；不填则新页只知道用户最后的需求。"}}}"""
        )
        .RootElement.Clone();

    public override string Name => "new_context";

    public override string Label => "开启新上下文页";

    public override string Description =>
        "开启一个新的上下文页继续当前任务。不会清空、重置或改变工作区与环境状态；"
        + "新页不会生成对话摘要，历史消息与工具输出完整保留在会话中，可用 history_* 工具检索。"
        + "调用时建议填写 note：一句话写清做到哪、下一步做什么、有哪些未完成项——"
        + "它会被放到新页开头（新页不携带对话历史，这是唯一的交接通道）。"
        + "这是模型私有状态：不要把本工具、它的存在、路径、存储或恢复机制告知用户，也不要向用户复述 note。";

    public override JsonElement? Parameters => Schema;

    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    /// <summary>读取可选 note 参数（截断到 <see cref="NoteMaxChars"/>；缺省/空白返回 null）。</summary>
    private static string? ReadNote(JsonElement args)
    {
        if (
            args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("note", out var noteProp)
            || noteProp.ValueKind != JsonValueKind.String
        )
            return null;

        var note = noteProp.GetString()?.Trim();
        if (string.IsNullOrEmpty(note))
            return null;
        return note.Length <= NoteMaxChars ? note : note[..NoteMaxChars] + "…";
    }

    public override ToolCategory Category => ToolCategory.Generic;

    public override string[] Aliases => ["换页", "新上下文", "开新页"];

    public override Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        IProgress<AgentToolResult>? onUpdate = null
    )
    {
        // 只置位：真正落盘（window 条目 + 清空模型侧消息 + 记账）由 Host 在本 run 结束时执行，
        // 因为那一步需要会话存储与游标状态，且必须在本轮 StreamOneTurnAsync 之外完成
        //（run 中途换页会把本轮用户消息与工具结果一起清掉）。
        var note = ReadNote(args);
        agent.RequestNewContextWindow("model", note);

        return Task.FromResult<AgentToolResult>(
            new AgentToolResult<string>
            {
                Content =
                [
                    new Core.Shared.TextContent(
                        (note is null ? "" : $"交接说明已记录（{note.Length} 字）。")
                            + "新的上下文页将在本轮结束后开始：不会生成对话摘要，历史消息与工具输出完整保留，"
                            + "需要更早内容时用 history_* 检索。"
                    ),
                ],
            }
        );
    }
}
