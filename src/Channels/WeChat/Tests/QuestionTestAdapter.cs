using System.Collections.Concurrent;
using Core.Interactions;
using Core.Shared;

namespace Channels.WeChat.Tests;

/// <summary>
/// 测试侧的旧问题存储外观：把 CreateQuestions/WaitForAnswerAsync 映射到 InteractionHub，
/// 让微信通道测试继续按「建题 → 等答案」的语义编写，同时走生产同一条 Core 路径。
/// </summary>
internal sealed class QuestionTestAdapter
{
    private readonly ConcurrentDictionary<string, string> _interactionByQuestion = new(
        StringComparer.Ordinal
    );

    public InteractionHub Hub { get; } = new();

    public QuestionTestAdapter()
    {
        Hub.CurrentContext = new InteractionContext("", "wechat-test");
    }

    public List<PendingQuestion> CreateQuestions(IReadOnlyList<QuestionDef> definitions)
    {
        var snapshot = Hub.CreateQuestionSet(Hub.CurrentContext, definitions);
        foreach (var item in snapshot.Questions!.Items)
            _interactionByQuestion[item.Question.Id] = snapshot.Id;
        return snapshot.Questions.Items.Select(i => i.Question).ToList();
    }

    public Task<string[]> WaitForAnswerAsync(string questionId, CancellationToken ct) =>
        Hub.WaitForQuestionAnswerAsync(_interactionByQuestion[questionId], questionId, ct);

    public PendingQuestion? Get(string questionId) =>
        _interactionByQuestion.TryGetValue(questionId, out var interactionId)
            ? Hub.Get(interactionId)
                ?.Questions?.Items.FirstOrDefault(i => i.Question.Id == questionId)
                ?.Question
            : null;

    public IReadOnlyList<PendingQuestion> GetPending() =>
        Hub.GetPendingQuestions(Hub.CurrentContext.SessionId);
}
