using System.Net;
using System.Text;
using ClientPlatform;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 题材目录缓存：云端响应 → ~/.mythology/genre-catalog.yaml → 离线读取。
/// 目录必须整体替换（旧分类不能残留），云端失败时必须保留缓存而不是清空。
/// </summary>
public sealed class GenreCatalogServiceTests : IDisposable
{
    private const string CloudJson = """
        {
          "version": "1",
          "categories": [
            {
              "name": "轻小说",
              "subcategories": [
                { "name": "原生幻想" },
                { "name": "青春日常" }
              ]
            }
          ]
        }
        """;

    private readonly string _root;
    private readonly string _cachePath;

    public GenreCatalogServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"genre-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _cachePath = Path.Combine(_root, "genre-catalog.yaml");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task RefreshAsync_WritesCloudCatalog_AndOfflineLoadReadsFromCache()
    {
        var handler = new StubHandler(_ => JsonResponse(CloudJson));
        using var http = new HttpClient(handler);
        var client = new GenreCatalogRestClient(
            "https://cloud.test/",
            () => "test-key",
            http: http
        );
        var service = new GenreCatalogService(client, _cachePath);

        var cloud = await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("cloud", cloud.Source);
        Assert.Equal("1", cloud.Version);
        Assert.NotNull(cloud.Find("轻小说", "原生幻想"));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/v1/fiction/genres", handler.LastPath);
        Assert.Equal("test-key", handler.LastApiKey);

        Assert.True(File.Exists(_cachePath));
        var yaml = await File.ReadAllTextAsync(_cachePath, TestContext.Current.CancellationToken);
        Assert.Contains("轻小说", yaml);
        Assert.Contains("原生幻想", yaml);

        // 新服务实例只读缓存，不发网络请求（离线启动路径）。
        var offline = new GenreCatalogService(null, _cachePath);
        var cached = await offline.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("cache", cached.Source);
        Assert.NotNull(cached.Find("轻小说", "原生幻想"));
        Assert.Equal("原生幻想 │ 轻小说", cached.Find("轻小说", "原生幻想")!.Display);
    }

    [Fact]
    public async Task RefreshAsync_ReplacesStaleCacheWholesale()
    {
        await File.WriteAllTextAsync(
            _cachePath,
            """
            version: stale
            categories:
              - name: 旧分类
                subcategories:
                  - 旧题材
            """,
            TestContext.Current.CancellationToken
        );

        var handler = new StubHandler(_ => JsonResponse(CloudJson));
        using var http = new HttpClient(handler);
        var service = new GenreCatalogService(
            new GenreCatalogRestClient("https://cloud.test", () => "test-key", http: http),
            _cachePath
        );

        var cloud = await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Null(cloud.Find("旧分类", "旧题材"));
        Assert.NotNull(cloud.Find("轻小说", "原生幻想"));
        var yaml = await File.ReadAllTextAsync(_cachePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("旧分类", yaml);
        Assert.DoesNotContain("旧题材", yaml);
    }

    [Fact]
    public async Task RefreshAsync_CloudFailure_PreservesCachedCatalog()
    {
        await File.WriteAllTextAsync(
            _cachePath,
            """
            version: cached
            categories:
              - name: 轻小说
                subcategories:
                  - 原生幻想
            """,
            TestContext.Current.CancellationToken
        );

        var handler = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.ServiceUnavailable
        )
        {
            Content = new StringContent(
                """{"code":"unavailable","detail":"offline"}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var http = new HttpClient(handler);
        var service = new GenreCatalogService(
            new GenreCatalogRestClient("https://cloud.test", () => "test-key", http: http),
            _cachePath
        );

        var fallback = await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("cache", fallback.Source);
        Assert.NotNull(fallback.Find("轻小说", "原生幻想"));
        Assert.True(File.Exists(_cachePath));
    }

    [Fact]
    public void Resolve_LegacyChildOnly_InfersParentFromLocalCatalog()
    {
        var resolved = GenreCatalog.Local.Resolve(null, "原生幻想");

        Assert.Equal("轻小说", resolved.Parent);
        Assert.Equal("原生幻想", resolved.Child);
        Assert.Equal("原生幻想 │ 轻小说", resolved.Display);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            LastApiKey = request.Headers.TryGetValues("X-API-Key", out var values)
                ? values.SingleOrDefault()
                : null;
            return Task.FromResult(respond(request));
        }
    }
}
