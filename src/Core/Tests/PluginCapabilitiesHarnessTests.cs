using Core.Cloud;
using Core.Data;
using Core.Plugins;
using Xunit;

namespace Core.Tests;

/// <summary>
/// PluginCapabilities 权限 → 服务映射测试（CHANGE_07 harness 接线）：
/// cloud:harness 权限必须放行 IFictionHarnessClient，且未声明权限的插件拿不到。
/// </summary>
public sealed class PluginCapabilitiesHarnessTests
{
    [Fact]
    public void ResolveAllowedServices_WithCloudHarness_IncludesHarnessClient()
    {
        var services = PluginCapabilities.ResolveAllowedServices([PluginPermissions.CloudHarness]);

        Assert.Contains(typeof(IFictionHarnessClient), services);
    }

    [Fact]
    public void ResolveAllowedServices_WithoutCloudHarness_ExcludesHarnessClient()
    {
        var services = PluginCapabilities.ResolveAllowedServices([PluginPermissions.NovelRead]);

        Assert.DoesNotContain(typeof(IFictionHarnessClient), services);
    }

    [Fact]
    public void ResolveAllowedServices_CloudHarnessCombined_KeepsOtherMappings()
    {
        var services = PluginCapabilities.ResolveAllowedServices([
            PluginPermissions.NovelRead,
            PluginPermissions.CloudHarness,
        ]);

        Assert.Contains(typeof(IPluginStore), services);
        Assert.Contains(typeof(IFictionHarnessClient), services);
    }

    [Fact]
    public void CloudHarness_IsNotInExternalAllowed()
    {
        // 云端 Harness 产生计量费用，External 插件不得默认获得。
        Assert.DoesNotContain(PluginPermissions.CloudHarness, PluginPermissions.ExternalAllowed);
    }
}
