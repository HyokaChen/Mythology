using Core.Interactions;
using Core.Shared;
using Plugins.FileSystem.Tools;
using Xunit;

namespace FileSystem.Tests;

public class AskUserToolTests
{
    [Fact]
    public async Task ExecuteAsync_NoQuestions_ReturnsError()
    {
        var hub = new InteractionHub();
        var tool = new AskUserTool(hub);

        var result = await tool.ExecuteAsync(
            "call_1",
            new AskUserParams(),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_TooManyQuestions_ReturnsError()
    {
        var hub = new InteractionHub();
        var tool = new AskUserTool(hub);

        var result = await tool.ExecuteAsync(
            "call_1",
            new AskUserParams
            {
                Questions = Enumerable
                    .Range(0, 5)
                    .Select(i => new AskQuestion
                    {
                        Header = $"Q{i}",
                        Question = $"问题{i}",
                        Options = ["A", "B"],
                    })
                    .ToArray(),
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_QuestionWithTooFewOptions_ReturnsError()
    {
        var hub = new InteractionHub();
        var tool = new AskUserTool(hub);

        var result = await tool.ExecuteAsync(
            "call_1",
            new AskUserParams
            {
                Questions =
                [
                    new AskQuestion
                    {
                        Header = "Q",
                        Question = "问题",
                        Options = ["only"],
                    },
                ],
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_QuestionWithTooManyOptions_ReturnsError()
    {
        var hub = new InteractionHub();
        var tool = new AskUserTool(hub);

        var result = await tool.ExecuteAsync(
            "call_1",
            new AskUserParams
            {
                Questions =
                [
                    new AskQuestion
                    {
                        Header = "Q",
                        Question = "问题",
                        Options = ["A", "B", "C", "D", "E"],
                    },
                ],
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_MultiSelectAnswers_JoinedByEnglishComma()
    {
        var hub = new InteractionHub();
        var tool = new AskUserTool(hub);
        var pendingCreated = new TaskCompletionSource<InteractionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        hub.Changed += snapshot =>
        {
            if (
                snapshot.Kind == InteractionKind.QuestionSet
                && snapshot.Questions?.Items.Any(i => i.Outcome == QuestionOutcome.Unanswered)
                    == true
            )
                pendingCreated.TrySetResult(snapshot);
        };

        var executeTask = tool.ExecuteAsync(
            "call_1",
            new AskUserParams
            {
                Questions =
                [
                    new AskQuestion
                    {
                        Header = "方向",
                        Question = "选择要保留的线索",
                        Options = ["身世", "师门", "旧案"],
                        MultiSelect = true,
                    },
                ],
            },
            TestContext.Current.CancellationToken
        );

        var pending = await pendingCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(pending.Id, "test", out var claimed));
        var questionId = pending.Questions!.Items[0].Question.Id;
        var submitted = hub.SubmitQuestionAnswer(
            pending.Id,
            questionId,
            ["身世", "旧案"],
            claimed!.Claim!.LeaseId
        );
        Assert.Equal(SubmitStatus.Accepted, submitted.Status);

        var result = await executeTask;
        var text = GetText(result);
        Assert.False(result.IsError);
        Assert.Equal("User answers:\n方向: [身世, 旧案]", text);
    }

    [Fact]
    public async Task ExecuteAsync_ValidQuestion_CreatesPendingQuestion()
    {
        var hub = new InteractionHub();
        var tool = new AskUserTool(hub);

        // Pass the cancellation token so the tool can be cancelled
        using var cts = new CancellationTokenSource(200);
        var token = cts.Token;

        var result = await tool.ExecuteAsync(
            "call_1",
            new AskUserParams
            {
                Questions =
                [
                    new AskQuestion
                    {
                        Header = "Test",
                        Question = "测试?",
                        Options = ["A", "B"],
                    },
                ],
            },
            token
        );

        Assert.True(result.IsError);
        Assert.Contains("取消", GetText(result));
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
