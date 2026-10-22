using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClientPlatform;

/// <summary>
/// CHANGE_09 形态 B：本地回调授权客户端 — PKCE + loopback redirect + 前端中转回调 + token 交换。
/// 流程：生成 PKCE → 绑定 127.0.0.1:0 监听 /api/oauth/code → 打开浏览器授权页（redirect_uri 指向前端 /callback） →
/// 前端转发 code → 校验 state → POST /v1/oauth/token 换 API Key。
/// 失败时由调用方回退到设备流。
/// </summary>
public sealed class OAuthLoopbackClient(HttpClient http, string baseUrl)
{
    private const string ClientId = "mythology-tui";
    private const string CallbackPath = "/callback";
    private const string CodeEndpoint = "/api/oauth/code";

    // 回调等待窗口共享收口（CloudTimings.OAuthCallbackTimeout）：浏览器授权后回调 120s 内未收回 → 回退设备流
    private static readonly TimeSpan CallbackTimeout = CloudTimings.OAuthCallbackTimeout;

    /// <summary>
    /// 执行本地回调 OAuth 授权流程。
    /// 返回授权结果；任何失败抛出 <see cref="OAuthLoopbackException"/>，调用方应回退设备流。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（用户取消或超时）。</param>
    public async Task<OAuthTokenResult> AuthorizeAsync(CancellationToken cancellationToken) =>
        await AuthorizeCoreAsync(OpenBrowser, cancellationToken);

    /// <summary>
    /// internal 测试缝：注入浏览器打开动作（跨仓库契约测试用真实后端但不真开浏览器）。
    /// 公开入口不传该参数时走平台真实 OpenBrowser。
    /// </summary>
    internal async Task<OAuthTokenResult> AuthorizeAsync(
        Func<string, bool> openBrowser,
        CancellationToken cancellationToken
    ) => await AuthorizeCoreAsync(openBrowser, cancellationToken);

    private async Task<OAuthTokenResult> AuthorizeCoreAsync(
        Func<string, bool> openBrowser,
        CancellationToken cancellationToken
    )
    {
        // ① 生成 PKCE verifier + challenge + state
        var verifier = GenerateCodeVerifier();
        var challenge = ComputeS256Challenge(verifier);
        var state = GenerateState();

        // ② 绑定 127.0.0.1:0 监听
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Clear();
        var port = FindAvailablePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new OAuthLoopbackException("端口绑定失败", ex);
        }

        // 前端中转回调地址（统一收口）：预检、授权 URL、铸码、兑换四处使用同一字面值
        // （服务端精确比对 redirect_uri）；本地监听端口经 client_port 查询参数带出，不进入 redirect_uri。
        var frontendCallbackUri = $"{CloudEndpoints.ResolveWebOrigin().TrimEnd('/')}{CallbackPath}";
        var clientPortParameter = $"&client_port={port}";

        // ③ 可选前置校验：GET /v1/oauth/authorize 验证前端回调地址/challenge 是否被服务端接受，
        //    并读取后端声明的授权页地址（开发环境前后端异源时后端按 Cloud:WebPublicUrl 配置返回）
        var verificationEndpoint = await PreflightAuthorizeAsync(
            frontendCallbackUri,
            challenge,
            state,
            cancellationToken
        );
        if (verificationEndpoint is null)
        {
            listener.Stop();
            throw new OAuthLoopbackException("服务端不接受本地回调参数，请改用设备流");
        }

        // ④ 构建授权 URL 并打开浏览器（测试缝：注入动作；真实流程走平台 OpenBrowser）
        var authorizeUrl = BuildAuthorizeUrl(
            frontendCallbackUri,
            challenge,
            state,
            verificationEndpoint,
            clientPortParameter
        );
        var browserOpened = openBrowser(authorizeUrl);
        if (!browserOpened)
        {
            listener.Stop();
            throw new OAuthLoopbackException("无法打开浏览器");
        }

        // ④ 等待前端 POST 转发 code（内部超时 → OAuthLoopbackException，调用方回退设备流；用户取消 → OCE 保持原语义）
        var callbackResult = await ListenForCodeAsync(
            listener,
            state,
            CallbackTimeout,
            cancellationToken
        );

        // ⑤ 用 code + verifier 换 token（redirectUri 用前端中转地址，与 /v1/oauth/code 中保存的一致）
        return await ExchangeTokenAsync(
            callbackResult.Code,
            verifier,
            frontendCallbackUri,
            cancellationToken
        );
    }

    /// <summary>
    /// 检测当前环境是否为 SSH 远程会话（决策 D-C）。
    /// SSH 环境下浏览器无法在本地打开，应直接走设备流。
    /// </summary>
    public static bool IsSshSession() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"));

    // ── PKCE 生成 ──

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeS256Challenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    private static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // ── 端口绑定 ──

    private static int FindAvailablePort()
    {
        using var tcp = System.Net.Sockets.TcpListener.Create(0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    // ── 可选前置校验（§5 可选前置） ──

    /// <summary>
    /// GET /v1/oauth/authorize 校验 redirect_uri/challenge 是否被服务端接受。
    /// 成功返回后端声明的授权页地址：绝对 URL（开发环境前后端异源时后端按
    /// Cloud:WebPublicUrl 配置返回）直接用；相对路径（同源部署默认）拼 API baseUrl。
    /// 服务端明确拒绝（400）返回 null，调用方应提前切设备流，避免打开浏览器后才发现参数不被接受。
    /// </summary>
    private async Task<string?> PreflightAuthorizeAsync(
        string redirectUri,
        string challenge,
        string state,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url =
                $"{baseUrl.TrimEnd('/')}/v1/oauth/authorize"
                + $"?client_id={Uri.EscapeDataString(ClientId)}"
                + $"&response_type=code"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + $"&code_challenge={Uri.EscapeDataString(challenge)}"
                + "&code_challenge_method=S256"
                + $"&state={Uri.EscapeDataString(state)}";

            // 登录客户端 Timeout 无限（LoginHttpTimeout，浏览器授权可长挂起），但预检是纯
            // HTTP 握手——远端不可达时必须快速失败回退设备流，不能把登录对话框挂起。
            using var preflightCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            preflightCts.CancelAfter(CloudTimings.LoginConnectTimeout);
            using var response = await http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                preflightCts.Token
            );
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken
            );
            if (
                doc.RootElement.TryGetProperty("verification-endpoint", out var ep)
                && ep.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(ep.GetString())
            )
                return ep.GetString();

            // 旧后端/字段缺失：默认同源路径（与历史行为一致）
            return "/app/authorize/device";
        }
        catch
        {
            // 网络不可达等 → 返回 null，调用方快速回退设备流，避免打开浏览器指向死地址
            return null;
        }
    }

    // ── 授权 URL 构建 ──

    private string BuildAuthorizeUrl(
        string redirectUri,
        string challenge,
        string state,
        string verificationEndpoint,
        string clientPortParameter
    )
    {
        // 前端中转回调：redirect_uri 指向前端 /callback，client_port 供前端提取后回传 code。
        // 后端仍校验 redirect_uri 为 loopback 地址（见 PreflightAuthorizeAsync），前端端口仅用于本地回传。
        var pageBase =
            verificationEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || verificationEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? verificationEndpoint.TrimEnd('/')
                : $"{baseUrl.TrimEnd('/')}{verificationEndpoint}";

        var sb = new StringBuilder(pageBase);
        sb.Append($"?client_id={Uri.EscapeDataString(ClientId)}");
        sb.Append($"&redirect_uri={Uri.EscapeDataString(redirectUri)}");
        sb.Append($"&code_challenge={Uri.EscapeDataString(challenge)}");
        sb.Append("&code_challenge_method=S256");
        sb.Append($"&state={Uri.EscapeDataString(state)}");
        sb.Append("&response_type=code");
        sb.Append(clientPortParameter);
        return sb.ToString();
    }

    // ── 浏览器打开 ──

    private static bool OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
                );
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", url);
            }
            else
            {
                System.Diagnostics.Process.Start("xdg-open", url);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── 等待前端中转回调 ──

    /// <summary>
    /// 等待前端 /callback 页面 POST /api/oauth/code 转发授权码。
    /// 内部超时（默认 120s）转为 OAuthLoopbackException；用户取消保持 OCE 语义。
    /// internal 测试缝：ClientPlatform.Tests 经 InternalsVisibleTo 访问。
    /// </summary>
    internal static async Task<OAuthCallbackResult> ListenForCodeAsync(
        System.Net.HttpListener listener,
        string expectedState,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token);
                var request = context.Request;
                var response = context.Response;

                // CORS：预检（OPTIONS）**与实际 POST 响应**都必须带 Access-Control-Allow-Origin，
                // 否则浏览器预检通过后仍会拦截实际 POST 的响应（前端 /callback 页 fetch 报
                // No 'Access-Control-Allow-Origin' header + ERR_FAILED）。
                // 仅回显允许的前端源（本地开发 Nuxt 3000）；其他源不返回 CORS 头。
                TryAddCorsHeaders(request, response);

                // CORS 预检
                if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 204;
                    response.Close();
                    continue;
                }

                // 仅接受 POST /api/oauth/code
                if (
                    !request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                    || !request.Url?.AbsolutePath.Equals(
                        CodeEndpoint,
                        StringComparison.OrdinalIgnoreCase
                    ) == true
                )
                {
                    WriteJson(response, 404, "{ \"error\": \"not_found\" }");
                    continue;
                }

                // 解析 JSON body
                string? code = null;
                string? returnedState = null;
                try
                {
                    using var doc = await JsonDocument.ParseAsync(
                        request.InputStream,
                        cancellationToken: timeoutCts.Token
                    );
                    if (doc.RootElement.TryGetProperty("code", out var codeEl))
                        code = codeEl.GetString();
                    if (doc.RootElement.TryGetProperty("state", out var stateEl))
                        returnedState = stateEl.GetString();
                }
                catch
                {
                    WriteJson(response, 400, "{ \"error\": \"invalid_request\" }");
                    continue;
                }

                // state 不匹配 → 可恢复错误，继续等待下一次回调
                if (!string.Equals(returnedState, expectedState, StringComparison.Ordinal))
                {
                    WriteJson(response, 400, "{ \"error\": \"state_mismatch\" }");
                    continue;
                }

                if (string.IsNullOrEmpty(code))
                {
                    WriteJson(response, 400, "{ \"error\": \"missing_code\" }");
                    continue;
                }

                // 成功！
                WriteJson(response, 200, "{ \"status\": \"ok\" }");
                return new OAuthCallbackResult(code, returnedState!);
            }
        }
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw CallbackTimeoutException(timeout);
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            { /* 已停止 */
            }
        }

        if (ct.IsCancellationRequested)
            ct.ThrowIfCancellationRequested();
        throw CallbackTimeoutException(timeout);
    }

    /// <summary>
    /// 为允许的前端源添加 CORS 响应头。允许源从 MYTHOLOGY_WEB_URL 推导（含 localhost/127.0.0.1 两种写法），
    /// 不再硬编码端口；非允许源不加头。
    /// </summary>
    private static bool TryAddCorsHeaders(
        System.Net.HttpListenerRequest request,
        System.Net.HttpListenerResponse response
    )
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
            return false;
        if (
            !AllowedCallbackOrigins()
                .Any(allowed => allowed.Equals(origin, StringComparison.OrdinalIgnoreCase))
        )
            return false;

        response.AddHeader("Access-Control-Allow-Origin", origin);
        response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
        response.AddHeader("Access-Control-Max-Age", "600");
        return true;
    }

    /// <summary>允许向本地监听器发回调的前端源（MYTHOLOGY_WEB_URL + localhost/127.0.0.1 变体）。</summary>
    private static IReadOnlyList<string> AllowedCallbackOrigins()
    {
        var baseUri = new Uri(CloudEndpoints.ResolveWebOrigin());
        var port = baseUri.IsDefaultPort ? string.Empty : $":{baseUri.Port}";
        var origins = new List<string> { $"{baseUri.Scheme}://{baseUri.Host}{port}" };
        var alternateHost = baseUri.Host switch
        {
            "127.0.0.1" => "localhost",
            "localhost" => "127.0.0.1",
            _ => baseUri.Host,
        };
        if (alternateHost != baseUri.Host)
            origins.Add($"{baseUri.Scheme}://{alternateHost}{port}");
        return origins;
    }

    private static OAuthLoopbackException CallbackTimeoutException(TimeSpan timeout) =>
        new($"回调超时（{timeout.TotalSeconds:0}秒），浏览器与终端可能不在同一台机器");

    private static void WriteJson(System.Net.HttpListenerResponse response, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.OutputStream.Close();
    }

    // ── Token 交换（含重试一次，§5.2） ──

    private async Task<OAuthTokenResult> ExchangeTokenAsync(
        string code,
        string verifier,
        string redirectUri,
        CancellationToken ct
    )
    {
        // §5.2: 兑换失败 → 重试一次 → 设备流
        var firstAttempt = await TryExchangeTokenAsync(code, verifier, redirectUri, ct);
        if (firstAttempt.Result is not null)
            return firstAttempt.Result;

        // 网络瞬态失败允许重试一次；协议拒绝不应重复兑换已消费的 code。
        if (firstAttempt.Error is not null)
            throw new OAuthLoopbackException(
                $"Token 交换失败：{firstAttempt.Error.Message}",
                firstAttempt.Error
            );

        var retry = await TryExchangeTokenAsync(code, verifier, redirectUri, ct);
        if (retry.Result is not null)
            return retry.Result;

        if (retry.Error is not null)
        {
            throw new OAuthLoopbackException("Token 交换失败（已重试一次）", retry.Error);
        }

        throw new OAuthLoopbackException("Token 交换失败（已重试一次）");
    }

    private async Task<(OAuthTokenResult? Result, CloudApiException? Error)> TryExchangeTokenAsync(
        string code,
        string verifier,
        string redirectUri,
        CancellationToken ct
    )
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/v1/oauth/token",
                new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["code-verifier"] = verifier,
                    ["redirect-uri"] = redirectUri,
                },
                ClientJsonDefaults.Cloud,
                ct
            );

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = CloudErrorMapper.Map(response.StatusCode, body);
                return (null, error);
            }

            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // §13 Tier 2：可选 expires-at（ISO-8601 UTC）— 缺失/畸形都不破坏兑换，客户端回退滑动续期
            DateTimeOffset? expiresAt = null;
            if (
                root.TryGetProperty("expires-at", out var expiresAtEl)
                && expiresAtEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(expiresAtEl.GetString(), out var parsed)
            )
                expiresAt = parsed;

            return (
                new OAuthTokenResult(
                    root.GetProperty("api-key").GetString() ?? "",
                    root.GetProperty("public-id").GetString() ?? "",
                    root.GetProperty("workspace-id").GetString() ?? "",
                    root.TryGetProperty("plan", out var plan) ? plan.GetString() ?? "" : "",
                    expiresAt,
                    root.TryGetProperty("display-name", out var dn) ? dn.GetString() ?? "" : ""
                ),
                null
            );
        }
        catch (CloudApiException error)
        {
            return (null, error);
        }
        catch (HttpRequestException)
        {
            return (null, null);
        }
    }
}

/// <summary>OAuth 回调结果（code + state）。</summary>
public sealed record OAuthCallbackResult(string Code, string State);

/// <summary>
/// OAuth token 交换结果 — 与 DeviceToken 结构一致（§13 Tier 2：可选服务端过期时间 ExpiresAt，JSON `expires-at`）。
/// </summary>
public sealed record OAuthTokenResult(
    string ApiKey,
    string PublicId,
    string WorkspaceId,
    string Plan,
    DateTimeOffset? ExpiresAt = null,
    string DisplayName = ""
);

/// <summary>
/// 本地回调 OAuth 流程异常 — 调用方应回退到设备流。
/// </summary>
public sealed class OAuthLoopbackException : Exception
{
    public OAuthLoopbackException(string message)
        : base(message) { }

    public OAuthLoopbackException(string message, Exception inner)
        : base(message, inner) { }
}
