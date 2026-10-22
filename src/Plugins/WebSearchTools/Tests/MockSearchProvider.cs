using Plugins.WebSearchTools;

namespace WebSearchTools.Tests;

public class MockSearchProvider(List<WebSearchResult>? results = null, string? fetchResult = null)
    : IWebSearchProvider
{
    private readonly List<WebSearchResult> _results = results ?? [];

    public int SearchCallCount { get; private set; }
    public int FetchCallCount { get; private set; }
    public string? LastQuery { get; private set; }
    public string? LastUrl { get; private set; }

    public Task<List<WebSearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default
    )
    {
        SearchCallCount++;
        LastQuery = query;
        return Task.FromResult(_results.Take(maxResults).ToList());
    }

    public Task<string> FetchAndSummarizeAsync(string url, CancellationToken ct = default)
    {
        FetchCallCount++;
        LastUrl = url;
        return Task.FromResult(fetchResult ?? $"Mock content for {url}");
    }
}
