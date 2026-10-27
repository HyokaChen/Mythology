using Core.Data;
using Core.Shared;
using Core.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 端到端回归：实体改名只重分词、**不重嵌入**。
/// 旧实现把小说级 lexical_version 当作文档级过期条件，改名会让该小说所有文档重新 embedding；
/// 现在版本挂在 FTS 分区上，重分词在取版本快照之前完成。
/// </summary>
public sealed class VectorSyncRetokenizeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"mythology-sync-{Guid.NewGuid():N}"
    );
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"mythology-sync-{Guid.NewGuid():N}.db"
    );
    private CountingEmbedder _embedder = null!;
    private FileNovelStore _novelStore = null!;
    private SqliteRetrievalStore _retrievalStore = null!;
    private RetrievalKernel _kernel = null!;
    private VectorSyncService _sync = null!;

    private sealed class CountingEmbedder(int dimension) : IEmbeddingService, IBatchEmbeddingService
    {
        private readonly HashEmbedder _inner = new(dimension);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return _inner.EmbedAsync(text, ct);
        }

        public async Task<float[][]> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _calls);
            var result = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
                result[i] = await _inner.EmbedAsync(texts[i], ct);
            return result;
        }

        public int Dimension => dimension;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _novelStore = new FileNovelStore(
            novelsRoot: Path.Combine(_root, "novels"),
            mythologyDir: _root
        )
        {
            SkipConflictCheck = true,
        };
        _embedder = new CountingEmbedder(64);
        _retrievalStore = new SqliteRetrievalStore(_dbPath, dimension: 64);
        await _retrievalStore.InitializeAsync(CancellationToken.None);
        _kernel = new RetrievalKernel(_retrievalStore, _embedder);
        _sync = new VectorSyncService(
            _novelStore,
            _retrievalStore,
            _kernel,
            new DocumentBuilder(),
            NullLogger<VectorSyncService>.Instance
        );
    }

    public async ValueTask DisposeAsync()
    {
        await _kernel.DisposeAsync();
        await _retrievalStore.DisposeAsync();
        AtomicFile.ForgetPath(_root);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 清理失败不影响断言
        }
    }

    [Fact]
    public async Task RenameCharacter_RetokenizesWithoutReembedding()
    {
        NovelLexicon.Clear();
        try
        {
            var novel = new Novel
            {
                Id = "novel-sync",
                Title = "测试书",
                Genre = "校园",
            };
            await _novelStore.SaveNovel(novel, CancellationToken.None);
            var character = new Character
            {
                Id = "char-sync",
                NovelId = novel.Id,
                Name = "松雪美乃",
                Personality = "安静",
            };
            await _novelStore.SaveCharacter(character, CancellationToken.None);
            // 第二个角色：用来看清「改名只重嵌入被改的那一个文档」，而不是整部小说
            await _novelStore.SaveCharacter(
                new Character
                {
                    Id = "char-other",
                    NovelId = novel.Id,
                    Name = "陈折花",
                },
                CancellationToken.None
            );

            await _sync.SyncAsync(CancellationToken.None);
            var afterFirst = _embedder.Calls;
            Assert.True(afterFirst > 0, "首次同步应当发生 embedding");

            // 改名：松雪美乃 → 松雪彩子
            character.Name = "松雪彩子";
            await _novelStore.SaveCharacter(character, CancellationToken.None);
            await _sync.SyncAsync(CancellationToken.None);

            // 只重嵌入「被改的那个角色文档」；未被改动的角色与小说概要不得重嵌入
            Assert.Equal(afterFirst + 1, _embedder.Calls);

            // 新名字必须已经可检索（词典 + 重分词生效）
            var docs = await _retrievalStore.GetDocumentVersionsAsync(
                novel.Id,
                CancellationToken.None
            );
            var chunkIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var docId in docs.Keys)
            foreach (
                var chunk in await _retrievalStore.GetChunksAsync(docId, CancellationToken.None)
            )
                chunkIds.Add(chunk.ChunkId);

            var hits = await _retrievalStore.LexicalSearchAsync(
                "松雪彩子",
                chunkIds,
                5,
                novel.Id,
                CancellationToken.None,
                useOr: false
            );
            Assert.NotEmpty(hits);
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }
}
