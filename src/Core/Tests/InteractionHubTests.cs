using Core.Interactions;
using Core.Shared;
using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// InteractionHub 状态机测试：唯一展示权、单次答案、取消语义、变体幂等与效果状态。
/// </summary>
public sealed class InteractionHubTests
{
    private static InteractionContext Context(string sessionId = "s1") => new("", sessionId);

    private static QuestionDef Choice(string header = "剧情走向") =>
        new()
        {
            Header = header,
            Question = "选一个？",
            Options = ["A", "B"],
            MultiSelect = false,
        };

    private static ExploreDetails Explore(string variantSetId = "variant-set-1") =>
        new()
        {
            VariantSetId = variantSetId,
            ExploreType = "style",
            Prompt = "explore prompt",
            Variants =
            [
                new VariantResult
                {
                    Id = "v1",
                    Label = "方案A",
                    Content = "正文A",
                },
            ],
        };

    [Fact]
    public void TryClaim_SecondHostIsRejected()
    {
        var hub = new InteractionHub();
        var interaction = hub.CreateQuestionSet(Context(), [Choice()]);

        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(interaction.Id, "desktop", out var first));
        Assert.Equal("desktop", first!.Claim?.HostId);

        Assert.Equal(
            SubmitStatus.AlreadyClaimed,
            hub.TryClaim(interaction.Id, "tui", out var second)
        );
        Assert.Equal("desktop", second?.Claim?.HostId);
    }

    [Fact]
    public void SubmitAnswer_ResolvesOnlyTheAnsweredSlot()
    {
        var hub = new InteractionHub();
        var created = hub.CreateQuestionSet(Context(), [Choice("第一"), Choice("第二")]);
        var firstId = created.Questions!.Items[0].Question.Id;
        var secondId = created.Questions.Items[1].Question.Id;

        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(created.Id, "desktop", out var claimed));
        var lease = claimed!.Claim!.LeaseId;

        var first = hub.SubmitQuestionAnswer(created.Id, firstId, ["A"], lease);
        Assert.Equal(SubmitStatus.Accepted, first.Status);
        var mid = first.Snapshot!;
        Assert.False(mid.IsTerminal);
        Assert.Equal(QuestionOutcome.Answered, mid.Questions!.Items[0].Outcome);
        Assert.True(mid.Questions.Items[0].Question.IsAnswered);
        Assert.Equal(QuestionOutcome.Unanswered, mid.Questions.Items[1].Outcome);
        Assert.Equal(secondId, Assert.Single(hub.GetPendingQuestions("s1")).Id);

        var duplicate = hub.SubmitQuestionAnswer(created.Id, firstId, ["A"], lease);
        Assert.Equal(SubmitStatus.AlreadyResolved, duplicate.Status);

        var second = hub.SubmitQuestionAnswer(created.Id, secondId, ["B"], lease);
        Assert.Equal(SubmitStatus.Accepted, second.Status);
        Assert.True(second.Snapshot!.IsTerminal);
        Assert.Null(second.Snapshot.Claim);
        Assert.Empty(hub.GetPendingQuestions("s1"));
    }

    [Fact]
    public void CancelQuestion_DoesNotMarkQuestionAnswered()
    {
        var hub = new InteractionHub();
        var created = hub.CreateQuestionSet(Context(), [Choice()]);
        var questionId = created.Questions!.Items[0].Question.Id;
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(created.Id, "desktop", out var claimed));

        var cancelled = hub.CancelQuestion(
            created.Id,
            questionId,
            claimed!.Claim!.LeaseId,
            "user-cancel"
        );

        Assert.Equal(SubmitStatus.Accepted, cancelled.Status);
        var item = Assert.Single(cancelled.Snapshot!.Questions!.Items);
        Assert.Equal(QuestionOutcome.Cancelled, item.Outcome);
        Assert.False(item.Question.IsAnswered);
        Assert.Null(item.Question.AnsweredAt);
        Assert.True(cancelled.Snapshot.IsTerminal);
    }

    [Fact]
    public void Cancel_TurnAbortDoesNotMarkQuestionsAnswered()
    {
        var hub = new InteractionHub();
        var created = hub.CreateQuestionSet(Context(), [Choice("第一"), Choice("第二")]);
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(created.Id, "desktop", out _));

        var cancelled = hub.Cancel(created.Id, "turn-abort", cancelWaiters: false);
        Assert.Equal(SubmitStatus.Accepted, cancelled.Status);
        Assert.Equal(InteractionState.Cancelled, cancelled.Snapshot!.State);
        Assert.All(
            cancelled.Snapshot.Questions!.Items,
            item =>
            {
                Assert.Equal(QuestionOutcome.Cancelled, item.Outcome);
                Assert.False(item.Question.IsAnswered);
            }
        );
    }

    [Fact]
    public void VariantReview_IsIdempotentAndProducesEffect()
    {
        var hub = new InteractionHub();
        var details = Explore();

        var first = hub.CreateOrGetVariantReview(Context(), details);
        var second = hub.CreateOrGetVariantReview(Context(), details);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(hub.ListOpen("s1"));

        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(first.Id, "desktop", out var claimed));
        var submitted = hub.SubmitVariantDecision(
            first.Id,
            new VariantReviewDecision
            {
                Action = VariantReviewAction.Apply,
                VariantId = "v1",
                VariantLabel = "方案A",
            },
            claimed!.Claim!.LeaseId
        );

        Assert.Equal(SubmitStatus.Accepted, submitted.Status);
        var effect = submitted.Effect!;
        Assert.Equal(VariantEffectKind.SendExtractionPrompt, effect.Kind);
        Assert.Equal("正文A", effect.VariantContent);
        Assert.True(submitted.Snapshot!.IsTerminal);
        Assert.Equal(VariantEffectState.Pending, submitted.Snapshot.VariantReview!.EffectState);

        Assert.Equal(
            SubmitStatus.Accepted,
            hub.SetVariantEffectState(first.Id, effect.EffectId, VariantEffectState.Applied)
        );
        Assert.Equal(VariantEffectState.Applied, hub.Get(first.Id)!.VariantReview!.EffectState);
    }

    [Fact]
    public async Task CancelingOneWaiter_DoesNotPoisonSharedQuestionSignal()
    {
        var hub = new InteractionHub();
        var created = hub.CreateQuestionSet(Context(), [Choice()]);
        var questionId = created.Questions!.Items[0].Question.Id;

        using var firstCts = new CancellationTokenSource();
        var first = hub.WaitForQuestionAnswerAsync(created.Id, questionId, firstCts.Token);
        await firstCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var later = hub.WaitForQuestionAnswerAsync(created.Id, questionId);
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(created.Id, "desktop", out var claimed));
        var submitted = hub.SubmitQuestionAnswer(
            created.Id,
            questionId,
            ["A"],
            claimed!.Claim!.LeaseId
        );

        Assert.Equal(SubmitStatus.Accepted, submitted.Status);
        Assert.Equal(["A"], await later.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task VariantEffectExecutor_MarksRunningThenApplied()
    {
        var hub = new InteractionHub();
        var created = hub.CreateOrGetVariantReview(Context(), Explore());
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(created.Id, "desktop", out var claimed));
        var submitted = hub.SubmitVariantDecision(
            created.Id,
            new VariantReviewDecision { Action = VariantReviewAction.Apply, VariantId = "v1" },
            claimed!.Claim!.LeaseId
        );
        var effect = submitted.Effect!;
        using var executor = new VariantEffectExecutor(hub);
        var observed = VariantEffectState.None;

        await executor.ExecuteAsync(
            created.Id,
            effect,
            _ =>
            {
                observed = hub.Get(created.Id)!.VariantReview!.EffectState;
                return Task.CompletedTask;
            }
        );

        Assert.Equal(VariantEffectState.Running, observed);
        Assert.Equal(VariantEffectState.Applied, hub.Get(created.Id)!.VariantReview!.EffectState);
    }

    [Fact]
    public async Task VariantEffectExecutor_MarksFailedAndRethrows()
    {
        var hub = new InteractionHub();
        var created = hub.CreateOrGetVariantReview(Context(), Explore());
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(created.Id, "desktop", out var claimed));
        var submitted = hub.SubmitVariantDecision(
            created.Id,
            new VariantReviewDecision { Action = VariantReviewAction.Apply, VariantId = "v1" },
            claimed!.Claim!.LeaseId
        );
        var effect = submitted.Effect!;
        using var executor = new VariantEffectExecutor(hub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                created.Id,
                effect,
                _ => throw new InvalidOperationException("boom")
            )
        );

        Assert.Equal("boom", ex.Message);
        var review = hub.Get(created.Id)!.VariantReview!;
        Assert.Equal(VariantEffectState.Failed, review.EffectState);
        Assert.Equal("boom", review.Effect!.FailureReason);
    }

    [Fact]
    public void CancelSession_CancelsAllOpenInteractions()
    {
        var hub = new InteractionHub();
        var question = hub.CreateQuestionSet(Context(), [Choice()]);
        var variant = hub.CreateOrGetVariantReview(Context(), Explore());

        hub.CancelSession("s1", "session-switch");

        Assert.Equal(InteractionState.Cancelled, hub.Get(question.Id)!.State);
        Assert.Equal(InteractionState.Cancelled, hub.Get(variant.Id)!.State);
        Assert.Empty(hub.ListOpen("s1"));
    }
}
