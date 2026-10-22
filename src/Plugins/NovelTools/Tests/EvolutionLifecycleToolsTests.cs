using Core.Data;
using Core.Evolution;
using Core.Shared;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>生命周期工具：待发布登记、发布/撤回、补记、删除待发布、撤销设定变更。</summary>
[Collection("FileNovelStore")]
public sealed class EvolutionLifecycleToolsTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly FileNovelStore _store;

    public EvolutionLifecycleToolsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tools-lifecycle-{Guid.NewGuid():N}");
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

    private async Task<Chapter> SeedAsync(ChapterStatus status = ChapterStatus.Draft)
    {
        await _store.SaveNovel(new Novel { Id = "n1", Title = "生命周期工具" });
        await _store.SaveCharacter(
            new Character
            {
                Id = "lin",
                NovelId = "n1",
                Name = "林长歌",
                FactionId = "fac",
            }
        );
        await _store.SaveFaction(
            new Faction
            {
                Id = "fac",
                NovelId = "n1",
                Name = "青云宗",
                Leader = "lin",
                Members = ["lin"],
            }
        );
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "n1",
            Title = "第一章",
            Order = 1,
            Content = "风起。",
            Status = status,
        };
        await _store.SaveChapter(chapter);
        return chapter;
    }

    private static async Task<string> TextOf<T>(AgentToolResult<T> result)
    {
        await Task.CompletedTask;
        return string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));
    }

    [Fact]
    public async Task RecordEvolution_DraftChapter_WritesPending_ThenPublishApplies()
    {
        var chapter = await SeedAsync();
        var tool = new RecordEvolutionTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new RecordEvolutionParams
            {
                NovelId = "n1",
                ChapterId = chapter.Id,
                TargetId = "lin",
                TargetType = EntityType.Character,
                Aspect = EvolutionAspect.Status,
                Effects = [new StateEffectParams { Field = "status", Value = "deceased" }],
                Cause = "死于围攻",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Contains("待发布", await TextOf(result));
        Assert.Equal(CharacterStatus.Active, (await _store.GetCharacter("n1", "lin"))!.Status);

        var change = Assert.Single(await _store.GetEntityStateChangesByChapter("n1", chapter.Id));
        Assert.Equal(ChangeLifecycle.Pending, change.Lifecycle);
        Assert.Equal(1, change.Sequence);

        var statusTool = new SetChapterStatusTool(_store);
        var publish = await statusTool.ExecuteAsync(
            "t2",
            new SetChapterStatusParams
            {
                NovelId = "n1",
                ChapterId = chapter.Id,
                Status = "published",
            },
            CancellationToken.None
        );
        Assert.False(publish.IsError, await TextOf(publish));
        Assert.Equal(CharacterStatus.Deceased, (await _store.GetCharacter("n1", "lin"))!.Status);
        Assert.Equal(ChapterStatus.Published, (await _store.GetChapter("n1", chapter.Id))!.Status);

        var unpublish = await statusTool.ExecuteAsync(
            "t3",
            new SetChapterStatusParams
            {
                NovelId = "n1",
                ChapterId = chapter.Id,
                Status = "draft",
            },
            CancellationToken.None
        );
        Assert.False(unpublish.IsError, await TextOf(unpublish));
        Assert.Equal(CharacterStatus.Active, (await _store.GetCharacter("n1", "lin"))!.Status);
    }

    [Fact]
    public async Task RecordEvolution_PublishedChapter_AppliesImmediately()
    {
        var chapter = await SeedAsync();
        using (OntologyWriteContext.SuppressChapterLifecycle())
        {
            chapter.Status = ChapterStatus.Published;
            await _store.SaveChapter(chapter);
        }

        var tool = new RecordEvolutionTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new RecordEvolutionParams
            {
                NovelId = "n1",
                ChapterId = chapter.Id,
                TargetId = "lin",
                TargetType = EntityType.Character,
                Effects = [new StateEffectParams { Field = "status", Value = "missing" }],
            },
            CancellationToken.None
        );
        Assert.False(result.IsError, await TextOf(result));
        Assert.Contains("已生效", await TextOf(result));
        Assert.Equal(CharacterStatus.Missing, (await _store.GetCharacter("n1", "lin"))!.Status);
    }

    [Fact]
    public async Task RecordEvolution_InvalidField_RejectedWithoutWriting()
    {
        var chapter = await SeedAsync();
        var tool = new RecordEvolutionTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new RecordEvolutionParams
            {
                NovelId = "n1",
                ChapterId = chapter.Id,
                TargetId = "lin",
                Effects = [new StateEffectParams { Field = "bogus", Value = "x" }],
            },
            CancellationToken.None
        );
        Assert.True(result.IsError);
        Assert.Empty(await _store.GetEntityStateChangesByChapter("n1", chapter.Id));
    }

    [Fact]
    public async Task DeleteEvolution_RemovesPending_RejectsCommitted()
    {
        var chapter = await SeedAsync();
        var tool = new RecordEvolutionTool(_store);
        var created = await tool.ExecuteAsync(
            "t1",
            new RecordEvolutionParams
            {
                NovelId = "n1",
                ChapterId = chapter.Id,
                TargetId = "lin",
                Effects =
                [
                    new StateEffectParams
                    {
                        Field = "tags",
                        Operation = StateEffectOperation.Add,
                        Value = "剑修",
                    },
                ],
            },
            CancellationToken.None
        );
        var changeId = created.TypedDetails!.ChangeId;

        var delete = new DeleteEvolutionTool(_store);
        var removed = await delete.ExecuteAsync(
            "t2",
            new DeleteEvolutionParams { NovelId = "n1", ChangeId = changeId },
            CancellationToken.None
        );
        Assert.False(removed.IsError);
        Assert.Empty(await _store.GetEntityStateChangesByChapter("n1", chapter.Id));

        // 已生效记录不可删除
        var second = await tool.ExecuteAsync(
            "t3",
            new RecordEvolutionParams
            {
                NovelId = "n1",
                ChapterId = chapter.Id,
                TargetId = "lin",
                Effects = [new StateEffectParams { Field = "status", Value = "deceased" }],
            },
            CancellationToken.None
        );
        await new ChapterLifecycleService(_store).PublishAsync(
            "n1",
            chapter.Id,
            new PublishOptions()
        );
        var rejected = await delete.ExecuteAsync(
            "t4",
            new DeleteEvolutionParams { NovelId = "n1", ChangeId = second.TypedDetails!.ChangeId },
            CancellationToken.None
        );
        Assert.True(rejected.IsError);
        Assert.Contains("先撤回章节", await TextOf(rejected));
    }

    [Fact]
    public async Task DeleteEvolution_AllowsCommittedResidue_OnDraftChapter()
    {
        var chapter = await SeedAsync();
        // 模拟事务崩溃残留：草稿章里留下已生效记录
        await _store.SaveEntityStateChanges(
            "n1",
            chapter.Id,
            [
                new EntityStateChange
                {
                    Id = "residue",
                    NovelId = "n1",
                    TargetEntityType = EntityType.Character,
                    TargetId = "lin",
                    ChapterId = chapter.Id,
                    Summary = "残留",
                    Lifecycle = ChangeLifecycle.Committed,
                    CommittedAt = DateTime.UtcNow,
                    Sequence = 1,
                },
            ]
        );

        var tool = new DeleteEvolutionTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new DeleteEvolutionParams { NovelId = "n1", ChangeId = "residue" },
            CancellationToken.None
        );
        Assert.False(result.IsError, await TextOf(result));
        Assert.Empty(await _store.GetEntityStateChangesByChapter("n1", chapter.Id));
    }

    [Fact]
    public async Task UndoManualStateChange_ReversesLatestManualRecord()
    {
        await SeedAsync();
        using (OntologyWriteContext.BeginUser("s1"))
        {
            var character = await _store.GetCharacter("n1", "lin");
            character!.Status = CharacterStatus.Deceased;
            await _store.SaveCharacter(character);
        }
        Assert.Single(await _store.GetManualStateChanges("n1"));

        var tool = new UndoManualStateChangeTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new UndoManualStateChangeParams
            {
                NovelId = "n1",
                EntityType = EntityType.Character,
                EntityId = "lin",
                Field = "status",
            },
            CancellationToken.None
        );
        Assert.False(result.IsError, await TextOf(result));
        Assert.Equal(CharacterStatus.Active, (await _store.GetCharacter("n1", "lin"))!.Status);
        Assert.Equal(2, (await _store.GetManualStateChanges("n1")).Count);

        // 第二次撤销：已没有更早的人工记录可撤销前值仍可反向（最新记录 from=deceased）→ 仍可继续撤销
        var again = await tool.ExecuteAsync(
            "t2",
            new UndoManualStateChangeParams
            {
                NovelId = "n1",
                EntityType = EntityType.Character,
                EntityId = "lin",
                Field = "status",
            },
            CancellationToken.None
        );
        Assert.False(again.IsError);
    }
}
