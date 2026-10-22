using System.Net;
using ClientPlatform;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 云端工具的超时文案契约：transport 按请求 CTS 触发的超时（HttpClient 抛
/// <see cref="TaskCanceledException"/> 且调用方 token 未取消）必须给出可读中文（含单请求上限），
/// 不得把框架英文（<c>The operation was canceled.</c>）漏进对话流；
/// 调用方主动取消则必须重抛，不能伪装成超时。
/// </summary>
public sealed class CloudToolTimeoutCopyTests
{
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("The operation was canceled.", null, cancellationToken)
            );
    }

    private static WebScrapeTool CreateTool() =>
        new(
            new RetrievalRestClient(
                "http://localhost",
                () => "mk_test_public-secret",
                http: new HttpClient(new TimeoutHandler())
            )
        );

    [Fact]
    public async Task WebScrape_RequestTimeout_ReportsChineseTimeoutCopy()
    {
        var result = await CreateTool()
            .ExecuteAsync(
                "call-1",
                new WebScrapeParams { Url = "https://example.com" },
                CancellationToken.None
            );

        var text = ToolDispatcher.ExtractText(result.Content);
        Assert.True(result.IsError);
        Assert.Contains("60 秒", text, StringComparison.Ordinal);
        Assert.DoesNotContain("canceled", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebScrape_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateTool()
                .ExecuteAsync(
                    "call-2",
                    new WebScrapeParams { Url = "https://example.com" },
                    cts.Token
                )
        );
    }
}
