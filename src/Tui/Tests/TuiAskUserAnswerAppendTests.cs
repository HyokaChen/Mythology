using System.Reflection;
using Core.Shared;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// TUI 追加的 ask_user 答案必须与工具结果同格式，且从 store 重新读取
/// （Show 期间的 Answer 会替换记录，调用方保留的 pending 快照没有答案）。
/// </summary>
public sealed class TuiAskUserAnswerAppendTests
{
    [Fact]
    public async Task AppendAskUserAnswers_UsesOmpFormatAndStoreAnswers()
    {
        var store = new UserQuestionStore();
        await using var harness = new TuiSlashTestHarness(questionStore: store);

        var pending = store.CreateQuestions([
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
        ]);
        Assert.True(store.Answer(pending[0].Id, ["身世", "旧案"]));
        Assert.True(store.Answer(pending[1].Id, ["第三人称"]));

        var method = typeof(TuiApp).GetMethod(
            "AppendAskUserAnswersToConversation",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var task = (Task)method.Invoke(harness.App, [pending])!;
        await task;

        var message = Assert.Single(harness.Agent.Messages);
        Assert.Equal("User answers:\n方向: [身世, 旧案]\n视角: 第三人称", message.Content);
    }
}
