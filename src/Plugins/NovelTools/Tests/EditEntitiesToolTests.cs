using Core.Data;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// edit_entities：一次调用跨类型批量修改设定面板字段。
/// 覆盖此前 manage_* 缺失的字段（角色 status/tags/abilities/notes、关系时窗、场景道具、
/// 章节细纲、小说四字段）与错误路径（未知字段/只读字段/章节序号正文）。
/// </summary>
public sealed class EditEntitiesToolTests
{
    private static async Task<FakeNovelStore> CreateStoreAsync()
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
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "旧标题",
                Order = 1,
                Status = ChapterStatus.Draft,
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "小明",
                Role = CharacterRole.Supporting,
            }
        );
        await store.SaveRelationship(
            new Relationship
            {
                Id = "r1",
                NovelId = "n1",
                CharacterAId = "c1",
                CharacterBId = "c2",
            }
        );
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "s1",
                NovelId = "n1",
                Title = "初遇",
                SceneType = SceneType.Dialogue,
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
    public async Task BatchEdit_CoversFieldsMissingFromManageTools()
    {
        var store = await CreateStoreAsync();
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
                        new EntityFieldPatch { Field = "status", Value = "已死亡" },
                        new EntityFieldPatch { Field = "tags", Values = ["复仇者", "剑客"] },
                        new EntityFieldPatch { Field = "abilities", Value = "御剑" },
                        new EntityFieldPatch { Field = "notes", Value = "注意口音" }
                    ),
                    Item(
                        "relationship",
                        "r1",
                        new EntityFieldPatch { Field = "since-chapter-id", Value = "ch1" },
                        new EntityFieldPatch { Field = "until-chapter-id", Value = "ch2" },
                        new EntityFieldPatch { Field = "type", Value = "结拜" }
                    ),
                    Item(
                        "scene",
                        "s1",
                        new EntityFieldPatch { Field = "item-ids", Values = ["i1", "i2"] }
                    ),
                    Item(
                        "chapter",
                        "ch1",
                        new EntityFieldPatch { Field = "title", Value = "新标题" },
                        new EntityFieldPatch { Field = "summary", Value = "本章梗概" },
                        new EntityFieldPatch { Field = "plotline-ids", Values = ["p1"] },
                        new EntityFieldPatch { Field = "character-ids", Values = ["c1"] },
                        new EntityFieldPatch { Field = "notes", Value = "节奏提示" },
                        new EntityFieldPatch { Field = "volume-id", Value = "v1" },
                        new EntityFieldPatch { Field = "status", Value = "发布" }
                    ),
                    Item(
                        "novel",
                        "书名",
                        new EntityFieldPatch { Field = "author", Value = "作者乙" },
                        new EntityFieldPatch { Field = "target-words", Value = "80000" },
                        new EntityFieldPatch { Field = "target-audience", Value = "general" },
                        new EntityFieldPatch { Field = "enhancement-profile", Value = "文学向" },
                        new EntityFieldPatch { Field = "auto-diagnose", Value = "否" },
                        new EntityFieldPatch { Field = "auto-enhance", Value = "全自动" },
                        new EntityFieldPatch { Field = "completion-status", Value = "已完结" }
                    ),
                    Item(
                        "volume",
                        "v1",
                        new EntityFieldPatch { Field = "notes", Value = "本卷钩子" }
                    ),
                ],
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var details = Assert.IsType<EditEntitiesDetails>(result.Details);
        Assert.Equal(6, details.UpdatedCount);
        Assert.Equal(0, details.FailedCount);

        var character = await store.GetCharacter("c1");
        Assert.Equal(CharacterStatus.Deceased, character!.Status);
        Assert.Equal(["复仇者", "剑客"], character.Tags);
        Assert.Equal("御剑", character.Abilities);
        Assert.Equal("注意口音", character.Notes);

        var relationship = await store.GetRelationship("r1");
        Assert.Equal("ch1", relationship!.SinceChapterId);
        Assert.Equal("ch2", relationship.UntilChapterId);
        Assert.Equal(RelationshipType.Sworn, relationship.Type);

        var scene = await store.GetScene("s1");
        Assert.Equal(["i1", "i2"], scene!.ItemIds);

        var chapter = await store.GetChapter("n1", "ch1");
        Assert.Equal("新标题", chapter!.Title);
        Assert.Equal("本章梗概", chapter.Summary);
        Assert.Equal(["p1"], chapter.PlotlineIds);
        Assert.Equal(["c1"], chapter.CharacterIds);
        Assert.Equal("节奏提示", chapter.Notes);
        Assert.Equal("v1", chapter.VolumeId);
        Assert.Equal(ChapterStatus.Published, chapter.Status);
        Assert.Equal(1, chapter.Order); // 序号不在本次编辑范围

        var novel = await store.GetNovelSummary("n1");
        Assert.Equal("作者乙", novel!.Author);
        Assert.Equal(80000, novel.TargetWords);
        Assert.Equal("general", novel.TargetAudience);
        Assert.Equal("literary", novel.EnhancementProfile);
        Assert.False(novel.AutoDiagnose);
        Assert.Equal("full", novel.AutoEnhance);
        Assert.Equal("completed", novel.CompletionStatus);

        var volume = await store.GetVolume("v1");
        Assert.Equal("本卷钩子", volume!.Notes);
    }

    [Fact]
    public async Task UnknownField_FailsOnlyThatItem()
    {
        var store = await CreateStoreAsync();
        await store.SaveCharacter(
            new Character
            {
                Id = "c2",
                NovelId = "n1",
                Name = "小红",
            }
        );
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
                        new EntityFieldPatch { Field = "bogus-field", Value = "x" }
                    ),
                    Item(
                        "character",
                        "c2",
                        new EntityFieldPatch { Field = "notes", Value = "合法修改" }
                    ),
                ],
            },
            CancellationToken.None
        );

        var details = Assert.IsType<EditEntitiesDetails>(result.Details);
        Assert.Equal(1, details.UpdatedCount);
        Assert.Equal(1, details.FailedCount);

        var untouched = await store.GetCharacter("c1");
        Assert.Equal("", untouched!.Notes);

        var updated = await store.GetCharacter("c2");
        Assert.Equal("合法修改", updated!.Notes);

        var text = Assert.IsType<Core.Shared.TextContent>(result.Content[0]).Text;
        Assert.Contains("可用字段", text);
        Assert.DoesNotContain("bogus-field: ", text);
    }

    [Fact]
    public async Task ChapterOrderAndContent_AreRejected_WithHint()
    {
        var store = await CreateStoreAsync();
        var chapter = await store.GetChapter("n1", "ch1");
        chapter!.Content = "原文";
        chapter.Order = 7;

        var tool = new EditEntitiesTool(store);
        var result = await tool.ExecuteAsync(
            "call1",
            new EditEntitiesParams
            {
                NovelId = "n1",
                Items =
                [
                    Item("chapter", "ch1", new EntityFieldPatch { Field = "order", Value = "3" }),
                    Item(
                        "chapter",
                        "ch1",
                        new EntityFieldPatch { Field = "content", Value = "新文" }
                    ),
                ],
            },
            CancellationToken.None
        );

        var details = Assert.IsType<EditEntitiesDetails>(result.Details);
        Assert.Equal(0, details.UpdatedCount);
        Assert.Equal(2, details.FailedCount);

        var text = Assert.IsType<Core.Shared.TextContent>(result.Content[0]).Text;
        Assert.Contains("reorder_chapters", text);
        Assert.Contains("write_chapter", text);

        Assert.Equal(7, chapter.Order);
        Assert.Equal("原文", chapter.Content);
    }

    [Fact]
    public async Task ReadOnlyField_IsRejected()
    {
        var store = await CreateStoreAsync();
        var tool = new EditEntitiesTool(store);

        var result = await tool.ExecuteAsync(
            "call1",
            new EditEntitiesParams
            {
                NovelId = "n1",
                Items =
                [
                    Item(
                        "chapter",
                        "ch1",
                        new EntityFieldPatch { Field = "char-count", Value = "99" }
                    ),
                ],
            },
            CancellationToken.None
        );

        var details = Assert.IsType<EditEntitiesDetails>(result.Details);
        Assert.Equal(1, details.FailedCount);

        var text = Assert.IsType<Core.Shared.TextContent>(result.Content[0]).Text;
        Assert.Contains("不可写", text);
    }

    [Fact]
    public async Task NameResolution_And_MembershipReconcile()
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
        var f1 = new Faction
        {
            Id = "f1",
            NovelId = "n1",
            Name = "青云宗",
            Members = ["c1"],
        };
        var f2 = new Faction
        {
            Id = "f2",
            NovelId = "n1",
            Name = "血刀门",
            Members = [],
        };
        await store.SaveFaction(f1);
        await store.SaveFaction(f2);
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

        var tool = new EditEntitiesTool(store);
        var result = await tool.ExecuteAsync(
            "call1",
            new EditEntitiesParams
            {
                NovelId = "n1",
                Items =
                [
                    // 用角色名称定位，并切到新组织
                    Item(
                        "character",
                        "小明",
                        new EntityFieldPatch { Field = "faction-id", Value = "f2" }
                    ),
                    // 用组织名称定位，成员列表新值要反向写回角色 FactionId
                    Item(
                        "faction",
                        "青云宗",
                        new EntityFieldPatch { Field = "members", Values = ["c2"] }
                    ),
                ],
            },
            CancellationToken.None
        );

        var details = Assert.IsType<EditEntitiesDetails>(result.Details);
        Assert.Equal(2, details.UpdatedCount);
        Assert.Equal(0, details.FailedCount);

        var c1 = await store.GetCharacter("c1");
        Assert.Equal("f2", c1!.FactionId);
        var c2 = await store.GetCharacter("c2");
        Assert.Equal("f1", c2!.FactionId);

        Assert.DoesNotContain("c1", f1.Members);
        Assert.Contains("c2", f1.Members);
        Assert.Contains("c1", f2.Members);
    }
}
