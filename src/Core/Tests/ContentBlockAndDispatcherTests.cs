using Core.Shared;
using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ContentBlock 内容块测试。
/// 验证文本和图像内容的存储。
/// </summary>
public class ContentBlockTests
{
    [Fact]
    /// <summary>文本内容应正确存储文本。</summary>
    public void TextContent_StoresText()
    {
        var tc = new TextContent("hello world");
        Assert.Equal("hello world", tc.Text);
    }

    [Fact]
    /// <summary>图像内容应正确存储 URL 和替代文本。</summary>
    public void ImageContent_StoresUrlAndAltText()
    {
        var ic = new ImageContent("https://example.com/img.png", "alt text");
        Assert.Equal("https://example.com/img.png", ic.Url);
        Assert.Equal("alt text", ic.AltText);
    }

    [Fact]
    /// <summary>图像替代文本默认值应为 null。</summary>
    public void ImageContent_AltTextDefaultsToNull()
    {
        var ic = new ImageContent("https://example.com/img.png");
        Assert.Null(ic.AltText);
    }
}

/// <summary>
/// ToolDispatcher 工具调度器测试。
/// 验证内容提取逻辑。
/// </summary>
public class ToolDispatcherTests
{
    [Fact]
    /// <summary>空内容应返回空字符串。</summary>
    public void ExtractText_NullContent_ReturnsEmpty()
    {
        Assert.Equal("", ToolDispatcher.ExtractText(null));
    }

    [Fact]
    /// <summary>空数组应返回空字符串。</summary>
    public void ExtractText_EmptyContent_ReturnsEmpty()
    {
        Assert.Equal("", ToolDispatcher.ExtractText([]));
    }

    [Fact]
    /// <summary>单个文本内容应返回其文本。</summary>
    public void ExtractText_SingleTextContent_ReturnsText()
    {
        var result = ToolDispatcher.ExtractText([new TextContent("hello")]);
        Assert.Equal("hello", result);
    }

    [Fact]
    /// <summary>多个文本内容应拼接。</summary>
    public void ExtractText_MultipleTextContents_Concatenates()
    {
        var result = ToolDispatcher.ExtractText([
            new TextContent("hello "),
            new TextContent("world"),
        ]);
        Assert.Equal("hello world", result);
    }

    [Fact]
    /// <summary>非文本内容应被跳过。</summary>
    public void ExtractText_SkipsNonTextContents()
    {
        var result = ToolDispatcher.ExtractText([
            new TextContent("hello"),
            new ImageContent("http://x.com/a.png"),
        ]);
        Assert.Equal("hello", result);
    }

    [Fact]
    /// <summary>混合内容只提取文本部分。</summary>
    public void ExtractText_MixedContent_ExtractsOnlyText()
    {
        var content = new ContentBlock[]
        {
            new TextContent("text1"),
            new ImageContent("data:image/png;base64,abc", "img.png"),
            new TextContent("text2"),
        };
        var result = ToolDispatcher.ExtractText(content);
        Assert.Equal("text1text2", result);
    }

    [Fact]
    /// <summary>全部非文本内容返回空字符串。</summary>
    public void ExtractText_AllNonText_ReturnsEmpty()
    {
        var content = new ContentBlock[]
        {
            new ImageContent("data:image/png;base64,abc", "img.png"),
        };
        var result = ToolDispatcher.ExtractText(content);
        Assert.Equal("", result);
    }
}
