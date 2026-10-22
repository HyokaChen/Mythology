using Core.Data;
using Core.Evolution;
using Xunit;

namespace Core.Tests;

/// <summary>本体 harness 策略：drift/stall/quality 信号与云端阻断。</summary>
[Collection("FileNovelStore")]
public sealed class OntologyHarnessPolicyTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly FileNovelStore _store;

    public OntologyHarnessPolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ontology-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileNovelStore(_root, Path.Combine(_root, ".mythology"));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
        return default;
    }

    [Fact]
    public async Task DeceasedCharacterInScene_IsCriticalAndBlocksCloud()
    {
        var (novel, chapter, scenes) = await SeedAsync();
        var lin = (await _store.GetCharacter(novel.Id, "lin"))!;
        lin.Status = CharacterStatus.Deceased;
        await _store.SaveCharacter(lin);
        novel = (await _store.GetNovel(novel.Id))!;

        var result = await EvaluateAsync(novel, chapter, scenes, []);

        Assert.Contains(
            result.Signals,
            s => s.Kind == HarnessSignalKind.Drift && s.Severity == HarnessSeverity.Critical
        );
        Assert.True(result.BlocksCloud);
        Assert.Contains("[本体 harness]", result.Render);
        Assert.Contains("风险：", result.Render);
        Assert.Contains("边界：", result.Render);
    }

    [Fact]
    public async Task GateCloudOff_DoesNotBlock()
    {
        var (novel, chapter, scenes) = await SeedAsync();
        var lin = (await _store.GetCharacter(novel.Id, "lin"))!;
        lin.Status = CharacterStatus.Deceased;
        await _store.SaveCharacter(lin);
        novel = (await _store.GetNovel(novel.Id))!;

        var result = await EvaluateAsync(novel, chapter, scenes, [], gateCloud: false);

        Assert.Contains(result.Signals, s => s.Severity == HarnessSeverity.Critical);
        Assert.False(result.BlocksCloud);
    }

    [Fact]
    public async Task ConstraintRuleTrigger_IsCritical()
    {
        var (novel, chapter, scenes) = await SeedAsync();
        scenes[0].PlannedDeltas =
        [
            new PlannedDelta
            {
                Kind = PlannedDeltaKind.Action,
                TargetEntityType = EntityType.Character,
                TargetId = "lin",
                ActionType = "御剑",
                Summary = "计划御剑",
            },
        ];
        await _store.SaveScene(scenes[0]);
        var rule = new WorldRule
        {
            Id = "r1",
            EntryKind = WorldRuleEntryKind.Rule,
            RuleKind = RuleKind.Forbids,
            SubjectType = EntityType.Character,
            ForbiddenAction = "御剑",
            Description = "外门弟子不得御剑",
            Severity = RuleSeverity.Constraint,
            Effect = new PlannedDelta { Summary = "被执法堂处罚" },
        };

        var result = await EvaluateAsync(novel, chapter, scenes, [rule]);

        Assert.Contains(
            result.Signals,
            s => s.RuleId == "r1" && s.Severity == HarnessSeverity.Critical
        );
        Assert.True(result.BlocksCloud);
    }

    [Fact]
    public async Task NoPlannedDelta_ProducesStallSignal()
    {
        var (novel, chapter, scenes) = await SeedAsync(planned: false);

        var result = await EvaluateAsync(novel, chapter, scenes, []);

        Assert.Contains(
            result.Signals,
            s => s.Kind == HarnessSignalKind.Stall && s.SuggestedTool == "plan_scene_deltas"
        );
        Assert.Contains("plan_scene_deltas", result.Render);
    }

    [Fact]
    public async Task DanglingParticipant_ProducesDriftSignal()
    {
        var (novel, chapter, scenes) = await SeedAsync();
        scenes[0].ParticipantIds.Add("ghost");
        await _store.SaveScene(scenes[0]);

        var result = await EvaluateAsync(novel, chapter, scenes, []);

        Assert.Contains(result.Signals, s => s.Id.Contains("drift.dangling"));
    }

    [Fact]
    public async Task PlotlineIdleBeyondWindow_ProducesStall()
    {
        var (novel, chapter, scenes) = await SeedAsync();
        novel.Plotlines =
        [
            new Plotline
            {
                Id = "p1",
                NovelId = novel.Id,
                Name = "主线",
                PlannedRevealChapterId = null,
                ResolvedAtChapterId = null,
            },
        ];
        // 第 1 章推进过，当前第 7 章 → 6 章未推进
        novel.Chapters[0].PlotlineIds = ["p1"];
        chapter.Order = 7;
        chapter.PlotlineIds = [];

        var result = await EvaluateAsync(novel, chapter, scenes, []);

        Assert.Contains(result.Signals, s => s.Id == "stall.plotline:p1");
    }

    [Fact]
    public async Task HighAiBuzzDensity_ProducesQualitySignal()
    {
        var (novel, chapter, scenes) = await SeedAsync();
        chapter.Content = string.Concat(Enumerable.Repeat("至关重要。", 40));

        var result = await EvaluateAsync(novel, chapter, scenes, []);

        Assert.Contains(
            result.Signals,
            s => s.Kind == HarnessSignalKind.Quality && s.Severity == HarnessSeverity.Warning
        );
        Assert.False(result.BlocksCloud);
    }

    private async Task<OntologyHarnessResult> EvaluateAsync(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<SceneDefinition> scenes,
        IReadOnlyList<WorldRule> rules,
        bool gateCloud = true
    )
    {
        var view = await EntityTimelineView.BuildAsync(_store, novel.Id);
        return OntologyHarnessPolicy.Evaluate(
            novel,
            chapter,
            scenes,
            rules,
            view,
            [],
            new OntologyHarnessRules(4, 5, gateCloud)
        );
    }

    private async Task<(Novel Novel, Chapter Chapter, List<SceneDefinition> Scenes)> SeedAsync(
        bool planned = true
    )
    {
        await _store.SaveNovel(new Novel { Id = "n1", Title = "harness" });
        await _store.SaveCharacter(
            new Character
            {
                Id = "lin",
                NovelId = "n1",
                Name = "林长歌",
            }
        );
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "n1",
            Title = "第一章",
            Order = 1,
            Content = "林长歌推门而入。",
        };
        await _store.SaveChapter(chapter);
        var scene = new SceneDefinition
        {
            Id = "sc1",
            NovelId = "n1",
            ChapterId = "ch1",
            Title = "场景",
            Order = 1,
            ParticipantIds = ["lin"],
            PlannedDeltas = planned
                ?
                [
                    new PlannedDelta
                    {
                        Kind = PlannedDeltaKind.StateChange,
                        TargetEntityType = EntityType.Character,
                        TargetId = "lin",
                        FromSummary = "健康",
                        ToSummary = "重伤",
                        Summary = "受伤",
                    },
                ]
                : [],
        };
        await _store.SaveScene(scene);
        var novel = (await _store.GetNovel("n1"))!;
        return (novel, chapter, [scene]);
    }
}
