using Core.Data;
using Tacos;
using Xunit;

namespace Core.Tests;

public sealed class NovelSemanticServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public NovelSemanticServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"novel-semantic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    [Fact]
    public void L3Aggregates_MatchExpected()
    {
        CreateNovel(
            "nov001",
            chapters:
            [
                ("ch001", "第一章", 1, "draft", "第一章内容。"),
                ("ch002", "第二章", 2, "published", "第二章内容内容。"),
            ],
            characters: [("c001", "角色A"), ("c002", "角色B"), ("c003", "角色C")],
            relationships: [("c001", "c002")],
            plotlines:
            [
                ("p001", "伏笔一", "foreshadowing", null),
                ("p002", "回收伏笔", "foreshadowing", "ch001"),
                ("p003", "主线", "main", null),
            ]
        );

        var service = new NovelSemanticService(_tempRoot);
        service.ProbeAll();

        Assert.Equal(1, service.DraftChapterCount("nov001"));
        Assert.Equal(1, service.OrphanCharacterCount("nov001")); // c003 无关系
        Assert.Equal(1, service.OverdueForeshadowCount("nov001")); // p001 未回收
        Assert.True(service.TotalWordCount("nov001") > 0);
    }

    [Fact]
    public void ExternalEdit_IsPickedUpByMtimeProbe()
    {
        var novelDir = CreateNovel(
            "nov002",
            chapters: [("ch001", "第一章", 1, "draft", "旧正文")],
            characters: [],
            relationships: [],
            plotlines: []
        );

        var service = new NovelSemanticService(_tempRoot);
        service.ProbeAll();
        Assert.Equal(1, service.DraftChapterCount("nov002"));
        Assert.True(service.TotalWordCount("nov002") > 0);
        var oldCount = service.TotalWordCount("nov002");

        // 模拟外部编辑：改状态并追加正文（不经过 FileNovelStore 事件）
        var chapterPath = Path.Combine(novelDir, "chapters", "ch001.md");
        File.WriteAllText(
            chapterPath,
            """
            ---
            id: ch001
            order: 1
            title: 第一章
            status: published
            ---
            新正文更长一些。
            """
        );

        service.ProbeAll();
        Assert.Equal(0, service.DraftChapterCount("nov002"));
        Assert.True(service.TotalWordCount("nov002") > oldCount);
    }

    [Fact]
    public void StatusOnlyEdit_DoesNotRecomputeDraftCount()
    {
        var novelDir = CreateNovel(
            "nov003",
            chapters: [("ch001", "第一章", 1, "draft", "正文内容")],
            characters: [],
            relationships: [],
            plotlines: []
        );

        var service = new NovelSemanticService(_tempRoot);
        service.ProbeAll();
        Assert.Equal(1, service.DraftChapterCount("nov003"));

        var executions = 0;
        using var subscription = service.SubscribeEvents(
            new CountingObserver("DraftChapterCount", () => executions++)
        );

        // 外部编辑：正文变化但状态仍为 draft
        var chapterPath = Path.Combine(novelDir, "chapters", "ch001.md");
        File.WriteAllText(
            chapterPath,
            """
            ---
            id: ch001
            order: 1
            title: 第一章
            status: draft
            ---
            正文内容（只是加了个错别字修正）。
            """
        );

        service.ProbeAll();
        _ = service.DraftChapterCount("nov003");

        // L2 StatusOf 吸收正文变化后，DraftChapterCount 不应重新执行。
        Assert.Equal(0, executions);
    }

    // ──────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────

    private string CreateNovel(
        string novelId,
        IReadOnlyList<(string Id, string Title, int Order, string Status, string Content)> chapters,
        IReadOnlyList<(string Id, string Name)> characters,
        IReadOnlyList<(string AId, string BId)> relationships,
        IReadOnlyList<(string Id, string Name, string Type, string? ResolvedAt)> plotlines
    )
    {
        var novelDir = Path.Combine(_tempRoot, $"novel-{novelId}");
        Directory.CreateDirectory(Path.Combine(novelDir, "chapters"));
        Directory.CreateDirectory(Path.Combine(novelDir, "characters"));
        Directory.CreateDirectory(Path.Combine(novelDir, "plotlines"));

        File.WriteAllText(
            Path.Combine(novelDir, "novel.yaml"),
            $"id: {novelId}\ntitle: 测试小说 {novelId}\n"
        );

        foreach (var (id, title, order, status, content) in chapters)
        {
            File.WriteAllText(
                Path.Combine(novelDir, "chapters", $"ch{order:D3}.md"),
                $"""
                ---
                id: {id}
                order: {order}
                title: {title}
                status: {status}
                ---
                {content}
                """
            );
        }

        foreach (var (id, name) in characters)
        {
            File.WriteAllText(
                Path.Combine(novelDir, "characters", $"{name}-{id}.yaml"),
                $"id: {id}\nnovel-id: {novelId}\nname: {name}\nrole: supporting\nstatus: active\n"
            );
        }

        if (relationships.Count > 0)
        {
            var lines = relationships
                .Select(r => $"- character-a-id: {r.AId}\n  character-b-id: {r.BId}")
                .ToList();
            File.WriteAllText(
                Path.Combine(novelDir, "relationships.yaml"),
                string.Join("\n", lines) + "\n"
            );
        }

        foreach (var (id, name, type, resolvedAt) in plotlines)
        {
            var resolvedLine = resolvedAt is null ? "" : $"resolved-at-chapter-id: {resolvedAt}\n";
            File.WriteAllText(
                Path.Combine(novelDir, "plotlines", $"{name}-{id}.yaml"),
                $"id: {id}\nnovel-id: {novelId}\nname: {name}\ntype: {type}\n{resolvedLine}"
            );
        }

        return novelDir;
    }

    private sealed class CountingObserver(string queryName, Action onExecute) : ITacosObserver
    {
        public void OnWillExecute(in TacosWillExecuteEventArgs args)
        {
            if (args.QueryName == queryName)
                onExecute();
        }

        public void OnDidExecute(in TacosDidExecuteEventArgs args) { }

        public void OnMemoHit(in TacosMemoHitEventArgs args) { }

        public void OnValueChanged(in TacosValueChangedEventArgs args) { }

        public void OnValidatedGreen(in TacosValidatedGreenEventArgs args) { }

        public void OnTransactionCommitted(in TacosTransactionCommittedEventArgs args) { }

        public void OnCancellationRequested(in TacosCancellationRequestedEventArgs args) { }

        public void OnCycleRecovered(in TacosCycleRecoveredEventArgs args) { }

        public void OnCachesMaintained(in TacosCachesMaintainedEventArgs args) { }
    }
}
