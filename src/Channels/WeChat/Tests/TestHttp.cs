// 功能：测试用 HTTP 桩 —— 记录请求（头 + body）并按脚本回响应，不碰网络

using System.Net;
using System.Text;
using Microsoft.Extensions.Http;

namespace Channels.WeChat.Tests;

/// <summary>记录每个出站请求的原始请求对象与 body，供头部/请求体断言。</summary>
internal sealed class RecordingHandler(
    Func<HttpRequestMessage, string, HttpResponseMessage> respond
) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(request);
        Bodies.Add(body);
        return respond(request, body);
    }

    public string LastBody => Bodies[^1];

    public HttpRequestMessage LastRequest => Requests[^1];
}

/// <summary>只服务一个桩的工厂（协议客户端唯一需要的外部依赖）。</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>按脚本顺序回响应的桩：第 N 次请求用第 N 个步骤，脚本用完即报错（漏写一步不会静默变成空响应）。</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, string, HttpResponseMessage>> _script = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public int RemainingSteps => _script.Count;

    public void Enqueue(Func<HttpRequestMessage, string, HttpResponseMessage> step) =>
        _script.Enqueue(step);

    public void EnqueueJson(string json) => _script.Enqueue((_, _) => Stubs.Json(json));

    public void EnqueueThrow(Exception exception) => _script.Enqueue((_, _) => throw exception);

    /// <summary>按顺序取下一步；不足时抛错并带上请求 URI，便于定位漏写了哪一步。</summary>
    public HttpResponseMessage Next(HttpRequestMessage request, string body) =>
        _script.Count > 0
            ? _script.Dequeue()(request, body)
            : throw new InvalidOperationException(
                $"脚本已用尽，收到意外请求: {request.RequestUri}"
            );

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // 像真 handler 那样先尊重取消，否则"已取消"的用例会变成"脚本用尽"这种无关失败
        cancellationToken.ThrowIfCancellationRequested();

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(request);
        Bodies.Add(body);
        return Next(request, body);
    }
}

/// <summary>
/// 按 URL 片段匹配的桩：适合**出站请求顺序不确定**的场景
/// （例如 sendmessage 与 sendtyping 并发，严格顺序脚本会脆）。
/// 与 ScriptedHandler 共存而不替换：那边靠严格顺序断状态机分支，替换会丢掉那个能力。
/// </summary>
internal sealed class PathHandler : HttpMessageHandler
{
    private sealed class Route
    {
        public required string Fragment { get; init; }
        public required string Json { get; init; }
        public int Remaining { get; set; }

        /// <summary>条件就绪才响应（用来让后续入站消息等问题渲染出来后再送达）。</summary>
        public Func<bool>? Ready { get; init; }
    }

    private readonly List<Route> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    /// <summary>无路由命中时的响应（默认空成功响应）。</summary>
    public string FallbackJson { get; set; } = """{"ret":0}""";

    /// <summary>每个请求的回调（用于观察 + 触发 TCS）。</summary>
    public Action<HttpRequestMessage, string>? OnRequest { get; set; }

    /// <summary>登记一条路由；<paramref name="times"/> 为可命中次数。</summary>
    public void RouteTo(string fragment, string json, int times = 1) =>
        _routes.Add(
            new Route
            {
                Fragment = fragment,
                Json = json,
                Remaining = times,
            }
        );

    /// <summary>
    /// 登记一条"条件就绪才响应"的路由。
    /// 用途：让第二条入站消息等问题已经渲染到手机上之后再送达——真实服务端每轮挂起 ~30s，
    /// 问题在毫秒级就渲染完了，不会出现"答案早于问题"；而桩是瞬时返回的，不加闸就会。
    /// </summary>
    public void RouteWhen(string fragment, Func<bool> ready, string json, int times = 1) =>
        _routes.Add(
            new Route
            {
                Fragment = fragment,
                Json = json,
                Remaining = times,
                Ready = ready,
            }
        );

    public IReadOnlyList<HttpRequestMessage> RequestsTo(string fragment) =>
        Requests
            .Where(r => r.RequestUri!.AbsoluteUri.Contains(fragment, StringComparison.Ordinal))
            .ToList();

    public IReadOnlyList<string> BodiesTo(string fragment) =>
        Requests
            .Select((r, i) => (r, i))
            .Where(x => x.r.RequestUri!.AbsoluteUri.Contains(fragment, StringComparison.Ordinal))
            .Select(x => Bodies[x.i])
            .ToList();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(request);
        Bodies.Add(body);
        OnRequest?.Invoke(request, body);

        var uri = request.RequestUri!.AbsoluteUri;
        var route = _routes.FirstOrDefault(r =>
            r.Remaining > 0 && uri.Contains(r.Fragment, StringComparison.Ordinal)
        );
        if (route is null)
            return Stubs.Json(FallbackJson);

        if (route.Ready is not null)
        {
            var readyBy = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!route.Ready() && DateTimeOffset.UtcNow < readyBy)
                await Task.Delay(10, cancellationToken);
        }

        route.Remaining--;
        return Stubs.Json(route.Json);
    }
}

/// <summary>测试用临时目录（每个用例一份，用完删）。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"mythology-wechat-test-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结果
        }
    }
}

internal static class Stubs
{
    /// <summary>200 + JSON 体。</summary>
    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>始终返回同一 JSON 的客户端。</summary>
    public static WeChatIlinkClient Client(string json, out RecordingHandler handler)
    {
        handler = new RecordingHandler((_, _) => Json(json));
        return new WeChatIlinkClient(new StubHttpClientFactory(handler));
    }

    /// <summary>始终返回同一 JSON 的客户端（不需要检查请求时用）。</summary>
    public static WeChatIlinkClient Client(string json) => Client(json, out _);

    /// <summary>请求挂起不返回（用于验证客户端超时预算）。</summary>
    public static WeChatIlinkClient HangingClient(out RecordingHandler handler)
    {
        handler = new RecordingHandler(
            (_, _) =>
            {
                throw new OperationCanceledException("stub: request never completes");
            }
        );
        return new WeChatIlinkClient(new StubHttpClientFactory(handler));
    }

    public static WeChatCredential Credential { get; } =
        new()
        {
            BaseUrl = "https://ilinkai.weixin.qq.com",
            BotToken = "token-abc",
            AccountId = "bot1234@im.bot",
            PeerUserId = "wxid_user",
            BoundAt = DateTimeOffset.UnixEpoch,
        };
}
