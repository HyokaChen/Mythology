using Core.Data;
using Microsoft.Extensions.Logging;

namespace Core.Mcp;

/// <summary>
/// 外部 MCP 桥接 — 根据配置连接启用的 MCP 服务器，把工具注册为 <see cref="ExternalMcpTool"/>。
/// 连接/断开触发 <see cref="ToolsChanged"/> 事件，宿主据此重建搜索索引。
/// </summary>
public sealed class ExternalMcpBridge : IAsyncDisposable
{
    private readonly List<ExternalMcpConnection> _connections = [];
    private readonly ILogger? _logger;
    private readonly Func<McpServerEntry, IJsonRpcTransport>? _transportFactory;

    /// <summary>工具清单变化事件（连接/断开后触发，宿主据此 InvalidateIndex + 重建）。</summary>
    public event Action? ToolsChanged;

    public ExternalMcpBridge(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>测试用构造：注入自定义传输工厂（替换默认 ExternalMcpConnection.Create）。</summary>
    internal ExternalMcpBridge(
        Func<McpServerEntry, IJsonRpcTransport> transportFactory,
        ILogger? logger = null
    )
    {
        _transportFactory = transportFactory;
        _logger = logger;
    }

    /// <summary>
    /// 连接已启用服务器并注册工具。
    /// 单台失败：记日志跳过（不阻断其他服务器，不抛）。全部失败返回空列表。
    /// </summary>
    /// <returns>注册到 Registry 的外部工具列表。</returns>
    public async Task<IReadOnlyList<ExternalMcpTool>> ConnectAsync(
        IReadOnlyList<McpServerEntry> servers,
        CancellationToken ct = default
    )
    {
        var allTools = new List<ExternalMcpTool>();
        // 已分配工具名集合：MCP 工具注册进同一全局 registry，重名会互相覆盖
        // （ToolRegistry.Register 按名覆盖），因此跨服务器做全局唯一分配。
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in servers)
        {
            if (!entry.Enabled)
                continue;

            try
            {
                var connection = _transportFactory is not null
                    ? new ExternalMcpConnection(entry.Name, _transportFactory(entry), _logger)
                    : ExternalMcpConnection.Create(entry, _logger);
                _connections.Add(connection);
                await connection.InitializeAsync(ct);
                var descriptors = await connection.ListToolsAsync(ct);

                foreach (var descriptor in descriptors)
                {
                    // 基础名 = mcp_{server}_{tool}（清洗后），冲突则追加 _2/_3…
                    var baseName = new ExternalMcpTool(connection, entry.Name, descriptor).Name;
                    var finalName = baseName;
                    if (!usedNames.Add(baseName))
                    {
                        var suffix = 2;
                        while (!usedNames.Add(baseName + "_" + suffix))
                            suffix++;
                        finalName = baseName + "_" + suffix;
                    }
                    allTools.Add(
                        new ExternalMcpTool(connection, entry.Name, descriptor, finalName)
                    );
                }
                _logger?.LogInformation(
                    "MCP 服务器已连接: {Server}，发现 {Count} 个工具",
                    entry.Name,
                    descriptors.Count
                );
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MCP 服务器连接失败: {Server}，跳过", entry.Name);
            }
        }
        if (allTools.Count > 0)
            ToolsChanged?.Invoke();
        return allTools;
    }

    /// <summary>断开所有连接。</summary>
    public async Task DisconnectAsync()
    {
        foreach (var conn in _connections)
            await conn.DisposeAsync();
        _connections.Clear();
        ToolsChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
