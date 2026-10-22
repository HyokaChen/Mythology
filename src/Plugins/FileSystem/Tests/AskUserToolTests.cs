using Core.Shared;
using Plugins.FileSystem.Tools;
using Xunit;

namespace FileSystem.Tests;

public class AskUserToolTests
{
    [Fact]
    public async Task ExecuteAsync_NoQuestions_ReturnsError()
    {
        var store = new UserQuestionStore();
        var tool = new AskUserTool(store);

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
        var store = new UserQuestionStore();
        var tool = new AskUserTool(store);

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
        var store = new UserQuestionStore();
        var tool = new AskUserTool(store);

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
        var store = new UserQuestionStore();
        var tool = new AskUserTool(store);

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
    public async Task ExecuteAsync_ValidQuestion_CreatesPendingQuestion()
    {
        var store = new UserQuestionStore();
        var tool = new AskUserTool(store);

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
