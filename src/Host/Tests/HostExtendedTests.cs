using Core.Data;
using Core.Shared;
using Core.Tools;
using Xunit;

namespace Host.Tests;

/// <summary>
/// Host 层扩展测试：TornadoLlmProvider、PluginStore、VectorStore 配置。
/// </summary>
public class HostExtendedTests
{
    // ══════════════════════════════════════════════════════
    //  TornadoLlmProvider.CreateApi — 多 provider 创建
    // ══════════════════════════════════════════════════════

    [Fact]
    public void CreateProviderApi_MultipleProviders_DoesNotThrow()
    {
        var cfg = new ProviderConfig
        {
            Default = "openai",
            DefaultModel = "gpt-4o",
            ApiKeys =
            [
                new ApiKeyEntry { Provider = "openai", ApiKey = "sk-test1" },
                new ApiKeyEntry { Provider = "anthropic", ApiKey = "sk-test2" },
            ],
        };

        var (api, model, _) = HostBootstrapper.CreateProviderApi(cfg);
        Assert.Equal("gpt-4o", model);
    }

    [Fact]
    public void CreateProviderApi_CustomProvider_WithBaseUrl()
    {
        var cfg = new ProviderConfig
        {
            Default = "custom:my-llm",
            DefaultModel = "custom-model",
            ApiKeys =
            [
                new ApiKeyEntry
                {
                    Provider = "custom",
                    Name = "my-llm",
                    ApiKey = "sk-custom",
                    BaseUrl = "https://api.example.com/v1",
                },
            ],
        };

        var (api, model, _) = HostBootstrapper.CreateProviderApi(cfg);
        Assert.Equal("custom-model", model);
    }

    [Fact]
    public void CreateProviderApi_CustomProvider_WithoutBaseUrl()
    {
        var cfg = new ProviderConfig
        {
            Default = "custom:minimal-custom",
            DefaultModel = "local-model",
            ApiKeys =
            [
                new ApiKeyEntry
                {
                    Provider = "custom",
                    Name = "minimal-custom",
                    ApiKey = "sk-local",
                    BaseUrl = null,
                },
            ],
        };

        var (api, model, _) = HostBootstrapper.CreateProviderApi(cfg);
        Assert.Equal("local-model", model);
    }

    [Fact]
    public void CreateProviderApi_EmptyApiKeys_CreatesEmptyApi()
    {
        var cfg = new ProviderConfig
        {
            Default = "google",
            DefaultModel = "gemini-2.0-flash",
            ApiKeys = [],
        };

        var (api, model, handler) = HostBootstrapper.CreateProviderApi(cfg);
        Assert.Equal("gemini-2.0-flash", model);
        Assert.NotNull(handler);
    }

    [Fact]
    public void CreateProviderApi_CustomProvider_UnknownName_FallsBack()
    {
        var cfg = new ProviderConfig
        {
            Default = "custom:nonexistent-name",
            DefaultModel = "fallback-model",
            ApiKeys =
            [
                new ApiKeyEntry
                {
                    Provider = "custom",
                    Name = "other-name",
                    ApiKey = "sk-other",
                },
            ],
        };

        // Should not throw — gracefully handles missing matching key
        var (api, model, _) = HostBootstrapper.CreateProviderApi(cfg);
        Assert.Equal("fallback-model", model);
    }

    // ══════════════════════════════════════════════════════
    //  LoadNovelHook 行为
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task LoadNovelHook_BeforeToolCall_AllowsExecution()
    {
        var agent = new Core.Agent.Agent(new ToolRegistry());
        var hook = new LoadNovelHook(agent, new MockNovelStore());

        var context = new ToolHookContext
        {
            ToolCallId = "call-1",
            ToolName = "test_tool",
            Arguments = "{}",
        };

        var result = await hook.BeforeToolCallAsync(context, CancellationToken.None);
        Assert.False(result.Block); // LoadNovelHook should not block any tool
    }

    [Fact]
    public async Task LoadNovelHook_AfterToolCall_DoesNotThrow()
    {
        var agent = new Core.Agent.Agent(new ToolRegistry());
        agent.CurrentNovelId = "novel-1";
        var hook = new LoadNovelHook(agent, new MockNovelStore());

        var context = new ToolHookContext
        {
            ToolCallId = "call-2",
            ToolName = "manage_novel",
            Arguments = """{"novel-id":"novel-1"}""",
            Agent = agent,
        };

        // AfterToolCall should not throw even with no result
        await hook.AfterToolCallAsync(context, CancellationToken.None);
    }

    // ══════════════════════════════════════════════════════
    //  PluginStore — 接口定义验证
    // ══════════════════════════════════════════════════════

    [Fact]
    public void PluginStore_Interface_IsAvailable()
    {
        // IPluginStore is defined in Core.Data and used by plugins
        var storeType = typeof(IPluginStore);
        Assert.True(storeType.IsInterface);
        Assert.True(storeType.IsPublic);
    }

    [Fact]
    public void PluginStore_DefinesGetNovelSummary()
    {
        var method = typeof(IPluginStore).GetMethod("GetNovelSummary");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<NovelSummary?>), method!.ReturnType);
    }

    // ══════════════════════════════════════════════════════
    //  ProductInfo
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ProductInfo_Name_IsNotEmpty()
    {
        Assert.NotEmpty(ProductInfo.Name);
    }

    [Fact]
    public void ProductInfo_Version_IsNotEmpty()
    {
        Assert.NotEmpty(ProductInfo.CurrentVersion);
    }

    // ══════════════════════════════════════════════════════
    //  MythologyPaths
    // ══════════════════════════════════════════════════════

    [Fact]
    public void MythologyPaths_GetMythologyDir_EndsWithDotMythology()
    {
        var dir = MythologyPaths.GetMythologyDir();
        Assert.NotEmpty(dir);
        Assert.EndsWith(MythologyPaths.ConfigDirName, dir);
    }

    [Fact]
    public void MythologyPaths_GetSessionsDir_IsUnderMythologyDir()
    {
        var sessionsDir = MythologyPaths.GetSessionsDir();
        Assert.Contains(MythologyPaths.ConfigDirName, sessionsDir);
        Assert.EndsWith(MythologyPaths.SessionsSubDir, sessionsDir);
    }

    [Fact]
    public void MythologyPaths_GetSessionDirName_ContainsNovelId()
    {
        var name = MythologyPaths.GetSessionDirName("novel123");
        Assert.Contains("novel123", name);
    }

    [Fact]
    public void MythologyPaths_GetNovelsRoot_EndsWithNovels()
    {
        var root = MythologyPaths.GetNovelsRoot();
        Assert.EndsWith(MythologyPaths.NovelsSubDir, root);
    }
}
