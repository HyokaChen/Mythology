using Core.Data;
using Core.Inference;
using Core.Shared;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// scan_relationships（jevgraph 提炼）：管线（全实体提及/同句封锁/本体域值域门/去重/稳定 ID/证据 span）
/// 与工具闸门（none→拒、uncertain/越界/低概率/低置信/缺答案→review、既有关系对排除、不写库、回执）。
/// </summary>
public sealed class RelationshipScanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ══════════════════════════════════════════════════════
    //  管线（RelationshipScanPipeline.Find）
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Pipeline_FindsSameSentencePair_WithStableIdAndEvidence()
    {
        var chapters = new[] { MakeChapter("chap1", 1, "林寒把剑递还给苏晚，说：这次换我等你。") };
        var catalog = Characters(("ca", "林寒"), ("cb", "苏晚"));

        var first = RelationshipScanPipeline.Find(
            "n1",
            chapters,
            catalog,
            NoPairs(),
            maxNeighbors: 8,
            maxCandidates: 24
        );
        var second = RelationshipScanPipeline.Find(
            "n1",
            chapters,
            catalog,
            NoPairs(),
            maxNeighbors: 8,
            maxCandidates: 24
        );

        var candidate = Assert.Single(first);
        Assert.Equal("ca", candidate.SourceEntityId); // A = 句内先出现者
        Assert.Equal("cb", candidate.TargetEntityId);
        Assert.Equal(EntityType.Character, candidate.SourceEntityType);
        Assert.Equal("林寒把剑递还给苏晚，说：这次换我等你。", candidate.EvidenceText);
        Assert.Equal(1, candidate.ChapterOrder);
        Assert.Equal(candidate.CandidateId, Assert.Single(second).CandidateId); // 稳定 ID
    }

    [Fact]
    public void Pipeline_SkipsExistingPairs()
    {
        var result = RelationshipScanPipeline.Find(
            "n1",
            [MakeChapter("chap1", 1, "林寒看着苏晚。")],
            Characters(("ca", "林寒"), ("cb", "苏晚")),
            CharacterPairs(("ca", "cb")),
            maxNeighbors: 8,
            maxCandidates: 24
        );

        Assert.Empty(result);
    }

    [Fact]
    public void Pipeline_DedupesPairToNearestOccurrence()
    {
        // 同对出现在两句：只保留距离最近的一次（第 1 句距离 4 < 第 2 句距离 10）
        var result = RelationshipScanPipeline.Find(
            "n1",
            [MakeChapter("chap1", 1, "林寒看着苏晚。苏晚回头，看向远处的林寒。")],
            Characters(("ca", "林寒"), ("cb", "苏晚")),
            NoPairs(),
            maxNeighbors: 8,
            maxCandidates: 24
        );

        var candidate = Assert.Single(result);
        Assert.Equal("林寒看着苏晚。", candidate.EvidenceText);
    }

    [Fact]
    public void Pipeline_SkipsSingleCharNames()
    {
        var result = RelationshipScanPipeline.Find(
            "n1",
            [MakeChapter("chap1", 1, "林寒与苏晚同游。")],
            Characters(("ca", "林"), ("cb", "苏晚")),
            NoPairs(),
            maxNeighbors: 8,
            maxCandidates: 24
        );

        Assert.Empty(result); // 单字名跳过 → 只剩 1 个提及
    }

    [Fact]
    public void Pipeline_LongestNameOccupiesSpan()
    {
        var result = RelationshipScanPipeline.Find(
            "n1",
            [MakeChapter("chap1", 1, "李寒山走了过来。")],
            Characters(("ca", "李寒山"), ("cb", "李寒")),
            NoPairs(),
            maxNeighbors: 8,
            maxCandidates: 24
        );

        Assert.Empty(result); // 「李寒」被「李寒山」占用区间挡住
    }

    [Fact]
    public void Pipeline_NonAllowedTypePair_IsBlockedByOntology()
    {
        // 角色 × 地点：叙事本体没有允许该类型对的关系 → 不成候选（域/值域门）
        var catalog = new List<CatalogEntry>
        {
            new("ca", EntityType.Character, "林寒"),
            new("loc1", EntityType.Location, "后山"),
        };

        var result = RelationshipScanPipeline.Find(
            "n1",
            [MakeChapter("chap1", 1, "林寒走进了后山。")],
            catalog,
            NoPairs(),
            maxNeighbors: 8,
            maxCandidates: 24
        );

        Assert.Empty(result);
    }

    [Fact]
    public void Pipeline_NonCharacterEntityOccupiesSpan()
    {
        // 组织「青云宗」占住区间 → 角色「青云」不再命中该子串，只剩 1 个提及 → 无候选
        var catalog = new List<CatalogEntry>
        {
            new("ca", EntityType.Character, "林寒"),
            new("cb", EntityType.Character, "青云"),
            new("f1", EntityType.Faction, "青云宗"),
        };

        var result = RelationshipScanPipeline.Find(
            "n1",
            [MakeChapter("chap1", 1, "林寒加入了青云宗。")],
            catalog,
            NoPairs(),
            maxNeighbors: 8,
            maxCandidates: 24
        );

        Assert.Empty(result);
    }

    // ══════════════════════════════════════════════════════
    //  工具（RelationshipScanTool）
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task Tool_JudgeDisabled_ReturnsError()
    {
        var store = await SeedAsync("林寒看着苏晚。", ["林寒", "苏晚"]);
        var tool = new RelationshipScanTool(store, systemOne: null);

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        Assert.True(result.IsError);
        Assert.Contains("system-one.enabled", Text(result));
    }

    [Fact]
    public async Task Tool_ProposesRelationship_WithEvidence()
    {
        var store = await SeedAsync("林寒把剑递还给苏晚，说：这次换我等你。", ["林寒", "苏晚"]);
        var judge = new StubJudge([Choice("lover", 0.86)]);
        var tool = new RelationshipScanTool(store, judge);

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        Assert.False(result.IsError);
        var details = result.TypedDetails!;
        Assert.Equal(1, details.ProposedCount);
        Assert.Equal(0, details.ReviewCount);
        Assert.Equal(0, details.RejectedCount);
        var text = Text(result);
        Assert.Contains("【建议】(1)", text);
        Assert.Contains("lover", text);
        Assert.Contains("p=0.86", text);
        Assert.Contains("林寒把剑递还给苏晚", text);
        Assert.Equal(1, judge.CallCount);
    }

    [Fact]
    public async Task Tool_NoneVerdict_Rejected()
    {
        var store = await SeedAsync("林寒看着苏晚。", ["林寒", "苏晚"]);
        var tool = new RelationshipScanTool(store, new StubJudge([Choice("none", 0.95)]));

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        var details = result.TypedDetails!;
        Assert.Equal(0, details.ProposedCount);
        Assert.Equal(0, details.ReviewCount);
        Assert.Equal(1, details.RejectedCount);
        var text = Text(result);
        Assert.DoesNotContain("【建议】", text);
        Assert.DoesNotContain("【待确认】", text);
    }

    [Fact]
    public async Task Tool_BelowThreshold_GoesToReview()
    {
        var store = await SeedAsync("林寒看着苏晚。", ["林寒", "苏晚"]);
        var tool = new RelationshipScanTool(store, new StubJudge([Choice("lover", 0.5)]));

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        var details = result.TypedDetails!;
        Assert.Equal(0, details.ProposedCount);
        Assert.Equal(1, details.ReviewCount);
        Assert.Contains("【待确认】(1)", Text(result));
        Assert.Contains("below_probability_threshold", Text(result));
    }

    [Fact]
    public async Task Tool_LowConfidence_GoesToReviewWithReason()
    {
        // 标签概率高（0.9 ≥ 0.7）但整体置信度低（0.5 < 0.8）→ 第二个门拦下 → review
        var store = await SeedAsync("林寒看着苏晚。", ["林寒", "苏晚"]);
        var answer = new SystemOneAnswer
        {
            Type = "choice",
            Choice = "lover",
            Probabilities = new Dictionary<string, double> { ["lover"] = 0.9 },
            Confidence = 0.5,
        };
        var tool = new RelationshipScanTool(store, new StubJudge([answer]));

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        var details = result.TypedDetails!;
        Assert.Equal(0, details.ProposedCount);
        Assert.Equal(1, details.ReviewCount);
        Assert.Contains("below_confidence_threshold", Text(result));
    }

    [Fact]
    public async Task Tool_OutOfSchemaToken_GoesToReviewWithReason()
    {
        // 判定模型给出闭集外的 token → 本体/判据校验失败 → review（不冒充提案）
        var store = await SeedAsync("林寒看着苏晚。", ["林寒", "苏晚"]);
        var tool = new RelationshipScanTool(store, new StubJudge([Choice("knight", 0.95)]));

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        var details = result.TypedDetails!;
        Assert.Equal(0, details.ProposedCount);
        Assert.Equal(1, details.ReviewCount);
        Assert.Contains("relation_failed_schema_check", Text(result));
    }

    [Fact]
    public async Task Tool_MissingAnswer_GoesToReviewNotRejected()
    {
        var store = await SeedAsync("林寒看着苏晚。", ["林寒", "苏晚"]);
        var tool = new RelationshipScanTool(store, new StubJudge([null]));

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        var details = result.TypedDetails!;
        Assert.Equal(0, details.ProposedCount);
        Assert.Equal(1, details.ReviewCount); // 未知 ≠ 拒绝
        Assert.Equal(0, details.RejectedCount);
        Assert.Contains("missing_answer", Text(result));
    }

    [Fact]
    public async Task Tool_ExistingPair_ExcludedWithoutJudgeCall()
    {
        var store = await SeedAsync(
            "林寒看着苏晚。",
            ["林寒", "苏晚"],
            [
                new Relationship
                {
                    Id = "r1",
                    NovelId = "n1",
                    CharacterAId = "ch0",
                    CharacterBId = "ch1",
                    Type = RelationshipType.Friend,
                },
            ]
        );
        var judge = new StubJudge([Choice("lover", 0.9)]);
        var tool = new RelationshipScanTool(store, judge);

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        Assert.Equal(0, judge.CallCount);
        Assert.Contains("未发现新的关系候选", Text(result));
    }

    [Fact]
    public async Task Tool_EmitsReceiptPerBatch()
    {
        var store = await SeedAsync("林寒看着苏晚。", ["林寒", "苏晚"]);
        var tool = new RelationshipScanTool(store, new StubJudge([Choice("ally", 0.9)]));

        var result = await tool.ExecuteAsync(
            "call",
            new RelationshipScanParams { NovelId = "n1" },
            Ct
        );

        var receipt = Assert.Single(result.TypedDetails!.Receipts);
        Assert.True(receipt.Ok);
        Assert.Equal(1, receipt.Questions);
        Assert.Equal("stub", receipt.Model);
        Assert.Contains("判定批次 1", Text(result));
    }

    // ══════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════

    private static Chapter MakeChapter(string id, int order, string content) =>
        new()
        {
            Id = id,
            NovelId = "n1",
            Order = order,
            Title = $"第{order}章",
            Content = content,
        };

    private static Character MakeCharacter(string id, string name) =>
        new()
        {
            Id = id,
            NovelId = "n1",
            Name = name,
        };

    private static List<CatalogEntry> Characters(params (string Id, string Name)[] entries) =>
        [.. entries.Select(entry => new CatalogEntry(entry.Id, EntityType.Character, entry.Name))];

    private static IReadOnlySet<(EntityRef A, EntityRef B)> NoPairs() =>
        new HashSet<(EntityRef A, EntityRef B)>();

    private static IReadOnlySet<(EntityRef A, EntityRef B)> CharacterPairs(
        params (string A, string B)[] pairs
    ) =>
        pairs
            .Select(pair =>
                RelationshipScanPipeline.PairKey(
                    new EntityRef(EntityType.Character, pair.A),
                    new EntityRef(EntityType.Character, pair.B)
                )
            )
            .ToHashSet();

    private static async Task<FakeNovelStore> SeedAsync(
        string chapterContent,
        string[] characterNames,
        List<Relationship>? relationships = null
    )
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "测试小说" });
        for (var i = 0; i < characterNames.Length; i++)
        {
            await store.SaveCharacter(
                new Character
                {
                    Id = $"ch{i}",
                    NovelId = "n1",
                    Name = characterNames[i],
                }
            );
        }
        await store.SaveChapter(
            new Chapter
            {
                Id = "chap1",
                NovelId = "n1",
                Order = 1,
                Title = "第一章",
                Content = chapterContent,
            }
        );
        if (relationships is not null)
        {
            foreach (var relationship in relationships)
                await store.SaveRelationship(relationship);
        }
        return store;
    }

    private static string Text(AgentToolResult<RelationshipScanDetails> result) =>
        (result.Content[0] as TextContent)?.Text ?? "";

    private static SystemOneAnswer Choice(string selected, double probability) =>
        new()
        {
            Type = "choice",
            Choice = selected,
            Probabilities = new Dictionary<string, double> { [selected] = probability },
            Confidence = probability,
        };

    /// <summary>判定桩：按题序发放预设答案；用尽/为 null 表示该题无答案（未知）。</summary>
    private sealed class StubJudge(IEnumerable<SystemOneAnswer?> answers, bool enabled = true)
        : ISystemOneClient
    {
        private readonly Queue<SystemOneAnswer?> _answers = new(answers);

        public int CallCount { get; private set; }

        public bool Enabled => Options.Enabled;

        public SystemOneConfig Options { get; } =
            new()
            {
                Enabled = enabled,
                Relations = new SystemOneRelationConfig { Enabled = true },
            };

        public Task<SystemOneResponse?> AskAsync(
            object state,
            IReadOnlyDictionary<string, SystemOneQuestion> questions,
            CancellationToken ct = default
        )
        {
            CallCount++;
            var map = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
            foreach (var key in questions.Keys)
            {
                if (_answers.Count == 0)
                    break;
                var answer = _answers.Dequeue();
                if (answer is not null)
                    map[key] = answer;
            }
            return Task.FromResult<SystemOneResponse?>(
                new SystemOneResponse { Model = "stub", Answers = map }
            );
        }

        public void UpdateConfig(SystemOneConfig options)
        {
            // 测试桩忽略热更新
        }
    }
}
