using Core.Shared;
using Core.Tools;
using Plugins.FileSystem.Tools;
using Xunit;

namespace FileSystem.Tests;

public class GetPendingQuestionsToolTests
{
    [Fact]
    public async Task ExecuteAsync_NoPending_ReturnsEmptyMessage()
    {
        var store = new UserQuestionStore();
        var tool = new GetPendingQuestionsTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new GetPendingQuestionsParams(),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("没有待回复", text);
    }

    [Fact]
    public async Task ExecuteAsync_HasPending_ReturnsQuestions()
    {
        var store = new UserQuestionStore();
        store.CreateQuestions([
            new QuestionDef
            {
                Header = "Test",
                Question = "测试?",
                Options = ["A", "B"],
            },
        ]);
        var tool = new GetPendingQuestionsTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new GetPendingQuestionsParams(),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("测试?", text);
        Assert.Contains("A", text);
        Assert.Contains("B", text);
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePending_ReturnsAll()
    {
        var store = new UserQuestionStore();
        store.CreateQuestions([
            new QuestionDef
            {
                Header = "Q1",
                Question = "问题1",
                Options = ["A", "B"],
            },
            new QuestionDef
            {
                Header = "Q2",
                Question = "问题2",
                Options = ["C", "D"],
            },
        ]);
        var tool = new GetPendingQuestionsTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new GetPendingQuestionsParams(),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("问题1", text);
        Assert.Contains("问题2", text);
        Assert.Contains("2 个", text);
    }

    private static string GetText(AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
