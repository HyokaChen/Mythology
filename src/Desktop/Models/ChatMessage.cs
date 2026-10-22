using System.Collections.ObjectModel;
using LiveMarkdown.Avalonia;

namespace Desktop.Models;

/// <summary>
/// 对话消息。流式内容使用 ObservableStringBuilder（LiveMarkdown 增量渲染），
/// 必须在 UI 线程上追加。
/// </summary>
public sealed class ChatMessage(MessageSender sender)
{
    /// <summary>消息 ID（Ulid）。</summary>
    public string Id { get; } = Ulid.NewUlid().ToString();

    /// <summary>发送者。</summary>
    public MessageSender Sender { get; init; } = sender;

    /// <summary>消息内容（可增量追加，LiveMarkdown 流式渲染）。</summary>
    public ObservableStringBuilder Content { get; } = new();

    /// <summary>是否正在生成（加载指示）。</summary>
    public bool Loading { get; set; }

    /// <summary>Agent 步骤（思考/工具调用）。</summary>
    public ObservableCollection<AgentStep> AgentSteps { get; } = [];

    /// <summary>信息来源（结束时提交显示）。</summary>
    public ObservableCollection<SourceInfo> Sources { get; } = [];

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    /// <summary>是否包含任何内容。</summary>
    public bool IsEmpty => Content.Length == 0 && AgentSteps.Count == 0 && Sources.Count == 0;
}
