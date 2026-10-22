using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Core.Plugins;
using Core.Shared;
using Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// 权限声明 — 编译进 DLL，受强名称签名保护
[assembly: PluginPermission(PluginPermissions.ConfigRead)]
[assembly: PluginPermission(PluginPermissions.Network)]

namespace Plugins.WebSearchTools;

/// <summary>
/// Web 检索服务接口：定义搜索和网页抓取的标准契约。
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    /// 执行搜索查询。
    /// </summary>
    Task<List<WebSearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default
    );

    /// <summary>
    /// 获取并摘要指定 URL 的内容。
    /// </summary>
    Task<string> FetchAndSummarizeAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Web 搜索结果实体。
/// </summary>
public sealed record WebSearchResult
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Snippet { get; init; } = "";
}

/// <summary>
/// 基于 Bing Search API 的搜索实现，支持速率限制。
/// </summary>
public sealed partial class BingSearchProvider(string apiKey, double rateLimitPerSecond = 1.0)
    : IWebSearchProvider
{
    // HTTP 客户端 — 设置 30 秒超时防止请求无限挂起
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // 最小请求间隔
    private readonly TimeSpan _minInterval = TimeSpan.FromSeconds(1.0 / rateLimitPerSecond);

    // 速率限制信号量
    private readonly SemaphoreSlim _rateGate = new(1, 1);

    // 使用 Stopwatch 替代 DateTime.UtcNow，避免系统时钟调整导致的问题
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _lastRequestTicks;

    public async Task<List<WebSearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default
    )
    {
        // 如果没有 API Key，构造 Bing 搜索 URL 供 LLM 通过 web_read 工具读取
        if (string.IsNullOrEmpty(apiKey))
        {
            var searchUrl = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";
            return
            [
                new WebSearchResult
                {
                    Title = $"搜索: {query}",
                    Snippet =
                        "未配置 Bing API Key，已构造搜索链接。请使用 web_read 工具读取以下 URL 获取结果。",
                    Url = searchUrl,
                },
            ];
        }

        // ── 速率限制（使用 Stopwatch） ──
        await _rateGate.WaitAsync(ct);
        try
        {
            var elapsedTicks = _stopwatch.ElapsedTicks - _lastRequestTicks;
            var elapsed = TimeSpan.FromTicks(elapsedTicks);
            if (elapsed < _minInterval)
            {
                await Task.Delay(_minInterval - elapsed, ct);
            }
            _lastRequestTicks = _stopwatch.ElapsedTicks;
        }
        finally
        {
            _rateGate.Release();
        }

        try
        {
            // 构建 Bing Search API 请求
            var url =
                $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count={maxResults}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);

            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            // 解析 JSON 响应
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var results = new List<WebSearchResult>();

            if (
                json.TryGetProperty("webPages", out var webPages)
                && webPages.TryGetProperty("value", out var value)
            )
            {
                results.AddRange(
                    value
                        .EnumerateArray()
                        .Select(item => new WebSearchResult
                        {
                            Title = item.TryGetProperty("name", out var t)
                                ? t.GetString() ?? ""
                                : "",
                            Url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                            Snippet = item.TryGetProperty("snippet", out var s)
                                ? s.GetString() ?? ""
                                : "",
                        })
                );
            }
            return results;
        }
        catch (Exception ex)
        {
            return [new WebSearchResult { Title = "搜索失败", Snippet = ex.Message }];
        }
    }

    public async Task<string> FetchAndSummarizeAsync(string url, CancellationToken ct = default)
    {
        // SSRF 防护：阻止内网地址和危险 URL
        if (!IsSafeUrl(url))
            return "获取失败: URL 不安全或指向内部网络地址";

        try
        {
            // 流式抓取：ResponseHeadersRead 拿到响应头即返回，逐块读取并尽早断开。
            // 只保留前 512KB 原始 HTML（足够覆盖 2000 字符正文），
            // 大页面不再整块驻留内存（原 GetStringAsync 会缓冲整个响应）。
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var sb = new StringBuilder(capacity: 16 * 1024);
            var buffer = new char[8192];
            const int maxRawChars = 512 * 1024;

            while (sb.Length < maxRawChars)
            {
                var toRead = Math.Min(buffer.Length, maxRawChars - sb.Length);
                var charsRead = await reader.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (charsRead == 0)
                    break;
                sb.Append(buffer, 0, charsRead);
            }

            // 使用预编译 Regex 替代运行时编译，避免每次调用的编译开销
            var text = HtmlTagRegex().Replace(sb.ToString(), " ");
            text = WhitespaceRegex().Replace(text, " ").Trim();
            return text.Truncate(2000);
        }
        catch (Exception ex)
        {
            return $"获取失败: {ex.Message}";
        }
    }

    /// <summary>
    /// SSRF 防护：检查 URL 是否指向安全的公网地址。
    /// 阻止内网地址（localhost/127.0.0.1/10.x/192.168.x/169.254.x/::1）。
    /// </summary>
    private static bool IsSafeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;
        // 阻止回环地址
        if (uri.Host is "localhost" or "127.0.0.1" or "0.0.0.0" or "::1")
            return false;
        // 阻止私有网络和链路本地地址
        if (
            uri.Host.StartsWith("10.")
            || uri.Host.StartsWith("192.168.")
            || uri.Host.StartsWith("169.254.")
        )
            return false;
        // 阻止 172.16.0.0/12 私有网络
        if (uri.Host.StartsWith("172."))
        {
            var parts = uri.Host.Split('.');
            if (
                parts.Length >= 2
                && int.TryParse(parts[1], out var second)
                && second is >= 16 and <= 31
            )
                return false;
        }
        return true;
    }

    /// <summary>预编译 HTML 标签正则（源生成器）。</summary>
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    /// <summary>预编译空白字符正则（源生成器）。</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>
/// Web 检索插件：提供网络搜索、网页读取、文学评论搜索等功能。
/// </summary>
public sealed class WebSearchToolsPlugin : IToolPlugin
{
    // Web 搜索提供器
    private IWebSearchProvider _searchProvider = null!;

    /// <summary>
    /// 初始化插件，从配置中读取 Bing API Key 并创建搜索提供器。
    /// </summary>
    public void Initialize(IServiceProvider sp)
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var apiKey = config["WebSearch:BingApiKey"] ?? "";
        _searchProvider = new BingSearchProvider(apiKey);
    }

    /// <summary>
    /// 获取插件提供的所有工具列表。
    /// </summary>
    public IEnumerable<AgentTool> GetTools()
    {
        yield return new WebSearchTool(_searchProvider);
        yield return new BatchWebSearchTool(_searchProvider);
        yield return new WebReadTool(_searchProvider);
        yield return new SearchLiteraryCriticismTool(_searchProvider);
        yield return new LookupTermTool(_searchProvider);
        yield return new RecommendSimilarStyleTool(_searchProvider);
    }
}

// ── 网络搜索工具 ──

/// <summary>
/// 网络搜索工具：搜索互联网（历史、地理、专业术语），返回摘要列表。
/// </summary>
public sealed class WebSearchTool(IWebSearchProvider provider)
    : AgentTool<WebSearchParams, WebSearchDetails>
{
    public override string Name => "web_search";
    public override string Label => "网络搜索";
    public override string Description => "搜索互联网（历史、地理、专业术语），返回摘要列表";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<WebSearchDetails>> ExecuteAsync(
        string toolCallId,
        WebSearchParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebSearchDetails>>? onUpdate = null
    )
    {
        var results = await provider.SearchAsync(args.Query, args.MaxResults, ct);
        ThrowIfCancelled(ct);
        var lines = results.Select(r => $"- {r.Title}\n  {r.Url}\n  {r.Snippet}");
        return new AgentToolResult<WebSearchDetails>
        {
            Content = [new TextContent(string.Join("\n\n", lines))],
            Details = new WebSearchDetails { Count = results.Count },
        };
    }
}

/// <summary>
/// 网络搜索工具的参数。
/// </summary>
public sealed record WebSearchParams
{
    [Description("搜索关键词")]
    public string Query { get; init; } = "";

    [Description("最大返回结果数量（默认 5）")]
    public int MaxResults { get; init; } = 5;
}

/// <summary>
/// 网络搜索工具的返回详情。
/// </summary>
public sealed record WebSearchDetails
{
    public int Count { get; init; }
}

// ── 读取网页工具 ──

/// <summary>
/// 读取网页工具：获取并摘要指定 URL 的内容。
/// </summary>
public sealed class WebReadTool(IWebSearchProvider provider)
    : AgentTool<WebReadParams, WebReadDetails>
{
    public override string Name => "web_read";
    public override string Label => "读取网页";
    public override string Description => "获取并摘要指定 URL 的内容";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<WebReadDetails>> ExecuteAsync(
        string toolCallId,
        WebReadParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebReadDetails>>? onUpdate = null
    )
    {
        var content = await provider.FetchAndSummarizeAsync(args.Url, ct);
        ThrowIfCancelled(ct);
        return new AgentToolResult<WebReadDetails>
        {
            Content = [new TextContent(content)],
            Details = new WebReadDetails { Url = args.Url, Length = content.Length },
        };
    }
}

/// <summary>
/// 读取网页工具的参数。
/// </summary>
public sealed record WebReadParams
{
    [Description("要读取的网页 URL")]
    public string Url { get; init; } = "";
}

/// <summary>
/// 读取网页工具的返回详情。
/// </summary>
public sealed record WebReadDetails
{
    public string Url { get; init; } = "";
    public int Length { get; init; }
}

// ── 搜索文学评论工具 ──

/// <summary>
/// 搜索文学评论工具：搜索特定作品的评论/分析，获取灵感。
/// </summary>
public sealed class SearchLiteraryCriticismTool(IWebSearchProvider provider)
    : AgentTool<SearchLiteraryCriticismParams, WebSearchDetails>
{
    public override string Name => "search_literary_criticism";
    public override string Label => "搜索文学评论";
    public override string Description => "搜索特定作品的评论/分析，获取灵感";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<WebSearchDetails>> ExecuteAsync(
        string toolCallId,
        SearchLiteraryCriticismParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebSearchDetails>>? onUpdate = null
    )
    {
        var query = $"{args.WorkName} 文学评论 分析";
        var results = await provider.SearchAsync(query, 5, ct);
        ThrowIfCancelled(ct);
        var lines = results.Select(r => $"- {r.Title}\n  {r.Snippet}");
        return new AgentToolResult<WebSearchDetails>
        {
            Content = [new TextContent(string.Join("\n\n", lines))],
            Details = new WebSearchDetails { Count = results.Count },
        };
    }
}

/// <summary>
/// 搜索文学评论工具的参数。
/// </summary>
public sealed record SearchLiteraryCriticismParams
{
    [Description("要搜索评论的作品名称")]
    public string WorkName { get; init; } = "";
}

// ── 查询词语工具 ──

/// <summary>
/// 查询词语工具：查询词语释义、俚语、成语。
/// </summary>
public sealed class LookupTermTool(IWebSearchProvider provider)
    : AgentTool<LookupTermParams, WebSearchDetails>
{
    public override string Name => "lookup_term";
    public override string Label => "查询词语";
    public override string Description => "查询词语释义、俚语、成语";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<WebSearchDetails>> ExecuteAsync(
        string toolCallId,
        LookupTermParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebSearchDetails>>? onUpdate = null
    )
    {
        var query = $"{args.Term} 释义 含义";
        var results = await provider.SearchAsync(query, 3, ct);
        ThrowIfCancelled(ct);
        var lines = results.Select(r => $"- {r.Title}: {r.Snippet}");
        return new AgentToolResult<WebSearchDetails>
        {
            Content = [new TextContent(string.Join("\n", lines))],
            Details = new WebSearchDetails { Count = results.Count },
        };
    }
}

/// <summary>
/// 查询词语工具的参数。
/// </summary>
public sealed record LookupTermParams
{
    [Description("要查询的词语/术语")]
    public string Term { get; init; } = "";
}

// ── 推荐风格相近作品工具 ──

/// <summary>
/// 推荐风格相近作品工具：基于当前片段推荐风格相近的文学作品。
/// </summary>
public sealed class RecommendSimilarStyleTool(IWebSearchProvider provider)
    : AgentTool<RecommendSimilarStyleParams, WebSearchDetails>
{
    public override string Name => "recommend_similar_style";
    public override string Label => "推荐风格相近作品";
    public override string Description => "基于当前片段推荐风格相近的文学作品";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<WebSearchDetails>> ExecuteAsync(
        string toolCallId,
        RecommendSimilarStyleParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebSearchDetails>>? onUpdate = null
    )
    {
        var query = $"类似风格小说推荐 {args.StyleDescription}";
        var results = await provider.SearchAsync(query, 5, ct);
        ThrowIfCancelled(ct);
        var lines = results.Select(r => $"- {r.Title}\n  {r.Snippet}");
        return new AgentToolResult<WebSearchDetails>
        {
            Content = [new TextContent(string.Join("\n\n", lines))],
            Details = new WebSearchDetails { Count = results.Count },
        };
    }
}

/// <summary>
/// 推荐风格相近作品工具的参数。
/// </summary>
public sealed record RecommendSimilarStyleParams
{
    [Description("风格描述，如「黑暗奇幻」「赛博朋克」「中国古典仙侠」")]
    public string StyleDescription { get; init; } = "";
}
