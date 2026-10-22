using Core.Data;
using Core.Inference;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>scan_ontology：Jev 提案输出（不写库）与提案应用回执。</summary>
public sealed class ScanOntologyToolTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly FileNovelStore _store;

    public ScanOntologyToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scan-ontology-{Guid.NewGuid():N}");
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

    private async Task SeedAsync()
    {
        await _store.SaveNovel(new Novel { Id = "n1", Title = "提案测试" });
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "第一章",
                Order = 1,
                Content = "林长歌推门而入，神色骤变。",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "lin",
                NovelId = "n1",
                Name = "林长歌",
            }
        );
        await _store.SaveChapterAppearances(
            "n1",
            "ch1",
            [
                new AppearanceRecord
                {
                    Id = "ap1",
                    NovelId = "n1",
                    EntityType = EntityType.Character,
                    EntityId = "lin",
                    ChapterId = "ch1",
                    Mode = AppearanceMode.Present,
                    EvidenceSpan = "林长歌推门而入",
                },
            ]
        );
    }

    [Fact]
    public async Task DisabledJudge_ReturnsConfigError()
    {
        await SeedAsync();
        var tool = new ScanOntologyTool(_store, null);
        var result = await tool.ExecuteAsync(
            "t1",
            new ScanOntologyParams { NovelId = "n1" },
            CancellationToken.None
        );
        Assert.True(result.IsError);
        var text = string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("system-one.ontology.enabled", text);
    }

    [Fact]
    public async Task EnabledJudge_ReturnsProposalsWithoutWriting()
    {
        await SeedAsync();
        var before = await _store.GetOntologyActions("n1");
        var tool = new ScanOntologyTool(_store, new StubJudge());
        var result = await tool.ExecuteAsync(
            "t1",
            new ScanOntologyParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var text = string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("【建议】", text);
        Assert.Contains("status", text);
        Assert.Contains("本工具不写库", text);
        Assert.True(result.TypedDetails!.ProposedCount > 0);

        var after = await _store.GetOntologyActions("n1");
        Assert.Equal(before.Count, after.Count);
    }

    [Fact]
    public async Task AppliedProposal_RecordsProposalIdAndEvidence()
    {
        await SeedAsync();
        await _store.SaveCharacter(
            new Character
            {
                Id = "su",
                NovelId = "n1",
                Name = "苏砚",
            }
        );
        var tool = new ManageRelationshipTool(_store);
        var result = await tool.ExecuteAsync(
            "t1",
            new ManageRelationshipParams
            {
                Action = "create",
                NovelId = "n1",
                CharacterAId = "lin",
                CharacterBId = "su",
                Type = "friend",
                Evidence = "证据引文",
                ProposalId = "op-1",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var actions = await _store.GetOntologyActions("n1");
        Assert.Contains(
            actions,
            a =>
                a.TargetEntityType == EntityType.Relationship
                && a.ProposalId == "op-1"
                && a.Evidence == "证据引文"
        );
    }

    private sealed class StubJudge : ISystemOneClient
    {
        public bool Enabled => true;

        public SystemOneConfig Options { get; } =
            new()
            {
                Enabled = true,
                Ontology = new SystemOneOntologyConfig
                {
                    Enabled = true,
                    MinProbability = 0.5,
                    MinConfidence = 0.5,
                    MaxCandidates = 4,
                },
            };

        public Task<SystemOneResponse?> AskAsync(
            object state,
            IReadOnlyDictionary<string, SystemOneQuestion> questions,
            CancellationToken ct = default
        )
        {
            var isChoice = questions.Values.FirstOrDefault() is ChoiceQuestion;
            var answers = questions.Keys.ToDictionary(
                key => key,
                key =>
                    isChoice
                        ? new SystemOneAnswer
                        {
                            Choice = "deceased",
                            Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                            {
                                ["deceased"] = 0.9,
                            },
                            Confidence = 0.9,
                        }
                        : new SystemOneAnswer { Noul = 0.9, Confidence = 0.9 },
                StringComparer.Ordinal
            );
            return Task.FromResult<SystemOneResponse?>(
                new SystemOneResponse { Model = "stub", Answers = answers }
            );
        }

        public void UpdateConfig(SystemOneConfig options) { }
    }
}
