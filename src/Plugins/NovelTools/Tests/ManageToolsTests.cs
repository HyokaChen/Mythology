using System.Text.Json;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 公共管理工具测试：ManageNovelTool / ManageCharacterTool / ManageFactionTool / ManageItemTool / ManageLocationTool / ManagePlotlineTool / ManageRelationshipTool / ManageSceneTool / BatchCreateTimelineEventsTool / WorldBuildingTools / NovelManagementTools / CharacterTools
/// </summary>
public class ManageToolsTests
{
    private static FakeNovelStore CreateStoreWithNovel()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(
            new Novel
            {
                Id = "n1",
                Title = "原标题",
                Genre = "奇幻",
                Description = "描述",
                WritingStyle = "简洁",
            }
        );
        return store;
    }

    // ═══════════════════════════════════════════════════════
    //  ManageNovelTool
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task ManageNovelTool_Create_ValidatesRequiredFields()
    {
        var store = new FakeNovelStore();
        var tool = new ManageNovelTool(store);

        var args = JsonDocument.Parse("""{"action":"create"}""").RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ManageNovelTool_Create_SucceedsWithValidData()
    {
        var store = new FakeNovelStore();
        var tool = new ManageNovelTool(store);

        var args = JsonDocument
            .Parse("""{"action":"create","title":"新小说","genre":"玄幻","description":"描述"}""")
            .RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ManageNovelTool_Get_ReturnsNovelDetails()
    {
        var store = CreateStoreWithNovel();
        var tool = new ManageNovelTool(store);

        var args = JsonDocument.Parse("""{"action":"get","novel-id":"n1"}""").RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ManageNovelTool_Update_WritesOutlineAndKeepsUntouchedFields()
    {
        // 总纲（大纲一层）此前只有 UI 能写：模型必须能 update 写、get_outline 读
        var store = CreateStoreWithNovel();
        var tool = new ManageNovelTool(store);

        var args = JsonDocument
            .Parse(
                """{"action":"update","novel-id":"n1","outline":"主角从边城走向王都，终局与旧友反目"}"""
            )
            .RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var novel = await store.GetNovel("n1");
        Assert.Equal("主角从边城走向王都，终局与旧友反目", novel!.Outline);
        // 未传字段不得被清空
        Assert.Equal("原标题", novel.Title);
        Assert.Equal("简洁", novel.WritingStyle);
    }

    [Fact]
    public async Task ManageNovelTool_Update_WithoutFields_IsRejected()
    {
        var store = CreateStoreWithNovel();
        var tool = new ManageNovelTool(store);

        var args = JsonDocument.Parse("""{"action":"update","novel-id":"n1"}""").RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("", (await store.GetNovel("n1"))!.Outline); // 误调用不得改动总纲
    }

    [Fact]
    public async Task ManageNovelTool_Update_RequiresNovel()
    {
        var store = new FakeNovelStore();
        var tool = new ManageNovelTool(store);

        var args = JsonDocument
            .Parse("""{"action":"update","novel-id":"missing","outline":"x"}""")
            .RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ManageNovelTool_Delete_RemovesNovel()
    {
        var store = CreateStoreWithNovel();
        var tool = new ManageNovelTool(store);

        var args = JsonDocument.Parse("""{"action":"delete","novel-id":"n1"}""").RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var got = await store.GetNovel("n1");
        Assert.Null(got);
    }

    // ═══════════════════════════════════════════════════════
    //  ManageCharacterTool
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ManageCharacterTool_Create_AddsCharacter()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        var tool = new ManageCharacterTool(store);

        var args = JsonDocument
            .Parse(
                """{"action":"create","novel-id":"n1","name":"林默","role":"protagonist","gender":"男"}"""
            )
            .RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        if (result.IsError)
        {
            var errorText = ToolDispatcher.ExtractText(result.Content);
            Assert.Fail($"Tool returned error: {errorText}");
        }
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ManageFactionTool_Create_AddsFaction()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        var tool = new ManageFactionTool(store);

        var args = JsonDocument
            .Parse("""{"action":"create","novel-id":"n1","name":"青云宗","type":"sect"}""")
            .RootElement;
        var result = await new ManageFactionTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ManageLocationTool_Create_AddsLocation()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        var tool = new ManageLocationTool(store);

        var args = JsonDocument
            .Parse("""{"action":"create","novel-id":"n1","name":"青云峰","type":"wilderness"}""")
            .RootElement;
        var result = await new ManageLocationTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ManageItemTool_Create_AddsItem()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        var tool = new ManageItemTool(store);

        var args = JsonDocument
            .Parse("""{"action":"create","novel-id":"n1","name":"青云剑","type":"weapon"}""")
            .RootElement;
        var result = await new ManageItemTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ManageRelationshipTool_Create_AddsRelationship()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "A",
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "c2",
                NovelId = "n1",
                Name = "B",
            }
        );
        var tool = new ManageRelationshipTool(store);

        var args = JsonDocument
            .Parse(
                """{"action":"create","novel-id":"n1","character-a-id":"c1","character-b-id":"c2","type":"friend","strength":"strong"}"""
            )
            .RootElement;
        var result = await new ManageRelationshipTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ManageSceneTool_Create_AddsScene()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        var tool = new ManageSceneTool(store);

        var args = JsonDocument
            .Parse(
                """{"action":"create","novel-id":"n1","title":"入门试炼","order":1,"chapter-id":"c1","time-description":"清晨"}"""
            )
            .RootElement;
        var result = await new ManageSceneTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ManageSceneTool_Update_ResolvesSceneByOrder()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "scene-real-1",
                NovelId = "n1",
                ChapterId = "ch1",
                Title = "旧标题",
                Order = 12,
                Conflict = "旧冲突",
            }
        );

        var args = JsonDocument
            .Parse(
                """{"action":"update","novel-id":"n1","chapter-id":"ch1","scene-id":"12","title":"新标题","conflict":"新冲突"}"""
            )
            .RootElement;
        var result = await new ManageSceneTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var updated = await store.GetScene("scene-real-1");
        Assert.NotNull(updated);
        Assert.Equal("新标题", updated.Title);
        Assert.Equal("新冲突", updated.Conflict);
    }

    [Fact]
    public async Task ManageSceneTool_Get_ResolvesSceneByOrder()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "scene-real-2",
                NovelId = "n1",
                ChapterId = "ch1",
                Title = "雨夜对峙",
                Order = 3,
            }
        );

        var args = JsonDocument
            .Parse("""{"action":"get","novel-id":"n1","chapter-id":"ch1","scene-id":"3"}""")
            .RootElement;
        var result = await new ManageSceneTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Contains("雨夜对峙", ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task ManageSceneTool_Delete_ResolvesSceneByOrder()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "scene-real-3",
                NovelId = "n1",
                ChapterId = "ch1",
                Title = "待删除",
                Order = 7,
            }
        );

        var args = JsonDocument
            .Parse("""{"action":"delete","novel-id":"n1","chapter-id":"ch1","scene-id":"7"}""")
            .RootElement;
        var result = await new ManageSceneTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Null(await store.GetScene("scene-real-3"));
    }

    [Fact]
    public async Task ManageSceneTool_List_IncludesSceneId()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "scene-real-4",
                NovelId = "n1",
                ChapterId = "ch1",
                Title = "悬崖对峙",
                Order = 12,
                Conflict = "双方僵持",
            }
        );

        var args = JsonDocument
            .Parse("""{"action":"list","novel-id":"n1","chapter-id":"ch1"}""")
            .RootElement;
        var result = await new ManageSceneTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var text = ((TextContent)result.Content[0]).Text;
        Assert.Contains("12. 悬崖对峙", text);
        Assert.Contains("(ID: scene-real-4)", text);
    }

    [Fact]
    public async Task ManageCharacterTool_Update_ResolvesByName()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveCharacter(
            new Character
            {
                Id = "char-1",
                NovelId = "n1",
                Name = "千山秋依",
            }
        );

        var args = JsonDocument
            .Parse(
                """{"action":"update","novel-id":"n1","character-id":"千山秋依","personality":"外冷内热"}"""
            )
            .RootElement;
        var result = await new ManageCharacterTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var updated = await store.GetCharacter("char-1");
        Assert.NotNull(updated);
        Assert.Equal("外冷内热", updated.Personality);
    }

    [Fact]
    public async Task ManagePlotlineTool_Update_ResolvesByName()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SavePlotline(
            new Plotline
            {
                Id = "pl-1",
                NovelId = "n1",
                Name = "夏夜萤火主线",
            }
        );

        var args = JsonDocument
            .Parse(
                """{"action":"update","novel-id":"n1","plotline-id":"夏夜萤火主线","description":"萤火虫变异之谜"}"""
            )
            .RootElement;
        var result = await new ManagePlotlineTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var updated = await store.GetPlotline("pl-1");
        Assert.NotNull(updated);
        Assert.Equal("萤火虫变异之谜", updated.Description);
    }

    [Fact]
    public async Task ManageLocationTool_Get_ResolvesByName()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveLocation(
            new Location
            {
                Id = "loc-1",
                NovelId = "n1",
                Name = "废弃湿地",
            }
        );

        var args = JsonDocument
            .Parse("""{"action":"get","novel-id":"n1","location-id":"废弃湿地"}""")
            .RootElement;
        var result = await new ManageLocationTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Contains("废弃湿地", ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task ManageRelationshipTool_List_ReturnsRelationshipIds()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveCharacter(
            new Character
            {
                Id = "char-a",
                NovelId = "n1",
                Name = "林晚",
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "char-b",
                NovelId = "n1",
                Name = "陆离",
            }
        );
        await store.SaveRelationship(
            new Relationship
            {
                Id = "rel-1",
                NovelId = "n1",
                CharacterAId = "char-a",
                CharacterBId = "char-b",
                Type = RelationshipType.Mentor,
            }
        );

        var args = JsonDocument.Parse("""{"action":"list","novel-id":"n1"}""").RootElement;
        var result = await new ManageRelationshipTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var text = ((TextContent)result.Content[0]).Text;
        Assert.Contains("林晚", text);
        Assert.Contains("陆离", text);
        Assert.Contains("(ID: rel-1)", text);
    }

    [Fact]
    public async Task AdvancePlotlineTool_ResolvesPlotlineByName()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SavePlotline(
            new Plotline
            {
                Id = "pl-1",
                NovelId = "n1",
                Name = "夏夜萤火主线",
            }
        );

        // 不传 novel_id：名称匹配走「扫描全部小说」路径
        var args = JsonDocument
            .Parse("""{"plotline-id":"夏夜萤火主线","progress-note":"果果在废墟看到蓝光"}""")
            .RootElement;
        var result = await new AdvancePlotlineTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var updated = await store.GetPlotline("pl-1");
        Assert.NotNull(updated);
        Assert.Contains("果果在废墟看到蓝光", updated.Description);
    }

    [Fact]
    public async Task TransferItemTool_ResolvesItemAndOwnerByName()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        await store.SaveCharacter(
            new Character
            {
                Id = "char-a",
                NovelId = "n1",
                Name = "林晚",
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "char-b",
                NovelId = "n1",
                Name = "陆离",
            }
        );
        await store.SaveItem(
            new Item
            {
                Id = "item-1",
                NovelId = "n1",
                Name = "玄铁剑",
                OwnerId = "char-a",
            }
        );

        // 道具与新持有者都传名称，且不传 novel_id（扫描全部小说路径）
        var args = JsonDocument.Parse("""{"item-id":"玄铁剑","new-owner-id":"陆离"}""").RootElement;
        var result = await new TransferItemTool(store).ExecuteAsync(
            "call1",
            args,
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var updated = await store.GetItem("item-1");
        Assert.NotNull(updated);
        Assert.Equal("char-b", updated.OwnerId); // 存的是真实 ID
        Assert.Contains("林晚", ((TextContent)result.Content[0]).Text);
        Assert.Contains("陆离", ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task BatchCreateTimelineEventsTool_Create_AddsEvent()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "T" });
        var tool = new BatchCreateTimelineEventsTool(store);

        var args = JsonDocument
            .Parse(
                """{"action":"create_timeline_events","novel-id":"n1","events":[{"title":"主角出生","description":"在月圆之夜，主角诞生"}]}"""
            )
            .RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.False(result.IsError);
    }
}
