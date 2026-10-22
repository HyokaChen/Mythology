using Core.Data;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// SettingsService 测试：配置加载、保存、插件扫描。
/// </summary>
public class SettingsServiceTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly ConfigStore _configStore;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"settings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configStore = new ConfigStore(_tempDir);
        _service = new SettingsService(_configStore);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch { }
        return default;
    }

    [Fact]
    public void LoadAll_CreatesDefaultConfig_WhenMissing()
    {
        // Use a unique temp directory to ensure clean state
        var uniqueTempDir = Path.Combine(Path.GetTempPath(), $"settings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(uniqueTempDir);
        try
        {
            var configStore = new ConfigStore(uniqueTempDir);
            var service = new SettingsService(configStore);
            service.LoadAll();

            Assert.Equal("openai", service.Provider.Default);
            Assert.Equal("gpt-4o-mini", service.Provider.DefaultModel);
            Assert.Equal("medium", service.Provider.ThinkingLevel);
        }
        finally
        {
            try
            {
                Directory.Delete(uniqueTempDir, true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task LoadAll_WithExistingConfig_LoadsCorrectly()
    {
        var cfg = new ConfigData
        {
            Providers = new ProviderConfig
            {
                Default = "anthropic",
                DefaultModel = "claude-3",
                ThinkingLevel = "high",
                ApiKeys = [new ApiKeyEntry { Provider = "anthropic", ApiKey = "sk-test" }],
            },
            Mcp = new McpConfig
            {
                Servers =
                [
                    new McpServerEntry
                    {
                        Name = "test",
                        Command = "cmd",
                        Enabled = true,
                    },
                ],
            },
        };
        await _configStore.SaveAsync(cfg);

        _service.LoadAll();

        Assert.Equal("anthropic", _service.Provider.Default);
        Assert.Equal("claude-3", _service.Provider.DefaultModel);
        Assert.Equal("high", _service.Provider.ThinkingLevel);
    }

    [Fact]
    public async Task SaveAll_PersistsConfig()
    {
        _service.LoadAll();
        _service.Provider.Default = "google";
        _service.Provider.DefaultModel = "gemini-pro";

        await _service.SaveProviderAsync();

        var loaded = _configStore.Load();
        Assert.Equal("google", loaded.Providers.Default);
        Assert.Equal("gemini-pro", loaded.Providers.DefaultModel);
    }

    [Fact]
    public void DiscoveredPlugins_ScanPopulatesList()
    {
        _service.LoadAll();
        Assert.NotNull(_service.DiscoveredPlugins);
    }

    [Fact]
    public void IsPluginEnabled_ReturnsTrue_ByDefault()
    {
        _service.LoadAll();
        Assert.True(_service.IsPluginEnabled("any-plugin-id"));
    }

    [Fact]
    public void ReloadPlugins_RepopulatesList()
    {
        _service.LoadAll();
        var count1 = _service.DiscoveredPlugins.Count;
        _service.ReloadPlugins();
        Assert.Equal(count1, _service.DiscoveredPlugins.Count);
    }
}
