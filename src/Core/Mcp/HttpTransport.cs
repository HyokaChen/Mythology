using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Core.Data;
using Microsoft.Extensions.Logging;

namespace Core.Mcp;

/// <summary>
/// HTTP JSON-RPC 传输 — 通过 POST 请求发送 JSON-RPC 消息。
/// 支持 streamable-http（Accept: application/json, text/event-stream）；
/// 响应为 SSE 时取首个 data: 行的 JSON。
/// </summary>
public sealed class HttpTransport : IJsonRpcTransport, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _authHeader;
    private readonly string? _authValue;

    /// <summary>HTTP 传输始终认为已连接，除非 HttpClient 已释放。</summary>
    public bool IsConnected => !_disposed;

    private bool _disposed;

    /// <param name="endpoint">MCP 端点 URL。</param>
    /// <param name="http">可选 HttpClient（默认新建，60s 超时）。</param>
    /// <param name="logger">可选日志。</param>
    /// <param name="authHeader">鉴权头名（null/空 = 不带鉴权）。</param>
    /// <param name="authValue">鉴权头的值（Bearer 方式传 "Bearer &lt;token&gt;"）。</param>
    public HttpTransport(
        string endpoint,
        HttpClient? http = null,
        ILogger? logger = null,
        string? authHeader = null,
        string? authValue = null
    )
    {
        _endpoint = endpoint;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _authHeader = string.IsNullOrWhiteSpace(authHeader) ? null : authHeader.Trim();
        _authValue = authValue;
    }

    /// <summary>解析 MCP 服务器条目的鉴权配置（none/bearer/header → 头名 + 头值）。</summary>
    public static (string? Header, string? Value) ResolveAuth(McpServerEntry entry) =>
        entry.Auth?.Trim().ToLowerInvariant() switch
        {
            "bearer" when !string.IsNullOrWhiteSpace(entry.BearerToken) => (
                "Authorization",
                $"Bearer {entry.BearerToken.Trim()}"
            ),
            "header" when !string.IsNullOrWhiteSpace(entry.AuthHeader) => (
                entry.AuthHeader.Trim(),
                entry.AuthValue ?? ""
            ),
            _ => (null, null),
        };

    public async Task<string> SendAsync(string requestJson, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HttpTransport));

        var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = content,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_authHeader is not null)
            request.Headers.TryAddWithoutValidation(_authHeader, _authValue ?? "");

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        if (contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // SSE 响应：取首个 data: 行的 JSON 部分
            using var reader = new StreamReader(
                await response.Content.ReadAsStreamAsync(ct),
                Encoding.UTF8
            );
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    var json = line[6..].Trim();
                    if (!string.IsNullOrEmpty(json))
                        return json;
                }
            }
            throw new InvalidOperationException("MCP HTTP SSE 响应中未找到 data: 行");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    public Task DisconnectAsync()
    {
        _disposed = true;
        _http.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
