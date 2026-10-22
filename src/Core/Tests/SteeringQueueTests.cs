using Core.Agent;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;

namespace Core.Tests;

/// <summary>
/// SteeringQueue 转向队列测试。
/// 验证消息入队、出队和清空逻辑。
/// </summary>
public class SteeringQueueTests
{
    [Fact]
    /// <summary>入队后 HasPending 应为 true。</summary>
    public void Enqueue_AndHasPending()
    {
        var queue = new SteeringQueue();
        Assert.False(queue.HasPending);

        queue.Enqueue(new ChatMessage(ChatMessageRoles.User, "hello"));
        Assert.True(queue.HasPending);
    }

    [Fact]
    /// <summary>All 模式应返回所有消息（含 SteerId）。</summary>
    public void DequeueAllWithIds_ModeAll_ReturnsAllMessages()
    {
        var queue = new SteeringQueue { Mode = QueueDeliveryMode.All };
        queue.Enqueue(new ChatMessage(ChatMessageRoles.User, "msg1"), "sid1");
        queue.Enqueue(new ChatMessage(ChatMessageRoles.User, "msg2"));

        var messages = queue.DequeueAllWithIds();
        Assert.Equal(2, messages.Count);
        Assert.Equal("sid1", messages[0].SteerId);
        Assert.Null(messages[1].SteerId);
        Assert.Equal("msg1", messages[0].Message.Content);
        Assert.Equal("msg2", messages[1].Message.Content);
        Assert.False(queue.HasPending);
    }

    [Fact]
    /// <summary>OneAtATime 模式应每次返回一条消息。</summary>
    public void DequeueAllWithIds_ModeOneAtATime_ReturnsOneMessage()
    {
        var queue = new SteeringQueue { Mode = QueueDeliveryMode.OneAtATime };
        queue.Enqueue(new ChatMessage(ChatMessageRoles.User, "msg1"));
        queue.Enqueue(new ChatMessage(ChatMessageRoles.User, "msg2"));

        var first = queue.DequeueAllWithIds();
        Assert.Single(first);

        var second = queue.DequeueAllWithIds();
        Assert.Single(second);

        Assert.False(queue.HasPending);
    }

    [Fact]
    /// <summary>空队列应返回空列表。</summary>
    public void DequeueAllWithIds_EmptyQueue_ReturnsEmpty()
    {
        var queue = new SteeringQueue();
        var result = queue.DequeueAllWithIds();
        Assert.Empty(result);
    }

    [Fact]
    /// <summary>清空后队列应为空。</summary>
    public void Clear_RemovesAllMessages()
    {
        var queue = new SteeringQueue();
        queue.Enqueue(new ChatMessage(ChatMessageRoles.User, "msg1"));
        queue.Enqueue(new ChatMessage(ChatMessageRoles.User, "msg2"));
        queue.Clear();

        Assert.False(queue.HasPending);
        Assert.Empty(queue.DequeueAllWithIds());
    }
}
