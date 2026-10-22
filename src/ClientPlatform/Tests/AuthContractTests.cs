using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// 跨仓库契约测试（审计 CHANGE_09_FLOW_AUDIT §6 的“两端各自测自己想象的对方”盲区）：
/// 真实 Mythology.Cloud.Api（InMemory 子进程）↔ 真实 ClientPlatform 客户端类。
/// 设备流与本地回调各一条全链：discovery → authorize → approve/code → token。
/// 网页控制面一侧（登录/批准/铸码）用 HTTP 模拟，客户端侧全部走真实类。
/// </summary>
[Collection("live-backend")]
public sealed class AuthContractTests(CloudBackendFixture fixture)
{
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromSeconds(60);

    /// <summary>OAuth 本地回调：客户端与 fixture 后端约定同一前端 origin（localhost:3000）。</summary>
    private static IDisposable WithLocalWebOrigin()
    {
        var previous = Environment.GetEnvironmentVariable("MYTHOLOGY_WEB_URL");
        Environment.SetEnvironmentVariable("MYTHOLOGY_WEB_URL", "http://localhost:3000");
        return new RestoreEnv(previous);
    }

    private sealed class RestoreEnv(string previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("MYTHOLOGY_WEB_URL", previous);
    }

    private const string SkipReason =
        "未找到后端项目（设置 MYTHOLOGY_CLOUD_BACKEND 指向私有后端仓库的 backend 后重跑）";

    [Fact]
    public async Task DeviceFlow_RealBackend_AuthorizeApprovePollToken()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (jwt, workspaceId) = await LoginAsync(ct);

        var deviceClient = new DeviceAuthorizationClient(new HttpClient(), fixture.BaseUrl!);

        // ① 真实客户端 authorize（内部先 discovery → device_authorization_endpoint）
        //    —— 若客户端仍读错字段（POST 到 /v1/oauth/authorize），真实后端会回 405 使本测试失败。
        var authorization = await deviceClient.AuthorizeAsync("contract-device", ct);
        Assert.Equal(8, authorization.UserCode.Length);
        Assert.False(string.IsNullOrWhiteSpace(authorization.DeviceCode));
        Assert.Contains("/app/device?user_code=", authorization.VerificationUri);

        // ② 客户端 SSE 轮询挂起（pending → approved 信号路径），随后模拟浏览器批准
        var pollTask = deviceClient.PollTokenAsync(authorization.DeviceCode, ct);
        var approve = await PostWithJwtAsync(
            "/v1/device/approve",
            new Dictionary<string, object?>
            {
                ["user-code"] = authorization.UserCode,
                ["workspace-id"] = workspaceId,
            },
            jwt,
            ct
        );
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

        // ③ 真实客户端收到 approved 事件 → 数据面 API Key
        var token = await pollTask.WaitAsync(FlowTimeout, ct);
        Assert.NotNull(token);
        Assert.StartsWith("mk_live_", token!.ApiKey);
        Assert.Equal(workspaceId.ToString(), token.WorkspaceId);
        // ④ §13 Tier 2：服务端权威过期真值（签发 now+30d）
        Assert.NotNull(token.ExpiresAt);
        Assert.InRange(
            token.ExpiresAt!.Value,
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-2),
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(2)
        );
    }

    [Fact]
    public async Task LocalCallback_RealBackend_PkceLoopbackExchange()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (jwt, workspaceId) = await LoginAsync(ct);

        using var webOrigin = WithLocalWebOrigin();
        var oauthClient = new OAuthLoopbackClient(new HttpClient(), fixture.BaseUrl!);

        // ① 真实客户端全流程（PKCE → 绑定 loopback 监听 → 预检 → 构建授权 URL），浏览器动作注入
        var urlTcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var flowTask = oauthClient.AuthorizeAsync(
            url =>
            {
                urlTcs.TrySetResult(url);
                return true; // 模拟浏览器打开成功
            },
            ct
        );

        var authorizeUrl = await urlTcs.Task.WaitAsync(FlowTimeout, ct);
        var state = QueryParam(authorizeUrl, "state");
        var redirectUri = QueryParam(authorizeUrl, "redirect_uri");
        var challenge = QueryParam(authorizeUrl, "code_challenge");
        var clientPort = QueryParam(authorizeUrl, "client_port");
        Assert.NotNull(state);
        Assert.Equal("http://localhost:3000/callback", redirectUri);
        Assert.False(string.IsNullOrWhiteSpace(clientPort));
        Assert.Equal("S256", QueryParam(authorizeUrl, "code_challenge_method"));

        // ② 模拟浏览器：登录后确认授权 → 铸码（真实后端 + camelCase body，审计 C 的回归守卫）
        var codeResp = await PostWithJwtAsync(
            "/v1/oauth/code",
            new Dictionary<string, object?>
            {
                ["client-id"] = "mythology-tui",
                ["redirect-uri"] = redirectUri,
                ["code-challenge"] = challenge,
                ["code-challenge-method"] = "S256",
                ["state"] = state,
                ["workspace-id"] = workspaceId,
            },
            jwt,
            ct
        );
        Assert.Equal(HttpStatusCode.Created, codeResp.StatusCode);
        var codeBody = await codeResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var code = codeBody.GetProperty("code").GetString();
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.Equal(state, codeBody.GetProperty("state").GetString());

        // ③ 模拟前端 /callback 页面 POST /api/oauth/code 转发 code + state 到客户端本地监听
        var callbackPort = int.Parse(clientPort!);
        using var callbackClient = new HttpClient();
        var callbackResp = await callbackClient.PostAsJsonAsync(
            $"http://127.0.0.1:{callbackPort}/api/oauth/code",
            new { code = code!, state = state! },
            ct
        );
        Assert.Equal(HttpStatusCode.OK, callbackResp.StatusCode);

        // ④ 真实客户端校验 state → 兑换 → 数据面 API Key
        var result = await flowTask.WaitAsync(FlowTimeout, ct);
        Assert.StartsWith("mk_live_", result.ApiKey);
        Assert.Equal(workspaceId.ToString(), result.WorkspaceId);
        // ⑤ §13 Tier 2：OAuth token 同样携带服务端权威过期真值
        Assert.NotNull(result.ExpiresAt);
        Assert.InRange(
            result.ExpiresAt!.Value,
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-2),
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(2)
        );
    }

    // ── 网页控制面一侧（模拟浏览器） ──

    private async Task<(string Jwt, Guid WorkspaceId)> LoginAsync(CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl!) };
        var email = $"contract-{Guid.NewGuid():N}@example.com";
        using (
            await http.PostAsJsonAsync(
                "/v1/auth/register",
                new Dictionary<string, object?>
                {
                    ["email"] = email,
                    ["password"] = "password123",
                    ["display-name"] = "Contract",
                    ["device-name"] = "contract-test",
                },
                ct
            )
        ) { }

        using var login = await http.PostAsJsonAsync(
            "/v1/auth/login",
            new Dictionary<string, object?>
            {
                ["email"] = email,
                ["password"] = "password123",
                ["device-name"] = "contract-test",
            },
            ct
        );
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var jwt = body.GetProperty("access-token").GetString()!;
        var workspaceId = body.GetProperty("user")
            .GetProperty("workspaces")[0]
            .GetProperty("workspace-id")
            .GetGuid();
        return (jwt, workspaceId);
    }

    private async Task<HttpResponseMessage> PostWithJwtAsync(
        string path,
        object body,
        string jwt,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}{path}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new("Bearer", jwt);
        using var http = new HttpClient();
        return await http.SendAsync(request, ct);
    }

    private static string? QueryParam(string url, string name) =>
        new Uri(url)
            .Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0] == name)
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();
}
