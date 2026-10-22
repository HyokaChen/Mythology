using Core.Tools;
using Xunit;

namespace Core.Tests;

public sealed class ChapterWriteResultTests
{
    [Fact]
    public void IChapterWriteResult_ChapterDetails_ImplementsInterface()
    {
        IChapterWriteResult result = new TestChapterDetails { Id = "ch1", WordCount = 500 };
        Assert.Equal("ch1", result.Id);
        Assert.Equal(500, result.WordCount);
    }

    [Fact]
    public void IChapterWriteResult_RewriteDetails_ImplementsInterface()
    {
        IChapterWriteResult result = new TestRewriteDetails { Id = "ch2", NewLength = 300 };
        Assert.Equal("ch2", result.Id);
        Assert.Equal(300, result.WordCount); // WordCount maps to NewLength
    }

    [Fact]
    public void PatternMatch_DetailsIsInterface_Matches()
    {
        object? details = new TestChapterDetails { Id = "ch1", WordCount = 100 };
        Assert.True(details is IChapterWriteResult);
        if (details is IChapterWriteResult cr)
        {
            Assert.Equal("ch1", cr.Id);
            Assert.Equal(100, cr.WordCount);
        }
    }

    [Fact]
    public void PatternMatch_NullDetails_DoesNotMatch()
    {
        object? details = null;
        Assert.False(details is IChapterWriteResult);
    }

    [Fact]
    public void PatternMatch_UnknownType_DoesNotMatch()
    {
        object? details = "some string";
        Assert.False(details is IChapterWriteResult);
    }

    // Minimal test doubles mirroring the real record shapes
    private sealed record TestChapterDetails : IChapterWriteResult
    {
        public string Id { get; init; } = "";
        public int WordCount { get; init; }
    }

    private sealed record TestRewriteDetails : IChapterWriteResult
    {
        public string Id { get; init; } = "";
        public int NewLength { get; init; }
        int IChapterWriteResult.WordCount => NewLength;
    }
}
