using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 发射模式：索引侧用搜索模式（长词 + 已知子词），查询侧 AND 只取最佳路径、OR 含子词。
/// 依据 docs/lexical-retokenize-plan.md §A0。
/// </summary>
public sealed class LexicalTextSearchModeTests
{
    private static string[] Tokens(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void IndexText_EmitsLongWordAndSubwords()
    {
        NovelLexicon.Clear();
        try
        {
            NovelLexicon.Update("mode-novel", ["潮音堂书店"]);
            var tokens = Tokens(LexicalText.ToIndexText("他去了潮音堂书店", "mode-novel"));

            // 长词（原子命中）与子词（部分命中）同时存在
            Assert.Contains("潮音堂书店", tokens);
            Assert.Contains("书店", tokens);
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }

    [Fact]
    public void IndexText_ExpandsSubwordsOfLongWords()
    {
        NovelLexicon.Clear();
        try
        {
            // 长词会吞掉组成词：整词进索引后，搜「大学」「中国」「人民」「中国人民」本来都会落空
            var tokens = Tokens(LexicalText.ToIndexText("他考上了中国人民大学。", "mode-novel"));

            Assert.Contains("中国人民大学", tokens);
            Assert.Contains("大学", tokens);
            Assert.Contains("中国", tokens);
            Assert.Contains("人民", tokens);
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }

    [Fact]
    public void IndexText_DoesNotExpandShortOrdinaryWords()
    {
        NovelLexicon.Clear();
        try
        {
            // 3 字普通词（未注册）不补子词：体育馆保持整词，索引不为此多付成本
            var tokens = Tokens(LexicalText.ToIndexText("他去了体育馆", "mode-novel"));

            Assert.Contains("体育馆", tokens);
            Assert.DoesNotContain("体育", tokens);
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }

    [Fact]
    public void QueryTerms_AndLaneHasNoSubwords_OrLaneHasThem()
    {
        NovelLexicon.Clear();
        try
        {
            NovelLexicon.Update("mode-novel", ["潮音堂书店"]);

            var andTerms = LexicalText.GetQueryTerms("潮音堂书店", "mode-novel", searchMode: false);
            var orTerms = LexicalText.GetQueryTerms("潮音堂书店", "mode-novel", searchMode: true);

            Assert.Equal(["潮音堂书店"], andTerms);
            Assert.Contains("书店", orTerms);
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }

    [Fact]
    public void BuildMatchQuery_UsesAndForPrecisionOrForRecall()
    {
        NovelLexicon.Clear();
        try
        {
            NovelLexicon.Update("mode-novel", ["潮音堂书店"]);
            Assert.Equal(
                "\"潮音堂书店\"",
                LexicalText.BuildMatchQuery("潮音堂书店", "mode-novel", useOr: false)
            );
            Assert.Contains(
                " OR ",
                LexicalText.BuildMatchQuery("潮音堂书店", "mode-novel", useOr: true)
            );
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }
}
