using Core.Interactions;
using Core.Shared;
using LlmTornado.Code;
using Tui.Controls;
using XenoAtom.Terminal.UI.Controls;

namespace Tui;

/// <summary>
/// TUI 交互呈现器（Core 快照 → 终端对话框 → Core Submit）。
/// 只做展示/认领/提交；状态与唯一结果由 InteractionHub 保证。
/// </summary>
public sealed partial class TuiApp
{
    private const string TuiHostId = "tui";
    private readonly Queue<string> _interactionQueue = new();
    private bool _presentingInteraction;
    private string? _answersAppendedInteractionId;

    private void WireInteractionEvents()
    {
        if (_interactionHub is null)
            return;
        _interactionHub.ReleaseHost(TuiHostId);
        _interactionHub.Changed += OnInteractionChanged;
        foreach (var snapshot in _interactionHub.ListOpen(_sessionId))
            OnInteractionChanged(snapshot);
    }

    private void UnwireInteractionEvents()
    {
        if (_interactionHub is null)
            return;
        _interactionHub.Changed -= OnInteractionChanged;
        _interactionHub.CancelHost(TuiHostId);
        _interactionQueue.Clear();
    }

    private void OnInteractionChanged(InteractionSnapshot snapshot) =>
        PostToUi(() => HandleInteraction(snapshot));

    private void HandleInteraction(InteractionSnapshot snapshot)
    {
        if (_interactionHub is null)
            return;
        if (_weChatBridge?.IsTurnActive == true)
            return;
        if (!string.IsNullOrEmpty(_sessionId) && snapshot.SessionId != _sessionId)
            return;
        if (snapshot.Kind is not (InteractionKind.QuestionSet or InteractionKind.VariantReview))
            return;

        if (snapshot.IsTerminal)
        {
            if (
                snapshot.State == InteractionState.Resolved
                && snapshot.Questions is { IsComplete: true }
                && !string.Equals(
                    _answersAppendedInteractionId,
                    snapshot.Id,
                    StringComparison.Ordinal
                )
            )
            {
                _answersAppendedInteractionId = snapshot.Id;
                var answerText = AskUserReplyFormatter.Format(
                    snapshot.Questions.Items.Select(i => i.Question)
                );
                if (!string.IsNullOrEmpty(answerText))
                    AppendAskUserAnswersToConversation(
                        snapshot.Questions.Items.Select(i => i.Question).ToList()
                    );
            }
            return;
        }

        if (snapshot.State != InteractionState.Available)
            return;

        var status = _interactionHub.TryClaim(snapshot.Id, TuiHostId, out var claimed);
        if (status != SubmitStatus.Accepted || claimed?.Claim is null)
            return;

        if (!_interactionQueue.Contains(snapshot.Id))
            _interactionQueue.Enqueue(snapshot.Id);
        _ = PumpInteractionsAsync();
    }

    private async Task PumpInteractionsAsync()
    {
        if (_presentingInteraction || _interactionHub is null)
            return;
        _presentingInteraction = true;
        try
        {
            while (_interactionQueue.Count > 0)
            {
                var id = _interactionQueue.Dequeue();
                var snapshot = _interactionHub.Get(id);
                if (snapshot is null || snapshot.IsTerminal || snapshot.Claim?.HostId != TuiHostId)
                    continue;
                if (snapshot.Kind == InteractionKind.QuestionSet)
                    await PresentQuestionsAsync(snapshot);
                else if (snapshot.Kind == InteractionKind.VariantReview)
                    await PresentVariantReviewAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "present interaction");
        }
        finally
        {
            _presentingInteraction = false;
        }
    }

    /// <summary>把已完成的回答追加为一条 user 消息；格式与工具结果保持一致。</summary>
    private Task AppendAskUserAnswersToConversation(IReadOnlyList<PendingQuestion> questions)
    {
        var answerText = AskUserReplyFormatter.Format(questions);
        if (string.IsNullOrEmpty(answerText))
            return Task.CompletedTask;
        _agent.AppendMessage(new LlmTornado.Chat.ChatMessage(ChatMessageRoles.User, answerText));
        return Task.CompletedTask;
    }

    private async Task PresentQuestionsAsync(InteractionSnapshot snapshot)
    {
        var lease = snapshot.Claim!.LeaseId;
        while (true)
        {
            var current = _interactionHub!.Get(snapshot.Id);
            if (current is null || current.IsTerminal || current.Claim?.HostId != TuiHostId)
                return;
            var item = current.Questions?.Items.FirstOrDefault(i =>
                i.Outcome == QuestionOutcome.Unanswered
            );
            if (item is null)
                return;

            await AskUserDialog.ShowAsync(_interactionHub, current.Id, lease, item);
            // 对话框关闭后再次读 Core：可能已回答/取消/会话切走。
        }
    }

    /// <summary>变体审阅：唯一弹窗路径；结果与效果 plan 都写回 Core。</summary>
    private async Task PresentVariantReviewAsync(InteractionSnapshot snapshot)
    {
        var review = snapshot.VariantReview;
        var lease = snapshot.Claim?.LeaseId;
        if (review is null || string.IsNullOrEmpty(lease))
            return;

        _lastExploreDetails = review.Details;
        await PersistVariantSetAsync(review.Details);
        var decision = await VariantReviewDialog.ShowAsync(review.Details, _store);

        var result = _interactionHub!.SubmitVariantDecision(snapshot.Id, decision, lease);
        if (result.Status != SubmitStatus.Accepted)
            return;
        var effect = result.Effect ?? result.Snapshot?.VariantReview?.Effect;
        if (effect is null)
            return;

        var executor = _variantEffectExecutor;
        if (executor is null)
            return;

        try
        {
            await executor.ExecuteAsync(snapshot.Id, effect, ExecuteVariantEffectAsync);
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "apply variant effect");
            ToastService.Info($"应用变体失败：{ex.Message}");
        }
    }
}
