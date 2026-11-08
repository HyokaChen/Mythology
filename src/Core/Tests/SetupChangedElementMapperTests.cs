using Core.Cloud;
using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// SetupChangedElementMapper 测试（设定联动 change-impact 输入）：
/// 本体日志（OntologyAction）→ 被编辑元素折叠。关键回归锁：
/// - 章节 GUID 必须规范化为 ch_{Order}（云端只认规范形式）；
/// - 关系用 source|type|target 复合键（与云端 SetupLinkageIndex.RelationKey 同格式），
///   并同时上报两端角色（防键不一致时影响面丢失）。
/// </summary>
public class SetupChangedElementMapperTests
{
    private static Novel Novel() => new() { Id = "n1", Title = "林氏家族" };

    private static List<Chapter> Chapters() =>
        [
            new()
            {
                Id = "ch-guid-0001",
                Order = 1,
                Title = "第一章",
            },
            new()
            {
                Id = "ch-guid-0002",
                Order = 2,
                Title = "第二章",
            },
        ];

    private static List<Relationship> Relationships() =>
        [
            new()
            {
                Id = "rel1",
                CharacterAId = "c2",
                CharacterBId = "c1",
                Type = RelationshipType.ParentOf,
            },
        ];

    private static OntologyAction Action(EntityType type, string targetId, int minutesAgo = 0) =>
        new()
        {
            TargetEntityType = type,
            TargetId = targetId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
        };

    [Fact]
    public void MapsCoreEntityTypesToLinkageKinds()
    {
        var actions = new List<OntologyAction>
        {
            Action(EntityType.Character, "c1", minutesAgo: 9),
            Action(EntityType.Scene, "s1", minutesAgo: 8),
            Action(EntityType.Plotline, "pl1", minutesAgo: 7),
            Action(EntityType.Location, "loc1", minutesAgo: 6),
            Action(EntityType.Item, "i1", minutesAgo: 5),
            Action(EntityType.Faction, "f1", minutesAgo: 4),
            Action(EntityType.WorldRule, "wr1", minutesAgo: 3),
            Action(EntityType.Volume, "v1", minutesAgo: 2),
            Action(EntityType.Novel, "n1", minutesAgo: 1),
        };

        var mapped = SetupChangedElementMapper.FromOntologyActions(
            Novel(),
            actions,
            Relationships(),
            Chapters()
        );

        Assert.Equal(
            [
                // 折叠顺序 = 本体日志按时间倒序（最近的修改优先）
                ("novel", "n1"),
                ("volume", "v1"),
                ("worldrule", "wr1"),
                ("organization", "f1"),
                ("item", "i1"),
                ("location", "loc1"),
                ("plotline", "pl1"),
                ("scene", "s1"),
                ("character", "c1"),
            ],
            mapped.Select(e => (e.Kind, e.Id)).ToArray()
        );
    }

    [Fact]
    public void ChapterAction_NormalizesToChapterOrderForm()
    {
        var mapped = SetupChangedElementMapper.FromOntologyActions(
            Novel(),
            [Action(EntityType.Chapter, "ch-guid-0002")],
            Relationships(),
            Chapters()
        );

        var element = Assert.Single(mapped);
        Assert.Equal("chapter", element.Kind);
        Assert.Equal("ch_2", element.Id);
    }

    [Fact]
    public void RelationshipAction_EmitsCompositeKeyAndBothEndpoints()
    {
        var mapped = SetupChangedElementMapper.FromOntologyActions(
            Novel(),
            [Action(EntityType.Relationship, "rel1")],
            Relationships(),
            Chapters()
        );

        // parent-of 在设定检查通道是专用 token（RelationshipOntology.CloudSetupToken）。
        Assert.Contains(mapped, e => e.Kind == "relation" && e.Id == "c2|parent-of|c1");
        Assert.Contains(mapped, e => e.Kind == "character" && e.Id == "c2");
        Assert.Contains(mapped, e => e.Kind == "character" && e.Id == "c1");
    }

    [Fact]
    public void RepeatedActions_AreDeduplicated()
    {
        var actions = new List<OntologyAction>
        {
            Action(EntityType.Character, "c1", minutesAgo: 3),
            Action(EntityType.Character, "c1", minutesAgo: 2),
            Action(EntityType.Character, "c1", minutesAgo: 1),
        };

        var mapped = SetupChangedElementMapper.FromOntologyActions(
            Novel(),
            actions,
            Relationships(),
            Chapters()
        );

        var element = Assert.Single(mapped);
        Assert.Equal("c1", element.Id);
    }

    [Fact]
    public void TakeLimit_KeepsOnlyMostRecentEdits()
    {
        var actions = new List<OntologyAction>
        {
            Action(EntityType.Character, "old", minutesAgo: 10),
            Action(EntityType.Scene, "recent", minutesAgo: 1),
        };

        var mapped = SetupChangedElementMapper.FromOntologyActions(
            Novel(),
            actions,
            Relationships(),
            Chapters(),
            take: 1
        );

        Assert.Equal("scene", Assert.Single(mapped).Kind);
    }

    [Fact]
    public void UnmappedEntityTypes_AreIgnored()
    {
        var actions = new List<OntologyAction>
        {
            Action(EntityType.TimelineEvent, "te1"),
            Action(EntityType.Appearance, "ap1"),
            Action(EntityType.Note, "note1"),
            Action(EntityType.FactionRelationship, "fr1"),
            Action(EntityType.Character, "c1"),
        };

        var mapped = SetupChangedElementMapper.FromOntologyActions(
            Novel(),
            actions,
            Relationships(),
            Chapters()
        );

        Assert.Equal("c1", Assert.Single(mapped).Id);
    }

    [Fact]
    public void NoActions_ReturnsEmpty()
    {
        Assert.Empty(
            SetupChangedElementMapper.FromOntologyActions(Novel(), [], Relationships(), Chapters())
        );
    }
}
