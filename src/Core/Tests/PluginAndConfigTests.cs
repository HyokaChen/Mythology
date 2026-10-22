using Core.Data;
using Core.Plugins;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Plugin 系统测试：PluginManifest、PluginPermissions、PluginTrustLevel、ConfigStore。
/// </summary>
public class PluginAndConfigTests : IAsyncDisposable
{
    private readonly string _tempDir;

    public PluginAndConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"plugin-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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

    /// <summary>
    /// 面板列宽（桌面端 ui.outline-panel-width）：往返保持、越界归一、缺省回落，且不破坏其它节。
    /// </summary>
    [Fact]
    public async Task ConfigStore_UiOutlinePanelWidth_RoundTripsAndClamps()
    {
        var store = new ConfigStore(_tempDir);

        // 缺省：320（旧 config.yaml 无 ui 节也不报错）
        Assert.Equal(320, store.Load().Ui.OutlinePanelWidth);

        var config = store.Load();
        config.Providers.Default = "custom:商汤";
        config.Ui.OutlinePanelWidth = 480;
        await store.SaveAsync(config);

        var reloaded = store.Load();
        Assert.Equal(480, reloaded.Ui.OutlinePanelWidth);
        Assert.Equal("custom:商汤", reloaded.Providers.Default); // 其它节未被覆盖

        // 越界值归一（1000 → 720；100 → 280）
        Assert.Equal(720, UiConfig.ClampWidth(1000));
        Assert.Equal(280, UiConfig.ClampWidth(100));
        Assert.Equal(320, UiConfig.ClampWidth(double.NaN));
    }

    // ══════════════════════════════════════════════════════
    //  PluginManifest
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Manifest_Defaults_AreCorrect()
    {
        var m = new PluginManifest();
        Assert.Equal("", m.Name);
        Assert.Equal("", m.Id);
        Assert.Equal("1.0.0", m.Version);
        Assert.Empty(m.Permissions);
    }

    [Fact]
    public void Manifest_SaveAndLoad_Roundtrip()
    {
        var subDir = Path.Combine(_tempDir, "test-plugin");
        var manifest = new PluginManifest
        {
            Name = "测试插件",
            Id = "test-plugin",
            Version = "2.1.0",
            Description = "一个测试插件",
            Author = "Test Author",
            Permissions = ["novel:read", "config:read"],
        };

        manifest.SaveToDirectory(subDir);

        var loaded = PluginManifest.LoadFromDirectory(subDir);
        Assert.NotNull(loaded);
        Assert.Equal("测试插件", loaded!.Name);
        Assert.Equal("test-plugin", loaded.Id);
        Assert.Equal("2.1.0", loaded.Version);
        Assert.Equal("Test Author", loaded.Author);
        Assert.Equal(["novel:read", "config:read"], loaded.Permissions);
    }

    [Fact]
    public void Manifest_LoadFromDirectory_NoJsonFile_ReturnsNull()
    {
        var result = PluginManifest.LoadFromDirectory(_tempDir);
        Assert.Null(result);
    }

    [Fact]
    public void Manifest_LoadFromDirectory_InvalidJson_ReturnsNull()
    {
        var subDir = Path.Combine(_tempDir, "bad-plugin");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "plugin.json"), "not valid json {{{");

        var result = PluginManifest.LoadFromDirectory(subDir);
        Assert.Null(result);
    }

    [Fact]
    public void Manifest_SetsOptionalFields()
    {
        var m = new PluginManifest { Homepage = "https://example.com", MinCoreVersion = "2.0.0" };
        Assert.Equal("https://example.com", m.Homepage);
        Assert.Equal("2.0.0", m.MinCoreVersion);
    }

    // ══════════════════════════════════════════════════════
    //  PluginPermissions
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Permissions_Constants_AreDefined()
    {
        Assert.Equal("novel:read", PluginPermissions.NovelRead);
        Assert.Equal("novel:write", PluginPermissions.NovelWrite);
        Assert.Equal("vector:read", PluginPermissions.VectorRead);
        Assert.Equal("vector:write", PluginPermissions.VectorWrite);
        Assert.Equal("config:read", PluginPermissions.ConfigRead);
        Assert.Equal("config:write", PluginPermissions.ConfigWrite);
        Assert.Equal("fs:access", PluginPermissions.FileSystem);
        Assert.Equal("net:access", PluginPermissions.Network);
        Assert.Equal("process:execute", PluginPermissions.Process);
        Assert.Equal("ui:interact", PluginPermissions.UserInteract);
    }

    [Fact]
    public void ExternalAllowed_ContainsLowRiskPermissions()
    {
        Assert.Contains(PluginPermissions.NovelRead, PluginPermissions.ExternalAllowed);
        Assert.Contains(PluginPermissions.ConfigRead, PluginPermissions.ExternalAllowed);
        Assert.Contains(PluginPermissions.Network, PluginPermissions.ExternalAllowed);
        Assert.Contains(PluginPermissions.UserInteract, PluginPermissions.ExternalAllowed);
        Assert.Equal(4, PluginPermissions.ExternalAllowed.Count);
    }

    [Fact]
    public void ExternalAllowed_ExcludesHighRiskPermissions()
    {
        Assert.DoesNotContain(PluginPermissions.NovelWrite, PluginPermissions.ExternalAllowed);
        Assert.DoesNotContain(PluginPermissions.VectorWrite, PluginPermissions.ExternalAllowed);
        Assert.DoesNotContain(PluginPermissions.ConfigWrite, PluginPermissions.ExternalAllowed);
        Assert.DoesNotContain(PluginPermissions.FileSystem, PluginPermissions.ExternalAllowed);
        Assert.DoesNotContain(PluginPermissions.Process, PluginPermissions.ExternalAllowed);
        // 会话转录是私有模型状态（完整对话 + 工具输出 + 私有笔记）——不得对外部插件开放
        Assert.DoesNotContain(PluginPermissions.SessionRead, PluginPermissions.ExternalAllowed);
        Assert.DoesNotContain(PluginPermissions.SessionWrite, PluginPermissions.ExternalAllowed);
    }

    // ══════════════════════════════════════════════════════
    //  PluginTrustLevel
    // ══════════════════════════════════════════════════════

    [Fact]
    public void TrustLevel_EnumValues_HaveCorrectSemantics()
    {
        // Builtin > Verified > External
        var values = Enum.GetValues<PluginTrustLevel>();
        Assert.Equal(3, values.Length);
        Assert.Contains(PluginTrustLevel.Builtin, values);
        Assert.Contains(PluginTrustLevel.Verified, values);
        Assert.Contains(PluginTrustLevel.External, values);
    }

    // ══════════════════════════════════════════════════════
    //  PluginPermissionAttribute
    // ══════════════════════════════════════════════════════

    [Fact]
    public void PermissionAttribute_StoresPermission()
    {
        var attr = new PluginPermissionAttribute("novel:read");
        Assert.Equal("novel:read", attr.Permission);
    }

    [Fact]
    public void PermissionAttribute_AllowsMultiple()
    {
        // [PluginPermission("novel:read")]
        // [PluginPermission("config:read")]
        var attr1 = new PluginPermissionAttribute("novel:read");
        var attr2 = new PluginPermissionAttribute("config:read");
        Assert.NotEqual(attr1.Permission, attr2.Permission);
    }

    // ══════════════════════════════════════════════════════
    //  PluginCapabilities
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ResolveAllowedServices_EmptyPermissions_ReturnsBaseline()
    {
        var services = PluginCapabilities.ResolveAllowedServices([]);
        Assert.Single(services); // baseline: ILogger
    }

    [Fact]
    public void ResolveAllowedServices_NovelRead_ContainsPluginStore()
    {
        var services = PluginCapabilities.ResolveAllowedServices(["novel:read"]);
        Assert.Equal(2, services.Count); // IPluginStore + baseline ILogger
        Assert.Contains(typeof(IPluginStore), services);
    }

    [Fact]
    public void ResolveAllowedServices_ConfigRead_ContainsConfiguration()
    {
        var services = PluginCapabilities.ResolveAllowedServices(["config:read"]);
        Assert.Equal(2, services.Count); // IConfiguration + baseline ILogger
        Assert.Contains(typeof(Microsoft.Extensions.Configuration.IConfiguration), services);
    }

    // ══════════════════════════════════════════════════════
    //  ConfigStore
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ConfigStore_Constructor_CreatesDirectory()
    {
        var configDir = Path.Combine(_tempDir, ".mythology");
        var _ = new ConfigStore(configDir);
        Assert.True(Directory.Exists(configDir));
    }

    [Fact]
    public void ConfigStore_Load_NoFile_ReturnsDefaults()
    {
        var configDir = Path.Combine(_tempDir, "empty-config-dir");
        Directory.CreateDirectory(configDir);
        var store = new ConfigStore(configDir);

        var config = store.Load();
        Assert.NotNull(config);
        Assert.Equal("custom", config.Providers.Default);
    }

    [Fact]
    public async Task ConfigStore_SaveAndLoad_Roundtrip()
    {
        var configDir = Path.Combine(_tempDir, "roundtrip-config");
        var store = new ConfigStore(configDir);

        var original = store.Load();
        original.Providers.Default = "openai";
        original.Providers.DefaultModel = "gpt-4o";

        await store.SaveAsync(original);

        var loaded = store.Load();
        Assert.Equal("openai", loaded.Providers.Default);
        Assert.Equal("gpt-4o", loaded.Providers.DefaultModel);
    }

    [Fact]
    public async Task ConfigStore_SaveAndLoad_ApiKeys()
    {
        var configDir = Path.Combine(_tempDir, "apikey-config");
        var store = new ConfigStore(configDir);

        var config = store.Load();
        config.Providers.ApiKeys.Add(
            new ApiKeyEntry
            {
                Provider = "openai",
                Name = "default",
                ApiKey = "sk-test123",
            }
        );

        await store.SaveAsync(config);

        var loaded = store.Load();
        Assert.Single(loaded.Providers.ApiKeys);
        Assert.Equal("openai", loaded.Providers.ApiKeys[0].Provider);
    }

    [Fact]
    public async Task ConfigStore_SaveAndLoad_ThinkingLevel()
    {
        var configDir = Path.Combine(_tempDir, "think-config");
        var store = new ConfigStore(configDir);

        var config = store.Load();
        config.Providers.ThinkingLevel = "high";

        await store.SaveAsync(config);

        var loaded = store.Load();
        Assert.Equal("high", loaded.Providers.ThinkingLevel);
    }

    // ══════════════════════════════════════════════════════
    //  ProviderDefinition
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ProviderDefinition_All_ContainsMajorProviders()
    {
        var all = ProviderDefinition.All;
        Assert.NotEmpty(all);
        Assert.Contains(all, d => d.ConfigKey == "openai");
        Assert.Contains(all, d => d.ConfigKey == "anthropic");
        Assert.Contains(all, d => d.ConfigKey == "google");
    }

    [Fact]
    public void ProviderDefinition_Custom_HasNoLlmProvider()
    {
        var custom = ProviderDefinition.Custom;
        Assert.Equal("custom", custom.ConfigKey);
        Assert.Null(custom.LlmProvider);
    }

    [Fact]
    public void ProviderDefinition_FindByConfigKey_AllKnownKeys()
    {
        var knownKeys = new[] { "openai", "anthropic", "google", "deepseek", "groq", "openrouter" };
        foreach (var key in knownKeys)
        {
            var def = ProviderDefinition.FindByConfigKey(key);
            Assert.NotNull(def);
            var isCustom = def!.ConfigKey == "custom";
            if (!isCustom)
                Assert.Equal(key, def.ConfigKey);
        }
    }

    [Fact]
    public void ProviderDefinition_IsCustom_DetectsCustom()
    {
        Assert.True(ProviderDefinition.IsCustom("custom"));
        Assert.True(ProviderDefinition.IsCustom("custom:商汤"));
        Assert.False(ProviderDefinition.IsCustom("openai"));
    }

    [Fact]
    public void ProviderDefinition_GetCustomName_ExtractsName()
    {
        Assert.Equal("商汤", ProviderDefinition.GetCustomName("custom:商汤"));
        Assert.Equal("my-llm", ProviderDefinition.GetCustomName("custom:my-llm"));
        Assert.Null(ProviderDefinition.GetCustomName("custom"));
        Assert.Null(ProviderDefinition.GetCustomName("openai"));
        // 刻意传 null：验证 GetCustomName 对 null 输入容忍（string.IsNullOrEmpty 短路返回 null）
        Assert.Null(ProviderDefinition.GetCustomName(null!));
    }

    // ══════════════════════════════════════════════════════
    //  ApiKeyEntry
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ApiKeyEntry_Defaults()
    {
        var entry = new ApiKeyEntry();
        Assert.Equal("", entry.Provider);
        Assert.Null(entry.Name);
        Assert.Null(entry.ApiKey);
        Assert.Null(entry.BaseUrl);
    }
}
