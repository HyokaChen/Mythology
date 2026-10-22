using System.Net;
using System.Text.Json;
using Core.Data;
using Core.Inference;
using Xunit;

namespace Core.Tests;

/// <summary>
/// SystemOneClient 线协议单元测试（不触网，脚本化 HttpMessageHandler）：
/// 请求形状（noul/choice + criteria "true"/"false" 键）、应答容错解析（probabilities/p 回退）、
/// 失败即 null（500/超时/非法 JSON）、禁用零请求、UpdateConfig 热更新即时生效。
/// </summary>
public sealed class SystemOneClientTests
{
    // ══════════════════════════════════════════════════════
    //  请求形状
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task AskAsync_BuildsRequestShape()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(
            """{"model":"m","answers":{},"usage":{"input_tokens":1,"output_tokens":0}}"""
        );
        using var client = CreateClient(handler);

        var questions = new Dictionary<string, SystemOneQuestion>
        {
            ["p0"] = new NoulQuestion
            {
                Instructions = "判断相关性",
                Criteria = new NoulCriteria { Yes = "相关", No = "无关" },
            },
            ["c0"] = new ChoiceQuestion
            {
                Instructions = "判断类型",
                Criteria = new Dictionary<string, string?> { ["friend"] = "朋友", ["none"] = null },
            },
        };

        await client.AskAsync(
            new { doc = "林寒递给苏晚一把剑。" },
            questions,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("http://127.0.0.1:8080/v1/systemone", handler.RequestUris[0].ToString());

        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = doc.RootElement;
        Assert.Equal("d1-omni", root.GetProperty("model").GetString());
        Assert.Equal(
            "林寒递给苏晚一把剑。",
            root.GetProperty("state").GetProperty("doc").GetString()
        );

        var q = root.GetProperty("questions");
        Assert.Equal("noul", q.GetProperty("p0").GetProperty("type").GetString());
        Assert.Equal("判断相关性", q.GetProperty("p0").GetProperty("instructions").GetString());
        Assert.Equal(
            "相关",
            q.GetProperty("p0").GetProperty("criteria").GetProperty("true").GetString()
        );
        Assert.Equal(
            "无关",
            q.GetProperty("p0").GetProperty("criteria").GetProperty("false").GetString()
        );
        Assert.Equal("choice", q.GetProperty("c0").GetProperty("type").GetString());
        Assert.Equal(
            "朋友",
            q.GetProperty("c0").GetProperty("criteria").GetProperty("friend").GetString()
        );
    }

    // ══════════════════════════════════════════════════════
    //  应答解析
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task AskAsync_ParsesNoulAndChoice()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(
            """
            {
              "model": "m",
              "answers": {
                "p0": {"type": "noul", "noul": 0.87},
                "c0": {"type": "choice", "choice": "lover",
                       "probabilities": {"lover": 0.8, "friend": 0.2}, "confidence": 0.9}
              },
              "usage": {"input_tokens": 10, "output_tokens": 0}
            }
            """
        );
        using var client = CreateClient(handler);

        var response = await client.AskAsync(
            new { doc = "x" },
            Noul("p0"),
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(response);
        Assert.Equal(0.87, response!.Noul("p0")!.Value, 3);
        Assert.Equal(10, response.InputTokens);

        var choice = response.Answers["c0"];
        Assert.Equal("lover", choice.Choice);
        Assert.Equal(0.8, choice.Probabilities!["lover"], 3);
        Assert.Equal(0.9, choice.Confidence!.Value, 3);
    }

    [Fact]
    public async Task ParseResponse_ProbabilitiesFallbackToP()
    {
        var response = SystemOneWire.ParseResponse(
            """{"model":"m","answers":{"c0":{"type":"choice","choice":"a","p":{"a":0.6,"b":0.4}}},"usage":{}}"""
        );

        Assert.NotNull(response);
        var choice = response!.Answers["c0"];
        Assert.Equal("a", choice.Choice);
        Assert.Equal(0.6, choice.Probabilities!["a"], 3);
    }

    [Fact]
    public void ParseResponse_MissingAnswerKey_KeyAbsent()
    {
        var response = SystemOneWire.ParseResponse("""{"model":"m","answers":{},"usage":{}}""");

        Assert.NotNull(response);
        Assert.Empty(response!.Answers);
        Assert.Null(response.Noul("p0"));
    }

    [Fact]
    public void ParseResponse_OutOfRangeNoul_ReturnsNull()
    {
        var response = SystemOneWire.ParseResponse(
            """{"model":"m","answers":{"p0":{"type":"noul","noul":1.4}},"usage":{}}"""
        );

        Assert.NotNull(response);
        Assert.Null(response!.Noul("p0")); // 越界 → 未知
    }

    // ══════════════════════════════════════════════════════
    //  失败降级
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task AskAsync_Http500_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        );
        using var client = CreateClient(handler);

        var response = await client.AskAsync(
            new { },
            Noul("p0"),
            TestContext.Current.CancellationToken
        );

        Assert.Null(response);
        Assert.Equal(1, handler.CallCount); // 不重试
    }

    [Fact]
    public async Task AskAsync_Timeout_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(
            async (_, ct) =>
            {
                await Task.Delay(2_000, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = CreateClient(handler, timeoutMs: 50);

        var response = await client.AskAsync(
            new { },
            Noul("p0"),
            TestContext.Current.CancellationToken
        );

        Assert.Null(response);
    }

    [Fact]
    public async Task AskAsync_InvalidJson_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("not json at all");
        using var client = CreateClient(handler);

        var response = await client.AskAsync(
            new { },
            Noul("p0"),
            TestContext.Current.CancellationToken
        );

        Assert.Null(response);
    }

    [Fact]
    public async Task AskAsync_Disabled_SendsNoRequest()
    {
        var handler = new FakeHttpMessageHandler();
        using var client = new SystemOneClient(
            new SystemOneConfig { Enabled = false },
            handler: handler
        );

        var response = await client.AskAsync(
            new { },
            Noul("p0"),
            TestContext.Current.CancellationToken
        );

        Assert.Null(response);
        Assert.Equal(0, handler.CallCount);
    }

    // ══════════════════════════════════════════════════════
    //  配置热更新
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateConfig_TakesEffectImmediately()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"model":"m","answers":{},"usage":{}}""");
        using var client = new SystemOneClient(
            new SystemOneConfig { Enabled = false },
            handler: handler
        );

        client.UpdateConfig(
            new SystemOneConfig { Enabled = true, BaseUrl = "http://127.0.0.1:9999" }
        );

        await client.AskAsync(new { }, Noul("p0"), TestContext.Current.CancellationToken);

        Assert.True(client.Enabled);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("http://127.0.0.1:9999/v1/systemone", handler.RequestUris[0].ToString());
    }

    [Fact]
    public async Task AskAsync_ApiKeyAndPath_SendsBearerToHostedEndpoint()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"model":"m","answers":{},"usage":{}}""");
        using var client = new SystemOneClient(
            new SystemOneConfig
            {
                Enabled = true,
                BaseUrl = "https://api.typesafe.ai",
                Path = "/v1/systemone",
                ApiKey = "sk-test",
                Model = "jev-latest",
            },
            handler: handler
        );

        await client.AskAsync(new { }, Noul("p0"), TestContext.Current.CancellationToken);

        // 托管 Jev（TypeSafe）端点为基址 + /v1/systemone，且带 Bearer 头
        Assert.Equal("https://api.typesafe.ai/v1/systemone", handler.RequestUris[0].ToString());
        Assert.Equal("Bearer sk-test", handler.AuthHeaders[0]);
    }

    [Fact]
    public async Task AskAsync_Local_NoAuthorizationHeader()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"model":"m","answers":{},"usage":{}}""");
        using var client = CreateClient(handler);

        await client.AskAsync(new { }, Noul("p0"), TestContext.Current.CancellationToken);

        Assert.Null(handler.AuthHeaders[0]); // 本地端点不发 Authorization
    }

    // ══════════════════════════════════════════════════════
    //  批打包
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Pack_SplitsByBudget()
    {
        var items = new[] { "aaaa", "bbbb", "cccc" }; // 每项 4 字符

        var batches = SystemOnePacking.Pack(items, 9, x => x);

        Assert.Equal(2, batches.Count);
        Assert.Equal(["aaaa", "bbbb"], batches[0]);
        Assert.Equal(["cccc"], batches[1]);
    }

    [Fact]
    public void Pack_OversizedItem_StillSingleBatch()
    {
        var items = new[] { "xxxxxx", "y" };

        var batches = SystemOnePacking.Pack(items, 3, x => x);

        Assert.Equal(2, batches.Count); // 超预算项单独成包，不丢弃
        Assert.Equal(["xxxxxx"], batches[0]);
    }

    // ══════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════

    private static Dictionary<string, SystemOneQuestion> Noul(string key) =>
        new() { [key] = new NoulQuestion { Instructions = "?" } };

    private static SystemOneClient CreateClient(
        FakeHttpMessageHandler handler,
        int timeoutMs = 5_000
    ) =>
        new(
            new SystemOneConfig
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:8080",
                TimeoutMs = timeoutMs,
            },
            logger: null,
            handler: handler
        );

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
        > _responses = new();

        public int CallCount { get; private set; }
        public List<Uri> RequestUris { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public List<string?> AuthHeaders { get; } = [];

        public void Enqueue(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> factory
        ) => _responses.Enqueue(factory);

        public void EnqueueJson(string json) =>
            Enqueue(
                (_, _) =>
                    Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                json,
                                System.Text.Encoding.UTF8,
                                "application/json"
                            ),
                        }
                    )
            );

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            RequestUris.Add(request.RequestUri!);
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            RequestBodies.Add(
                request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken)
            );
            if (_responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"model":"m","answers":{},"usage":{}}"""),
                };
            return await _responses.Dequeue()(request, cancellationToken);
        }
    }
}
