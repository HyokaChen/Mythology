using System.Xml.Linq;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设置页模型发现的状态机契约（连接器 / 嵌入共用 DiscoverAsync 实现）：
/// 自定义提供商不走发现（取首行会错配 BaseUrl）→ 提示手动输入；
/// 未填该提供商密钥 → 提示先填密钥。两分支均为纯本地判定，不触网。
/// </summary>
public sealed class SettingsModelDiscoveryTests
{
    [Fact]
    public async Task Embedding_CustomProviderWithoutKey_AsksForKey()
    {
        // 自定义连接器同样支持发现（拼 /v1/models）；只是没有密钥时先要求填密钥
        var vm = new SettingsViewModel { EmbeddingProviderDisplay = "自定义" };

        await vm.DiscoverEmbeddingModelsAsync();

        Assert.Equal("请先在下方为该提供商填写 API Key", vm.EmbeddingModelDiscoverStatus);
        Assert.False(vm.IsEmbeddingModelListVisible);
        Assert.False(vm.IsDiscoveringEmbeddingModels);
    }

    [Fact]
    public async Task Embedding_NoApiKeyForProvider_HintsMissingKey()
    {
        var vm = new SettingsViewModel { EmbeddingProviderDisplay = "OpenAI" };

        await vm.DiscoverEmbeddingModelsAsync();

        Assert.Equal("请先在下方为该提供商填写 API Key", vm.EmbeddingModelDiscoverStatus);
        Assert.False(vm.IsEmbeddingModelListVisible);
    }

    [Fact]
    public async Task Chat_CustomProviderWithKeyButNoBaseUrl_AsksForBaseUrl()
    {
        var vm = new SettingsViewModel { ProviderDisplay = "自定义" };
        vm.ApiKeys.Add(new ApiKeyRow("custom", "", "sk-test", ""));

        await vm.DiscoverModelsAsync();

        Assert.Equal("自定义提供商需先填写 Base URL 才能发现模型", vm.ModelDiscoverStatus);
        Assert.False(vm.IsModelListVisible);
    }

    [Fact]
    public void ChatProviderChange_HidesDiscoveredListAndKeepsManualFallback()
    {
        var vm = new SettingsViewModel { Model = "old-model", IsModelListVisible = true };
        vm.ModelOptions.Add("old-model");

        vm.ProviderDisplay = "DeepSeek";

        Assert.False(vm.IsModelListVisible);
        Assert.Equal("old-model", vm.Model);
        Assert.Contains("重新发现", vm.ModelDiscoverStatus);
    }

    [Fact]
    public void EmbeddingProviderChange_HidesDiscoveredListAndKeepsManualFallback()
    {
        var vm = new SettingsViewModel
        {
            EmbeddingModel = "old-embedding",
            IsEmbeddingModelListVisible = true,
        };
        vm.EmbeddingModelOptions.Add("old-embedding");

        vm.EmbeddingProviderDisplay = "DeepSeek";

        Assert.False(vm.IsEmbeddingModelListVisible);
        Assert.Equal("old-embedding", vm.EmbeddingModel);
        Assert.Contains("重新发现", vm.EmbeddingModelDiscoverStatus);
    }

    [Fact]
    public void SettingsView_ModelDropdowns_BindSelectedItemTwoWay()
    {
        var bindings = LoadSettingsView()
            .Descendants()
            .Where(e => e.Name.LocalName == "ComboBox")
            .Select(e => e.Attribute("SelectedItem")?.Value)
            .OfType<string>()
            .ToList();

        Assert.Contains("{Binding Model, Mode=TwoWay}", bindings);
        Assert.Contains("{Binding EmbeddingModel, Mode=TwoWay}", bindings);
    }

    private static XElement LoadSettingsView()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src",
                "Desktop",
                "Views",
                "SettingsView.axaml"
            );
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("未找到 src/Desktop/Views/SettingsView.axaml");
    }
}
