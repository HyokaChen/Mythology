using System.Runtime.CompilerServices;

namespace Core.Infrastructure;

/// <summary>
/// 自定义 <see cref="DelegatingHandler"/>，追踪活跃的流式 HTTP 响应，
/// 并在 <see cref="CancelActiveRequests"/> 被调用时强制关闭它们。
/// <para>
/// .NET 的 <c>NetworkStream.ReadAsync</c> 不响应 <c>CancellationToken</c>，
/// 因此在 SSE 流式读取期间，仅靠取消令牌无法中断阻塞的 I/O 操作。
/// 唯一可靠的方式是关闭底层 socket（即 dispose <c>HttpResponseMessage</c>），
/// 这会使 <c>ReadAsync</c> 抛出 <c>IOException</c> 从而退出阻塞。
/// </para>
/// <para>
/// 通过 LlmTornado 的 <c>EndpointBase.OnHttpClientRequested</c> 钩子注入
/// 带有此 handler 的 <c>HttpClient</c>，即可在不修改 LlmTornado 源码的前提下
/// 实现强制中断流式请求。
/// </para>
/// <para>
/// 使用 <see cref="ConditionalWeakTable{TKey,TValue}"/> 追踪活跃响应，
/// 当 <c>HttpResponseMessage</c> 被 GC 回收时自动移除条目，避免无限增长。
/// </para>
/// </summary>
/// <param name="innerHandler">
/// 内层处理器（可选）：缺省 <see cref="HttpClientHandler"/>。测试注入 <c>UseProxy=false</c> 的
/// 处理器以获得与本机代理设置无关的确定性失败路径（连不上即 <see cref="HttpRequestException"/>）。
/// </param>
public class CancellationHttpHandler(HttpMessageHandler? innerHandler = null)
    : DelegatingHandler(innerHandler ?? new HttpClientHandler())
{
    /// <summary>
    /// 使用 ConditionalWeakTable 追踪活跃响应。
    /// 当 HttpResponseMessage 被 GC 回收时，关联条目自动移除，避免内存泄漏。
    /// </summary>
    private readonly ConditionalWeakTable<HttpResponseMessage, object> _activeResponses = new();

    /// <summary>当前追踪的响应数量（用于诊断和 CancelActiveRequests）。</summary>
    private readonly List<HttpResponseMessage> _trackedList = [];
    private readonly Lock _lock = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            lock (_lock)
            {
                _activeResponses.Add(response, Sentinel.Value);
                _trackedList.Add(response);
            }

            response.Headers.TryAddWithoutValidation("X-Cancellation-Tracked", "true");
        }

        return response;
    }

    /// <summary>
    /// 强制关闭所有活跃的流式 HTTP 响应。
    /// 调用后，阻塞在 <c>ReadAsync</c> 上的 SSE 读取将收到 <c>IOException</c> 并退出。
    /// </summary>
    public void CancelActiveRequests()
    {
        HttpResponseMessage[] responses;
        lock (_lock)
        {
            responses = [.. _trackedList];
            _trackedList.Clear();
            // ConditionalWeakTable 不需要手动清除——GC 会回收已 dispose 的 key
        }

        foreach (var response in responses)
        {
            try
            {
                response.Dispose();
            }
            catch
            {
                // Dispose 可能因流已关闭而抛出，忽略
            }
        }
    }

    /// <summary>从追踪集合中移除已完成的响应（由外部调用）。</summary>
    public void Untrack(HttpResponseMessage response)
    {
        lock (_lock)
        {
            _trackedList.Remove(response);
            // ConditionalWeakTable 的 Remove 是可选的——key 被 GC 后自动清除
        }
    }

    /// <summary>
    /// ConditionalWeakTable 的 TValue 必须是引用类型。
    /// 使用单例 Sentinel 替代 byte，避免每次 Add 分配新对象。
    /// </summary>
    private sealed class Sentinel
    {
        public static readonly Sentinel Value = new();
    }
}
