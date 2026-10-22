using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 模糊搜索（设定面板 / 大纲搜索框的共享口径）：子序列 + 忽略大小写 + 多词与词序无关。
/// </summary>
public sealed class FuzzySearchTests
{
    [Theory]
    [InlineData("林长歌", "林歌", true)] // 子序列（不要求连续）
    [InlineData("林长歌", "长歌", true)]
    [InlineData("林长歌", "林长歌", true)]
    [InlineData("林长歌", "歌林", false)] // 子序列不成立（歌在最后，后面没有林）
    [InlineData("Yin Xue Chen", "yxc", true)] // 忽略大小写
    [InlineData("夜巡司", "夜巡", true)]
    [InlineData("夜巡司", "司夜", false)]
    [InlineData("落雁城", "", true)] // 空查询恒真（不过滤）
    [InlineData("落雁城", "   ", true)] // 全空白同样不过滤
    [InlineData("", "林", false)]
    [InlineData(null, "林", false)]
    public void Matches_Follows_Subsequence_Rules(string? text, string query, bool expected) =>
        Assert.Equal(expected, FuzzySearch.Matches(text, query));

    [Theory]
    [InlineData("林长风 男 主角", "林 主角", true)] // 多词：每词都要命中，词序无关
    [InlineData("林长风 男 主角", "主角 林", true)]
    [InlineData("林长风 男 主角", "林 反派", false)] // 有一词不命中即不匹配
    public void Matches_MultiTerm_RequiresEveryTerm(string text, string query, bool expected) =>
        Assert.Equal(expected, FuzzySearch.Matches(text, query));
}
