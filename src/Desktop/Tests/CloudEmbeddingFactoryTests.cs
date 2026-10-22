using System.Reflection;
using ClientPlatform;
using Core.Data;
using Core.Vector;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 云端 embedding 工厂接线（Desktop 路径）：config.yaml Embedding.Provider = "cloud" 时
/// CreateCloudEmbeddingFactory 返回 CloudEmbeddingClient 工厂；未配置时返回 null
/// （Core 按本地 LlmTornado/Dummy 选择，不发任何云端 embedding 请求）。
/// </summary>
public sealed class CloudEmbeddingFactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"myth-desktop-emb-{Guid.NewGuid():N}"
    );

    public CloudEmbeddingFactoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static Func<IServiceProvider, IEmbeddingService>? CreateFactory(string dir) =>
        (Func<IServiceProvider, IEmbeddingService>?)
            typeof(Desktop.Program)
                .GetMethod(
                    "CreateCloudEmbeddingFactory",
                    BindingFlags.NonPublic | BindingFlags.Static
                )!
                .Invoke(null, [dir, new CloudSessionStore(dir), new CloudAuthInvalidationBus()]);

    [Fact]
    public void NoConfig_ReturnsNull_KeepingLocalSelection()
    {
        Assert.Null(CreateFactory(_dir));
    }

    [Fact]
    public async Task ProviderCloud_ReturnsFactoryThatBuildsCloudEmbeddingClient()
    {
        await new ConfigStore(_dir).SaveAsync(
            new ConfigData { Embedding = new EmbeddingConfig { Provider = "cloud" } }
        );

        var factory = CreateFactory(_dir);

        Assert.NotNull(factory);
        var service = factory!(null!); // Core 侧以 null! 调用（闭包式工厂，不解析 DI）
        var client = Assert.IsType<CloudEmbeddingClient>(service);
        Assert.Equal(VectorDefaults.Dimension, client.Dimension);
    }

    [Fact]
    public async Task OtherProvider_ReturnsNull()
    {
        await new ConfigStore(_dir).SaveAsync(
            new ConfigData { Embedding = new EmbeddingConfig { Provider = "local" } }
        );

        Assert.Null(CreateFactory(_dir));
    }
}
