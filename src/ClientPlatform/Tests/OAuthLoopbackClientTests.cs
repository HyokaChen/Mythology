using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// OAuthLoopbackClient 前端中转回调测试（CHANGE_09 形态 B 改造后）：
/// - 等待前端 POST /api/oauth/code 转发授权码；
/// - 内部超时（默认 120s）转 OAuthLoopbackException → 调用方回退设备流；
/// - 用户取消保持 OperationCanceledException 语义；
/// - state 校验失败可恢复，继续等待下一次 POST；
/// - CORS 预检仅放行 localhost:3000。
/// </summary>
public sealed class OAuthLoopbackClientTests
{
    [Fact]
    public async Task ListenForCode_Timeout_ThrowsOAuthLoopbackException()
    {
        using var listener = CreateLoopbackListener(out _);

        // 不发任何回调 → 内部超时（150ms）→ 必须转 OAuthLoopbackException
        var ex = await Assert.ThrowsAsync<OAuthLoopbackException>(() =>
            OAuthLoopbackClient.ListenForCodeAsync(
                listener,
                "state",
                TimeSpan.FromMilliseconds(150),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("超时", ex.Message);
    }

    [Fact]
    public async Task ListenForCode_UserCancellation_PropagatesOperationCanceled()
    {
        using var listener = CreateLoopbackListener(out _);
        using var cts = new CancellationTokenSource();

        var task = OAuthLoopbackClient.ListenForCodeAsync(
            listener,
            "state",
            TimeSpan.FromSeconds(5),
            cts.Token
        );

        // 回调窗口内用户取消（Esc / 应用关停）→ 保持 OCE 语义，绝不转成"超时回退"
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ListenForCode_ValidPost_ReturnsCodeAndState()
    {
        using var listener = CreateLoopbackListener(out var port);

        var task = OAuthLoopbackClient.ListenForCodeAsync(
            listener,
            "expected-state",
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        // 模拟前端 /callback 页面 POST /api/oauth/code
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/oauth/code",
            new { code = "abc", state = "expected-state" }
        );

        var result = await task;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("abc", result.Code);
        Assert.Equal("expected-state", result.State);
    }

    [Fact]
    public async Task ListenForCode_StateMismatch_Returns400AndContinues()
    {
        using var listener = CreateLoopbackListener(out var port);

        var task = OAuthLoopbackClient.ListenForCodeAsync(
            listener,
            "expected-state",
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        // 第一次 POST：state 不匹配 → 400，监听继续
        using var client = new HttpClient();
        var badResponse = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/oauth/code",
            new { code = "bad", state = "wrong" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        // 第二次 POST：合法 → 成功
        var goodResponse = await client.PostAsJsonAsync(
            $"http://127.0.0.1:{port}/api/oauth/code",
            new { code = "good", state = "expected-state" }
        );
        Assert.Equal(HttpStatusCode.OK, goodResponse.StatusCode);

        var result = await task;
        Assert.Equal("good", result.Code);
        Assert.Equal("expected-state", result.State);
    }

    [Fact]
    public async Task ListenForCode_OptionsPreflight_Returns204WithCorsHeaders()
    {
        using var _ = WithLocalWebOrigin();
        using var listener = CreateLoopbackListener(out var port);

        // 必须启动监听循环才能 accept OPTIONS；短超时让任务在断言后自然以超时结束
        var task = OAuthLoopbackClient.ListenForCodeAsync(
            listener,
            "state",
            TimeSpan.FromMilliseconds(500),
            TestContext.Current.CancellationToken
        );

        var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/api/oauth/code");
        request.Method = "OPTIONS";
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = (HttpWebResponse)await request.GetResponseAsync();
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:3000", response.Headers["Access-Control-Allow-Origin"]);
        Assert.Contains("POST", response.Headers["Access-Control-Allow-Methods"] ?? "");

        // 无后续请求 → 内部超时结束监听循环
        await Assert.ThrowsAsync<OAuthLoopbackException>(() => task);
    }

    [Fact]
    public async Task ListenForCode_PostFromAllowedOrigin_Returns200WithCorsHeaders()
    {
        using var _ = WithLocalWebOrigin();
        using var listener = CreateLoopbackListener(out var port);

        var task = OAuthLoopbackClient.ListenForCodeAsync(
            listener,
            "expected-state",
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        // 模拟浏览器 /callback 页：带 Origin 的 POST（实际会先 OPTIONS 预检，再发实际 POST）。
        // 实际 POST 的响应必须带 Access-Control-Allow-Origin，否则浏览器拦截响应 → ERR_FAILED。
        using var client = new HttpClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{port}/api/oauth/code"
        )
        {
            Content = new StringContent(
                "{\"code\":\"abc\",\"state\":\"expected-state\"}",
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Add("Origin", "http://localhost:3000");
        var response = await client.SendAsync(request);

        var result = await task;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "http://localhost:3000",
            response.Headers.GetValues("Access-Control-Allow-Origin").First()
        );
        Assert.Equal("abc", result.Code);
        Assert.Equal("expected-state", result.State);
    }

    [Fact]
    public async Task ListenForCode_NonOriginPreflight_Returns204WithoutCorsHeaders()
    {
        using var _ = WithLocalWebOrigin();
        using var listener = CreateLoopbackListener(out var port);

        var task = OAuthLoopbackClient.ListenForCodeAsync(
            listener,
            "state",
            TimeSpan.FromMilliseconds(500),
            TestContext.Current.CancellationToken
        );

        var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/api/oauth/code");
        request.Method = "OPTIONS";
        request.Headers.Add("Origin", "http://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = (HttpWebResponse)await request.GetResponseAsync();
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(response.Headers["Access-Control-Allow-Origin"]);

        await Assert.ThrowsAsync<OAuthLoopbackException>(() => task);
    }

    /// <summary>绑定 127.0.0.1 随机端口并启动监听（与 OAuthLoopbackClient 相同模式）。</summary>
    /// <summary>CORS 用例依赖 MYTHOLOGY_WEB_URL 指向本地前端源（默认是生产域名，不包含 localhost:3000）；测试内显式设置并恢复，避免裸机/CI 环境变量缺失导致误失败。</summary>
    private static IDisposable WithLocalWebOrigin()
    {
        var previous = Environment.GetEnvironmentVariable("MYTHOLOGY_WEB_URL");
        Environment.SetEnvironmentVariable("MYTHOLOGY_WEB_URL", "http://localhost:3000");
        return new RestoreEnv(previous);
    }

    private sealed class RestoreEnv(string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("MYTHOLOGY_WEB_URL", previous);
    }

    private static HttpListener CreateLoopbackListener(out int port)
    {
        using var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }
}
