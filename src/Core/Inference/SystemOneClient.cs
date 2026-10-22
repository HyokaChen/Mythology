using System.Text;
using Core.Data;
using Microsoft.Extensions.Logging;

namespace Core.Inference;

/// <summary>
/// 本地判定模型客户端契约（System One / d1-omni）。
/// 失败一律返回 null —— 调用方按"未知"降级，绝不构成负判、绝不重试。
/// </summary>
public interface ISystemOneClient
{
    /// <summary>总开关（配置热更新后即时反映）。</summary>
    bool Enabled { get; }

    /// <summary>当前生效配置。</summary>
    SystemOneConfig Options { get; }

    /// <summary>单次请求；失败/禁用返回 null。</summary>
    Task<SystemOneResponse?> AskAsync(
        object state,
        IReadOnlyDictionary<string, SystemOneQuestion> questions,
        CancellationToken ct = default
    );

    /// <summary>配置热更新（UI 保存路径调用；Loop 停止期，无并发风险）。</summary>
    void UpdateConfig(SystemOneConfig options);
}

/// <summary>
/// llama.cpp <c>/v1/systemone</c> HTTP 客户端。
/// - 单槽串行（<see cref="SemaphoreSlim"/>）：llama.cpp 单模型单槽，防止并发压垮。
/// - 不设 <see cref="HttpClient.Timeout"/>，每次请求用链接 CTS + <c>CancelAfter(当前 TimeoutMs)</c>
///   （超时可随配置热更新）；超时归 null，调用方取消则重抛。
/// - 不重试：429/5xx 与传输失败一视同仁，直接降级。
/// </summary>
public sealed class SystemOneClient : ISystemOneClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger? _logger;
    private SystemOneConfig _options;
    private bool _disposed;

    /// <param name="options">初始配置。</param>
    /// <param name="logger">日志（可选）。</param>
    /// <param name="handler">HTTP 处理器（测试注入；null = 默认）。</param>
    public SystemOneClient(
        SystemOneConfig options,
        ILogger? logger = null,
        HttpMessageHandler? handler = null
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _http = new HttpClient(handler ?? new HttpClientHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public bool Enabled => Volatile.Read(ref _options).Enabled;

    public SystemOneConfig Options => Volatile.Read(ref _options);

    public void UpdateConfig(SystemOneConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Volatile.Write(ref _options, options);
    }

    public async Task<SystemOneResponse?> AskAsync(
        object state,
        IReadOnlyDictionary<string, SystemOneQuestion> questions,
        CancellationToken ct = default
    )
    {
        var options = Options;
        if (!options.Enabled || questions.Count == 0)
            return null;

        var body = SystemOneWire.BuildRequestJson(state, options.Model, questions);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.TimeoutMs);
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(options))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            // 托管 Jev（TypeSafe / OpenRouter）：Bearer 密钥；本地端点留空则不加头
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                request.Headers.TryAddWithoutValidation(
                    "Authorization",
                    $"Bearer {options.ApiKey}"
                );
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("SystemOne 请求失败：HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var json = await response
                .Content.ReadAsStringAsync(timeoutCts.Token)
                .ConfigureAwait(false);
            return SystemOneWire.ParseResponse(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方取消：正常中断传播
        }
        catch (Exception ex)
        {
            // 超时/传输/解析失败：按不可用降级
            _logger?.LogDebug(ex, "SystemOne 请求异常（按不可用降级）");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>拼接口地址：基址去尾斜杠 + 规范化路径（默认 /v1/systemone）。</summary>
    private static string BuildUrl(SystemOneConfig options)
    {
        var path = string.IsNullOrWhiteSpace(options.Path) ? "/v1/systemone" : options.Path;
        if (!path.StartsWith('/'))
            path = "/" + path;
        return options.BaseUrl.TrimEnd('/') + path;
    }
}
