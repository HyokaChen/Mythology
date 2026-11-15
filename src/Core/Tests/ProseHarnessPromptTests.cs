using Core.Prose;
using Xunit;

namespace Core.Tests;

public sealed class ProseHarnessPromptTests
{
    [Fact]
    public void SystemPrompt_GivesHarnessFinalDecisionAndNoFixedCount()
    {
        Assert.Contains("最终决定权", ProseHarnessPrompt.System);
        Assert.Contains("不设固定数量", ProseHarnessPrompt.System);
        Assert.DoesNotContain("1–5", ProseHarnessPrompt.System);
    }

    [Fact]
    public void SystemPrompt_TreatsInlineDashAsLegalProse()
    {
        Assert.Contains("句内破折号是合法正文", ProseHarnessPrompt.System);
        Assert.Contains("语音延长", ProseHarnessPrompt.System);
        Assert.Contains("话语中断", ProseHarnessPrompt.System);
        Assert.Contains("不要把它当作独立分隔符", ProseHarnessPrompt.System);
        Assert.Contains("不要删除、合并、替换或改写它", ProseHarnessPrompt.System);
    }

    [Fact]
    public void BuildUser_IncludesChapterAndNeighborContext()
    {
        var request = new ProseHarnessRequest
        {
            NovelTitle = "测试小说",
            Genre = "悬疑",
            ChapterTitle = "第二章",
            ChapterOrder = 2,
            ChapterSummary = "本章细纲",
            Content = "甲\n\n---\n\n乙",
            Operation = "edit",
            PreviousChapterTitle = "第一章",
            PreviousChapterSummary = "前章细纲",
            PreviousChapterExcerpt = "前章结尾",
            NextChapterTitle = "第三章",
            NextChapterSummary = "后章细纲",
            NextChapterExcerpt = "后章开头",
        };

        var prompt = ProseHarnessPrompt.BuildUser(request);

        Assert.Contains("第二章", prompt);
        Assert.Contains("本章细纲", prompt);
        Assert.Contains("前章结尾", prompt);
        Assert.Contains("后章开头", prompt);
        Assert.Contains("甲\n\n---\n\n乙", prompt);
        Assert.Contains("局部编辑后的整章", prompt);
    }

    [Fact]
    public void BuildUser_WithRepairHint_IncludesRepairRequest()
    {
        var prompt = ProseHarnessPrompt.BuildUser(
            new ProseHarnessRequest { Content = "甲" },
            "正文第 1 段不一致"
        );

        Assert.Contains("repair_request", prompt);
        Assert.Contains("正文第 1 段不一致", prompt);
    }
}
