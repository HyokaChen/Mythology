using System.Reflection;
using Core.Cloud;
using Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Host.Tests;

/// <summary>
/// Host DI 集成测试（CHANGE_07 harness 接线）：
/// 验证 RegisterStores 注册了 IFictionHarnessClient（HttpFictionHarnessClient 动态 key），
/// 且插件声明了 cloud:harness 权限（csproj PluginPermissions 元数据一致）。
/// </summary>
public sealed class HostHarnessRegistrationTests
{
    [Fact]
    public void RegisterStores_RegistersIFictionHarnessClient()
    {
        var services = new ServiceCollection();
        var method = typeof(HostBootstrapper).GetMethod(
            "RegisterStores",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        Assert.NotNull(method);

        method!.Invoke(
            null,
            [
                services,
                NovelWorkspace.Single("novels_test"),
                Path.GetTempPath() + Path.GetRandomFileName(),
                new ClientPlatform.CloudSessionStore(Path.GetTempPath() + Path.GetRandomFileName()),
                new ClientPlatform.CloudAuthInvalidationBus(),
            ]
        );

        var descriptor = Assert.Single(
            services,
            d => d.ServiceType == typeof(IFictionHarnessClient)
        );
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterStores_AlsoRegistersRetrievalRestClient()
    {
        var services = new ServiceCollection();
        var method = typeof(HostBootstrapper).GetMethod(
            "RegisterStores",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method!.Invoke(
            null,
            [
                services,
                NovelWorkspace.Single("novels_test"),
                Path.GetTempPath() + Path.GetRandomFileName(),
                new ClientPlatform.CloudSessionStore(Path.GetTempPath() + Path.GetRandomFileName()),
                new ClientPlatform.CloudAuthInvalidationBus(),
            ]
        );

        Assert.Contains(
            services,
            d => d.ServiceType.FullName == "ClientPlatform.RetrievalRestClient"
        );
    }

    [Fact]
    public void RegisterStores_HarnessIsSingleton_NotTransient()
    {
        var services = new ServiceCollection();
        var method = typeof(HostBootstrapper).GetMethod(
            "RegisterStores",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method!.Invoke(
            null,
            [
                services,
                NovelWorkspace.Single("novels_test"),
                Path.GetTempPath() + Path.GetRandomFileName(),
                new ClientPlatform.CloudSessionStore(Path.GetTempPath() + Path.GetRandomFileName()),
                new ClientPlatform.CloudAuthInvalidationBus(),
            ]
        );

        var descriptor = services.Single(d => d.ServiceType == typeof(IFictionHarnessClient));
        // Singleton：客户端实例与 API Key provider 复用；HTTP handler 由 IHttpClientFactory 池化（C4）
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterStores_RegistersHttpClientFactory()
    {
        var services = new ServiceCollection();
        var method = typeof(HostBootstrapper).GetMethod(
            "RegisterStores",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method!.Invoke(
            null,
            [
                services,
                NovelWorkspace.Single("novels_test"),
                Path.GetTempPath() + Path.GetRandomFileName(),
                new ClientPlatform.CloudSessionStore(Path.GetTempPath() + Path.GetRandomFileName()),
                new ClientPlatform.CloudAuthInvalidationBus(),
            ]
        );

        // C4 收口：云端客户端统一经 IHttpClientFactory 创建（handler 池化 + DNS 轮换）
        Assert.Contains(services, d => d.ServiceType == typeof(IHttpClientFactory));
    }

    [Fact]
    public void HarnessClientIsRegisteredViaFactory_WithDynamicKey()
    {
        var services = new ServiceCollection();
        var method = typeof(HostBootstrapper).GetMethod(
            "RegisterStores",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method!.Invoke(
            null,
            [
                services,
                NovelWorkspace.Single("novels_test"),
                Path.GetTempPath() + Path.GetRandomFileName(),
                new ClientPlatform.CloudSessionStore(Path.GetTempPath() + Path.GetRandomFileName()),
                new ClientPlatform.CloudAuthInvalidationBus(),
            ]
        );

        var descriptor = services.Single(d => d.ServiceType == typeof(IFictionHarnessClient));
        // 工厂注册：HttpFictionHarnessClient 用 Func<string?> apiKeyProvider 动态读取 cloud-session.json
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    [Fact]
    public void HarnessClient_ApiKeyProvider_ReadsSessionFileViaStoreDecryption()
    {
        // 回归（Windows DPAPI）：cloud-session.json 是加密密文（CloudSessionStore.Save 用
        // ProtectedData.Protect），key provider 必须经 Load() 解密，不能明文解析——否则本地快速失败
        // 误判"无 API Key"：云状态"已连接"却所有 Harness 调用被跳过。
        var dir = Path.Combine(Path.GetTempPath(), $"myth-key-{Guid.NewGuid():N}");
        try
        {
            var store = new ClientPlatform.CloudSessionStore(dir);
            store.Save(
                new ClientPlatform.CloudSession(
                    "mk_live_secret_key",
                    "pub-1",
                    "ws-1",
                    "free",
                    DateTimeOffset.UtcNow
                )
            );

            var services = new ServiceCollection();
            typeof(HostBootstrapper)
                .GetMethod("RegisterStores", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(
                    null,
                    [
                        services,
                        NovelWorkspace.Single("novels_test"),
                        dir,
                        new ClientPlatform.CloudSessionStore(dir),
                        new ClientPlatform.CloudAuthInvalidationBus(),
                    ]
                );

            using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IFictionHarnessClient>();
            var keyProvider =
                (Func<string?>)
                    typeof(ClientPlatform.HttpFictionHarnessClient)
                        .GetField(
                            "_apiKeyProvider",
                            BindingFlags.NonPublic | BindingFlags.Instance
                        )!
                        .GetValue(client)!;

            Assert.Equal("mk_live_secret_key", keyProvider());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
