using System.Text.Json;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Core.Tests;

/// <summary>
/// AgentMemory 13 个工具测试：happy path + 参数错误路径 + novelId 传递。
/// 工具是 LLM 与记忆系统的唯一入口，错误语义直接影响 Agent Loop。
/// </summary>
public sealed class AgentMemoryToolsTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly AgentMemoryStore _store;
    private readonly AgentType _agent;
    private bool _disposed;

    public AgentMemoryToolsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_tools_{Guid.NewGuid():N}.db");
        _store = new AgentMemoryStore(_dbPath, dimension: 128);
        _store.InitializeAsync().GetAwaiter().GetResult();
        _agent = new AgentType(new ToolRegistry()) { CurrentNovelId = "novel-x" };
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _store.DisposeAsync();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    private static Task<AgentToolResult<AgentMemoryDetails>> RunAsync<TTool, TParams>(
        TTool tool,
        TParams args,
        CancellationToken ct = default
    )
        where TTool : AgentTool<TParams, AgentMemoryDetails>
        where TParams : class, new() => tool.ExecuteAsync("test-call", args, ct);

    // ══════════════════════════════════════════════════════
    //  1. memory_store
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolStore_StoresMemory()
    {
        var tool = new StoreMemoryTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new StoreMemoryParams { Content = "主角怕蜘蛛", Ns = "character" },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.False(string.IsNullOrEmpty(result.TypedDetails!.Id));

        var mem = await _store.GetAsync(
            result.TypedDetails!.Id,
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotNull(mem);
        Assert.Equal("主角怕蜘蛛", mem!.Content);
        Assert.Equal("character", mem.Namespace);
        Assert.Equal("novel-x", mem.NovelId); // 自动绑定当前小说
    }

    [Fact]
    public async Task ToolStore_EmptyContent_ReturnsError()
    {
        var tool = new StoreMemoryTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new StoreMemoryParams { Content = "  " },
            ct: TestContext.Current.CancellationToken
        );
        Assert.True(result.IsError);
    }

    /// <summary>scope 决定落库维度：session 带 sessionId；global 不带 novelId；novel 用当前小说。</summary>
    [Fact]
    public async Task StoreMemoryTool_Scope_SetsNovelAndSessionDimensions()
    {
        var tool = new StoreMemoryTool(_agent, _store);
        _agent.CurrentNovelId = "novel-1";
        _agent.SessionId = "sess-1";

        await tool.ExecuteAsync(
            "c1",
            JsonDocument.Parse("""{"content":"会话级结论","scope":"session"}""").RootElement,
            TestContext.Current.CancellationToken
        );
        await tool.ExecuteAsync(
            "c2",
            JsonDocument.Parse("""{"content":"小说级结论","scope":"novel"}""").RootElement,
            TestContext.Current.CancellationToken
        );
        await tool.ExecuteAsync(
            "c3",
            JsonDocument.Parse("""{"content":"跨小说约定","scope":"global"}""").RootElement,
            TestContext.Current.CancellationToken
        );
        await tool.ExecuteAsync(
            "c4",
            JsonDocument.Parse("""{"content":"非法作用域","scope":"planet"}""").RootElement,
            TestContext.Current.CancellationToken
        );

        var all = await _store.GetAllAsync(
            "local",
            novelId: "novel-1",
            sessionId: "sess-1",
            scope: MemoryVisibility.All,
            ct: TestContext.Current.CancellationToken
        );
        Assert.Contains(all, m => m is { SessionId: "sess-1", NovelId: "novel-1" });
        Assert.Contains(all, m => m is { SessionId: null, NovelId: "novel-1" });
        Assert.Contains(all, m => m is { SessionId: null, NovelId: null });
        Assert.DoesNotContain(all, m => m.Content == "非法作用域");
    }

    // ══════════════════════════════════════════════════════
    //  2. memory_recall
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolRecall_ReturnsMatches()
    {
        await _store.StoreAsync(
            "主角怕蜘蛛",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "天气晴朗",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new RecallMemoryTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new RecallMemoryParams
            {
                Query = "蜘蛛",
                K = 5,
                Mode = "keyword",
            },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(1, result.TypedDetails!.Count);
        Assert.Contains(result.Content, c => ((TextContent)c).Text.Contains("主角怕蜘蛛"));
    }

    [Fact]
    public async Task ToolRecall_EmptyQuery_ReturnsError()
    {
        var tool = new RecallMemoryTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new RecallMemoryParams { Query = "" },
            ct: TestContext.Current.CancellationToken
        );
        Assert.True(result.IsError);
    }

    // ══════════════════════════════════════════════════════
    //  3. memory_reinforce
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolReinforce_BoostsImportance()
    {
        var mem = await _store.StoreAsync(
            "重要设定",
            "local",
            ct: TestContext.Current.CancellationToken
        );
        var tool = new ReinforceMemoryTool(_store);
        var result = await RunAsync(
            tool,
            new ReinforceMemoryParams { MemoryId = mem.Id },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var updated = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.True(updated!.Importance > mem.Importance);
    }

    [Fact]
    public async Task ToolReinforce_MissingId_ReturnsError()
    {
        var tool = new ReinforceMemoryTool(_store);
        var result = await RunAsync(
            tool,
            new ReinforceMemoryParams { MemoryId = "no-such" },
            ct: TestContext.Current.CancellationToken
        );
        Assert.True(result.IsError);
    }

    // ══════════════════════════════════════════════════════
    //  4. memory_forget
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolForget_DeletesMemory()
    {
        var mem = await _store.StoreAsync(
            "将被遗忘",
            "local",
            ct: TestContext.Current.CancellationToken
        );
        var tool = new ForgetMemoryTool(_store);
        var result = await RunAsync(
            tool,
            new ForgetMemoryParams { MemoryId = mem.Id },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(1, result.TypedDetails!.Count);
        Assert.Null(await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken));
    }

    // ══════════════════════════════════════════════════════
    //  5/6. memory_mark_useful / memory_mark_useless
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolMarkUseful_RaisesUsefulness()
    {
        var mem = await _store.StoreAsync(
            "有用的记忆",
            "local",
            ct: TestContext.Current.CancellationToken
        );
        var result = await RunAsync(
            new MarkUsefulTool(_store),
            new MarkUsefulParams { MemoryId = mem.Id },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var updated = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.True(updated!.Usefulness > 0);
    }

    [Fact]
    public async Task ToolMarkUseless_LowersUsefulness()
    {
        var mem = await _store.StoreAsync(
            "无用的记忆",
            "local",
            ct: TestContext.Current.CancellationToken
        );
        var result = await RunAsync(
            new MarkUselessTool(_store),
            new MarkUsefulParams { MemoryId = mem.Id },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var updated = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.True(updated!.Usefulness < 0);
    }

    // ══════════════════════════════════════════════════════
    //  7. memory_find_conflicts
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolFindConflicts_DetectsNegation()
    {
        await _store.StoreAsync(
            "主角怕蜘蛛",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "主角不怕蜘蛛",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new FindConflictsTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new FindConflictsParams { Query = "蜘蛛" },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.True(result.TypedDetails!.Count >= 1);
    }

    [Fact]
    public async Task ToolFindConflicts_NoConflicts_ReturnsZero()
    {
        await _store.StoreAsync(
            "主角怕蜘蛛",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        var tool = new FindConflictsTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new FindConflictsParams { Query = "蜘蛛" },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(0, result.TypedDetails!.Count);
    }

    // ══════════════════════════════════════════════════════
    //  8. memory_resolve_conflict
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolResolveConflict_DropsLoser()
    {
        var keep = await _store.StoreAsync(
            "主角怕蜘蛛",
            "local",
            ct: TestContext.Current.CancellationToken
        );
        var drop = await _store.StoreAsync(
            "主角不怕蜘蛛",
            "local",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new ResolveConflictTool(_store);
        var result = await RunAsync(
            tool,
            new ResolveConflictParams { KeepId = keep.Id, DropIds = [drop.Id] },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.NotNull(await _store.GetAsync(keep.Id, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _store.GetAsync(drop.Id, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ToolResolveConflict_EmptyDropIds_ReturnsError()
    {
        var tool = new ResolveConflictTool(_store);
        var result = await RunAsync(
            tool,
            new ResolveConflictParams { KeepId = "x", DropIds = [] },
            ct: TestContext.Current.CancellationToken
        );
        Assert.True(result.IsError);
    }

    // ══════════════════════════════════════════════════════
    //  9. memory_snapshot
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolSnapshot_CreatesSnapshot()
    {
        var tool = new SnapshotTool(_store);
        var result = await RunAsync(
            tool,
            new SnapshotParams { Label = "ch5-done" },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var snaps = await _store.SnapshotsAsync(ct: TestContext.Current.CancellationToken);
        Assert.Contains("ch5-done", snaps.Keys);
    }

    // ══════════════════════════════════════════════════════
    //  10. memory_as_of（novelId 过滤）
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolAsOf_FiltersByCurrentNovel()
    {
        // 当前小说 novel-x + 全局记忆 + 另一本小说 novel-y
        await _store.StoreAsync(
            "小说X的记忆",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        // novelId null = 全局
        await _store.StoreAsync("全局记忆", "local", ct: TestContext.Current.CancellationToken);
        await _store.StoreAsync(
            "小说Y的记忆",
            "local",
            novelId: "novel-y",
            ct: TestContext.Current.CancellationToken
        );
        await _store.SnapshotAsync("snap", ct: TestContext.Current.CancellationToken);

        var tool = new AsOfTool(_agent, _store); // CurrentNovelId = "novel-x"
        var result = await RunAsync(
            tool,
            new AsOfParams { Label = "snap" },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(2, result.TypedDetails!.Count); // 小说X + 全局，不含小说Y
        Assert.Contains(result.Content, c => ((TextContent)c).Text.Contains("小说X的记忆"));
        Assert.Contains(result.Content, c => ((TextContent)c).Text.Contains("全局记忆"));
        Assert.DoesNotContain(result.Content, c => ((TextContent)c).Text.Contains("小说Y的记忆"));
    }

    // ══════════════════════════════════════════════════════
    //  11. memory_diff
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolDiff_ReportsChanges()
    {
        var mem = await _store.StoreAsync(
            "旧设定",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        await _store.SnapshotAsync("before", ct: TestContext.Current.CancellationToken);
        await _store.ForgetAsync(memoryId: mem.Id, ct: TestContext.Current.CancellationToken);
        await _store.SnapshotAsync("after", ct: TestContext.Current.CancellationToken);

        var tool = new DiffTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new DiffParams { BeforeLabel = "before", AfterLabel = "after" },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.True(result.TypedDetails!.Count >= 1);
        Assert.Contains(result.Content, c => ((TextContent)c).Text.Contains('-')); // removed 行
    }

    // ══════════════════════════════════════════════════════
    //  12. memory_pack
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolPack_RespectsBudget()
    {
        for (var i = 0; i < 10; i++)
            await _store.StoreAsync(
                $"记忆内容编号{i}：主角在古堡冒险",
                "local",
                novelId: "novel-x",
                ct: TestContext.Current.CancellationToken
            );

        var tool = new PackTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new PackParams
            {
                Query = "古堡",
                BudgetTokens = 200,
                Mode = "keyword",
            },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.True(result.TypedDetails!.Count >= 1);
    }

    // ══════════════════════════════════════════════════════
    //  13. memory_consolidate
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ToolConsolidate_MergesDuplicates()
    {
        await _store.StoreAsync(
            "主角怕蜘蛛",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "主角怕蜘蛛",
            "local",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new ConsolidateTool(_store);
        var result = await RunAsync(
            tool,
            new ConsolidateParams { Ns = "default" },
            ct: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.True(result.TypedDetails!.Count >= 1);
        Assert.Single(
            await _store.GetAllAsync(
                "local",
                ns: "default",
                novelId: "novel-x",
                ct: TestContext.Current.CancellationToken
            )
        );
    }
}

/// <summary>
/// memory_analyze_style（VEC-LOCAL 域三：候选推断）测试：
/// 章节不足报错、小说不存在报错、稳定短段落模式产生候选。
/// </summary>
public sealed class AnalyzeStyleToolTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly FileNovelStore _novels;
    private readonly AgentType _agent;
    private bool _disposed;

    public AnalyzeStyleToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"test_style_{Guid.NewGuid():N}");
        _novels = new FileNovelStore(_tempRoot, Path.Combine(_tempRoot, "myth"));
        _agent = new AgentType(new ToolRegistry()) { CurrentNovelId = "novel-x" };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnalyzeStyle_InsufficientChapters_ReturnsError()
    {
        await _novels.SaveNovel(new Novel { Id = "novel-x", Title = "测试" });
        await _novels.SaveChapter(
            new Chapter
            {
                NovelId = "novel-x",
                Order = 1,
                Content = "只有一章。",
            }
        );

        var tool = new AnalyzeStyleTool(_agent, _novels);
        var result = await tool.ExecuteAsync(
            "test-call",
            new AnalyzeStyleParams(),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Contains(result.Content, c => ((TextContent)c).Text.Contains("不足 3 章"));
    }

    [Fact]
    public async Task AnalyzeStyle_NovelNotFound_ReturnsError()
    {
        var tool = new AnalyzeStyleTool(_agent, _novels);
        var result = await tool.ExecuteAsync(
            "test-call",
            new AnalyzeStyleParams(),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Contains(result.Content, c => ((TextContent)c).Text.Contains("不存在"));
    }

    [Fact]
    public async Task AnalyzeStyle_ConsistentShortParagraphs_ProducesCandidates()
    {
        await _novels.SaveNovel(new Novel { Id = "novel-x", Title = "测试" });
        for (var i = 1; i <= 3; i++)
        {
            await _novels.SaveChapter(
                new Chapter
                {
                    NovelId = "novel-x",
                    Order = i,
                    Title = $"第{i}章",
                    Content = "他推门而入。\n雨还在下。",
                }
            );
        }

        var tool = new AnalyzeStyleTool(_agent, _novels);
        var result = await tool.ExecuteAsync(
            "test-call",
            new AnalyzeStyleParams(),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var details = Assert.IsType<AgentMemoryDetails>(result.Details);
        Assert.True(details.Count >= 1);
        Assert.Contains(result.Content, c => ((TextContent)c).Text.Contains("候选风格推断"));
    }
}
