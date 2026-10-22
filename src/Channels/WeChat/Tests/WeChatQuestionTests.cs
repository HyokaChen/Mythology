// 功能：裁决回路测试 —— ask_user 在手机上以编号选项呈现，回复即答案
//
// 这些用例同时钉死一个顺序约束：答案必须优先于"忙守卫"。
// Agent 阻塞在 WaitForAnswerAsync 里等拍板时 _turnActive 仍为 true，
// 若先判忙，答案永远送不进去，回合就死了。

using Core.Shared;
using Xunit;

namespace Channels.WeChat.Tests;

public class WeChatQuestionTests
{
    private static QuestionDef Choice =>
        new()
        {
            Header = "剧情走向",
            Question = "主角要放过仇人吗？",
            Options = ["放过他", "当场复仇"],
            MultiSelect = false,
        };

    /// <summary>等出站里出现包含指定文本的消息。</summary>
    private static async Task WaitForSentAsync(WeChatFixture f, string needle, int count = 1)
    {
        // 必须先解码：请求体里的中文被默认编码器转义成 \uXXXX，原文搜不到
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (
            DateTimeOffset.UtcNow < deadline
            && f.Handler.BodiesTo("sendmessage")
                .Select(WeChatFixtures.Decoded)
                .Count(b => b.Contains(needle, StringComparison.Ordinal)) < count
        )
            await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QuestionIsRenderedToPhoneWithNumberedOptions()
    {
        var questions = new UserQuestionStore();
        var f = WeChatFixtures.Build(
            (_, _, ct) =>
            {
                var created = questions.CreateQuestions([Choice]);
                // 复刻 ask_user 工具：阻塞在回合内等答案
                return questions.WaitForAnswerAsync(created[0].Id, ct);
            },
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("续写第三章"), times: 1);

        await WeChatFixtures.RunUntilAsync(f, () => f.Handler.RequestsTo("sendmessage").Count > 0);

        var body = WeChatFixtures.Decoded(Assert.Single(f.Handler.BodiesTo("sendmessage")));
        Assert.Contains("需要你拍板", body, StringComparison.Ordinal);
        Assert.Contains("剧情走向", body, StringComparison.Ordinal);
        Assert.Contains("主角要放过仇人吗", body, StringComparison.Ordinal);
        Assert.Contains("1) 放过他", body, StringComparison.Ordinal);
        Assert.Contains("2) 当场复仇", body, StringComparison.Ordinal);
        Assert.Contains("回复编号", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NumberReplyBecomesTheAnswer()
    {
        var questions = new UserQuestionStore();
        string? questionId = null;
        var f = WeChatFixtures.Build(
            (_, _, ct) =>
            {
                var created = questions.CreateQuestions([Choice]);
                questionId = created[0].Id;
                return questions.WaitForAnswerAsync(created[0].Id, ct);
            },
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("续写第三章"), times: 1);
        f.Handler.RouteWhen(
            "getupdates",
            WeChatFixtures.SentText(f, "需要你拍板"),
            WeChatFixtures.Inbound("2", seq: "9"),
            times: 1
        );

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);

        // 等手机上收到问题，再等它被回答（回合返回）
        await WaitForSentAsync(f, "需要你拍板");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && questions.Get(questionId!)?.IsAnswered != true)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.NotNull(questionId);
        var answered = questions.Get(questionId!)!;
        Assert.True(answered.IsAnswered);
        Assert.Equal(["当场复仇"], answered.SelectedAnswers ?? []);
    }

    [Fact]
    public async Task AnswerBeatsTheBusyGuard()
    {
        // 关键顺序约束：等答案时 _turnActive 仍为 true，先判忙就永远答不了
        var questions = new UserQuestionStore();
        var busy = false;
        string? questionId = null;
        var f = WeChatFixtures.Build(
            (_, _, ct) =>
            {
                var created = questions.CreateQuestions([Choice]);
                questionId = created[0].Id;
                busy = true; // 模拟"另一个来源也忙" + 本回合未结束
                return questions.WaitForAnswerAsync(created[0].Id, ct);
            },
            isAgentBusy: () => busy,
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("续写第三章"), times: 1);
        f.Handler.RouteWhen(
            "getupdates",
            WeChatFixtures.SentText(f, "需要你拍板"),
            WeChatFixtures.Inbound("1", seq: "9"),
            times: 1
        );

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);

        await WaitForSentAsync(f, "需要你拍板");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && questions.Get(questionId!)?.IsAnswered != true)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.True(questions.Get(questionId!)!.IsAnswered);
        Assert.Equal(["放过他"], questions.Get(questionId!)!.SelectedAnswers);
    }

    [Fact]
    public async Task MultiSelectAcceptsCommaSeparatedNumbers()
    {
        var questions = new UserQuestionStore();
        string? questionId = null;
        var f = WeChatFixtures.Build(
            (_, _, ct) =>
            {
                var created = questions.CreateQuestions([
                    new QuestionDef
                    {
                        Header = "多选",
                        Question = "本章要埋哪些线？",
                        Options = ["暗线A", "暗线B", "暗线C"],
                        MultiSelect = true,
                    },
                ]);
                questionId = created[0].Id;
                return questions.WaitForAnswerAsync(created[0].Id, ct);
            },
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("写"), times: 1);
        f.Handler.RouteWhen(
            "getupdates",
            WeChatFixtures.SentText(f, "多选"),
            WeChatFixtures.Inbound("1,3", seq: "9"),
            times: 1
        );

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);

        await WaitForSentAsync(f, "多选");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && questions.Get(questionId!)?.IsAnswered != true)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.Equal(["暗线A", "暗线C"], questions.Get(questionId!)!.SelectedAnswers ?? []);
    }

    [Fact]
    public async Task SingleSelectWithMultipleNumbersIsRejectedAndQuestionResent()
    {
        // 静默取第一个会猜错意图；宁可重发题目让用户重选
        var questions = new UserQuestionStore();
        string? questionId = null;
        var f = WeChatFixtures.Build(
            (_, _, ct) =>
            {
                var created = questions.CreateQuestions([Choice]);
                questionId = created[0].Id;
                return questions.WaitForAnswerAsync(created[0].Id, ct);
            },
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("续写"), times: 1);
        f.Handler.RouteWhen(
            "getupdates",
            WeChatFixtures.SentText(f, "需要你拍板"),
            WeChatFixtures.Inbound("1,2", seq: "9"),
            times: 1
        );

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);

        await WaitForSentAsync(f, "需要你拍板", count: 2);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.False(questions.Get(questionId!)!.IsAnswered);
        Assert.Equal(
            2,
            f.Handler.BodiesTo("sendmessage")
                .Select(WeChatFixtures.Decoded)
                .Count(b => b.Contains("需要你拍板", StringComparison.Ordinal))
        );
    }

    [Fact]
    public async Task OutOfRangeNumberIsRejectedAndQuestionResent()
    {
        var questions = new UserQuestionStore();
        string? questionId = null;
        var f = WeChatFixtures.Build(
            (_, _, ct) =>
            {
                var created = questions.CreateQuestions([Choice]);
                questionId = created[0].Id;
                return questions.WaitForAnswerAsync(created[0].Id, ct);
            },
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("续写"), times: 1);
        f.Handler.RouteWhen(
            "getupdates",
            WeChatFixtures.SentText(f, "需要你拍板"),
            WeChatFixtures.Inbound("9", seq: "8"),
            times: 1
        );

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);

        await WaitForSentAsync(f, "需要你拍板", count: 2);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.False(questions.Get(questionId!)!.IsAnswered);
    }

    [Fact]
    public async Task QuestionWithoutOptionsAcceptsFreeText()
    {
        var questions = new UserQuestionStore();
        string? questionId = null;
        var f = WeChatFixtures.Build(
            (_, _, ct) =>
            {
                var created = questions.CreateQuestions([
                    new QuestionDef
                    {
                        Header = "补充设定",
                        Question = "这一章的暗线你想叫什么名字？",
                    },
                ]);
                questionId = created[0].Id;
                return questions.WaitForAnswerAsync(created[0].Id, ct);
            },
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("写"), times: 1);
        f.Handler.RouteWhen(
            "getupdates",
            WeChatFixtures.SentText(f, "补充设定"),
            WeChatFixtures.Inbound("就叫「旧钟」吧", seq: "9"),
            times: 1
        );

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);

        await WaitForSentAsync(f, "补充设定");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && questions.Get(questionId!)?.IsAnswered != true)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.Equal(["就叫「旧钟」吧"], questions.Get(questionId!)!.SelectedAnswers ?? []);
    }

    [Fact]
    public async Task SecondQuestionIsSentAfterFirstIsAnswered()
    {
        var questions = new UserQuestionStore();
        var f = WeChatFixtures.Build(
            (_, _, ct) =>
            {
                var created = questions.CreateQuestions([
                    Choice,
                    Choice with
                    {
                        Header = "第二个",
                    },
                ]);
                return questions.WaitForAnswerAsync(created[1].Id, ct);
            },
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("续写"), times: 1);
        f.Handler.RouteWhen(
            "getupdates",
            WeChatFixtures.SentText(f, "剧情走向"),
            WeChatFixtures.Inbound("1", seq: "9"),
            times: 1
        );

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);

        await WaitForSentAsync(f, "剧情走向");
        await WaitForSentAsync(f, "第二个");

        // 等答案真的落地：不能只等固定时长（下一轮轮询有 500ms 最小间隔）
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && questions.GetPending().Count != 1)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        var pending = questions.GetPending();
        Assert.Single(pending); // 第一个已答，第二个仍待答
        Assert.Equal("第二个", pending[0].Header);
    }

    [Fact]
    public async Task QuestionCreatedOutsideOurTurnIsNotSentToPhone()
    {
        // 终端里的对话：宿主自己的弹窗处理，通道不接管
        var questions = new UserQuestionStore();
        var f = WeChatFixtures.Build((_, _, _) => Task.CompletedTask, questions: questions);
        WeChatFixtures.RouteCommon(f.Handler);

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);
        await Task.Delay(150, TestContext.Current.CancellationToken);

        questions.CreateQuestions([Choice]); // 无回合进行中
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.Empty(f.Handler.RequestsTo("sendmessage"));
    }

    [Fact]
    public async Task AnsweredQuestionIsNotConsumedAgainByTheNextMessage()
    {
        // 答完之后再发消息应回到普通 prompt 路径（否则手机上永远只能答题）
        var questions = new UserQuestionStore();
        var turnTexts = new List<string>();
        string? questionId = null;
        var f = WeChatFixtures.Build(
            (_, text, ct) =>
            {
                turnTexts.Add(text);
                if (questionId is null)
                {
                    var created = questions.CreateQuestions([Choice]);
                    questionId = created[0].Id;
                    return questions.WaitForAnswerAsync(created[0].Id, ct);
                }
                return Task.CompletedTask;
            },
            questions: questions
        );
        WeChatFixtures.RouteCommon(f.Handler);
        f.Handler.RouteTo("getupdates", WeChatFixtures.Inbound("续写"), times: 1);
        f.Handler.RouteWhen(
            "getupdates",
            WeChatFixtures.SentText(f, "需要你拍板"),
            WeChatFixtures.Inbound("2", seq: "9"),
            times: 1
        );
        // 第三条要等问题真的被回答之后再送达，否则会被当成第二次 prompt
        f.Handler.RouteWhen(
            "getupdates",
            () => questions.Get(questionId!)?.IsAnswered == true,
            WeChatFixtures.Inbound("再改一版", seq: "8"),
            times: 1
        );

        using var cts = new CancellationTokenSource();
        var loop = f.Bridge.RunAsync(cts.Token);

        await WaitForSentAsync(f, "需要你拍板");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && turnTexts.Count < 2)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop;
        await f.Bridge.DisposeAsync();

        Assert.Equal(["续写", "再改一版"], turnTexts);
        Assert.True(questions.Get(questionId!)!.IsAnswered);
    }
}
