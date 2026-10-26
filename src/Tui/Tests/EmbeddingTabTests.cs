using Core.Data;
using Tui.Controls;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// Embedding 设置保存契约：保存值必须与下拉可见选择一致。
/// 回归：模型列表自动选中首项后，_model 仍为空 → 保存提示「embedding 模型名为空」。
/// </summary>
public sealed class EmbeddingTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "myth-embedding-tab-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 测试清理尽力而为
        }
    }

    [Fact]
    public void BuildEmbeddingConfig_AutoSelectedFirstDiscoveredModel_IsSaved()
    {
        var tab = new EmbeddingTab(CreateService());
        tab.Build();
        tab.ApplyDiscoveredModels(["text-embedding-3-small", "text-embedding-3-large"]);

        var config = tab.BuildEmbeddingConfig();

        Assert.Equal("text-embedding-3-small", config.Model);
        Assert.Equal("text-embedding-3-small", config.DefaultModel);
    }

    [Fact]
    public void BuildEmbeddingConfig_PreservesSavedModel_WhenDiscovered()
    {
        var service = CreateService();
        service.Embedding = new EmbeddingConfig { Model = "embed-b" };
        var tab = new EmbeddingTab(service);
        tab.Build();
        tab.ApplyDiscoveredModels(["embed-a", "embed-b"]);

        var config = tab.BuildEmbeddingConfig();

        Assert.Equal("embed-b", config.Model);
    }

    [Fact]
    public void BuildEmbeddingConfig_ManualFallback_PreservesModel()
    {
        var service = CreateService();
        service.Embedding = new EmbeddingConfig { Model = "manual-embed" };
        var tab = new EmbeddingTab(service);
        tab.Build();

        var config = tab.BuildEmbeddingConfig();

        Assert.Equal("manual-embed", config.Model);
    }

    [Fact]
    public void ApplyDiscoveredModels_UnavailableSavedModel_SavesVisibleFirstModel()
    {
        var service = CreateService();
        service.Embedding = new EmbeddingConfig { Model = "old-provider-embed" };
        var tab = new EmbeddingTab(service);
        tab.Build();
        tab.ApplyDiscoveredModels(["new-provider-embed-a", "new-provider-embed-b"]);

        var config = tab.BuildEmbeddingConfig();

        Assert.Equal("new-provider-embed-a", config.Model);
    }

    private SettingsService CreateService() => new(new ConfigStore(_dir));
}
