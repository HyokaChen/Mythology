using Core.Data;
using Core.Evolution;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>plan_scene_deltas：场景计划变化写入面与动作日志。</summary>
public sealed class PlanSceneDeltasToolTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly FileNovelStore _store;

    public PlanSceneDeltasToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"plan-deltas-{Guid.NewGuid():N}");
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

    private async Task SeedAsync()
    {
        await _store.SaveNovel(new Novel { Id = "n1", Title = "计划测试" });
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "第一章",
                Order = 1,
                Content = "正文",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "lin",
                NovelId = "n1",
                Name = "林长歌",
            }
        );
        await _store.SaveScene(
            new SceneDefinition
            {
                Id = "sc1",
                NovelId = "n1",
                ChapterId = "ch1",
                Title = "场景",
                Order = 1,
                ParticipantIds = ["lin"],
            }
        );
        await _store.SaveWorldRule(
            "n1",
            new WorldRule
            {
                Id = "r1",
                NovelId = "n1",
                EntryKind = WorldRuleEntryKind.Rule,
                RuleKind = RuleKind.Forbids,
                SubjectType = EntityType.Character,
                ForbiddenAction = "御剑",
                Description = "外门弟子不得御剑",
                Severity = RuleSeverity.Constraint,
                Effect = new PlannedDelta { Summary = "被执法堂处罚" },
            }
        );
    }

    [Fact]
    public async Task Replace_WritesDeltasAndJournalsAction()
    {
        await SeedAsync();
        var tool = new PlanSceneDeltasTool(_store);

        var result = await tool.ExecuteAsync(
            "t1",
            new PlanSceneDeltasParams
            {
                NovelId = "n1",
                SceneId = "sc1",
                Mode = "replace",
                ProposalId = "op-1",
                Evidence = "证据",
                Deltas =
                [
                    new PlannedDeltaParams
                    {
                        Kind = "action",
                        TargetEntityType = "character",
                        TargetId = "lin",
                        ActionType = "御剑",
                        Summary = "计划御剑",
                    },
                    new PlannedDeltaParams
                    {
                        Kind = "rule-trigger",
                        TargetEntityType = "character",
                        TargetId = "lin",
                        RuleId = "r1",
                        Summary = "规则触发",
                    },
                ],
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var scene = await _store.GetScene("n1", "sc1");
        Assert.NotNull(scene);
        Assert.Equal(2, scene!.PlannedDeltas.Count);

        var actions = await _store.GetOntologyActions("n1");
        Assert.Contains(
            actions,
            a =>
                a.Element == OntologyElement.Action
                && a.Field == "planned-deltas"
                && a.ProposalId == "op-1"
        );
    }

    [Fact]
    public async Task UnknownRuleId_ReturnsErrorWithoutWriting()
    {
        await SeedAsync();
        var tool = new PlanSceneDeltasTool(_store);

        var result = await tool.ExecuteAsync(
            "t1",
            new PlanSceneDeltasParams
            {
                NovelId = "n1",
                SceneId = "sc1",
                Deltas =
                [
                    new PlannedDeltaParams
                    {
                        Kind = "rule-trigger",
                        TargetEntityType = "character",
                        TargetId = "lin",
                        RuleId = "missing",
                        Summary = "规则触发",
                    },
                ],
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        var scene = await _store.GetScene("n1", "sc1");
        Assert.Empty(scene!.PlannedDeltas);
    }

    [Fact]
    public async Task PlannedAction_TriggersRuleCheck()
    {
        await SeedAsync();
        var tool = new PlanSceneDeltasTool(_store);
        await tool.ExecuteAsync(
            "t1",
            new PlanSceneDeltasParams
            {
                NovelId = "n1",
                SceneId = "sc1",
                Deltas =
                [
                    new PlannedDeltaParams
                    {
                        Kind = "action",
                        TargetEntityType = "character",
                        TargetId = "lin",
                        ActionType = "御剑",
                        Summary = "计划御剑",
                    },
                ],
            },
            CancellationToken.None
        );

        var check = new CheckWorldRulesTool(_store);
        var result = await check.ExecuteAsync(
            "t2",
            new CheckWorldRulesParams { NovelId = "n1", ChapterId = "ch1" },
            CancellationToken.None
        );
        var text = string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("[硬约束]", text);
        Assert.Contains("林长歌", text);
    }
}
