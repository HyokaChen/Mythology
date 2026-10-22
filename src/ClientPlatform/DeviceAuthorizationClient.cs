using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;

namespace ClientPlatform;

/// <summary>
/// 设备授权客户端（CHANGE_07 §10）— TUI/Desktop 的 RFC 8628 变体流程：
/// authorize → 展示二维码/用户码 → 用户网页批准 → SSE/轮询 token 拿数据面 API Key。
/// </summary>
public sealed class DeviceAuthorizationClient(HttpClient http, string baseUrl)
{
    private static readonly JsonSerializerOptions Json = ClientJsonDefaults.Cloud;

    // 流式客户端模式：客户端超时设为无限（SSE 长连接不能被 HttpClient.Timeout 掐断），
    // 改为按请求的链接 CTS 作为超时安全网。
    private readonly HttpClient _http = ConfigureTimeout(http);

    private static HttpClient ConfigureTimeout(HttpClient client)
    {
        client.Timeout = CloudTimings.StreamingClientTimeout; // 流式 SSE 客户端：共享收口（CloudTimings）
        return client;
    }

    public async Task<DeviceAuthorization> AuthorizeAsync(
        string deviceName,
        CancellationToken cancellationToken
    )
    {
        // D5 统一：优先 OAuth Discovery（/.well-known/oauth-authorization-server）声明的
        // device_authorization_endpoint（RFC 8628 字段），缺失/失败回退硬编码路径
        // （Cloud.Api 端点路径稳定，discovery 为前瞻兼容）。
        var authorizeEndpoint =
            await DiscoverDeviceAuthorizationEndpointAsync(cancellationToken)
            ?? $"{baseUrl}/v1/device/authorize";

        // 登录客户端 Timeout 无限（LoginHttpTimeout，交互期可长挂起），但 authorize 是纯
        // HTTP 握手——远端不可达时快速失败让登录框 Toast「无法连接到云端」，不能无限挂起。
        using var authorizeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authorizeCts.CancelAfter(CloudTimings.LoginConnectTimeout);
        using var response = await _http.PostAsJsonAsync(
            authorizeEndpoint,
            new Dictionary<string, object?> { ["device-name"] = deviceName },
            Json,
            authorizeCts.Token
        );
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CloudErrorMapper.Map(response.StatusCode, body);

        try
        {
            return JsonSerializer.Deserialize<DeviceAuthorization>(body, Json)
                ?? throw new CloudApiException("invalid_response", "Empty authorize response.");
        }
        catch (JsonException)
        {
            // 响应体非法 JSON → 统一为可识别错误码，不向调用方泄漏裸 JsonException
            throw new CloudApiException("invalid_response", "Invalid authorize response.");
        }
    }

    /// <summary>
    /// OAuth Discovery：GET /.well-known/oauth-authorization-server 获取 device_authorization_endpoint
    /// （RFC 8628 专用字段）。注意绝不能读 authorization_endpoint——那是 OAuth 授权页入口
    /// （GET-only 的 /v1/oauth/authorize），把设备流 payload POST 过去会 405。
    /// 云端不可达/不支持/字段缺失时返回 null（调用方回退默认路径）。
    /// </summary>
    private async Task<string?> DiscoverDeviceAuthorizationEndpointAsync(CancellationToken ct)
    {
        try
        {
            using var dcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dcts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync(
                $"{baseUrl.TrimEnd('/')}/.well-known/oauth-authorization-server",
                dcts.Token
            );
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(dcts.Token)
            );
            var root = doc.RootElement;
            if (
                root.TryGetProperty("device_authorization_endpoint", out var endpoint)
                && endpoint.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(endpoint.GetString())
            )
            {
                return endpoint.GetString();
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 5s discovery 超时 → 回退默认路径；调用方取消则向上传播
            return null;
        }
        catch (HttpRequestException)
        {
            // 网络不可达 → 回退默认路径
            return null;
        }
        catch (JsonException)
        {
            // 畸形 discovery 响应（空/非 JSON）→ 回退默认路径，不中断登录
            return null;
        }
    }

    /// <summary>
    /// 轮询 token。未批准返回 null（调用方继续轮询）；已批准返回 API Key。
    /// SSE 优先（POST /v1/device/token/stream），旧服务器回退到 POST /v1/device/token。
    /// </summary>
    public async Task<DeviceToken?> PollTokenAsync(
        string deviceCode,
        CancellationToken cancellationToken
    )
    {
        using var requestMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/v1/device/token/stream"
        );
        requestMessage.Content = JsonContent.Create(
            new Dictionary<string, object?> { ["device-code"] = deviceCode },
            options: Json
        );
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        // 旧服务器 / 非 SSE → 回退到原 POST /v1/device/token 逻辑
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            return await PollTokenLegacyAsync(deviceCode, cancellationToken);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parser = SseParser.Create(stream);
        await foreach (var item in parser.EnumerateAsync(cancellationToken))
        {
            switch (item.EventType)
            {
                case "approved":
                {
                    try
                    {
                        var token = JsonSerializer.Deserialize<DeviceToken>(item.Data, Json);
                        return token
                            ?? throw new CloudApiException(
                                "invalid_response",
                                "Empty token response."
                            );
                    }
                    catch (JsonException)
                    {
                        throw new CloudApiException("invalid_response", "Invalid token response.");
                    }
                }
                case "error":
                {
                    using var error = JsonDocument.Parse(item.Data);
                    var code = error.RootElement.TryGetProperty("code", out var codeElement)
                        ? codeElement.GetString()
                        : null;
                    var detail = error.RootElement.TryGetProperty("detail", out var detailElement)
                        ? detailElement.GetString()
                        : null;
                    if (code is "expired_grant" or "invalid_grant")
                    {
                        throw new CloudApiException(
                            code,
                            string.IsNullOrWhiteSpace(detail)
                                ? "设备授权已失效，请重新发起登录。"
                                : detail
                        );
                    }
                    return null;
                }
                default:
                    continue;
            }
        }

        // 流结束且无终态事件 → 视为未批准
        return null;
    }

    /// <summary>
    /// 原 POST /v1/device/token 逻辑 — 旧服务器回退路径。
    /// </summary>
    private async Task<DeviceToken?> PollTokenLegacyAsync(
        string deviceCode,
        CancellationToken cancellationToken
    )
    {
        using var response = await _http.PostAsJsonAsync(
            $"{baseUrl}/v1/device/token",
            new Dictionary<string, object?> { ["device-code"] = deviceCode },
            Json,
            cancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // 409：区分 pending（继续轮询）与 expired（grant 已过期，终止并提示重新发起）。
        // 旧服务器无结构化 body → 视为 pending 继续轮询（向后兼容）。
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var status = doc.RootElement.TryGetProperty("status", out var statusEl)
                    ? statusEl.GetString()
                    : null;
                if (status == "expired")
                {
                    throw new CloudApiException(
                        "expired_grant",
                        "设备授权已过期，请重新发起登录。"
                    );
                }
            }
            catch (JsonException)
            {
                // 非结构化 body → 按 pending 处理，不中断轮询
            }
            return null;
        }

        if (!response.IsSuccessStatusCode)
            throw CloudErrorMapper.Map(response.StatusCode, body);

        try
        {
            return JsonSerializer.Deserialize<DeviceToken>(body, Json)
                ?? throw new CloudApiException("invalid_response", "Empty token response.");
        }
        catch (JsonException)
        {
            throw new CloudApiException("invalid_response", "Invalid token response.");
        }
    }
}

public sealed record DeviceAuthorization(
    [property: System.Text.Json.Serialization.JsonPropertyName("device-code")] string DeviceCode,
    [property: System.Text.Json.Serialization.JsonPropertyName("user-code")] string UserCode,
    [property: System.Text.Json.Serialization.JsonPropertyName("verification-uri")]
        string VerificationUri,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires-in-seconds")]
        int ExpiresInSeconds,
    [property: System.Text.Json.Serialization.JsonPropertyName("poll-interval-seconds")]
        int PollIntervalSeconds
);

/// <summary>
/// 设备流 token 响应（§13 Tier 2 契约）：可选携带服务端过期时间 ExpiresAt（JSON `expires-at`，ISO-8601 UTC）。
/// 旧服务端不返回该字段 → null，客户端回退滑动续期；返回 → 客户端优先采用服务端真值。
/// </summary>
public sealed record DeviceToken(
    [property: System.Text.Json.Serialization.JsonPropertyName("api-key")] string ApiKey,
    [property: System.Text.Json.Serialization.JsonPropertyName("public-id")] string PublicId,
    [property: System.Text.Json.Serialization.JsonPropertyName("workspace-id")] string WorkspaceId,
    [property: System.Text.Json.Serialization.JsonPropertyName("plan")] string Plan,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires-at")]
        DateTimeOffset? ExpiresAt = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("display-name")]
        string DisplayName = ""
);
