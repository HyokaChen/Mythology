using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ClientPlatform;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// :cloud status 积分余量接线测试（无终端环境，反射驱动 HandleSlashCommand）：
/// 已配置云端会话 + 已接线用量客户端 → 命令触发零价 GET /v1/agent/usage；
/// 未配置会话 / 未接线客户端 → 不发请求（仅连接态 Toast）。
///
/// Toast 文本的真实渲染由 TuiAppTerminalSlashCommandIntegrationTests
/// .CloudColonStatus_ShowsCloudStatusToast（虚拟终端）覆盖；本类只断言可观察的 HTTP 副作用。
/// </summary>
public sealed class TuiAppCloudUsageTests : IAsyncDisposable
{
    private const string UsageJson = """
        {"period":"2026-09","plan":"creator","included-units":5000,"used-units":87,
         "reserved-units":0,"remaining-units":4913,"breakdown":{"fiction.story.check":87}}
        """;

    public async ValueTask DisposeAsync() => await H.DisposeAsync();

    private TuiSlashTestHarness H { get; }

    private readonly ConcurrentQueue<string> _paths = new();

    public TuiAppCloudUsageTests()
    {
        H = new TuiSlashTestHarness(cloudUsage: CreateUsageClient());
    }

    private UsageRestClient CreateUsageClient() =>
        new(
            "http://localhost",
            () => "mk_test_public-secret",
            http: new HttpClient(
                new StubHandler(request =>
                {
                    _paths.Enqueue(request.RequestUri!.PathAndQuery);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(UsageJson, Encoding.UTF8, "application/json"),
                    };
                })
            )
        );

    [Fact]
    public async Task CloudStatus_WhenConfigured_QueriesUsageEndpoint()
    {
        H.SeedConfiguredCloudSession();

        H.InvokeSlashCommand(":cloud status");

        await TuiSlashTestHarness.WaitUntilAsync(
            () => !_paths.IsEmpty,
            "已配置云端时 :cloud status 未触发用量查询"
        );
        var path = Assert.Single(_paths);
        Assert.Equal("/v1/agent/usage", path); // 无 period → 服务端缺省当月
    }

    [Fact]
    public async Task CloudStatus_WhenNotConfigured_DoesNotQueryUsage()
    {
        // 未登录：连接态 Toast 已给出原因，不产生无谓的 401 请求
        H.InvokeSlashCommand(":cloud status");

        await Task.Delay(200);
        Assert.Empty(_paths);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(responder(request));
    }
}
