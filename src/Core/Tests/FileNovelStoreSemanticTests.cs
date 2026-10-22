using Core.Data;
using Xunit;

namespace Core.Tests;

public sealed class FileNovelStoreSemanticTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _mythologyDir;

    public FileNovelStoreSemanticTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"novel-store-semantic-{Guid.NewGuid():N}");
        _mythologyDir = Path.Combine(_tempRoot, ".mythology");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    [Fact]
    public async Task IncrementalMode_MatchesDirectReads()
    {
        var directStore = new FileNovelStore(_tempRoot, _mythologyDir);
        await SeedAsync(directStore);

        var semanticService = new NovelSemanticService(_tempRoot);
        var incrementalStore = new FileNovelStore(_tempRoot, _mythologyDir, semanticService)
        {
            StoreMode = NovelStoreMode.Incremental,
        };

        var directChapters = await directStore.GetChapters("nov001");
        var incrementalChapters = await incrementalStore.GetChapters("nov001");
        Assert.Equal(directChapters.Count, incrementalChapters.Count);
        Assert.Equal(directChapters[0].Title, incrementalChapters[0].Title);
        Assert.Equal(directChapters[0].Content, incrementalChapters[0].Content);
        Assert.Equal(directChapters[0].Status, incrementalChapters[0].Status);

        var directCharacters = await directStore.GetCharacters("nov001");
        var incrementalCharacters = await incrementalStore.GetCharacters("nov001");
        Assert.Equal(directCharacters.Count, incrementalCharacters.Count);
        Assert.Equal(directCharacters[0].Name, incrementalCharacters[0].Name);

        var directPlotlines = await directStore.GetPlotlines("nov001");
        var incrementalPlotlines = await incrementalStore.GetPlotlines("nov001");
        Assert.Equal(directPlotlines.Count, incrementalPlotlines.Count);
        Assert.Equal(directPlotlines[0].Name, incrementalPlotlines[0].Name);

        var directRelationships = await directStore.GetRelationshipsByNovel("nov001");
        var incrementalRelationships = await incrementalStore.GetRelationshipsByNovel("nov001");
        Assert.Equal(directRelationships.Count, incrementalRelationships.Count);
    }

    [Fact]
    public async Task ShadowMode_ReportsNoDifference_WhenDataConsistent()
    {
        var directStore = new FileNovelStore(_tempRoot, _mythologyDir);
        await SeedAsync(directStore);

        var semanticService = new NovelSemanticService(_tempRoot);
        var shadowStore = new FileNovelStore(_tempRoot, _mythologyDir, semanticService)
        {
            StoreMode = NovelStoreMode.Shadow,
        };

        var differences = new List<string>();
        shadowStore.ShadowDifference += (method, direct, incremental) =>
            differences.Add($"{method}: {direct} != {incremental}");

        _ = await shadowStore.GetChapters("nov001");
        _ = await shadowStore.GetCharacters("nov001");
        _ = await shadowStore.GetPlotlines("nov001");
        _ = await shadowStore.GetRelationshipsByNovel("nov001");

        Assert.Empty(differences);
    }

    [Fact]
    public async Task IncrementalMode_PicksUpExternalChapterEdit()
    {
        var directStore = new FileNovelStore(_tempRoot, _mythologyDir);
        await SeedAsync(directStore);

        var semanticService = new NovelSemanticService(_tempRoot);
        var incrementalStore = new FileNovelStore(_tempRoot, _mythologyDir, semanticService)
        {
            StoreMode = NovelStoreMode.Incremental,
        };

        var before = await incrementalStore.GetChapter("nov001", "ch001");
        Assert.NotNull(before);
        Assert.Equal("第一章", before!.Title);

        // 外部直接改文件（不经过 FileNovelStore 事件）
        var novelDir = Directory
            .GetDirectories(_tempRoot)
            .Single(d => File.ReadAllText(Path.Combine(d, "novel.yaml")).Contains("id: nov001"));
        await File.WriteAllTextAsync(
            Path.Combine(novelDir, "chapters", "ch001.md"),
            """
            ---
            id: ch001
            order: 1
            title: 第一章（外部修改）
            status: draft
            ---
            新正文。
            """
        );

        var after = await incrementalStore.GetChapter("nov001", "ch001");
        Assert.NotNull(after);
        Assert.Equal("第一章（外部修改）", after!.Title);
        Assert.Equal("新正文。", after.Content);
    }

    private async Task SeedAsync(FileNovelStore store)
    {
        await store.SaveNovel(new Novel { Id = "nov001", Title = "测试小说" });
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch001",
                NovelId = "nov001",
                Title = "第一章",
                Order = 1,
                Status = ChapterStatus.Draft,
                Content = "第一章内容。",
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "c001",
                NovelId = "nov001",
                Name = "角色A",
                Role = CharacterRole.Protagonist,
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "c002",
                NovelId = "nov001",
                Name = "角色B",
                Role = CharacterRole.Supporting,
            }
        );
        await store.SavePlotline(
            new Plotline
            {
                Id = "p001",
                NovelId = "nov001",
                Name = "主线",
                Type = PlotlineType.Main,
            }
        );
        await store.SaveRelationship(
            new Relationship
            {
                Id = "r001",
                NovelId = "nov001",
                CharacterAId = "c001",
                CharacterBId = "c002",
                Type = RelationshipType.Friend,
            }
        );
    }
}
