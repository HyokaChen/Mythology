using Core.Data;
using Core.Mcp;
using Desktop.ViewModels;
using ReactiveUI.Primitives;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设置页提供商下拉与 config.yaml 的往返契约：
/// 具名自定义提供商（"custom:krb"）必须显示为「自定义: krb」，保存时原样写回复合键
/// ——LlmApiFactory 按 Provider=="custom" 且 Name 匹配查找密钥，写成裸 "custom" 会让 API 静默为空。
/// </summary>
public sealed class SettingsProviderRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "myth-settings-" + Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task NamedCustomProvider_DisplaysNameAndPreservesCompositeKeyOnSave()
    {
        var store = new ConfigStore(_dir);
        await store.SaveAsync(
            new ConfigData
            {
                Providers = new ProviderConfig
                {
                    Default = "custom:krb",
                    DefaultModel = "DeepSeek-V4-Flash",
                    ThinkingLevel = "medium",
                    ContextWindowSize = 200_000,
                    ApiKeys =
                    [
                        new ApiKeyEntry
                        {
                            Provider = "custom",
                            Name = "商汤",
                            ApiKey = "k1",
                            BaseUrl = "https://a/v1",
                        },
                        new ApiKeyEntry
                        {
                            Provider = "custom",
                            Name = "krb",
                            ApiKey = "k2",
                            BaseUrl = "https://b/v1",
                            Protocol = "anthropic-message",
                        },
                    ],
                },
                Embedding = new EmbeddingConfig
                {
                    Default = "custom:krb",
                    DefaultModel = "wemm-embedding",
                    // 云端 opt-in 字段：UI 面板不编辑，保存管线必须原样保留（防被清掉回归）
                    Provider = "cloud",
                    ApiKeys =
                    [
                        new ApiKeyEntry
                        {
                            Provider = "custom",
                            Name = "krb",
                            ApiKey = "k3",
                            BaseUrl = "http://c/v1",
                        },
                    ],
                },
            }
        );

        var vm = new SettingsViewModel(store, null, null).Refresh();

        Assert.Equal("自定义: krb", vm.ProviderDisplay);
        Assert.Contains("自定义: 商汤", vm.ProviderOptions);
        Assert.Contains("自定义: krb", vm.ProviderOptions);
        Assert.Equal("DeepSeek-V4-Flash", vm.Model);
        Assert.Equal("200K", vm.ContextWindow);
        Assert.Equal("自定义: krb", vm.EmbeddingProviderDisplay);
        Assert.Equal("wemm-embedding", vm.EmbeddingModel);

        // 改动可观察的字段，确保落盘确实发生
        vm.ContextWindow = "128K";
        vm.ThinkingLevel = "xhigh";
        vm.SaveCommand.Execute().Subscribe();

        var saved = await WaitForAsync(store, c => c.Providers.ContextWindowSize == 131_072);

        Assert.Equal("custom:krb", saved.Providers.Default);
        Assert.Equal("custom:krb", saved.Embedding.Default);
        Assert.Equal("xhigh", saved.Providers.ThinkingLevel);
        Assert.Equal("wemm-embedding", saved.Embedding.DefaultModel);
        // 云端 embedding opt-in 字段穿越 UI 保存管线保留（面板不编辑但不得丢失）
        Assert.Equal("cloud", saved.Embedding.Provider);
        Assert.Equal(["商汤", "krb"], saved.Providers.ApiKeys.Select(k => k.Name));
        Assert.Equal("k2", saved.Providers.ApiKeys[1].ApiKey);
        // 协议随密钥一起往返（下拉改过就要写回，不能被重置回默认）
        Assert.Equal("anthropic-message", saved.Providers.ApiKeys[1].Protocol);
        Assert.Equal("openai-completion", saved.Providers.ApiKeys[0].Protocol);
    }

    [Fact]
    public async Task SelectingPreset_ReplacesCompositeKey()
    {
        var store = new ConfigStore(_dir);
        await store.SaveAsync(
            new ConfigData
            {
                Providers = new ProviderConfig
                {
                    Default = "custom:krb",
                    DefaultModel = "m",
                    ApiKeys =
                    [
                        new ApiKeyEntry
                        {
                            Provider = "custom",
                            Name = "krb",
                            ApiKey = "k2",
                            BaseUrl = "https://b/v1",
                            Protocol = "anthropic-message",
                        },
                    ],
                },
            }
        );

        var vm = new SettingsViewModel(store, null, null).Refresh();
        vm.ProviderDisplay = "DeepSeek";
        vm.Model = "deepseek-chat";
        vm.SaveCommand.Execute().Subscribe();

        var saved = await WaitForAsync(store, c => c.Providers.Default == "deepseek");

        Assert.Equal("deepseek", saved.Providers.Default);
        Assert.Equal("deepseek-chat", saved.Providers.DefaultModel);
    }

    [Fact]
    public async Task McpAuth_ResolvesTransportHeaderAndPersists()
    {
        var store = new ConfigStore(_dir);
        await store.SaveAsync(
            new ConfigData
            {
                Mcp = new McpConfig
                {
                    Servers =
                    [
                        new McpServerEntry
                        {
                            Name = "remote",
                            Transport = "streamable-http",
                            Url = "https://mcp.example.com/v1",
                            Auth = "bearer",
                            BearerToken = "tok-123",
                            Enabled = true,
                        },
                        new McpServerEntry
                        {
                            Name = "custom-header",
                            Transport = "sse",
                            Url = "https://mcp.example.com/sse",
                            Auth = "header",
                            AuthHeader = "X-API-Key",
                            AuthValue = "secret",
                            Enabled = true,
                        },
                        new McpServerEntry
                        {
                            Name = "local",
                            Transport = "stdio",
                            Command = "npx",
                        },
                    ],
                },
            }
        );

        var vm = new SettingsViewModel(store, null, null).Refresh();

        Assert.Equal("remote", vm.McpServers[0].TitleLine);
        Assert.True(vm.McpServers[0].IsBearerAuth);
        Assert.Contains("bearer", vm.McpServers[0].AuthLine);
        Assert.NotEqual("none", vm.McpServers[1].Auth);
        Assert.True(vm.McpServers[1].IsHeaderAuth);

        // 传输层解析：bearer → Authorization: Bearer <token>；自定义头 → 原样头名
        var bearer = HttpTransport.ResolveAuth(
            new McpServerEntry { Auth = "bearer", BearerToken = "tok-123" }
        );
        Assert.Equal("Authorization", bearer.Header);
        Assert.Equal("Bearer tok-123", bearer.Value);

        var header = HttpTransport.ResolveAuth(
            new McpServerEntry
            {
                Auth = "header",
                AuthHeader = "X-API-Key",
                AuthValue = "secret",
            }
        );
        Assert.Equal("X-API-Key", header.Header);
        Assert.Equal("secret", header.Value);

        var none = HttpTransport.ResolveAuth(new McpServerEntry());
        Assert.Null(none.Header);

        // 保存后鉴权字段不被清空
        vm.McpServers[0].Auth = "header";
        vm.McpServers[0].AuthHeader = "X-Token";
        vm.McpServers[0].AuthValue = "v2";
        vm.SaveCommand.Execute().Subscribe();

        var saved = await WaitForAsync(
            store,
            c => c.Mcp.Servers.Count > 0 && c.Mcp.Servers[0].AuthHeader == "X-Token"
        );
        Assert.Equal("X-Token", saved.Mcp.Servers[0].AuthHeader);
        Assert.Equal("v2", saved.Mcp.Servers[0].AuthValue);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
        catch
        {
            // 临时目录清理失败不影响断言结果
        }
    }

    /// <summary>
    /// 上下文管理模式（compact | continuation）与 config.yaml 的往返：
    /// 打开设置页要回显磁盘模式；保存要写回所选模式（热切换未接线时也必须落盘，重启后生效）。
    /// </summary>
    [Fact]
    public async Task ContextMode_RoundTripsThroughConfigYaml()
    {
        var store = new ConfigStore(_dir);
        await store.SaveAsync(
            new ConfigData
            {
                ContextManagement = new ContextManagementConfig
                {
                    Mode = "continuation",
                    ReminderPercent = 55,
                    VectorRetrieval = true,
                },
            }
        );

        var vm = new SettingsViewModel(store, null, null).Refresh();

        Assert.Equal("接续（换页，不丢细节）", vm.ContextModeDisplay);
        Assert.True(vm.ContextVectorRetrieval);

        // 切回压缩：显示名 → 配置值 → 磁盘
        vm.ContextModeDisplay = "压缩（摘要，保留最近若干条）";
        vm.ContextVectorRetrieval = false;
        vm.SaveCommand.Execute().Subscribe();

        var saved = await WaitForAsync(store, c => c.ContextManagement.Mode == "compact");

        Assert.False(saved.ContextManagement.VectorRetrieval);
        Assert.Equal(55, saved.ContextManagement.ReminderPercent); // 未在 UI 暴露的参数不得被清掉
    }

    private async Task<ConfigData> WaitForAsync(ConfigStore store, Func<ConfigData, bool> done)
    {
        for (var i = 0; i < 100; i++)
        {
            var config = store.Load();
            if (done(config))
                return config;
            await Task.Delay(50);
        }
        Assert.Fail(
            "配置未在预期时间内落盘；文件内容:\n"
                + File.ReadAllText(Path.Combine(_dir, "config.yaml"))
        );
        return store.Load();
    }
}
