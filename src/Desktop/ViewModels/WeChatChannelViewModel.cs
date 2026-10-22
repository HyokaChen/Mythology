// 功能：设置页「微信」卡片 ViewModel（状态 + 二维码 + 绑定/解绑）
//
// 与 TUI 的 WeChatBindDialog 共用同一个 WeChatChannelService：状态源唯一，
// 两个入口不可能显示不一致的绑定态。
//
// 二维码给的是内容字符串（liteapp 链接），在这里转成 PNG 位图给 Avalonia 显示；
// TUI 那边转成半块字符。渲染差异只存在于各自宿主。

using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Channels.WeChat;
using Lucide.Avalonia;
using QRCoder;
using ReactiveUI;

namespace Desktop.ViewModels;

/// <summary>微信绑定卡片。</summary>
public sealed class WeChatChannelViewModel : ViewModelBase, IDisposable
{
    private readonly WeChatChannelService? _channel;
    private string? _lastQrContent;
    private bool _disposed;

    public WeChatChannelViewModel(WeChatChannelService? channel)
    {
        _channel = channel;
        Refresh();

        if (_channel is not null)
            _channel.Status.Changed += OnStatusChanged;
    }

    /// <summary>状态文本（按相位组合）。</summary>
    public string StatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>绑定后的微信用户行（未绑定为空）。</summary>
    public string PeerText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>二维码位图（等待扫码时非空）。</summary>
    public Bitmap? QrImage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsQrVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsBound
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>握手进行中（按钮禁用）。</summary>
    public bool IsBusy
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string BindButtonText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "扫码绑定";

    /// <summary>扫码绑定。已在握手中再点 = 由通道服务内部取消旧的并重开。</summary>
    public async Task BindAsync()
    {
        if (_channel is null)
            return;

        await _channel.BindAsync();
        Refresh();
    }

    /// <summary>解除绑定。</summary>
    public bool Unbind()
    {
        if (_channel?.Unbind() != true)
            return false;

        Refresh();
        return true;
    }

    private void OnStatusChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        if (_disposed)
            return;

        var snapshot = _channel?.Status.Snapshot ?? new WeChatChannelSnapshot();

        IsQrVisible = snapshot.IsQrVisible;
        IsBound = snapshot.IsBound;
        IsBusy = snapshot.IsBusy;

        PeerText = snapshot.PeerUserId is { Length: > 0 } peer ? $"微信用户 {peer}" : string.Empty;
        BindButtonText = IsBound ? "重新扫码" : "扫码绑定";

        StatusText = snapshot.Phase switch
        {
            WeChatPhase.Restoring => "正在恢复绑定…",
            WeChatPhase.WaitingScan => "等待扫码…",
            WeChatPhase.Scanned => "已扫码，请在手机上点确认",
            WeChatPhase.Connecting => "正在保存绑定…",
            WeChatPhase.Polling => "已绑定，长轮询中",
            WeChatPhase.Failed => $"异常：{snapshot.Error}",
            _ => "未绑定",
        };

        UpdateQrImage(snapshot.QrContent);
    }

    private void UpdateQrImage(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            ClearQr();
            return;
        }

        // 同一二维码重复刷新（StateChanged 多次触发）不重建位图
        if (QrImage is not null && string.Equals(_lastQrContent, content, StringComparison.Ordinal))
            return;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(data);
            var bytes = png.GetGraphic(4);
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            QrImage?.Dispose();
            QrImage = bitmap;
            _lastQrContent = content;
        }
        catch
        {
            // 解码失败不展示二维码（状态文本仍给出相位，用户可重新扫码）
            ClearQr();
        }
    }

    private void ClearQr()
    {
        QrImage?.Dispose();
        QrImage = null;
        _lastQrContent = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_channel is not null)
            _channel.Status.Changed -= OnStatusChanged;

        ClearQr();
    }
}
