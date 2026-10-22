using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 检索内核集成测试：使用 InMemoryRetrievalStore 验证过滤和索引逻辑。
/// </summary>
public class RetrievalFilterIntegrationTests
{
    [Fact]
    public async Task MetadataFilter_NovelId_PreventsCrossNovelLeak()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var novel1Doc = CreateDoc(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel1",
            "Character",
            "ch1"
        );
        var novel2Doc = CreateDoc(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel2",
            "Character",
            "ch2"
        );

        await store.UpsertDocumentAsync(
            novel1Doc,
            [CreateChunk(novel1Doc.DocumentId, "小说1", 1536)],
            TestContext.Current.CancellationToken
        );
        await store.UpsertDocumentAsync(
            novel2Doc,
            [CreateChunk(novel2Doc.DocumentId, "小说2", 1536)],
            TestContext.Current.CancellationToken
        );

        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "owner1",
            NovelId = "novel1",
            Query = "小说",
            FinalTopK = 10,
        };

        var queryEmbedding = GenerateDeterministicVector("小说", 1536);
        var results = await store.SearchAsync(
            request,
            queryEmbedding,
            TestContext.Current.CancellationToken
        );

        Assert.All(results, r => Assert.Equal("novel1", r.NovelId));
    }

    [Fact]
    public async Task Deleted_Document_NotReturned()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var doc = CreateDoc(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel1",
            "Character",
            "ch1"
        );
        var chunk = CreateChunk(doc.DocumentId, "内容", 1536);
        await store.UpsertDocumentAsync(doc, [chunk], TestContext.Current.CancellationToken);

        await store.MarkDocumentDeletedAsync(doc.DocumentId, TestContext.Current.CancellationToken);

        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "owner1",
            NovelId = "novel1",
            Query = "内容",
            FinalTopK = 10,
        };

        var queryEmbedding = GenerateDeterministicVector("内容", 1536);
        var results = await store.SearchAsync(
            request,
            queryEmbedding,
            TestContext.Current.CancellationToken
        );
        Assert.Empty(results);
    }

    [Fact]
    public async Task DocumentKind_Filter_Works()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var charDoc = CreateDoc(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel1",
            "Character",
            "ch1"
        );
        var plotDoc = CreateDoc(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.PlotlineProfile,
            "novel1",
            "Plotline",
            "pl1"
        );

        await store.UpsertDocumentAsync(
            charDoc,
            [CreateChunk(charDoc.DocumentId, "角色", 1536)],
            TestContext.Current.CancellationToken
        );
        await store.UpsertDocumentAsync(
            plotDoc,
            [CreateChunk(plotDoc.DocumentId, "情节", 1536)],
            TestContext.Current.CancellationToken
        );

        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "owner1",
            NovelId = "novel1",
            DocumentKinds = [DocumentKind.CharacterProfile],
            Query = "角色",
            FinalTopK = 10,
        };

        var queryEmbedding = GenerateDeterministicVector("角色", 1536);
        var results = await store.SearchAsync(
            request,
            queryEmbedding,
            TestContext.Current.CancellationToken
        );

        Assert.All(results, r => Assert.Equal(DocumentKind.CharacterProfile, r.DocumentKind));
    }

    [Fact]
    public async Task SourceEntityType_Filter_Works()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var charDoc = CreateDoc(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel1",
            "Character",
            "ch1"
        );
        var plotDoc = CreateDoc(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.PlotlineProfile,
            "novel1",
            "Plotline",
            "pl1"
        );

        await store.UpsertDocumentAsync(
            charDoc,
            [CreateChunk(charDoc.DocumentId, "角色内容", 1536)],
            TestContext.Current.CancellationToken
        );
        await store.UpsertDocumentAsync(
            plotDoc,
            [CreateChunk(plotDoc.DocumentId, "情节内容", 1536)],
            TestContext.Current.CancellationToken
        );

        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "owner1",
            NovelId = "novel1",
            SourceEntityTypes = ["Character"],
            Query = "内容",
            FinalTopK = 10,
        };

        var queryEmbedding = GenerateDeterministicVector("内容", 1536);
        var results = await store.SearchAsync(
            request,
            queryEmbedding,
            TestContext.Current.CancellationToken
        );

        Assert.All(results, r => Assert.Equal("Character", r.SourceEntityType));
    }

    // ── 辅助方法 ──

    private static RetrievalDocument CreateDoc(
        RetrievalDomain domain,
        DocumentKind kind,
        string novelId,
        string entityType,
        string entityId
    )
    {
        var docId = StableId.DocumentId(domain, kind, novelId, entityType, entityId);
        return new RetrievalDocument
        {
            DocumentId = docId,
            Domain = domain,
            NovelId = novelId,
            DocumentKind = kind,
            SourceEntityType = entityType,
            SourceEntityId = entityId,
            IndexStatus = IndexStatus.Ready,
        };
    }

    private static RetrievalChunk CreateChunk(string documentId, string text, int dim)
    {
        var chunkId = StableId.ChunkId(documentId, 0);
        return new RetrievalChunk
        {
            ChunkId = chunkId,
            DocumentId = documentId,
            ChunkOrdinal = 0,
            Text = text,
            SourceOffsetStart = 0,
            SourceOffsetEnd = text.Length,
            ContentHash = StableId.ContentHash(text),
            Embedding = GenerateDeterministicVector(text, dim),
        };
    }

    private static float[] GenerateDeterministicVector(string seed, int dim)
    {
        var rng = new Random(seed.GetHashCode(StringComparison.Ordinal));
        var v = new float[dim];
        for (var i = 0; i < dim; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1);

        var mag = Math.Sqrt(v.Sum(x => (double)x * x));
        if (mag > 0)
            for (var i = 0; i < dim; i++)
                v[i] /= (float)mag;
        return v;
    }
}
