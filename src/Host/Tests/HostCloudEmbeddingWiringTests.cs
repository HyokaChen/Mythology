using System.Reflection;
using Core.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Host.Tests;

/// <summary>
/// 云端 embedding 接线（Host/TUI 路径）：ConfigureRetrievalAsync 的 provider=cloud 分支
/// 必须让云端客户端优先于本地 LlmTornado/Dummy 被选中（与 Core 路径的 embeddingFactory 优先级对齐）。
/// </summary>
public sealed class HostCloudEmbeddingWiringTests
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimension => VectorDefaults.Dimension;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new float[VectorDefaults.Dimension]);
    }

    [Fact]
    public async Task ConfigureRetrievalAsync_CloudEmbedding_WinsOverLocalFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"myth-cloud-emb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var method = typeof(HostBootstrapper).GetMethod(
                "ConfigureRetrievalAsync",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.NotNull(method);

            var stub = new StubEmbedding();
            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().Build();

            var task = (Task)
                method!.Invoke(
                    null,
                    [
                        config,
                        services,
                        dir,
                        false, // useDebug
                        null, // tornadoApi
                        null, // embeddingApi
                        null, // savedEmbeddingModel（云端模式不依赖本地模型名）
                        stub, // cloudEmbedding
                        null, // systemOne（本用例只验证 embedding 优先级）
                        CancellationToken.None,
                    ]
                )!;
            await task;

            var tuple = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var embedding = (IEmbeddingService)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;

            // 云端客户端优先于全部本地分支（含 Dummy 回退）
            Assert.Same(stub, embedding);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 测试构造的 SqliteRetrievalStore/AgentMemoryStore 仍持有 vectors.db（Windows 文件锁）；
                // 临时目录交给系统回收，不影响断言。
            }
        }
    }
}
