using System.Runtime.CompilerServices;
using System.Text;
using Core.Shared;

namespace Core.Tools;

/// <summary>
/// 工具调度器：提供工具执行相关的辅助方法。
/// 包括内容提取、调用记录和环形缓冲区等实用功能。
/// </summary>
public static class ToolDispatcher
{
    /// <summary>
    /// 从内容块列表中提取文本内容。
    /// 如果列表为空或 null，返回空字符串。
    /// 如果只有一个 TextContent 元素，直接返回其文本。
    /// 否则拼接所有 TextContent 的文本。
    /// </summary>
    /// <param name="content">内容块列表。</param>
    /// <returns>提取的文本内容。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractText(IReadOnlyList<ContentBlock>? content)
    {
        if (content is null || content.Count == 0)
            return "";
        if (content is [TextContent tc])
            return tc.Text;
        var sb = new StringBuilder(content.Count * 32);
        foreach (var block in content)
        {
            if (block is TextContent text)
                sb.Append(text.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 工具调用记录结构体，用于断路器历史追踪。
    /// 记录每次工具调用的名称、参数和回合 ID。
    /// </summary>
    /// <param name="ToolName">调用的工具名称。</param>
    /// <param name="Arguments">调用参数（JSON 格式）。</param>
    /// <param name="TurnId">回合 ID，默认为 0。</param>
    public readonly record struct ToolCallRecord(string ToolName, string Arguments, int TurnId = 0);

    /// <summary>
    /// 固定大小的环形缓冲区，用于存储调用历史。
    /// 当缓冲区满时，新元素会覆盖最旧的元素。
    /// </summary>
    /// <typeparam name="T">存储的元素类型。</typeparam>
    public sealed class CircularBuffer<T>(int capacity)
    {
        /// <summary>内部存储数组。</summary>
        private readonly T[] _buffer = new T[capacity];

        /// <summary>缓冲区最大容量。</summary>
        private readonly int _capacity = capacity;

        /// <summary>当前写入位置（头指针）。</summary>
        private int _head;

        /// <summary>当前缓冲区中的元素数量。</summary>
        public int Count { get; private set; }

        /// <summary>
        /// 添加元素到缓冲区。
        /// 如果缓冲区已满，新元素会覆盖最旧的元素。
        /// </summary>
        /// <param name="item">要添加的元素。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;
            if (Count < _capacity)
                Count++;
        }

        /// <summary>
        /// 获取最近的 N 个元素（按插入时间倒序，最新的在最前面）。
        /// </summary>
        /// <param name="count">要获取的元素数量。</param>
        /// <returns>最近的元素列表。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public List<T> GetRecent(int count)
        {
            var n = Math.Min(count, Count);
            var result = new List<T>(n);
            for (var i = 0; i < n; i++)
            {
                // 计算索引，处理负数取模
                var idx = ((_head - 1 - i) % _capacity + _capacity) % _capacity;
                result.Add(_buffer[idx]);
            }
            return result;
        }
    }
}
