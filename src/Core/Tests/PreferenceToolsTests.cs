using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Core.Tests;

/// <summary>
/// VEC-LOCAL §3.6 偏好工具测试：save/recall/list/delete_preference。
/// 覆盖：作用域（global/novel）、确认语义、候选推断隔离、ID 删除撤销。
/// </summary>
public sealed class PreferenceToolsTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly AgentMemoryStore _store;
    private readonly AgentType _agent;
    private readonly RetrievalKernel _kernel;
    private bool _disposed;

    public PreferenceToolsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_pref_{Guid.NewGuid():N}.db");
        _store = new AgentMemoryStore(_dbPath, dimension: 128);
        _store.InitializeAsync().GetAwaiter().GetResult();
        _agent = new AgentType(new ToolRegistry()) { CurrentNovelId = "novel-x" };
        _kernel = new RetrievalKernel(
            new InMemoryRetrievalStore(),
            new HashEmbedder(128),
            agentMemoryStore: _store
        );
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
        TParams args
    )
        where TTool : AgentTool<TParams, AgentMemoryDetails>
        where TParams : class, new() =>
        tool.ExecuteAsync("test-call", args, TestContext.Current.CancellationToken);

    // ══════════════════════════════════════════════════════
    //  save_preference
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task SavePreference_GlobalScope_SavesWithoutNovel()
    {
        var tool = new SavePreferenceTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new SavePreferenceParams { Content = "偏好短句", Scope = "global" }
        );

        Assert.False(result.IsError);
        Assert.Contains("已保存", GetText(result));
        var saved = await _store.GetAllAsync(
            "local",
            ns: "default",
            novelId: null,
            ct: TestContext.Current.CancellationToken
        );
        var mem = Assert.Single(saved);
        Assert.Null(mem.NovelId);
        Assert.Equal(AgentMemoryKind.Explicit, mem.MemoryKind);
    }

    [Fact]
    public async Task SavePreference_NovelScope_UsesCurrentNovel()
    {
        var tool = new SavePreferenceTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new SavePreferenceParams { Content = "这本小说不用文言文", Scope = "novel" }
        );

        Assert.False(result.IsError);
        var saved = await _store.GetAllAsync(
            "local",
            ns: "default",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        var mem = Assert.Single(saved);
        Assert.Equal("novel-x", mem.NovelId);
    }

    [Fact]
    public async Task SavePreference_ConfirmedSource_UsesConfirmedInferenceKind()
    {
        var tool = new SavePreferenceTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new SavePreferenceParams
            {
                Content = "常压缩长段环境描写",
                Scope = "global",
                Source = "confirmed",
            }
        );

        Assert.False(result.IsError);
        var saved = await _store.GetAllAsync(
            "local",
            ns: "default",
            novelId: null,
            ct: TestContext.Current.CancellationToken
        );
        var mem = Assert.Single(saved);
        Assert.Equal(AgentMemoryKind.ConfirmedInference, mem.MemoryKind);
        Assert.True(mem.Confidence <= 0.6, "确认推断置信度不得超过 0.6");
    }

    [Fact]
    public async Task SavePreference_InvalidScope_ReturnsError()
    {
        var tool = new SavePreferenceTool(_agent, _store);
        var result = await RunAsync(
            tool,
            new SavePreferenceParams { Content = "x", Scope = "bogus" }
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task SavePreference_NullContent_ReturnsError()
    {
        var tool = new SavePreferenceTool(_agent, _store);
        var result = await RunAsync(tool, new SavePreferenceParams { Content = "  " });

        Assert.True(result.IsError);
    }

    // ══════════════════════════════════════════════════════
    //  list_preferences
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ListPreferences_ExcludesCandidateInference()
    {
        // 写入：1 显式 + 1 候选推断
        await _store.StoreAsync(
            "偏好短句",
            "local",
            ns: "default",
            memoryKind: AgentMemoryKind.Explicit,
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "可能偏好破折号",
            "local",
            ns: "default",
            memoryKind: AgentMemoryKind.CandidateInference,
            ct: TestContext.Current.CancellationToken
        );

        var tool = new ListPreferencesTool(_agent, _store);
        var result = await RunAsync(tool, new ListPreferencesParams());

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("偏好短句", text);
        Assert.DoesNotContain("可能偏好破折号", text);
        Assert.Equal(1, result.TypedDetails?.Count);
    }

    [Fact]
    public async Task ListPreferences_NovelScope_FiltersByNovel()
    {
        await _store.StoreAsync(
            "全局规则A",
            "local",
            ns: "rule",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "小说规则B",
            "local",
            novelId: "novel-x",
            ns: "rule",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new ListPreferencesTool(_agent, _store);
        var result = await RunAsync(tool, new ListPreferencesParams { Scope = "novel" });

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("小说规则B", text);
        Assert.DoesNotContain("全局规则A", text);
    }

    // ══════════════════════════════════════════════════════
    //  delete_preference
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task DeletePreference_RemovesById()
    {
        var mem = await _store.StoreAsync(
            "不再需要的偏好",
            "local",
            ns: "default",
            memoryKind: AgentMemoryKind.Explicit,
            ct: TestContext.Current.CancellationToken
        );

        var tool = new DeletePreferenceTool(_store);
        var result = await RunAsync(tool, new DeletePreferenceParams { Id = mem.Id[..8] });

        Assert.False(result.IsError);
        Assert.Contains("已删除", GetText(result));
        Assert.Null(await _store.GetAsync(mem.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletePreference_UnknownId_ReturnsError()
    {
        var tool = new DeletePreferenceTool(_store);
        var result = await RunAsync(tool, new DeletePreferenceParams { Id = "deadbeef" });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task DeletePreference_EmptyId_ReturnsError()
    {
        var tool = new DeletePreferenceTool(_store);
        var result = await RunAsync(tool, new DeletePreferenceParams { Id = "" });

        Assert.True(result.IsError);
    }

    // ══════════════════════════════════════════════════════
    //  recall_preference
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task RecallPreference_EmptyQuery_ReturnsAllRules()
    {
        await _store.StoreAsync(
            "不用文言文",
            "local",
            ns: "rule",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "偏好短句",
            "local",
            ns: "default",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new RecallPreferenceTool(_agent, _kernel);
        var result = await RunAsync(tool, new RecallPreferenceParams());

        Assert.False(result.IsError);
        var text = GetText(result);
        // §3.4：无 query → 全量返回显式规则（硬规则不因相似度丢失）
        Assert.Contains("不用文言文", text);
    }

    [Fact]
    public async Task RecallPreference_WithQuery_ReturnsRulesAndPreferences()
    {
        await _store.StoreAsync(
            "不用文言文",
            "local",
            ns: "rule",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "偏好短句",
            "local",
            ns: "default",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new RecallPreferenceTool(_agent, _kernel);
        // query 与偏好共享词汇：偏好走 hybrid 相似度召回
        var result = await RunAsync(tool, new RecallPreferenceParams { Query = "短句" });

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("不用文言文", text);
        Assert.Contains("偏好短句", text);
    }

    [Fact]
    public async Task RecallPreference_CandidateInference_NeverInjected()
    {
        // 候选推断（kind=2）写入后：recall 不得返回（VEC-LOCAL §3.1/§4.4 红线①）
        await _store.StoreAsync(
            "可能偏好破折号",
            "local",
            ns: "default",
            novelId: "novel-x",
            memoryKind: AgentMemoryKind.CandidateInference,
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "偏好短句",
            "local",
            ns: "default",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new RecallPreferenceTool(_agent, _kernel);
        var result = await RunAsync(tool, new RecallPreferenceParams { Query = "破折号" });

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.DoesNotContain("可能偏好破折号", text);
    }

    [Fact]
    public async Task RecallPreference_RuleOutranksPreference()
    {
        // 规则（keyword 精确命中）必须优先于偏好（语义相似）——确定性优先（§3.4）
        await _store.StoreAsync(
            "不用文言文",
            "local",
            ns: "rule",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "偏好短句",
            "local",
            ns: "default",
            novelId: "novel-x",
            ct: TestContext.Current.CancellationToken
        );

        var tool = new RecallPreferenceTool(_agent, _kernel);
        // query 同时关联两者：规则走 keyword（确定性），偏好走 hybrid 相似度
        var result = await RunAsync(tool, new RecallPreferenceParams { Query = "短句" });

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("不用文言文", text); // 规则必须被召回
        Assert.Contains("偏好短句", text); // 偏好也被召回
        Assert.True(
            text.IndexOf("不用文言文", StringComparison.Ordinal)
                < text.IndexOf("偏好短句", StringComparison.Ordinal)
        );
    }

    private static string GetText(AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
