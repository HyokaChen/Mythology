using Core.Data;
using Tui.Controls;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 连接器模型保存契约：模型发现自动选中首项后，隐藏的手动输入状态必须同步。
/// 回归：下拉先选中列表首项、后绑定 SelectionChanged，导致回退手动输入时显示旧模型。
/// </summary>
public sealed class ProviderTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "myth-provider-tab-" + Guid.NewGuid().ToString("N")
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
    public void SelectedModel_AutoSelectedFirstDiscoveredModel_PersistsAfterFallback()
    {
        var service = CreateService("openai", "manual-model");
        var tab = new ProviderTab(service);
        tab.Build();

        tab.ApplyDiscoveredModels(["gpt-a", "gpt-b"]);
        tab.ApplyDiscoveredModels([]); // 模拟发现失败 → 回退手动输入

        Assert.Equal("gpt-a", tab.SelectedModel);
    }

    [Fact]
    public void SelectedModel_PreservesConfiguredModel_WhenDiscoveredAndFallback()
    {
        var service = CreateService("openai", "gpt-b");
        var tab = new ProviderTab(service);
        tab.Build();

        tab.ApplyDiscoveredModels(["gpt-a", "gpt-b"]);
        tab.ApplyDiscoveredModels([]);

        Assert.Equal("gpt-b", tab.SelectedModel);
    }

    [Fact]
    public void SelectedModel_ManualFallbackWithoutDiscovery_PreservesModel()
    {
        var service = CreateService("openai", "manual-model");
        var tab = new ProviderTab(service);
        tab.Build();

        Assert.Equal("manual-model", tab.SelectedModel);
    }

    private SettingsService CreateService(string provider, string model)
    {
        var service = new SettingsService(new ConfigStore(_dir));
        service.Provider = new ProviderConfig
        {
            Default = provider,
            DefaultModel = model,
            ApiKeys = [],
        };
        return service;
    }
}
