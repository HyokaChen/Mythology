using CloudDiagnosis;
using Core.Agent;
using Core.Data;
using Core.Tools;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

/// <summary>本体 harness BeforeRun：每章按日志/章节变更戳注入一次。</summary>
public sealed class OntologyHarnessTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly FileNovelStore _store;

    public OntologyHarnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ontology-harness-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileNovelStore(_root, Path.Combine(_root, ".mythology"));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
        return default;
    }

    private async Task<(Agent Agent, string ChapterId)> SeedAsync()
    {
        await _store.SaveNovel(new Novel { Id = "n1", Title = "harness" });
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "n1",
            Title = "第一章",
            Order = 1,
            Content = "林长歌推门而入。",
            Status = ChapterStatus.Draft,
        };
        await _store.SaveChapter(chapter);
        await _store.SaveCharacter(
            new Character
            {
                Id = "lin",
                NovelId = "n1",
                Name = "林长歌",
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
                ParticipantIds = ["lin"],
                PlannedDeltas =
                [
                    new PlannedDelta
                    {
                        Kind = PlannedDeltaKind.StateChange,
                        TargetEntityType = EntityType.Character,
                        TargetId = "lin",
                        FromSummary = "健康",
                        ToSummary = "重伤",
                        Summary = "受伤",
                    },
                ],
            }
        );
        // 跨会话摘要独立于 harness 开关：先推进游标，避免测试把 seed 写入的 Create 动作当摘要。
        await _store.SetOntologyCursorAsync("n1", await _store.GetOntologyJournalStamp("n1"));
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = "n1" };
        return (agent, chapter.Id);
    }

    [Fact]
    public async Task PlannedDelta_InjectsOncePerStamp()
    {
        var (agent, chapterId) = await SeedAsync();
        var harness = new OntologyHarness(
            agent,
            _store,
            new CloudDiagnosisOptions { OntologyHarnessEnabled = true }
        );

        await harness.RunAsync();
        var first = agent.InjectQueue.DequeueAllWithIds();
        var message = Assert.Single(first);
        Assert.Contains("[本体 harness]", message.Message.Content);
        Assert.Contains(chapterId, message.Message.Content);

        await harness.RunAsync();
        Assert.Empty(agent.InjectQueue.DequeueAllWithIds());
    }

    [Fact]
    public async Task ChangedJournal_Reevaluates()
    {
        var (agent, _) = await SeedAsync();
        var harness = new OntologyHarness(
            agent,
            _store,
            new CloudDiagnosisOptions { OntologyHarnessEnabled = true }
        );

        await harness.RunAsync();
        agent.InjectQueue.DequeueAllWithIds();

        await _store.AppendOntologyActions(
            "n1",
            [
                new OntologyAction
                {
                    Id = "a1",
                    NovelId = "n1",
                    Element = OntologyElement.Relation,
                    Operation = OntologyOperation.Update,
                    TargetEntityType = EntityType.Character,
                    TargetId = "lin",
                    Field = "faction-id",
                    To = "f1",
                    Summary = "角色「林长歌」faction-id:  → f1",
                    CreatedAt = DateTime.UtcNow.AddMinutes(5),
                },
            ]
        );

        await harness.RunAsync();
        Assert.NotEmpty(agent.InjectQueue.DequeueAllWithIds());
    }

    [Fact]
    public async Task Digest_InjectedOnce_EvenWhenHarnessDisabled()
    {
        await _store.SaveNovel(new Novel { Id = "n2", Title = "digest" });
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n2",
                Name = "林长歌",
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n2",
                Title = "第一章",
                Order = 1,
                Content = "正文",
            }
        );
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = "n2" };
        var harness = new OntologyHarness(
            agent,
            _store,
            new CloudDiagnosisOptions { OntologyHarnessEnabled = false }
        );

        await harness.RunAsync();
        var first = agent.InjectQueue.DequeueAllWithIds();
        var message = Assert.Single(first);
        Assert.Contains("[本体 harness]", message.Message.Content);
        Assert.Equal(
            await _store.GetOntologyJournalStamp("n2"),
            await _store.GetOntologyCursorAsync("n2")
        );

        await harness.RunAsync();
        Assert.Empty(agent.InjectQueue.DequeueAllWithIds());
    }

    [Fact]
    public async Task Disabled_DoesNotInject()
    {
        var (agent, _) = await SeedAsync();
        var harness = new OntologyHarness(
            agent,
            _store,
            new CloudDiagnosisOptions { OntologyHarnessEnabled = false }
        );

        await harness.RunAsync();
        Assert.Empty(agent.InjectQueue.DequeueAllWithIds());
    }
}
