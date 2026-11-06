using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// # 引用名称匹配：名称可含空格，但必须落在词边界；同前缀取最长。
/// 这是 Desktop/TUI 提交内联共用的唯一口径。
/// </summary>
public sealed class EntityReferenceMatcherTests
{
    [Fact]
    public void MatchLongest_ExactAtEnd()
    {
        var hit = EntityReferenceMatcher.MatchLongest(
            "#角色:张三",
            "#角色:".Length,
            [("张三", "ch1")]
        );

        Assert.Equal(("张三", "ch1"), hit);
    }

    [Fact]
    public void MatchLongest_NameWithSpaces_MatchedBeforeFollowingText()
    {
        var text = "#章节:第一章 开场 继续写";
        var hit = EntityReferenceMatcher.MatchLongest(
            text,
            "#章节:".Length,
            [("第一章 开场", "c1")]
        );

        Assert.Equal(("第一章 开场", "c1"), hit);
    }

    [Fact]
    public void MatchLongest_PicksLongestPrefix()
    {
        var text = "#分卷:卷一 风起 ";
        var hit = EntityReferenceMatcher.MatchLongest(
            text,
            "#分卷:".Length,
            [("卷一", "v1"), ("卷一 风起", "v2")]
        );

        Assert.Equal(("卷一 风起", "v2"), hit);
    }

    [Fact]
    public void MatchLongest_ShortNameDoesNotEatLongerWord()
    {
        // 「李」后面是「四」（仍是字母/数字）→ 不是词边界，拒绝
        var hit = EntityReferenceMatcher.MatchLongest(
            "#角色:李四",
            "#角色:".Length,
            [("李", "ch1")]
        );

        Assert.Null(hit);
    }

    [Fact]
    public void MatchLongest_PunctuationAfterNameIsBoundary()
    {
        var hit = EntityReferenceMatcher.MatchLongest(
            "#角色:张三，出场",
            "#角色:".Length,
            [("张三", "ch1")]
        );

        Assert.Equal(("张三", "ch1"), hit);
    }

    [Fact]
    public void MatchLongest_UnknownName_ReturnsNull()
    {
        var hit = EntityReferenceMatcher.MatchLongest(
            "#角色:王五 ",
            "#角色:".Length,
            [("张三", "ch1")]
        );

        Assert.Null(hit);
    }
}
