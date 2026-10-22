// 功能：微信通道对外门面 —— 宿主的唯一入口（TUI / Desktop 都只用这个类）
//
// 分层：协议（WeChatIlinkClient，internal）+ 落盘（WeChatBindingStore）+ 编排（WeChatBindingFlow）
// 都是本程序集内部实现，宿主只看到 Status / BindAsync / Unbind / TryRestore。
//
// 单实例：锁的生命周期 = "本进程负责这个 bot"。绑定前和长轮询前都要持有它，
// 否则两个入口会同时轮询同一个 bot（消息重复或丢失），或互相覆盖绑定凭证。

using Microsoft.Extensions.Logging;

namespace Channels.WeChat;

/// <summary>微信通道服务。</summary>
public sealed partial class WeChatChannelService : IDisposable
{
    private readonly WeChatIlinkClient _client;
    private readonly WeChatBindingStore _store;
    private readonly WeChatBindingFlow _flow;
    private readonly ILogger<WeChatChannelService>? _logger;
    private readonly string _directory;
    private readonly string _ownerLabel;
    private readonly object _gate = new();

    private WeChatChannelLock? _channelLock;
    private WeChatBindingState _state = new();
    private bool _disposed;

    public WeChatChannelService(
        string directory,
        string ownerLabel,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory? loggerFactory = null
    )
    {
        _directory = directory;
        _ownerLabel = ownerLabel;
        _logger = loggerFactory?.CreateLogger<WeChatChannelService>();
        _client = new WeChatIlinkClient(
            httpClientFactory,
            loggerFactory?.CreateLogger<WeChatIlinkClient>()
        );
        _store = new WeChatBindingStore(
            directory,
            loggerFactory?.CreateLogger<WeChatBindingStore>()
        );
        Status = new WeChatChannelStatus();
        _flow = new WeChatBindingFlow(
            _client,
            _store,
            Status,
            loggerFactory?.CreateLogger<WeChatBindingFlow>()
        );
    }

    /// <summary>状态快照源（绑定卡片 / 对话框据此渲染）。</summary>
    public WeChatChannelStatus Status { get; }

    /// <summary>最近一次读到的落盘状态。</summary>
    public WeChatBindingState State => Volatile.Read(ref _state);

    /// <summary>本进程是否持有通道独占锁（持有 = 本进程负责轮询）。</summary>
    public bool HasOwnership => Volatile.Read(ref _channelLock) is not null;

    /// <summary>
    /// 启动恢复：读盘，有可用凭证就进入已绑定态并尝试接管轮询权。
    /// 不自动轮询——轮询由宿主的运行循环负责（通道服务不自己起后台线程）。
    /// </summary>
    /// <returns>true = 已绑定且本进程取得轮询权；false 见 <see cref="WeChatChannelStatus"/>。</returns>
    public bool TryRestore()
    {
        var state = _store.Load();
        Volatile.Write(ref _state, state);

        if (!state.IsBound)
        {
            Status.MarkIdle();
            return false;
        }

        if (EnsureOwnership())
        {
            Status.MarkPolling(state.Credential);
            return true;
        }

        // 已绑定但抢不到轮询权：另一个入口在用。让用户看到可操作的原因，而不是假装正常。
        Status.MarkFailed(
            "微信通道已被另一个入口占用（同一个 bot 只允许一个进程轮询）",
            state.Credential
        );
        return false;
    }

    /// <summary>
    /// 扫码绑定。成功时凭证已落盘、相位切到 Polling。
    /// </summary>
    public async Task<bool> BindAsync(CancellationToken cancellationToken = default)
    {
        if (!EnsureOwnership())
        {
            Status.MarkFailed(
                "微信通道已被另一个入口占用，请先关闭它再绑定",
                Volatile.Read(ref _state).Credential
            );
            return false;
        }

        var bound = await _flow.BindAsync(_ownerLabel, cancellationToken);
        if (bound)
            Volatile.Write(ref _state, _store.Load());
        return bound;
    }

    /// <summary>
    /// 解绑：清掉落盘凭证并释放轮询权。
    /// </summary>
    /// <returns>false = 本进程没有轮询权（另一个入口在用），未做任何修改。</returns>
    public bool Unbind()
    {
        // 没有轮询权就拒绝：另一个入口可能正拿内存里的凭证轮询，
        // 单方面删掉凭证文件会让它处于"没有凭证却还在轮询"的悬挂态。
        if (!HasOwnership)
        {
            _logger?.LogInformation("微信解绑被拒绝：本进程未持有轮询权");
            return false;
        }

        _store.Clear();
        Volatile.Write(ref _state, new WeChatBindingState());
        ReleaseOwnership();
        Status.MarkIdle();
        return true;
    }

    /// <summary>
    /// 绑定失效（长轮询收到 -14）。不自动重试：token 已无效，只能重新扫码，
    /// 静默重试一万次也不会成功，反而让用户以为“还在跑”。
    /// </summary>
    public void ReportBindingExpired()
    {
        _logger?.LogWarning("微信绑定已失效（errcode -14），需要重新扫码");
        Status.MarkFailed("绑定已失效，请重新扫码（/wechat bind）");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseOwnership();
    }

    /// <summary>取通道独占锁（幂等）。已被别的进程占用时返回 false。</summary>
    private bool EnsureOwnership()
    {
        lock (_gate)
        {
            if (_channelLock is not null)
                return true;

            _channelLock = WeChatChannelLock.TryAcquire(_directory, _ownerLabel, _logger);
            if (_channelLock is not null)
                _logger?.LogInformation("微信通道已取得独占权: owner={Owner}", _ownerLabel);
            return _channelLock is not null;
        }
    }

    private void ReleaseOwnership()
    {
        lock (_gate)
        {
            _channelLock?.Dispose();
            _channelLock = null;
        }
    }
}
