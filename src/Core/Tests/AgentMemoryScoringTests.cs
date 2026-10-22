using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// AgentMemoryScoring 单元测试：评分公式、时间衰退、有用性压缩、余弦相似度。
/// 覆盖：边界条件、精确计算、冻结时间确定性。
/// </summary>
public sealed class AgentMemoryScoringTests
{
    // ── RecencyFactor ──

    [Fact]
    public void RecencyFactor_ZeroAge_ReturnsOne()
    {
        var now = 1000.0;
        var factor = AgentMemoryScoring.RecencyFactor(
            createdAt: 500,
            lastAccessedAt: 1000,
            now,
            halfLife: 7 * 24 * 3600
        );
        Assert.Equal(1.0, factor, 6);
    }

    [Fact]
    public void RecencyFactor_ExactHalfLife_ReturnsHalf()
    {
        var halfLife = 100.0;
        var now = 1000.0;
        var factor = AgentMemoryScoring.RecencyFactor(
            createdAt: 900,
            lastAccessedAt: null,
            now,
            halfLife
        );
        // age = 100, half_life = 100 → exp(-ln2 * 100 / 100) = exp(-ln2) = 0.5
        Assert.Equal(0.5, factor, 6);
    }

    [Fact]
    public void RecencyFactor_TwoHalfLives_ReturnsQuarter()
    {
        var halfLife = 100.0;
        var now = 1000.0;
        var factor = AgentMemoryScoring.RecencyFactor(
            createdAt: 800,
            lastAccessedAt: null,
            now,
            halfLife
        );
        // age = 200, half_life = 100 → exp(-ln2 * 200 / 100) = exp(-2*ln2) = 0.25
        Assert.Equal(0.25, factor, 6);
    }

    [Fact]
    public void RecencyFactor_NegativeAge_ReturnsOne()
    {
        // 未来创建时间不应该发生，但如果发生了应返回 1
        var now = 1000.0;
        var factor = AgentMemoryScoring.RecencyFactor(
            createdAt: 1500,
            lastAccessedAt: null,
            now,
            halfLife: 100
        );
        Assert.Equal(1.0, factor, 6);
    }

    [Fact]
    public void RecencyFactor_LastAccessedBeatsCreatedAt()
    {
        var halfLife = 100.0;
        var now = 1000.0;
        // 创建时间很早，但最近刚访问过
        var factor = AgentMemoryScoring.RecencyFactor(
            createdAt: 100,
            lastAccessedAt: 950,
            now,
            halfLife
        );
        // age = 50
        var expected = Math.Exp(-0.6931471805599453 * 50 / 100);
        Assert.Equal(expected, factor, 6);
    }

    [Fact]
    public void RecencyFactor_NeverAccessed_UsesCreatedAt()
    {
        var halfLife = 100.0;
        var now = 1000.0;
        var factor = AgentMemoryScoring.RecencyFactor(
            createdAt: 900,
            lastAccessedAt: null,
            now,
            halfLife
        );
        var expected = Math.Exp(-0.6931471805599453 * 100 / 100);
        Assert.Equal(expected, factor, 6);
    }

    [Fact]
    public void RecencyFactor_LongAgo_ApproachesZero()
    {
        var halfLife = 7 * 24 * 3600.0; // 1 week
        var now = 1000.0;
        // 一年前
        var factor = AgentMemoryScoring.RecencyFactor(
            createdAt: 100,
            lastAccessedAt: null,
            now + 365 * 86400,
            halfLife
        );
        Assert.True(factor < 0.01, "一年前的记忆衰退因子应接近 0");
    }

    // ── UsefulnessFactor ──

    [Fact]
    public void UsefulnessFactor_Zero_ReturnsHalf()
    {
        var factor = AgentMemoryScoring.UsefulnessFactor(0.0);
        Assert.Equal(0.5, factor, 6);
    }

    [Fact]
    public void UsefulnessFactor_Positive_ApproachesOne()
    {
        var factor = AgentMemoryScoring.UsefulnessFactor(5.0);
        Assert.True(factor > 0.9, "大正数应接近 1");
    }

    [Fact]
    public void UsefulnessFactor_Negative_ApproachesZero()
    {
        var factor = AgentMemoryScoring.UsefulnessFactor(-5.0);
        Assert.True(factor < 0.1, "大负数应接近 0");
    }

    [Fact]
    public void UsefulnessFactor_Symmetric()
    {
        // sigmoid(-x) = 1 - sigmoid(x)
        var pos = AgentMemoryScoring.UsefulnessFactor(2.0);
        var neg = AgentMemoryScoring.UsefulnessFactor(-2.0);
        Assert.Equal(1.0, pos + neg, 6);
    }

    [Fact]
    public void UsefulnessFactor_Monotonic()
    {
        var f1 = AgentMemoryScoring.UsefulnessFactor(-2.0);
        var f2 = AgentMemoryScoring.UsefulnessFactor(-1.0);
        var f3 = AgentMemoryScoring.UsefulnessFactor(0.0);
        var f4 = AgentMemoryScoring.UsefulnessFactor(1.0);
        var f5 = AgentMemoryScoring.UsefulnessFactor(2.0);
        Assert.True(f1 < f2 && f2 < f3 && f3 < f4 && f4 < f5);
    }

    // ── CosineSimilarity ──

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var a = new float[] { 1, 2, 3 };
        var sim = AgentMemoryScoring.CosineSimilarity(a, a);
        Assert.Equal(1.0, sim, 6);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1, 0, 0 };
        var b = new float[] { 0, 1, 0 };
        var sim = AgentMemoryScoring.CosineSimilarity(a, b);
        Assert.Equal(0.0, sim, 6);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegative()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { -1, 0 };
        var sim = AgentMemoryScoring.CosineSimilarity(a, b);
        Assert.Equal(-1.0, sim, 6);
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_ReturnsZero()
    {
        var a = new float[] { 0, 0, 0 };
        var b = new float[] { 1, 2, 3 };
        var sim = AgentMemoryScoring.CosineSimilarity(a, b);
        Assert.Equal(0.0, sim, 6);
    }

    [Fact]
    public void CosineSimilarity_DifferentLength_ReturnsZero()
    {
        var a = new float[] { 1, 2 };
        var b = new float[] { 1, 2, 3 };
        var sim = AgentMemoryScoring.CosineSimilarity(a, b);
        Assert.Equal(0.0, sim, 6);
    }

    [Fact]
    public void CosineSimilarity_EmptyVectors_ReturnsZero()
    {
        var sim = AgentMemoryScoring.CosineSimilarity([], []);
        Assert.Equal(0.0, sim, 6);
    }

    [Fact]
    public void CosineSimilarity_PartialMatch_ReturnsBetweenZeroAndOne()
    {
        var a = new float[] { 1, 2, 3 };
        var b = new float[] { 1, 2, 0 };
        var sim = AgentMemoryScoring.CosineSimilarity(a, b);
        Assert.InRange(sim, 0.0, 1.0);
    }

    // ── HybridSimilarity ──

    [Fact]
    public void HybridSimilarity_DefaultWeight_CombinesCorrectly()
    {
        // 0.65 * 0.8 + 0.35 * 0.6 = 0.52 + 0.21 = 0.73
        var hybrid = AgentMemoryScoring.HybridSimilarity(0.8, 0.6, 0.35);
        Assert.Equal(0.73, hybrid, 6);
    }

    [Fact]
    public void HybridSimilarity_KeywordWeightZero_ReturnsPureCosine()
    {
        var hybrid = AgentMemoryScoring.HybridSimilarity(0.8, 0.0, 0.0);
        Assert.Equal(0.8, hybrid, 6);
    }

    [Fact]
    public void HybridSimilarity_KeywordWeightOne_ReturnsPureBm25()
    {
        var hybrid = AgentMemoryScoring.HybridSimilarity(0.0, 0.7, 1.0);
        Assert.Equal(0.7, hybrid, 6);
    }

    // ── CompositeScore ──

    [Fact]
    public void CompositeScore_ExactCalculation_WithDefaultConfig()
    {
        var now = 1000.0;
        var config = new AgentMemoryScoringConfig
        {
            WeightSimilarity = 0.65,
            WeightRecency = 0.15,
            WeightImportance = 0.10,
            WeightUsefulness = 0.10,
            HalfLifeSeconds = 100.0,
        };

        var mem = new MemoryRecord
        {
            Id = "test-1",
            OwnerId = "user-1",
            Content = "test content",
            Importance = 0.8,
            CreatedAt = 900.0, // age = 100
            LastAccessedAt = null, // 用 created_at
            Usefulness = 2.0, // sigmoid(2) ≈ 0.8808
        };

        var similarity = 0.75;
        var (score, recency) = AgentMemoryScoring.CompositeScore(similarity, mem, config, now);

        // recency = exp(-ln2 * 100 / 100) = 0.5
        Assert.Equal(0.5, recency, 4);

        // usefulness = 1/(1+exp(-2)) ≈ 0.8808
        var usefulness = AgentMemoryScoring.UsefulnessFactor(2.0);

        // weights normalized: 0.65, 0.15, 0.10, 0.10 (sum = 1)
        // score = 0.65*0.75 + 0.15*0.5 + 0.10*0.8 + 0.10*0.8808
        //       = 0.4875 + 0.075 + 0.08 + 0.08808
        //       = 0.73058
        var expected = 0.65 * 0.75 + 0.15 * 0.5 + 0.10 * 0.8 + 0.10 * usefulness;
        Assert.Equal(expected, score, 4);
    }

    [Fact]
    public void CompositeScore_ZeroSimilarity_StillScored()
    {
        // 注意：硬规则在调用方（RecallAsync）中实施，composite_score 本身不拒绝 0 similarity
        var now = 1000.0;
        var config = new AgentMemoryScoringConfig();
        var mem = new MemoryRecord
        {
            Id = "test-1",
            OwnerId = "user-1",
            Content = "test",
            Importance = 0.5,
            CreatedAt = 900.0,
            Usefulness = 0.0,
        };

        var (score, _) = AgentMemoryScoring.CompositeScore(0.0, mem, config, now);
        // 即使 similarity=0，recency/importance/usefulness 仍贡献分数
        Assert.True(score > 0);
    }

    [Fact]
    public void CompositeScore_FrozenTime_Deterministic()
    {
        var now = 5000.0;
        var config = new AgentMemoryScoringConfig();
        var mem = new MemoryRecord
        {
            Id = "test-1",
            OwnerId = "user-1",
            Content = "frozen",
            Importance = 0.3,
            CreatedAt = 4000.0,
            LastAccessedAt = 4500.0,
            Usefulness = 1.0,
        };

        var (score1, _) = AgentMemoryScoring.CompositeScore(0.5, mem, config, now);
        var (score2, _) = AgentMemoryScoring.CompositeScore(0.5, mem, config, now);
        Assert.Equal(score1, score2, 6);
    }

    [Fact]
    public void CompositeScore_HigherImportance_HigherScore()
    {
        var now = 1000.0;
        var config = new AgentMemoryScoringConfig();

        var baseMem = new MemoryRecord
        {
            Id = "base",
            OwnerId = "u",
            Content = "c",
            Importance = 0.3,
            CreatedAt = 900,
            Usefulness = 0,
        };
        var importantMem = baseMem with { Id = "imp", Importance = 0.9 };

        var (scoreBase, _) = AgentMemoryScoring.CompositeScore(0.5, baseMem, config, now);
        var (scoreImp, _) = AgentMemoryScoring.CompositeScore(0.5, importantMem, config, now);
        Assert.True(scoreImp > scoreBase, "高重要性的记忆应获得更高评分");
    }

    [Fact]
    public void CompositeScore_UsefulMemory_OutranksNeutral()
    {
        var now = 1000.0;
        var config = new AgentMemoryScoringConfig();

        var neutral = new MemoryRecord
        {
            Id = "neu",
            OwnerId = "u",
            Content = "c",
            Importance = 0.5,
            CreatedAt = 900,
            Usefulness = 0.0,
        };
        var useful = neutral with { Id = "use", Usefulness = 3.0 };

        var (scoreNeu, _) = AgentMemoryScoring.CompositeScore(0.5, neutral, config, now);
        var (scoreUse, _) = AgentMemoryScoring.CompositeScore(0.5, useful, config, now);
        Assert.True(scoreUse > scoreNeu, "有用性高的记忆应获得更高评分");
    }

    [Fact]
    public void CompositeScore_RecentlyAccessed_OutranksOld()
    {
        var now = 1000.0;
        var config = new AgentMemoryScoringConfig { HalfLifeSeconds = 100.0 };

        var oldMem = new MemoryRecord
        {
            Id = "old",
            OwnerId = "u",
            Content = "c",
            Importance = 0.5,
            CreatedAt = 100,
            LastAccessedAt = 100,
            Usefulness = 0,
        };
        var recentMem = oldMem with { Id = "recent", LastAccessedAt = 950 };

        var (scoreOld, _) = AgentMemoryScoring.CompositeScore(0.5, oldMem, config, now);
        var (scoreRecent, _) = AgentMemoryScoring.CompositeScore(0.5, recentMem, config, now);
        Assert.True(scoreRecent > scoreOld, "最近访问的记忆应获得更高评分");
    }
}
