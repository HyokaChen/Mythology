using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>月度积分配额快照（GET /v1/agent/usage，服务端 kebab-case 载荷）。</summary>
/// <param name="Period">账期（yyyy-MM）。</param>
/// <param name="Plan">云端套餐标识（展示用）。</param>
/// <param name="IncludedUnits">本月套餐内含额度。</param>
/// <param name="UsedUnits">已结算消耗。</param>
/// <param name="ReservedUnits">在途预留（未结算的进行中调用）。</param>
/// <param name="RemainingUnits">可用余量（含额度 − 已用 − 在途）。</param>
/// <param name="Breakdown">按能力的已用额度明细（能力名 → units）。</param>
public sealed record UsageSnapshot(
    string Period,
    string Plan,
    int IncludedUnits,
    int UsedUnits,
    int ReservedUnits,
    int RemainingUnits,
    IReadOnlyDictionary<string, int> Breakdown
);

/// <summary>单笔账本记录（追加式账本：余额可由账本重建）。</summary>
/// <param name="InvocationId">关联的调用标识（预留/释放/结算行）；零价直扣行为 null。</param>
/// <param name="EntryType">账目类型（charge / reserve / release / settle 等）。</param>
/// <param name="Capability">能力标识（如 fiction.story.check）。</param>
/// <param name="Status">账目状态。</param>
/// <param name="Units">正数为扣减、负数为返还。</param>
/// <param name="IdempotencyKeyHash">幂等键哈希（幂等去重审计用）。</param>
/// <param name="CreatedAt">记账时间。</param>
public sealed record UsageLedgerLine(
    Guid? InvocationId,
    string EntryType,
    string Capability,
    string Status,
    int Units,
    string? IdempotencyKeyHash,
    DateTimeOffset CreatedAt
);

/// <summary>逐笔账本分页响应。</summary>
/// <param name="Period">账期（yyyy-MM）。</param>
/// <param name="Entries">本页账目明细，按时间升序。</param>
/// <param name="NextCursor">下一页游标；无更多时为 null。</param>
public sealed record UsageLedgerPage(
    string Period,
    IReadOnlyList<UsageLedgerLine> Entries,
    string? NextCursor
);

/// <summary>
/// 云端用量客户端 — 调用 /v1/agent/usage 与 /v1/agent/usage/ledger（只读查询，零价）。
///
/// 鉴权：数据面 X-API-Key（Bearer 通道保留给控制面 JWT，API Key 走 Bearer 会被拒 401）。
/// 计费：两个端点均为零价查询——不预留、不扣费，不计入月度用量。
/// period 缺省当月；非法格式（非 yyyy-MM）服务端回 400 invalid_request。
/// 传输（baseUrl 归一 / X-API-Key / 重试 / 按请求 60s 安全网 / 401→403 吊销总线 /
/// 错误映射）由 <see cref="CloudRestTransport"/> 收口。
///
/// 响应为服务端 kebab-case JSON，此处反序列化为强类型（UI 消费需要数值做算术与格式化，
/// 与 KnowledgeRestClient 的原文透传形态不同）。
/// </summary>
public sealed class UsageRestClient
{
    private readonly CloudRestTransport _transport;

    public UsageRestClient(
        string baseUrl,
        Func<string?> apiKeyProvider,
        HttpClient? http = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null
    )
    {
        _transport = new CloudRestTransport(baseUrl, apiKeyProvider, http, invalidationBus, logger);
    }

    /// <summary>月度配额快照（period 缺省当月；非法格式服务端回 400）。</summary>
    public async Task<UsageSnapshot> GetUsageAsync(
        string? period = null,
        CancellationToken ct = default
    )
    {
        var query = string.IsNullOrWhiteSpace(period)
            ? ""
            : $"?period={Uri.EscapeDataString(period)}";
        var json = await GetAsync($"/v1/agent/usage{query}", ct);
        return Deserialize<UsageSnapshot>(json);
    }

    /// <summary>逐笔账本（游标翻页；capability 非空时按能力过滤）。</summary>
    public async Task<UsageLedgerPage> GetLedgerAsync(
        string? period = null,
        string? capability = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default
    )
    {
        var query = new List<string> { $"limit={Math.Clamp(limit, 1, 500)}" };
        if (!string.IsNullOrWhiteSpace(period))
            query.Add($"period={Uri.EscapeDataString(period)}");
        if (!string.IsNullOrWhiteSpace(capability))
            query.Add($"capability={Uri.EscapeDataString(capability)}");
        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var json = await GetAsync($"/v1/agent/usage/ledger?{string.Join("&", query)}", ct);
        return Deserialize<UsageLedgerPage>(json);
    }

    private Task<string> GetAsync(string path, CancellationToken ct) =>
        _transport.SendForStringAsync(
            HttpMethod.Get,
            path,
            body: null,
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ClientJsonDefaults.Cloud)
        ?? throw new CloudApiException("usage_failed", "云端用量响应解析失败（载荷为空）。");
}
