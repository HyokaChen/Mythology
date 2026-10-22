using System.Text.Json;
using ClientPlatform;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 云端网页检索 — 通过云端 Retrieval REST（Draco.Sharp 引擎）抓取网页为 Markdown。
/// 走 /v1/retrieval/scrape，每次调用经三段式计费扣 RetrievalUnitCost 积分（CloudOptions）。
/// 云端不可用/未登录时返回可读错误，不阻塞本地创作。
/// </summary>
public sealed class WebScrapeTool(RetrievalRestClient? rest = null)
    : AgentTool<WebScrapeParams, WebScrapeDetails>
{
    public override string Name => "web_scrape";
    public override string Label => "网页抓取";
    public override string Description =>
        "抓取网页为 Markdown/HTML/JSON（云端 Retrieval 引擎，消耗积分）";
    public override ToolCategory Category => ToolCategory.EntityRead;

    private static readonly string[] arguments = new[] { "markdown" };

    public override async Task<AgentToolResult<WebScrapeDetails>> ExecuteAsync(
        string toolCallId,
        WebScrapeParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebScrapeDetails>>? onUpdate = null
    )
    {
        if (rest is null)
        {
            return Error("云端检索客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.Url))
        {
            return Error("url 参数必填。");
        }

        try
        {
            onUpdate?.Report(Progress("正在抓取网页…"));
            var json = await rest.ScrapeAsync(
                new
                {
                    url = args.Url,
                    formats = arguments,
                    onlyMainContent = true,
                    timeout = args.TimeoutMs ?? 30000,
                },
                ct
            );
            return new AgentToolResult<WebScrapeDetails>
            {
                Content = [new TextContent(json)],
                Details = new WebScrapeDetails { Markdown = json },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"网页抓取失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("网页抓取"));
        }
        catch (Exception ex)
        {
            return Error($"网页抓取失败：{ex.Message}");
        }
    }

    private static AgentToolResult<WebScrapeDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new WebScrapeDetails(),
        };

    private static AgentToolResult<WebScrapeDetails> Progress(string message) =>
        new() { Content = [new TextContent(message)], Details = new WebScrapeDetails() };
}

/// <summary>
/// 云端网页搜索 — 通过云端 Retrieval REST 多引擎搜索（RRF 共识融合）。
/// 走 /v1/retrieval/search，每次调用经三段式计费扣 RetrievalUnitCost 积分。
/// </summary>
public sealed class WebSearchTool(RetrievalRestClient? rest = null)
    : AgentTool<WebSearchParams, WebSearchDetails>
{
    public override string Name => "web_search";
    public override string Label => "网页搜索";
    public override string Description => "多引擎搜索（云端 Retrieval，消耗积分）";
    public override ToolCategory Category => ToolCategory.EntityRead;

    public override async Task<AgentToolResult<WebSearchDetails>> ExecuteAsync(
        string toolCallId,
        WebSearchParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebSearchDetails>>? onUpdate = null
    )
    {
        if (rest is null)
        {
            return Error("云端检索客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.Query))
        {
            return Error("query 参数必填。");
        }

        try
        {
            onUpdate?.Report(Progress("正在搜索…"));
            var json = await rest.SearchAsync(
                new { query = args.Query, limit = Math.Clamp(args.Limit ?? 5, 1, 20) },
                ct
            );
            return new AgentToolResult<WebSearchDetails>
            {
                Content = [new TextContent(json)],
                Details = new WebSearchDetails { Json = json },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"搜索失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("搜索"));
        }
        catch (Exception ex)
        {
            return Error($"搜索失败：{ex.Message}");
        }
    }

    private static AgentToolResult<WebSearchDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new WebSearchDetails(),
        };

    private static AgentToolResult<WebSearchDetails> Progress(string message) =>
        new() { Content = [new TextContent(message)], Details = new WebSearchDetails() };
}

/// <summary>
/// 云端站点 URL 发现 — 经 /v1/retrieval/map 读取 sitemap 与页面内链接列出站内 URL。
/// 每次调用经三段式计费扣 RetrievalUnitCost 积分（失败请求不扣费）。
/// </summary>
public sealed class WebMapTool(RetrievalRestClient? rest = null)
    : AgentTool<WebMapParams, WebMapDetails>
{
    public override string Name => "web_map";
    public override string Label => "站点 URL 发现";
    public override string Description =>
        "列出一个站点的全部 URL（sitemap + 页面内链接；云端 Retrieval，消耗积分）";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<WebMapDetails>> ExecuteAsync(
        string toolCallId,
        WebMapParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebMapDetails>>? onUpdate = null
    )
    {
        if (rest is null)
        {
            return Error("云端检索客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.Url))
        {
            return Error("url 参数必填。");
        }

        try
        {
            onUpdate?.Report(Progress("正在发现站点 URL…"));
            var json = await rest.MapAsync(
                new { url = args.Url, limit = Math.Clamp(args.Limit ?? 5000, 1, 10000) },
                ct
            );
            return Ok(json);
        }
        catch (CloudApiException ex)
        {
            return Error($"站点 URL 发现失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("站点 URL 发现"));
        }
        catch (Exception ex)
        {
            return Error($"站点 URL 发现失败：{ex.Message}");
        }
    }

    private static AgentToolResult<WebMapDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new WebMapDetails(),
        };

    private static AgentToolResult<WebMapDetails> Ok(string json) =>
        new()
        {
            Content = [new TextContent(json)],
            Details = new WebMapDetails { Json = json },
        };

    private static AgentToolResult<WebMapDetails> Progress(string message) =>
        new() { Content = [new TextContent(message)], Details = new WebMapDetails() };
}

/// <summary>
/// 云端接口发现 — 经 /v1/retrieval/discover 捕获页面 JS 发起的 API 请求端点（tierMax=3 时经浏览器渲染）。
/// 每次调用经三段式计费扣 RetrievalUnitCost 积分（失败请求不扣费）。
/// </summary>
public sealed class WebDiscoverTool(RetrievalRestClient? rest = null)
    : AgentTool<WebDiscoverParams, WebDiscoverDetails>
{
    public override string Name => "web_discover";
    public override string Label => "接口发现";
    public override string Description =>
        "捕获一个页面 JS 发起的 API 请求端点（云端 Retrieval，消耗积分）";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<WebDiscoverDetails>> ExecuteAsync(
        string toolCallId,
        WebDiscoverParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebDiscoverDetails>>? onUpdate = null
    )
    {
        if (rest is null)
        {
            return Error("云端检索客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.Url))
        {
            return Error("url 参数必填。");
        }

        try
        {
            onUpdate?.Report(Progress("正在捕获页面 API 端点…"));
            var json = await rest.DiscoverAsync(
                new
                {
                    url = args.Url,
                    timeout = args.TimeoutMs ?? 30000,
                    captureWindowMs = args.CaptureWindowMs ?? 2000,
                },
                ct
            );
            return Ok(json);
        }
        catch (CloudApiException ex)
        {
            return Error($"接口发现失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("接口发现"));
        }
        catch (Exception ex)
        {
            return Error($"接口发现失败：{ex.Message}");
        }
    }

    private static AgentToolResult<WebDiscoverDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new WebDiscoverDetails(),
        };

    private static AgentToolResult<WebDiscoverDetails> Ok(string json) =>
        new()
        {
            Content = [new TextContent(json)],
            Details = new WebDiscoverDetails { Json = json },
        };

    private static AgentToolResult<WebDiscoverDetails> Progress(string message) =>
        new() { Content = [new TextContent(message)], Details = new WebDiscoverDetails() };
}

public sealed record WebScrapeParams
{
    public string? Url { get; init; }
    public int? TimeoutMs { get; init; }
}

public sealed record WebScrapeDetails
{
    public string Markdown { get; init; } = "";
}

public sealed record WebSearchParams
{
    public string? Query { get; init; }
    public int? Limit { get; init; }
}

public sealed record WebSearchDetails
{
    public string Json { get; init; } = "";
}

public sealed record WebMapParams
{
    public string? Url { get; init; }
    public int? Limit { get; init; }
}

public sealed record WebMapDetails
{
    public string Json { get; init; } = "";
}

public sealed record WebDiscoverParams
{
    public string? Url { get; init; }
    public int? TimeoutMs { get; init; }
    public int? CaptureWindowMs { get; init; }
}

public sealed record WebDiscoverDetails
{
    public string Json { get; init; } = "";
}
