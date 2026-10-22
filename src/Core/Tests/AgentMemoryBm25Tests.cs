using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// AgentMemoryBm25 单元测试：纯 C# BM25 Okapi 实现。
/// 覆盖：分词、词频、BM25 分数计算、归一化、混合中文/英文。
/// </summary>
public sealed class AgentMemoryBm25Tests
{
    // ── Tokenize ──

    [Fact]
    public void Tokenize_EnglishText_SplitsWords()
    {
        // 高频停用词（this/is/a 等）被过滤，保留实义词
        var tokens = AgentMemoryBm25.Tokenize("Hello World! This is a test.");
        Assert.Equal(["hello", "world", "test"], tokens);
    }

    [Fact]
    public void Tokenize_MixedCase_Lowercases()
    {
        var tokens = AgentMemoryBm25.Tokenize("UPPER lower MiXeD");
        Assert.Equal(["upper", "lower", "mixed"], tokens);
    }

    [Fact]
    public void Tokenize_WithNumbers_KeepsNumbers()
    {
        var tokens = AgentMemoryBm25.Tokenize("Chapter 5, verse 3");
        Assert.Equal(["chapter", "5", "verse", "3"], tokens);
    }

    [Fact]
    public void Tokenize_EmptyString_ReturnsEmpty()
    {
        var tokens = AgentMemoryBm25.Tokenize("");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_OnlyPunctuation_ReturnsEmpty()
    {
        // 中文标点、普通英文标点应被过滤
        var tokens = AgentMemoryBm25.Tokenize("！！！、、、……");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_ChineseText_JiebaSegments()
    {
        // jieba 正确分词：主角/怕/蜘蛛
        var tokens = AgentMemoryBm25.Tokenize("主角怕蜘蛛");
        Assert.Contains("主角", tokens);
        Assert.Contains("怕", tokens);
        Assert.Contains("蜘蛛", tokens);
    }

    [Fact]
    public void Tokenize_EnglishWithApostrophes_TreatsApostropheAsSeparator()
    {
        // "it" 是停用词被过滤
        var tokens = AgentMemoryBm25.Tokenize("don't can't it's");
        Assert.Equal(["don", "t", "can", "t", "s"], tokens);
    }

    // ── TermFrequencies ──

    [Fact]
    public void TermFrequencies_RepeatedTokens_CountsCorrectly()
    {
        var tokens = new List<string> { "the", "cat", "sat", "on", "the", "mat" };
        var tf = AgentMemoryBm25.TermFrequencies(tokens);
        Assert.Equal(2, tf["the"]);
        Assert.Equal(1, tf["cat"]);
        Assert.Equal(1, tf["sat"]);
        Assert.Equal(1, tf["on"]);
        Assert.Equal(1, tf["mat"]);
        Assert.False(tf.ContainsKey("dog"));
    }

    [Fact]
    public void TermFrequencies_EmptyList_ReturnsEmpty()
    {
        var tf = AgentMemoryBm25.TermFrequencies([]);
        Assert.Empty(tf);
    }

    // ── Bm25Scores ──

    [Fact]
    public void Bm25Scores_ExactMatch_ScoresHighest()
    {
        var query = "cat sat mat";
        var docs = new List<string>
        {
            "the cat sat on the mat",
            "the dog ran in the park",
            "i like pizza",
        };

        var scores = AgentMemoryBm25.ComputeNormalizedBm25(query, docs);
        Assert.Equal(3, scores.Length);
        Assert.True(scores[0] > scores[1], "匹配文档应得最高分");
        Assert.True(scores[0] > scores[2], "匹配文档应得最高分");
    }

    [Fact]
    public void Bm25Scores_EmptyQuery_ReturnsZeros()
    {
        var docs = new List<string> { "hello world", "test document" };
        var scores = AgentMemoryBm25.ComputeNormalizedBm25("", docs);
        Assert.All(scores, s => Assert.Equal(0.0, s));
    }

    [Fact]
    public void Bm25Scores_EmptyCorpus_ReturnsEmpty()
    {
        var scores = AgentMemoryBm25.ComputeNormalizedBm25("test", []);
        Assert.Empty(scores);
    }

    [Fact]
    public void Bm25Scores_NoMatch_AllZero()
    {
        var query = "xyznonexistent";
        var docs = new List<string> { "the cat sat on the mat", "hello world" };
        var scores = AgentMemoryBm25.ComputeNormalizedBm25(query, docs);
        Assert.All(scores, s => Assert.Equal(0.0, s));
    }

    [Fact]
    public void Bm25Scores_PartialMatch_ReturnsProportional()
    {
        var query = "cat dog";
        var docs = new List<string>
        {
            "cat", // 1/2 词匹配
            "cat dog", // 2/2 词匹配
            "dog", // 1/2 词匹配
            "none", // 0/2 词匹配
        };

        var scores = AgentMemoryBm25.ComputeNormalizedBm25(query, docs);
        Assert.Equal(4, scores.Length);
        Assert.True(scores[1] >= scores[0], "两词匹配应高于一词匹配");
        Assert.True(scores[0] > 0, "部分匹配应 > 0");
        Assert.Equal(0.0, scores[3]); // 无匹配
    }

    // ── NormalizeScores ──

    [Fact]
    public void NormalizeScores_AllPositive_ScalesToMax()
    {
        var raw = new double[] { 0.0, 2.0, 4.0, 1.0 };
        var norm = AgentMemoryBm25.NormalizeScores(raw);
        Assert.Equal([0.0, 0.5, 1.0, 0.25], norm);
    }

    [Fact]
    public void NormalizeScores_AllZero_ReturnsSame()
    {
        var raw = new double[] { 0.0, 0.0, 0.0 };
        var norm = AgentMemoryBm25.NormalizeScores(raw);
        Assert.Equal(raw, norm);
    }

    [Fact]
    public void NormalizeScores_Empty_ReturnsEmpty()
    {
        var norm = AgentMemoryBm25.NormalizeScores([]);
        Assert.Empty(norm);
    }

    [Fact]
    public void NormalizeScores_SingleValue_ReturnsOne()
    {
        var norm = AgentMemoryBm25.NormalizeScores([3.14]);
        Assert.Equal([1.0], norm);
    }

    // ── ComputeNormalizedBm25 (integration) ──

    [Fact]
    public void ComputeNormalizedBm25_EndToEnd_RangeIsZeroToOne()
    {
        var query = "the protagonist enters the castle";
        var docs = new List<string>
        {
            "the protagonist enters the ancient castle cautiously",
            "the weather is nice today",
            "the castle gates creak as the protagonist pushes them open",
        };

        var scores = AgentMemoryBm25.ComputeNormalizedBm25(query, docs);
        Assert.All(scores, s => Assert.InRange(s, 0.0, 1.0));
        Assert.True(scores[0] > 0, "高匹配文档应 > 0");
    }

    [Fact]
    public void ComputeNormalizedBm25_WithCustomParams_ReflectsK1AndB()
    {
        var query = "cat";
        var docs = new List<string> { "cat", "cat cat", "cat cat cat" };

        // k1 控制 term frequency 饱和：k1 越小，高频词收益越低
        var scoresLowK1 = AgentMemoryBm25.ComputeNormalizedBm25(query, docs, k1: 0.5);
        var scoresHighK1 = AgentMemoryBm25.ComputeNormalizedBm25(query, docs, k1: 2.0);

        // 高频词在低 k1 时被抑制，差异应较小
        // 高 k1 时高频词差异更大
        var diffLowK1 = scoresLowK1[2] - scoresLowK1[0];
        var diffHighK1 = scoresHighK1[2] - scoresHighK1[0];
        Assert.True(diffHighK1 >= diffLowK1, "高 k1 应放大高频词的差异");
    }
}
