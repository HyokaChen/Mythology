// 功能：Markdown 降级与分段测试
//
// 最重要的一条是 PlainProseIsUntouched：Agent 写的正文绝大多数是普通中文段落，
// 降级器把正文改坏比不降级严重得多。

using Xunit;

namespace Channels.WeChat.Tests;

public class WeChatMarkdownTests
{
    [Theory]
    [InlineData("# 第三章", "第三章")]
    [InlineData("### 三、夜访", "三、夜访")]
    [InlineData("> 引用的一句话", "引用的一句话")]
    [InlineData("- 第一条", "· 第一条")]
    [InlineData("* 第二条", "· 第二条")]
    public void StripsStructuralMarkers(string input, string expected)
    {
        Assert.Equal(expected, WeChatMarkdown.ToPlainText(input));
    }

    [Theory]
    [InlineData("这是**加粗**的", "这是加粗的")]
    [InlineData("这是__加粗__的", "这是加粗的")]
    [InlineData("这是*斜体*的", "这是斜体的")]
    [InlineData("行内 `code` 保留", "行内 code 保留")]
    public void StripsEmphasisAndInlineCode(string input, string expected)
    {
        Assert.Equal(expected, WeChatMarkdown.ToPlainText(input));
    }

    [Fact]
    public void KeepsLinkTextAndUrl()
    {
        // URL 必须留着：丢掉它等于丢掉信息
        Assert.Equal(
            "见 文档 (https://example.com/a)",
            WeChatMarkdown.ToPlainText("见 [文档](https://example.com/a)")
        );
    }

    [Fact]
    public void ImageBecomesAltText()
    {
        Assert.Equal(
            "封面图",
            WeChatMarkdown.ToPlainText("![封面图](https://example.com/cover.png)")
        );
    }

    [Fact]
    public void CodeFenceMarkersAreDroppedButContentKept()
    {
        var markdown = "前文\n```json\n{\"a\":1}\n```\n后文";

        var plain = WeChatMarkdown.ToPlainText(markdown);

        Assert.DoesNotContain("```", plain, StringComparison.Ordinal);
        Assert.Contains("\"a\":1", plain, StringComparison.Ordinal);
        Assert.Contains("前文", plain, StringComparison.Ordinal);
        Assert.Contains("后文", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownInsideCodeFenceIsNotRewritten()
    {
        // 围栏内是代码：`- ` 不该变成 `· `，`**` 不该被剥
        var markdown = "```\n- **not a bullet**\n```";

        var plain = WeChatMarkdown.ToPlainText(markdown);

        Assert.Equal("- **not a bullet**", plain);
    }

    [Fact]
    public void HorizontalRuleIsDropped()
    {
        // 分隔线整行消失，段落间隔保留（不要把它也当成空行吃掉）
        Assert.Equal("上\n\n下", WeChatMarkdown.ToPlainText("上\n\n---\n\n下"));
    }

    [Fact]
    public void BlankRunsCollapse()
    {
        Assert.Equal("甲\n\n乙", WeChatMarkdown.ToPlainText("甲\n\n\n\n\n乙"));
    }

    [Fact]
    public void PlainProseIsUntouched()
    {
        // 回归护栏：正文里的下划线命名、乘号星号、单下划线都不得被当成强调标记
        const string prose =
            "他把 file_name 里的字段念了三遍，2*3 等于 6，然后继续往下写。\n\n第二段。";

        Assert.Equal(prose, WeChatMarkdown.ToPlainText(prose));
    }

    [Fact]
    public void EmptyInputYieldsEmpty()
    {
        Assert.Equal(string.Empty, WeChatMarkdown.ToPlainText(""));
        Assert.Empty(WeChatMarkdown.SplitForWeChat(""));
    }
}

public class WeChatChunkingTests
{
    [Fact]
    public void ShortTextStaysOneMessage()
    {
        Assert.Equal(["短"], WeChatMarkdown.SplitForWeChat("短", 100));
    }

    [Fact]
    public void NeverTruncates()
    {
        // 最不能接受的失败模式：静默丢掉回复后半段
        var lines = Enumerable.Range(0, 50).Select(i => $"第 {i} 行内容").ToArray();
        var text = string.Join('\n', lines);

        var chunks = WeChatMarkdown.SplitForWeChat(text, 40);

        Assert.True(chunks.Count > 1);
        Assert.Equal(text, string.Join('\n', chunks));
    }

    [Fact]
    public void EveryChunkFitsTheLimit()
    {
        var text = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"第 {i} 行"));

        var chunks = WeChatMarkdown.SplitForWeChat(text, 60);

        Assert.All(chunks, c => Assert.True(c.Length <= 60, $"超限：{c.Length}"));
    }

    [Fact]
    public void SplitsOnLineBoundaryNotMidSentence()
    {
        var text = "第一句话。\n第二句话。\n第三句话。";

        var chunks = WeChatMarkdown.SplitForWeChat(text, 6);

        Assert.Equal("第一句话。", chunks[0]);
        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public void HardSplitsASingleOverlongLine()
    {
        var text = new string('字', 25);

        var chunks = WeChatMarkdown.SplitForWeChat(text, 10);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void DoesNotEmitEmptyChunks()
    {
        var text = string.Join('\n', Enumerable.Range(0, 20).Select(_ => "行"));

        Assert.All(WeChatMarkdown.SplitForWeChat(text, 2), c => Assert.NotEmpty(c));
    }
}
