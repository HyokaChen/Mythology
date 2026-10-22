using Core.Data;
using Core.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Core.Tests;

/// <summary>
/// VectorSyncService 增量同步测试：首次全量索引后，未变更实体跳过重索引
/// （source_revision + content_hash 一致），变更实体只重索引自己。
/// 通过文档 IndexedAt 是否变化判断是否发生重索引。
/// </summary>
public sealed class VectorSyncServiceTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly FileNovelStore _store;
    private readonly SqliteRetrievalStore _retrievalStore;
    private readonly VectorSyncService _sync;

    public VectorSyncServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"vecsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _store = new FileNovelStore(_tempRoot, Path.Combine(_tempRoot, ".mythology"));
        _retrievalStore = new SqliteRetrievalStore(Path.Combine(_tempRoot, "vectors.db"));
        _retrievalStore.InitializeAsync().GetAwaiter().GetResult();
        _sync = new VectorSyncService(
            _store,
            _retrievalStore,
            new RetrievalKernel(_retrievalStore, new DummyEmbeddingService()),
            new DocumentBuilder(),
            NullLogger<VectorSyncService>.Instance
        );
    }

    public async ValueTask DisposeAsync()
    {
        await _retrievalStore.DisposeAsync();
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch { }
    }

    [Fact]
    public async Task SecondSync_UnchangedEntities_AllSkipped()
    {
        await _store.SaveNovel(
            new Novel
            {
                Id = "n1",
                Title = "测试小说",
                Genre = "奇幻",
                Description = "描述",
                WritingStyle = "简洁",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林晚",
                Personality = "冷静",
            }
        );

        await _sync.SyncAsync(CancellationToken.None);
        var afterFirst = await GetIndexedAtAsync();
        Assert.Equal(2, afterFirst.Count); // 小说概要 + 角色各一条文档

        await _sync.SyncAsync(CancellationToken.None);
        var afterSecond = await GetIndexedAtAsync();

        Assert.Equal(afterFirst, afterSecond); // 全部跳过：indexed_at 不变
    }

    [Fact]
    public async Task ChangedEntity_OnlyItself_Reindexed()
    {
        await _store.SaveNovel(
            new Novel
            {
                Id = "n1",
                Title = "测试小说",
                Genre = "奇幻",
                Description = "描述",
                WritingStyle = "简洁",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林晚",
                Personality = "冷静",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c2",
                NovelId = "n1",
                Name = "陆离",
                Personality = "热情",
            }
        );

        await _sync.SyncAsync(CancellationToken.None);
        var before = await GetIndexedAtAsync();

        // 修改 c2 并重新保存（UpdatedAt 提升 + 内容哈希变化）
        var c2 = await _store.GetCharacter("c2");
        Assert.NotNull(c2);
        c2!.Personality = "内敛";
        await _store.SaveCharacter(c2);
        await Task.Delay(10);

        await _sync.SyncAsync(CancellationToken.None);
        var after = await GetIndexedAtAsync();

        // 两个角色文档的稳定 document_id（与 DocumentBuilder 相同映射）
        var c1DocId = StableId.DocumentId(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "n1",
            "Character",
            "c1"
        );
        var c2DocId = StableId.DocumentId(
            RetrievalDomain.NovelKnowledge,
            DocumentKind.CharacterProfile,
            "n1",
            "Character",
            "c2"
        );

        Assert.Equal(before.Count, after.Count);
        Assert.NotEqual(before[c2DocId], after[c2DocId]); // c2 被重索引
        Assert.Equal(before[c1DocId], after[c1DocId]); // c1 跳过
    }

    /// <summary>读取 n1 下所有 Ready 文档的 document_id → indexed_at。</summary>
    private async Task<Dictionary<string, DateTime?>> GetIndexedAtAsync()
    {
        var versions = await _retrievalStore.GetDocumentVersionsAsync("n1");
        var result = new Dictionary<string, DateTime?>();
        foreach (var docId in versions.Keys)
        {
            var doc = await _retrievalStore.GetDocumentAsync(docId);
            result[docId] = doc?.IndexedAt;
        }
        return result;
    }
}
