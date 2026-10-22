using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// FileNovelStore 数据层集成测试。
/// 使用临时目录，测试结束后清理。
/// </summary>
[Collection("FileNovelStore")]
public sealed class FileNovelStoreIntegrationTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly string _mythologyDir;
    private readonly FileNovelStore _store;

    public FileNovelStoreIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"novelstore-{Guid.NewGuid():N}");
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

    // ══════════════════════════════════════════════════════
    //  Novel CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveNovel_And_GetNovel_Roundtrip()
    {
        var novel = new Novel
        {
            Id = "nov001",
            Title = "测试小说",
            Genre = "奇幻",
            Description = "一段测试描述",
            WritingStyle = "简洁风格",
        };
        await _store.SaveNovel(novel);

        var loaded = await _store.GetNovel("nov001");
        Assert.NotNull(loaded);
        Assert.Equal("测试小说", loaded!.Title);
        Assert.Equal("奇幻", loaded.Genre);
        Assert.Equal("一段测试描述", loaded.Description);
        Assert.Equal("简洁风格", loaded.WritingStyle);
    }

    [Fact]
    public async Task GetNovel_NotFound_ReturnsNull()
    {
        var result = await _store.GetNovel("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveNovel_PersistsAudienceProfileAndAutoFlags_AcrossAllReadPaths()
    {
        var novel = new Novel
        {
            Id = "nov002",
            Title = "落盘测试",
            Author = "作者甲",
            TargetWords = 123456,
            TargetAudience = "male_18_25",
            EnhancementProfile = "literary",
            AutoDiagnose = false,
            AutoEnhance = "full",
            CompletionStatus = "completed",
        };
        await _store.SaveNovel(novel);

        // 新实例重新读盘：读/写两侧都必须覆盖四个字段（旧文件缺键走默认值）。
        var reopened = new FileNovelStore(_tempRoot, _mythologyDir);

        var loaded = await reopened.GetNovel("nov002");
        Assert.NotNull(loaded);
        Assert.Equal("作者甲", loaded!.Author);
        Assert.Equal(123456, loaded.TargetWords);
        Assert.Equal("male_18_25", loaded.TargetAudience);
        Assert.Equal("literary", loaded.EnhancementProfile);
        Assert.False(loaded.AutoDiagnose);
        Assert.Equal("full", loaded.AutoEnhance);
        Assert.Equal("completed", loaded.CompletionStatus);

        var summary = await reopened.GetNovelSummary("nov002");
        Assert.NotNull(summary);
        Assert.Equal("male_18_25", summary!.TargetAudience);
        Assert.Equal("literary", summary.EnhancementProfile);
        Assert.False(summary.AutoDiagnose);
        Assert.Equal("full", summary.AutoEnhance);

        var fromAll = Assert.Single(await reopened.GetAllNovels(), n => n.Id == "nov002");
        Assert.Equal("male_18_25", fromAll.TargetAudience);
        Assert.Equal("literary", fromAll.EnhancementProfile);
        Assert.False(fromAll.AutoDiagnose);
        Assert.Equal("full", fromAll.AutoEnhance);
    }

    [Fact]
    public async Task GetNovel_OldFileWithoutNewKeys_UsesModelDefaults()
    {
        var dir = Path.Combine(_tempRoot, "旧书");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "novel.yaml"),
            "id: nov003\ntitle: 旧书\nwriting-style: 简洁\n"
        );

        var loaded = await _store.GetNovelSummary("nov003");
        Assert.NotNull(loaded);
        Assert.Equal("", loaded!.TargetAudience);
        Assert.Equal("commercial", loaded.EnhancementProfile);
        Assert.True(loaded.AutoDiagnose);
        Assert.Equal("semi", loaded.AutoEnhance);
    }

    [Fact]
    public async Task SaveNovel_UpdateExisting_ChangesTitle()
    {
        var novel = new Novel { Id = "u001", Title = "原始标题" };
        await _store.SaveNovel(novel);

        novel.Title = "更新标题";
        await _store.SaveNovel(novel);

        var loaded = await _store.GetNovel("u001");
        Assert.Equal("更新标题", loaded!.Title);
    }

    [Fact]
    public async Task SaveNovel_GeneratesCreatedAt()
    {
        var novel = new Novel { Id = "gen001", Title = "生成时间测试" };
        await _store.SaveNovel(novel);

        var loaded = await _store.GetNovel("gen001");
        Assert.NotEqual(default, loaded!.CreatedAt);
    }

    [Fact]
    public async Task GetAllNovels_ReturnsAllNovels()
    {
        await _store.SaveNovel(new Novel { Id = "n1", Title = "小说A" });
        await _store.SaveNovel(new Novel { Id = "n2", Title = "小说B" });

        var all = await _store.GetAllNovels();
        Assert.True(all.Count >= 2);
        Assert.Contains(all, n => n.Title == "小说A");
        Assert.Contains(all, n => n.Title == "小说B");
    }

    [Fact]
    public async Task DeleteNovel_RemovesNovel()
    {
        var novel = new Novel { Id = "del001", Title = "待删除" };
        await _store.SaveNovel(novel);
        await _store.DeleteNovel("del001");

        var loaded = await _store.GetNovel("del001");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task GetNovelSummary_ReturnsWithoutChildren()
    {
        var novel = new Novel { Id = "sum001", Title = "摘要测试" };
        await _store.SaveNovel(novel);

        var summary = await _store.GetNovelSummary("sum001");
        Assert.NotNull(summary);
        Assert.Equal("摘要测试", summary!.Title);
        Assert.Empty(summary.Chapters); // 不含子实体
    }

    // ══════════════════════════════════════════════════════
    //  Chapter CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveChapter_And_GetChapters_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "ch001", Title = "章节目录测试" });

        var chapter = new Chapter
        {
            Id = "ch0001",
            NovelId = "ch001",
            Title = "第一章",
            Order = 1,
            Content = "这是第一章的内容。",
            Status = ChapterStatus.Draft,
        };
        await _store.SaveChapter(chapter);

        var chapters = await _store.GetChapters("ch001");
        Assert.Single(chapters);
        Assert.Equal("第一章", chapters[0].Title);
        Assert.Equal("这是第一章的内容。", chapters[0].Content);
        Assert.Equal(ChapterStatus.Draft, chapters[0].Status);
    }

    [Fact]
    public async Task SaveChapter_UpdatesContent()
    {
        await _store.SaveNovel(new Novel { Id = "ch002", Title = "更新章节测试" });

        var chapter = new Chapter
        {
            Id = "ch0002",
            NovelId = "ch002",
            Title = "初稿",
            Order = 1,
            Content = "原始内容",
        };
        await _store.SaveChapter(chapter);

        chapter.Content = "更新内容";
        await _store.SaveChapter(chapter);

        var loaded = await _store.GetChapter("ch002", "ch0002");
        Assert.Equal("更新内容", loaded!.Content);
        Assert.Equal(ChapterStatus.Draft, loaded.Status);
    }

    [Fact]
    public async Task SaveChapter_StatusChangeBypassingLifecycle_Throws()
    {
        await _store.SaveNovel(new Novel { Id = "ch002b", Title = "状态门禁测试" });
        var chapter = new Chapter
        {
            Id = "ch0002b",
            NovelId = "ch002b",
            Title = "初稿",
            Order = 1,
            Content = "原始内容",
        };
        await _store.SaveChapter(chapter);

        chapter.Status = ChapterStatus.Published;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.SaveChapter(chapter)
        );
        Assert.Contains("ChapterLifecycleService", exception.Message);

        // 抑制位下允许（发布事务内部使用）
        using (Core.Evolution.OntologyWriteContext.SuppressChapterLifecycle())
        {
            await _store.SaveChapter(chapter);
        }
        Assert.Equal(
            ChapterStatus.Published,
            (await _store.GetChapter("ch002b", "ch0002b"))!.Status
        );
    }

    [Fact]
    public async Task GetChapterCount_ReturnsCorrectCount()
    {
        await _store.SaveNovel(new Novel { Id = "ch003", Title = "计数测试" });

        await _store.SaveChapter(
            new Chapter
            {
                Id = "a1",
                NovelId = "ch003",
                Title = "1",
                Order = 1,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "a2",
                NovelId = "ch003",
                Title = "2",
                Order = 2,
            }
        );

        var count = await _store.GetChapterCount("ch003");
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task DeleteChapter_RemovesChapter()
    {
        await _store.SaveNovel(new Novel { Id = "ch004", Title = "删除章测试" });

        var chapter = new Chapter
        {
            Id = "c001",
            NovelId = "ch004",
            Title = "删除目标",
            Order = 1,
        };
        await _store.SaveChapter(chapter);
        await _store.DeleteChapter("ch004", "c001");

        var chapters = await _store.GetChapters("ch004");
        Assert.Empty(chapters);
    }

    // ══════════════════════════════════════════════════════
    //  Character CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveCharacter_And_GetCharacters_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "cn001", Title = "角色测试" });

        var character = new Character
        {
            Id = "c0001",
            NovelId = "cn001",
            Name = "林默",
            Gender = "男",
            Age = "25",
            Role = CharacterRole.Protagonist,
            Personality = "沉默寡言，内心坚韧",
            Appearance = "黑发黑瞳，身材修长",
            Background = "出身平凡",
            Motivation = "寻找失散家人",
            Tags = ["主角", "剑修"],
            Abilities = "剑术精通",
            Weakness = "过于信任他人",
            Habits = "习惯擦拭剑刃",
            Notes = "核心角色",
            Status = CharacterStatus.Active,
            FactionId = null,
        };
        await _store.SaveCharacter(character);

        var chars = await _store.GetCharacters("cn001");
        Assert.Single(chars);
        Assert.Equal("林默", chars[0].Name);
        Assert.Equal(CharacterRole.Protagonist, chars[0].Role);
        Assert.Equal(["主角", "剑修"], chars[0].Tags);
    }

    [Fact]
    public async Task GetCharacterCount_ReturnsCorrectCount()
    {
        await _store.SaveNovel(new Novel { Id = "cn002", Title = "角色计数" });

        await _store.SaveCharacter(
            new Character
            {
                Id = "ch1",
                NovelId = "cn002",
                Name = "A",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "ch2",
                NovelId = "cn002",
                Name = "B",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "ch3",
                NovelId = "cn002",
                Name = "C",
            }
        );

        var count = await _store.GetCharacterCount("cn002");
        Assert.Equal(3, count);
    }

    // ══════════════════════════════════════════════════════
    //  Plotline CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SavePlotline_And_GetPlotlines_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "pl001", Title = "情节线测试" });

        var plotline = new Plotline
        {
            Id = "p0001",
            NovelId = "pl001",
            Name = "主线：寻找神器",
            Description = "主角寻找失落神器的旅程",
            Type = PlotlineType.Main,
            Tension = PlotlineTension.High,
            Turnpoints = "第3章发现线索；第7章获得神器",
        };
        await _store.SavePlotline(plotline);

        var plotlines = await _store.GetPlotlines("pl001");
        Assert.Single(plotlines);
        Assert.Equal("主线：寻找神器", plotlines[0].Name);
        Assert.Equal(PlotlineType.Main, plotlines[0].Type);
        Assert.Equal(PlotlineTension.High, plotlines[0].Tension);
    }

    [Fact]
    public async Task GetOpenPlotlines_ReturnsUnresolved()
    {
        await _store.SaveNovel(new Novel { Id = "pl002", Title = "未解决情节" });

        await _store.SavePlotline(
            new Plotline
            {
                Id = "open1",
                NovelId = "pl002",
                Name = "开放线",
                Type = PlotlineType.Main,
            }
        );
        await _store.SavePlotline(
            new Plotline
            {
                Id = "closed1",
                NovelId = "pl002",
                Name = "已解决",
                Type = PlotlineType.Side,
                ResolvedAtChapterId = "chapter_1",
            }
        );

        var open = await _store.GetOpenPlotlines("pl002");
        // GetOpenPlotlines filters by ResolvedAtChapterId == null.
        // YAML null now correctly roundtrips (fixed in YamlHelper.FromYamlNode).
        Assert.Single(open);
        Assert.Equal("开放线", open[0].Name);
    }

    // ══════════════════════════════════════════════════════
    //  Faction CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveFaction_And_GetFactions_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "fac001", Title = "组织测试" });

        var faction = new Faction
        {
            Id = "f0001",
            NovelId = "fac001",
            Name = "青云宗",
            Type = FactionType.Sect,
            Description = "修仙界第一宗门",
            Leader = "青云真人",
            Goal = "维护修仙界秩序",
            Members = ["m001", "m002"],
        };
        await _store.SaveFaction(faction);

        var factions = await _store.GetFactions("fac001");
        Assert.Single(factions);
        Assert.Equal("青云宗", factions[0].Name);
        Assert.Equal(FactionType.Sect, factions[0].Type);
        Assert.Equal(["m001", "m002"], factions[0].Members);
    }

    // ══════════════════════════════════════════════════════
    //  Location CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveLocation_And_GetLocations_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "loc001", Title = "地点测试" });

        var location = new Location
        {
            Id = "l0001",
            NovelId = "loc001",
            Name = "青云峰",
            Type = LocationType.Wilderness,
            Atmosphere = "仙气缭绕，宁静庄严",
            Description = "青云宗主峰，高耸入云",
        };
        await _store.SaveLocation(location);

        var locations = await _store.GetLocations("loc001");
        Assert.Single(locations);
        Assert.Equal("青云峰", locations[0].Name);
        Assert.Equal(LocationType.Wilderness, locations[0].Type);
    }

    // ══════════════════════════════════════════════════════
    //  Item CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveItem_And_GetItems_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "it001", Title = "道具测试" });

        var item = new Item
        {
            Id = "i0001",
            NovelId = "it001",
            Name = "青云剑",
            Type = ItemType.Weapon,
            Description = "青云宗镇宗之宝",
            OwnerId = "c0001",
        };
        await _store.SaveItem(item);

        var items = await _store.GetItems("it001");
        Assert.Single(items);
        Assert.Equal("青云剑", items[0].Name);
        Assert.Equal(ItemType.Weapon, items[0].Type);
        Assert.Equal("c0001", items[0].OwnerId);
    }

    // ══════════════════════════════════════════════════════
    //  Relationship CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveRelationship_And_GetRelationshipsByNovel_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "rel001", Title = "关系测试" });
        await _store.SaveCharacter(
            new Character
            {
                Id = "rch1",
                NovelId = "rel001",
                Name = "角色A",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "rch2",
                NovelId = "rel001",
                Name = "角色B",
            }
        );

        var rel = new Relationship
        {
            Id = "r0001",
            NovelId = "rel001",
            CharacterAId = "rch1",
            CharacterBId = "rch2",
            Type = RelationshipType.Mentor,
            Strength = RelationshipStrength.Strong,
            Description = "师徒关系",
        };
        await _store.SaveRelationship(rel);

        var rels = await _store.GetRelationshipsByNovel("rel001");
        Assert.Single(rels);
        Assert.Equal(RelationshipType.Mentor, rels[0].Type);
        Assert.Equal(RelationshipStrength.Strong, rels[0].Strength);

        // CHANGE_14：多词关系类型以 kebab 落盘（无分隔符历史写法不再产生），回读一致
        var parentRel = new Relationship
        {
            Id = "r0002",
            NovelId = "rel001",
            CharacterAId = "rch1",
            CharacterBId = "rch2",
            Type = RelationshipType.ParentOf,
            Strength = RelationshipStrength.Medium,
            Description = "父子",
        };
        await _store.SaveRelationship(parentRel);

        var relFile = Directory
            .GetFiles(_tempRoot, "relationships.yaml", SearchOption.AllDirectories)
            .Single();
        var yaml = await File.ReadAllTextAsync(relFile, TestContext.Current.CancellationToken);
        Assert.Contains("type: parent-of", yaml);

        var reloaded = await _store.GetRelationshipsByNovel("rel001");
        Assert.Equal(RelationshipType.ParentOf, reloaded.Single(r => r.Id == "r0002").Type);
    }

    // ══════════════════════════════════════════════════════
    //  TimelineEvent CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveTimelineEvent_And_Get_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "tl001", Title = "时间线测试" });

        var evt = new TimelineEvent
        {
            Id = "t0001",
            NovelId = "tl001",
            Title = "主角出生",
            Description = "在月圆之夜，主角诞生",
            ChapterId = "chapter01",
        };
        await _store.SaveTimelineEvent(evt);

        var events = await _store.GetTimelineEvents("tl001");
        Assert.Single(events);
        Assert.Equal("主角出生", events[0].Title);
    }

    // ══════════════════════════════════════════════════════
    //  SceneDefinition CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveScene_And_GetScenesByNovel_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "sc001", Title = "场景测试" });

        var scene = new SceneDefinition
        {
            Id = "s0001",
            NovelId = "sc001",
            Title = "入门试炼",
            Order = 1,
            ChapterId = "ch0001",
            LocationId = "l0001",
            TimeDescription = "清晨，雾气弥漫",
            ParticipantIds = ["c0001", "c0002"],
            PlotlineIds = ["p0001"],
            Conflict = "试炼对抗",
            Goal = "通过入门考核",
            EmotionalArc = "紧张→突破→喜悦",
            SceneType = SceneType.Action,
            Notes = "关键转折场景",
        };
        await _store.SaveScene(scene);

        var scenes = await _store.GetScenesByNovel("sc001");
        Assert.Single(scenes);
        Assert.Equal("入门试炼", scenes[0].Title);
        Assert.Equal(SceneType.Action, scenes[0].SceneType);
        Assert.Equal(["c0001", "c0002"], scenes[0].ParticipantIds);
    }

    // ══════════════════════════════════════════════════════
    //  FactionRelationship CRUD
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveFactionRelationship_And_Get_Roundtrip()
    {
        await _store.SaveNovel(new Novel { Id = "fr001", Title = "组织关系测试" });

        var rel = new FactionRelationship
        {
            NovelId = "fr001",
            FactionAId = "f1",
            FactionBId = "f2",
            Type = FactionRelationType.Ally,
            Description = "攻守同盟",
        };
        await _store.SaveFactionRelationship(rel);

        var rels = await _store.GetFactionRelationships("fr001");
        Assert.Single(rels);
        Assert.Equal(FactionRelationType.Ally, rels[0].Type);
    }

    // ══════════════════════════════════════════════════════
    //  Novel with All Children
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task GetNovel_LoadsAllChildEntities()
    {
        var novelId = "full001";
        await _store.SaveNovel(new Novel { Id = novelId, Title = "完整小说" });

        await _store.SaveChapter(
            new Chapter
            {
                Id = "fc1",
                NovelId = novelId,
                Title = "Ch1",
                Order = 1,
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "fch1",
                NovelId = novelId,
                Name = "角色1",
            }
        );
        await _store.SavePlotline(
            new Plotline
            {
                Id = "fpl1",
                NovelId = novelId,
                Name = "情节1",
            }
        );
        await _store.SaveFaction(
            new Faction
            {
                Id = "ff1",
                NovelId = novelId,
                Name = "组织1",
            }
        );
        await _store.SaveLocation(
            new Location
            {
                Id = "fl1",
                NovelId = novelId,
                Name = "地点1",
            }
        );
        await _store.SaveItem(
            new Item
            {
                Id = "fi1",
                NovelId = novelId,
                Name = "道具1",
            }
        );
        await _store.SaveTimelineEvent(
            new TimelineEvent
            {
                Id = "ft1",
                NovelId = novelId,
                Title = "事件1",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "fch2",
                NovelId = novelId,
                Name = "角色2",
            }
        );
        await _store.SaveRelationship(
            new Relationship
            {
                Id = "fr1",
                NovelId = novelId,
                CharacterAId = "fch1",
                CharacterBId = "fch2",
            }
        );
        await _store.SaveScene(
            new SceneDefinition
            {
                Id = "fs1",
                NovelId = novelId,
                Title = "场景1",
                Order = 1,
            }
        );

        var novel = await _store.GetNovel(novelId);
        Assert.NotNull(novel);
        Assert.Single(novel!.Chapters);
        Assert.Equal(2, novel.Characters.Count);
        Assert.Single(novel.Plotlines);
        Assert.Single(novel.Factions);
        Assert.Single(novel.Locations);
        Assert.Single(novel.Items);
        Assert.Single(novel.TimelineEvents);
        Assert.Single(novel.Relationships);
        Assert.Single(novel.Scenes);
    }

    // ══════════════════════════════════════════════════════
    //  Entity Change Events
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SaveNovel_FiresEntityChangedEvent()
    {
        var fired = false;
        EntityChangeEvent? captured = null;
        _store.OnEntityChanged += evt =>
        {
            fired = true;
            captured = evt;
            return Task.CompletedTask;
        };

        await _store.SaveNovel(new Novel { Id = "evt001", Title = "事件测试" });

        Assert.True(fired);
        Assert.NotNull(captured);
        Assert.Equal(EntityType.Novel, captured!.EntityType);
        Assert.Equal("evt001", captured.EntityId);
    }

    [Fact]
    public async Task SaveCharacter_New_FiresCreateEvent()
    {
        await _store.SaveNovel(new Novel { Id = "evt002", Title = "事件测试2" });

        EntityChangeEvent? captured = null;
        _store.OnEntityChanged += evt =>
        {
            captured = evt;
            return Task.CompletedTask;
        };

        await _store.SaveCharacter(
            new Character
            {
                Id = "ec1",
                NovelId = "evt002",
                Name = "测试角色",
            }
        );

        Assert.NotNull(captured);
        Assert.Equal(AuditAction.Create, captured!.Action);
    }

    [Fact]
    public async Task SaveCharacter_Update_FiresUpdateEvent()
    {
        await _store.SaveNovel(new Novel { Id = "evt003", Title = "事件测试3" });

        await _store.SaveCharacter(
            new Character
            {
                Id = "ec2",
                NovelId = "evt003",
                Name = "原始名",
            }
        );

        EntityChangeEvent? captured = null;
        _store.OnEntityChanged += evt =>
        {
            captured = evt;
            return Task.CompletedTask;
        };

        await _store.SaveCharacter(
            new Character
            {
                Id = "ec2",
                NovelId = "evt003",
                Name = "新名字",
            }
        );

        Assert.NotNull(captured);
        Assert.Equal(AuditAction.Update, captured!.Action);
    }

    [Fact]
    public async Task WorldRules_SaveLoadDelete_RoundTrip()
    {
        // CHANGE_10 5.3：WorldRule 持久化（world-rules.yaml，独立于 novel.yaml）
        var novel = new Novel { Id = "wr-novel", Title = "规则测试" };
        await _store.SaveNovel(novel);

        var rule = new WorldRule
        {
            Id = "wr1",
            NovelId = "wr-novel",
            Type = "first_birth_age_min",
            Value = 16,
            Unit = "岁",
            Description = "凡人寿命起点",
        };
        await _store.SaveWorldRule("wr-novel", rule);

        var loaded = await _store.GetWorldRules("wr-novel");
        var found = Assert.Single(loaded);
        Assert.Equal("wr1", found.Id);
        Assert.Equal("first_birth_age_min", found.Type);
        Assert.Equal(16, found.Value);
        Assert.Equal("岁", found.Unit);
        Assert.Equal(WorldRuleEntryKind.Rule, found.EntryKind);
        Assert.Equal(WorldRuleCategory.Other, found.Category);
        Assert.Equal("", found.Content);

        // 设定条目与规则同存 world-rules.yaml，多行正文往返
        await _store.SaveWorldRule(
            "wr-novel",
            new WorldRule
            {
                Id = "set1",
                NovelId = "wr-novel",
                EntryKind = WorldRuleEntryKind.Setting,
                Category = WorldRuleCategory.Social,
                Description = "宗门礼制",
                Content = "外门弟子不得御剑。\n内门弟子可申请。",
            }
        );
        loaded = await _store.GetWorldRules("wr-novel");
        Assert.Equal(2, loaded.Count);
        var setting = Assert.Single(loaded.Where(r => r.Id == "set1"));
        Assert.Equal(WorldRuleEntryKind.Setting, setting.EntryKind);
        Assert.Equal(WorldRuleCategory.Social, setting.Category);
        Assert.Equal("宗门礼制", setting.Description);
        Assert.Equal("外门弟子不得御剑。\n内门弟子可申请。", setting.Content);

        // Upsert 同 Id 覆盖
        rule.Value = 18;
        await _store.SaveWorldRule("wr-novel", rule);
        loaded = await _store.GetWorldRules("wr-novel");
        Assert.Equal(2, loaded.Count);
        Assert.Equal(18, loaded.Single(r => r.Id == "wr1").Value);

        await _store.DeleteWorldRule("wr-novel", "wr1");
        await _store.DeleteWorldRule("wr-novel", "set1");
        Assert.Empty(await _store.GetWorldRules("wr-novel"));
    }
}
