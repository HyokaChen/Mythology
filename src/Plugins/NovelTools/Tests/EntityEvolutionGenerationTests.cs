using Core.Data;
using Core.Evolution;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 实体演化生成链路测试（entity-evolution-lifecycle §8 E2 验收）：
/// WriteChapterTool 保存章节 → L0 扫描钩子 → appearances 出场记录落地；
/// record_appearance / record_evolution 工具的 L2 显式登记。
/// </summary>
public sealed class EntityEvolutionGenerationTests
{
    private static (FakeNovelStore Store, string NovelId, string ChapterId) SeedAsync()
    {
        var store = new FakeNovelStore();
        // FakeNovelStore 为内存桩：直接构造实体（不走 SaveNovel 路径依赖）
        return (store, "nov001", "ch1");
    }

    [Fact]
    public async Task Recorder_ScanChapter_WritesAppearanceRecords()
    {
        var (store, novelId, chapterId) = SeedAsync();
        var chapter = new Chapter
        {
            Id = chapterId,
            NovelId = novelId,
            Title = "第一章",
            Order = 1,
            Content = "林长歌推门而入，玄铁剑在腰间嗡鸣。青云门远处灯火通明。",
        };
        await store.SaveChapter(chapter);
        await store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = novelId,
                Name = "林长歌",
            }
        );
        await store.SaveLocation(
            new Location
            {
                Id = "l1",
                NovelId = novelId,
                Name = "青云门",
            }
        );
        await store.SaveItem(
            new Item
            {
                Id = "i1",
                NovelId = novelId,
                Name = "玄铁剑",
            }
        );
        await store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = novelId,
                Name = "不存在的组织",
            }
        );

        await AppearanceEvolutionRecorder.RecordChapterAsync(store, novelId, chapter);

        var records = await store.GetAppearancesByChapter(novelId, chapterId);
        // 三个命中实体写入；未出现实体（不存在的组织）不写
        Assert.Equal(3, records.Count);
        Assert.Contains(records, r => r.EntityId == "c1" && r.EntityType == EntityType.Character);
        Assert.Contains(records, r => r.EntityId == "l1" && r.EntityType == EntityType.Location);
        Assert.Contains(records, r => r.EntityId == "i1" && r.EntityType == EntityType.Item);
        Assert.DoesNotContain(records, r => r.EntityId == "f1");

        // 证据引文含实体名上下文
        var charRecord = records.Single(r => r.EntityId == "c1");
        Assert.NotNull(charRecord.EvidenceSpan);
        Assert.Contains("林长歌", charRecord.EvidenceSpan!);
        Assert.Equal(AuditSource.System, charRecord.Source);
        Assert.Equal(AppearanceMode.Present, charRecord.Mode);
    }

    [Fact]
    public async Task Recorder_Rerun_IsIdempotent()
    {
        var (store, novelId, chapterId) = SeedAsync();
        var chapter = new Chapter
        {
            Id = chapterId,
            NovelId = novelId,
            Title = "第一章",
            Order = 1,
            Content = "林长歌推门而入。",
        };
        await store.SaveChapter(chapter);
        await store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = novelId,
                Name = "林长歌",
            }
        );

        await AppearanceEvolutionRecorder.RecordChapterAsync(store, novelId, chapter);
        await AppearanceEvolutionRecorder.RecordChapterAsync(store, novelId, chapter);

        var records = await store.GetAppearancesByChapter(novelId, chapterId);
        Assert.Single(records); // 整章替换，不重复
    }

    [Fact]
    public async Task RecordAppearanceTool_ExplicitRegistration_MergesWithSystem()
    {
        var (store, novelId, chapterId) = SeedAsync();
        var chapter = new Chapter
        {
            Id = chapterId,
            NovelId = novelId,
            Title = "第一章",
            Order = 1,
            Content = "正文",
        };
        await store.SaveChapter(chapter);
        // System 打底：另一实体
        await store.SaveChapterAppearances(
            novelId,
            chapterId,
            [
                new()
                {
                    Id = "ap_sys",
                    NovelId = novelId,
                    EntityType = EntityType.Character,
                    EntityId = "c_sys",
                    ChapterId = chapterId,
                    Source = AuditSource.System,
                },
            ]
        );

        var tool = new RecordAppearanceTool(store);
        var result = await tool.ExecuteAsync(
            "call-1",
            new RecordAppearanceParams
            {
                NovelId = novelId,
                EntityId = "c1",
                EntityType = EntityType.Character,
                ChapterId = chapterId,
                Mode = AppearanceMode.Mentioned,
                Evidence = "「听说林长歌已经死了。」",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var merged = await store.GetAppearancesByChapter(novelId, chapterId);
        // System 打底保留 + Ai 登记加入
        Assert.Equal(2, merged.Count);
        var ai = merged.Single(r => r.Source == AuditSource.Ai);
        Assert.Equal("c1", ai.EntityId);
        Assert.Equal(AppearanceMode.Mentioned, ai.Mode);
        Assert.Contains("听说林长歌", ai.EvidenceSpan!);
    }

    [Fact]
    public async Task RecordEvolutionTool_RegistersChange()
    {
        var (store, novelId, chapterId) = SeedAsync();
        var chapter = new Chapter
        {
            Id = chapterId,
            NovelId = novelId,
            Title = "第十二章",
            Order = 12,
            Content = "正文",
        };
        await store.SaveChapter(chapter);

        var tool = new RecordEvolutionTool(store);
        var result = await tool.ExecuteAsync(
            "call-2",
            new RecordEvolutionParams
            {
                NovelId = novelId,
                TargetId = "c1",
                TargetType = EntityType.Character,
                ChapterId = chapterId,
                Aspect = EvolutionAspect.Relationship,
                From = "敌视",
                To = "信任",
                Evidence = "「这条命，从今往后是你的。」",
                Summary = "生死之交",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var changes = await store.GetEntityStateChangesByEntity(
            novelId,
            EntityType.Character,
            "c1"
        );
        Assert.Single(changes);
        Assert.Equal(EvolutionAspect.Relationship, changes[0].Aspect);
        Assert.Equal("敌视", changes[0].FromSummary);
        Assert.Equal("信任", changes[0].ToSummary);
        Assert.Equal(AuditSource.Ai, changes[0].Source);
    }

    [Fact]
    public async Task RecordEvolutionTool_UnknownChapter_ReturnsError()
    {
        var (store, novelId, _) = SeedAsync();
        var tool = new RecordEvolutionTool(store);
        var result = await tool.ExecuteAsync(
            "call-3",
            new RecordEvolutionParams
            {
                NovelId = novelId,
                TargetId = "c1",
                ChapterId = "nonexistent",
                Summary = "测试",
            },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }
}
