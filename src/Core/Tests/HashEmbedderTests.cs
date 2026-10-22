using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// HashEmbedder 测试：确定性、维度、中文、归一化、相似性。
/// 它是 AgentMemoryStore 的默认本地 embedding（离线、零依赖）。
/// </summary>
public sealed class HashEmbedderTests
{
    [Fact]
    public async Task EmbedAsync_SameText_SameVector()
    {
        var embedder = new HashEmbedder(dimension: 128);
        var v1 = await embedder.EmbedAsync("主角怕蜘蛛", TestContext.Current.CancellationToken);
        var v2 = await embedder.EmbedAsync("主角怕蜘蛛", TestContext.Current.CancellationToken);
        Assert.Equal(v1, v2);
    }

    [Fact]
    public async Task EmbedAsync_Dimension_Matches()
    {
        var embedder = new HashEmbedder(dimension: 256);
        Assert.Equal(256, embedder.Dimension);
        var v = await embedder.EmbedAsync("test", TestContext.Current.CancellationToken);
        Assert.Equal(256, v.Length);
    }

    [Fact]
    public async Task EmbedAsync_ChineseText_NonZeroNormalized()
    {
        var embedder = new HashEmbedder(dimension: 128);
        var v = await embedder.EmbedAsync(
            "主角怕蜘蛛，古堡在北方",
            TestContext.Current.CancellationToken
        );
        Assert.False(v.All(x => x == 0), "中文文本必须产生非零向量");

        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, 4);
    }

    [Fact]
    public async Task EmbedAsync_EmptyText_ZeroVector()
    {
        var embedder = new HashEmbedder(dimension: 128);
        var v = await embedder.EmbedAsync("   ", TestContext.Current.CancellationToken);
        Assert.All(v, x => Assert.Equal(0f, x));
    }

    [Fact]
    public async Task EmbedAsync_SharedWords_MoreSimilarThanUnrelated()
    {
        var embedder = new HashEmbedder(dimension: 256);
        var related = await embedder.EmbedAsync(
            "主角怕蜘蛛",
            TestContext.Current.CancellationToken
        );
        var sameTopic = await embedder.EmbedAsync(
            "主角抓蜘蛛",
            TestContext.Current.CancellationToken
        );
        var unrelated = await embedder.EmbedAsync(
            "天气晴朗适合出行",
            TestContext.Current.CancellationToken
        );

        var simSame = AgentMemoryScoring.CosineSimilarity(related, sameTopic);
        var simDiff = AgentMemoryScoring.CosineSimilarity(related, unrelated);

        Assert.True(simSame > 0, "共享词文本的相似度必须 > 0");
        Assert.True(simSame > simDiff, "共享词文本应比无关文本更相似");
    }

    [Fact]
    public async Task EmbedAsync_IdenticalText_CosineNearOne()
    {
        var embedder = new HashEmbedder(dimension: 128);
        var v1 = await embedder.EmbedAsync(
            "古堡建于十三世纪",
            TestContext.Current.CancellationToken
        );
        var v2 = await embedder.EmbedAsync(
            "古堡建于十三世纪",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(1.0, AgentMemoryScoring.CosineSimilarity(v1, v2), 5);
    }

    [Fact]
    public void Constructor_InvalidDimension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashEmbedder(dimension: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashEmbedder(dimension: -5));
    }
}
