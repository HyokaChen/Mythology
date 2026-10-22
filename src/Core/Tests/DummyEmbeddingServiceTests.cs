using Core.Vector;
using Xunit;

namespace Core.Tests;

public class DummyEmbeddingServiceTests
{
    [Fact]
    public async Task EmbedAsync_ReturnsCorrectDimension()
    {
        var svc = new DummyEmbeddingService();
        var result = await svc.EmbedAsync("test text", TestContext.Current.CancellationToken);
        Assert.Equal(VectorDefaults.Dimension, result.Length);
    }

    [Fact]
    public async Task EmbedAsync_DifferentTexts_ProducesDifferentVectors()
    {
        var svc = new DummyEmbeddingService();
        var v1 = await svc.EmbedAsync("hello world", TestContext.Current.CancellationToken);
        var v2 = await svc.EmbedAsync("goodbye world", TestContext.Current.CancellationToken);
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public async Task EmbedAsync_SameText_ProducesDifferentVectors()
    {
        var svc = new DummyEmbeddingService();
        var v1 = await svc.EmbedAsync("same text", TestContext.Current.CancellationToken);
        var v2 = await svc.EmbedAsync("same text", TestContext.Current.CancellationToken);
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public async Task EmbedAsync_AllElementsAreFinite()
    {
        var svc = new DummyEmbeddingService();
        var vector = await svc.EmbedAsync("test", TestContext.Current.CancellationToken);

        Assert.All(vector, v => Assert.False(double.IsNaN(v)));
        Assert.All(vector, v => Assert.False(double.IsInfinity(v)));
    }

    [Fact]
    public async Task EmbedAsync_NoAllZeroVector()
    {
        var svc = new DummyEmbeddingService();
        var vector = await svc.EmbedAsync("test", TestContext.Current.CancellationToken);

        // At least some variation — not all zeros
        Assert.Contains(vector, v => Math.Abs(v) > 0.001);
    }

    [Fact]
    public async Task EmbedAsync_EmptyString_ReturnsCorrectDimension()
    {
        var svc = new DummyEmbeddingService();
        var vector = await svc.EmbedAsync("", TestContext.Current.CancellationToken);

        Assert.Equal(VectorDefaults.Dimension, vector.Length);
        Assert.Contains(vector, v => Math.Abs(v) > 0.001);
    }

    [Fact]
    public void Dimension_ReturnsDefault()
    {
        var svc = new DummyEmbeddingService();
        Assert.Equal(VectorDefaults.Dimension, svc.Dimension);
    }

    [Fact]
    public async Task EmbedAsync_MultipleCalls_AllProduceNonZeroVectors()
    {
        var svc = new DummyEmbeddingService();
        for (var i = 0; i < 5; i++)
        {
            var vector = await svc.EmbedAsync($"test {i}", TestContext.Current.CancellationToken);
            Assert.Equal(VectorDefaults.Dimension, vector.Length);
            Assert.Contains(vector, v => Math.Abs(v) > 0.001);
        }
    }

    [Fact]
    public async Task EmbedAsync_LongText_WorksCorrectly()
    {
        var svc = new DummyEmbeddingService();
        var longText = string.Join(" ", Enumerable.Repeat("long text", 1000));
        var vector = await svc.EmbedAsync(longText, TestContext.Current.CancellationToken);
        Assert.Equal(VectorDefaults.Dimension, vector.Length);
    }

    [Fact]
    public async Task EmbedAsync_Values_AreInReasonableRange()
    {
        var svc = new DummyEmbeddingService();
        var vector = await svc.EmbedAsync("test", TestContext.Current.CancellationToken);

        // Values should be within [-2, 2] for a normalized random vector
        Assert.All(vector, v => Assert.InRange(v, -2.0, 2.0));
    }
}
