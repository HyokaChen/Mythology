using System.Collections.Concurrent;

namespace Core.Shared;

/// <summary>
/// 会话级用户问题存储（内存），供 ask_user 工具和 TUI 读取。
/// 工具产出问题列表 → TUI 渲染交互面板 → 用户回复 → Agent 读取回复。
/// </summary>
public sealed class UserQuestionStore
{
    private readonly ConcurrentDictionary<string, PendingQuestion> _questions = new(
        StringComparer.Ordinal
    );
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string[]>> _answerTcs = new(
        StringComparer.Ordinal
    );
    private int _nextId = 1;

    /// <summary>
    /// 新问题创建通知（HARNESS-CLOUD §5.4 触发桥）：
    /// ask_user 工具由工具渲染路径触发 UI；Hook 侧组件创建的问题经此事件通知宿主弹窗。
    /// 回调在问题创建线程上同步执行，宿主应自行调度到 UI 流程。
    /// </summary>
    public event Action<IReadOnlyList<PendingQuestion>>? PendingQuestionsCreated;

    /// <summary>创建一组待回复的问题。</summary>
    /// <param name="questions">问题定义列表。</param>
    /// <returns>创建的 PendingQuestion 列表。</returns>
    public List<PendingQuestion> CreateQuestions(List<QuestionDef> questions)
    {
        var result = new List<PendingQuestion>(questions.Count);
        foreach (var q in questions)
        {
            var id = Interlocked.Increment(ref _nextId).ToString();
            var pq = new PendingQuestion
            {
                Id = id,
                Header = q.Header,
                Question = q.Question,
                Options = q.Options,
                MultiSelect = q.MultiSelect,
                CreatedAt = DateTime.UtcNow,
            };
            _questions[id] = pq;
            _answerTcs[id] = new TaskCompletionSource<string[]>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            result.Add(pq);
        }
        PendingQuestionsCreated?.Invoke(result);
        return result;
    }

    /// <summary>用户回复某个问题。完成 TCS 以便工具解除阻塞。</summary>
    /// <param name="questionId">问题 ID。</param>
    /// <param name="selectedOptions">用户选择的答案。</param>
    /// <returns>是否成功处理回复。</returns>
    public bool Answer(string questionId, string[] selectedOptions)
    {
        if (!_questions.TryGetValue(questionId, out var q))
            return false;
        _questions[questionId] = q with
        {
            SelectedAnswers = selectedOptions,
            AnsweredAt = DateTime.UtcNow,
        };
        // Signal the waiting tool
        if (_answerTcs.TryGetValue(questionId, out var tcs))
            tcs.TrySetResult(selectedOptions);
        return true;
    }

    /// <summary>等待用户回复特定问题。</summary>
    /// <param name="questionId">问题 ID。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>用户选择的答案数组。</returns>
    public Task<string[]> WaitForAnswerAsync(string questionId, CancellationToken ct = default)
    {
        if (!_answerTcs.TryGetValue(questionId, out var tcs))
            return Task.FromResult(Array.Empty<string>());
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    /// <summary>获取所有待回复（未回答）的问题。</summary>
    /// <returns>待回复问题列表，按创建时间排序。</returns>
    public IReadOnlyList<PendingQuestion> GetPending() =>
        _questions.Values.Where(q => q.AnsweredAt is null).OrderBy(q => q.CreatedAt).ToList();

    /// <summary>获取所有问题（包括已回复的）。</summary>
    /// <returns>所有问题列表，按创建时间排序。</returns>
    public IReadOnlyList<PendingQuestion> GetAll() =>
        _questions.Values.OrderBy(q => q.CreatedAt).ToList();

    /// <summary>根据 ID 获取问题。</summary>
    /// <param name="id">问题 ID。</param>
    /// <returns>找到的 PendingQuestion，如果不存在则返回 null。</returns>
    public PendingQuestion? Get(string id) => _questions.GetValueOrDefault(id);
}

/// <summary>问题定义（工具参数侧）。</summary>
public sealed record QuestionDef
{
    public string Header { get; init; } = "";
    public string Question { get; init; } = "";
    public string[] Options { get; init; } = [];
    public bool MultiSelect { get; init; }
}

/// <summary>待回复的问题（含状态）。</summary>
public sealed record PendingQuestion
{
    public string Id { get; init; } = "";
    public string Header { get; init; } = "";
    public string Question { get; init; } = "";
    public string[] Options { get; init; } = [];
    public bool MultiSelect { get; init; }
    public DateTime CreatedAt { get; init; }
    public string[]? SelectedAnswers { get; init; }
    public DateTime? AnsweredAt { get; init; }

    public bool IsAnswered => AnsweredAt is not null;
}
