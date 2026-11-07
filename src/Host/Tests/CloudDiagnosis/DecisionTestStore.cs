using System.Collections.Concurrent;
using Core.Interactions;
using Core.Shared;

namespace Host.Tests.CloudDiagnosis;

/// <summary>
/// 测试侧的旧 UserQuestionStore 外观：内部走 InteractionHub，
/// 让裁决测试继续断言「待答/已答」，同时明确传入生产同款 hub。
/// </summary>
internal sealed class DecisionTestStore
{
    private readonly ConcurrentDictionary<string, string> _interactionByQuestion = new(
        StringComparer.Ordinal
    );

    public InteractionHub Hub { get; } = new();

    public event Action<IReadOnlyList<PendingQuestion>>? PendingQuestionsCreated;

    public DecisionTestStore()
    {
        Hub.CurrentContext = new InteractionContext("", "decision-test");
        Hub.Changed += OnChanged;
    }

    public static implicit operator InteractionHub(DecisionTestStore store) => store.Hub;

    public IReadOnlyList<PendingQuestion> GetPending() =>
        Hub.GetPendingQuestions(Hub.CurrentContext.SessionId);

    public PendingQuestion? Get(string questionId) =>
        _interactionByQuestion.TryGetValue(questionId, out var interactionId)
            ? Hub.Get(interactionId)
                ?.Questions?.Items.FirstOrDefault(i => i.Question.Id == questionId)
                ?.Question
            : null;

    public bool Answer(string questionId, IReadOnlyList<string> answers)
    {
        if (
            !_interactionByQuestion.TryGetValue(questionId, out var interactionId)
            || Hub.Get(interactionId) is not { } snapshot
            || snapshot.IsTerminal
        )
            return false;

        var claim = Hub.TryClaim(interactionId, "test", out var claimed);
        if (claim is not (SubmitStatus.Accepted or SubmitStatus.AlreadyClaimed))
            return false;
        var lease = claimed?.Claim?.LeaseId;
        if (string.IsNullOrEmpty(lease))
            return false;

        return Hub.SubmitQuestionAnswer(interactionId, questionId, answers, lease).Status
            == SubmitStatus.Accepted;
    }

    private void OnChanged(InteractionSnapshot snapshot)
    {
        if (snapshot.Kind != InteractionKind.QuestionSet || snapshot.Questions is null)
            return;

        foreach (var item in snapshot.Questions.Items)
            _interactionByQuestion[item.Question.Id] = snapshot.Id;

        if (
            !snapshot.IsTerminal
            && snapshot.Questions.Items.Any(i => i.Outcome == QuestionOutcome.Unanswered)
        )
            PendingQuestionsCreated?.Invoke(
                snapshot.Questions.Items.Select(i => i.Question).ToList()
            );
    }
}
