using Core.Vector;
using Microsoft.Data.Sqlite;
using StabilityHarness.Retrieval;
using Xunit;

namespace StabilityHarness.Tests;

/// <summary>
/// 检索评测回归：gold 从档案文档推导、三臂指标可算，且「实体注册 + 片段」不劣于基线。
/// 用临时 SQLite 库构造 3 篇文档（角色档案 / 关系陈述 / 正文块），不依赖本机 vectors.db。
/// </summary>
public sealed class RetrievalEvalTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"retrieval-eval-{Guid.NewGuid():N}.db"
    );

    public ValueTask InitializeAsync() => new(Task.CompletedTask);

    public ValueTask DisposeAsync()
    {
        NovelLexicon.Clear();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        return new ValueTask(Task.CompletedTask);
    }

    [Fact]
    public async Task RunAsync_BuildsGoldFromProfileDocs_AndCurrentArmIsNotWorse()
    {
        NovelLexicon.Clear();
        await PrepareDbAsync();

        var (metrics, details) = await RetrievalEval.RunAsync(
            _dbPath,
            "novel-eval",
            GoldMode.Strict
        );

        Assert.Equal(3, metrics.Count);
        Assert.All(metrics, m => Assert.True(m.Queries > 0));
        Assert.NotEmpty(details);

        var baseline = metrics[0];
        var current = metrics[1];
        Assert.True(
            current.Recall5 >= baseline.Recall5,
            $"现行 R@5 ({current.Recall5:P0}) 不应低于基线 ({baseline.Recall5:P0})"
        );
    }

    private async Task PrepareDbAsync()
    {
        await using var store = new SqliteRetrievalStore(_dbPath, dimension: 4);
        await store.InitializeAsync(CancellationToken.None);

        // 角色档案（推断 gold 的锚点）
        await store.UpsertDocumentAsync(
            Doc("doc-char", DocumentKind.CharacterProfile, "char-1"),
            [Chunk("chunk-char", "doc-char", "角色: 陈折花\n性格: 沉默寡言")],
            CancellationToken.None
        );
        // 关系陈述（提到该角色）
        await store.UpsertDocumentAsync(
            Doc("doc-rel", DocumentKind.RelationshipStatement, "rel-1"),
            [Chunk("chunk-rel", "doc-rel", "关系: 陈折花 与 海野天音 — Friend")],
            CancellationToken.None
        );
        // 正文块（提到部分名「折花」，用于检验片段召回）
        await store.UpsertDocumentAsync(
            Doc("doc-body", DocumentKind.ChapterBodyChunk, "ch-1"),
            [Chunk("chunk-body", "doc-body", "折花站在门口，海野天音没有回头。")],
            CancellationToken.None
        );
    }

    private static RetrievalDocument Doc(string id, DocumentKind kind, string entityId) =>
        new()
        {
            DocumentId = id,
            Domain = RetrievalDomain.NovelKnowledge,
            DocumentKind = kind,
            NovelId = "novel-eval",
            SourceEntityType = "CharacterTest",
            SourceEntityId = entityId,
            SourceRevision = 1,
            ContentHash = $"hash-{id}",
            IndexStatus = IndexStatus.Ready,
        };

    private static RetrievalChunk Chunk(string id, string docId, string text) =>
        new()
        {
            ChunkId = id,
            DocumentId = docId,
            Text = text,
            ContentHash = $"ch-{id}",
            BoundaryQuality = "sentence_end",
        };
}
