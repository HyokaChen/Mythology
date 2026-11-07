namespace Core.Interactions;

/// <summary>
/// 变体效果执行器：用单一 idle gate 串行执行 host 回调，
/// 并把 Running/Applied/Failed 写回 InteractionHub。
/// Host 只负责实际副作用（写章节、追加上下文、预填输入等）。
/// </summary>
public sealed class VariantEffectExecutor : IDisposable
{
    private readonly InteractionHub _hub;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VariantEffectExecutor(InteractionHub hub)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    }

    public async Task ExecuteAsync(
        string interactionId,
        VariantEffect effect,
        Func<VariantEffect, Task> execute,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(execute);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = _hub.Get(interactionId);
            if (snapshot?.VariantReview is not { } review)
                return;
            if (!string.Equals(review.Effect?.EffectId, effect.EffectId, StringComparison.Ordinal))
                return;
            if (review.EffectState == VariantEffectState.Applied)
                return;

            var running = _hub.SetVariantEffectState(
                interactionId,
                effect.EffectId,
                VariantEffectState.Running
            );
            if (running != SubmitStatus.Accepted)
                return;

            try
            {
                await execute(effect).ConfigureAwait(false);
                _hub.SetVariantEffectState(
                    interactionId,
                    effect.EffectId,
                    VariantEffectState.Applied
                );
            }
            catch (Exception ex)
            {
                _hub.SetVariantEffectState(
                    interactionId,
                    effect.EffectId,
                    VariantEffectState.Failed,
                    ex.Message
                );
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
