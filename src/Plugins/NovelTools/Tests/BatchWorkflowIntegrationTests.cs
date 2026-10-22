using Core.Data;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 端到端集成测试：模拟真实创作流程，验证多个 batch tool 协作。
/// 场景：创建小说 → 批量创建角色 → 批量建立关系 → 批量创建地点 → 验证数据一致性。
/// </summary>
public class BatchWorkflowIntegrationTests
{
    private readonly FakeNovelStore _store = new();

    [Fact]
    /// <summary>完整工作流：创建小说、角色、关系、地点并验证数据一致性。</summary>
    public async Task FullWorkflow_CreateNovel_Characters_Relationships_Locations()
    {
        // ── 步骤 1: 创建小说项目 ──
        var novel = new Novel
        {
            Id = "novel-1",
            Title = "测试小说",
            Genre = "奇幻",
            Description = "一个测试用的小说",
        };
        await _store.SaveNovel(novel);

        // ── 步骤 2: 批量创建 3 个角色 ──
        var charTool = new BatchCreateCharactersTool(_store);
        var charResult = await charTool.ExecuteAsync(
            "call-1",
            new BatchCreateCharactersParams
            {
                NovelId = "novel-1",
                Characters =
                [
                    new()
                    {
                        Name = "艾莉亚",
                        Personality = "勇敢果断",
                        Background = "北方贵族",
                    },
                    new()
                    {
                        Name = "琼恩",
                        Personality = "沉默坚毅",
                        Background = "私生子",
                    },
                    new()
                    {
                        Name = "丹妮",
                        Personality = "雄心勃勃",
                        Background = "流亡公主",
                    },
                ],
            },
            CancellationToken.None
        );

        Assert.False(charResult.IsError);
        var charDetails = (BatchCreateCharactersDetails)charResult.Details!;
        Assert.Equal(3, charDetails.CreatedCount);
        Assert.Equal(3, charDetails.CreatedIds.Length);

        // 验证角色确实存储到了 store
        var storedChars = await _store.GetCharacters("novel-1");
        Assert.Equal(3, storedChars.Count);
        Assert.Contains(storedChars, c => c.Name == "艾莉亚");
        Assert.Contains(storedChars, c => c.Name == "琼恩");
        Assert.Contains(storedChars, c => c.Name == "丹妮");

        // ── 步骤 3: 批量创建地点 ──
        var locTool = new BatchCreateLocationsTool(_store);
        var locResult = await locTool.ExecuteAsync(
            "call-2",
            new BatchCreateLocationsParams
            {
                NovelId = "novel-1",
                Locations =
                [
                    new() { Name = "临冬城", Description = "北方首府" },
                    new() { Name = "君临城", Description = "南方都城" },
                    new() { Name = "龙石岛", Description = "海外据点" },
                ],
            },
            CancellationToken.None
        );

        Assert.False(locResult.IsError);
        var locDetails = (BatchCreateLocationsDetails)locResult.Details!;
        Assert.Equal(3, locDetails.CreatedCount);

        var storedLocs = await _store.GetLocations("novel-1");
        Assert.Equal(3, storedLocs.Count);

        // ── 步骤 4: 批量创建组织 ──
        var facTool = new BatchCreateFactionsTool(_store);
        var facResult = await facTool.ExecuteAsync(
            "call-3",
            new BatchCreateFactionsParams
            {
                NovelId = "novel-1",
                Factions =
                [
                    new() { Name = "史塔克家族", Leader = "艾莉亚" },
                    new() { Name = "坦格利安家族", Leader = "丹妮" },
                ],
            },
            CancellationToken.None
        );

        Assert.False(facResult.IsError);
        Assert.Equal(2, ((BatchCreateFactionsDetails)facResult.Details!).CreatedCount);

        // ── 步骤 5: 批量创建角色关系 ──
        var charIds = storedChars.Select(c => c.Id).ToList();
        var relTool = new BatchCreateRelationshipsTool(_store);
        var relResult = await relTool.ExecuteAsync(
            "call-4",
            new BatchCreateRelationshipsParams
            {
                NovelId = "novel-1",
                Relationships =
                [
                    new()
                    {
                        CharacterAId = charIds[0],
                        CharacterBId = charIds[1],
                        Type = "family",
                        Description = "兄妹",
                    },
                    new()
                    {
                        CharacterAId = charIds[0],
                        CharacterBId = charIds[2],
                        Type = "rival",
                        Description = "竞争对手",
                    },
                    new()
                    {
                        CharacterAId = charIds[1],
                        CharacterBId = charIds[2],
                        Type = "friend",
                        Description = "盟友",
                    },
                ],
            },
            CancellationToken.None
        );

        Assert.False(relResult.IsError);
        Assert.Equal(3, ((BatchCreateRelationshipsDetails)relResult.Details!).CreatedCount);

        // ── 步骤 6: 批量创建情节线 ──
        var plotTool = new BatchCreatePlotlinesTool(_store);
        var plotResult = await plotTool.ExecuteAsync(
            "call-5",
            new BatchCreatePlotlinesParams
            {
                NovelId = "novel-1",
                Plotlines =
                [
                    new()
                    {
                        Name = "铁王座之争",
                        Type = "main",
                        PlannedRevealChapterId = "ch30",
                    },
                    new()
                    {
                        Name = "异鬼入侵",
                        Type = "hidden",
                        PlannedRevealChapterId = "ch25",
                    },
                    new()
                    {
                        Name = "龙之传承",
                        Type = "side",
                        PlannedRevealChapterId = "ch20",
                    },
                ],
            },
            CancellationToken.None
        );

        Assert.False(plotResult.IsError);
        Assert.Equal(3, ((BatchCreatePlotlinesDetails)plotResult.Details!).CreatedCount);

        var storedPlots = await _store.GetPlotlines("novel-1");
        Assert.Equal(3, storedPlots.Count);

        // ── 步骤 7: 批量创建道具 ──
        var itemTool = new BatchCreateItemsTool(_store);
        var itemResult = await itemTool.ExecuteAsync(
            "call-6",
            new BatchCreateItemsParams
            {
                NovelId = "novel-1",
                Items =
                [
                    new() { Name = "寒冰剑", Description = "史塔克家传宝剑" },
                    new() { Name = "龙晶", Description = "可杀死异鬼" },
                ],
            },
            CancellationToken.None
        );

        Assert.False(itemResult.IsError);
        Assert.Equal(2, ((BatchCreateItemsDetails)itemResult.Details!).CreatedCount);

        // ── 最终验证：数据完整性 ──
        Assert.Equal(3, await _store.GetCharacterCount("novel-1"));
        Assert.Equal(3, (await _store.GetCharacters("novel-1")).Count);
        Assert.Equal(3, (await _store.GetLocations("novel-1")).Count);
        Assert.Equal(2, (await _store.GetFactions("novel-1")).Count);
        Assert.Equal(3, (await _store.GetPlotlines("novel-1")).Count);
        Assert.Equal(2, (await _store.GetItems("novel-1")).Count);
    }

    [Fact]
    /// <summary>部分失败不应中断工作流。</summary>
    public async Task PartialFailure_DoesNotBreakWorkflow()
    {
        // 场景：批量创建角色时部分名称为空，但其他角色仍应成功创建
        var tool = new BatchCreateCharactersTool(_store);
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateCharactersParams
            {
                NovelId = "novel-1",
                Characters =
                [
                    new() { Name = "英雄" },
                    new() { Name = "" }, // 空名称，应跳过
                    new() { Name = "反派" },
                    new() { Name = "   " }, // 空白名称，应跳过
                ],
            },
            CancellationToken.None
        );

        // 工具不应报错
        Assert.False(result.IsError);
        var details = (BatchCreateCharactersDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(2, details.FailedCount);

        // 验证只有 2 个角色被存储
        var stored = await _store.GetCharacters("novel-1");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, c => c.Name == "英雄");
        Assert.Contains(stored, c => c.Name == "反派");
    }

    [Fact]
    /// <summary>缺少 ID 时应记录失败。</summary>
    public async Task BatchCreateRelationships_WithMissingIds_RecordsFailure()
    {
        var tool = new BatchCreateRelationshipsTool(_store);
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateRelationshipsParams
            {
                NovelId = "novel-1",
                Relationships =
                [
                    new() { CharacterAId = "char-1", CharacterBId = "char-2" },
                    new() { CharacterAId = "", CharacterBId = "char-3" }, // 缺少 A
                    new() { CharacterAId = "char-1", CharacterBId = "" }, // 缺少 B
                ],
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var details = (BatchCreateRelationshipsDetails)result.Details!;
        Assert.Equal(1, details.CreatedCount);
        Assert.Equal(2, details.FailedCount);
    }
}
