using System.Net;
using System.Text;
using Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// UpdateCheckService 契约测试（HttpMessageHandler 桩，仓库既有私有嵌套类范式）：
/// releases/latest 响应解析、版本/资产匹配决策、下载落盘与失败清理（不留 .part）。
/// </summary>
public sealed class UpdateCheckServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"update-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言
        }
    }

    [Fact]
    public async Task CheckAsync_NewerReleaseWithMatchingAsset_ReportsUpdate()
    {
        var rid = UpdateAssetMap.CurrentRid();
        if (rid is null)
            Assert.Skip("当前 RID 不在支持列表（无法构造匹配资产）");

        var installerName = UpdateAssetMap.InstallerNameFor(rid!, "0.0.3")!;
        var handler = new StubHandler(_ =>
            Json(
                $$"""
                {
                  "tag_name": "v0.0.3",
                  "published_at": "2026-10-12T07:33:00Z",
                  "body": "release notes body",
                  "assets": [
                    { "name": "{{installerName}}", "browser_download_url": "https://github.com/HyokaChen/Mythology/releases/download/v0.0.3/{{installerName}}", "size": 12345678 },
                    { "name": "{{installerName}}.sha256", "browser_download_url": "https://github.com/HyokaChen/Mythology/releases/download/v0.0.3/{{installerName}}.sha256", "size": 100 }
                  ]
                }
                """
            )
        );
        var service = CreateService(handler);

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.HasUpdate);
        Assert.Equal("v0.0.3", result.TagName);
        Assert.Equal("0.0.3", result.LatestVersion);
        Assert.Equal("0.0.1", result.CurrentVersion);
        Assert.Equal("release notes body", result.ReleaseNotes);
        Assert.NotNull(result.PublishedAt);
        Assert.Equal(installerName, result.InstallerAssetName);
        Assert.NotNull(result.InstallerDownloadUrl);
        Assert.Contains(
            "/download/v0.0.3/",
            result.InstallerDownloadUrl!,
            StringComparison.Ordinal
        );
        Assert.Equal(12_345_678L, result.InstallerSizeBytes);
        Assert.Equal(installerName + ".sha256", result.ChecksumAssetName);
        Assert.NotNull(result.ChecksumDownloadUrl);
        Assert.Equal(
            "https://api.github.com/repos/HyokaChen/Mythology/releases/latest",
            handler.LastRequestUri!.ToString()
        );
    }

    [Fact]
    public async Task CheckAsync_SameVersion_NoUpdate()
    {
        var handler = new StubHandler(_ => Json("""{"tag_name":"v0.0.3","assets":[]}"""));
        var service = CreateService(handler, currentVersion: "0.0.3+abc");

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.HasUpdate);
        Assert.Equal("0.0.3", result.LatestVersion); // 供设置页显示「已是最新」
    }

    [Fact]
    public async Task CheckAsync_OlderRelease_NoUpdate()
    {
        var handler = new StubHandler(_ => Json("""{"tag_name":"v0.0.2"}"""));
        var service = CreateService(handler, currentVersion: "0.0.3");

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckAsync_CurrentVersionMissing_SkipsWithoutRequest()
    {
        var handler = new StubHandler(_ => Json("""{"tag_name":"v9.9.9"}"""));
        var service = CreateService(handler, currentVersion: null);

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.HasUpdate);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CheckAsync_NoAssetForCurrentRid_NoUpdate()
    {
        var handler = new StubHandler(_ =>
            Json(
                """
                {"tag_name":"v0.0.3","assets":[{"name":"Mythology-Desktop-0.0.3-nosuchrid.bin","browser_download_url":"https://example.invalid/x","size":1}]}
                """
            )
        );
        var service = CreateService(handler);

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckAsync_NotFound_NoUpdateNoThrow()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateService(handler);

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckAsync_MalformedJson_NoUpdate()
    {
        var handler = new StubHandler(_ => Json("{ not-json"));
        var service = CreateService(handler);

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_NoUpdateNoThrow()
    {
        // 断网 / 连接被拒（受限网络）：静默按「无更新」处理，不抛、不弹
        var handler = new StubHandler(_ => throw new HttpRequestException("unreachable"));
        var service = CreateService(handler);

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.HasUpdate);
        Assert.Equal(string.Empty, result.LatestVersion);
    }

    [Fact]
    public async Task DownloadAsync_WritesFileAndReportsMonotonicProgress()
    {
        var payload = new byte[1024];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        var handler = new StubHandler(_ =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = payload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var service = CreateService(handler);
        var reports = new List<double>();

        var path = await service.DownloadAsync(
            "https://github.com/HyokaChen/Mythology/releases/download/v0.0.3/setup.exe",
            "setup.exe",
            new DirectProgress(reports.Add),
            expectedSizeBytes: payload.Length,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(path);
        Assert.Equal(Path.Combine(_dir, "setup.exe"), path);
        Assert.Equal(
            payload,
            await File.ReadAllBytesAsync(path!, TestContext.Current.CancellationToken)
        );
        Assert.NotEmpty(reports);
        Assert.Equal(100, reports[^1]);
        Assert.True(reports.SequenceEqual(reports.OrderBy(v => v))); // 单调不减
    }

    [Fact]
    public async Task DownloadAsync_HttpError_ReturnsNullWithoutPartFile()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError
        ));
        var service = CreateService(handler);

        var path = await service.DownloadAsync(
            "https://github.com/HyokaChen/Mythology/releases/download/v0.0.3/setup.exe",
            "setup.exe",
            progress: null,
            expectedSizeBytes: 0,
            TestContext.Current.CancellationToken
        );

        Assert.Null(path);
        Assert.False(File.Exists(Path.Combine(_dir, "setup.exe")));
        Assert.False(File.Exists(Path.Combine(_dir, "setup.exe.part")));
    }

    private UpdateCheckService CreateService(
        StubHandler handler,
        string? currentVersion = "0.0.1"
    ) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            NullLogger<UpdateCheckService>.Instance,
            _dir,
            () => currentVersion
        );

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>直通进度回调（Progress&lt;T&gt; 会依赖同步上下文投递，测试里必须同步收集）。</summary>
    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
