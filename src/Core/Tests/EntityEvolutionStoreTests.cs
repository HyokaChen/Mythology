using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 实体×章节演化元信息存储测试（ENTITY-EVOLUTION §8 E1 验收）。
/// 覆盖：按章分片往返序列化、章删除级联删除演化分片、EntityType snake_case 新枚举值兼容。
/// </summary>
[Collection("FileNovelStore")]
public sealed class EntityEvolutionStoreTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly string _mythologyDir;
    private readonly FileNovelStore _store;

    public EntityEvolutionStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"evolution-{Guid.NewGuid():N}");
        _mythologyDir = Path.Combine(_tempRoot, ".mythology");
        Directory.CreateDirectory(_tempRoot);
        _store = new FileNovelStore(_tempRoot, _mythologyDir);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch { }
        return default;
    }

    private async Task<Chapter> SeedNovelWithChapterAsync(int order = 1)
    {
        var novel = new Novel { Id = "nov001", Title = "演化测试" };
        await _store.SaveNovel(novel);
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "nov001",
            Title = "第一章",
            Order = order,
            Content = "林长歌推门而入，冷月紧随其后。",
        };
        await _store.SaveChapter(chapter);
        return chapter;
    }

    // ── 出场记录：按章分片往返 ──

    [Fact]
    public async Task SaveAppearances_Roundtrip_WithModeAndSource()
    {
        var chapter = await SeedNovelWithChapterAsync();
        var records = new List<AppearanceRecord>
        {
            new()
            {
                Id = "ap1",
                NovelId = "nov001",
                EntityType = EntityType.Character,
                EntityId = "char1",
                ChapterId = chapter.Id,
                Mode = AppearanceMode.Present,
                SceneIds = ["sc1"],
                EvidenceSpan = "林长歌推门而入",
                Source = AuditSource.System,
            },
            new()
            {
                Id = "ap2",
                NovelId = "nov001",
                EntityType = EntityType.Location,
                EntityId = "loc1",
                ChapterId = chapter.Id,
                Mode = AppearanceMode.Mentioned,
                Source = AuditSource.Ai,
            },
        };
        await _store.SaveChapterAppearances("nov001", chapter.Id, records);

        // 按章读回
        var byChapter = await _store.GetAppearancesByChapter("nov001", chapter.Id);
        Assert.Equal(2, byChapter.Count);
        Assert.Contains(
            byChapter,
            r =>
                r.EntityId == "char1"
                && r.Mode == AppearanceMode.Present
                && r.Source == AuditSource.System
                && r.EvidenceSpan == "林长歌推门而入"
        );
        Assert.Contains(
            byChapter,
            r =>
                r.EntityId == "loc1"
                && r.Mode == AppearanceMode.Mentioned
                && r.Source == AuditSource.Ai
        );

        // 全量读回
        var all = await _store.GetAppearances("nov001");
        Assert.Equal(2, all.Count);

        // 文件落盘按章分片（appearances/ch001.yaml）——novel 目录在 novelsRoot 下
        var novelDir = Directory
            .GetDirectories(_tempRoot, "演化测试*")
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "novel.yaml")));
        Assert.NotNull(novelDir);
        Assert.True(File.Exists(Path.Combine(novelDir, "appearances", "ch001.yaml")));
    }

    // ── 变化事件：按实体过滤 ──

    [Fact]
    public async Task SaveStateChanges_FilterByEntity()
    {
        var chapter = await SeedNovelWithChapterAsync();
        var changes = new List<EntityStateChange>
        {
            new()
            {
                Id = "ev1",
                NovelId = "nov001",
                TargetEntityType = EntityType.Character,
                TargetId = "char1",
                ChapterId = chapter.Id,
                Aspect = EvolutionAspect.Relationship,
                FromSummary = "敌视",
                ToSummary = "信任",
                EvidenceQuote = "「这条命，从今往后是你的。」",
                Summary = "生死之交",
                Source = AuditSource.Ai,
            },
            new()
            {
                Id = "ev2",
                NovelId = "nov001",
                TargetEntityType = EntityType.Character,
                TargetId = "char2",
                ChapterId = chapter.Id,
                Aspect = EvolutionAspect.Status,
                ToSummary = "Deceased",
                Summary = "阵亡",
                Source = AuditSource.System,
            },
        };
        await _store.SaveEntityStateChanges("nov001", chapter.Id, changes);

        var char1Events = await _store.GetEntityStateChangesByEntity(
            "nov001",
            EntityType.Character,
            "char1"
        );
        Assert.Single(char1Events);
        Assert.Equal(EvolutionAspect.Relationship, char1Events[0].Aspect);
        Assert.Equal("敌视", char1Events[0].FromSummary);
        Assert.Equal("信任", char1Events[0].ToSummary);

        var all = await _store.GetEntityStateChanges("nov001");
        Assert.Equal(2, all.Count);
    }

    // ── E1 核心验收：章删除级联删除演化分片 ──

    [Fact]
    public async Task EvolutionShards_ClassifyAsTypedDocuments_NotDegradedToFile()
    {
        // 回归护栏（ENTITY-EVOLUTION §10 开放问题 7）：分片内容是 YAML 列表而非常规实体的
        // mapping，若 Classify 只认 mapping 的 id 字段，分片会被降级为 file —— 云端
        // 云端图谱投影器 虽然能按路径前缀兜底，但图边 SourceDocumentType 会记成 file。
        var chapter = await SeedNovelWithChapterAsync(order: 3);
        await _store.SaveChapterAppearances(
            "nov001",
            chapter.Id,
            [
                new AppearanceRecord
                {
                    Id = "ap1",
                    NovelId = "nov001",
                    EntityType = EntityType.Character,
                    EntityId = "char1",
                    ChapterId = chapter.Id,
                    Mode = AppearanceMode.Present,
                    Source = AuditSource.System,
                },
            ]
        );
        await _store.SaveEntityStateChanges(
            "nov001",
            chapter.Id,
            [
                new EntityStateChange
                {
                    Id = "ev1",
                    NovelId = "nov001",
                    TargetEntityType = EntityType.Character,
                    TargetId = "char1",
                    ChapterId = chapter.Id,
                    Aspect = EvolutionAspect.Status,
                    ToSummary = "Deceased",
                    Source = AuditSource.Ai,
                },
            ]
        );
        // 用户自放的非分片文件必须仍降级 file（不丢文件）
        var novelDir = Directory
            .GetDirectories(_tempRoot, "演化测试*")
            .First(d => File.Exists(Path.Combine(d, "novel.yaml")));
        await File.WriteAllTextAsync(
            Path.Combine(novelDir, "appearances", "notes.yaml"),
            "- 手工笔记\n"
        );

        var documents = await SyncDocumentEnumerator.EnumerateAsync(
            _tempRoot,
            "nov001",
            TestContext.Current.CancellationToken
        );

        var appearanceShard = Assert.Single(
            documents.Where(d => d.RelativePath.EndsWith("appearances/ch003.yaml"))
        );
        Assert.Equal("appearance", appearanceShard.DocumentType);
        Assert.Equal("ch_3", appearanceShard.EntityId); // 去前导零，与 ch_{Order} 引用同形

        var stateChangeShard = Assert.Single(
            documents.Where(d => d.RelativePath.EndsWith("evolution/ch003.yaml"))
        );
        Assert.Equal("state-change", stateChangeShard.DocumentType);
        Assert.Equal("ch_3", stateChangeShard.EntityId);

        var handPlaced = Assert.Single(
            documents.Where(d => d.RelativePath.EndsWith("appearances/notes.yaml"))
        );
        Assert.Equal("file", handPlaced.DocumentType);
        Assert.Equal(handPlaced.RelativePath, handPlaced.EntityId);
    }

    [Fact]
    public async Task DeleteChapter_CascadesEvolutionShards()
    {
        var chapter = await SeedNovelWithChapterAsync();
        await _store.SaveChapterAppearances(
            "nov001",
            chapter.Id,
            [
                new()
                {
                    Id = "ap1",
                    NovelId = "nov001",
                    EntityId = "char1",
                    ChapterId = chapter.Id,
                },
            ]
        );
        await _store.SaveEntityStateChanges(
            "nov001",
            chapter.Id,
            [
                new()
                {
                    Id = "ev1",
                    NovelId = "nov001",
                    TargetId = "char1",
                    ChapterId = chapter.Id,
                    Summary = "变化",
                },
            ]
        );

        // 前置：分片存在
        Assert.NotEmpty(await _store.GetAppearances("nov001"));
        Assert.NotEmpty(await _store.GetEntityStateChanges("nov001"));

        await _store.DeleteChapter("nov001", chapter.Id);

        // 级联删除：两集合归零
        Assert.Empty(await _store.GetAppearances("nov001"));
        Assert.Empty(await _store.GetEntityStateChanges("nov001"));
    }

    // ── 整章替换语义 ──

    [Fact]
    public async Task SaveChapterAppearances_ReplacesWholeShard()
    {
        var chapter = await SeedNovelWithChapterAsync();
        await _store.SaveChapterAppearances(
            "nov001",
            chapter.Id,
            [
                new()
                {
                    Id = "ap1",
                    NovelId = "nov001",
                    EntityId = "char1",
                    ChapterId = chapter.Id,
                },
                new()
                {
                    Id = "ap2",
                    NovelId = "nov001",
                    EntityId = "char2",
                    ChapterId = chapter.Id,
                },
            ]
        );

        // 整章替换：只留一条，旧的 ap1/ap2 全部消失
        await _store.SaveChapterAppearances(
            "nov001",
            chapter.Id,
            [
                new()
                {
                    Id = "ap3",
                    NovelId = "nov001",
                    EntityId = "char3",
                    ChapterId = chapter.Id,
                },
            ]
        );

        var all = await _store.GetAppearancesByChapter("nov001", chapter.Id);
        Assert.Single(all);
        Assert.Equal("char3", all[0].EntityId);
    }

    // ── 章不存在：静默不写（宁可漏不误报） ──

    [Fact]
    public async Task SaveAppearances_UnknownChapter_NoThrow()
    {
        await SeedNovelWithChapterAsync();
        await _store.SaveChapterAppearances(
            "nov001",
            "nonexistent",
            [
                new()
                {
                    Id = "apX",
                    NovelId = "nov001",
                    EntityId = "char1",
                    ChapterId = "nonexistent",
                },
            ]
        );
        Assert.Empty(await _store.GetAppearances("nov001"));
    }

    // ── EntityType 新枚举值 snake_case 兼容（E1 验收第三条） ──

    [Fact]
    public void EntityType_NewMembers_SnakeCaseRoundtrip()
    {
        Assert.Equal("appearance", EntityType.Appearance.ToKebabCaseString());
        Assert.Equal("state-change", EntityType.StateChange.ToKebabCaseString());

        // 旧 JSONL 值不受影响
        Assert.Equal(EntityType.Chapter, EntityTypeExtensions.ParseEntityType("chapter"));
        Assert.Equal(EntityType.Character, EntityTypeExtensions.ParseEntityType("character"));

        // 新值可解析（ReverseMap 覆盖）
        Assert.Equal(EntityType.Appearance, EntityTypeExtensions.ParseEntityType("appearance"));
        Assert.Equal(EntityType.StateChange, EntityTypeExtensions.ParseEntityType("state-change"));

        // 未知值仍返回 null（既有语义）
        Assert.Null(EntityTypeExtensions.ParseEntityType("not_a_type"));
    }
}
