using System.Security.Cryptography;
using System.Text;
using Core.Vector;

namespace SemanticRetrievalTool.Tests;

/// <summary>
/// 确定性嵌入服务：相同文本生成相同向量。
/// 用于测试，确保查询向量与索引向量可匹配。
/// </summary>
public sealed class DeterministicEmbeddingService : IEmbeddingService
{
    public int Dimension => 64;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // 用 SHA256 哈希文本作为伪随机种子，保证确定性
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var seed = (int)BitConverter.ToUInt32(hash, 0);
        var rng = new Random(seed);

        var vector = new float[Dimension];
        for (var i = 0; i < Dimension; i++)
            vector[i] = (float)(rng.NextDouble() * 2 - 1);

        // 归一化
        var mag = Math.Sqrt(vector.Sum(v => v * v));
        if (mag > 0)
            for (var i = 0; i < Dimension; i++)
                vector[i] /= (float)mag;

        return Task.FromResult(vector);
    }
}

/// <summary>
/// SemanticRetrievalTool 测试基类：提供共享的 DeterministicEmbeddingService 和数据插入辅助方法。
/// </summary>
public abstract class SemanticRetrievalToolTestBase
{
    protected readonly DeterministicEmbeddingService Emb = new();
    protected readonly InMemoryRetrievalStore RetrievalStore = new();
    protected readonly RetrievalKernel Kernel;

    protected SemanticRetrievalToolTestBase()
    {
        Kernel = new RetrievalKernel(RetrievalStore, Emb);
    }

    /// <summary>
    /// 向检索存储插入一条测试文档（单块）。
    /// </summary>
    protected async Task IndexDocumentAsync(
        string documentId,
        DocumentKind kind,
        string text,
        string novelId,
        string? sourceEntityId = null
    ) => await IndexDocumentAsync(documentId, kind, text, novelId, sourceEntityId, null);

    /// <summary>
    /// 向检索存储插入一条测试文档（单块，带结构化 metadata）。
    /// </summary>
    protected async Task IndexDocumentAsync(
        string documentId,
        DocumentKind kind,
        string text,
        string novelId,
        string? sourceEntityId,
        Dictionary<string, object?>? metadata
    )
    {
        var embedding = await Emb.EmbedAsync(text);
        var doc = new RetrievalDocument
        {
            DocumentId = documentId,
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "local",
            NovelId = novelId,
            DocumentKind = kind,
            SourceEntityId = sourceEntityId,
            IndexStatus = IndexStatus.Ready,
            MetadataJson = metadata is null
                ? "{}"
                : System.Text.Json.JsonSerializer.Serialize(metadata),
        };
        var chunk = new RetrievalChunk
        {
            ChunkId = $"{documentId}_c0",
            DocumentId = documentId,
            Text = text,
            Embedding = embedding,
            ChunkOrdinal = 0,
        };
        await RetrievalStore.UpsertDocumentAsync(doc, [chunk]);
    }
}
