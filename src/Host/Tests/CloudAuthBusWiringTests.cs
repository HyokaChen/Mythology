using System.Reflection;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Tui.Services;
using Xunit;

namespace Host.Tests;

/// <summary>
/// §13.6 总线单例集成测试：Host RegisterStores 注册后，客户端工厂
/// （HttpFictionHarnessClient / RetrievalRestClient）与 TUI 云端门面
/// （CloudConnectionService——TuiApp 经 InitializeAsync 元组收到的实例）解析到**同一**
/// CloudAuthInvalidationBus 实例。这是事件驱动失效检测闭环的关键：
/// 真实 API 调用 401/403 的吊销事件必须能到达认证门面。
/// </summary>
public sealed class CloudAuthBusWiringTests
{
    [Fact]
    public void RegisterStores_AllResolveTheSameBusSingleton()
    {
        var services = new ServiceCollection();
        var method = typeof(HostBootstrapper).GetMethod(
            "RegisterStores",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);
        var mythologyDir = Path.GetTempPath() + Path.GetRandomFileName();
        method!.Invoke(
            null,
            [
                services,
                NovelWorkspace.Single("novels_test"),
                mythologyDir,
                new CloudSessionStore(mythologyDir),
                new CloudAuthInvalidationBus(),
            ]
        );

        using var provider = services.BuildServiceProvider();

        // 总线单例：重复解析同一实例
        var bus = provider.GetRequiredService<CloudAuthInvalidationBus>();
        Assert.Same(bus, provider.GetRequiredService<CloudAuthInvalidationBus>());

        // 客户端工厂解析（触发工厂构造，内部经 sp.GetRequiredService 注入同一总线）
        var harness = provider.GetRequiredService<IFictionHarnessClient>();
        var retrieval = provider.GetRequiredService<RetrievalRestClient>();
        var usage = provider.GetRequiredService<UsageRestClient>();

        // TUI 云端门面（TuiApp 收到的实例）
        var connection = provider.GetRequiredService<CloudConnectionService>();

        // 工厂注入的总线 == 门面持有的总线 == 容器单例
        Assert.Same(bus, ReadBus(harness));
        Assert.Same(bus, ReadBus(retrieval));
        Assert.Same(bus, ReadBus(usage));
        Assert.Same(bus, ReadBus(connection));

        // 客户端与门面均为单例（重复解析同一实例）
        Assert.Same(harness, provider.GetRequiredService<IFictionHarnessClient>());
        Assert.Same(retrieval, provider.GetRequiredService<RetrievalRestClient>());
        Assert.Same(usage, provider.GetRequiredService<UsageRestClient>());
        Assert.Same(connection, provider.GetRequiredService<CloudConnectionService>());

        // 能力目录缓存：探活门面注入的 == 容器单例
        // （断链则探活不写、调度门禁永远 fail-open —— 静默失效，必须有锁）
        Assert.Same(
            provider.GetRequiredService<CloudCapabilityCatalogStore>(),
            ReadCapabilityCatalog(connection)
        );
    }

    /// <summary>读取门面注入的私有能力目录缓存（验证 探活 → 诊断调度 接线用）。</summary>
    private static CloudCapabilityCatalogStore ReadCapabilityCatalog(object instance)
    {
        var field = instance
            .GetType()
            .GetField("_capabilityCatalog", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<CloudCapabilityCatalogStore>(field!.GetValue(instance));
    }

    /// <summary>
    /// 读取客户端/门面注入的私有总线（验证接线用）：云端客户端的总线由
    /// CloudRestTransport 组合持有 → 先下钻 _transport；门面（CloudConnectionService）
    /// 直接持有 _invalidationBus 字段。
    /// </summary>
    private static CloudAuthInvalidationBus ReadBus(object instance)
    {
        var type = instance.GetType();
        var transportField = type.GetField(
            "_transport",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        if (transportField is not null)
        {
            instance = transportField.GetValue(instance)!;
            type = instance.GetType();
        }

        var field = type.GetField(
            "_invalidationBus",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(field);
        return Assert.IsType<CloudAuthInvalidationBus>(field!.GetValue(instance));
    }
}
