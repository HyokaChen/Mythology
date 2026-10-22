using Core.Cloud;
using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// SetupDigestBuilder 测试（CHANGE_10 · fiction.setup.check）：
/// 本地 Novel 设定实体 → SetupCheckInput（云端 CloudSetupInput 镜像）映射。
/// 关键回归锁：客户端章节 ID 是 12 位 hex GUID，必须规范化为 ch_{Order}——
/// 否则云端 ChapterRefResolver 解析失败 → 章×线矩阵为空 + 休眠假告警。
/// </summary>
public class SetupDigestBuilderTests
{
    private static (
        Novel Novel,
        List<Character> Characters,
        List<Faction> Factions,
        List<Relationship> Relationships,
        List<SceneDefinition> Scenes,
        List<Plotline> Plotlines,
        List<Chapter> Chapters,
        List<TimelineEvent> TimelineEvents
    ) Sample()
    {
        var novel = new Novel
        {
            Id = "n1",
            Title = "林氏家族",
            Genre = "玄幻",
            CompletionStatus = "completed",
        };
        var characters = new List<Character>
        {
            new()
            {
                Id = "c1",
                Name = "林默",
                Age = "25",
                Role = CharacterRole.Protagonist,
                Status = CharacterStatus.Active,
                FactionId = "f1",
                Personality = "沉毅",
                Motivation = "查清灭门真相",
                Background = "林家遗孤",
                Habits = "摩挲玉佩",
            },
            new()
            {
                Id = "c2",
                Name = "林崇山",
                Age = "46",
                Role = CharacterRole.Supporting,
                Status = CharacterStatus.Deceased,
                Personality = "威严",
                Motivation = "守护家族",
            },
        };
        var factions = new List<Faction>
        {
            new() { Id = "f1", Name = "林家" },
        };
        var relationships = new List<Relationship>
        {
            new()
            {
                CharacterAId = "c2",
                CharacterBId = "c1",
                Type = RelationshipType.Family,
                Strength = RelationshipStrength.Strong,
            },
        };
        var scenes = new List<SceneDefinition>
        {
            new()
            {
                Id = "s1",
                Title = "山门",
                ChapterId = "ch-guid-0001",
                LocationId = "loc1",
                ParticipantIds = ["c1", "c2"],
                PlotlineIds = ["pl1"],
                TimeDescription = "黄昏",
            },
        };
        var plotlines = new List<Plotline>
        {
            new()
            {
                Id = "pl1",
                Name = "家族秘密",
                Type = PlotlineType.Main,
                PlannedRevealChapterId = "ch-guid-0002",
            },
        };
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "ch-guid-0001",
                Order = 1,
                Content = "一千二百字的正文。",
            },
            new()
            {
                Id = "ch-guid-0002",
                Order = 2,
                Content = "八百字。",
            },
        };
        return (novel, characters, factions, relationships, scenes, plotlines, chapters, []);
    }

    [Fact]
    public void Build_CanonicalizesChapterRefsToChOrder()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );

        Assert.NotNull(setup);
        Assert.Equal(
            ["ch_1", "ch_2"],
            setup.ChapterAnchors!.Select(a => a.ChapterId).OrderBy(x => x).ToArray()
        );
        Assert.Equal("ch_1", setup.Scenes![0].ChapterId);
        Assert.Equal("ch_2", setup.Plotlines![0].PlannedRevealChapterId);
    }

    [Fact]
    public void Build_MapsCharacterFields()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );

        var c = Assert.Single(setup.Characters!.Where(x => x.Id == "c1"));
        Assert.Equal("c1", c.Id);
        Assert.Equal("林默", c.Name);
        Assert.Equal("protagonist", c.Role);
        Assert.Equal("active", c.Status);
        Assert.Equal(["f1"], c.Organizations); // 组织实体 Id（悬空可判）
        // v2 写作锚点（反漂移）：四个字段全部上报
        Assert.Equal("沉毅", c.Personality);
        Assert.Equal("查清灭门真相", c.Motivation);
        Assert.Equal("林家遗孤", c.Background);
        Assert.Equal("摩挲玉佩", c.Habits);
        // v3 章节锚点：字数透传（云 章 Gini 数据源）
        Assert.Equal(9, setup.ChapterAnchors![0].WordCount);
    }

    [Fact]
    public void Build_MapsRelationsAndStrength()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );

        var r = Assert.Single(setup.Relations!);
        Assert.Equal("c2", r.SourceId);
        Assert.Equal("c1", r.TargetId);
        Assert.Equal("family", r.Type);
        Assert.Equal("strong", r.Strength);
    }

    [Fact]
    public void Build_MapsScenesAndPlotlines()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );

        var s = Assert.Single(setup.Scenes!);
        Assert.Equal("s1", s.Id);
        Assert.Equal("山门", s.Name);
        Assert.Equal("loc1", s.LocationId);
        Assert.Equal(["c1", "c2"], s.ParticipantIds);
        Assert.Equal(["pl1"], s.PlotlineIds);
        Assert.Equal("黄昏", s.TimeDescription);

        var p = Assert.Single(setup.Plotlines!);
        Assert.Equal("main", p.Type);
        Assert.Null(p.Beats); // Turnpoints 自由文本 → LLM 解析层为后续项
    }

    [Fact]
    public void Build_MapsNovelStatusAndTitle()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );

        Assert.Equal("completed", setup.Status);
        Assert.Equal("林氏家族", setup.NovelTitle);
        Assert.Empty(setup.WorldRules!);
        Assert.Null(setup.AssertedFacts);
    }

    [Fact]
    public void Build_UnmappedChapterRefFallsBackToNull()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        // 场景引用不存在的章节 → 置 null（云端对无法解析的引用静默降级，无误报）
        scenes[0].ChapterId = "ch-guid-9999";
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );

        Assert.Null(setup.Scenes![0].ChapterId);
    }

    [Fact]
    public void Build_EmptyNovel_Safe()
    {
        var novel = new Novel { Id = "n2" };
        var setup = SetupDigestBuilder.Build(novel, [], [], [], [], [], [], []);

        Assert.NotNull(setup);
        Assert.Empty(setup.Characters!);
        Assert.Empty(setup.ChapterAnchors!);
        Assert.Equal("ongoing", setup.Status);
    }

    [Fact]
    public void Build_OrgNameFromFaction_UnknownFactionDropped()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        characters[0].FactionId = "f-missing"; // 指向不存在的组织
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );

        var c = Assert.Single(setup.Characters!.Where(x => x.Id == "c1"));
        Assert.Equal(["f-missing"], c.Organizations); // 原样上报 → 云端 L0 悬空判定负责
    }

    [Fact]
    public void Build_MapsLocationsOrganizationsItems()
    {
        // v2 一等实体：地点/组织（含阵营）/道具（所有权链）上报
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var locations = new List<Location>
        {
            new()
            {
                Id = "loc1",
                Name = "林府",
                ParentId = "东荒",
            },
        };
        factions[0].Members = ["c1"];
        var items = new List<Item>
        {
            new()
            {
                Id = "it1",
                Name = "家传玉佩",
                OwnerId = "c1",
            },
            new() { Id = "it2", Name = "无主古剑" },
        };

        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline,
            locations,
            items
        );

        var loc = Assert.Single(setup.Locations!);
        Assert.Equal("loc1", loc.Id);
        Assert.Equal("林府", loc.Name);
        Assert.Equal("东荒", loc.Region);

        var org = Assert.Single(setup.Organizations!);
        Assert.Equal("f1", org.Id);
        // 成员双向并集：Faction.Members ∪ 角色侧 FactionId 反查
        Assert.Equal(new[] { "c1" }, org.MemberIds!); // sample 中 c2 无 FactionId
        Assert.Equal(2, setup.Items!.Count);
        var owned = setup.Items!.Single(i => i.Id == "it1");
        var ownership = Assert.Single(owned.Ownership!);
        Assert.Equal("c1", ownership.OwnerId);
        Assert.Null(ownership.SinceChapterId); // 单值 OwnerId → 从头持有，无时窗
        Assert.Null(setup.Items!.Single(i => i.Id == "it2").Ownership);
    }

    [Fact]
    public void Build_EmptyLocationsItems_OmitFields()
    {
        // 硬反例：无地点/道具 → 字段缺省（云端相应检查静默，不产生空实体噪声）
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline,
            [],
            []
        );
        Assert.Null(setup.Locations);
        Assert.Null(setup.Items);
        Assert.NotEmpty(setup.Organizations!); // 组织来自 factions
    }

    [Fact]
    public void Build_TurnpointsParsedToBeats()
    {
        // §2.7 源③：Turnpoints 确定性解析 → Beats（非法章号跳过）
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        plotlines[0].Turnpoints = "第1章：埋下伏笔；第2章：真相揭露；第99章：不存在的章";
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );
        var beats = setup.Plotlines![0].Beats;
        Assert.NotNull(beats);
        Assert.Equal(2, beats!.Count); // ch99 不在 chapters → 跳过
        Assert.Equal("plant", beats[0].Type);
        Assert.Equal("ch_1", beats[0].ChapterId);
        Assert.Equal("reveal", beats[1].Type);
    }

    [Fact]
    public void Build_EmptyTurnpoints_BeatsNull()
    {
        // 硬反例：Turnpoints 空 → Beats 缺省（不传空数组噪声）
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );
        Assert.Null(setup.Plotlines![0].Beats);
    }

    [Fact]
    public void Build_WorldRulesAndSceneItemIds_Reported()
    {
        // 5.3 L2 硬前置 + v3 契诃夫之枪数据源
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var worldRules = new List<WorldRule>
        {
            new()
            {
                Id = "wr1",
                Type = "first_birth_age_min",
                Value = 16,
                Unit = "岁",
            },
            new()
            {
                Id = "set1",
                EntryKind = WorldRuleEntryKind.Setting,
                Category = WorldRuleCategory.Social,
                Description = "宗门礼制",
                Content = "外门弟子不得御剑。",
            },
        };
        scenes[0].ItemIds = ["it1"];

        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline,
            worldRules: worldRules
        );
        var rule = Assert.Single(setup.WorldRules!);
        Assert.Equal("first_birth_age_min", rule.Type);
        Assert.Equal(16, rule.Value);
        Assert.Equal(["it1"], setup.Scenes![0].ItemIds);
    }

    [Fact]
    public void Build_SidePlotlineType_MapsToSub()
    {
        // 云端 dormancy 分档语义：Side ≡ sub
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        plotlines[0].Type = PlotlineType.Side;
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );
        Assert.Equal("sub", setup.Plotlines![0].Type);
    }

    [Fact]
    public void Build_RelationshipDirectionalSemantics()
    {
        // 方向语义：mentor/master → 有向；其余（含 family/friend）→ 无向（Directional=false），
        // 云端对称检查对无向边不判"反向缺失"。
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        relationships.Add(
            new Relationship
            {
                CharacterAId = "c1",
                CharacterBId = "c2",
                Type = RelationshipType.Mentor,
            }
        );
        relationships.Add(
            new Relationship
            {
                CharacterAId = "c1",
                CharacterBId = "c2",
                Type = RelationshipType.MasterServant,
            }
        );
        relationships.Add(
            new Relationship
            {
                CharacterAId = "c1",
                CharacterBId = "c2",
                Type = RelationshipType.FellowDisciple,
            }
        );
        // CHANGE_14：新增关系类型 → 云端规范 token（kebab）与方向语义
        foreach (
            var type in new[]
            {
                RelationshipType.ParentOf,
                RelationshipType.ChildOf,
                RelationshipType.Sibling,
                RelationshipType.Spouse,
                RelationshipType.Sworn,
                RelationshipType.Disciple,
                RelationshipType.Ally,
            }
        )
        {
            relationships.Add(
                new Relationship
                {
                    CharacterAId = "c1",
                    CharacterBId = "c2",
                    Type = type,
                }
            );
        }
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );
        Assert.False(setup.Relations!.Single(r => r.Type == "family").Directional);
        Assert.True(setup.Relations!.Single(r => r.Type == "mentor").Directional);
        Assert.True(setup.Relations!.Single(r => r.Type == "master").Directional);
        Assert.Equal("master", setup.Relations!.Single(r => r.Type == "master").Type);
        Assert.Equal("sect-mate", setup.Relations!.Single(r => r.Type == "sect-mate").Type);
        Assert.False(setup.Relations!.Single(r => r.Type == "sect-mate").Directional);
        // 亲缘/师承 → 有向（参与云端逆关系方向检查）；结拜/夫妻/盟友 → 无向（参与对称完备性检查）
        Assert.True(setup.Relations!.Single(r => r.Type == "parent-of").Directional);
        Assert.True(setup.Relations!.Single(r => r.Type == "child-of").Directional);
        Assert.True(setup.Relations!.Single(r => r.Type == "disciple").Directional);
        Assert.False(setup.Relations!.Single(r => r.Type == "sibling").Directional);
        Assert.False(setup.Relations!.Single(r => r.Type == "spouse").Directional);
        Assert.False(setup.Relations!.Single(r => r.Type == "sworn").Directional);
        Assert.False(setup.Relations!.Single(r => r.Type == "ally").Directional);
    }

    // ── entity-evolution-lifecycle §6.1 通道①（E4 镜像）──

    [Fact]
    public void Build_WithoutEvolutionData_OmitsNewFields()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        // 上传开关默认关：调用方不传演化数据
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline
        );

        Assert.Null(setup.EntityAppearances);
        Assert.Null(setup.EntityEvolutionEvents);
    }

    [Fact]
    public void Build_WithEvolutionData_MapsAppearancesAggregated()
    {
        var (novel, characters, factions, relationships, scenes, plotlines, chapters, timeline) =
            Sample();
        var appearances = new List<AppearanceRecord>
        {
            // 林默（c1）ch1 在场 + ch2 在场 → 聚合单条带两章
            new()
            {
                Id = "ap1",
                NovelId = novel.Id,
                EntityType = EntityType.Character,
                EntityId = "c1",
                ChapterId = "ch-guid-0001",
                Mode = AppearanceMode.Present,
                Source = AuditSource.System,
            },
            new()
            {
                Id = "ap2",
                NovelId = novel.Id,
                EntityType = EntityType.Character,
                EntityId = "c1",
                ChapterId = "ch-guid-0002",
                Mode = AppearanceMode.Mentioned,
                Source = AuditSource.System,
            },
            // 无法解析的章（不在 chapters 中）→ 跳过
            new()
            {
                Id = "ap3",
                NovelId = novel.Id,
                EntityType = EntityType.Location,
                EntityId = "unknown-chapter-loc",
                ChapterId = "ghost-chapter",
                Mode = AppearanceMode.Present,
                Source = AuditSource.System,
            },
        };
        var stateChanges = new List<EntityStateChange>
        {
            new()
            {
                Id = "ev1",
                NovelId = novel.Id,
                TargetEntityType = EntityType.Character,
                TargetId = "c1",
                ChapterId = "ch-guid-0002",
                Aspect = EvolutionAspect.Relationship,
                FromSummary = "敌视",
                ToSummary = "信任",
                EvidenceQuote = "「这条命给你了」",
                Summary = "化敌为友",
                Source = AuditSource.Ai,
            },
        };

        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timeline,
            appearances: appearances,
            stateChanges: stateChanges
        );

        // 出现摘要：c1 聚合为一条（ch_1/ch_2 升序，mode 对齐；ghost 章被跳过）
        var c1Appearance = Assert.Single(setup.EntityAppearances!.Where(a => a.EntityId == "c1"));
        Assert.Equal(["ch_1", "ch_2"], c1Appearance.ChapterOrders);
        Assert.Equal(["present", "mentioned"], c1Appearance.Modes);
        Assert.Equal("character", c1Appearance.EntityType);
        Assert.Empty(setup.EntityAppearances.Where(a => a.EntityId == "unknown-chapter-loc"));

        // 变化事件：直映射 + ch_{Order} 规范化 + 空串转 null
        var ev = Assert.Single(setup.EntityEvolutionEvents!);
        Assert.Equal("ch_2", ev.ChapterOrder);
        Assert.Equal("relationship", ev.Aspect);
        Assert.Equal("敌视", ev.From);
        Assert.Equal("信任", ev.To);
        Assert.Equal("「这条命给你了」", ev.Evidence);
        Assert.Equal("化敌为友", ev.Summary);
    }
}
