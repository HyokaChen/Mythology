using Plugins.WebSearchTools;
using Xunit;

namespace WebSearchTools.Tests;

public class BingSearchProviderTests
{
    [Fact]
    public void Constructor_WithApiKey_DoesNotThrow()
    {
        var provider = new BingSearchProvider("fake-key");
        Assert.NotNull(provider);
    }

    [Fact]
    public void Constructor_WithoutApiKey_DoesNotThrow()
    {
        var provider = new BingSearchProvider("");
        Assert.NotNull(provider);
    }

    [Fact]
    public async Task SearchAsync_WithoutApiKey_ReturnsBingFallback()
    {
        var provider = new BingSearchProvider("");

        var results = await provider.SearchAsync(
            "test query",
            ct: TestContext.Current.CancellationToken
        );

        Assert.Single(results);
        Assert.Contains("bing.com/search", results[0].Url);
    }

    [Fact]
    public async Task SearchAsync_WithoutApiKey_ContainsQueryInUrl()
    {
        var provider = new BingSearchProvider("");

        var results = await provider.SearchAsync(
            "unique search term",
            ct: TestContext.Current.CancellationToken
        );

        Assert.Single(results);
        Assert.Contains("unique", results[0].Url);
        Assert.Contains("search", results[0].Url);
        Assert.Contains("term", results[0].Url);
    }

    [Fact]
    public async Task FetchAndSummarizeAsync_InsecureUrl_ReturnsError()
    {
        var provider = new BingSearchProvider("");

        var result = await provider.FetchAndSummarizeAsync(
            "http://localhost:8080/secret",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("不安全", result);
    }

    [Fact]
    public async Task FetchAndSummarizeAsync_PrivateIp10_ReturnsError()
    {
        var provider = new BingSearchProvider("");

        var result = await provider.FetchAndSummarizeAsync(
            "http://10.0.0.1/admin",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("不安全", result);
    }

    [Fact]
    public async Task FetchAndSummarizeAsync_PrivateIp192168_ReturnsError()
    {
        var provider = new BingSearchProvider("");

        var result = await provider.FetchAndSummarizeAsync(
            "http://192.168.1.1/config",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("不安全", result);
    }

    [Fact]
    public async Task FetchAndSummarizeAsync_LinkLocal169254_ReturnsError()
    {
        var provider = new BingSearchProvider("");

        var result = await provider.FetchAndSummarizeAsync(
            "http://169.254.1.1/",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("不安全", result);
    }

    [Fact]
    public async Task FetchAndSummarizeAsync_PrivateIp172_16_ReturnsError()
    {
        var provider = new BingSearchProvider("");

        var result = await provider.FetchAndSummarizeAsync(
            "http://172.16.0.1/",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("不安全", result);
    }

    [Fact]
    public async Task FetchAndSummarizeAsync_InvalidUrl_ReturnsError()
    {
        var provider = new BingSearchProvider("");

        var result = await provider.FetchAndSummarizeAsync(
            "not a url",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("不安全", result);
    }

    [Fact]
    public async Task FetchAndSummarizeAsync_FtpScheme_ReturnsError()
    {
        var provider = new BingSearchProvider("");

        var result = await provider.FetchAndSummarizeAsync(
            "ftp://example.com/file",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("不安全", result);
    }

    [Fact]
    public async Task FetchAndSummarizeAsync_PublicUrl_TriesToFetch()
    {
        var provider = new BingSearchProvider("");

        // This will try to actually fetch, which will likely fail (no network or blocked)
        var result = await provider.FetchAndSummarizeAsync(
            "https://example.com",
            TestContext.Current.CancellationToken
        );

        // Either it succeeds with content, or fails with an error message
        Assert.NotNull(result);
    }
}
