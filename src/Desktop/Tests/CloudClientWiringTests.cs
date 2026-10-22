using System.Reflection;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Desktop;
using Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// §13.6 总线单例集成测试（与 Host.Tests CloudAuthBusWiringTests 同款）：
/// Program.RegisterCloudClients 注册后，客户端工厂（HttpFictionHarnessClient /
/// RetrievalRestClient）与认证门面（CloudAuthService）解析到**同一**
/// CloudAuthInvalidationBus 实例。这是 Desktop 进程内事件驱动失效检测闭环的关键：
/// 插件云端调用（检索 REST / Harness）401/403 的吊销事件必须能到达认证门面。
/// </summary>
public sealed class CloudClientWiringTests
{
    [Fact]
    public void RegisterCloudClients_AllResolveTheSameBusSingleton()
    {
        var services = new ServiceCollection();

        // RegisterCloudClients 依赖的最小注册（与 ConfigureServices 相同语义；
        // 临时目录存储，避免触碰真实 ~/.mythology）
        using var store = new TestStoreDir();
        services.AddSingleton(store.Store);
        services.AddSingleton<CloudAuthInvalidationBus>();
        services.AddSingleton<CloudAuthService>();
        services.AddSingleton<CloudCapabilityCatalogStore>(); // 与 ConfigureServices 相同语义（探活 → 调度共享缓存）

        // 临时目录作为 novelsRoot / mythologyDir（TestStoreDir 同目录策略，不触碰真实 ~/.mythology）
        Program.RegisterCloudClients(
            services,
            store.Dir,
            NovelWorkspace.Single(Path.Combine(store.Dir, "novels"))
        );

        using var provider = services.BuildServiceProvider();

        // 总线单例：重复解析同一实例
        var bus = provider.GetRequiredService<CloudAuthInvalidationBus>();
        Assert.Same(bus, provider.GetRequiredService<CloudAuthInvalidationBus>());

        // 客户端工厂解析（触发工厂构造，内部经 sp.GetRequiredService 注入同一总线）
        var harness = provider.GetRequiredService<IFictionHarnessClient>();
        var retrieval = provider.GetRequiredService<RetrievalRestClient>();
        var usage = provider.GetRequiredService<UsageRestClient>();

        // 工厂注入的总线 == 认证门面（CloudAuthService）订阅的总线 == 容器单例
        Assert.Same(bus, ReadBus(harness));
        Assert.Same(bus, ReadBus(retrieval));
        Assert.Same(bus, ReadBus(usage));
        Assert.Same(bus, ReadBus(provider.GetRequiredService<CloudAuthService>()));

        // 客户端与门面均为单例（重复解析同一实例）
        Assert.Same(harness, provider.GetRequiredService<IFictionHarnessClient>());
        Assert.Same(retrieval, provider.GetRequiredService<RetrievalRestClient>());
        Assert.Same(usage, provider.GetRequiredService<UsageRestClient>());
        Assert.Same(
            provider.GetRequiredService<CloudAuthService>(),
            provider.GetRequiredService<CloudAuthService>()
        );

        // 能力目录缓存：探活门面注入的 == 容器单例
        // （断链则探活不写、调度门禁永远 fail-open —— 静默失效，必须有锁）
        Assert.Same(
            provider.GetRequiredService<CloudCapabilityCatalogStore>(),
            ReadCapabilityCatalog(provider.GetRequiredService<CloudAuthService>())
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
    /// CloudRestTransport 组合持有 → 先下钻 _transport；门面（CloudAuthService）
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

    /// <summary>临时凭据目录（测试结束自动清理），避免触碰真实 ~/.mythology。</summary>
    private sealed class TestStoreDir : IDisposable
    {
        public TestStoreDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"myth-cloudwiring-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Dir);
            Store = new CloudSessionStore(Dir);
        }

        public string Dir { get; }
        public CloudSessionStore Store { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Dir))
                    Directory.Delete(Dir, true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }
}
