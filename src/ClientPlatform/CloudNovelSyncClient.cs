using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

// ── DTO（字段名与云端 Contracts 逐字段对齐；kebab-case 由 ClientJsonDefaults.Cloud 出站）──

public sealed record SyncNovelSummaryDto(
    string NovelId,
    string? Title,
    string? DirectoryName,
    DateTimeOffset UpdatedAt,
    int DocumentCount
);

public sealed record SyncDocumentMetaDto(
    string DocumentType,
    string EntityId,
    int Revision,
    [property: JsonPropertyName("content-sha-256")] string ContentSha256,
    string RelativePath,
    DateTimeOffset UpdatedAt
);

public sealed record SyncDocumentDto(
    string DocumentType,
    string EntityId,
    int Revision,
    [property: JsonPropertyName("content-sha-256")] string ContentSha256,
    string? Title,
    string RelativePath,
    string ContentEncoding,
    string Content,
    DateTimeOffset UpdatedAt
);

public sealed record SyncPushItemDto(
    string DocumentType,
    string EntityId,
    int BaseRevision,
    [property: JsonPropertyName("content-sha-256")] string ContentSha256,
    string? Title,
    string RelativePath,
    string ContentEncoding,
    string Content
);

public sealed record SyncPushResultDto(
    string DocumentType,
    string EntityId,
    string Status,
    int? Revision,
    SyncDocumentDto? Current,
    string? Error
);

public sealed record SyncDeleteItemDto(string DocumentType, string EntityId, int BaseRevision);

public sealed record SyncDeleteResultDto(
    string DocumentType,
    string EntityId,
    string Status,
    SyncDocumentDto? Current,
    string? Error
);

/// <summary>
/// 云端小说同步客户端（/v1/sync 数据面）。协议契约见私有后端仓库
/// CLOUD_NOVEL_SYNC_PLAN.md v3；conflict 以 HTTP 200 + 结构化 Current 返回（非异常路径）。
/// </summary>
public interface ICloudNovelSyncClient
{
    Task<IReadOnlyList<SyncNovelSummaryDto>> ListNovelsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SyncDocumentMetaDto>> ListDocumentsAsync(
        string novelId,
        CancellationToken ct = default
    );
    Task<IReadOnlyList<SyncDocumentDto>> FetchDocumentsAsync(
        string novelId,
        IReadOnlyList<(string Type, string EntityId)> keys,
        CancellationToken ct = default
    );
    Task<IReadOnlyList<SyncPushResultDto>> PushDocumentsAsync(
        string novelId,
        IReadOnlyList<SyncPushItemDto> items,
        CancellationToken ct = default
    );
    Task<IReadOnlyList<SyncDeleteResultDto>> DeleteDocumentsAsync(
        string novelId,
        IReadOnlyList<SyncDeleteItemDto> items,
        CancellationToken ct = default
    );
}

/// <summary>
/// 实现 — 传输（baseUrl 归一 / X-API-Key / 重试 / 按请求 60s 安全网 / 401→403 吊销总线 /
/// 错误映射）收口在 <see cref="CloudRestTransport"/>；本类声明端点与分批语义。
/// </summary>
public sealed class CloudNovelSyncClient(
    HttpClient http,
    Func<string?> apiKeyProvider,
    string baseUrl,
    CloudAuthInvalidationBus? invalidationBus = null,
    ILogger? logger = null
) : ICloudNovelSyncClient
{
    private static readonly JsonSerializerOptions Json = ClientJsonDefaults.Cloud;

    /// <summary>fetch 单批 keys 上限（与服务端 MaxSyncBatchItems 对齐）。</summary>
    public const int MaxBatchItems = 500;

    /// <summary>push 单批字节上限（6MB，留余量于服务端 MaxSyncBatchBytes=8MB）。</summary>
    public const int MaxBatchBytes = 6 * 1024 * 1024;

    private readonly CloudRestTransport _transport = new(
        baseUrl,
        apiKeyProvider,
        http,
        invalidationBus,
        logger
    );

    public async Task<IReadOnlyList<SyncNovelSummaryDto>> ListNovelsAsync(
        CancellationToken ct = default
    ) =>
        await SendAsync<List<SyncNovelSummaryDto>>(
            HttpMethod.Get,
            "/v1/sync/novels",
            body: null,
            ct
        ) ?? [];

    public async Task<IReadOnlyList<SyncDocumentMetaDto>> ListDocumentsAsync(
        string novelId,
        CancellationToken ct = default
    ) =>
        await SendAsync<List<SyncDocumentMetaDto>>(
            HttpMethod.Get,
            $"/v1/sync/novels/{Uri.EscapeDataString(novelId)}/documents",
            body: null,
            ct
        ) ?? [];

    public async Task<IReadOnlyList<SyncDocumentDto>> FetchDocumentsAsync(
        string novelId,
        IReadOnlyList<(string Type, string EntityId)> keys,
        CancellationToken ct = default
    )
    {
        var results = new List<SyncDocumentDto>(keys.Count);
        foreach (var batch in keys.Chunk(MaxBatchItems))
        {
            var response =
                await SendAsync<FetchResponse>(
                    HttpMethod.Post,
                    $"/v1/sync/novels/{Uri.EscapeDataString(novelId)}/documents:fetch",
                    new FetchRequest([
                        .. batch.Select(key => new FetchKey(key.Type, key.EntityId)),
                    ]),
                    ct
                ) ?? new FetchResponse([]);
            results.AddRange(response.Documents);
        }
        return results;
    }

    public async Task<IReadOnlyList<SyncPushResultDto>> PushDocumentsAsync(
        string novelId,
        IReadOnlyList<SyncPushItemDto> items,
        CancellationToken ct = default
    )
    {
        var results = new List<SyncPushResultDto>(items.Count);
        foreach (var batch in BatchByBytes(items))
        {
            var response =
                await SendAsync<PushResponse>(
                    HttpMethod.Post,
                    $"/v1/sync/novels/{Uri.EscapeDataString(novelId)}/documents:push",
                    new PushRequest([.. batch]),
                    ct
                ) ?? new PushResponse([]);
            results.AddRange(response.Results);
        }
        return results;
    }

    public async Task<IReadOnlyList<SyncDeleteResultDto>> DeleteDocumentsAsync(
        string novelId,
        IReadOnlyList<SyncDeleteItemDto> items,
        CancellationToken ct = default
    )
    {
        var results = new List<SyncDeleteResultDto>(items.Count);
        foreach (var batch in items.Chunk(MaxBatchItems))
        {
            var response =
                await SendAsync<DeleteResponse>(
                    HttpMethod.Post,
                    $"/v1/sync/novels/{Uri.EscapeDataString(novelId)}/documents:delete",
                    new DeleteRequest([.. batch]),
                    ct
                ) ?? new DeleteResponse([]);
            results.AddRange(response.Results);
        }
        return results;
    }

    /// <summary>按 500 条 / 6MB 双上限分批（批内部分成功合法，结果按序拼接）。</summary>
    private static IEnumerable<List<SyncPushItemDto>> BatchByBytes(
        IReadOnlyList<SyncPushItemDto> items
    )
    {
        var batch = new List<SyncPushItemDto>();
        var bytes = 0;
        foreach (var item in items)
        {
            var size = System.Text.Encoding.UTF8.GetByteCount(item.Content);
            if (batch.Count > 0 && (batch.Count >= MaxBatchItems || bytes + size > MaxBatchBytes))
            {
                yield return batch;
                batch = [];
                bytes = 0;
            }
            batch.Add(item);
            bytes += size;
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>发一次请求并反序列化（null = 响应体为 JSON null，由调用方回退为空结果）。</summary>
    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct
    )
        where T : class
    {
        using var request = _transport.NewRequest(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }
        using var response = await _transport.SendAsync(request, ct);
        var responseText = await CloudRestTransport.ReadBodyAsync(response, ct);
        _transport.EnsureSuccess(response, responseText);
        return JsonSerializer.Deserialize<T>(responseText, Json);
    }

    private sealed record FetchKey(
        [property: JsonPropertyName("document-type")] string DocumentType,
        [property: JsonPropertyName("entity-id")] string EntityId
    );

    private sealed record FetchRequest(IReadOnlyList<FetchKey> Keys);

    private sealed record FetchResponse(IReadOnlyList<SyncDocumentDto> Documents);

    private sealed record PushRequest(IReadOnlyList<SyncPushItemDto> Documents);

    private sealed record PushResponse(IReadOnlyList<SyncPushResultDto> Results);

    private sealed record DeleteRequest(IReadOnlyList<SyncDeleteItemDto> Documents);

    private sealed record DeleteResponse(IReadOnlyList<SyncDeleteResultDto> Results);
}
