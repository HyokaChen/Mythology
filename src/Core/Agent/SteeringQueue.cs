using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LlmTornado.Chat;

namespace Core.Agent;

/// <summary>队列投递模式枚举。</summary>
public enum QueueDeliveryMode
{
    /// <summary>每次只投递一条消息。</summary>
    OneAtATime,

    /// <summary>一次性投递所有消息。</summary>
    All,
}

/// <summary>
/// Steering 消息队列。
/// 用于在 Agent 循环中注入引导性消息，控制 Agent 行为方向。
/// 优化：AggressiveInlining 热路径、预分配数组减少 DequeueAllWithIds 分配。
/// </summary>
public sealed class SteeringQueue
{
    /// <summary>内部带 SteerId 的消息记录。</summary>
    private sealed record QueuedMessage(ChatMessage Message, string? SteerId);

    /// <summary>内部消息队列。ConcurrentQueue: Enqueue 和 DequeueAll 可被不同线程并发调用。</summary>
    private readonly ConcurrentQueue<QueuedMessage> _messages = new();

    /// <summary>队列投递模式，默认一次性投递所有。</summary>
    public QueueDeliveryMode Mode { get; set; } = QueueDeliveryMode.All;

    /// <summary>是否有待处理的消息。</summary>
    public bool HasPending => !_messages.IsEmpty;

    /// <summary>将消息加入队列。</summary>
    /// <param name="message">要加入的消息。</param>
    /// <param name="steerId">Steer 标识符，用于匹配 Waiting 占位气泡。null 表示非用户 steer（Hook/CRUD 等）。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(ChatMessage message, string? steerId = null)
    {
        _messages.Enqueue(new QueuedMessage(message, steerId));
    }

    /// <summary>
    /// 根据模式取出消息（含 SteerId）。
    /// OneAtATime 模式只取一条，All 模式取所有并清空队列。
    /// </summary>
    /// <returns>取出的 (ChatMessage, string? SteerId) 元组列表。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IReadOnlyList<(ChatMessage Message, string? SteerId)> DequeueAllWithIds()
    {
        if (_messages.IsEmpty)
            return [];

        if (Mode == QueueDeliveryMode.OneAtATime)
        {
            return _messages.TryDequeue(out var qm) ? [(qm.Message, qm.SteerId)] : [];
        }

        var result = new List<(ChatMessage, string?)>(_messages.Count);
        while (_messages.TryDequeue(out var qm))
        {
            result.Add((qm.Message, qm.SteerId));
        }
        return result;
    }

    /// <summary>清空队列。</summary>
    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
    }
}
