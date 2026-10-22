namespace Core.Mcp;

/// <summary>
/// JSON-RPC 2.0 传输抽象 — 隐藏 stdio 子进程 / HTTP 端点两种传输的差异。
/// 接口只交换原始 JSON 字符串，JSON-RPC 信封由 <see cref="ExternalMcpConnection"/> 处理。
/// </summary>
public interface IJsonRpcTransport
{
    /// <summary>发送一条 JSON-RPC 请求体并返回响应体（原始 JSON 字符串）。</summary>
    Task<string> SendAsync(string requestJson, CancellationToken ct = default);

    /// <summary>传输是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>断开连接（释放资源）。</summary>
    Task DisconnectAsync();
}
