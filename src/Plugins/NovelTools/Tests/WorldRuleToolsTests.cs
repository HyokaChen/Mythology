using Core.Data;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>世界规则 AI 工具：create/update/delete + 本地检查。</summary>
public sealed class WorldRuleToolsTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly FileNovelStore _store;

    public WorldRuleToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"world-rule-tools-{Guid.NewGuid():N}");
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
        await _store.SaveNovel(new Novel { Id = "n1", Title = "规则测试" });
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "第一章",
                Order = 1,
                Content = "林长歌御剑而起。",
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
                PlannedDeltas =
                [
                    new PlannedDelta
                    {
                        Kind = PlannedDeltaKind.Action,
                        TargetEntityType = EntityType.Character,
                        TargetId = "lin",
                        ActionType = "御剑",
                        Summary = "计划御剑",
                    },
                ],
            }
        );
    }

    [Fact]
    public async Task ManageWorldRule_CreateUpdateDelete_RoundTripsOntologyFields()
    {
        await SeedAsync();
        var tool = new ManageWorldRuleTool(_store);

        var created = await tool.ExecuteAsync(
            "t1",
            new ManageWorldRuleParams
            {
                Action = "create",
                NovelId = "n1",
                EntryKind = "rule",
                Category = "social",
                Description = "外门弟子不得御剑",
                RuleKind = "forbids",
                SubjectType = "character",
                ForbiddenAction = "御剑",
                Severity = "constraint",
                RequiredRelationType = "member-of",
                Effect = new RuleEffectParams
                {
                    Kind = "state-change",
                    TargetEntityType = "character",
                    TargetId = "lin",
                    Aspect = "status",
                    To = "受罚",
                    Summary = "被执法堂处罚",
                },
            },
            CancellationToken.None
        );

        Assert.False(created.IsError);
        var rule = Assert.Single(await _store.GetWorldRules("n1"));
        Assert.Equal(RuleSeverity.Constraint, rule.Severity);
        Assert.Equal("member-of", rule.RequiredRelationType);

        var updated = await tool.ExecuteAsync(
            "t2",
            new ManageWorldRuleParams
            {
                Action = "update",
                NovelId = "n1",
                RuleId = rule.Id,
                Severity = "warning",
            },
            CancellationToken.None
        );
        Assert.False(updated.IsError);
        Assert.Equal(RuleSeverity.Warning, (await _store.GetWorldRules("n1"))[0].Severity);

        var deleted = await tool.ExecuteAsync(
            "t3",
            new ManageWorldRuleParams
            {
                Action = "delete",
                NovelId = "n1",
                RuleId = rule.Id,
            },
            CancellationToken.None
        );
        Assert.False(deleted.IsError);
        Assert.Empty(await _store.GetWorldRules("n1"));
    }

    [Fact]
    public async Task CheckWorldRules_ReportsConstraintWithSubject()
    {
        await SeedAsync();
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

        var tool = new CheckWorldRulesTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new CheckWorldRulesParams { NovelId = "n1", ChapterId = "ch1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var text = string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("世界规则检查（第 1 章", text);
        Assert.Contains("[硬约束]", text);
        Assert.Contains("林长歌", text);
    }

    [Fact]
    public async Task CheckWorldRules_StatusTokensWorkForFactionItemLocationPlotline()
    {
        await SeedAsync();
        await _store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = "n1",
                Name = "青云宗",
                Members = ["lin"],
                Status = FactionStatus.Dissolved,
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "lin",
                NovelId = "n1",
                Name = "林长歌",
                FactionId = "f1",
            }
        );
        await _store.SaveLocation(
            new Location
            {
                Id = "loc1",
                NovelId = "n1",
                Name = "青云山",
                Status = LocationStatus.Destroyed,
            }
        );
        await _store.SaveItem(
            new Item
            {
                Id = "it1",
                NovelId = "n1",
                Name = "青锋剑",
                Status = ItemStatus.Lost,
            }
        );
        await _store.SavePlotline(
            new Plotline
            {
                Id = "pl1",
                NovelId = "n1",
                Name = "复仇",
                Status = PlotlineStatus.Resolved,
            }
        );
        await _store.SaveScene(
            new SceneDefinition
            {
                Id = "sc2",
                NovelId = "n1",
                ChapterId = "ch1",
                Title = "废墟",
                Order = 2,
                LocationId = "loc1",
                ParticipantIds = ["lin"],
                ItemIds = ["it1"],
                PlotlineIds = ["pl1"],
            }
        );

        var rules = new (string Id, EntityType Type, string Action, string Summary)[]
        {
            ("r-fac", EntityType.Faction, "dissolved", "组织已覆灭仍出场"),
            ("r-item", EntityType.Item, "lost", "遗失道具仍被使用"),
            ("r-loc", EntityType.Location, "destroyed", "毁灭地点仍有场景"),
            ("r-pl", EntityType.Plotline, "resolved", "已解决情节线仍推进"),
        };
        foreach (var (id, type, action, summary) in rules)
        {
            await _store.SaveWorldRule(
                "n1",
                new WorldRule
                {
                    Id = id,
                    NovelId = "n1",
                    EntryKind = WorldRuleEntryKind.Rule,
                    RuleKind = RuleKind.Triggers,
                    SubjectType = type,
                    Action = action,
                    Description = summary,
                    Severity = RuleSeverity.Warning,
                    Effect = new PlannedDelta { Summary = summary },
                }
            );
        }

        var tool = new CheckWorldRulesTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new CheckWorldRulesParams { NovelId = "n1", ChapterId = "ch1" },
            CancellationToken.None
        );
        Assert.False(result.IsError);
        var text = string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("组织已覆灭仍出场", text);
        Assert.Contains("遗失道具仍被使用", text);
        Assert.Contains("毁灭地点仍有场景", text);
        Assert.Contains("已解决情节线仍推进", text);
    }

    [Fact]
    public async Task CheckWorldRules_WithoutRules_ReturnsHint()
    {
        await SeedAsync();
        var tool = new CheckWorldRulesTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new CheckWorldRulesParams { NovelId = "n1" },
            CancellationToken.None
        );
        var text = string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("未定义世界规则", text);
    }
}
