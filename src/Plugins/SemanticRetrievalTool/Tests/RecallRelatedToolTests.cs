using Core.Data;
using Core.Shared;
using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

/// <summary>
/// recall_related 测试：从事实源字段构建精确关联（关系/参与场景/成员组织），不建关系表。
/// </summary>
public class RecallRelatedToolTests : SemanticRetrievalToolTestBase
{
    private readonly FakeNovelStore _novelStore = new();
    private readonly RecallRelatedTool _tool;

    public RecallRelatedToolTests()
    {
        _tool = new RecallRelatedTool(Kernel, new PluginStore(_novelStore));
    }

    [Fact]
    public async Task ExecuteAsync_MissingEntityInfo_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallRelatedParams { NovelId = "novel1", EntityType = "character" },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownEntity_ReportsNotFound()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallRelatedParams
            {
                NovelId = "novel1",
                EntityType = "character",
                EntityName = "不存在的人",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("未找到实体", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_CharacterRelationshipsAndScenes()
    {
        var alice = new Character
        {
            Id = "ch1",
            NovelId = "novel1",
            Name = "林晚",
        };
        var bob = new Character
        {
            Id = "ch2",
            NovelId = "novel1",
            Name = "沈一",
        };
        _novelStore.AddCharacter(alice).AddCharacter(bob);
        _novelStore.AddRelationship(
            new Relationship
            {
                Id = "rel1",
                NovelId = "novel1",
                CharacterAId = "ch1",
                CharacterBId = "ch2",
                Type = RelationshipType.Rival,
                Strength = RelationshipStrength.Strong,
            }
        );
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene1",
                NovelId = "novel1",
                Title = "雨夜对峙",
                ParticipantIds = ["ch1", "ch2"],
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallRelatedParams
            {
                NovelId = "novel1",
                EntityType = "character",
                EntityName = "林晚",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("林晚", text);
        Assert.Contains("沈一", text);
        Assert.Contains("雨夜对峙", text);
    }

    [Fact]
    public async Task ExecuteAsync_PlotlineAdvancingScenes()
    {
        _novelStore.AddPlotline(
            new Plotline
            {
                Id = "pl1",
                NovelId = "novel1",
                Name = "夺宝主线",
            }
        );
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene1",
                NovelId = "novel1",
                Title = "山上夺宝",
                PlotlineIds = ["pl1"],
            }
        );
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene2",
                NovelId = "novel1",
                Title = "城中闲逛",
                PlotlineIds = [],
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallRelatedParams
            {
                NovelId = "novel1",
                EntityType = "plotline",
                EntityName = "夺宝主线",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("山上夺宝", text);
        Assert.DoesNotContain("城中闲逛", text);
    }

    [Fact]
    public async Task ExecuteAsync_SceneParticipantsAndLocation()
    {
        var loc = new Location
        {
            Id = "loc1",
            NovelId = "novel1",
            Name = "青云山",
        };
        var alice = new Character
        {
            Id = "ch1",
            NovelId = "novel1",
            Name = "林晚",
        };
        _novelStore.AddLocation(loc).AddCharacter(alice);
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene1",
                NovelId = "novel1",
                Title = "雨夜对峙",
                ParticipantIds = ["ch1"],
                LocationId = "loc1",
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallRelatedParams
            {
                NovelId = "novel1",
                EntityType = "scene",
                EntityName = "雨夜对峙",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("林晚", text);
        Assert.Contains("青云山", text);
    }

    [Fact]
    public async Task ExecuteAsync_FactionMembers()
    {
        var faction = new Faction
        {
            Id = "fac1",
            NovelId = "novel1",
            Name = "天剑宗",
            Members = ["ch1", "ch2"],
        };
        _novelStore.AddFaction(faction);
        _novelStore.AddCharacter(
            new Character
            {
                Id = "ch1",
                NovelId = "novel1",
                Name = "林晚",
            }
        );
        _novelStore.AddCharacter(
            new Character
            {
                Id = "ch2",
                NovelId = "novel1",
                Name = "沈一",
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallRelatedParams
            {
                NovelId = "novel1",
                EntityType = "faction",
                EntityName = "天剑宗",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("林晚", text);
        Assert.Contains("沈一", text);
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
