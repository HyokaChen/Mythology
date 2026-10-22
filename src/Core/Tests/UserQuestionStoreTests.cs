using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// UserQuestionStore 用户问题存储测试。
/// 验证问题创建、回答和查询功能。
/// </summary>
public class UserQuestionStoreTests
{
    [Fact]
    /// <summary>创建问题应返回待处理状态的问题。</summary>
    public void CreateQuestions_ReturnsPendingQuestions()
    {
        var store = new UserQuestionStore();
        var questions = store.CreateQuestions([
            new QuestionDef
            {
                Header = "风格",
                Question = "选择写作风格？",
                Options = ["写实", "浪漫"],
            },
        ]);

        Assert.Single(questions);
        Assert.Equal("风格", questions[0].Header);
        Assert.Equal("选择写作风格？", questions[0].Question);
        Assert.Equal(2, questions[0].Options.Length);
        Assert.False(questions[0].IsAnswered);
    }

    [Fact]
    /// <summary>创建问题应递增 ID。</summary>
    public void CreateQuestions_IncrementsIds()
    {
        var store = new UserQuestionStore();
        var q1 = store.CreateQuestions([
            new QuestionDef
            {
                Header = "A",
                Question = "Q1",
                Options = ["X", "Y"],
            },
        ]);
        var q2 = store.CreateQuestions([
            new QuestionDef
            {
                Header = "B",
                Question = "Q2",
                Options = ["M", "N"],
            },
        ]);

        // IDs should be different and increasing
        var id1 = int.Parse(q1[0].Id);
        var id2 = int.Parse(q2[0].Id);
        Assert.True(id2 > id1);
    }

    [Fact]
    /// <summary>回答应更新问题并完成 TaskCompletionSource。</summary>
    public void Answer_UpdatesQuestionAndCompletesTcs()
    {
        var store = new UserQuestionStore();
        var questions = store.CreateQuestions([
            new QuestionDef
            {
                Header = "风格",
                Question = "选择风格？",
                Options = ["写实", "浪漫"],
            },
        ]);
        var qId = questions[0].Id;

        var result = store.Answer(qId, ["写实"]);
        Assert.True(result);

        var answered = store.Get(qId);
        Assert.NotNull(answered);
        Assert.True(answered.IsAnswered);
        Assert.Equal("写实", answered.SelectedAnswers![0]);
    }

    [Fact]
    /// <summary>回答不存在的 ID 应返回 false。</summary>
    public void Answer_ReturnsFalseForMissingId()
    {
        var store = new UserQuestionStore();
        Assert.False(store.Answer("999", ["x"]));
    }

    [Fact]
    /// <summary>等待回答应在回答后完成。</summary>
    public async Task WaitForAnswerAsync_ResolvesAfterAnswer()
    {
        var store = new UserQuestionStore();
        var questions = store.CreateQuestions([
            new QuestionDef
            {
                Header = "风格",
                Question = "选择风格？",
                Options = ["写实", "浪漫"],
            },
        ]);
        var qId = questions[0].Id;

        // Answer in background
        _ = Task.Run(
            async () =>
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                store.Answer(qId, ["浪漫"]);
            },
            TestContext.Current.CancellationToken
        );

        var answers = await store.WaitForAnswerAsync(qId, TestContext.Current.CancellationToken);
        Assert.Equal("浪漫", answers[0]);
    }

    [Fact]
    /// <summary>取消令牌应能取消等待。</summary>
    public async Task WaitForAnswerAsync_CancelsWithToken()
    {
        var store = new UserQuestionStore();
        var questions = store.CreateQuestions([
            new QuestionDef
            {
                Header = "风格",
                Question = "选择风格？",
                Options = ["写实", "浪漫"],
            },
        ]);
        var qId = questions[0].Id;

        using var cts = new CancellationTokenSource(50);
        // The TCS cancellation may throw TaskCanceledException (subclass of OperationCanceledException)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WaitForAnswerAsync(qId, cts.Token)
        );
    }

    [Fact]
    /// <summary>GetPending 应只返回未回答的问题。</summary>
    public void GetPending_ReturnsOnlyUnanswered()
    {
        var store = new UserQuestionStore();
        var q1 = store.CreateQuestions([
            new QuestionDef
            {
                Header = "A",
                Question = "Q1",
                Options = ["X", "Y"],
            },
        ]);
        store.CreateQuestions([
            new QuestionDef
            {
                Header = "B",
                Question = "Q2",
                Options = ["M", "N"],
            },
        ]);

        store.Answer(q1[0].Id, ["X"]);

        var pending = store.GetPending();
        Assert.Single(pending);
        Assert.Equal("B", pending[0].Header);
    }

    [Fact]
    /// <summary>GetAll 应返回所有问题包括已回答的。</summary>
    public void GetAll_ReturnsAllIncludingAnswered()
    {
        var store = new UserQuestionStore();
        var q1 = store.CreateQuestions([
            new QuestionDef
            {
                Header = "A",
                Question = "Q1",
                Options = ["X", "Y"],
            },
        ]);
        store.CreateQuestions([
            new QuestionDef
            {
                Header = "B",
                Question = "Q2",
                Options = ["M", "N"],
            },
        ]);

        store.Answer(q1[0].Id, ["X"]);

        var all = store.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    /// <summary>按 ID 获取应返回对应问题。</summary>
    public void Get_ReturnsQuestionById()
    {
        var store = new UserQuestionStore();
        var questions = store.CreateQuestions([
            new QuestionDef
            {
                Header = "风格",
                Question = "选择风格？",
                Options = ["写实", "浪漫"],
            },
        ]);

        var found = store.Get(questions[0].Id);
        Assert.NotNull(found);
        Assert.Equal("风格", found.Header);
    }

    [Fact]
    /// <summary>获取不存在的 ID 应返回 null。</summary>
    public void Get_ReturnsNullForMissingId()
    {
        var store = new UserQuestionStore();
        Assert.Null(store.Get("999"));
    }
}
