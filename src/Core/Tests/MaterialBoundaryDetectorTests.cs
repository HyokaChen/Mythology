using Core.Data;
using Core.Evolution;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 本体状态变更 → 素材边界测试（v3.2）：
/// 老角色无变化不触发；缺席期间变化触发；首现触发；planned 触发；规则模板触发。
/// </summary>
[Collection("FileNovelStore")]
public sealed class MaterialBoundaryDetectorTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly FileNovelStore _store;

    public MaterialBoundaryDetectorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"material-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _store = new FileNovelStore(_tempRoot, Path.Combine(_tempRoot, ".mythology"));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // best effort
        }
        return default;
    }

    [Fact]
    public async Task ReturningCharacterWithoutStateChange_ProducesNoDelta()
    {
        var (novel, chapter) = await SeedAsync(
            appearances: [Appearance("ch1", "lin", EntityType.Character, AppearanceMode.Present)],
            changes: []
        );

        var deltas = await DetectAsync(novel, chapter, ["lin"]);

        Assert.Empty(deltas);
    }

    [Fact]
    public async Task StateChangedWhileAbsent_ProducesStatusDelta()
    {
        var (novel, chapter) = await SeedAsync(
            appearances: [Appearance("ch1", "lin", EntityType.Character, AppearanceMode.Present)],
            changes:
            [
                new EntityStateChange
                {
                    Id = "ev1",
                    NovelId = "nov001",
                    TargetEntityType = EntityType.Character,
                    TargetId = "lin",
                    ChapterId = "ch2",
                    Aspect = EvolutionAspect.Status,
                    FromSummary = "健康",
                    ToSummary = "重伤",
                    Summary = "为救女主中箭重伤",
                },
            ]
        );

        var deltas = await DetectAsync(novel, chapter, ["lin"]);

        var delta = Assert.Single(deltas);
        Assert.Equal(EvolutionAspect.Status, delta.Aspect);
        Assert.Equal("健康", delta.From);
        Assert.Equal("重伤", delta.To);
        Assert.Equal(DeltaSource.Persisted, delta.Source);
        Assert.Equal(MaterialNeed.Strong, delta.Need);
    }

    [Fact]
    public async Task StateChangedInLastPresentChapter_DoesNotRetriggerNextChapter()
    {
        var (novel, chapter) = await SeedAsync(
            appearances: [Appearance("ch1", "lin", EntityType.Character, AppearanceMode.Present)],
            changes:
            [
                new EntityStateChange
                {
                    Id = "ev1",
                    NovelId = "nov001",
                    TargetEntityType = EntityType.Character,
                    TargetId = "lin",
                    ChapterId = "ch1",
                    Aspect = EvolutionAspect.Status,
                    FromSummary = "健康",
                    ToSummary = "重伤",
                    Summary = "第1章受伤",
                },
            ]
        );

        var deltas = await DetectAsync(novel, chapter, ["lin"]);

        Assert.DoesNotContain(deltas, d => d.Aspect == EvolutionAspect.Status);
    }

    [Fact]
    public async Task FirstPresentAppearance_ProducesFirstAppearanceDelta()
    {
        var (novel, chapter) = await SeedAsync(appearances: [], changes: []);

        var deltas = await DetectAsync(novel, chapter, ["lin"]);

        var delta = Assert.Single(deltas);
        Assert.Null(delta.Aspect);
        Assert.Equal(MaterialNeed.Strong, delta.Need);
        Assert.Contains("description", delta.Kinds);
    }

    [Fact]
    public async Task PlannedDelta_IsIncludedWithPlannedSource()
    {
        var (novel, chapter) = await SeedAsync(
            appearances: [Appearance("ch1", "lin", EntityType.Character, AppearanceMode.Present)],
            changes: []
        );
        var scene = await _store.GetScenesByChapter(chapter.Id);
        scene[0].PlannedDeltas =
        [
            new PlannedDelta
            {
                Kind = PlannedDeltaKind.StateChange,
                TargetEntityType = EntityType.Character,
                TargetId = "lin",
                Aspect = EvolutionAspect.Status,
                FromSummary = "健康",
                ToSummary = "重伤",
                Summary = "本场景中箭",
            },
        ];
        await _store.SaveScene(scene[0]);

        var deltas = await DetectAsync(novel, chapter, ["lin"]);

        var delta = Assert.Single(deltas);
        Assert.Equal(DeltaSource.Planned, delta.Source);
        Assert.Equal("重伤", delta.To);
    }

    [Fact]
    public async Task RuleTrigger_FromFactsProvider_ProducesComputedDelta()
    {
        var (novel, chapter) = await SeedAsync(
            appearances: [Appearance("ch1", "lin", EntityType.Character, AppearanceMode.Present)],
            changes: []
        );
        var scene = await _store.GetScenesByChapter(chapter.Id);
        var view = await EntityTimelineView.BuildAsync(_store, novel.Id);
        var rule = new WorldRule
        {
            Id = "sword",
            RuleKind = RuleKind.Requires,
            SubjectType = EntityType.Character,
            SubjectId = "lin",
            Action = "御剑",
            RequiredState = "金丹",
            Effect = new PlannedDelta
            {
                Kind = PlannedDeltaKind.StateChange,
                TargetEntityType = EntityType.Character,
                TargetId = "lin",
                Aspect = EvolutionAspect.Ability,
                FromSummary = "强行御剑",
                ToSummary = "灵力反噬",
                Summary = "未达金丹强行御剑，灵力反噬",
            },
        };
        var facts = new RuleFacts(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "御剑" }
        );

        var boundaries = MaterialBoundaryDetector.DetectForChapter(
            view,
            novel,
            chapter,
            scene,
            [rule],
            (_, _) => facts
        );

        var delta = Assert.Single(boundaries.SelectMany(b => b.Deltas));
        Assert.Equal(PlannedDeltaKind.RuleTrigger, delta.Kind);
        Assert.Equal(DeltaSource.Computed, delta.Source);
        Assert.Equal("灵力反噬", delta.To);
    }

    [Fact]
    public void RuleEvaluator_NumericThreshold_TriggersOnlyWhenExceeded()
    {
        var rule = new WorldRule
        {
            Id = "ftl",
            RuleKind = RuleKind.Numeric,
            SubjectType = EntityType.Item,
            Metric = "speed",
            Operator = ">",
            Threshold = 100,
            Effect = new PlannedDelta
            {
                Kind = PlannedDeltaKind.StateChange,
                TargetEntityType = EntityType.Item,
                TargetId = "ship",
                Aspect = EvolutionAspect.Status,
                FromSummary = "完好",
                ToSummary = "解体",
                Summary = "超速解体",
            },
        };
        var fast = new RuleFacts(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["speed"] = 120 },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );
        var slow = new RuleFacts(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["speed"] = 80 },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );

        Assert.NotNull(RuleEvaluator.Evaluate(rule, EntityType.Item, "ship", fast));
        Assert.Null(RuleEvaluator.Evaluate(rule, EntityType.Item, "ship", slow));
    }

    [Fact]
    public void RuleEvaluator_RequiresAndForbids_UseExactTokens()
    {
        var requires = new WorldRule
        {
            Id = "sword",
            RuleKind = RuleKind.Requires,
            SubjectType = EntityType.Character,
            Action = "御剑",
            RequiredState = "金丹",
            Effect = new PlannedDelta { TargetEntityType = EntityType.Character, TargetId = "lin" },
        };
        var forbidden = new WorldRule
        {
            Id = "threshold",
            RuleKind = RuleKind.Forbids,
            SubjectType = EntityType.Character,
            ForbiddenAction = "跨门槛",
            Effect = new PlannedDelta
            {
                TargetEntityType = EntityType.Character,
                TargetId = "vamp",
            },
        };
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "御剑", "跨门槛" };
        var facts = new RuleFacts(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            tokens
        );

        Assert.NotNull(RuleEvaluator.Evaluate(requires, EntityType.Character, "lin", facts));
        Assert.NotNull(RuleEvaluator.Evaluate(forbidden, EntityType.Character, "vamp", facts));
        Assert.Null(RuleEvaluator.Evaluate(requires, EntityType.Item, "ship", facts));
    }

    [Fact]
    public void RuleEvaluator_SettingEntry_NeverTriggers()
    {
        var setting = new WorldRule
        {
            Id = "social-rule",
            EntryKind = WorldRuleEntryKind.Setting,
            Category = WorldRuleCategory.Social,
            Description = "宗门礼制",
            Content = "外门弟子不得御剑。",
        };
        var facts = new RuleFacts(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "御剑" }
        );

        Assert.Null(RuleEvaluator.Evaluate(setting, EntityType.Character, "lin", facts));
    }

    [Fact]
    public async Task WorldRule_RoundTripsOntologyFields()
    {
        var novel = new Novel { Id = "nov-rule", Title = "规则测试" };
        await _store.SaveNovel(novel);
        var rule = new WorldRule
        {
            Id = "ftl",
            NovelId = novel.Id,
            Category = WorldRuleCategory.Power,
            RuleKind = RuleKind.Numeric,
            SubjectType = EntityType.Item,
            SubjectId = "ship",
            Metric = "speed",
            Operator = ">",
            Threshold = 299792458,
            Action = "超光速",
            Effect = new PlannedDelta
            {
                Kind = PlannedDeltaKind.StateChange,
                TargetEntityType = EntityType.Item,
                TargetId = "ship",
                Aspect = EvolutionAspect.Status,
                FromSummary = "完好",
                ToSummary = "解体",
                Summary = "超光速解体",
            },
        };
        await _store.SaveWorldRule(novel.Id, rule);

        var loaded = Assert.Single(await _store.GetWorldRules(novel.Id));
        Assert.Equal(WorldRuleEntryKind.Rule, loaded.EntryKind);
        Assert.Equal(WorldRuleCategory.Power, loaded.Category);
        Assert.Equal(RuleKind.Numeric, loaded.RuleKind);
        Assert.Equal(EntityType.Item, loaded.SubjectType);
        Assert.Equal("speed", loaded.Metric);
        Assert.Equal(">", loaded.Operator);
        Assert.Equal(299792458, loaded.Threshold);
        Assert.Equal("超光速", loaded.Action);
        Assert.NotNull(loaded.Effect);
        Assert.Equal("解体", loaded.Effect!.ToSummary);
    }

    [Fact]
    public async Task CharacterMetrics_RoundTrip()
    {
        var (novel, _) = await SeedAsync(appearances: [], changes: []);
        var character = (await _store.GetCharacter(novel.Id, "lin"))!;
        character.Metrics["speed"] = 120;
        character.Metrics["realm"] = 9;
        await _store.SaveCharacter(character);

        var loaded = await _store.GetCharacter(novel.Id, "lin");

        Assert.NotNull(loaded);
        Assert.Equal(120, loaded!.Metrics["speed"]);
        Assert.Equal(9, loaded.Metrics["realm"]);
    }

    [Fact]
    public async Task NumericRule_FromEntityMetrics_ProducesComputedDelta()
    {
        var (novel, chapter) = await SeedAsync(
            appearances: [Appearance("ch1", "lin", EntityType.Character, AppearanceMode.Present)],
            changes: []
        );
        var character = (await _store.GetCharacter(novel.Id, "lin"))!;
        character.Metrics["speed"] = 120;
        await _store.SaveCharacter(character);
        novel = (await _store.GetNovel(novel.Id))!;
        var scene = await _store.GetScenesByChapter(chapter.Id);
        var view = await EntityTimelineView.BuildAsync(_store, novel.Id);
        var rule = new WorldRule
        {
            Id = "ftl",
            RuleKind = RuleKind.Numeric,
            SubjectType = EntityType.Character,
            SubjectId = "lin",
            Metric = "speed",
            Operator = ">",
            Threshold = 100,
            Effect = new PlannedDelta
            {
                Kind = PlannedDeltaKind.StateChange,
                TargetEntityType = EntityType.Character,
                TargetId = "lin",
                Aspect = EvolutionAspect.Status,
                FromSummary = "完好",
                ToSummary = "解体",
                Summary = "超速解体",
            },
        };

        var boundaries = MaterialBoundaryDetector.DetectForChapter(
            view,
            novel,
            chapter,
            scene,
            [rule]
        );

        var delta = Assert.Single(boundaries.SelectMany(b => b.Deltas));
        Assert.Equal(PlannedDeltaKind.RuleTrigger, delta.Kind);
        Assert.Equal(DeltaSource.Computed, delta.Source);
    }

    [Fact]
    public async Task SettingsStateAction_ProducesSettingsDelta()
    {
        var (novel, chapter) = await SeedAsync(appearances: [], changes: []);
        var scene = await _store.GetScenesByChapter(chapter.Id);
        var actions = new List<OntologyAction>
        {
            new()
            {
                Id = "act1",
                NovelId = novel.Id,
                Element = OntologyElement.State,
                Operation = OntologyOperation.Update,
                TargetEntityType = EntityType.Character,
                TargetId = "lin",
                TargetName = "林长歌",
                Field = "status",
                From = "active",
                To = "deceased",
                Summary = "角色「林长歌」status: active → deceased",
                CreatedAt = DateTime.UtcNow.AddMinutes(10),
                Source = AuditSource.User,
            },
        };

        var deltas = await DetectAsync(novel, chapter, scene, null, actions);

        var delta = Assert.Single(deltas.Where(d => d.Source == DeltaSource.Settings));
        Assert.Equal(OntologyCalculations.SettingsChange, delta.CalculationId);
        Assert.Equal("lin", delta.EntityId);
        Assert.Equal(EvolutionAspect.Status, delta.Aspect);
    }

    [Fact]
    public async Task ObjectActions_DoNotProduceSettingsDelta()
    {
        var (novel, chapter) = await SeedAsync(appearances: [], changes: []);
        var scene = await _store.GetScenesByChapter(chapter.Id);
        var actions = new List<OntologyAction>
        {
            new()
            {
                Id = "act2",
                NovelId = novel.Id,
                Element = OntologyElement.Object,
                Operation = OntologyOperation.Update,
                TargetEntityType = EntityType.Character,
                TargetId = "lin",
                Field = "personality",
                Summary = "性格描述更新",
                CreatedAt = DateTime.UtcNow.AddMinutes(10),
            },
        };

        var deltas = await DetectAsync(novel, chapter, scene, null, actions);

        Assert.DoesNotContain(deltas, d => d.Source == DeltaSource.Settings);
    }

    [Fact]
    public async Task RelationshipSettingsAction_MapsToSceneEndpoint()
    {
        var (novel, chapter) = await SeedAsync(appearances: [], changes: []);
        await _store.SaveCharacter(
            new Character
            {
                Id = "su",
                NovelId = novel.Id,
                Name = "苏砚",
            }
        );
        var relationship = new Relationship
        {
            Id = "rel1",
            NovelId = novel.Id,
            CharacterAId = "lin",
            CharacterBId = "su",
            Type = RelationshipType.Enemy,
        };
        await _store.SaveRelationship(relationship);
        novel = (await _store.GetNovel(novel.Id))!;
        Assert.Single(novel.Relationships);
        var scene = await _store.GetScenesByChapter(chapter.Id);
        var actions = new List<OntologyAction>
        {
            new()
            {
                Id = "act3",
                NovelId = novel.Id,
                Element = OntologyElement.Relation,
                Operation = OntologyOperation.Update,
                TargetEntityType = EntityType.Relationship,
                TargetId = "rel1",
                Field = "type",
                From = "friend",
                To = "enemy",
                Summary = "关系「林↔苏」type: friend → enemy",
                CreatedAt = DateTime.UtcNow.AddMinutes(10),
            },
        };

        var deltas = await DetectAsync(novel, chapter, scene, null, actions);

        Assert.Contains(deltas, d => d.Source == DeltaSource.Settings && d.EntityId == "lin");
    }

    [Fact]
    public void AppliesToChapter_RespectsEffectiveRange()
    {
        var rule = new WorldRule
        {
            Id = "r1",
            EffectiveFromChapterId = "ch2",
            EffectiveUntilChapterId = "ch3",
        };
        var order = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ch1"] = 1,
            ["ch2"] = 2,
            ["ch3"] = 3,
            ["ch4"] = 4,
        };

        Assert.False(RuleEvaluator.AppliesToChapter(rule, order, 1));
        Assert.True(RuleEvaluator.AppliesToChapter(rule, order, 2));
        Assert.True(RuleEvaluator.AppliesToChapter(rule, order, 3));
        Assert.False(RuleEvaluator.AppliesToChapter(rule, order, 4));
    }

    [Fact]
    public async Task PlannedActionToken_TriggersForbidsRule()
    {
        var (novel, chapter) = await SeedAsync(appearances: [], changes: []);
        var scene = (await _store.GetScenesByChapter(chapter.Id)).Single();
        scene.PlannedDeltas =
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
        var rule = new WorldRule
        {
            Id = "forbid1",
            EntryKind = WorldRuleEntryKind.Rule,
            RuleKind = RuleKind.Forbids,
            SubjectType = EntityType.Character,
            ForbiddenAction = "御剑",
            Description = "外门弟子不得御剑",
            Severity = RuleSeverity.Constraint,
            Effect = new PlannedDelta { Summary = "被执法堂处罚" },
        };

        var deltas = await DetectAsync(novel, chapter, [scene], [rule], null);

        var delta = Assert.Single(deltas.Where(d => d.Kind == PlannedDeltaKind.RuleTrigger));
        Assert.Equal("forbid1", delta.RuleId);
        Assert.Equal(MaterialNeed.Strong, delta.Need);
    }

    [Fact]
    public async Task RequiresRelation_TriggersWhenMissing_AndStopsWhenPresent()
    {
        var (novel, chapter) = await SeedAsync(appearances: [], changes: []);
        var scene = (await _store.GetScenesByChapter(chapter.Id)).Single();
        scene.PlannedDeltas =
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
        var rule = new WorldRule
        {
            Id = "req1",
            EntryKind = WorldRuleEntryKind.Rule,
            RuleKind = RuleKind.Requires,
            SubjectType = EntityType.Character,
            Action = "御剑",
            RequiredRelationType = "member-of",
            Description = "御剑需要宗门身份",
            Effect = new PlannedDelta { Summary = "被弹开" },
        };

        var missing = await DetectAsync(novel, chapter, [scene], [rule], null);
        Assert.Contains(missing, d => d.Kind == PlannedDeltaKind.RuleTrigger);

        var character = (await _store.GetCharacter(novel.Id, "lin"))!;
        character.FactionId = "f1";
        await _store.SaveCharacter(character);
        novel = (await _store.GetNovel(novel.Id))!;

        var present = await DetectAsync(novel, chapter, [scene], [rule], null);
        Assert.DoesNotContain(present, d => d.Kind == PlannedDeltaKind.RuleTrigger);
    }

    private async Task<List<MaterialDelta>> DetectAsync(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<SceneDefinition>? scenes,
        IReadOnlyList<WorldRule>? rules,
        IReadOnlyList<OntologyAction>? actions
    )
    {
        var view = await EntityTimelineView.BuildAsync(_store, novel.Id);
        var boundaries = MaterialBoundaryDetector.DetectForChapter(
            view,
            novel,
            chapter,
            scenes ?? [],
            rules,
            recentActions: actions
        );
        return boundaries.SelectMany(b => b.Deltas).ToList();
    }

    private async Task<IReadOnlyList<MaterialDelta>> DetectAsync(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<string> participants
    )
    {
        var scenes = await _store.GetScenesByChapter(chapter.Id);
        if (scenes.Count == 0)
        {
            scenes =
            [
                new SceneDefinition
                {
                    Id = $"scene-{chapter.Id}",
                    NovelId = novel.Id,
                    ChapterId = chapter.Id,
                    Title = "测试场景",
                    Order = 1,
                    ParticipantIds = participants.ToList(),
                },
            ];
        }
        var view = await EntityTimelineView.BuildAsync(_store, novel.Id);
        var boundaries = MaterialBoundaryDetector.DetectForChapter(view, novel, chapter, scenes);
        return boundaries.SelectMany(b => b.Deltas).ToList();
    }

    private async Task<(Novel Novel, Chapter Chapter)> SeedAsync(
        IReadOnlyList<AppearanceRecord> appearances,
        IReadOnlyList<EntityStateChange> changes
    )
    {
        var novel = new Novel { Id = "nov001", Title = "边界测试" };
        await _store.SaveNovel(novel);
        var lin = new Character
        {
            Id = "lin",
            NovelId = novel.Id,
            Name = "林长歌",
            Role = CharacterRole.Protagonist,
        };
        await _store.SaveCharacter(lin);

        var chapters = new List<Chapter>();
        for (var order = 1; order <= 3; order++)
        {
            var chapter = new Chapter
            {
                Id = $"ch{order}",
                NovelId = novel.Id,
                Title = $"第{order}章",
                Order = order,
                Content = "测试正文",
            };
            await _store.SaveChapter(chapter);
            chapters.Add(chapter);
        }

        if (appearances.Count > 0)
        {
            foreach (var group in appearances.GroupBy(a => a.ChapterId))
            {
                await _store.SaveChapterAppearances(novel.Id, group.Key, group.ToList());
            }
        }
        if (changes.Count > 0)
        {
            foreach (var group in changes.GroupBy(c => c.ChapterId))
            {
                await _store.SaveEntityStateChanges(novel.Id, group.Key, group.ToList());
            }
        }

        var scene = new SceneDefinition
        {
            Id = "scene3",
            NovelId = novel.Id,
            ChapterId = "ch3",
            Title = "第3章场景",
            Order = 1,
            ParticipantIds = ["lin"],
        };
        await _store.SaveScene(scene);

        novel = (await _store.GetNovel(novel.Id))!;
        novel.Chapters = chapters;
        novel.Characters = [lin];
        return (novel, chapters[2]);
    }

    private static AppearanceRecord Appearance(
        string chapterId,
        string entityId,
        EntityType type,
        AppearanceMode mode
    ) =>
        new()
        {
            Id = $"ap-{chapterId}-{entityId}",
            NovelId = "nov001",
            EntityType = type,
            EntityId = entityId,
            ChapterId = chapterId,
            Mode = mode,
            Source = AuditSource.System,
        };
}
