using System.Reflection;
using Core.Interactions;
using Core.Shared;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// TUI 追加的 ask_user 答案必须与工具结果同格式，并使用 Core 已落地的终态快照
/// （终态快照中的 PendingQuestion 已带 SelectedAnswers，调用方不再回读旧 store）。
/// </summary>
public sealed class TuiAskUserAnswerAppendTests
{
    [Fact]
    public async Task AppendAskUserAnswers_UsesOmpFormatAndStoreAnswers()
    {
        var store = new UserQuestionStore();
        await using var harness = new TuiSlashTestHarness(questionStore: store);

        var hub = new InteractionHub();
        hub.CurrentContext = new InteractionContext("", "tui-answer-test");
        var created = hub.CreateQuestionSet(
            hub.CurrentContext,
            [
                new QuestionDef
                {
                    Header = "方向",
                    Question = "选择要保留的线索",
                    Options = ["身世", "师门", "旧案"],
                    MultiSelect = true,
                },
                new QuestionDef
                {
                    Header = "视角",
                    Question = "用第几人称？",
                    Options = ["第一人称", "第三人称"],
                },
            ]
        );
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(created.Id, "tui", out var claimed));
        var lease = claimed!.Claim!.LeaseId;
        var firstId = created.Questions!.Items[0].Question.Id;
        var secondId = created.Questions.Items[1].Question.Id;

        Assert.Equal(
            SubmitStatus.Accepted,
            hub.SubmitQuestionAnswer(created.Id, firstId, ["身世", "旧案"], lease).Status
        );
        var resolved = hub.SubmitQuestionAnswer(
            created.Id,
            secondId,
            ["第三人称"],
            lease
        ).Snapshot!;
        var questions = resolved.Questions!.Items.Select(i => i.Question).ToList();

        var method = typeof(TuiApp).GetMethod(
            "AppendAskUserAnswersToConversation",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var task = (Task)method.Invoke(harness.App, [questions])!;
        await task;

        var message = Assert.Single(harness.Agent.Messages);
        Assert.Equal("User answers:\n方向: [身世, 旧案]\n视角: 第三人称", message.Content);
    }
}
