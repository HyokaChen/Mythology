using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// SqliteRetrievalStore 集成回归测试。
/// 背景：GetChunkResultsAsync 的 SELECT 返回 16 列（ordinal 0-15，末列 metadata_json），
/// 但代码按硬编码 ordinal 16 读取 metadata_json → 候选集命中时抛
/// ArgumentOutOfRangeException("ordinal", Actual=16)，表现为"第二次召回失败"。
/// 修复：改用 reader.GetOrdinal("metadata_json") 按列名读取。
/// </summary>
public sealed class SqliteRetrievalStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"test_retrieval_{Guid.NewGuid():N}.db"
    );
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // store 以 Pooling=False 打开连接，DisposeAsync 即释放文件句柄，无需清池
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public async Task GetChunkResultsAsync_ReturnsFullRow_WithMetadata()
    {
        await using var store = new SqliteRetrievalStore(_dbPath, dimension: 4);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var doc = new RetrievalDocument
        {
            DocumentId = "doc-1",
            Domain = RetrievalDomain.NovelKnowledge,
            DocumentKind = DocumentKind.CharacterProfile,
            NovelId = "novel-1",
            SourceEntityType = "Character",
            SourceEntityId = "char-1",
            SourceRevision = 1,
            ContentHash = "hash-1",
            EmbeddingModelId = "test-model",
            EmbeddingDimension = 4,
            IndexStatus = IndexStatus.Ready,
            MetadataJson = """{"chapter-title":"第一章","chapter-order":1}""",
        };
        var chunk = new RetrievalChunk
        {
            ChunkId = "chunk-1",
            DocumentId = "doc-1",
            ChunkOrdinal = 0,
            Text = "陈折花是一个冷静果断的角色。",
            SourceOffsetStart = 0,
            SourceOffsetEnd = 14,
            ContentHash = "ch-1",
            BoundaryQuality = "sentence_end",
            Embedding = [0.1f, 0.2f, 0.3f, 0.4f],
        };
        await store.UpsertDocumentAsync(doc, [chunk], TestContext.Current.CancellationToken);

        var results = await store.GetChunkResultsAsync(
            new HashSet<string> { "chunk-1" },
            TestContext.Current.CancellationToken
        );

        var result = Assert.Single(results);
        Assert.Equal("chunk-1", result.ChunkId);
        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal("novel-1", result.NovelId);
        Assert.Equal("Character", result.SourceEntityType);
        Assert.Equal("char-1", result.SourceEntityId);
        Assert.Equal("第一章", result.ChapterTitle);
        Assert.Equal(1, result.ChapterOrder);
        Assert.Equal("陈折花是一个冷静果断的角色。", result.Text);
    }

    [Fact]
    public async Task GetChunkResultsAsync_EmptyCandidate_ReturnsEmpty()
    {
        await using var store = new SqliteRetrievalStore(_dbPath, dimension: 4);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var results = await store.GetChunkResultsAsync(
            new HashSet<string>(),
            TestContext.Current.CancellationToken
        );

        Assert.Empty(results);
    }

    /// <summary>
    /// 维度漂移重建：先用 1536 维建表并写入向量，再用 1024 维打开同一库。
    /// vec0 表创建后维度不可 ALTER，store 必须在初始化时检测到不匹配并 DROP 重建，
    /// 之后能正常写入 1024 维向量（否则抛 SQLite Dimension mismatch）。
    /// </summary>
    [Fact]
    public async Task Initialize_WhenExistingVecDimensionMismatches_RebuildsTable()
    {
        // ── 第一次：1536 维建表 + 写入 ──
        await using (var oldStore = new SqliteRetrievalStore(_dbPath, dimension: 1536))
        {
            await oldStore.InitializeAsync(TestContext.Current.CancellationToken);
            await oldStore.UpsertDocumentAsync(
                MakeDoc("doc-old", "old-1"),
                [MakeChunk("old-1", "doc-old", "旧文本", 1536)],
                TestContext.Current.CancellationToken
            );
        }

        // ── 第二次：1024 维打开同一库（模拟更换 embedding 模型） ──
        await using (var newStore = new SqliteRetrievalStore(_dbPath, dimension: 1024))
        {
            await newStore.InitializeAsync(TestContext.Current.CancellationToken);

            // 维度不匹配时应已 DROP 重建表，写入 1024 维不再抛异常
            await newStore.UpsertDocumentAsync(
                MakeDoc("doc-new", "new-1"),
                [MakeChunk("new-1", "doc-new", "新文本", 1024)],
                TestContext.Current.CancellationToken
            );
        }

        // ── 第三次：再次以 1024 维打开（幂等，维度一致不重建） ──
        await using (var againStore = new SqliteRetrievalStore(_dbPath, dimension: 1024))
        {
            await againStore.InitializeAsync(TestContext.Current.CancellationToken);
            await againStore.UpsertDocumentAsync(
                MakeDoc("doc-new2", "new-2"),
                [MakeChunk("new-2", "doc-new2", "新文本2", 1024)],
                TestContext.Current.CancellationToken
            );
        }
    }

    [Fact]
    public async Task LexicalSearch_FindsChineseWordInsideSentence()
    {
        await using var store = new SqliteRetrievalStore(_dbPath, dimension: 4);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.UpsertDocumentAsync(
            MakeDoc("doc-lex", "lex-1"),
            [MakeChunk("chunk-lex", "doc-lex", "对过往有着近乎固执的眷恋，灵魂仍在共鸣。", 4)],
            TestContext.Current.CancellationToken
        );

        var candidates = new HashSet<string> { "chunk-lex" };
        var affection = await store.LexicalSearchAsync(
            "眷恋",
            candidates,
            10,
            TestContext.Current.CancellationToken
        );
        var soul = await store.LexicalSearchAsync(
            "灵魂 不存在的词",
            candidates,
            10,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(affection, hit => hit.ChunkId == "chunk-lex");
        Assert.Contains(soul, hit => hit.ChunkId == "chunk-lex");

        // 精确车道：唯一词项是 2 字泛词（“关系”）时不做 AND，避免把全部关系文档拉进来。
        var generic = await store.LexicalSearchAsync(
            "关系",
            candidates,
            10,
            TestContext.Current.CancellationToken,
            useOr: false
        );
        Assert.Empty(generic);
    }

    private static RetrievalDocument MakeDoc(string documentId, string sourceEntityId)
    {
        return new RetrievalDocument
        {
            DocumentId = documentId,
            Domain = RetrievalDomain.NovelKnowledge,
            DocumentKind = DocumentKind.CharacterProfile,
            NovelId = "novel-1",
            SourceEntityType = "Character",
            SourceEntityId = sourceEntityId,
            SourceRevision = 1,
            ContentHash = $"hash-{documentId}",
            EmbeddingModelId = "test-model",
            IndexStatus = IndexStatus.Ready,
        };
    }

    private static RetrievalChunk MakeChunk(string chunkId, string documentId, string text, int dim)
    {
        var rng = new Random(chunkId.GetHashCode(StringComparison.Ordinal));
        var vector = new float[dim];
        for (var i = 0; i < dim; i++)
            vector[i] = (float)rng.NextDouble();
        return new RetrievalChunk
        {
            ChunkId = chunkId,
            DocumentId = documentId,
            ChunkOrdinal = 0,
            Text = text,
            ContentHash = $"ch-{chunkId}",
            BoundaryQuality = "sentence_end",
            Embedding = vector,
        };
    }
}
