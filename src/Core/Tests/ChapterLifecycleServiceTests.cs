using Core.Data;
using Core.Evolution;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 章节状态变更全生命周期：发布预检、联动应用、撤回回滚、补记、引用守卫、设定变更与跨会话摘要。
/// </summary>
[Collection("FileNovelStore")]
public sealed class ChapterLifecycleServiceTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly FileNovelStore _store;
    private readonly ChapterLifecycleService _service;

    public ChapterLifecycleServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _store = new FileNovelStore(_tempRoot, Path.Combine(_tempRoot, ".mythology"));
        _service = new ChapterLifecycleService(_store);
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

    private async Task<Chapter> SeedAsync()
    {
        var novel = new Novel
        {
            Id = "nov001",
            Title = "生命周期测试",
            Characters =
            [
                new Character
                {
                    Id = "char1",
                    NovelId = "nov001",
                    Name = "林长歌",
                    Status = CharacterStatus.Active,
                    FactionId = "fac1",
                    CurrentLocationId = "loc1",
                },
                new Character
                {
                    Id = "char2",
                    NovelId = "nov001",
                    Name = "沈月",
                    FactionId = "fac1",
                },
            ],
            Factions =
            [
                new Faction
                {
                    Id = "fac1",
                    NovelId = "nov001",
                    Name = "青云宗",
                    Status = FactionStatus.Active,
                    Leader = "char1",
                    Members = ["char1", "char2"],
                    HeadquartersId = "loc1",
                },
            ],
            Items =
            [
                new Item
                {
                    Id = "item1",
                    NovelId = "nov001",
                    Name = "青锋剑",
                    OwnerId = "char1",
                    LocationId = "loc1",
                },
            ],
            Locations =
            [
                new Location
                {
                    Id = "loc1",
                    NovelId = "nov001",
                    Name = "青云山",
                },
                new Location
                {
                    Id = "loc2",
                    NovelId = "nov001",
                    Name = "山门",
                },
            ],
            Plotlines =
            [
                new Plotline
                {
                    Id = "pl1",
                    NovelId = "nov001",
                    Name = "复仇",
                },
            ],
            Relationships =
            [
                new Relationship
                {
                    Id = "rel1",
                    NovelId = "nov001",
                    CharacterAId = "char1",
                    CharacterBId = "char2",
                    Type = RelationshipType.Friend,
                },
            ],
        };
        await _store.SaveNovel(novel);
        foreach (var character in novel.Characters)
            await _store.SaveCharacter(character);
        foreach (var faction in novel.Factions)
            await _store.SaveFaction(faction);
        foreach (var item in novel.Items)
            await _store.SaveItem(item);
        foreach (var location in novel.Locations)
            await _store.SaveLocation(location);
        foreach (var plotline in novel.Plotlines)
            await _store.SavePlotline(plotline);
        foreach (var relationship in novel.Relationships)
            await _store.SaveRelationship(relationship);

        var chapter = new Chapter
        {
            Id = "ch12",
            NovelId = "nov001",
            Title = "第十二章",
            Order = 12,
            Content = "风起云涌。",
            Status = ChapterStatus.Draft,
            PlotlineIds = ["pl1"],
        };
        await _store.SaveChapter(chapter);

        await _store.SaveScene(
            new SceneDefinition
            {
                Id = "scene1",
                NovelId = "nov001",
                ChapterId = chapter.Id,
                Title = "山巅对决",
                Order = 1,
                LocationId = "loc1",
                TimeDescription = "黄昏",
                ParticipantIds = ["char1", "char2"],
                PlotlineIds = ["pl1"],
            }
        );

        await _store.SaveEntityStateChanges("nov001", chapter.Id, BuildPending(chapter));
        return chapter;
    }

    private static List<EntityStateChange> BuildPending(Chapter chapter)
    {
        EntityStateChange Make(
            int sequence,
            string targetId,
            EntityType type,
            EvolutionAspect aspect,
            params StateEffect[] effects
        ) =>
            new()
            {
                Id = $"ev{sequence}",
                NovelId = chapter.NovelId,
                TargetEntityType = type,
                TargetId = targetId,
                ChapterId = chapter.Id,
                Aspect = aspect,
                Summary = $"{type}:{targetId} @{sequence}",
                Source = AuditSource.Ai,
                Effects = [.. effects],
                Sequence = sequence,
                Cause = "章内冲突",
                Visibility = ChangeVisibility.Public,
                WitnessIds = ["char2"],
                LocationId = "loc1",
                SceneId = "scene1",
                TimeDescription = "黄昏",
                PlotlineIds = ["pl1"],
                Lifecycle = ChangeLifecycle.Pending,
            };

        return
        [
            Make(
                1,
                "char1",
                EntityType.Character,
                EvolutionAspect.Status,
                new StateEffect
                {
                    TargetType = EntityType.Character,
                    TargetId = "char1",
                    Field = "status",
                    Operation = StateEffectOperation.Set,
                    Value = "deceased",
                    From = "active",
                }
            ),
            Make(
                2,
                "char1",
                EntityType.Character,
                EvolutionAspect.Relationship,
                new StateEffect
                {
                    TargetType = EntityType.Character,
                    TargetId = "char1",
                    Field = "faction-id",
                    Operation = StateEffectOperation.Clear,
                    From = "fac1",
                }
            ),
            Make(
                3,
                "item1",
                EntityType.Item,
                EvolutionAspect.Possession,
                new StateEffect
                {
                    TargetType = EntityType.Item,
                    TargetId = "item1",
                    Field = "owner-id",
                    Operation = StateEffectOperation.Clear,
                    From = "char1",
                }
            ),
            Make(
                4,
                "fac1",
                EntityType.Faction,
                EvolutionAspect.Relationship,
                new StateEffect
                {
                    TargetType = EntityType.Faction,
                    TargetId = "fac1",
                    Field = "leader",
                    Operation = StateEffectOperation.Clear,
                    From = "char1",
                }
            ),
            Make(
                5,
                "loc1",
                EntityType.Location,
                EvolutionAspect.Location,
                new StateEffect
                {
                    TargetType = EntityType.Location,
                    TargetId = "loc1",
                    Field = "status",
                    Operation = StateEffectOperation.Set,
                    Value = "destroyed",
                }
            ),
            Make(
                6,
                "pl1",
                EntityType.Plotline,
                EvolutionAspect.Status,
                new StateEffect
                {
                    TargetType = EntityType.Plotline,
                    TargetId = "pl1",
                    Field = "status",
                    Operation = StateEffectOperation.Set,
                    Value = "resolved",
                }
            ),
        ];
    }

    [Fact]
    public async Task PendingChanges_DoNotAffectEntities_UntilPublished()
    {
        var chapter = await SeedAsync();

        var character = await _store.GetCharacter("nov001", "char1");
        Assert.Equal(CharacterStatus.Active, character!.Status);
        Assert.Equal("fac1", character.FactionId);

        var view = await EntityTimelineView.BuildAsync(_store, "nov001");
        var map = new Dictionary<string, int> { [chapter.Id] = chapter.Order };
        var asOf = view.GetStateAsOf(EntityType.Character, "char1", map, chapter.Order);
        Assert.False(asOf.ContainsKey(EvolutionAspect.Status));
    }

    [Fact]
    public async Task Preview_ReportsPendingAndAutomaticCascades()
    {
        var chapter = await SeedAsync();
        var preview = await _service.PreviewPublishAsync("nov001", chapter.Id);

        Assert.False(preview.HasBlockingIssues);
        Assert.Equal(6, preview.PendingChanges.Count);
        Assert.Empty(preview.DetectedDeaths);

        Assert.Contains(
            preview.Suggestions,
            s =>
                s.TargetType == EntityType.Relationship
                && s.Field == "until-chapter-id"
                && s.IsAutomatic
        );
        Assert.Contains(
            preview.Suggestions,
            s => s.TargetType == EntityType.Faction && s.Field == "members" && s.IsAutomatic
        );
        Assert.True(
            preview.Suggestions.Any(s =>
                s.TargetType == EntityType.Item && s.Field == "status" && s.IsAutomatic
            ),
            string.Join("\n", preview.Suggestions.Select(s => $"{s.Id} auto={s.IsAutomatic}"))
        );
        Assert.Contains(
            preview.Suggestions,
            s =>
                s.TargetType == EntityType.Plotline
                && s.Field == "resolved-at-chapter-id"
                && s.IsAutomatic
        );
    }

    [Fact]
    public async Task Publish_AppliesEffectsAndCascades_ThenUnpublishRollsBack()
    {
        var chapter = await SeedAsync();
        var result = await _service.PublishAsync("nov001", chapter.Id, new PublishOptions());
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ChangeSetId);
        Assert.True(result.Warnings.Count == 0, string.Join(" | ", result.Warnings));

        var character = await _store.GetCharacter("nov001", "char1");
        Assert.Equal(CharacterStatus.Deceased, character!.Status);
        Assert.Null(character.FactionId);
        Assert.Null(character.CurrentLocationId);

        var faction = await _store.GetFaction("nov001", "fac1");
        Assert.NotEqual("char1", faction!.Leader);
        Assert.DoesNotContain("char1", faction.Members);
        Assert.Null(faction.HeadquartersId);

        var item = await _store.GetItem("nov001", "item1");
        Assert.Null(item!.OwnerId);
        Assert.Equal(ItemStatus.Lost, item.Status);

        var location = await _store.GetLocation("nov001", "loc1");
        Assert.Equal(LocationStatus.Destroyed, location!.Status);

        var plotline = await _store.GetPlotline("nov001", "pl1");
        Assert.Equal(PlotlineStatus.Resolved, plotline!.Status);
        Assert.Equal(chapter.Id, plotline.ResolvedAtChapterId);

        var relationship = await _store.GetRelationship("nov001", "rel1");
        Assert.Equal(chapter.Id, relationship!.UntilChapterId);

        Assert.Equal(
            ChapterStatus.Published,
            (await _store.GetChapter("nov001", chapter.Id))!.Status
        );
        var sets = await _store.GetChapterChangeSets("nov001", chapter.Id);
        Assert.Single(sets);
        Assert.Equal(ChangeSetStatus.Applied, sets[0].Status);
        Assert.Contains(sets[0].Entries, e => e.Kind == ChangeSetEntryKind.StateChange);
        Assert.Contains(sets[0].Entries, e => e.Kind == ChangeSetEntryKind.EntityUpdate);

        var records = await _store.GetEntityStateChangesByChapter("nov001", chapter.Id);
        Assert.All(
            records,
            r =>
            {
                Assert.Equal(ChangeLifecycle.Committed, r.Lifecycle);
                Assert.Equal(sets[0].Id, r.ChangeSetId);
                Assert.NotNull(r.CommittedAt);
            }
        );
        Assert.All(records, r => Assert.Equal("loc1", r.LocationId));
        Assert.All(records, r => Assert.Contains("pl1", r.PlotlineIds));

        // 撤回：全部恢复
        var unpublish = await _service.UnpublishAsync("nov001", chapter.Id);
        Assert.True(unpublish.Success, unpublish.Error);

        Assert.Equal(
            CharacterStatus.Active,
            (await _store.GetCharacter("nov001", "char1"))!.Status
        );
        Assert.Equal("fac1", (await _store.GetCharacter("nov001", "char1"))!.FactionId);
        Assert.Equal("char1", (await _store.GetFaction("nov001", "fac1"))!.Leader);
        Assert.Equal("char1", (await _store.GetItem("nov001", "item1"))!.OwnerId);
        Assert.Equal(ItemStatus.Active, (await _store.GetItem("nov001", "item1"))!.Status);
        Assert.Equal(LocationStatus.Active, (await _store.GetLocation("nov001", "loc1"))!.Status);
        Assert.Equal(PlotlineStatus.Active, (await _store.GetPlotline("nov001", "pl1"))!.Status);
        Assert.Null((await _store.GetRelationship("nov001", "rel1"))!.UntilChapterId);
        Assert.Equal(ChapterStatus.Draft, (await _store.GetChapter("nov001", chapter.Id))!.Status);

        var reverted = (await _store.GetChapterChangeSets("nov001", chapter.Id))[0];
        Assert.Equal(ChangeSetStatus.Reverted, reverted.Status);
        Assert.All(
            await _store.GetEntityStateChangesByChapter("nov001", chapter.Id),
            r => Assert.Equal(ChangeLifecycle.Pending, r.Lifecycle)
        );
        var actions = await _store.GetOntologyActions("nov001");
        Assert.Contains(
            actions,
            a => a.Operation == OntologyOperation.Revert && a.ChangeSetId == reverted.Id
        );

        // 幂等：再次发布/撤回
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );
        Assert.True((await _service.UnpublishAsync("nov001", chapter.Id)).Success);
    }

    [Fact]
    public async Task Publish_InvalidEffect_FailsWithoutWriting()
    {
        var chapter = await SeedAsync();
        var records = await _store.GetEntityStateChangesByChapter("nov001", chapter.Id);
        records[0].Effects = [new StateEffect { TargetId = "char1", Field = "not-a-field" }];
        await _store.SaveEntityStateChanges("nov001", chapter.Id, records);

        var result = await _service.PublishAsync("nov001", chapter.Id, new PublishOptions());
        Assert.False(result.Success);
        Assert.Contains("不支持的状态字段", result.Error);
        Assert.Equal(ChapterStatus.Draft, (await _store.GetChapter("nov001", chapter.Id))!.Status);
        Assert.Equal(
            CharacterStatus.Active,
            (await _store.GetCharacter("nov001", "char1"))!.Status
        );
    }

    [Fact]
    public async Task ManualModifiedField_ProducesWarningNotBlock()
    {
        var chapter = await SeedAsync();
        // 人工把角色状态改成失踪（设定变更），发布记录的 From=active 已不匹配
        using (OntologyWriteContext.BeginUser("session-a"))
        {
            var character = await _store.GetCharacter("nov001", "char1");
            character!.Status = CharacterStatus.Missing;
            await _store.SaveCharacter(character);
        }

        var preview = await _service.PreviewPublishAsync("nov001", chapter.Id);
        Assert.False(preview.HasBlockingIssues);
        Assert.Contains(
            preview.ValidationIssues,
            i => i.Code == "manual-modified" && !i.BlocksPublish
        );
    }

    [Fact]
    public async Task Amendment_OnPublishedChapter_AppliesImmediately_AndReverts()
    {
        var chapter = await SeedAsync();
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );

        var amendment = new EntityStateChange
        {
            Id = "amend1",
            NovelId = "nov001",
            TargetEntityType = EntityType.Character,
            TargetId = "char2",
            ChapterId = chapter.Id,
            Aspect = EvolutionAspect.Status,
            Summary = "沈月失踪",
            Source = AuditSource.Ai,
            Effects =
            [
                new StateEffect
                {
                    TargetType = EntityType.Character,
                    TargetId = "char2",
                    Field = "status",
                    Operation = StateEffectOperation.Set,
                    Value = "missing",
                },
            ],
            Sequence = 99,
            Lifecycle = ChangeLifecycle.Pending,
        };
        var records = await _store.GetEntityStateChangesByChapter("nov001", chapter.Id);
        records.Add(amendment);
        await _store.SaveEntityStateChanges("nov001", chapter.Id, records);

        var result = await _service.ApplyAmendmentAsync("nov001", chapter.Id, "amend1");
        Assert.True(result.Success, result.Error);
        Assert.Equal(
            CharacterStatus.Missing,
            (await _store.GetCharacter("nov001", "char2"))!.Status
        );

        // 补记后撤回：回到待发布
        Assert.True((await _service.UnpublishAsync("nov001", chapter.Id)).Success);
        Assert.Equal(
            CharacterStatus.Active,
            (await _store.GetCharacter("nov001", "char2"))!.Status
        );
        var pending = await _store.GetEntityStateChangesByChapter("nov001", chapter.Id);
        Assert.All(pending, r => Assert.Equal(ChangeLifecycle.Pending, r.Lifecycle));
    }

    [Fact]
    public async Task Unpublish_LegacyChapterWithoutChangeSets_OnlyFlipsStatus()
    {
        var chapter = await SeedAsync();
        // 变更集机制上线前发布的章：直接置为 Published，无变更集
        using (OntologyWriteContext.SuppressChapterLifecycle())
        {
            var loaded = await _store.GetChapter("nov001", chapter.Id);
            loaded!.Status = ChapterStatus.Published;
            await _store.SaveChapter(loaded);
        }

        var result = await _service.UnpublishAsync("nov001", chapter.Id);
        Assert.True(result.Success, result.Error);
        Assert.Single(result.Warnings);
        Assert.Equal(ChapterStatus.Draft, (await _store.GetChapter("nov001", chapter.Id))!.Status);
        Assert.Equal(
            CharacterStatus.Active,
            (await _store.GetCharacter("nov001", "char1"))!.Status
        );
    }

    [Fact]
    public async Task Unpublish_BlockedByLaterPublishedChapter()
    {
        var chapter = await SeedAsync();
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch13",
                NovelId = "nov001",
                Title = "第十三章",
                Order = 13,
                Status = ChapterStatus.Draft,
            }
        );
        Assert.True((await _service.PublishAsync("nov001", "ch13", new PublishOptions())).Success);

        var preview = await _service.PreviewUnpublishAsync("nov001", chapter.Id);
        Assert.Single(preview.LaterPublishedChapters);
        var result = await _service.UnpublishAsync("nov001", chapter.Id);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Unpublish_BlockedWhenEntityModifiedAfterPublish()
    {
        var chapter = await SeedAsync();
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );

        var item = await _store.GetItem("nov001", "item1");
        item!.Description = "发布后人工修改";
        await _store.SaveItem(item);

        var preview = await _service.PreviewUnpublishAsync("nov001", chapter.Id);
        Assert.NotEmpty(preview.Conflicts);
        var result = await _service.UnpublishAsync("nov001", chapter.Id);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task DeleteChapter_PublishedChapter_UnpublishesFirst()
    {
        var chapter = await SeedAsync();
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );

        var result = await _service.DeleteChapterAsync("nov001", chapter.Id);
        Assert.True(result.Success, result.Error);
        Assert.Null(await _store.GetChapter("nov001", chapter.Id));
        Assert.Equal(
            CharacterStatus.Active,
            (await _store.GetCharacter("nov001", "char1"))!.Status
        );
    }

    [Fact]
    public async Task PublishedReferenceGuard_BlocksDelete_UntilUnpublish()
    {
        var chapter = await SeedAsync();
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.DeleteCharacter("nov001", "char2")
        );
        Assert.Contains("已发布章节引用", exception.Message);

        Assert.True((await _service.UnpublishAsync("nov001", chapter.Id)).Success);
        await _store.DeleteCharacter("nov001", "char2");
        Assert.Null(await _store.GetCharacter("nov001", "char2"));
    }

    [Fact]
    public async Task StateEffectApplier_DirectApply_SavesEntity()
    {
        var chapter = await SeedAsync();
        var novel = await _store.GetNovel("nov001");
        var result = await StateEffectApplier.ApplyAllAsync(
            _store,
            novel!,
            chapter,
            [
                new StateEffect
                {
                    TargetType = EntityType.Character,
                    TargetId = "char1",
                    Field = "status",
                    Operation = StateEffectOperation.Set,
                    Value = "deceased",
                },
            ]
        );
        Assert.True(result.Warnings.Count == 0, string.Join("|", result.Warnings));
        Assert.Single(result.Changed);
        Assert.Equal(
            CharacterStatus.Deceased,
            (await _store.GetCharacter("nov001", "char1"))!.Status
        );
    }

    [Fact]
    public async Task Unpublish_AfterAmendmentTouchingSameEntity_Succeeds()
    {
        var chapter = await SeedAsync();
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );

        // 补记改的正是发布事务已经改过的角色（char1）
        var amendment = new EntityStateChange
        {
            Id = "amend-same",
            NovelId = "nov001",
            TargetEntityType = EntityType.Character,
            TargetId = "char1",
            ChapterId = chapter.Id,
            Aspect = EvolutionAspect.Status,
            Summary = "林长歌复活",
            Source = AuditSource.Ai,
            Effects =
            [
                new StateEffect
                {
                    TargetType = EntityType.Character,
                    TargetId = "char1",
                    Field = "status",
                    Operation = StateEffectOperation.Set,
                    Value = "active",
                },
            ],
            Sequence = 98,
            Lifecycle = ChangeLifecycle.Pending,
        };
        var records = await _store.GetEntityStateChangesByChapter("nov001", chapter.Id);
        records.Add(amendment);
        await _store.SaveEntityStateChanges("nov001", chapter.Id, records);
        Assert.True(
            (await _service.ApplyAmendmentAsync("nov001", chapter.Id, "amend-same")).Success
        );

        var unpublish = await _service.UnpublishAsync("nov001", chapter.Id);
        Assert.True(unpublish.Success, unpublish.Error);
        Assert.Equal(
            CharacterStatus.Active,
            (await _store.GetCharacter("nov001", "char1"))!.Status
        );
    }

    [Fact]
    public async Task PublishFactionChange_UnpublishRestoresBothFactions()
    {
        var chapter = await SeedAsync();
        await _store.SaveFaction(
            new Faction
            {
                Id = "fac2",
                NovelId = "nov001",
                Name = "天机阁",
            }
        );
        // 只保留一条待发布变化：角色换组织
        await _store.SaveEntityStateChanges(
            "nov001",
            chapter.Id,
            [
                new EntityStateChange
                {
                    Id = "ev-faction",
                    NovelId = "nov001",
                    TargetEntityType = EntityType.Character,
                    TargetId = "char2",
                    ChapterId = chapter.Id,
                    Aspect = EvolutionAspect.Relationship,
                    Summary = "沈月转投天机阁",
                    Source = AuditSource.Ai,
                    Effects =
                    [
                        new StateEffect
                        {
                            TargetType = EntityType.Character,
                            TargetId = "char2",
                            Field = "faction-id",
                            Operation = StateEffectOperation.Set,
                            Value = "fac2",
                        },
                    ],
                    Sequence = 1,
                    Lifecycle = ChangeLifecycle.Pending,
                },
            ]
        );

        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );
        Assert.DoesNotContain("char2", (await _store.GetFaction("nov001", "fac1"))!.Members);
        Assert.Contains("char2", (await _store.GetFaction("nov001", "fac2"))!.Members);

        var unpublish = await _service.UnpublishAsync("nov001", chapter.Id);
        Assert.True(unpublish.Success, unpublish.Error);
        Assert.Contains("char2", (await _store.GetFaction("nov001", "fac1"))!.Members);
        Assert.DoesNotContain("char2", (await _store.GetFaction("nov001", "fac2"))!.Members);
        Assert.Equal("fac1", (await _store.GetCharacter("nov001", "char2"))!.FactionId);
    }

    [Fact]
    public async Task Preview_DoesNotEndRelationshipAlreadyEndedEarlier()
    {
        var chapter = await SeedAsync();
        var relationship = await _store.GetRelationship("nov001", "rel1");
        relationship!.UntilChapterId = "ch3";
        await _store.SaveRelationship(relationship);

        var preview = await _service.PreviewPublishAsync("nov001", chapter.Id);
        Assert.DoesNotContain(
            preview.Suggestions,
            s => s.TargetType == EntityType.Relationship && s.TargetId == "rel1"
        );
    }

    [Fact]
    public async Task Publish_MetricsOnPlotline_Rejected()
    {
        var chapter = await SeedAsync();
        await _store.SaveEntityStateChanges(
            "nov001",
            chapter.Id,
            [
                new EntityStateChange
                {
                    Id = "ev-metrics",
                    NovelId = "nov001",
                    TargetEntityType = EntityType.Plotline,
                    TargetId = "pl1",
                    ChapterId = chapter.Id,
                    Summary = "非法 metrics",
                    Source = AuditSource.Ai,
                    Effects =
                    [
                        new StateEffect
                        {
                            TargetType = EntityType.Plotline,
                            TargetId = "pl1",
                            Field = "metrics:x",
                            Operation = StateEffectOperation.Set,
                            Value = "1",
                        },
                    ],
                    Sequence = 1,
                    Lifecycle = ChangeLifecycle.Pending,
                },
            ]
        );

        var result = await _service.PublishAsync("nov001", chapter.Id, new PublishOptions());
        Assert.False(result.Success);
        Assert.Contains("不支持 metrics", result.Error);
    }

    [Fact]
    public async Task CharacterFactionChange_PersistsFactionMembership()
    {
        var chapter = await SeedAsync();
        var novel = await _store.GetNovel("nov001");
        await _store.SaveFaction(
            new Faction
            {
                Id = "fac2",
                NovelId = "nov001",
                Name = "天机阁",
            }
        );

        var result = await StateEffectApplier.ApplyAllAsync(
            _store,
            novel!,
            chapter,
            [
                new StateEffect
                {
                    TargetType = EntityType.Character,
                    TargetId = "char2",
                    Field = "faction-id",
                    Operation = StateEffectOperation.Set,
                    Value = "fac2",
                },
            ]
        );
        Assert.Empty(result.Warnings);
        var oldFaction = await _store.GetFaction("nov001", "fac1");
        var newFaction = await _store.GetFaction("nov001", "fac2");
        Assert.DoesNotContain("char2", oldFaction!.Members);
        Assert.Contains("char2", newFaction!.Members);
        Assert.Equal("fac2", (await _store.GetCharacter("nov001", "char2"))!.FactionId);
    }

    [Fact]
    public async Task ManualProseEdit_DoesNotCreateManualStateChange()
    {
        await SeedAsync();
        using (OntologyWriteContext.BeginUser("s1"))
        {
            var character = await _store.GetCharacter("nov001", "char1");
            character!.Personality = "更沉稳了";
            await _store.SaveCharacter(character);
        }
        Assert.Empty(await _store.GetManualStateChanges("nov001"));
    }

    [Fact]
    public async Task PublishedReferenceGuard_IgnoresManualStateChanges()
    {
        var chapter = await SeedAsync();
        await _store.SaveCharacter(
            new Character
            {
                Id = "char3",
                NovelId = "nov001",
                Name = "路人",
            }
        );
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );
        using (OntologyWriteContext.BeginUser("s1"))
        {
            var extra = await _store.GetCharacter("nov001", "char3");
            extra!.Status = CharacterStatus.Missing;
            await _store.SaveCharacter(extra);
        }

        // 设定变更（无章节归属）不构成已发布引用
        await _store.DeleteCharacter("nov001", "char3");
        Assert.Null(await _store.GetCharacter("nov001", "char3"));
    }

    // ── 设定变更（人工直接改状态） ──

    [Fact]
    public async Task ManualStatusChange_RecordsManualEntry_AndOverlaysAsOf()
    {
        var chapter = await SeedAsync();
        using (OntologyWriteContext.BeginUser("session-a"))
        {
            var character = await _store.GetCharacter("nov001", "char1");
            character!.Status = CharacterStatus.Deceased;
            await _store.SaveCharacter(character);
        }

        var manual = await _store.GetManualStateChanges("nov001");
        var record = Assert.Single(manual);
        Assert.Equal("", record.ChapterId);
        Assert.Equal(AuditSource.User, record.Source);
        Assert.Equal("session-a", record.SessionId);
        Assert.Equal(ChangeLifecycle.Committed, record.Lifecycle);
        Assert.Contains(record.Effects, e => e.Field == "status" && e.Value == "deceased");

        var view = await EntityTimelineView.BuildAsync(_store, "nov001");
        var map = new Dictionary<string, int> { [chapter.Id] = chapter.Order };
        var asOf = view.GetStateAsOf(EntityType.Character, "char1", map, int.MaxValue);
        Assert.Equal("deceased", asOf[EvolutionAspect.Status]);

        var actions = await _store.GetOntologyActions("nov001");
        Assert.Contains(
            actions,
            a =>
                a.TargetEntityType == EntityType.Character
                && a.Field == "status"
                && a.Source == AuditSource.User
                && a.To == "deceased"
        );

        var log = await EntityLogView.BuildAsync(_store, "nov001");
        var rows = log.Get(EntityType.Character, "char1");
        Assert.Contains(rows, r => r.ChapterOrder == 0 && r.Source == AuditSource.User);
    }

    [Fact]
    public async Task ManualOverlay_LosesToLaterPublishedChange_AndWinsAfterNewerManual()
    {
        var chapter = await SeedAsync();
        // 先人工死亡
        using (OntologyWriteContext.BeginUser("s1"))
        {
            var character = await _store.GetCharacter("nov001", "char1");
            character!.Status = CharacterStatus.Deceased;
            await _store.SaveCharacter(character);
        }
        var map = new Dictionary<string, int> { [chapter.Id] = chapter.Order };
        var view = await EntityTimelineView.BuildAsync(_store, "nov001");
        Assert.Equal(
            "deceased",
            view.GetStateAsOf(EntityType.Character, "char1", map, int.MaxValue)[
                EvolutionAspect.Status
            ]
        );

        // 发布叙事变化 status=active（时间更晚）
        var records = await _store.GetEntityStateChangesByChapter("nov001", chapter.Id);
        records[0].Effects[0].Value = "active";
        records[0].FromSummary = "deceased";
        records[0].ToSummary = "active";
        await _store.SaveEntityStateChanges("nov001", chapter.Id, records);
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );

        view = await EntityTimelineView.BuildAsync(_store, "nov001");
        Assert.Equal(
            "active",
            view.GetStateAsOf(EntityType.Character, "char1", map, int.MaxValue)[
                EvolutionAspect.Status
            ]
        );

        // 再人工改死亡（更晚）→ 覆盖
        await Task.Delay(5);
        using (OntologyWriteContext.BeginUser("s2"))
        {
            var character = await _store.GetCharacter("nov001", "char1");
            character!.Status = CharacterStatus.Deceased;
            await _store.SaveCharacter(character);
        }
        view = await EntityTimelineView.BuildAsync(_store, "nov001");
        Assert.Equal(
            "deceased",
            view.GetStateAsOf(EntityType.Character, "char1", map, int.MaxValue)[
                EvolutionAspect.Status
            ]
        );
    }

    [Fact]
    public async Task PublishTransaction_DoesNotCreateManualRecords()
    {
        var chapter = await SeedAsync();
        Assert.True(
            (await _service.PublishAsync("nov001", chapter.Id, new PublishOptions())).Success
        );
        Assert.Empty(await _store.GetManualStateChanges("nov001"));
    }

    // ── 跨会话摘要 ──

    [Fact]
    public async Task OntologyDigest_ReportsChangesSinceCursor_ThenEmpty()
    {
        var chapter = await SeedAsync();
        for (var i = 0; i < 3; i++)
        {
            using (OntologyWriteContext.Begin(AuditSource.Ai, "record_evolution", "session-a"))
            {
                var character = await _store.GetCharacter("nov001", "char2");
                character!.Notes = $"注{i}";
                await _store.SaveCharacter(character);
            }
        }

        Assert.Equal(0, await _store.GetOntologyCursorAsync("nov001"));
        var digest = await OntologyDigestBuilder.BuildAsync(_store, "nov001", "session-b");
        Assert.Contains("上次会话以来的本体变化", digest.Text);
        Assert.Contains("待发布变化", digest.Text);
        Assert.Equal(await _store.GetOntologyJournalStamp("nov001"), digest.NextOffset);

        await _store.SetOntologyCursorAsync("nov001", digest.NextOffset);
        var again = await OntologyDigestBuilder.BuildAsync(_store, "nov001", "session-b");
        Assert.Equal("", again.Text);

        using (OntologyWriteContext.Begin(AuditSource.Ai, "record_evolution", "session-b"))
        {
            var character = await _store.GetCharacter("nov001", "char2");
            character!.Notes = "新会话改动";
            await _store.SaveCharacter(character);
        }
        var incremental = await OntologyDigestBuilder.BuildAsync(_store, "nov001", "session-c");
        Assert.Contains("新会话改动", incremental.Text);
        Assert.Contains("上次会话", incremental.Text);
    }

    [Fact]
    public async Task OntologyActionsAfterOffset_MidLineOffset_DropsPartialLine()
    {
        var chapter = await SeedAsync();
        using (OntologyWriteContext.Begin(AuditSource.Ai, "record_evolution", "s"))
        {
            var character = await _store.GetCharacter("nov001", "char2");
            character!.Notes = "第一笔";
            await _store.SaveCharacter(character);
        }
        var full = await _store.GetOntologyActions("nov001");
        Assert.NotEmpty(full);

        // 从半行位置读：第一条残行丢弃，仍能读到后续完整行或不抛
        var actions = await _store.GetOntologyActionsAfterOffset("nov001", 5);
        Assert.NotNull(actions);

        // 偏移超出文件长度 → 按 0 处理
        var all = await _store.GetOntologyActionsAfterOffset("nov001", 999_999);
        Assert.Equal(full.Count, all.Count);
    }

    [Fact]
    public async Task DeathCandidateDetected_WhenDraftMentionsDeathWithoutRecord()
    {
        var chapter = await SeedAsync();
        Assert.NotNull(await _store.GetNovel("nov001"));
        var draft = await _store.GetChapter("nov001", chapter.Id);
        draft!.Content = "林长歌一刀斩落，林长歌死了。";
        await _store.SaveChapter(draft);

        var candidates = DeathCandidateDetector.Detect(
            (await _store.GetNovel("nov001"))!,
            draft,
            []
        );
        Assert.Single(candidates);

        draft.Content = "林长歌假死脱身。";
        await _store.SaveChapter(draft);
        Assert.Empty(DeathCandidateDetector.Detect((await _store.GetNovel("nov001"))!, draft, []));
    }
}
