using Core.Data;
using Core.Evolution;
using Xunit;

namespace Core.Tests;

/// <summary>状态效果应用器与全实体时间线：列表/数值操作、一致性规则、时间线上下文。</summary>
[Collection("FileNovelStore")]
public sealed class StateEffectApplierTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly FileNovelStore _store;

    public StateEffectApplierTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"applier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _store = new FileNovelStore(_tempRoot, Path.Combine(_tempRoot, ".mythology"));
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

    private async Task<(Novel Novel, Chapter Chapter)> SeedAsync()
    {
        await _store.SaveNovel(new Novel { Id = "n1", Title = "applier" });
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林长歌",
                Tags = ["剑修"],
                Metrics = { ["speed"] = 10 },
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c2",
                NovelId = "n1",
                Name = "沈月",
                FactionId = "f1",
            }
        );
        await _store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = "n1",
                Name = "青云宗",
                Leader = "c2",
                Members = ["c2"],
                HeadquartersId = "l1",
                Metrics = { ["influence"] = 5 },
            }
        );
        await _store.SaveItem(
            new Item
            {
                Id = "it1",
                NovelId = "n1",
                Name = "青锋剑",
                OwnerId = "c1",
                LocationId = "l1",
                OwnershipPeriods =
                [
                    new ItemOwnershipSpan { OwnerId = "c1", SinceChapterId = "ch1" },
                ],
            }
        );
        await _store.SaveLocation(
            new Location
            {
                Id = "l1",
                NovelId = "n1",
                Name = "青云山",
                Metrics = { ["qi"] = 3 },
            }
        );
        await _store.SavePlotline(
            new Plotline
            {
                Id = "pl1",
                NovelId = "n1",
                Name = "复仇",
            }
        );
        await _store.SaveScene(
            new SceneDefinition
            {
                Id = "sc1",
                NovelId = "n1",
                ChapterId = "ch1",
                Title = "场景",
                Order = 1,
                ParticipantIds = ["c1"],
                ItemIds = ["it1"],
                PlotlineIds = ["pl1"],
            }
        );
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "n1",
            Title = "第一章",
            Order = 1,
            Content = "正文",
        };
        await _store.SaveChapter(chapter);
        return ((await _store.GetNovel("n1"))!, chapter);
    }

    private Task<StateEffectApplyResult> ApplyAsync(
        Novel novel,
        Chapter chapter,
        params StateEffect[] effects
    ) => StateEffectApplier.ApplyAllAsync(_store, novel, chapter, effects);

    [Fact]
    public async Task ListAndMetricOperations_Apply()
    {
        var (novel, chapter) = await SeedAsync();
        var result = await ApplyAsync(
            novel,
            chapter,
            new StateEffect
            {
                TargetType = EntityType.Character,
                TargetId = "c1",
                Field = "tags",
                Operation = StateEffectOperation.Add,
                Value = "阵法",
            },
            new StateEffect
            {
                TargetType = EntityType.Character,
                TargetId = "c1",
                Field = "tags",
                Operation = StateEffectOperation.Remove,
                Value = "剑修",
            },
            new StateEffect
            {
                TargetType = EntityType.Character,
                TargetId = "c1",
                Field = "metrics:speed",
                Operation = StateEffectOperation.Increment,
                Value = "5",
            },
            new StateEffect
            {
                TargetType = EntityType.Faction,
                TargetId = "f1",
                Field = "members",
                Operation = StateEffectOperation.Add,
                Value = "c1",
            },
            new StateEffect
            {
                TargetType = EntityType.Scene,
                TargetId = "sc1",
                Field = "participant-ids",
                Operation = StateEffectOperation.Clear,
            },
            new StateEffect
            {
                TargetType = EntityType.Plotline,
                TargetId = "pl1",
                Field = "status",
                Operation = StateEffectOperation.Set,
                Value = "resolved",
            }
        );

        Assert.Empty(result.Warnings);
        var character = await _store.GetCharacter("n1", "c1");
        Assert.Equal(["阵法"], character!.Tags);
        Assert.Equal(15, character.Metrics["speed"]);

        var faction = await _store.GetFaction("n1", "f1");
        Assert.Contains("c1", faction!.Members);

        var scene = await _store.GetScene("n1", "sc1");
        Assert.Empty(scene!.ParticipantIds);

        var plotline = await _store.GetPlotline("n1", "pl1");
        Assert.Equal(PlotlineStatus.Resolved, plotline!.Status);
        Assert.Equal("ch1", plotline.ResolvedAtChapterId);
    }

    [Fact]
    public async Task OwnershipChange_ClosesOldSpan_AndAppendsNew()
    {
        var (novel, chapter) = await SeedAsync();
        await ApplyAsync(
            novel,
            chapter,
            new StateEffect
            {
                TargetType = EntityType.Item,
                TargetId = "it1",
                Field = "owner-id",
                Operation = StateEffectOperation.Set,
                Value = "c2",
            }
        );

        var item = await _store.GetItem("n1", "it1");
        Assert.Equal("c2", item!.OwnerId);
        Assert.Equal(
            "ch1",
            Assert.Single(item.OwnershipPeriods.Where(s => s.OwnerId == "c1")).UntilChapterId
        );
        Assert.Contains(item.OwnershipPeriods, s => s.OwnerId == "c2" && s.SinceChapterId == "ch1");
    }

    [Fact]
    public async Task FactionDissolved_ClearsMembersAndLeadership()
    {
        var (novel, chapter) = await SeedAsync();
        await ApplyAsync(
            novel,
            chapter,
            new StateEffect
            {
                TargetType = EntityType.Faction,
                TargetId = "f1",
                Field = "status",
                Operation = StateEffectOperation.Set,
                Value = "dissolved",
            }
        );

        var faction = await _store.GetFaction("n1", "f1");
        Assert.Equal(FactionStatus.Dissolved, faction!.Status);
        Assert.Empty(faction.Members);
        Assert.Equal("", faction.Leader);
        Assert.Null(faction.HeadquartersId);
        Assert.Null((await _store.GetCharacter("n1", "c2"))!.FactionId);
    }

    [Fact]
    public async Task LocationDestroyed_ClearsOccupantsAndItems()
    {
        var (novel, chapter) = await SeedAsync();
        await ApplyAsync(
            novel,
            chapter,
            new StateEffect
            {
                TargetType = EntityType.Location,
                TargetId = "l1",
                Field = "status",
                Operation = StateEffectOperation.Set,
                Value = "destroyed",
            },
            new StateEffect
            {
                TargetType = EntityType.Character,
                TargetId = "c1",
                Field = "current-location-id",
                Operation = StateEffectOperation.Set,
                Value = "l1",
            }
        );

        Assert.Null((await _store.GetCharacter("n1", "c1"))!.CurrentLocationId);
        Assert.Null((await _store.GetItem("n1", "it1"))!.LocationId);
        Assert.Null((await _store.GetFaction("n1", "f1"))!.HeadquartersId);
    }

    [Fact]
    public async Task OwnershipChange_WithoutChapterContext_DoesNotWriteEmptyChapterRefs()
    {
        var (novel, _) = await SeedAsync();
        // 设定变更撤销：无章节上下文（Id=""）
        await StateEffectApplier.ApplyAllAsync(
            _store,
            novel,
            new Chapter { Id = "", NovelId = "n1" },
            [
                new StateEffect
                {
                    TargetType = EntityType.Item,
                    TargetId = "it1",
                    Field = "owner-id",
                    Operation = StateEffectOperation.Set,
                    Value = "c2",
                },
            ]
        );

        var item = await _store.GetItem("n1", "it1");
        Assert.Contains(item!.OwnershipPeriods, s => s.OwnerId == "c1" && s.UntilChapterId is null);
        Assert.DoesNotContain(item.OwnershipPeriods, s => s.UntilChapterId == "");
    }

    [Fact]
    public async Task EntityLog_RendersStateRowsWithContext()
    {
        var (novel, chapter) = await SeedAsync();
        await _store.SaveEntityStateChanges(
            "n1",
            chapter.Id,
            [
                new EntityStateChange
                {
                    Id = "ev1",
                    NovelId = "n1",
                    TargetEntityType = EntityType.Character,
                    TargetId = "c1",
                    ChapterId = chapter.Id,
                    Aspect = EvolutionAspect.Status,
                    FromSummary = "active",
                    ToSummary = "deceased",
                    Summary = "林长歌死亡",
                    Source = AuditSource.Ai,
                    Effects =
                    [
                        new StateEffect
                        {
                            TargetType = EntityType.Character,
                            TargetId = "c1",
                            Field = "status",
                            Value = "deceased",
                        },
                    ],
                    Sequence = 1,
                    LocationId = "l1",
                    PlotlineIds = ["pl1"],
                    WitnessIds = ["c2"],
                    TimeDescription = "黄昏",
                    Cause = "围攻",
                    Visibility = ChangeVisibility.Secret,
                    Lifecycle = ChangeLifecycle.Pending,
                },
            ],
            CancellationToken.None
        );

        var view = await EntityLogView.BuildAsync(_store, "n1");
        var rows = view.Get(EntityType.Character, "c1");
        var row = Assert.Single(rows.Where(r => r.Kind == "state"));
        Assert.Equal(1, row.ChapterOrder);
        Assert.Equal("青云山", row.LocationName);
        Assert.Contains("复仇", row.PlotlineNames);
        Assert.Contains("沈月", row.WitnessIds);
        Assert.Equal("黄昏", row.TimeDescription);
        Assert.Equal("围攻", row.Cause);
        Assert.Equal(ChangeLifecycle.Pending, row.Lifecycle);
        Assert.Equal(ChangeVisibility.Secret, row.Visibility);

        var markdown = EntityLogFormatter.ToMarkdown("林长歌", rows);
        Assert.Contains("第1章", markdown);
        Assert.Contains("青云山", markdown);
        Assert.Contains("复仇", markdown);
        Assert.Contains("待发布", markdown);
        Assert.Contains("🔒", markdown);

        var byChapter = view.GetByChapter(chapter.Id);
        Assert.Contains(byChapter, r => r.EntityId == "c1");
    }
}
