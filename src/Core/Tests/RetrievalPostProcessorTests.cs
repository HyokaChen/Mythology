using Core.Vector;
using Xunit;

namespace Core.Tests;

public sealed class RetrievalPostProcessorTests
{
    [Fact]
    public void Process_DoesNotCapEntitiesWithoutChapterOrPlotline()
    {
        var results = Enumerable
            .Range(0, 7)
            .Select(i => new RetrievalSearchResult
            {
                ChunkId = $"chunk-{i}",
                DocumentId = $"doc-{i}",
                Domain = RetrievalDomain.NovelKnowledge,
                DocumentKind = DocumentKind.RelationshipStatement,
                Text = $"关系 {i}",
                Score = 1.0 - i * 0.01,
            })
            .ToList();

        var processed = RetrievalPostProcessor.Process(
            results,
            new RankingConfig
            {
                MaxPerChapter = 2,
                MaxPerPlotline = 2,
                MergeAdjacentChunks = false,
            }
        );

        Assert.Equal(results.Count, processed.Count);
    }

    [Fact]
    public void Process_StillCapsSameChapter()
    {
        var results = Enumerable
            .Range(0, 4)
            .Select(i => new RetrievalSearchResult
            {
                ChunkId = $"chunk-{i}",
                DocumentId = $"doc-{i}",
                Domain = RetrievalDomain.NovelKnowledge,
                DocumentKind = DocumentKind.ChapterBodyChunk,
                ChapterId = "chapter-1",
                Text = $"正文 {i}",
                Score = 1.0 - i * 0.01,
            })
            .ToList();

        var processed = RetrievalPostProcessor.Process(
            results,
            new RankingConfig { MaxPerChapter = 2, MergeAdjacentChunks = false }
        );

        Assert.Equal(2, processed.Count);
    }
}
