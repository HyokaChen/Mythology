using System.Text.Json;
using Core.Data;
using Microsoft.Extensions.Logging;

namespace Core.Mcp;

/// <summary>
/// 外部 MCP 服务器连接 — 管理一条 stdio 子进程或一个 HTTP 端点的 JSON-RPC 2.0 会话。
/// 负责：协议握手（initialize）、工具发现（tools/list）、工具调用（tools/call）。
/// 传输层通过 <see cref="IJsonRpcTransport"/> 抽象，实现在 <see cref="StdioTransport"/> 与 <see cref="HttpTransport"/>。
/// </summary>
public sealed class ExternalMcpConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Envelope = new(JsonSerializerDefaults.Web);

    private readonly string _serverName;
    private readonly IJsonRpcTransport _transport;
    private readonly ILogger? _logger;
    private int _nextId;

    /// <summary>服务端协议版本（协商后）。</summary>
    public string? ProtocolVersion { get; private set; }

    /// <summary>是否已初始化。</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>服务器名称。</summary>
    public string ServerName => _serverName;

    public ExternalMcpConnection(
        string serverName,
        IJsonRpcTransport transport,
        ILogger? logger = null
    )
    {
        _serverName = serverName;
        _transport = transport;
        _logger = logger;
    }

    /// <summary>根据 McpServerEntry 创建连接实例。</summary>
    public static ExternalMcpConnection Create(
        McpServerEntry entry,
        ILogger? logger = null,
        HttpClient? httpClient = null
    )
    {
        var transport = entry.Transport switch
        {
            "stdio" => (IJsonRpcTransport)new StdioTransport(entry.Command, entry.Args, logger),
            "http" or "streamable-http" or "sse" => CreateHttpTransport(entry, httpClient, logger),
            _ => throw new ArgumentException($"未知传输类型: {entry.Transport}"),
        };
        return new ExternalMcpConnection(entry.Name, transport, logger);
    }

    /// <summary>HTTP 传输：按服务器条目的鉴权配置装配 Authorization / 自定义头。</summary>
    private static HttpTransport CreateHttpTransport(
        McpServerEntry entry,
        HttpClient? httpClient,
        ILogger? logger
    )
    {
        var (header, value) = HttpTransport.ResolveAuth(entry);
        return new HttpTransport(entry.Url ?? "", httpClient, logger, header, value);
    }

    /// <summary>协议握手（initialize）。协商失败抛 InvalidOperationException。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var responseJson = await SendCoreAsync(
            "initialize",
            new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { } },
                clientInfo = new { name = "mythology-client", version = "0.1.0" },
            },
            ct
        );

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl))
        {
            throw new InvalidOperationException(
                $"MCP 初始化失败（{_serverName}）：{GetErrorCode(errorEl)} {GetErrorMessage(errorEl)}"
            );
        }

        var result = root.GetProperty("result");
        var requested = result.TryGetProperty("protocolVersion", out var pv)
            ? pv.GetString() ?? ""
            : "";
        var negotiated = requested is "2025-06-18" or "2025-03-26" or "2024-11-05"
            ? requested
            : "2025-06-18";
        ProtocolVersion = negotiated;
        IsInitialized = true;
        _logger?.LogInformation(
            "MCP 连接初始化完成: {Server} 协议 {Protocol}",
            _serverName,
            negotiated
        );
    }

    /// <summary>获取工具列表（tools/list）。</summary>
    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
        CancellationToken ct = default
    )
    {
        if (!IsInitialized)
            await InitializeAsync(ct);

        var responseJson = await SendCoreAsync("tools/list", null, ct);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl))
        {
            _logger?.LogWarning(
                "MCP tools/list 失败（{Server}）：{Code} {Msg}",
                _serverName,
                GetErrorCode(errorEl),
                GetErrorMessage(errorEl)
            );
            return [];
        }

        if (
            !root.TryGetProperty("result", out var resultEl)
            || !resultEl.TryGetProperty("tools", out var toolsEl)
            || toolsEl.ValueKind != JsonValueKind.Array
        )
            return [];

        var list = new List<McpToolDescriptor>();
        foreach (var tool in toolsEl.EnumerateArray())
        {
            list.Add(
                new McpToolDescriptor(
                    tool.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    tool.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    tool.TryGetProperty("inputSchema", out var s) ? s.Clone() : null
                )
            );
        }
        return list;
    }

    /// <summary>调用工具（tools/call）。</summary>
    public async Task<McpToolResult> CallToolAsync(
        string toolName,
        JsonElement? arguments = null,
        CancellationToken ct = default
    )
    {
        var responseJson = await SendCoreAsync(
            "tools/call",
            new
            {
                name = toolName,
                arguments = arguments?.ValueKind == JsonValueKind.Object ? arguments : null,
            },
            ct
        );

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl))
        {
            return new McpToolResult(
                IsError: true,
                Text: $"MCP 错误 {GetErrorCode(errorEl)}：{GetErrorMessage(errorEl)}"
            );
        }

        if (!root.TryGetProperty("result", out var resultEl))
            return new McpToolResult(true, "MCP 响应缺少 result 字段");

        var isError =
            resultEl.TryGetProperty("isError", out var errEl)
            && errEl.ValueKind == JsonValueKind.True;
        var text = ExtractContentText(resultEl);

        return new McpToolResult(isError, text ?? resultEl.GetRawText());
    }

    /// <summary>释放连接。</summary>
    public async ValueTask DisposeAsync()
    {
        await _transport.DisconnectAsync();
    }

    // ══════════════════════════════════════════════════════════
    // JSON-RPC 消息发送
    // ══════════════════════════════════════════════════════════

    private async Task<string> SendCoreAsync(string method, object? @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null)
            request["params"] = @params;

        var requestJson = JsonSerializer.Serialize(request, Envelope);
        return await _transport.SendAsync(requestJson, ct);
    }

    // ══════════════════════════════════════════════════════════
    // content 提取
    // ══════════════════════════════════════════════════════════

    private static string? ExtractContentText(JsonElement resultEl)
    {
        if (
            resultEl.ValueKind != JsonValueKind.Object
            || !resultEl.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() == 0
        )
            return null;

        var texts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (
                item.TryGetProperty("type", out var typeEl)
                && typeEl.GetString() == "text"
                && item.TryGetProperty("text", out var textEl)
                && textEl.ValueKind == JsonValueKind.String
            )
            {
                texts.Add(textEl.GetString()!);
            }
        }
        return texts.Count > 0 ? string.Join("\n", texts) : null;
    }

    private static int GetErrorCode(JsonElement errorEl) =>
        errorEl.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32()
            : -1;

    private static string GetErrorMessage(JsonElement errorEl) =>
        errorEl.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "unknown error"
            : "unknown error";
}

/// <summary>MCP 工具描述（tools/list 返回）。</summary>
public sealed record McpToolDescriptor(string Name, string Description, JsonElement? InputSchema);

/// <summary>MCP 工具调用结果。</summary>
public sealed record McpToolResult(bool IsError, string Text);
