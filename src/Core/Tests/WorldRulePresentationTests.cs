using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>世界规则/设定的共享展示与校验契约。</summary>
public sealed class WorldRulePresentationTests
{
    [Fact]
    public void SummaryFor_CoversEveryRuleKind()
    {
        Assert.Equal(
            "旅行速度 300里/日",
            WorldRulePresentation.SummaryFor(
                new WorldRule
                {
                    Type = "travel-speed",
                    Value = 300,
                    Unit = "里/日",
                    Description = "修士日行三百里",
                }
            )
        );

        Assert.Equal(
            "speed > 100 → 船体解体",
            WorldRulePresentation.SummaryFor(
                new WorldRule
                {
                    RuleKind = RuleKind.Numeric,
                    Metric = "speed",
                    Operator = ">",
                    Threshold = 100,
                    Effect = new PlannedDelta { Summary = "船体解体" },
                }
            )
        );

        Assert.Equal(
            "御剑 需要 金丹 → 御剑失败",
            WorldRulePresentation.SummaryFor(
                new WorldRule
                {
                    RuleKind = RuleKind.Requires,
                    Action = "御剑",
                    RequiredState = "金丹",
                    Effect = new PlannedDelta { Summary = "御剑失败" },
                }
            )
        );

        Assert.Equal(
            "禁止 御剑",
            WorldRulePresentation.SummaryFor(
                new WorldRule { RuleKind = RuleKind.Forbids, ForbiddenAction = "御剑" }
            )
        );

        Assert.Equal(
            "当 月圆",
            WorldRulePresentation.SummaryFor(
                new WorldRule { RuleKind = RuleKind.Triggers, Action = "月圆" }
            )
        );

        Assert.Equal(
            "【设定】宗门礼制",
            WorldRulePresentation.SummaryFor(
                new WorldRule
                {
                    EntryKind = WorldRuleEntryKind.Setting,
                    Description = "宗门礼制",
                    Content = "外门弟子不得御剑。",
                }
            )
        );
    }

    [Fact]
    public void Validate_EnforcesRuleAndSettingContracts()
    {
        Assert.Equal("请填写规则说明", WorldRulePresentation.Validate(new WorldRule()));

        Assert.Equal(
            "设定需要填写标题和正文",
            WorldRulePresentation.Validate(
                new WorldRule { EntryKind = WorldRuleEntryKind.Setting, Description = "宗门礼制" }
            )
        );

        Assert.Equal(
            "云端检查需要大于 0 的数值",
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "旅行速度",
                    Type = "travel-speed",
                    Unit = "里/日",
                }
            )
        );

        Assert.Equal(
            "数值规则需要填写指标、比较符和阈值",
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "超速",
                    RuleKind = RuleKind.Numeric,
                    Metric = "speed",
                    Operator = ">",
                    Effect = new PlannedDelta { Summary = "解体" },
                }
            )
        );

        Assert.Equal(
            "前置条件需要填写行动，以及所需状态或关系",
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "御剑",
                    RuleKind = RuleKind.Requires,
                    Action = "御剑",
                    Effect = new PlannedDelta { Summary = "失败" },
                }
            )
        );

        Assert.Equal(
            "禁止规则需要填写被禁止的行动",
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "禁剑",
                    RuleKind = RuleKind.Forbids,
                    Effect = new PlannedDelta { Summary = "被弹开" },
                }
            )
        );

        Assert.Equal(
            "触发规则需要填写触发条件",
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "月圆",
                    RuleKind = RuleKind.Triggers,
                    Effect = new PlannedDelta { Summary = "变身" },
                }
            )
        );

        Assert.Equal(
            "该机制必须配置后果摘要",
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "御剑",
                    RuleKind = RuleKind.Requires,
                    Action = "御剑",
                    RequiredState = "金丹",
                }
            )
        );

        Assert.Null(
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "御剑",
                    RuleKind = RuleKind.Requires,
                    Action = "御剑",
                    RequiredState = "金丹",
                    Effect = new PlannedDelta { Summary = "御剑失败" },
                }
            )
        );
        Assert.Null(
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    EntryKind = WorldRuleEntryKind.Setting,
                    Category = WorldRuleCategory.Social,
                    Description = "宗门礼制",
                    Content = "外门弟子不得御剑。",
                }
            )
        );
    }

    [Fact]
    public void IsCloudPreset_NormalizesLegacyKeys()
    {
        Assert.True(WorldRulePresentation.IsCloudPreset(new WorldRule { Type = "Travel_Speed" }));
        Assert.True(
            WorldRulePresentation.IsCloudPreset(new WorldRule { Type = "location_distance" })
        );
        Assert.False(WorldRulePresentation.IsCloudPreset(new WorldRule { Type = "" }));
        Assert.False(
            WorldRulePresentation.IsCloudPreset(new WorldRule { Type = "magic-requires" })
        );
    }

    [Fact]
    public void Validate_RequiresRelationWithoutState_IsValid()
    {
        Assert.Null(
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "御剑需要宗门身份",
                    RuleKind = RuleKind.Requires,
                    Action = "御剑",
                    RequiredRelationType = "member-of",
                    Effect = new PlannedDelta { Summary = "被弹开" },
                }
            )
        );
        Assert.Equal(
            "关系前置类型无效",
            WorldRulePresentation.Validate(
                new WorldRule
                {
                    Description = "御剑需要宗门身份",
                    RuleKind = RuleKind.Requires,
                    Action = "御剑",
                    RequiredRelationType = "not-a-real-edge",
                    Effect = new PlannedDelta { Summary = "被弹开" },
                }
            )
        );
    }

    [Fact]
    public void CopyAdvanced_PreservesOntologyFields()
    {
        var source = new WorldRule
        {
            Severity = RuleSeverity.Constraint,
            EffectiveFromChapterId = "ch2",
            EffectiveUntilChapterId = "ch5",
            RequiredRelationType = "member-of",
            RequiredRelationTargetId = "f1",
        };
        var target = new WorldRule();

        WorldRulePresentation.CopyAdvanced(source, target);

        Assert.Equal(RuleSeverity.Constraint, target.Severity);
        Assert.Equal("ch2", target.EffectiveFromChapterId);
        Assert.Equal("ch5", target.EffectiveUntilChapterId);
        Assert.Equal("member-of", target.RequiredRelationType);
        Assert.Equal("f1", target.RequiredRelationTargetId);
    }
}
