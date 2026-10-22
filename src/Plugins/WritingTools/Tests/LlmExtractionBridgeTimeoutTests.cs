using System.Diagnostics;
using System.Net;
using ClientPlatform;
using Core.Agent;
using Core.Tools;
using LlmTornado;
using LlmTornado.Code;
using Plugins.WritingTools.Infrastructure;
using Xunit;

namespace WritingTools.Tests;

/// <summary>
/// LlmExtractionBridge 超时降级回归测试（diagnose_writing 无限 spinner 修复）：
/// <see cref="LlmExtractionBridge.ExtractAsync"/> 的嵌套 LLM 流式调用没有 HttpClient.Timeout
/// 覆盖（SSE body 读取阶段不生效），provider 中途停流会让工具永久阻塞。修复后：
/// - 流挂起时在 <see cref="CloudTimings.AuxiliaryLlmTimeout"/> 内返回空结果（不永久 await）；
/// - 用户取消（外层 ct 已取消）仍重抛 OCE，不吞取消。
/// 通过 <see cref="EndpointBase.OnHttpClientRequested"/> 注入挂起的 HttpClient 模拟停流
/// （与 LlmApiFactory 注入 CancellationHttpHandler 同一钩子，不触碰真实网络）。
/// </summary>
public sealed class LlmExtractionBridgeTimeoutTests
{
    /// <summary>
    /// 立即返回 200 OK，但 body 流永不下发数据——模拟 provider 响应头已到、
    /// 正文中途停流且不关闭连接（正是 diagnose_writing 卡死的现场）。
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream()),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// 同步 Read 返回 0（EOF，StreamReader 构造的 BOM 探测不阻塞），
    /// 异步 ReadAsync 挂起到取消令牌触发才抛出 OCE——模拟 .NET 10
    /// NetworkStream.ReadAsync 在停流 + 令牌取消时的行为。
    /// </summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => 0; // EOF：不阻塞 BOM 探测

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => new(WaitForCancellationAsync(cancellationToken));

        private static async Task<int> WaitForCancellationAsync(CancellationToken ct)
        {
            // 挂起到令牌取消：取消时抛 OCE（模拟 NetworkStream.ReadAsync 行为）。
            // 若不取消则永不返回——被 CancelAfter 打断的正是这一行。
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    /// <summary>
    /// 核心回归：LLM 流挂起时，ExtractAsync 必须在 AuxiliaryLlmTimeout（60s）内返回空结果，
    /// 而不是永久 await（修复前 diagnose_writing 无限转圈）。
    /// </summary>
    [Fact]
    public async Task ExtractAsync_StreamHangs_TimesOutAndReturnsEmptyResult()
    {
        // 注意：不 Dispose —— LlmTornado 的 GetClient 会把该 client 缓存进静态
        // endpointClients（按 provider），同进程后续测试会复用；Dispose 会让复用路径抛 ObjectDisposedException。
        var hangingClient = new HttpClient(new HangingHandler());
        var previous = EndpointBase.OnHttpClientRequested;
        try
        {
            EndpointBase.OnHttpClientRequested = _ => hangingClient;

            var api = new TornadoApi(LLmProviders.OpenAi, "test-key");
            var agent = new Agent(new ToolRegistry(), api);
            agent.SetModelName("test-model");

            var sw = Stopwatch.StartNew();
            var extractTask = LlmExtractionBridge.ExtractAsync(
                agent,
                "测试章节内容：主角走进城门口。",
                [],
                TestContext.Current.CancellationToken
            );
            var completed = await Task.WhenAny(
                extractTask,
                Task.Delay(TimeSpan.FromSeconds(75), TestContext.Current.CancellationToken) // 回归（无超时）时 75s 上限判失败而非挂死套件
            );
            sw.Stop();

            Assert.True(
                completed == extractTask,
                $"ExtractAsync 未在 AuxiliaryLlmTimeout({CloudTimings.AuxiliaryLlmTimeout}) 内返回——流仍挂起"
            );

            var result = await extractTask;
            Assert.NotNull(result);
            Assert.Empty(result.CharacterPresences);
            Assert.Equal("", result.ModelUsed);
            // 必须真的等到了超时窗口（≈60s）而不是走了其他快速失败路径
            Assert.True(
                sw.Elapsed >= TimeSpan.FromSeconds(50),
                $"期望等待超时窗口（~{CloudTimings.AuxiliaryLlmTimeout}），实际 {sw.Elapsed}"
            );
        }
        finally
        {
            EndpointBase.OnHttpClientRequested = previous;
        }
    }

    /// <summary>
    /// 用户取消（外层 ct 已取消）必须重抛 OCE，而不是被降级分支吞掉——
    /// when 过滤器区分「用户取消（重抛）」与「超时（降级）」。
    /// </summary>
    [Fact]
    public async Task ExtractAsync_UserCancellation_Rethrows()
    {
        // 同第一个测试：不 Dispose，客户端会被 GetClient 静态缓存复用
        var hangingClient = new HttpClient(new HangingHandler());
        var previous = EndpointBase.OnHttpClientRequested;
        try
        {
            EndpointBase.OnHttpClientRequested = _ => hangingClient;

            var api = new TornadoApi(LLmProviders.OpenAi, "test-key");
            var agent = new Agent(new ToolRegistry(), api);
            agent.SetModelName("test-model");

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                LlmExtractionBridge.ExtractAsync(agent, "测试章节内容", [], cts.Token)
            );
        }
        finally
        {
            EndpointBase.OnHttpClientRequested = previous;
        }
    }
}
