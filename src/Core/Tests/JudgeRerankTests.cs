using Core.Data;
using Core.Inference;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// JudgeRerank 判定重排语义（jegrep 提炼）与 RetrievalKernel 接线：
/// 融合公式、缺答案保持原分、失败整体中止（原序）、适用范围零调用、
/// overfetch→Take(FinalTopK)、零向量纯词法分支仍判定、UserMemory 域不判定。
/// </summary>
public sealed class JudgeRerankTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ══════════════════════════════════════════════════════
    //  直接语义（JudgeRerank.ApplyAsync）
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyAsync_BlendsScoresAndReorders()
    {
        var stub = new StubJudge(key =>
            key switch
            {
                "p00" => 0.9,
                "p01" => 0.1,
                _ => null,
            }
        );
        var results = new List<RetrievalSearchResult> { Result("a", 0.02), Result("b", 0.03) };

        var outcome = await JudgeRerank.ApplyAsync(stub, "查询意图", results, Ct);

        Assert.False(outcome.Aborted);
        Assert.Equal(2, outcome.Judged);
        Assert.Equal("a", outcome.Results[0].ChunkId); // p=0.9 抬升后反超
        Assert.Equal(0.9, outcome.Results[0].JudgeProbability!.Value, 3);
        // final = score × (1 + w×(2p−1))，w=0.5：0.02×1.4 反超 0.03×0.6
        Assert.Equal(0.02 * 1.4, outcome.Results[0].Score, 6);
        Assert.Equal(0.03 * 0.6, outcome.Results[1].Score, 6);
    }

    [Fact]
    public async Task ApplyAsync_MissingAnswer_KeepsOriginalScore()
    {
        var stub = new StubJudge(key => key == "p00" ? 0.9 : null);
        var results = new List<RetrievalSearchResult>
        {
            Result("a", 0.02),
            Result("b", 0.03),
            Result("c", 0.04),
        };

        var outcome = await JudgeRerank.ApplyAsync(stub, "查询意图", results, Ct);

        Assert.Equal(1, outcome.Judged);
        var b = outcome.Results.Single(r => r.ChunkId == "b");
        Assert.Equal(0.03, b.Score, 6); // 缺答案 → 原分（失败≠负判）
        Assert.Null(b.JudgeProbability);
        var c = outcome.Results.Single(r => r.ChunkId == "c");
        Assert.Equal(0.04, c.Score, 6);
        Assert.Null(c.JudgeProbability);
    }

    [Fact]
    public async Task ApplyAsync_RequestFailure_AbortsAndKeepsOriginalList()
    {
        var stub = new StubJudge(_ => 0.9, fail: true);
        var results = new List<RetrievalSearchResult>
        {
            Result("a", 0.02),
            Result("b", 0.03),
            Result("c", 0.01),
        };

        var outcome = await JudgeRerank.ApplyAsync(stub, "查询意图", results, Ct);

        Assert.True(outcome.Aborted);
        Assert.Equal(0, outcome.Judged);
        Assert.Equal(new[] { "a", "b", "c" }, outcome.Results.Select(r => r.ChunkId).ToArray());
        Assert.All(outcome.Results, r => Assert.Null(r.JudgeProbability));
    }

    // ══════════════════════════════════════════════════════
    //  适用范围（Applies）
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Applies_NullClient_False() => Assert.False(JudgeRerank.Applies(null, Request()));

    [Fact]
    public void Applies_DisabledClient_False() =>
        Assert.False(JudgeRerank.Applies(new StubJudge(_ => 0.5, enabled: false), Request()));

    [Fact]
    public void Applies_RerankDisabled_False() =>
        Assert.False(JudgeRerank.Applies(new StubJudge(_ => 0.5, rerankEnabled: false), Request()));

    [Fact]
    public void Applies_UserMemoryDomain_False() =>
        Assert.False(
            JudgeRerank.Applies(
                new StubJudge(_ => 0.5),
                Request(domain: RetrievalDomain.UserMemory)
            )
        );

    [Fact]
    public void Applies_EmptyQuery_False() =>
        Assert.False(JudgeRerank.Applies(new StubJudge(_ => 0.5), Request(query: "  ")));

    [Fact]
    public void Applies_Defaults_True() =>
        Assert.True(JudgeRerank.Applies(new StubJudge(_ => 0.5), Request()));

    // ══════════════════════════════════════════════════════
    //  RetrievalKernel 接线
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task Kernel_WithJudge_SetsProbabilityAndTrimsToFinalTopK()
    {
        var store = new InMemoryRetrievalStore();
        var stub = new StubJudge(_ => 0.9);
        var kernel = new RetrievalKernel(store, new FakeEmbeddingService(), systemOne: stub);
        await SeedAsync(kernel, "c1", "第一段文本");
        await SeedAsync(kernel, "c2", "第二段文本");
        await SeedAsync(kernel, "c3", "第三段文本");

        var results = await kernel.SearchAsync(Request(finalTopK: 2), Ct);

        Assert.NotEmpty(results);
        Assert.True(results.Count <= 2); // 放大召回后仍回落到 FinalTopK
        Assert.All(results, r => Assert.NotNull(r.JudgeProbability));
        Assert.Equal(1, stub.CallCount); // 3 条候选一次请求装下
    }

    [Fact]
    public async Task Kernel_JudgeDisabled_MakesNoRequests()
    {
        var store = new InMemoryRetrievalStore();
        var stub = new StubJudge(_ => 0.9, enabled: false);
        var kernel = new RetrievalKernel(store, new FakeEmbeddingService(), systemOne: stub);
        await SeedAsync(kernel, "c1", "第一段文本");

        var results = await kernel.SearchAsync(Request(finalTopK: 2), Ct);

        Assert.NotEmpty(results);
        Assert.Equal(0, stub.CallCount);
        Assert.All(results, r => Assert.Null(r.JudgeProbability));
    }

    [Fact]
    public async Task Kernel_UserMemoryDomain_MakesNoRequests()
    {
        var store = new InMemoryRetrievalStore();
        var stub = new StubJudge(_ => 0.9);
        var kernel = new RetrievalKernel(store, new FakeEmbeddingService(), systemOne: stub);
        await SeedAsync(
            kernel,
            "u1",
            "用户规则文本",
            RetrievalDomain.UserMemory,
            DocumentKind.UserRule
        );

        var results = await kernel.SearchAsync(Request(domain: RetrievalDomain.UserMemory), Ct);

        Assert.NotEmpty(results); // 召回正常
        Assert.Equal(0, stub.CallCount); // 判定绝不参与用户记忆域
    }

    [Fact]
    public async Task Kernel_ZeroVectorQuery_StillJudges()
    {
        var store = new InMemoryRetrievalStore();
        var stub = new StubJudge(_ => 0.8);
        var kernel = new RetrievalKernel(
            store,
            new QueryZeroEmbeddingService("查询意图"),
            systemOne: stub
        );
        await SeedAsync(kernel, "c1", "第一段文本");
        await SeedAsync(kernel, "c2", "第二段文本");

        var results = await kernel.SearchAsync(Request(finalTopK: 2), Ct);

        Assert.True(stub.CallCount >= 1); // 零向量降级链上仍走判定
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task Kernel_JudgeFailure_KeepsOrderAndDoesNotThrow()
    {
        var store = new InMemoryRetrievalStore();
        var failing = new StubJudge(_ => 0.9, fail: true);
        var kernel = new RetrievalKernel(store, new FakeEmbeddingService(), systemOne: failing);
        await SeedAsync(kernel, "c1", "甲文本");
        await SeedAsync(kernel, "c2", "乙文本");
        await SeedAsync(kernel, "c3", "丙文本");

        var withFailure = await kernel.SearchAsync(Request(finalTopK: 3), Ct);

        var disabledKernel = new RetrievalKernel(
            store,
            new FakeEmbeddingService(),
            systemOne: new StubJudge(_ => 0.9, enabled: false)
        );
        var withoutJudge = await disabledKernel.SearchAsync(Request(finalTopK: 3), Ct);

        Assert.Equal(
            withoutJudge.Select(r => r.ChunkId).ToArray(),
            withFailure.Select(r => r.ChunkId).ToArray()
        );
        Assert.All(withFailure, r => Assert.Null(r.JudgeProbability));
    }

    // ══════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════

    private static RetrievalSearchRequest Request(
        string query = "查询意图",
        RetrievalDomain domain = RetrievalDomain.NovelKnowledge,
        int finalTopK = 10
    ) =>
        new()
        {
            Domain = domain,
            OwnerId = "local",
            NovelId = "n1",
            Query = query,
            FinalTopK = finalTopK,
        };

    private static RetrievalSearchResult Result(string chunkId, double score) =>
        new()
        {
            ChunkId = chunkId,
            DocumentId = "doc",
            Domain = RetrievalDomain.NovelKnowledge,
            DocumentKind = DocumentKind.ChapterBodyChunk,
            Text = "文本" + chunkId,
            Score = score,
        };

    private static async Task SeedAsync(
        RetrievalKernel kernel,
        string chunkId,
        string text,
        RetrievalDomain domain = RetrievalDomain.NovelKnowledge,
        DocumentKind kind = DocumentKind.ChapterBodyChunk
    )
    {
        var document = new RetrievalDocument
        {
            DocumentId = chunkId + "-doc",
            Domain = domain,
            OwnerId = "local",
            NovelId = "n1",
            DocumentKind = kind,
            SourceEntityType = "Chapter",
            SourceEntityId = chunkId,
            IndexStatus = IndexStatus.Pending,
        };
        var chunk = new RetrievalChunk
        {
            ChunkId = chunkId,
            DocumentId = document.DocumentId,
            ChunkOrdinal = 0,
            Text = text,
            ContentHash = "h-" + chunkId,
        };
        await kernel.IndexDocumentAsync(document, [chunk], Ct);
    }

    /// <summary>判定桩：按题号给 noul；fail=true 模拟服务不可用（返回 null）。</summary>
    private sealed class StubJudge : ISystemOneClient
    {
        private readonly Func<string, double?> _probability;
        private readonly bool _fail;
        private int _callCount;

        public StubJudge(
            Func<string, double?> probability,
            bool enabled = true,
            bool rerankEnabled = true,
            bool fail = false
        )
        {
            _probability = probability;
            _fail = fail;
            Options = new SystemOneConfig
            {
                Enabled = enabled,
                Rerank = new SystemOneRerankConfig { Enabled = rerankEnabled },
            };
        }

        public int CallCount => _callCount;

        public bool Enabled => Options.Enabled;

        public SystemOneConfig Options { get; private set; }

        public Task<SystemOneResponse?> AskAsync(
            object state,
            IReadOnlyDictionary<string, SystemOneQuestion> questions,
            CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _callCount);
            if (_fail)
                return Task.FromResult<SystemOneResponse?>(null);

            var answers = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
            foreach (var key in questions.Keys)
            {
                if (_probability(key) is { } value)
                    answers[key] = new SystemOneAnswer { Type = "noul", Noul = value };
            }
            return Task.FromResult<SystemOneResponse?>(
                new SystemOneResponse { Model = "stub", Answers = answers }
            );
        }

        public void UpdateConfig(SystemOneConfig options) => Options = options;
    }

    /// <summary>非零向量假 embedder（8 维）。</summary>
    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public int Dimension => 8;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[8];
            for (var i = 0; i < v.Length; i++)
                v[i] = text.Length + i + 1;
            return Task.FromResult(v);
        }
    }

    /// <summary>查询文本返回零向量、其余文本正常（触发零向量降级链）。</summary>
    private sealed class QueryZeroEmbeddingService(string zeroQueryText) : IEmbeddingService
    {
        public int Dimension => 8;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[8];
            if (!string.Equals(text, zeroQueryText, StringComparison.Ordinal))
            {
                for (var i = 0; i < v.Length; i++)
                    v[i] = text.Length + i + 1;
            }
            return Task.FromResult(v);
        }
    }
}
