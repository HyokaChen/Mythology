using Core.Data;
using Core.Evolution;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 本体动作日志（ontology/actions.jsonl）契约：创建/更新/删除、字段分类、来源标注、
/// 状态分片与正文排除。
/// </summary>
[Collection("FileNovelStore")]
public sealed class OntologyJournalTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly FileNovelStore _store;

    public OntologyJournalTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ontology-journal-{Guid.NewGuid():N}");
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

    private async Task<Novel> SeedNovelAsync()
    {
        var novel = new Novel { Id = "nov001", Title = "本体测试" };
        await _store.SaveNovel(novel);
        return novel;
    }

    [Fact]
    public async Task SaveNovel_CreatesObjectAction()
    {
        await SeedNovelAsync();

        var actions = await _store.GetOntologyActions("nov001");
        var action = Assert.Single(actions);
        Assert.Equal(OntologyElement.Object, action.Element);
        Assert.Equal(OntologyOperation.Create, action.Operation);
        Assert.Equal(EntityType.Novel, action.TargetEntityType);
        Assert.Equal("nov001", action.TargetId);
    }

    [Fact]
    public async Task SaveCharacter_WithAiScope_RecordsSourceAndTool()
    {
        await SeedNovelAsync();

        using (OntologyWriteContext.Begin(AuditSource.Ai, "edit_entities", "s1"))
        {
            await _store.SaveCharacter(
                new Character
                {
                    Id = "c1",
                    NovelId = "nov001",
                    Name = "林长歌",
                }
            );
        }

        var actions = await _store.GetOntologyActions("nov001");
        var create = Assert.Single(actions.Where(a => a.TargetEntityType == EntityType.Character));
        Assert.Equal(OntologyOperation.Create, create.Operation);
        Assert.Equal(AuditSource.Ai, create.Source);
        Assert.Equal("edit_entities", create.ToolName);
        Assert.Equal("s1", create.SessionId);
    }

    [Fact]
    public async Task StatusAndFactionChanges_AreClassifiedAsStateAndRelation()
    {
        await SeedNovelAsync();
        var character = new Character
        {
            Id = "c1",
            NovelId = "nov001",
            Name = "林长歌",
        };
        await _store.SaveCharacter(character);

        character.Status = CharacterStatus.Deceased;
        character.FactionId = "f1";
        await _store.SaveCharacter(character);

        var actions = await _store.GetOntologyActions("nov001");
        var status = Assert.Single(actions.Where(a => a.Field == "status"));
        Assert.Equal(OntologyElement.State, status.Element);
        Assert.Equal(OntologyOperation.Update, status.Operation);
        Assert.Equal("active", status.From);
        Assert.Equal("deceased", status.To);

        var faction = Assert.Single(actions.Where(a => a.Field == "faction-id"));
        Assert.Equal(OntologyElement.Relation, faction.Element);
        Assert.Equal("f1", faction.To);
    }

    [Fact]
    public async Task NoOpSave_DoesNotAppendActions()
    {
        await SeedNovelAsync();
        var character = new Character
        {
            Id = "c1",
            NovelId = "nov001",
            Name = "林长歌",
        };
        await _store.SaveCharacter(character);
        var before = (await _store.GetOntologyActions("nov001")).Count;

        await _store.SaveCharacter(character);
        Assert.Equal(before, (await _store.GetOntologyActions("nov001")).Count);
    }

    [Fact]
    public async Task Relationship_WorldRule_Timeline_AreJournaled()
    {
        await SeedNovelAsync();
        await _store.SaveCharacter(
            new Character
            {
                Id = "a",
                NovelId = "nov001",
                Name = "A",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "b",
                NovelId = "nov001",
                Name = "B",
            }
        );

        var rel = new Relationship
        {
            Id = "r1",
            NovelId = "nov001",
            CharacterAId = "a",
            CharacterBId = "b",
            Type = RelationshipType.Friend,
        };
        await _store.SaveRelationship(rel);
        rel.Type = RelationshipType.Enemy;
        await _store.SaveRelationship(rel);
        await _store.DeleteRelationship("nov001", "r1");

        await _store.SaveWorldRule(
            "nov001",
            new WorldRule
            {
                Id = "w1",
                NovelId = "nov001",
                EntryKind = WorldRuleEntryKind.Rule,
                RuleKind = RuleKind.Forbids,
                ForbiddenAction = "御剑",
                Description = "外门弟子不得御剑",
                Effect = new PlannedDelta { Summary = "被执法堂处罚" },
            }
        );

        await _store.SaveTimelineEvent(
            new TimelineEvent
            {
                Id = "t1",
                NovelId = "nov001",
                Title = "宗门大比",
                ChapterId = "ch1",
            }
        );

        var actions = await _store.GetOntologyActions("nov001");
        Assert.Equal(3, actions.Count(a => a.TargetEntityType == EntityType.Relationship));
        Assert.Contains(
            actions,
            a =>
                a.TargetEntityType == EntityType.WorldRule
                && a.Element == OntologyElement.Rule
                && a.Operation == OntologyOperation.Create
        );
        Assert.Contains(
            actions,
            a =>
                a.TargetEntityType == EntityType.TimelineEvent
                && a.Element == OntologyElement.Action
                && a.ChapterId == "ch1"
        );
    }

    [Fact]
    public async Task ChapterContentOnlyChange_DoesNotJournal_ButStatusDoes()
    {
        await SeedNovelAsync();
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "nov001",
            Title = "第一章",
            Order = 1,
            Content = "第一版",
        };
        await _store.SaveChapter(chapter);
        var before = (await _store.GetOntologyActions("nov001")).Count;

        chapter.Content = "第二版";
        await _store.SaveChapter(chapter);
        Assert.Equal(before, (await _store.GetOntologyActions("nov001")).Count);

        chapter.Status = ChapterStatus.Published;
        using (OntologyWriteContext.SuppressChapterLifecycle())
        {
            await _store.SaveChapter(chapter);
        }
        var actions = await _store.GetOntologyActions("nov001");
        Assert.Contains(
            actions,
            a =>
                a.TargetEntityType == EntityType.Chapter
                && a.Field == "status"
                && a.To == "published"
        );
    }

    [Fact]
    public async Task StateChanges_AreJournaledWithOwnSourceAndProposalId()
    {
        await SeedNovelAsync();
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "nov001",
            Title = "第一章",
            Order = 1,
            Content = "正文",
        };
        await _store.SaveChapter(chapter);

        using (OntologyWriteContext.Begin(AuditSource.Ai, "record_evolution", "s1"))
        using (OntologyWriteContext.Attach("证据引文", "op-1"))
        {
            await _store.SaveEntityStateChanges(
                "nov001",
                "ch1",
                [
                    new EntityStateChange
                    {
                        Id = "ev1",
                        NovelId = "nov001",
                        TargetEntityType = EntityType.Character,
                        TargetId = "c1",
                        ChapterId = "ch1",
                        Aspect = EvolutionAspect.Personality,
                        FromSummary = "温和",
                        ToSummary = "冷酷",
                        Summary = "性情大变",
                        EvidenceQuote = "证据引文",
                        Source = AuditSource.Ai,
                    },
                ]
            );
        }

        var actions = await _store.GetOntologyActions("nov001");
        var action = Assert.Single(actions.Where(a => a.Field == "state-change"));
        Assert.Equal(OntologyElement.State, action.Element);
        Assert.Equal(OntologyOperation.Create, action.Operation);
        Assert.Equal("性情大变", action.Summary);
        Assert.Equal("op-1", action.ProposalId);
        Assert.Equal("证据引文", action.Evidence);
        Assert.Equal(AuditSource.Ai, action.Source);
    }

    [Fact]
    public async Task JournalStamp_GrowsAfterAppend()
    {
        await SeedNovelAsync();
        var before = await _store.GetOntologyJournalStamp("nov001");
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "nov001",
                Name = "林",
            }
        );
        var after = await _store.GetOntologyJournalStamp("nov001");
        Assert.True(after > before);
    }
}
