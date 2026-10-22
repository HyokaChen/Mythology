using Core.Data;
using Core.Inference;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 记忆冲突本地判定（阶段 3）：
/// 四分类裁定（contradiction 优先、高置信 unrelated 丢弃、低于阈值保留）、
/// 判定失败 fail-open（原样返回）、AgentMemoryStore.FindConflictsAsync 接线（禁用零调用）。
/// </summary>
public sealed class ConflictAdjudicationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<string> _dbPaths = [];

    public void Dispose()
    {
        foreach (var path in _dbPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 测试清理尽力而为
            }
        }
    }

    // ══════════════════════════════════════════════════════
    //  AdjudicateAsync 直接语义
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task AdjudicateAsync_DropsHighConfidenceUnrelated_ContradictionFirst()
    {
        var conflicts = new List<Conflict>
        {
            MakeConflict("主角怕蜘蛛", "主角不怕蜘蛛"),
            MakeConflict("偏好短句", "对话尽量不超过十五字"),
            MakeConflict("收藏古董", "喜欢下棋"),
        };
        var judge = new StubChoiceJudge(key =>
            key switch
            {
                "c00" => Choice("contradiction", 0.9),
                "c01" => Choice("refinement", 0.8),
                "c02" => Choice("unrelated", 0.9),
                _ => null,
            }
        );

        var result = await AgentMemoryConflict.AdjudicateAsync(judge, conflicts, Ct);

        Assert.Equal(2, result.Count); // c02 高置信误报 → 丢弃
        Assert.Equal("contradiction", result[0].JudgeVerdict);
        Assert.Equal(0.9, result[0].JudgeProbability!.Value, 3);
        Assert.Equal("refinement", result[1].JudgeVerdict);
        Assert.Equal(0.8, result[1].JudgeProbability!.Value, 3);
        Assert.Equal(1, judge.CallCount);
    }

    [Fact]
    public async Task AdjudicateAsync_UnrelatedBelowThreshold_Kept()
    {
        var conflicts = new List<Conflict> { MakeConflict("甲记忆", "乙记忆") };
        var judge = new StubChoiceJudge(_ => Choice("unrelated", 0.5));

        var result = await AgentMemoryConflict.AdjudicateAsync(judge, conflicts, Ct);

        Assert.Single(result); // 低于丢弃阈值 → 保留（只标注，不删）
        Assert.Equal("unrelated", result[0].JudgeVerdict);
        Assert.Equal(0.5, result[0].JudgeProbability!.Value, 3);
    }

    [Fact]
    public async Task AdjudicateAsync_Failure_ReturnsHeuristicListUnchanged()
    {
        var conflicts = new List<Conflict>
        {
            MakeConflict("甲记忆", "乙记忆"),
            MakeConflict("丙记忆", "丁记忆"),
        };
        var judge = new StubChoiceJudge(_ => Choice("contradiction", 0.9), fail: true);

        var result = await AgentMemoryConflict.AdjudicateAsync(judge, conflicts, Ct);

        Assert.Equal(
            conflicts.Select(c => c.MemoryA.Id).ToArray(),
            result.Select(c => c.MemoryA.Id).ToArray()
        );
        Assert.All(result, c => Assert.Null(c.JudgeVerdict));
    }

    // ══════════════════════════════════════════════════════
    //  AgentMemoryStore.FindConflictsAsync 接线
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task FindConflictsAsync_WithJudge_AppliesAdjudication()
    {
        var judge = new StubChoiceJudge(_ => Choice("contradiction", 0.9));
        await using var store = NewStore(judge);
        await store.InitializeAsync(Ct);
        await store.StoreAsync("主角怕蜘蛛", "local", novelId: "novel-x", ct: Ct);
        await store.StoreAsync("主角不怕蜘蛛", "local", novelId: "novel-x", ct: Ct);

        var conflicts = await store.FindConflictsAsync("蜘蛛", "local", novelId: "novel-x", ct: Ct);

        Assert.NotEmpty(conflicts);
        Assert.All(conflicts, c => Assert.Equal("contradiction", c.JudgeVerdict));
        Assert.True(judge.CallCount >= 1);
    }

    [Fact]
    public async Task FindConflictsAsync_JudgeDisabled_KeepsHeuristicsOnly()
    {
        var judge = new StubChoiceJudge(_ => Choice("unrelated", 0.9), enabled: false);
        await using var store = NewStore(judge);
        await store.InitializeAsync(Ct);
        await store.StoreAsync("主角怕蜘蛛", "local", novelId: "novel-x", ct: Ct);
        await store.StoreAsync("主角不怕蜘蛛", "local", novelId: "novel-x", ct: Ct);

        var conflicts = await store.FindConflictsAsync("蜘蛛", "local", novelId: "novel-x", ct: Ct);

        Assert.NotEmpty(conflicts); // 启发式结果照常返回
        Assert.All(conflicts, c => Assert.Null(c.JudgeVerdict));
        Assert.Equal(0, judge.CallCount);
    }

    // ══════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════

    private AgentMemoryStore NewStore(ISystemOneClient? judge)
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_conflict_{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return new AgentMemoryStore(path, dimension: 128, systemOne: judge);
    }

    private static Conflict MakeConflict(string contentA, string contentB) =>
        new()
        {
            Confidence = "likely",
            MemoryA = MakeMemory(contentA),
            MemoryB = MakeMemory(contentB),
        };

    private static MemoryRecord MakeMemory(string content) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = "local",
            Content = content,
        };

    private static SystemOneAnswer Choice(string selected, double probability) =>
        new()
        {
            Type = "choice",
            Choice = selected,
            Probabilities = new Dictionary<string, double> { [selected] = probability },
            Confidence = probability,
        };

    /// <summary>判定桩：按题号给 choice 答案；fail=true 模拟服务不可用（返回 null）。</summary>
    private sealed class StubChoiceJudge(
        Func<string, SystemOneAnswer?> answer,
        bool enabled = true,
        bool conflictsEnabled = true,
        bool fail = false
    ) : ISystemOneClient
    {
        private int _callCount;

        public int CallCount => _callCount;

        public bool Enabled => Options.Enabled;

        public SystemOneConfig Options { get; } =
            new()
            {
                Enabled = enabled,
                Conflicts = new SystemOneConflictConfig { Enabled = conflictsEnabled },
            };

        public Task<SystemOneResponse?> AskAsync(
            object state,
            IReadOnlyDictionary<string, SystemOneQuestion> questions,
            CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _callCount);
            if (fail)
                return Task.FromResult<SystemOneResponse?>(null);

            var answers = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
            foreach (var key in questions.Keys)
            {
                if (answer(key) is { } value)
                    answers[key] = value;
            }
            return Task.FromResult<SystemOneResponse?>(
                new SystemOneResponse { Model = "stub", Answers = answers }
            );
        }

        public void UpdateConfig(SystemOneConfig options)
        {
            // 测试桩忽略热更新
        }
    }
}
