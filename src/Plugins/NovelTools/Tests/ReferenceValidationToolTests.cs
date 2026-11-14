using Core.Data;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 写工具的引用闸门：名称/序号/中文数字必须解析成真实 ID；解析失败不得落盘。
/// </summary>
public sealed class ReferenceValidationToolTests
{
    private static async Task<FakeNovelStore> SeedAsync()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(
            new Novel
            {
                Id = "n1",
                NovelId = "n1",
                Title = "书名",
            }
        );
        await store.SaveVolume(
            new Volume
            {
                Id = "v1",
                NovelId = "n1",
                Name = "第一卷",
                Order = 1,
            }
        );
        await store.SaveVolume(
            new Volume
            {
                Id = "v2",
                NovelId = "n1",
                Name = "第二卷",
                Order = 2,
            }
        );
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "第一章",
                Order = 1,
                VolumeId = "v1",
            }
        );
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch2",
                NovelId = "n1",
                Title = "第二章",
                Order = 2,
                VolumeId = "v1",
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "小明",
                FactionId = "f1",
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "c2",
                NovelId = "n1",
                Name = "小红",
            }
        );
        await store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = "n1",
                Name = "青云宗",
                Members = ["c1"],
            }
        );
        await store.SaveFaction(
            new Faction
            {
                Id = "f2",
                NovelId = "n1",
                Name = "血刀门",
            }
        );
        await store.SaveLocation(
            new Location
            {
                Id = "l1",
                NovelId = "n1",
                Name = "大陆",
            }
        );
        await store.SaveLocation(
            new Location
            {
                Id = "l2",
                NovelId = "n1",
                Name = "皇城",
                ParentId = "l1",
            }
        );
        await store.SaveItem(
            new Item
            {
                Id = "i1",
                NovelId = "n1",
                Name = "宝剑",
                OwnerId = "c1",
            }
        );
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "sc1",
                NovelId = "n1",
                ChapterId = "ch1",
                Title = "初遇",
                Order = 1,
                ParticipantIds = ["c1"],
                LocationId = "l1",
            }
        );
        await store.SavePlotline(
            new Plotline
            {
                Id = "p1",
                NovelId = "n1",
                Name = "主线",
            }
        );
        await store.SaveRelationship(
            new Relationship
            {
                Id = "r1",
                NovelId = "n1",
                CharacterAId = "c1",
                CharacterBId = "c2",
                Type = RelationshipType.Friend,
            }
        );
        return store;
    }

    private static EntityEditItem Item(
        string entityType,
        string entityId,
        params EntityFieldPatch[] fields
    ) =>
        new()
        {
            EntityType = entityType,
            EntityId = entityId,
            Fields = [.. fields],
        };

    [Fact]
    public async Task EditEntities_DanglingReferences_DoNotMutateOrSave()
    {
        var store = await SeedAsync();
        var tool = new EditEntitiesTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new EditEntitiesParams
            {
                NovelId = "n1",
                Items =
                [
                    Item(
                        "character",
                        "c1",
                        new EntityFieldPatch { Field = "faction-id", Value = "不存在" }
                    ),
                    Item(
                        "location",
                        "l2",
                        new EntityFieldPatch { Field = "parent-id", Value = "不存在" }
                    ),
                    Item(
                        "scene",
                        "sc1",
                        new EntityFieldPatch { Field = "participant-ids", Values = ["不存在"] }
                    ),
                    Item(
                        "chapter",
                        "ch1",
                        new EntityFieldPatch { Field = "volume-id", Value = "第99卷" }
                    ),
                    Item(
                        "relationship",
                        "r1",
                        new EntityFieldPatch { Field = "character-b-id", Value = "c1" }
                    ),
                ],
            },
            CancellationToken.None
        );

        var details = Assert.IsType<EditEntitiesDetails>(result.Details);
        Assert.Equal(0, details.UpdatedCount);
        Assert.Equal(5, details.FailedCount);
        Assert.Equal("f1", (await store.GetCharacter("c1"))!.FactionId);
        Assert.Equal("l1", (await store.GetLocation("l2"))!.ParentId);
        Assert.Equal(["c1"], (await store.GetScene("sc1"))!.ParticipantIds);
        Assert.Equal("v1", (await store.GetChapter("ch1"))!.VolumeId);
        Assert.Equal("c2", (await store.GetRelationship("r1"))!.CharacterBId);
    }

    [Fact]
    public async Task EditEntities_ResolvesNamesOrdersAndCanonicalizesIds()
    {
        var store = await SeedAsync();
        var tool = new EditEntitiesTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new EditEntitiesParams
            {
                NovelId = "n1",
                Items =
                [
                    Item(
                        "character",
                        "小明",
                        new EntityFieldPatch { Field = "faction-id", Value = "血刀门" }
                    ),
                    Item(
                        "chapter",
                        "第2章",
                        new EntityFieldPatch { Field = "volume-id", Value = "第二卷" }
                    ),
                    Item(
                        "scene",
                        "初遇",
                        new EntityFieldPatch
                        {
                            Field = "participant-ids",
                            Values = ["小明", "小红"],
                        }
                    ),
                    Item(
                        "location",
                        "皇城",
                        new EntityFieldPatch { Field = "parent-id", Value = "大陆" }
                    ),
                ],
            },
            CancellationToken.None
        );

        var details = Assert.IsType<EditEntitiesDetails>(result.Details);
        Assert.Equal(4, details.UpdatedCount);
        Assert.Equal(0, details.FailedCount);
        Assert.Equal("f2", (await store.GetCharacter("c1"))!.FactionId);
        Assert.Equal("v2", (await store.GetChapter("ch2"))!.VolumeId);
        Assert.Equal(["c1", "c2"], (await store.GetScene("sc1"))!.ParticipantIds);
        Assert.Equal("l1", (await store.GetLocation("l2"))!.ParentId);
    }

    [Fact]
    public async Task EditEntities_InvalidEnumAndSelectValues_DoNotMutate()
    {
        var store = await SeedAsync();
        var tool = new EditEntitiesTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new EditEntitiesParams
            {
                NovelId = "n1",
                Items =
                [
                    Item(
                        "character",
                        "c1",
                        new EntityFieldPatch { Field = "status", Value = "99" }
                    ),
                    Item(
                        "character",
                        "c1",
                        new EntityFieldPatch { Field = "gender", Value = "不是性别" }
                    ),
                    Item(
                        "novel",
                        "书名",
                        new EntityFieldPatch { Field = "auto-enhance", Value = "不是模式" }
                    ),
                ],
            },
            CancellationToken.None
        );

        var details = Assert.IsType<EditEntitiesDetails>(result.Details);
        Assert.Equal(0, details.UpdatedCount);
        Assert.Equal(3, details.FailedCount);
        var character = await store.GetCharacter("c1");
        Assert.Equal(CharacterStatus.Active, character!.Status);
        Assert.Equal("", character.Gender);
        Assert.Equal("semi", (await store.GetNovel("n1"))!.AutoEnhance);
    }

    [Fact]
    public async Task ManageScene_CreateDuplicateOrder_DoesNotWrite()
    {
        var store = await SeedAsync();
        var tool = new ManageSceneTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new ManageSceneParams
            {
                Action = "create",
                NovelId = "n1",
                ChapterId = "第1章",
                Title = "第二场",
                Order = 1,
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Single(await store.GetScenesByChapter("ch1"));
    }

    [Fact]
    public async Task ManageVolume_DuplicateOrderOrName_DoesNotWrite()
    {
        var store = await SeedAsync();
        var tool = new ManageVolumeTool(store);

        var order = await tool.ExecuteAsync(
            "call1",
            new ManageVolumeParams
            {
                Action = "create",
                NovelId = "n1",
                Name = "第三卷",
                Order = 1,
            },
            CancellationToken.None
        );
        Assert.True(order.IsError);

        var name = await tool.ExecuteAsync(
            "call2",
            new ManageVolumeParams
            {
                Action = "create",
                NovelId = "n1",
                Name = "第一卷",
                Order = 3,
            },
            CancellationToken.None
        );
        Assert.True(name.IsError);
        Assert.Equal(2, (await store.GetVolumes("n1")).Count);
    }

    [Fact]
    public async Task ManageRelationship_DuplicatePair_DoesNotWrite()
    {
        var store = await SeedAsync();
        var tool = new ManageRelationshipTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new ManageRelationshipParams
            {
                Action = "create",
                NovelId = "n1",
                CharacterAId = "小红",
                CharacterBId = "小明",
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Single(await store.GetRelationshipsByNovel("n1"));
    }

    [Fact]
    public async Task AdvancePlotline_UnknownChapter_DoesNotWrite()
    {
        var store = await SeedAsync();
        var tool = new AdvancePlotlineTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new AdvancePlotlineParams
            {
                NovelId = "n1",
                PlotlineId = "p1",
                ResolvedAtChapterId = "第99章",
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Null((await store.GetPlotline("p1"))!.ResolvedAtChapterId);
    }

    [Fact]
    public async Task TransferItem_UnknownOwner_DoesNotWrite()
    {
        var store = await SeedAsync();
        var tool = new TransferItemTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new TransferItemParams
            {
                NovelId = "n1",
                ItemId = "宝剑",
                NewOwnerId = "不存在",
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Equal("c1", (await store.GetItem("i1"))!.OwnerId);
    }

    [Fact]
    public async Task RecordAppearance_CanonicalizesNameChapterAndScene()
    {
        var store = await SeedAsync();
        var tool = new RecordAppearanceTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new RecordAppearanceParams
            {
                NovelId = "n1",
                EntityId = "小明",
                EntityType = EntityType.Character,
                ChapterId = "第一章",
                SceneIds = ["初遇"],
                Evidence = "他走进来",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var record = Assert.Single(await store.GetAppearancesByChapter("n1", "ch1"));
        Assert.Equal("c1", record.EntityId);
        Assert.Equal("ch1", record.ChapterId);
        Assert.Equal(["sc1"], record.SceneIds);
        Assert.DoesNotContain("小明", record.Id);
    }

    [Fact]
    public async Task RecordEvolution_CanonicalizesEffectTarget()
    {
        var store = await SeedAsync();
        var tool = new RecordEvolutionTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new RecordEvolutionParams
            {
                NovelId = "n1",
                ChapterId = "第1章",
                TargetId = "小明",
                TargetType = EntityType.Character,
                Aspect = EvolutionAspect.Status,
                Effects =
                [
                    new StateEffectParams
                    {
                        TargetType = EntityType.Character,
                        TargetId = "小红",
                        Field = "status",
                        Operation = StateEffectOperation.Set,
                        Value = "Deceased",
                    },
                ],
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var change = Assert.Single(await store.GetEntityStateChanges("n1"));
        Assert.Equal("c1", change.TargetId);
        Assert.Equal("c2", change.Effects[0].TargetId);
        Assert.Equal("ch1", change.ChapterId);
    }

    [Fact]
    public async Task PlanSceneDeltas_UnknownTarget_DoesNotWrite()
    {
        var store = await SeedAsync();
        var tool = new PlanSceneDeltasTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new PlanSceneDeltasParams
            {
                NovelId = "n1",
                SceneId = "初遇",
                Deltas =
                [
                    new PlannedDeltaParams
                    {
                        Kind = "state-change",
                        TargetEntityType = "character",
                        TargetId = "不存在",
                        Summary = "重伤",
                    },
                ],
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Empty((await store.GetScene("sc1"))!.PlannedDeltas);
    }

    [Fact]
    public async Task ManageLocation_SelfParentAndCycle_DoNotWrite()
    {
        var store = await SeedAsync();
        var tool = new ManageLocationTool(store);

        var self = await tool.ExecuteAsync(
            "call1",
            new ManageLocationParams
            {
                Action = "update",
                NovelId = "n1",
                LocationId = "l2",
                ParentId = "皇城",
            },
            CancellationToken.None
        );
        Assert.True(self.IsError);

        var cycle = await tool.ExecuteAsync(
            "call2",
            new ManageLocationParams
            {
                Action = "update",
                NovelId = "n1",
                LocationId = "大陆",
                Name = "新大陆",
                ParentId = "皇城",
            },
            CancellationToken.None
        );
        Assert.True(cycle.IsError);
        Assert.Equal("大陆", (await store.GetLocation("l1"))!.Name);
        Assert.Null((await store.GetLocation("l1"))!.ParentId);
        Assert.Equal("l1", (await store.GetLocation("l2"))!.ParentId);
    }

    [Fact]
    public async Task CreateOutline_DuplicateTitles_DoesNotCreate()
    {
        var store = await SeedAsync();
        var tool = new CreateOutlineTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new CreateOutlineParams
            {
                NovelId = "n1",
                VolumeId = "v1",
                ChapterTitles = ["第三章", "第三章"],
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Equal(2, (await store.GetChapters("n1")).Count);
    }

    [Fact]
    public async Task WriteChapter_ResolvesVolumeNameAndRejectsDuplicateOrder()
    {
        var store = await SeedAsync();
        var tool = new WriteChapterTool(store);

        var created = await tool.ExecuteAsync(
            "call1",
            new WriteChapterParams
            {
                NovelId = "n1",
                Title = "第三章",
                Order = 3,
                VolumeId = "第二卷",
                Content = "正文",
            },
            CancellationToken.None
        );
        Assert.False(created.IsError);
        Assert.Equal("v2", (await store.GetChapterByOrder("n1", 3))!.VolumeId);

        var duplicate = await tool.ExecuteAsync(
            "call2",
            new WriteChapterParams
            {
                NovelId = "n1",
                Title = "第四章",
                Order = 3,
                VolumeId = "v2",
                Content = "正文",
            },
            CancellationToken.None
        );
        Assert.True(duplicate.IsError);
        Assert.Equal(3, (await store.GetChapters("n1")).Count);
    }

    [Fact]
    public async Task WriteChapter_UnknownChapterId_DoesNotCreate()
    {
        var store = await SeedAsync();
        var tool = new WriteChapterTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new WriteChapterParams
            {
                NovelId = "n1",
                ChapterId = "不存在",
                Title = "新章",
                Order = 3,
                VolumeId = "v1",
                Content = "正文",
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Equal(2, (await store.GetChapters("n1")).Count);
    }

    [Fact]
    public async Task ReorderChapters_RejectsUnknownDuplicateAndIncompleteLists()
    {
        var store = await SeedAsync();
        var tool = new ReorderChaptersTool(store);

        var unknown = await tool.ExecuteAsync(
            "call1",
            new ReorderChaptersParams { NovelId = "n1", ChapterIds = ["ch2", "不存在"] },
            CancellationToken.None
        );
        Assert.True(unknown.IsError);

        var duplicate = await tool.ExecuteAsync(
            "call2",
            new ReorderChaptersParams { NovelId = "n1", ChapterIds = ["ch2", "ch2"] },
            CancellationToken.None
        );
        Assert.True(duplicate.IsError);

        var incomplete = await tool.ExecuteAsync(
            "call3",
            new ReorderChaptersParams { NovelId = "n1", ChapterIds = ["ch2"] },
            CancellationToken.None
        );
        Assert.True(incomplete.IsError);
        Assert.Equal(1, (await store.GetChapter("ch1"))!.Order);
        Assert.Equal(2, (await store.GetChapter("ch2"))!.Order);
    }
}
