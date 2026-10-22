using Core.Data;
using Core.Shared;
using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

/// <summary>
/// recall_scene 测试：场景按参与角色/情节线/地点/场景类型/章节范围过滤 + hybrid 召回。
/// </summary>
public class RecallSceneToolTests : SemanticRetrievalToolTestBase
{
    private readonly FakeNovelStore _novelStore = new();
    private readonly RecallSceneTool _tool;

    public RecallSceneToolTests()
    {
        _tool = new RecallSceneTool(Kernel, new PluginStore(_novelStore));
    }

    [Fact]
    public async Task ExecuteAsync_NoNovelId_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallSceneParams { NovelId = "", Query = "决战" },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_NoMatches_ReturnsNotFound()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallSceneParams { NovelId = "novel1", Query = "不存在的内容" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("未找到", text);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersByParticipantCharacter()
    {
        var alice = new Character
        {
            Id = "ch_alice",
            NovelId = "novel1",
            Name = "林晚",
        };
        var bob = new Character
        {
            Id = "ch_bob",
            NovelId = "novel1",
            Name = "沈一",
        };
        _novelStore.AddCharacter(alice).AddCharacter(bob);

        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene1",
                NovelId = "novel1",
                Title = "雨夜对峙",
                ParticipantIds = ["ch_alice"],
            }
        );
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene2",
                NovelId = "novel1",
                Title = "茶馆闲聊",
                ParticipantIds = ["ch_bob"],
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallSceneParams { NovelId = "novel1", CharacterNames = "林晚" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("雨夜对峙", text);
        Assert.DoesNotContain("茶馆闲聊", text);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCharacterName_FailsWithoutWideningRecall()
    {
        // 未索引任何角色：名称过滤请求落空 → 必须失败，不得扩大召回
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallSceneParams { NovelId = "novel1", CharacterNames = "不存在的人" },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersByChapterRange()
    {
        _novelStore.AddChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "novel1",
                Order = 1,
            }
        );
        _novelStore.AddChapter(
            new Chapter
            {
                Id = "ch10",
                NovelId = "novel1",
                Order = 10,
            }
        );
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene1",
                NovelId = "novel1",
                Title = "第一幕",
                ChapterId = "ch1",
            }
        );
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene2",
                NovelId = "novel1",
                Title = "第十幕",
                ChapterId = "ch10",
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallSceneParams
            {
                NovelId = "novel1",
                ChapterMin = 1,
                ChapterMax = 5,
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("第一幕", text);
        Assert.DoesNotContain("第十幕", text);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersBySceneType()
    {
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene1",
                NovelId = "novel1",
                Title = "打斗",
                SceneType = SceneType.Action,
            }
        );
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene2",
                NovelId = "novel1",
                Title = "谈心",
                SceneType = SceneType.Dialogue,
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallSceneParams { NovelId = "novel1", SceneTypes = "Action" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("打斗", text);
        Assert.DoesNotContain("谈心", text);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersByPlotlineAndLocation()
    {
        var plotline = new Plotline
        {
            Id = "pl1",
            NovelId = "novel1",
            Name = "夺宝主线",
        };
        var location = new Location
        {
            Id = "loc1",
            NovelId = "novel1",
            Name = "青云山",
        };
        _novelStore.AddPlotline(plotline).AddLocation(location);

        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene1",
                NovelId = "novel1",
                Title = "山上夺宝",
                PlotlineIds = ["pl1"],
                LocationId = "loc1",
            }
        );
        _novelStore.AddScene(
            new SceneDefinition
            {
                Id = "scene2",
                NovelId = "novel1",
                Title = "城中闲逛",
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallSceneParams
            {
                NovelId = "novel1",
                PlotlineNames = "夺宝主线",
                LocationName = "青云山",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("山上夺宝", text);
        Assert.DoesNotContain("城中闲逛", text);
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
