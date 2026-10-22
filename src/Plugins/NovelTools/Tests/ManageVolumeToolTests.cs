using System.Text.Json;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 分卷工具（大纲二层）与大纲输出（三层分组）测试：
/// manage_volume CRUD（含按序号解析、删卷级联清空章节归属）、get_outline 按卷分组 + 细纲梗概。
/// </summary>
public sealed class ManageVolumeToolTests
{
    private static JsonElement Args(object args) =>
        JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement;

    private static async Task<FakeNovelStore> SeedAsync()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "工具验证" });
        await store.SavePlotline(
            new Plotline
            {
                Id = "pl1",
                NovelId = "n1",
                Name = "夺宝线",
                Type = PlotlineType.Main,
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林长歌",
            }
        );
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "雨夜寄北",
                Order = 1,
                Summary = "发现密信",
            }
        );
        return store;
    }

    [Fact]
    public async Task ManageVolume_CreateUpdateGetListDelete_Works()
    {
        var store = await SeedAsync();
        var tool = new ManageVolumeTool(store);

        // create（Order 留空 → 自动 1）
        var created = await tool.ExecuteAsync(
            "t1",
            new ManageVolumeParams
            {
                Action = "create",
                NovelId = "n1",
                Name = "风起",
                Summary = "入城夺宝",
                PlotlineIds = ["pl1"],
                CharacterIds = ["c1"],
            },
            CancellationToken.None
        );
        Assert.False(created.IsError);
        var volumeId = ((ManageVolumeDetails)created.Details!).Id;
        Assert.NotEmpty(volumeId);
        var volume = await store.GetVolume("n1", volumeId);
        Assert.Equal(1, volume!.Order);
        Assert.Equal(["pl1"], volume.PlotlineIds);

        // create 第二个 → Order 自动 2
        var created2 = await tool.ExecuteAsync(
            "t2",
            new ManageVolumeParams
            {
                Action = "create",
                NovelId = "n1",
                Name = "夺宝",
            },
            CancellationToken.None
        );
        Assert.Equal(
            2,
            (await store.GetVolume("n1", ((ManageVolumeDetails)created2.Details!).Id))!.Order
        );

        // 章节归卷 + update（按序号解析 volume_id="1"）
        var chapter = await store.GetChapter("n1", "ch1");
        chapter!.VolumeId = volumeId;
        await store.SaveChapter(chapter);

        var updated = await tool.ExecuteAsync(
            "t3",
            new ManageVolumeParams
            {
                Action = "update",
                NovelId = "n1",
                VolumeId = "1",
                Goal = "立起动机",
            },
            CancellationToken.None
        );
        Assert.False(updated.IsError);
        Assert.Equal("立起动机", (await store.GetVolume("n1", volumeId))!.Goal);
        Assert.Equal("入城夺宝", (await store.GetVolume("n1", volumeId))!.Summary); // 未传字段保持

        // get 显示归属章节
        var got = await tool.ExecuteAsync(
            "t4",
            new ManageVolumeParams
            {
                Action = "get",
                NovelId = "n1",
                VolumeId = volumeId,
            },
            CancellationToken.None
        );
        Assert.False(got.IsError);
        var gotText = string.Join("", got.Content.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("第1卷 风起", gotText);
        Assert.Contains("第1章 雨夜寄北", gotText);

        // list
        var listed = await tool.ExecuteAsync(
            "t5",
            new ManageVolumeParams { Action = "list", NovelId = "n1" },
            CancellationToken.None
        );
        Assert.Equal(2, ((ManageVolumeDetails)listed.Details!).Count);

        // delete（级联清空章节归属）
        var deleted = await tool.ExecuteAsync(
            "t6",
            new ManageVolumeParams
            {
                Action = "delete",
                NovelId = "n1",
                VolumeId = volumeId,
            },
            CancellationToken.None
        );
        Assert.False(deleted.IsError);
        Assert.Null(await store.GetVolume("n1", volumeId));
        Assert.Null((await store.GetChapter("n1", "ch1"))!.VolumeId);
    }

    [Fact]
    public async Task ManageVolume_UnknownId_ReturnsError()
    {
        var store = await SeedAsync();
        var tool = new ManageVolumeTool(store);

        var result = await tool.ExecuteAsync(
            "t1",
            new ManageVolumeParams
            {
                Action = "update",
                NovelId = "n1",
                VolumeId = "9",
                Name = "不存在",
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetOutline_GroupsByVolume_AndAppendsChapterSummary()
    {
        var store = await SeedAsync();
        await store.SaveVolume(
            new Volume
            {
                Id = "vol1",
                NovelId = "n1",
                Name = "风起",
                Order = 1,
                Summary = "入城夺宝",
            }
        );
        var chapter = await store.GetChapter("n1", "ch1");
        chapter!.VolumeId = "vol1";
        await store.SaveChapter(chapter);
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch2",
                NovelId = "n1",
                Title = "未分卷章",
                Order = 2,
            }
        );

        var tool = new GetOutlineTool(store);
        var result = await tool.ExecuteAsync(
            "t1",
            new GetOutlineParams { NovelId = "n1" },
            CancellationToken.None
        );

        var text = string.Join("", result.Content.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("📦 第1卷 风起（1章）", text);
        Assert.Contains("第1章 [ch1]: 雨夜寄北", text);
        Assert.Contains("发现密信", text); // 细纲梗概
        Assert.Contains("（未分卷）", text);
        Assert.Contains("第2章 [ch2]: 未分卷章", text);
        Assert.Equal(1, ((OutlineDetails)result.Details!).VolumeCount);
    }

    [Fact]
    public async Task GetOutline_IncludesNovelOutline_AndPointsToUpdateWhenEmpty()
    {
        // 一层总纲必须出现在 get_outline 顶部（此前模型既读不到也写不了总纲）
        var store = await SeedAsync();
        var novel = await store.GetNovel("n1");
        novel!.Outline = "全书三幕：入城 → 夺权 → 反目";
        await store.SaveNovel(novel);

        var tool = new GetOutlineTool(store);
        var filled = await tool.ExecuteAsync(
            "t1",
            new GetOutlineParams { NovelId = "n1" },
            CancellationToken.None
        );
        var filledText = string.Join("", filled.Content.OfType<TextContent>().Select(t => t.Text));

        Assert.Contains("总纲（大纲一层）", filledText);
        Assert.Contains("全书三幕：入城 → 夺权 → 反目", filledText);

        // 未填写：给出可行动指路（manage_novel update），而不是静默省略
        novel.Outline = "";
        await store.SaveNovel(novel);
        var empty = await tool.ExecuteAsync(
            "t2",
            new GetOutlineParams { NovelId = "n1" },
            CancellationToken.None
        );
        var emptyText = string.Join("", empty.Content.OfType<TextContent>().Select(t => t.Text));

        Assert.Contains("未填写", emptyText);
        Assert.Contains("manage_novel", emptyText);
    }
}
