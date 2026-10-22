using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Core.Mcp;

/// <summary>
/// stdio JSON-RPC 传输 — 通过子进程 stdin/stdout 逐行交换 JSON 字符串。
/// 响应按 JSON-RPC id 匹配（ConcurrentDictionary），支持并发请求。
/// </summary>
public sealed class StdioTransport : IJsonRpcTransport, IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
    private int _disposed;

    public bool IsConnected => _disposed == 0 && !_process.HasExited;

    public StdioTransport(string command, IReadOnlyList<string> args, ILogger? logger = null)
    {
        _logger = logger;
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = string.Join(" ", args.Select(EscapeArg)),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = new Process { StartInfo = psi };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger?.LogWarning("MCP stderr [{Server}]: {Line}", command, e.Data);
        };

        _process.Start();
        _process.BeginErrorReadLine();
        _stdin = _process.StandardInput;

        // 后台读取 stdout 行，按 id 匹配 pending
        _ = Task.Run(ReadLoopAsync);
    }

    public async Task<string> SendAsync(string requestJson, CancellationToken ct = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("MCP stdio 传输未连接");

        // 解析 id 用于匹配
        using var doc = JsonDocument.Parse(requestJson);
        var id = doc.RootElement.GetProperty("id").GetInt32();

        var tcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _pending[id] = tcs;

        try
        {
            await _stdin.WriteLineAsync(requestJson);
            await _stdin.FlushAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException("MCP 调用超时（60s）");
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (
                        !root.TryGetProperty("id", out var idEl)
                        || idEl.ValueKind == JsonValueKind.Null
                    )
                        continue; // 通知，忽略

                    var id = idEl.GetInt32();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(line);
                    }
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning(ex, "MCP stdio JSON 解析失败: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MCP stdio 读取循环异常退出");
        }
        finally
        {
            foreach (var (id, tcs) in _pending.ToArray())
            {
                tcs.TrySetException(new InvalidOperationException("MCP 连接已断开"));
                _pending.TryRemove(id, out _);
            }
        }
    }

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch { }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(cts.Token);
        }
        catch { }
        _process.Dispose();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private static string EscapeArg(string arg) =>
        arg.Contains(' ') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}
