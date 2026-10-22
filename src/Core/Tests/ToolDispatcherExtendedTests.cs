using Core.Shared;
using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ToolDispatcher 扩展测试：ExtractText 更多边界情况。
/// </summary>
public sealed class ToolDispatcherExtendedTests
{
    // ══════════════════════════════════════════════════════
    //  ExtractText — 更多边界情况
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ExtractText_MixedContent_OnlyTextContentExtracted()
    {
        var content = new List<ContentBlock>
        {
            new TextContent("hello"),
            new ImageContent("url"),
            new TextContent(" world"),
        };
        var result = ToolDispatcher.ExtractText(content);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void ExtractText_OnlyNonTextContent_ReturnsEmpty()
    {
        var content = new List<ContentBlock> { new ImageContent("url1"), new ImageContent("url2") };
        var result = ToolDispatcher.ExtractText(content);
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractText_SingleEmptyTextContent_ReturnsEmpty()
    {
        var content = new List<ContentBlock> { new TextContent("") };
        var result = ToolDispatcher.ExtractText(content);
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractText_ManyEmptyTextContents_ReturnsEmpty()
    {
        var content = new List<ContentBlock> { new TextContent(""), new TextContent("") };
        var result = ToolDispatcher.ExtractText(content);
        Assert.Equal("", result);
    }

    // ══════════════════════════════════════════════════════
    //  CircularBuffer — 更多边界情况
    // ══════════════════════════════════════════════════════

    [Fact]
    public void CircularBuffer_SingleCapacity_OverwritesEveryTime()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(1);
        buf.Add(1);
        buf.Add(2);
        Assert.Equal(1, buf.Count);
        Assert.Equal(2, buf.GetRecent(1)[0]);
    }

    [Fact]
    public void CircularBuffer_GetRecent_MoreThanCount_ReturnsAll()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(5);
        buf.Add(1);
        buf.Add(2);
        var recent = buf.GetRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal(2, recent[0]); // newest first
        Assert.Equal(1, recent[1]);
    }

    [Fact]
    public void CircularBuffer_GetRecent_Zero_ReturnsEmpty()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(5);
        buf.Add(1);
        Assert.Empty(buf.GetRecent(0));
    }

    [Fact]
    public void CircularBuffer_ExactCapacity_NoOverflow()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(3);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);
        Assert.Equal(3, buf.Count);
        var recent = buf.GetRecent(3);
        Assert.Equal([3, 2, 1], recent);
    }

    [Fact]
    public void CircularBuffer_WrapAround_CorrectOrder()
    {
        var buf = new ToolDispatcher.CircularBuffer<int>(3);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);
        buf.Add(4); // wraps, overwrites 1
        Assert.Equal(3, buf.Count);
        var recent = buf.GetRecent(3);
        Assert.Equal([4, 3, 2], recent);
    }
}
