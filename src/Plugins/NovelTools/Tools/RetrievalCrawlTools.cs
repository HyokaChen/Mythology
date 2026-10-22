using ClientPlatform;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 云端站点爬取 — 经 /v1/retrieval/crawl 启动异步任务（限速/去重/重试；每成功页计费）。
/// 提交即预留 limit×RetrievalUnitCost 积分，后台按成功页结算、结束释放余量；
/// 用 web_crawl_status 轮询进度，web_crawl_cancel 取消。
/// </summary>
public sealed class WebCrawlTool(CrawlRestClient? crawl = null)
    : AgentTool<WebCrawlParams, WebCrawlDetails>
{
    public override string Name => "web_crawl";
    public override string Label => "站点爬取任务";
    public override string Description =>
        "启动站点异步爬取任务（每成功页计费；返回 jobId，用 web_crawl_status 轮询进度、web_crawl_cancel 取消）。"
        + "import=true 需云端知识库可用，否则提交被拒。";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<WebCrawlDetails>> ExecuteAsync(
        string toolCallId,
        WebCrawlParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebCrawlDetails>>? onUpdate = null
    )
    {
        if (crawl is null)
        {
            return Error("云端爬取客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.Url))
        {
            return Error("url 参数必填。");
        }

        try
        {
            onUpdate?.Report(Progress("正在提交爬取任务…"));
            var start = await crawl.StartAsync(
                new
                {
                    url = args.Url,
                    limit = Math.Clamp(args.Limit ?? 50, 1, 1000),
                    maxDepth = Math.Clamp(args.MaxDepth ?? 2, 0, 10),
                    includePaths = args.IncludePaths,
                    excludePaths = args.ExcludePaths,
                    import = args.Import ?? false,
                    category = args.Category,
                },
                idempotencyKey: $"tool-crawl-{Guid.NewGuid():N}",
                ct
            );
            var message =
                $"爬取任务已启动：jobId={start.JobId}，状态 {start.Status}，预留 {start.ReservedUnits} 积分"
                + "（按成功页结算，余量自动退回）。用 web_crawl_status 轮询进度。";
            return new AgentToolResult<WebCrawlDetails>
            {
                Content = [new TextContent(message)],
                Details = new WebCrawlDetails
                {
                    JobId = start.JobId,
                    Status = start.Status,
                    ReservedUnits = start.ReservedUnits,
                },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"爬取任务启动失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("爬取任务启动"));
        }
        catch (Exception ex)
        {
            return Error($"爬取任务启动失败：{ex.Message}");
        }
    }

    private static AgentToolResult<WebCrawlDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new WebCrawlDetails(),
        };

    private static AgentToolResult<WebCrawlDetails> Progress(string message) =>
        new() { Content = [new TextContent(message)], Details = new WebCrawlDetails() };
}

/// <summary>
/// 爬取任务进度查询 — 经 /v1/retrieval/crawl/{jobId} 读取快照（零价）：
/// status/completed/total/robotsBlocked/chargedUnits/errors 与分页 pages。
/// </summary>
public sealed class WebCrawlStatusTool(CrawlRestClient? crawl = null)
    : AgentTool<WebCrawlStatusParams, WebCrawlStatusDetails>
{
    public override string Name => "web_crawl_status";
    public override string Label => "爬取进度";
    public override string Description =>
        "查询爬取任务进度（零价）：status（queued/running/completed/failed/cancelled）、完成页数、"
        + "已结算积分、错误与页面列表；include_markdown=true 时页面携带 Markdown 正文。";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<WebCrawlStatusDetails>> ExecuteAsync(
        string toolCallId,
        WebCrawlStatusParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebCrawlStatusDetails>>? onUpdate = null
    )
    {
        if (crawl is null)
        {
            return Error("云端爬取客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.JobId))
        {
            return Error("jobId 参数必填。");
        }

        try
        {
            var json = await crawl.GetSnapshotAsync(
                args.JobId,
                includeMarkdown: args.IncludeMarkdown ?? false,
                ct
            );
            return new AgentToolResult<WebCrawlStatusDetails>
            {
                Content = [new TextContent(json)],
                Details = new WebCrawlStatusDetails { Json = json },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"爬取进度查询失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("爬取进度查询"));
        }
        catch (Exception ex)
        {
            return Error($"爬取进度查询失败：{ex.Message}");
        }
    }

    private static AgentToolResult<WebCrawlStatusDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new WebCrawlStatusDetails(),
        };
}

/// <summary>
/// 爬取任务取消 — 经 DELETE /v1/retrieval/crawl/{jobId}（202；后台按已成功页数结算后进入 cancelled 终态）。
/// </summary>
public sealed class WebCrawlCancelTool(CrawlRestClient? crawl = null)
    : AgentTool<WebCrawlCancelParams, WebCrawlCancelDetails>
{
    public override string Name => "web_crawl_cancel";
    public override string Label => "取消爬取";
    public override string Description => "取消一个进行中的爬取任务（按已成功页数结算，余量退回）";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<WebCrawlCancelDetails>> ExecuteAsync(
        string toolCallId,
        WebCrawlCancelParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WebCrawlCancelDetails>>? onUpdate = null
    )
    {
        if (crawl is null)
        {
            return Error("云端爬取客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.JobId))
        {
            return Error("jobId 参数必填。");
        }

        try
        {
            await crawl.CancelAsync(args.JobId, ct);
            return new AgentToolResult<WebCrawlCancelDetails>
            {
                Content =
                [
                    new TextContent(
                        $"已请求取消爬取任务 {args.JobId}（按已成功页数结算，余量退回）。"
                    ),
                ],
                Details = new WebCrawlCancelDetails { JobId = args.JobId },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"取消爬取任务失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("取消爬取任务"));
        }
        catch (Exception ex)
        {
            return Error($"取消爬取任务失败：{ex.Message}");
        }
    }

    private static AgentToolResult<WebCrawlCancelDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new WebCrawlCancelDetails(),
        };
}

public sealed record WebCrawlParams
{
    public string? Url { get; init; }
    public int? Limit { get; init; }
    public int? MaxDepth { get; init; }
    public string[]? IncludePaths { get; init; }
    public string[]? ExcludePaths { get; init; }
    public bool? Import { get; init; }
    public string? Category { get; init; }
}

public sealed record WebCrawlDetails
{
    public string JobId { get; init; } = "";
    public string Status { get; init; } = "";
    public int ReservedUnits { get; init; }
}

public sealed record WebCrawlStatusParams
{
    public string? JobId { get; init; }
    public bool? IncludeMarkdown { get; init; }
}

public sealed record WebCrawlStatusDetails
{
    public string Json { get; init; } = "";
}

public sealed record WebCrawlCancelParams
{
    public string? JobId { get; init; }
}

public sealed record WebCrawlCancelDetails
{
    public string JobId { get; init; } = "";
}
