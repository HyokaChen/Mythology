using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Core.Interactions;
using Desktop.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desktop.Services;

/// <summary>
/// Desktop 的唯一交互呈现器：订阅 Core <see cref="InteractionHub.Changed"/>，
/// 认领展示权、把快照渲染成窗口、把用户结果提交回 Core。
/// 这里没有业务状态；去重/唯一结果由 InteractionHub 保证。
/// </summary>
public sealed class InteractionPresenter : IDisposable
{
    public const string HostId = "desktop";

    private readonly InteractionHub _hub;
    private readonly Func<string?> _activeSession;
    private readonly Func<Window?> _owner;
    private readonly ILogger _logger;
    private readonly Func<InteractionSnapshot, Task>? _onVariantReview;
    private readonly Dictionary<string, AskUserWindow> _questionWindows = new(
        StringComparer.Ordinal
    );
    private readonly Queue<string> _queue = new();
    private readonly object _gate = new();
    private bool _presenting;
    private bool _disposed;

    public InteractionPresenter(
        InteractionHub hub,
        Func<string?> activeSession,
        Func<Window?> owner,
        ILogger<InteractionPresenter>? logger = null,
        Func<InteractionSnapshot, Task>? onVariantReview = null
    )
    {
        _hub = hub;
        _activeSession = activeSession;
        _owner = owner;
        _logger = logger ?? NullLogger<InteractionPresenter>.Instance;
        _onVariantReview = onVariantReview;
        _hub.Changed += OnChanged;
        // 启动时清理上一次 adapter 实例可能留下的 lease，并重新同步活动交互。
        _hub.ReleaseHost(HostId);
        foreach (var snapshot in _hub.ListOpen(_activeSession()))
            OnChanged(snapshot);
    }

    private void OnChanged(InteractionSnapshot snapshot)
    {
        if (_disposed)
            return;
        if (Dispatcher.UIThread.CheckAccess())
            Handle(snapshot);
        else
            Dispatcher.UIThread.Post(() => Handle(snapshot));
    }

    private void Handle(InteractionSnapshot snapshot)
    {
        if (_disposed)
            return;

        var active = _activeSession();
        if (!string.IsNullOrEmpty(active) && snapshot.SessionId != active)
            return;

        if (snapshot.IsTerminal)
        {
            if (_questionWindows.Remove(snapshot.Id, out var win))
                win.CloseFromCore();
            return;
        }

        if (snapshot.Kind is not (InteractionKind.QuestionSet or InteractionKind.VariantReview))
            return;

        if (snapshot.State != InteractionState.Available)
            return;

        var status = _hub.TryClaim(snapshot.Id, HostId, out var claimed);
        if (status != SubmitStatus.Accepted || claimed?.Claim is null)
            return;

        lock (_gate)
        {
            if (!_queue.Contains(snapshot.Id))
                _queue.Enqueue(snapshot.Id);
        }
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_presenting)
            return;
        _presenting = true;
        try
        {
            while (true)
            {
                string id;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                        break;
                    id = _queue.Dequeue();
                }

                var snapshot = _hub.Get(id);
                if (
                    snapshot is null
                    || snapshot.IsTerminal
                    || snapshot.State != InteractionState.Claimed
                )
                    continue;
                if (snapshot.Claim?.HostId != HostId)
                    continue;

                if (snapshot.Kind == InteractionKind.QuestionSet)
                    await PresentQuestionSetAsync(snapshot);
                else if (snapshot.Kind == InteractionKind.VariantReview)
                    await PresentVariantReviewAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Desktop 交互呈现失败");
        }
        finally
        {
            _presenting = false;
        }
    }

    private async Task PresentQuestionSetAsync(InteractionSnapshot snapshot)
    {
        var lease = snapshot.Claim!.LeaseId;
        try
        {
            while (true)
            {
                var current = _hub.Get(snapshot.Id);
                if (
                    current is null
                    || current.IsTerminal
                    || current.State != InteractionState.Claimed
                )
                    return;
                var items = current
                    .Questions?.Items.Where(i => i.Outcome == QuestionOutcome.Unanswered)
                    .ToList();
                if (items is not { Count: > 0 })
                    return;

                var win = new AskUserWindow(_hub, current.Id, lease, items);
                _questionWindows[current.Id] = win;
                var owner = _owner();
                if (owner is not null)
                {
                    DialogTheme.FitToOwner(
                        win,
                        owner,
                        0.68,
                        640,
                        1200,
                        0.72,
                        280,
                        860,
                        autoHeight: true
                    );
                    await win.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource();
                    win.Closed += (_, _) => tcs.TrySetResult();
                    win.Show();
                    await tcs.Task;
                }

                _questionWindows.Remove(current.Id);
                var after = _hub.Get(snapshot.Id);
                if (after is null || after.IsTerminal)
                    return;
            }
        }
        finally
        {
            if (_questionWindows.Remove(snapshot.Id, out var stale))
                stale.CloseFromCore();
        }
    }

    private async Task PresentVariantReviewAsync(InteractionSnapshot snapshot)
    {
        var lease = snapshot.Claim?.LeaseId;
        if (string.IsNullOrEmpty(lease))
            return;
        if (_onVariantReview is null)
        {
            _hub.Release(snapshot.Id, lease);
            return;
        }

        try
        {
            await _onVariantReview(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Desktop 变体呈现失败");
            _hub.Cancel(snapshot.Id, "desktop-variant-failed", cancelWaiters: false, lease);
        }
    }

    /// <summary>会话切换：取消旧 session 的 Core 交互（由 MainWindow 调用 CancelSession），并清理队列。</summary>
    public void OnSessionChanged()
    {
        lock (_gate)
            _queue.Clear();
        foreach (var win in _questionWindows.Values)
            win.CloseFromCore();
        _questionWindows.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hub.Changed -= OnChanged;
        _hub.CancelHost(HostId);
        foreach (var win in _questionWindows.Values)
            win.CloseFromCore();
        _questionWindows.Clear();
    }
}
