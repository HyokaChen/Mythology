using Core.Data;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 新检索内核单元测试：ChineseTextChunker、DocumentBuilder、RRF 融合、StableId。
/// </summary>
public class RetrievalKernelTests
{
    // ══════════════════════════════════════════════════════
    //  ChineseTextChunker
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_ShortText_ReturnsSingleChunk()
    {
        var chunker = new ChineseTextChunker();
        var text = "这是一段简短的文本，不超过三百字。";
        var chunks = chunker.Chunk(text);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0].Text.TrimEnd());
        Assert.Equal(0, chunks[0].ChunkOrdinal);
    }

    [Fact]
    public void Chunk_LongChapter_ProducesMultipleChunks()
    {
        var chunker = new ChineseTextChunker();
        // 生成约 1500 字的中文文本
        var text = string.Join(
            "\n\n",
            Enumerable
                .Range(0, 5)
                .Select(i =>
                    "第"
                    + (i + 1)
                    + "段：这是一段较长的中文文本，包含多句话。角色看着远方，心中想着过去的种种。夕阳照在城墙上，映出一片金色的光芒。"
                    + "她缓缓转过身来，目光扫过在场的每一个人。\u201C他们都不会理解。\u201D她低声说道。"
                    + "夜风渐起，吹动她的长发。远处传来马蹄声，由远及近，越来越响。"
                )
        );

        var chunks = chunker.Chunk(text);

        Assert.True(chunks.Count > 1, "长文本应产生多个块");
        Assert.True(
            chunks.All(c => c.Text.Length <= chunker.MaxLength + chunker.OverlapLength),
            "每个块长度不应超过上限+重叠"
        );
    }

    [Fact]
    public void Chunk_HeadingForcesBoundary()
    {
        var chunker = new ChineseTextChunker();
        var text =
            "# 第一章\n\n" + new string('x', 200) + "\n\n# 第二章\n\n" + new string('y', 200);

        var chunks = chunker.Chunk(text);

        Assert.True(chunks.Count >= 2, "标题应强制开始新块");
    }

    [Fact]
    public void Chunk_SceneBreakForcesBoundary()
    {
        var chunker = new ChineseTextChunker();
        var text = new string('一', 200) + "\n\n***\n\n" + new string('二', 200);

        var chunks = chunker.Chunk(text);

        Assert.True(chunks.Count >= 2, "场景分隔应强制开始新块");
    }

    [Fact]
    public void Chunk_OffsetsTrackPosition()
    {
        var chunker = new ChineseTextChunker();
        var text = "第一段内容。第二段内容。第三段内容。第四段内容。第五段内容。";

        var chunks = chunker.Chunk(text);

        // 所有块的 offset 应在文本范围内
        Assert.All(
            chunks,
            c =>
            {
                Assert.True(c.SourceOffsetStart >= 0);
                Assert.True(c.SourceOffsetEnd <= text.Length);
                Assert.True(c.SourceOffsetStart < c.SourceOffsetEnd);
            }
        );
    }

    [Fact]
    public void Chunk_ChapterTail_ReachLast300Chars()
    {
        // VEC-LOCAL 关键场景：伏笔只出现在 8000 字章节的后 300 字
        var chunker = new ChineseTextChunker();
        var foreShadowing =
            "这是关键伏笔：主角在窗台下发现了一封未署名的信，信中提到了一个从未出现过的地名——青霜谷。";
        var padding = new string('。', 3000); // 约 3000 个句号
        var text = padding + foreShadowing;

        var chunks = chunker.Chunk(text);

        // 最后一个块应包含伏笔文本
        var lastChunk = chunks[^1];
        Assert.Contains("青霜谷", lastChunk.Text);
        Assert.Contains("伏笔", lastChunk.Text);
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsEmptyList()
    {
        var chunker = new ChineseTextChunker();
        Assert.Empty(chunker.Chunk(""));
        Assert.Empty(chunker.Chunk(null!));
    }

    // ══════════════════════════════════════════════════════
    //  DocumentBuilder
    // ══════════════════════════════════════════════════════

    [Fact]
    public void BuildCharacter_IncludesAllFields()
    {
        var builder = new DocumentBuilder();
        var character = new Character
        {
            Id = "ch001",
            NovelId = "novel1",
            Name = "林默",
            Gender = "男",
            Age = "25",
            Role = CharacterRole.Protagonist,
            Personality = "沉默寡言",
            Appearance = "瘦削",
            Background = "孤儿",
            Motivation = "寻找真相",
            Abilities = "过目不忘",
            Weakness = "不善交际",
            Habits = "喜欢独处",
            Tags = ["孤独", "智慧"],
            Status = CharacterStatus.Active,
            FactionId = "fac001",
        };

        var (doc, chunks) = builder.BuildCharacter(character, "青霜阁");

        Assert.Equal(RetrievalDomain.NovelKnowledge, doc.Domain);
        Assert.Equal(DocumentKind.CharacterProfile, doc.DocumentKind);
        Assert.Equal("novel1", doc.NovelId);
        Assert.Equal("Character", doc.SourceEntityType);
        Assert.Equal("ch001", doc.SourceEntityId);
        Assert.Single(chunks);
        Assert.Contains("林默", chunks[0].Text);
        Assert.Contains("Protagonist", chunks[0].Text);
        Assert.Contains("过目不忘", chunks[0].Text);
        Assert.Contains("不善交际", chunks[0].Text);
        Assert.Contains("喜欢独处", chunks[0].Text);
        Assert.Contains("青霜阁", chunks[0].Text);
    }

    [Fact]
    public void BuildChapterSummary_ContainsOrderAndTitle()
    {
        var builder = new DocumentBuilder();
        var chapter = new Chapter
        {
            Id = "chap1",
            NovelId = "novel1",
            Title = "夜雨寄北",
            Order = 7,
            Status = ChapterStatus.Draft,
        };

        var (doc, chunks) = builder.BuildChapterSummary(chapter);

        Assert.Equal(DocumentKind.ChapterSummary, doc.DocumentKind);
        Assert.Contains("第7章", chunks[0].Text);
        Assert.Contains("夜雨寄北", chunks[0].Text);
    }

    [Fact]
    public void BuildChapterBodyChunks_SplitsIntoChunks()
    {
        var builder = new DocumentBuilder();
        var chapter = new Chapter
        {
            Id = "chap1",
            NovelId = "novel1",
            Title = "长章",
            Order = 1,
            Content = string.Join(
                "\n\n",
                Enumerable
                    .Range(0, 20)
                    .Select(i =>
                        $"这是第{i + 1}段的内容，描述了一个重要的故事转折点。角色在这个场景中做出了关键决定。"
                    )
            ),
        };

        var (doc, chunks) = builder.BuildChapterBodyChunks(chapter);

        Assert.Equal(DocumentKind.ChapterBodyChunk, doc.DocumentKind);
        Assert.True(chunks.Count > 1, "长章节正文应分为多个块");
        Assert.All(chunks, c => Assert.Equal(doc.DocumentId, c.DocumentId));
        // 块序号应连续
        for (var i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkOrdinal);
    }

    [Fact]
    public void BuildSceneDefinition_ContainsAllFields()
    {
        var builder = new DocumentBuilder();
        var scene = new SceneDefinition
        {
            Id = "scene1",
            NovelId = "novel1",
            Title = "初遇",
            Conflict = "信任 vs 猜疑",
            Goal = "建立联系",
            EmotionalArc = "紧张→好奇",
            TimeDescription = "黄昏",
            SceneType = SceneType.Dialogue,
            ParticipantIds = ["ch1", "ch2"],
            PlotlineIds = ["pl1"],
            LocationId = "loc1",
            Notes = "注意语气变化",
        };

        var (doc, chunks) = builder.BuildSceneDefinition(
            scene,
            locationName: "青霜阁",
            participantNames: ["林默", "苏瑶"],
            plotlineNames: ["主线"],
            chapterOrder: 3,
            chapterTitle: "夜雨"
        );

        Assert.Equal(DocumentKind.SceneDefinition, doc.DocumentKind);
        Assert.Contains("初遇", chunks[0].Text);
        Assert.Contains("黄昏", chunks[0].Text);
        Assert.Contains("信任 vs 猜疑", chunks[0].Text);
        Assert.Contains("林默", chunks[0].Text);
        Assert.Contains("青霜阁", chunks[0].Text);
    }

    // ══════════════════════════════════════════════════════
    //  StableId
    // ══════════════════════════════════════════════════════

    [Fact]
    public void DocumentId_Deterministic()
    {
        var id1 = StableId.DocumentId(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel1",
            "Character",
            "ch1"
        );
        var id2 = StableId.DocumentId(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel1",
            "Character",
            "ch1"
        );
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void DocumentId_DifferentInputs_DifferentOutputs()
    {
        var id1 = StableId.DocumentId(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel1",
            "Character",
            "ch1"
        );
        var id2 = StableId.DocumentId(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "novel1",
            "Character",
            "ch2"
        );
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ChunkId_Deterministic()
    {
        var docId = StableId.DocumentId(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.ChapterBodyChunk,
            "novel1",
            "Chapter",
            "ch1"
        );
        var chunkId1 = StableId.ChunkId(docId, 0);
        var chunkId2 = StableId.ChunkId(docId, 0);
        Assert.Equal(chunkId1, chunkId2);
        Assert.StartsWith(docId + "_c", chunkId1);
    }

    [Fact]
    public void ContentHash_Deterministic()
    {
        var hash1 = StableId.ContentHash("测试文本");
        var hash2 = StableId.ContentHash("测试文本");
        Assert.Equal(hash1, hash2);
        Assert.Equal(16, hash1.Length); // SHA-256 前 8 字节 = 16 hex chars
    }

    [Fact]
    public void ContentHash_DifferentText_DifferentHash()
    {
        var hash1 = StableId.ContentHash("文本A");
        var hash2 = StableId.ContentHash("文本B");
        Assert.NotEqual(hash1, hash2);
    }

    // ══════════════════════════════════════════════════════
    //  RankingConfig
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ForProfile_WriteContext_HasChapterDecay()
    {
        var config = RankingConfig.ForProfile(RankingProfile.WriteContext);
        Assert.True(config.ChapterDistanceDecay > 0);
        Assert.Equal(3, config.MaxPerChapter);
    }

    [Fact]
    public void ForProfile_ForeshadowingSearch_HighDiversity()
    {
        var config = RankingConfig.ForProfile(RankingProfile.ForeshadowingSearch);
        Assert.Equal(2, config.MaxPerChapter);
        Assert.Equal(3, config.MaxPerPlotline);
        Assert.True(config.DenseWeight > config.LexicalWeight);
    }

    // ══════════════════════════════════════════════════════
    //  RetrievalEnums
    // ══════════════════════════════════════════════════════

    [Fact]
    public void HitChannel_CombinesDenseAndLexical()
    {
        var combined = HitChannel.Dense | HitChannel.Lexical;
        Assert.True(combined.HasFlag(HitChannel.Dense));
        Assert.True(combined.HasFlag(HitChannel.Lexical));
        Assert.False(combined.HasFlag(HitChannel.ExactFilter));
    }

    // ══════════════════════════════════════════════════════
    //  InMemoryRetrievalStore 基硋试
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task InMemoryStore_UpsertAndGet_Works()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var doc = new RetrievalDocument
        {
            DocumentId = "doc1",
            Domain = RetrievalDomain.NovelKnowledge,
            NovelId = "novel1",
            DocumentKind = DocumentKind.CharacterProfile,
            SourceEntityType = "Character",
            SourceEntityId = "ch1",
            SourceRevision = 1000,
            ContentHash = "abc",
            IndexStatus = IndexStatus.Ready,
        };

        var chunk = new RetrievalChunk
        {
            ChunkId = "doc1_c0",
            DocumentId = "doc1",
            ChunkOrdinal = 0,
            Text = "角色: 林默",
            SourceOffsetStart = 0,
            SourceOffsetEnd = 6,
            ContentHash = "def",
            Embedding = new float[1536],
        };

        await store.UpsertDocumentAsync(doc, [chunk], TestContext.Current.CancellationToken);

        var retrieved = await store.GetDocumentAsync("doc1", TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("doc1", retrieved.DocumentId);

        var chunks = await store.GetChunksAsync("doc1", TestContext.Current.CancellationToken);
        Assert.Single(chunks);
        Assert.Equal("角色: 林默", chunks[0].Text);
    }

    [Fact]
    public async Task InMemoryStore_Delete_Works()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var doc = new RetrievalDocument
        {
            DocumentId = "doc1",
            Domain = RetrievalDomain.NovelKnowledge,
            NovelId = "novel1",
            DocumentKind = DocumentKind.CharacterProfile,
            SourceEntityType = "Character",
            SourceEntityId = "ch1",
            IndexStatus = IndexStatus.Ready,
        };

        var chunk = new RetrievalChunk
        {
            ChunkId = "doc1_c0",
            DocumentId = "doc1",
            ChunkOrdinal = 0,
            Text = "测试",
            Embedding = new float[1536],
        };

        await store.UpsertDocumentAsync(doc, [chunk], TestContext.Current.CancellationToken);
        await store.MarkDocumentDeletedAsync("doc1", TestContext.Current.CancellationToken);

        var retrieved = await store.GetDocumentAsync("doc1", TestContext.Current.CancellationToken);
        Assert.Null(retrieved);
    }

    // ══════════════════════════════════════════════════════
    //  IndexDocumentAsync 批量嵌入（IBatchEmbeddingService）
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task IndexDocumentAsync_BatchService_EmbedsAllChunksInOneCall()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var emb = new BatchFakeEmbeddingService();
        var kernel = new RetrievalKernel(store, emb);

        var (doc, chunks) = MakeDocWithChunks(3);
        await kernel.IndexDocumentAsync(doc, chunks, TestContext.Current.CancellationToken);

        Assert.Single(emb.BatchCalls); // 一次批量调用
        Assert.Equal(3, emb.BatchCalls[0].Count);
        Assert.Equal(0, emb.SingleCalls); // 没有走逐条路径

        var stored = await store.GetChunksAsync(
            doc.DocumentId,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(3, stored.Count);
        Assert.All(stored, c => Assert.NotNull(c.Embedding));
        Assert.All(stored, c => Assert.Contains(c.Embedding!, v => v != 0f));
        var storedDoc = await store.GetDocumentAsync(
            doc.DocumentId,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(IndexStatus.Ready, storedDoc!.IndexStatus);
    }

    [Fact]
    public async Task IndexDocumentAsync_BatchFailure_FallsBackToPerChunk()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var emb = new BatchFakeEmbeddingService
        {
            BatchException = new InvalidOperationException("boom"),
        };
        var kernel = new RetrievalKernel(store, emb);

        var (doc, chunks) = MakeDocWithChunks(2);
        await kernel.IndexDocumentAsync(doc, chunks, TestContext.Current.CancellationToken);

        Assert.Equal(2, emb.SingleCalls); // 批量失败 → 逐条重试
        var stored = await store.GetChunksAsync(
            doc.DocumentId,
            TestContext.Current.CancellationToken
        );
        Assert.All(stored, c => Assert.NotNull(c.Embedding));
        var storedDoc = await store.GetDocumentAsync(
            doc.DocumentId,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(IndexStatus.Ready, storedDoc!.IndexStatus);
    }

    [Fact]
    public async Task IndexDocumentAsync_NonBatchService_UsesPerChunk()
    {
        var store = new InMemoryRetrievalStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var emb = new SingleOnlyFakeEmbeddingService();
        var kernel = new RetrievalKernel(store, emb);

        var (doc, chunks) = MakeDocWithChunks(2);
        await kernel.IndexDocumentAsync(doc, chunks, TestContext.Current.CancellationToken);

        Assert.Equal(2, emb.SingleCalls);
        var stored = await store.GetChunksAsync(
            doc.DocumentId,
            TestContext.Current.CancellationToken
        );
        Assert.All(stored, c => Assert.NotNull(c.Embedding));
    }

    private static (RetrievalDocument Doc, List<RetrievalChunk> Chunks) MakeDocWithChunks(int count)
    {
        var doc = new RetrievalDocument
        {
            DocumentId = "batch-doc",
            Domain = RetrievalDomain.NovelKnowledge,
            NovelId = "novel1",
            DocumentKind = DocumentKind.ChapterBodyChunk,
            SourceEntityType = "Chapter",
            SourceEntityId = "ch1",
            IndexStatus = IndexStatus.Pending,
        };
        var chunks = new List<RetrievalChunk>();
        for (var i = 0; i < count; i++)
        {
            chunks.Add(
                new RetrievalChunk
                {
                    ChunkId = $"batch-doc_c{i}",
                    DocumentId = "batch-doc",
                    ChunkOrdinal = i,
                    Text = $"第{i + 1}块文本内容",
                    SourceOffsetStart = i * 10,
                    SourceOffsetEnd = i * 10 + 6,
                    ContentHash = $"hash{i}",
                }
            );
        }
        return (doc, chunks);
    }

    /// <summary>同时实现单条与批量接口的假服务。</summary>
    private sealed class BatchFakeEmbeddingService : IEmbeddingService, IBatchEmbeddingService
    {
        public int Dimension => 8;
        public List<IReadOnlyList<string>> BatchCalls { get; } = [];
        public int SingleCalls { get; private set; }
        public Exception? BatchException { get; init; }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.FromResult(MakeVector(text));
        }

        public Task<float[][]> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken ct = default
        )
        {
            if (BatchException is not null)
                throw BatchException;
            BatchCalls.Add(texts);
            return Task.FromResult(texts.Select(MakeVector).ToArray());
        }

        private static float[] MakeVector(string text)
        {
            var v = new float[8];
            for (var i = 0; i < v.Length; i++)
                v[i] = text.Length + i + 1;
            return v;
        }
    }

    /// <summary>仅实现单条接口的假服务（触发逐条路径）。</summary>
    private sealed class SingleOnlyFakeEmbeddingService : IEmbeddingService
    {
        public int Dimension => 8;
        public int SingleCalls { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            SingleCalls++;
            var v = new float[8];
            for (var i = 0; i < v.Length; i++)
                v[i] = text.Length + i + 1;
            return Task.FromResult(v);
        }
    }
}
