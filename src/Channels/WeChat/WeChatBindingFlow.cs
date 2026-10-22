// 功能：扫码绑定编排 —— 取二维码 → 轮询状态 → 落盘凭证
//
// 覆盖官方状态机实测的全部取值：wait / scaned / confirmed / expired /
// scaned_but_redirect / need_verifycode / verify_code_blocked / binded_redirect。
//
// 不负责：画二维码（宿主的二维码渲染器）、长轮询（WeChatChannelService）、单实例锁
// （锁的生命周期 = 通道进程生命周期，由通道服务持有，不在这个流程里取放）。

using Microsoft.Extensions.Logging;

namespace Channels.WeChat;

/// <summary>扫码绑定流程。</summary>
internal sealed class WeChatBindingFlow(
    WeChatIlinkClient client,
    WeChatBindingStore store,
    WeChatChannelStatus status,
    ILogger<WeChatBindingFlow>? logger = null
)
{
    /// <summary>
    /// 走完一次扫码绑定。成功时凭证已落盘、相位已切到 <see cref="WeChatPhase.Polling"/>。
    /// </summary>
    /// <returns>true = 已绑定；false = 失败（相位 Failed）或取消（相位回 Idle）。</returns>
    public async Task<bool> BindAsync(
        string ownerLabel,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await RunAsync(ownerLabel, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status.MarkIdle();
            throw;
        }
        catch (OperationCanceledException)
        {
            // 不是宿主取消，而是内部请求超时：换成可读文案，别让用户看到 "The operation was canceled"
            logger?.LogWarning("微信绑定请求超时");
            status.MarkFailed("绑定失败：请求超时，请检查网络后重试");
            return false;
        }
        catch (Exception ex)
        {
            // 网络/协议异常不该让宿主崩：收敛成 Failed 相位，用户在卡片上能看到原因并重试
            logger?.LogWarning(ex, "微信绑定失败");
            status.MarkFailed($"绑定失败：{ex.Message}");
            return false;
        }
    }

    private async Task<bool> RunAsync(string ownerLabel, CancellationToken cancellationToken)
    {
        var state = store.Load();
        var baseUrl = WeChatConstants.FixedBaseUrl;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(WeChatConstants.LoginTimeoutMs);

        for (var refreshed = 0; refreshed <= WeChatConstants.MaxQrRefresh; refreshed++)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                status.MarkFailed("绑定超时（二维码长时间未确认）");
                return false;
            }

            var qr = await client.FetchQrCodeAsync(baseUrl, state.LocalTokens, cancellationToken);
            if (string.IsNullOrWhiteSpace(qr.Qrcode))
            {
                status.MarkFailed("服务端未返回二维码");
                return false;
            }

            status.ShowQr(qr.QrcodeImgContent ?? string.Empty, refreshed);

            // 内层：同一张二维码轮询到"确认 / 过期"为止。服务端每次挂起约 30s 是正常的空转。
            var expired = false;
            while (!expired)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    status.MarkFailed("绑定超时（二维码长时间未确认）");
                    return false;
                }

                var poll = await client.PollQrStatusAsync(
                    baseUrl,
                    qr.Qrcode,
                    verifyCode: null,
                    timeoutMs: WeChatConstants.QrPollTimeoutMs,
                    cancellationToken
                );

                switch (poll.Status)
                {
                    case "confirmed":
                        return SaveAndFinish(state, poll, baseUrl, ownerLabel);

                    case "expired":
                        logger?.LogInformation(
                            "二维码已过期，刷新第 {Refreshed} 次",
                            refreshed + 1
                        );
                        expired = true;
                        break;

                    case "scaned":
                        status.MarkScanned();
                        break;

                    case "scaned_but_redirect":
                        // 服务端要求换 IDC 主机：后续二维码状态查询走新主机
                        var host = NormalizeHost(poll.RedirectHost);
                        if (host is not null)
                            baseUrl = host;
                        status.MarkConnecting();
                        logger?.LogInformation("服务端要求切换主机: {Host}", host ?? "(未给)");
                        break;

                    case "need_verifycode":
                    case "verify_code_blocked":
                        status.MarkFailed(
                            "微信要求安全验证码，当前版本不支持（请在微信里重新发起绑定）"
                        );
                        return false;

                    case "binded_redirect":
                        status.MarkFailed("该 bot 已绑定到其它客户端，请先在原客户端解绑");
                        return false;

                    default:
                        // wait 及未知状态：继续轮询。
                        // 未知状态不当作终止——服务端新增中间态时，误判失败比多等一轮贵得多。
                        break;
                }
            }
        }

        status.MarkFailed($"二维码多次过期（{WeChatConstants.MaxQrRefresh} 次），请重试");
        return false;
    }

    /// <summary>校验响应齐备后落盘并切相位。</summary>
    private bool SaveAndFinish(
        WeChatBindingState state,
        WeChatQrStatusResponse poll,
        string fallbackBaseUrl,
        string ownerLabel
    )
    {
        if (string.IsNullOrWhiteSpace(poll.BotToken) || string.IsNullOrWhiteSpace(poll.ILinkUserId))
        {
            // 半份凭证一旦落盘，下次启动会反复进入"恢复中"却永远不可用
            logger?.LogWarning(
                "绑定响应缺少 bot_token 或 ilink_user_id，拒绝落盘: botToken={HasToken}, userId={HasUser}",
                !string.IsNullOrWhiteSpace(poll.BotToken),
                !string.IsNullOrWhiteSpace(poll.ILinkUserId)
            );
            status.MarkFailed("绑定响应不完整，请重试");
            return false;
        }

        var credential = new WeChatCredential
        {
            BaseUrl = string.IsNullOrWhiteSpace(poll.BaseUrl) ? fallbackBaseUrl : poll.BaseUrl,
            BotToken = poll.BotToken,
            AccountId = poll.ILinkBotId ?? string.Empty,
            PeerUserId = poll.ILinkUserId,
            BoundAt = DateTimeOffset.UtcNow,
        };

        store.Save(
            (state with { Credential = credential, OwnerLabel = ownerLabel }).WithLocalToken(
                credential.BotToken
            )
        );
        logger?.LogInformation("微信绑定成功: account={Account}", credential.AccountId);
        status.MarkPolling(credential);
        return true;
    }

    /// <summary>把 redirect_host 归一成基地址（服务端可能只给主机名）。</summary>
    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        return host.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? host
            : $"https://{host}";
    }
}
