using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// CircularBuffer 循环缓冲区测试。
/// 验证环形存储、覆盖和最近数据获取。
/// </summary>
public class CircularBufferTests
{
    [Fact]
    /// <summary>添加元素应增加计数。</summary>
    public void Add_IncrementsCount()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(5);
        buf.Add(1);
        Assert.Equal(1, buf.Count);
        buf.Add(2);
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    /// <summary>超过容量时应循环覆盖旧数据。</summary>
    public void Add_WrapsAround()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(3);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);
        buf.Add(4); // overwrites 1

        Assert.Equal(3, buf.Count);
        var recent = buf.GetRecent(3);
        // Most recent first: 4, 3, 2
        Assert.Equal([4, 3, 2], recent);
    }

    [Fact]
    /// <summary>GetRecent 应按最近优先顺序返回。</summary>
    public void GetRecent_ReturnsMostRecentFirst()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(5);
        buf.Add(10);
        buf.Add(20);
        buf.Add(30);

        var recent = buf.GetRecent(2);
        Assert.Equal([30, 20], recent);
    }

    [Fact]
    /// <summary>请求数量超过实际数量时应返回全部。</summary>
    public void GetRecent_ReturnsAllIfCountLessThanRequested()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(5);
        buf.Add(1);
        buf.Add(2);

        var recent = buf.GetRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal([2, 1], recent);
    }

    [Fact]
    /// <summary>空缓冲区应返回空列表。</summary>
    public void GetRecent_ReturnsEmptyForEmptyBuffer()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(5);
        var recent = buf.GetRecent(3);
        Assert.Empty(recent);
    }

    [Fact]
    /// <summary>单元素缓冲区应正确返回。</summary>
    public void GetRecent_SingleElement()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(5);
        buf.Add(42);

        var recent = buf.GetRecent(1);
        Assert.Equal([42], recent);
    }
}
