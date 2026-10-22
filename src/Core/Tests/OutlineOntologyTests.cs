using Core.Data;
using Core.Evolution;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 大纲三层（总纲/卷纲/细纲）× 本体关系测试：
/// frontmatter 转义往返、三层读写往返（含旧文件兼容与删卷级联）、
/// 关系索引边类型/视角标签/悬空引用跳过、章节维度的出场与变化查询。
/// 用 FileNovelStore（真实 YAML/frontmatter 往返）保证与存储层契约一致。
/// </summary>
[Collection("FileNovelStore")]
public sealed class OutlineOntologyTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly string _mythologyDir;
    private readonly FileNovelStore _store;

    public OutlineOntologyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"outline-{Guid.NewGuid():N}");
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

    // ══════════ frontmatter 转义 ══════════

    [Fact]
    public void MarkdownHelper_SpecialChars_Roundtrip_AndStaysSingleLine()
    {
        var meta = new Dictionary<string, string>
        {
            ["id"] = "ch1",
            ["title"] = "第一章：雨夜 \"寄北\"",
            ["summary"] = "第一行\n第二行\r\n第三行",
            ["notes"] = @"路径 C:\novel\ch1.md",
        };

        var md = MarkdownHelper.WriteMarkdown(meta, "正文");
        var (parsed, body) = MarkdownHelper.ParseMarkdown(md);

        Assert.Equal(meta["id"], parsed["id"]);
        Assert.Equal(meta["title"], parsed["title"]);
        // CRLF 归一化为 LF（行尾归一，其余字符保真）
        Assert.Equal("第一行\n第二行\n第三行", parsed["summary"]);
        Assert.Equal(meta["notes"], parsed["notes"]);
        Assert.Equal("正文", body);

        // frontmatter 必须保持行式：行数 = 键数 + 2（--- 边界两行），无裸换行值
        var frontMatter = md[..md.IndexOf("\n---", StringComparison.Ordinal)];
        Assert.Equal(meta.Count + 1, frontMatter.Split('\n').Length);
    }

    // ══════════ 三层读写往返 ══════════

    [Fact]
    public async Task NovelOutline_Roundtrips_AllReadPaths()
    {
        var novel = new Novel
        {
            Id = "nov100",
            Title = "总纲测试",
            Outline = "全书走向：\n一、复仇线\n二、结局：主角放弃复仇",
        };
        await _store.SaveNovel(novel);

        var full = await _store.GetNovel("nov100");
        var summary = await _store.GetNovelSummary("nov100");
        var all = await _store.GetAllNovels();

        Assert.Equal(novel.Outline, full!.Outline);
        Assert.Equal(novel.Outline, summary!.Outline);
        Assert.Equal(novel.Outline, all.Single(n => n.Id == "nov100").Outline);
    }

    [Fact]
    public async Task Volume_Roundtrips_WithRelations()
    {
        await _store.SaveNovel(new Novel { Id = "nov101", Title = "卷测试" });
        var volume = new Volume
        {
            Id = "vol1",
            NovelId = "nov101",
            Name = "风起云涌",
            Order = 1,
            Summary = "主角入城，卷入夺宝。",
            Goal = "立起主角动机",
            PlotlineIds = ["pl1", "pl2"],
            CharacterIds = ["c1"],
            Notes = "卷末留钩子",
        };
        await _store.SaveVolume(volume);

        var volumes = await _store.GetVolumes("nov101");
        var single = await _store.GetVolume("nov101", "vol1");
        var byId = await _store.GetVolume("vol1");

        Assert.Single(volumes);
        Assert.Equal(["pl1", "pl2"], volumes[0].PlotlineIds);
        Assert.Equal(["c1"], volumes[0].CharacterIds);
        Assert.Equal("主角入城，卷入夺宝。", volumes[0].Summary);
        Assert.Equal("vol1", single!.Id);
        Assert.Equal("vol1", byId!.Id);

        // 同步分类：volumes/*.yaml 必须是 typed 文档（volume），否则云端图谱拿不到卷节点
        var documents = await SyncDocumentEnumerator.EnumerateAsync(
            _tempRoot,
            "nov101",
            TestContext.Current.CancellationToken
        );
        var volumeDoc = Assert.Single(documents.Where(d => d.RelativePath.Contains("volumes/")));
        Assert.Equal("volume", volumeDoc.DocumentType);
        Assert.Equal("vol1", volumeDoc.EntityId);
        Assert.Equal("风起云涌", volumeDoc.Title);
    }

    [Fact]
    public async Task ChapterOutlineFields_Roundtrip_DirectAndByIdPaths()
    {
        await _store.SaveNovel(new Novel { Id = "nov102", Title = "细纲测试" });
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "nov102",
            Title = "夜雨寄北",
            Order = 1,
            Content = "正文内容",
            VolumeId = "vol1",
            Summary = "梗概带换行\n第二行",
            PlotlineIds = ["pl1", "pl2"],
            CharacterIds = ["c1", "c2"],
            Notes = "节奏放缓",
        };
        await _store.SaveChapter(chapter);

        var list = await _store.GetChapters("nov102");
        var byNovelId = await _store.GetChapter("nov102", "ch1");

        foreach (var loaded in new[] { list.Single(), byNovelId! })
        {
            Assert.Equal("vol1", loaded.VolumeId);
            Assert.Equal("梗概带换行\n第二行", loaded.Summary);
            Assert.Equal(["pl1", "pl2"], loaded.PlotlineIds);
            Assert.Equal(["c1", "c2"], loaded.CharacterIds);
            Assert.Equal("节奏放缓", loaded.Notes);
        }

        // 落盘必须是单行 frontmatter（含换行的梗概被转义）
        var chapterFile = Path.Combine(_tempRoot, "细纲测试", "chapters", "ch001.md");
        var text = await File.ReadAllTextAsync(chapterFile);
        var frontMatter = text[..text.IndexOf("\n---", StringComparison.Ordinal)];
        Assert.Contains("volume-id: vol1", frontMatter);
        Assert.Contains("plotline-ids: pl1,pl2", frontMatter);
        Assert.Contains("\\n", frontMatter);
    }

    [Fact]
    public async Task LegacyChapterFile_WithoutOutlineKeys_LoadsEmpty()
    {
        var novelDir = Path.Combine(_tempRoot, "旧书记录");
        Directory.CreateDirectory(Path.Combine(novelDir, "chapters"));
        await File.WriteAllTextAsync(
            Path.Combine(novelDir, "novel.yaml"),
            "id: nov103\ntitle: 旧书记录\ngenre: 玄幻\n"
        );
        await File.WriteAllTextAsync(
            Path.Combine(novelDir, "chapters", "ch001.md"),
            "---\nid: chOld\norder: 1\ntitle: 旧章\nstatus: draft\n---\n\n正文\n"
        );

        var chapter = await _store.GetChapter("nov103", "chOld");
        Assert.NotNull(chapter);
        Assert.Null(chapter!.VolumeId);
        Assert.Equal("", chapter.Summary);
        Assert.Empty(chapter.PlotlineIds);
        Assert.Empty(chapter.CharacterIds);
        Assert.Equal("", chapter.Notes);
    }

    [Fact]
    public async Task DeleteVolume_ClearsChapterVolumeId()
    {
        await _store.SaveNovel(new Novel { Id = "nov104", Title = "删卷测试" });
        await _store.SaveVolume(
            new Volume
            {
                Id = "vol9",
                NovelId = "nov104",
                Name = "待删卷",
                Order = 1,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "chA",
                NovelId = "nov104",
                Title = "第一章",
                Order = 1,
                VolumeId = "vol9",
            }
        );

        await _store.DeleteVolume("nov104", "vol9");

        Assert.Empty(await _store.GetVolumes("nov104"));
        Assert.Null((await _store.GetChapter("nov104", "chA"))!.VolumeId);
    }

    // ══════════ 本体关系索引 ══════════

    private async Task SeedRelationNovelAsync()
    {
        await _store.SaveNovel(new Novel { Id = "nov200", Title = "关系测试" });

        await _store.SaveVolume(
            new Volume
            {
                Id = "vol1",
                NovelId = "nov200",
                Name = "风起",
                Order = 1,
                PlotlineIds = ["pl1"],
                CharacterIds = ["c1"],
            }
        );

        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "nov200",
                Title = "入城",
                Order = 1,
                VolumeId = "vol1",
                PlotlineIds = ["pl1"],
                CharacterIds = ["c1"],
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch2",
                NovelId = "nov200",
                Title = "夺宝",
                Order = 2,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch3",
                NovelId = "nov200",
                Title = "收束",
                Order = 3,
            }
        );

        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "nov200",
                Name = "林长歌",
                FactionId = "f1",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c2",
                NovelId = "nov200",
                Name = "苏白衣",
                FactionId = "missing", // 悬空引用：必须被跳过
            }
        );

        await _store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = "nov200",
                Name = "青云门",
                Leader = "c1",
                HeadquartersId = "l1",
                Members = ["c1"],
            }
        );

        await _store.SaveLocation(
            new Location
            {
                Id = "l1",
                NovelId = "nov200",
                Name = "青云山",
            }
        );
        await _store.SaveLocation(
            new Location
            {
                Id = "l2",
                NovelId = "nov200",
                Name = "山门",
                ParentId = "l1",
                AdjacentLocations = [new LocationAdjacency { TargetId = "l1", DistanceLi = 12 }],
            }
        );

        await _store.SavePlotline(
            new Plotline
            {
                Id = "pl1",
                NovelId = "nov200",
                Name = "夺宝线",
                PlannedRevealChapterId = "ch2",
                ResolvedAtChapterId = "ch3",
            }
        );

        await _store.SaveItem(
            new Item
            {
                Id = "it1",
                NovelId = "nov200",
                Name = "玉佩",
                OwnerId = "c1",
                OwnershipPeriods =
                [
                    new ItemOwnershipSpan
                    {
                        OwnerId = "c1",
                        SinceChapterId = "ch1",
                        UntilChapterId = "ch2",
                    },
                    new ItemOwnershipSpan { OwnerId = "c2", SinceChapterId = "ch3" },
                ],
            }
        );

        await _store.SaveScene(
            new SceneDefinition
            {
                Id = "sc1",
                NovelId = "nov200",
                Title = "城门相遇",
                Order = 1,
                ChapterId = "ch1",
                LocationId = "l2",
                ParticipantIds = ["c1"],
                PlotlineIds = ["pl1"],
                ItemIds = ["it1"],
            }
        );

        await _store.SaveRelationship(
            new Relationship
            {
                Id = "r1",
                NovelId = "nov200",
                CharacterAId = "c1",
                CharacterBId = "c2",
                Type = RelationshipType.Friend,
                SinceChapterId = "ch1",
                UntilChapterId = "ch3",
            }
        );

        await _store.SaveFactionRelationship(
            new FactionRelationship
            {
                Id = "fr1",
                NovelId = "nov200",
                FactionAId = "f1",
                FactionBId = "f1",
                Type = FactionRelationType.Ally,
            }
        );

        await _store.SaveTimelineEvent(
            new TimelineEvent
            {
                Id = "te1",
                NovelId = "nov200",
                Title = "入城事件",
                ChapterId = "ch1",
            }
        );

        await _store.SaveWorldRule(
            "nov200",
            new WorldRule
            {
                Id = "wr1",
                NovelId = "nov200",
                Type = "travel-speed",
                Value = 300,
                Unit = "里/日",
                Description = "修士日行三百里",
            }
        );
        await _store.SaveWorldRule(
            "nov200",
            new WorldRule
            {
                Id = "wr2",
                NovelId = "nov200",
                RuleKind = RuleKind.Requires,
                Category = WorldRuleCategory.Power,
                SubjectType = EntityType.Character,
                SubjectId = "c1",
                Action = "御剑",
                RequiredState = "金丹",
                Description = "御剑需金丹",
                Effect = new PlannedDelta
                {
                    Kind = PlannedDeltaKind.StateChange,
                    TargetEntityType = EntityType.Character,
                    TargetId = "c2",
                    Aspect = EvolutionAspect.Ability,
                    Summary = "御剑失败波及同伴",
                },
            }
        );
        await _store.SaveWorldRule(
            "nov200",
            new WorldRule
            {
                Id = "set1",
                NovelId = "nov200",
                EntryKind = WorldRuleEntryKind.Setting,
                Category = WorldRuleCategory.Social,
                Description = "宗门礼制",
                Content = "外门弟子不得御剑。",
            }
        );
    }

    [Fact]
    public async Task RelationIndex_BuildsExpectedEdges_AndSkipsDanglingRefs()
    {
        await SeedRelationNovelAsync();
        var index = await EntityRelationIndex.BuildAsync(_store, "nov200");

        var chapterRelations = index.GetRelations(EntityType.Chapter, "ch1");
        Assert.Contains(chapterRelations, r => r.EdgeType == "in-volume" && r.TargetId == "vol1");
        Assert.Contains(chapterRelations, r => r.EdgeType == "advances" && r.TargetId == "pl1");
        Assert.Contains(chapterRelations, r => r.EdgeType == "features" && r.TargetId == "c1");

        var characterRelations = index.GetRelations(EntityType.Character, "c1");
        Assert.Contains(
            characterRelations,
            r => r.EdgeType == "membership" && r.TargetType == EntityType.Faction
        );
        Assert.Contains(
            characterRelations,
            r => r.EdgeType == "relationship" && r.TargetId == "c2"
        );
        Assert.Contains(
            characterRelations,
            r => r.EdgeType == "appears-in" && r.TargetType == EntityType.Scene
        );

        // 悬空引用（c2.FactionId = missing）不产生边
        Assert.DoesNotContain(
            index.GetRelations(EntityType.Character, "c2"),
            r => r.EdgeType == "membership"
        );

        // 地点层级（规范方向 父→子）与邻接
        var locationRelations = index.GetRelations(EntityType.Location, "l2");
        var parentEdge = locationRelations.Single(r =>
            r.EdgeType == "contains" && r.SourceId == "l1"
        );
        Assert.Equal("l2", parentEdge.TargetId);
        Assert.Equal(
            "上级地点",
            EntityRelationIndex.LabelFor(parentEdge, EntityType.Location, "l2")
        );
        Assert.Equal(
            "下级地点",
            EntityRelationIndex.LabelFor(parentEdge, EntityType.Location, "l1")
        );
        Assert.Contains(locationRelations, r => r.EdgeType == "adjacent-to" && r.Detail == "12 里");

        // 道具持有：单值 + 时窗链（c1 合并窗口、c2 单窗口）
        var itemRelations = index.GetRelations(EntityType.Item, "it1");
        var ownerC1 = itemRelations.Single(r => r.EdgeType == "ownership" && r.TargetId == "c1");
        var ownerC2 = itemRelations.Single(r => r.EdgeType == "ownership" && r.TargetId == "c2");
        Assert.Equal("第1–2章", ownerC1.Detail);
        Assert.Equal("第3章起", ownerC2.Detail);

        // 情节线锚点 + 时间线 + 规则
        Assert.Contains(
            index.GetRelations(EntityType.Plotline, "pl1"),
            r => r.EdgeType == "reveals-at" && r.TargetId == "ch2"
        );
        Assert.Contains(
            index.GetRelations(EntityType.Plotline, "pl1"),
            r => r.EdgeType == "resolves-at" && r.TargetId == "ch3"
        );
        Assert.Contains(
            index.GetRelations(EntityType.TimelineEvent, "te1"),
            r => r.EdgeType == "occurs-in" && r.TargetId == "ch1"
        );
        Assert.Contains(
            index.GetRelations(EntityType.Novel, "nov200"),
            r =>
                r.EdgeType == "constrains"
                && r.SourceId == "wr1"
                && r.Detail == "travel-speed=300里/日" // 规范键（迁移 007 后）
        );
        var ruleRelations = index.GetRelations(EntityType.WorldRule, "wr2");
        Assert.Contains(
            ruleRelations,
            r =>
                r.EdgeType == "constrains"
                && r.TargetType == EntityType.Character
                && r.TargetId == "c1"
        );
        Assert.Contains(
            ruleRelations,
            r =>
                r.EdgeType == "affects"
                && r.TargetType == EntityType.Character
                && r.TargetId == "c2"
        );
        Assert.Empty(index.GetRelations(EntityType.WorldRule, "set1"));

        // 场景四条边
        var sceneRelations = index.GetRelations(EntityType.Scene, "sc1");
        Assert.Contains(sceneRelations, r => r.EdgeType == "located-at" && r.TargetId == "l2");
        Assert.Contains(sceneRelations, r => r.EdgeType == "in-chapter" && r.TargetId == "ch1");
        Assert.Contains(sceneRelations, r => r.EdgeType == "belongs-to" && r.TargetId == "pl1");
        Assert.Contains(sceneRelations, r => r.EdgeType == "appears-in" && r.SourceId == "it1");
    }

    [Fact]
    public async Task RelationIndex_ViewLabels_DifferByPerspective()
    {
        await SeedRelationNovelAsync();
        var index = await EntityRelationIndex.BuildAsync(_store, "nov200");

        var inVolume = index
            .GetRelations(EntityType.Chapter, "ch1")
            .Single(r => r.EdgeType == "in-volume");
        Assert.Equal("所属分卷", EntityRelationIndex.LabelFor(inVolume, EntityType.Chapter, "ch1"));
        Assert.Equal("包含章节", EntityRelationIndex.LabelFor(inVolume, EntityType.Volume, "vol1"));

        var chapterAdvance = index
            .GetRelations(EntityType.Chapter, "ch1")
            .Single(r => r.EdgeType == "advances");
        var volumeAdvance = index
            .GetRelations(EntityType.Volume, "vol1")
            .Single(r => r.EdgeType == "advances");
        Assert.Equal(
            "被推进章节",
            EntityRelationIndex.LabelFor(chapterAdvance, EntityType.Plotline, "pl1")
        );
        Assert.Equal(
            "被推进卷",
            EntityRelationIndex.LabelFor(volumeAdvance, EntityType.Plotline, "pl1")
        );

        var chapterFeature = index
            .GetRelations(EntityType.Chapter, "ch1")
            .Single(r => r.EdgeType == "features");
        Assert.Equal(
            "计划出场",
            EntityRelationIndex.LabelFor(chapterFeature, EntityType.Chapter, "ch1")
        );

        var membership = index
            .GetRelations(EntityType.Character, "c1")
            .Single(r => r.EdgeType == "membership");
        Assert.Equal(
            "所属组织",
            EntityRelationIndex.LabelFor(membership, EntityType.Character, "c1")
        );
        Assert.Equal("成员", EntityRelationIndex.LabelFor(membership, EntityType.Faction, "f1"));

        // 名称解析
        Assert.Equal("第1章 入城", index.NameOf(EntityType.Chapter, "ch1"));
        Assert.Equal("第1卷 风起", index.NameOf(EntityType.Volume, "vol1"));
        Assert.Equal("林长歌", index.NameOf(EntityType.Character, "c1"));
        Assert.Equal("unknown-id", index.NameOf(EntityType.Character, "unknown-id"));
    }

    // ══════════ 章节维度出场/变化查询 ══════════

    [Fact]
    public async Task TimelineView_ChapterQueries_AreScopedToChapter()
    {
        await SeedRelationNovelAsync();
        await _store.SaveChapterAppearances(
            "nov200",
            "ch1",
            [
                new AppearanceRecord
                {
                    Id = "ap1",
                    NovelId = "nov200",
                    EntityType = EntityType.Character,
                    EntityId = "c1",
                    ChapterId = "ch1",
                    Mode = AppearanceMode.Present,
                    Source = AuditSource.System,
                },
            ]
        );
        await _store.SaveEntityStateChanges(
            "nov200",
            "ch2",
            [
                new EntityStateChange
                {
                    Id = "sc1",
                    NovelId = "nov200",
                    TargetEntityType = EntityType.Character,
                    TargetId = "c1",
                    ChapterId = "ch2",
                    Aspect = EvolutionAspect.Status,
                    Summary = "重伤",
                    Source = AuditSource.Ai,
                },
            ]
        );

        var view = await EntityTimelineView.BuildAsync(_store, "nov200");

        var ch1Appearances = view.GetChapterAppearances("ch1");
        Assert.Single(ch1Appearances);
        Assert.Equal("c1", ch1Appearances[0].EntityId);
        Assert.Empty(view.GetChapterAppearances("ch2"));

        var ch2Changes = view.GetChapterStateChanges("ch2");
        Assert.Single(ch2Changes);
        Assert.Equal("重伤", ch2Changes[0].Summary);
        Assert.Empty(view.GetChapterStateChanges("ch1"));
        Assert.Empty(view.GetChapterAppearances("ch-not-exist"));
        Assert.Empty(view.GetChapterStateChanges("ch-not-exist"));
    }
}
