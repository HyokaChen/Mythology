using ClientPlatform;
using Core.Cloud;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using CloudConnectionService = Tui.Services.CloudConnectionService;
using CloudToastThrottle = Tui.Services.CloudToastThrottle;

namespace Tui.Controls;

/// <summary>
/// CHANGE_09 形态 B：云端登录对话框 — 本地回调 OAuth（默认）+ 设备授权流程（回退）+ 手动 API Key。
/// 三态统一入口：本地回调 → 失败时一键「改用设备码」→ 手动粘贴 mk_live_ Key 兜底。
/// SSH 环境自动跳过本地回调，直接走设备流（决策 D-C）。
/// </summary>
public sealed class CloudLoginDialog
{
    private readonly CloudConnectionService _session;
    private readonly string _baseUrl;
    private readonly bool _forceDeviceFlow;
    private readonly CloudToastThrottle? _toastThrottle;

    public CloudLoginDialog(
        CloudConnectionService session,
        string baseUrl,
        bool forceDeviceFlow = false
    )
        : this(session, baseUrl, forceDeviceFlow, null) { }

    /// <summary>内部构造：注入节流闸门（internal，闸门为 internal 类型）。</summary>
    internal CloudLoginDialog(
        CloudConnectionService session,
        string baseUrl,
        bool forceDeviceFlow,
        CloudToastThrottle? toastThrottle
    )
    {
        _session = session;
        _baseUrl = baseUrl;
        _forceDeviceFlow = forceDeviceFlow;
        // 登录失败 Toast 节流（与 TuiApp 探活/心跳共用同一闸门实例，避免重复轰炸）；null = 不节流
        _toastThrottle = toastThrottle;
    }

    /// <summary>
    /// 显示登录对话框。返回 true 表示成功获取 API Key。
    /// 自动选择最优流程：本地回调 OAuth（默认）→ 设备流（回退/SSH/--device）。
    /// </summary>
    public async Task<bool> ShowAsync(CancellationToken ct)
    {
        // 决策 D-C：SSH 环境直接走设备流
        if (_forceDeviceFlow || OAuthLoopbackClient.IsSshSession())
            return await ShowDeviceFlowAsync(ct);

        // 默认：先尝试本地回调 OAuth，失败回退设备流
        return await ShowLoopbackOAuthAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // 形态 B：本地回调 OAuth（默认）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地回调 OAuth 流程：PKCE → 监听 127.0.0.1 → 打开浏览器 → 等回调 → 换 token。
    /// 失败时自动回退到设备流。
    /// </summary>
    private async Task<bool> ShowLoopbackOAuthAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();

        // 构建对话框 UI — 等待浏览器回调状态
        var statusText = new State<string?>("🔄 正在启动本地回调授权…");
        var statusBlock = new Markup(() => $"[dim]{statusText.Value}[/]").HorizontalAlignment(
            Align.Center
        );

        // 手动输入 API Key（初始隐藏）
        var apiKeyInput = new MaskedInput { Template = "________________________________" };
        var apiKeyPanel = new Padder
        {
            Content = apiKeyInput,
            Padding = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = Align.Center,
            IsVisible = false,
        };

        var content = new VStack(
            new Markup("[bold]🌐 本地回调授权[/]").HorizontalAlignment(Align.Center),
            new Markup(""),
            new Markup("[dim]浏览器将自动打开授权页面，[/]").HorizontalAlignment(Align.Center),
            new Markup("[dim]选择工作区并确认后自动完成登录。[/]").HorizontalAlignment(
                Align.Center
            ),
            new Markup(""),
            statusBlock,
            apiKeyPanel
        ).Spacing(0);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("🌐 Mythology Cloud 登录")
            .Width(52)
            .MinHeight(14)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(PanelThemes.CreateDialogContent(content));

        var closeBtn = PanelThemes.CreateDialogCloseButton(() =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        });

        // 「改用设备码」按钮 — 手动切换设备流（自动回退也会触发，此按钮供用户主动切换）
        var deviceFlowBtn = new Button(new Markup("[dim]改用设备码[/]")).Style(
            ThemeStyles.BuildButton(PanelThemes.Current)
        );
        deviceFlowBtn.Click(() =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
            // 关闭后启动设备流
            _ = ShowDeviceFlowAsync(ct);
        });

        // 手动输入按钮
        var manualBtn = new Button(new Markup("[dim]手动输入[/]")).Style(
            ThemeStyles.BuildButton(PanelThemes.Current)
        );
        manualBtn.Click(() =>
        {
            apiKeyPanel.IsVisible = !apiKeyPanel.IsVisible;
            if (apiKeyPanel.IsVisible)
                dialog.App?.Focus(apiKeyInput);
        });

        // 确认手动输入（§14 P2：异步远程双重校验，保存前 Probe）
        void ConfirmManualKey() =>
            _ = SubmitManualKeyAsync(apiKeyInput.Value, statusText, tcs, dialog);

        var confirmBtn = new Button(new Markup("[bold]确认[/]")).Style(
            ThemeStyles.BuildButtonAccent(PanelThemes.Current)
        );
        confirmBtn.Click(ConfirmManualKey);

        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(() =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        });

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(
            new HStack(cancelBtn, deviceFlowBtn, manualBtn, confirmBtn).Spacing(2)
        );

        dialog.KeyDown(
            (_, e) =>
            {
                switch (e.Key)
                {
                    case TerminalKey.Escape:
                        tcs.TrySetResult(false);
                        dialog.Close();
                        e.Handled = true;
                        break;
                    case TerminalKey.Enter when apiKeyPanel.IsVisible:
                        ConfirmManualKey();
                        e.Handled = true;
                        break;
                }
            }
        );

        dialog.Show();

        // 启动本地回调 OAuth 流程（后台）
        _ = RunLoopbackOAuthAsync(_session, _baseUrl, statusText, tcs, dialog, ct);

        return await tcs.Task;
    }

    /// <summary>
    /// 后台执行本地回调 OAuth 流程，更新 UI 状态。
    /// 失败时自动回退到设备流（§5.2：回调超时/端口绑定/浏览器打开失败 → 设备流）。
    /// </summary>
    private async Task RunLoopbackOAuthAsync(
        CloudConnectionService session,
        string baseUrl,
        State<string?> statusText,
        TaskCompletionSource<bool> tcs,
        Dialog dialog,
        CancellationToken ct
    )
    {
        try
        {
            statusText.Value = "🔄 正在启动本地回调监听…";

            var oauthClient = new OAuthLoopbackClient(
                new HttpClient { Timeout = CloudTimings.LoginHttpTimeout }, // 登录超时共享收口（CloudTimings）
                baseUrl
            );

            var result = await oauthClient.AuthorizeAsync(ct);

            // 成功 — 保存凭据（D5：完整 CloudSession，含 workspaceId/plan，不再丢失）
            // §13 Tier 2：服务端 expiresAt 优先采用真值，缺失回退 +30d（与 Desktop ResolveExpiry 对齐）
            session.Save(
                CreateServerSession(
                    result.ApiKey,
                    result.PublicId,
                    result.WorkspaceId,
                    result.Plan,
                    result.ExpiresAt
                )
            );

            if (tcs.TrySetResult(true))
            {
                statusText.Value = "✅ 授权成功！";
                RunAfterDelay(800, dialog.Close);
            }
        }
        catch (OAuthLoopbackException ex)
        {
            // §5.2: 本地回调失败 → 自动回退设备流（关框与回退统一走 RunAfterDelay：应用退出时
            // 续体不再回投已脱离的 Dispatcher，见方法注释）
            if (tcs.TrySetResult(false))
            {
                statusText.Value = $"❗ {ex.Message}";
                RunAfterDelay(
                    1500,
                    () =>
                    {
                        dialog.Close();

                        // 自动回退到设备流
                        ToastService.Info($"本地回调失败（{ex.Message}），自动切换到设备流…");
                        _ = ShowDeviceFlowAsync(ct);
                    }
                );
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消/会话终止：确保对话框关闭，tcs 未设置时避免悬挂（超时已由
            // OAuthLoopbackException 分支处理并回退设备流，此处只负责 OCE 兜底）；
            // 关框经 PostIfAttached——会话终止（退出窗口）时续体不撞已脱离的 Dispatcher
            if (tcs.TrySetResult(false))
                PostIfAttached(dialog.Close);
        }
        catch (Exception ex)
        {
            if (tcs.TrySetResult(false))
            {
                statusText.Value = $"❗ 本地回调失败: {ex.Message}";
                RunAfterDelay(
                    1500,
                    () =>
                    {
                        dialog.Close();

                        ToastService.Info("本地回调失败，自动切换到设备流…");
                        _ = ShowDeviceFlowAsync(ct);
                    }
                );
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 形态 A：设备授权流程（回退 / --device / SSH）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 设备授权流程（CHANGE_07 原逻辑）— 二维码 + 用户码 + 轮询。
    /// </summary>
    private async Task<bool> ShowDeviceFlowAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();

        // Step 1: 请求设备授权（D5：走 ClientPlatform.DeviceAuthorizationClient）
        var deviceName = Environment.MachineName;
        var deviceClient = new DeviceAuthorizationClient(
            new HttpClient { Timeout = CloudTimings.LoginHttpTimeout }, // 登录超时共享收口（CloudTimings）
            _baseUrl
        );

        DeviceAuthorization authResult;
        try
        {
            authResult = await deviceClient.AuthorizeAsync(deviceName, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // LoginConnectTimeout（10s）握手超时 → 明确提示，避免「The operation was canceled.」误导
            if (
                _toastThrottle is null
                || !_toastThrottle.ShouldSuppress(
                    CloudConnectionState.Unavailable,
                    DateTimeOffset.UtcNow
                )
            )
                ToastService.Error("无法连接到云端（连接超时），请检查网络后重试。");
            return false;
        }
        catch (Exception ex)
        {
            // 同一降级原因（Unavailable）在冷却窗口内只提示一次，避免离线时连续重试 :cloud login 重复轰炸
            if (
                _toastThrottle is null
                || !_toastThrottle.ShouldSuppress(
                    CloudConnectionState.Unavailable,
                    DateTimeOffset.UtcNow
                )
            )
                ToastService.Error($"无法连接到云端: {ex.Message}");
            return false;
        }

        var userCode = FormatUserCode(authResult.UserCode);
        var verificationUri = authResult.VerificationUri;

        // ★ 自动打开浏览器
        OpenBrowser(verificationUri);

        // Step 2: 构建对话框 UI
        var qrText = TerminalQr.Render(verificationUri) ?? "（二维码生成失败，请手动输入用户码）";
        var qrBlock = new TextBlock(qrText)
            .TextAlignment(TextAlignment.Center)
            .HorizontalAlignment(Align.Center);

        var statusText = new State<string?>("⏳ 等待扫码确认…");
        var statusBlock = new Markup(() => $"[dim]{statusText.Value}[/]").HorizontalAlignment(
            Align.Center
        );

        // 手动输入 API Key（初始隐藏）
        var apiKeyInput = new MaskedInput { Template = "________________________________" };
        var apiKeyPanel = new Padder
        {
            Content = apiKeyInput,
            Padding = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = Align.Center,
            IsVisible = false,
        };

        var content = new VStack(
            qrBlock,
            new Markup(""),
            new Markup($"[dim]浏览器打开:[/] {verificationUri}"),
            new Markup($"[dim]输入用户码:  [/] [bold]{userCode}[/]"),
            new Markup(""),
            statusBlock,
            apiKeyPanel
        ).Spacing(0);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("🌐 Mythology Cloud 登录（设备流）")
            .Width(52)
            .MinHeight(20)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(PanelThemes.CreateDialogContent(content));

        var closeBtn = PanelThemes.CreateDialogCloseButton(() =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        });

        // 手动输入按钮
        var manualBtn = new Button(new Markup("[dim]手动输入[/]")).Style(
            ThemeStyles.BuildButton(PanelThemes.Current)
        );
        manualBtn.Click(() =>
        {
            apiKeyPanel.IsVisible = !apiKeyPanel.IsVisible;
            if (apiKeyPanel.IsVisible)
                dialog.App?.Focus(apiKeyInput);
        });

        // 确认手动输入（§14 P2：异步远程双重校验，保存前 Probe）
        void ConfirmManualKey() =>
            _ = SubmitManualKeyAsync(apiKeyInput.Value, statusText, tcs, dialog);

        var confirmBtn = new Button(new Markup("[bold]确认[/]")).Style(
            ThemeStyles.BuildButtonAccent(PanelThemes.Current)
        );
        confirmBtn.Click(ConfirmManualKey);

        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(() =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        });

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(new HStack(cancelBtn, manualBtn, confirmBtn).Spacing(2));

        dialog.KeyDown(
            (_, e) =>
            {
                switch (e.Key)
                {
                    case TerminalKey.Escape:
                        tcs.TrySetResult(false);
                        dialog.Close();
                        e.Handled = true;
                        break;
                    case TerminalKey.Enter when apiKeyPanel.IsVisible:
                        ConfirmManualKey();
                        e.Handled = true;
                        break;
                }
            }
        );

        // Step 3: 显示对话框 + 启动后台轮询
        dialog.Show();

        var pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(
            _session,
            deviceClient,
            authResult.DeviceCode,
            authResult.PollIntervalSeconds,
            authResult.ExpiresInSeconds,
            statusText,
            tcs,
            dialog,
            pollCts.Token
        );

        // Step 4: 等待用户操作或轮询结果
        try
        {
            return await tcs.Task;
        }
        finally
        {
            await pollCts.CancelAsync();
            pollCts.Dispose();
        }
    }

    /// <summary>
    /// 后台轮询设备授权状态（D5：走 ClientPlatform.DeviceAuthorizationClient），
    /// 更新 UI 文本，成功/超时时自动关闭对话框。
    /// </summary>
    private static async Task PollLoopAsync(
        CloudConnectionService session,
        DeviceAuthorizationClient deviceClient,
        string deviceCode,
        int pollIntervalSeconds,
        int expiresInSeconds,
        State<string?> statusText,
        TaskCompletionSource<bool> tcs,
        Dialog dialog,
        CancellationToken ct
    )
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(TimeSpan.FromSeconds(expiresInSeconds));

        try
        {
            DeviceToken? result = null;
            while (!deadlineCts.IsCancellationRequested)
            {
                result = await deviceClient.PollTokenAsync(deviceCode, deadlineCts.Token);
                if (result is not null)
                    break;
                // SSE 流关闭无终态 / legacy 轮询 pending → 间隔后重试
                await Task.Delay(pollIntervalSeconds * 1000, deadlineCts.Token);
            }

            if (result is not null && tcs.TrySetResult(true))
            {
                // §13 Tier 2：设备 token 携带服务端 expiresAt → 优先采用真值，缺失回退 +30d（与 Desktop 对齐）
                session.Save(
                    CreateServerSession(
                        result.ApiKey,
                        result.PublicId,
                        result.WorkspaceId,
                        result.Plan,
                        result.ExpiresAt
                    )
                );
                statusText.Value = "✅ 授权成功！";
                RunAfterDelay(800, dialog.Close);
            }
            else if (tcs.TrySetResult(false))
            {
                statusText.Value = "授权超时，请重新运行 :cloud login。";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时（deadlineCts 到期），非用户取消
            if (tcs.TrySetResult(false))
                statusText.Value = "授权超时，请重新运行 :cloud login。";
        }
        catch (OperationCanceledException)
        {
            // 用户取消，tcs 已由关闭回调设置
        }
        catch (Exception ex)
        {
            if (tcs.TrySetResult(false))
                statusText.Value = $"授权失败: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 共享工具方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 手动 API Key 提交（兜底）：格式校验 + 解析 publicId + **保存前同步远程 Probe**（§14 P2，
    /// 与 Desktop 双重校验对齐）。本地回调与设备流两个形态共用。
    /// - Authenticating（无效/已吊销）→ 拒绝不落盘，对话框停留供重试；
    /// - Connected → 落盘登录；
    /// - 云端不可达/限流/配额 → 离线暂存（与 Desktop 一致：允许落盘，稍后由心跳自动校验）。
    /// </summary>
    private async Task SubmitManualKeyAsync(
        string? rawApiKey,
        State<string?> statusText,
        TaskCompletionSource<bool> tcs,
        Dialog dialog
    )
    {
        var apiKey = rawApiKey?.Trim();
        var publicId = ApiKeyParser.TryGetPublicId(apiKey);
        if (publicId is null)
        {
            statusText.Value = "API Key 格式无效，应以 mk_live_ 开头。";
            return;
        }

        statusText.Value = "🔍 正在校验 API Key…";
        var state = await _session.ValidateKeyAsync(apiKey!);

        if (state == CloudConnectionState.Authenticating)
        {
            // 服务端拒绝 → 不落盘（与 Desktop：无效 Key 不保存）
            statusText.Value = "❗ API Key 无效或已吊销。";
            return;
        }

        // 手动 Key：ExpiresAt = null 永不本地过期（有效性完全由服务端 401/403 裁决，与 Desktop 对齐）
        _session.Save(CreateManualKeySession(apiKey!, publicId));
        statusText.Value =
            state == CloudConnectionState.Connected
                ? "✅ API Key 有效，已保存！"
                : "✅ 已保存（云端暂不可达，稍后自动校验）。";
        tcs.TrySetResult(true);
        dialog.Close();
    }

    /// <summary>
    /// 延时后在 UI 线程执行收尾操作（自动关框 / 回退提示）；应用已退出（Dispatcher 脱离）时静默丢弃。
    /// 为什么不能裸 await Task.Delay + 续体：演示延时可能晚于应用关闭完成——续体会被 Post 回
    /// 已脱离运行中 TerminalApp 的 Dispatcher（Dispatcher.Post 抛 InvalidOperationException →
    /// 线程池未处理异常 → 进程中止）。本方法让等待停在线程池（ConfigureAwait(false) 不再回投），
    /// 再由 <see cref="PostIfAttached"/> 守卫回投。
    /// </summary>
    private static void RunAfterDelay(int milliseconds, Action action) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(milliseconds).ConfigureAwait(false);
            PostIfAttached(action);
        });

    /// <summary>向 UI Dispatcher 投递操作；未附着运行中的 TerminalApp（应用关闭/未启动）时丢弃。</summary>
    private static void PostIfAttached(Action action)
    {
        try
        {
            XenoAtom.Terminal.UI.Threading.Dispatcher.Current.Post(action);
        }
        catch (InvalidOperationException)
        {
            // 没有运行中的 TerminalApp：没有 UI 需要收尾，直接丢弃（同 TodoPanel 守卫语义）
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 会话构建（internal 测试缝：Tui.Tests 经 InternalsVisibleTo 访问，
    // 与 MythMark 的 internal 测试缝同模式）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 构建服务端签发会话（OAuth/设备流，§13 Tier 2 与 Desktop ResolveExpiry 对齐）：
    /// 服务端 expiresAt 优先采用真值，缺失回退 now + 30d（共享常量 <see cref="CloudTimings.SessionLifetime"/>）。
    /// 过去值原样采用（矛盾响应由后续 RenewIfDue 防御回退滑动，与 Desktop 一致）。
    /// </summary>
    internal static CloudSession CreateServerSession(
        string apiKey,
        string publicId,
        string workspaceId,
        string plan,
        DateTimeOffset? serverExpiresAt
    ) =>
        new(
            apiKey,
            publicId,
            workspaceId,
            plan,
            DateTimeOffset.UtcNow,
            serverExpiresAt ?? DateTimeOffset.UtcNow.Add(CloudTimings.SessionLifetime)
        );

    /// <summary>
    /// 构建手动 Key 会话（兜底）：ExpiresAt = null 永不本地过期，
    /// 有效性完全由服务端 401/403 裁决（与 Desktop 手动 Key 语义对齐）。
    /// </summary>
    internal static CloudSession CreateManualKeySession(string apiKey, string publicId) =>
        new(apiKey, publicId, "", "free", DateTimeOffset.UtcNow);

    private static string FormatUserCode(string code) =>
        code.Length == 8 ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>
    /// 跨平台打开浏览器。失败静默。
    /// </summary>
    private static void OpenBrowser(string url)
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
        }
        catch
        {
            // 无默认浏览器/受限环境 → 忽略
        }
    }
}
