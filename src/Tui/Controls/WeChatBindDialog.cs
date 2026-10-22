// 功能：微信绑定对话框 —— 终端二维码 + 扫码状态 + 取消
//
// 与 CloudLoginDialog 的设备流对话框同构（State + 后台任务 + TCS + Esc 取消），
// 差别只在二维码是**响应式**的：绑定过程中二维码会过期重出，不能只在打开时渲染一次。

using Channels.WeChat;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>微信扫码绑定对话框。</summary>
public sealed class WeChatBindDialog(WeChatChannelService channel)
{
    /// <summary>
    /// 显示对话框直到扫码完成或用户取消（Esc / 关闭按钮）。
    /// </summary>
    /// <returns>true = 绑定成功。</returns>
    public async Task<bool> ShowAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        var qrState = new State<string>(RenderQr(channel.Status.Snapshot));
        var statusState = new State<string>(Describe(channel.Status.Snapshot));

        var qrBlock = new TextBlock(() => qrState.Value)
            .TextAlignment(TextAlignment.Center)
            .HorizontalAlignment(Align.Center);

        var content = new VStack(
            new Markup("[bold]💬 绑定微信[/]").HorizontalAlignment(Align.Center),
            new Markup(""),
            qrBlock,
            new Markup(""),
            new Markup("[dim]用微信扫码，扫完在手机上点确认。[/]").HorizontalAlignment(
                Align.Center
            ),
            new Markup(() => $"[dim]{statusState.Value}[/]").HorizontalAlignment(Align.Center)
        ).Spacing(0);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("💬 微信绑定")
            .Width(46)
            .MinHeight(26)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(PanelThemes.CreateDialogContent(content));

        var bindCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        void Cancel()
        {
            tcs.TrySetResult(false);
            dialog.Close();
        }

        var closeBtn = PanelThemes.CreateDialogCloseButton(Cancel);
        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(Cancel);

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(cancelBtn);

        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key != TerminalKey.Escape)
                    return;
                Cancel();
                e.Handled = true;
            }
        );

        // 状态事件来自后台线程：经 UiDispatch 回 UI 线程再改 State（State 有线程亲和）
        void OnStatusChanged() =>
            UiDispatch.PostIfAttached(() =>
            {
                var snapshot = channel.Status.Snapshot;
                qrState.Value = RenderQr(snapshot);
                statusState.Value = Describe(snapshot);
            });

        channel.Status.Changed += OnStatusChanged;

        dialog.Show();
        _ = RunBindAsync(bindCts.Token, statusState, tcs, dialog);

        try
        {
            return await tcs.Task;
        }
        finally
        {
            channel.Status.Changed -= OnStatusChanged;
            // 关框后必须取消：否则绑定流程会继续在后台轮询二维码
            await bindCts.CancelAsync();
            bindCts.Dispose();
        }
    }

    /// <summary>后台跑绑定流程，成功时刷新状态行并自动关框。</summary>
    private async Task RunBindAsync(
        CancellationToken ct,
        State<string> statusState,
        TaskCompletionSource<bool> tcs,
        Dialog dialog
    )
    {
        try
        {
            var bound = await channel.BindAsync(ct);
            if (!bound)
                return; // 失败相位已由状态行显示，留在框里让用户重试或取消

            if (!tcs.TrySetResult(true))
                return;

            statusState.Value = "✅ 绑定成功";
            // 让"成功"可见一瞬再关框（与 CloudLoginDialog 同节奏）
            await Task.Delay(800, CancellationToken.None).ConfigureAwait(false);
            UiDispatch.PostIfAttached(dialog.Close);
        }
        catch (OperationCanceledException)
        {
            // 用户取消或应用退出：关框由 finally 统一负责
        }
        catch (Exception ex)
        {
            UiDispatch.PostIfAttached(() => statusState.Value = $"❗ {ex.Message}");
        }
    }

    /// <summary>渲染二维码；无码时给一行占位而不是留白（留白会让人以为框坏了）。</summary>
    private static string RenderQr(WeChatChannelSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.QrContent))
            return snapshot.Phase is WeChatPhase.Polling or WeChatPhase.Restoring
                ? ""
                : "（正在获取二维码…）";

        return TerminalQr.Render(snapshot.QrContent) ?? "（二维码生成失败，请重试）";
    }

    private static string Describe(WeChatChannelSnapshot snapshot) =>
        snapshot.Phase switch
        {
            WeChatPhase.Restoring => "正在恢复绑定…",
            WeChatPhase.WaitingScan => "等待扫码…",
            WeChatPhase.Scanned => "已扫码，请在手机上点确认",
            WeChatPhase.Connecting => "正在保存绑定…",
            WeChatPhase.Polling => snapshot.PeerUserId is { Length: > 0 } peer
                ? $"已绑定（微信用户 {peer}）"
                : "已绑定",
            WeChatPhase.Failed => $"异常：{snapshot.Error}",
            _ => "未绑定",
        };
}
