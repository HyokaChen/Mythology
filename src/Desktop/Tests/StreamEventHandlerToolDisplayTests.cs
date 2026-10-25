using System.Text.Json;
using Core.Agent;
using Core.Shared;
using Core.Tools;
using Desktop.Models;
using Desktop.Services;
using Xunit;
using ChatMessage = Desktop.Models.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// 实时工具步骤：友好名称 + 可读参数/结果摘要；进度原始输出不刷进对话；
/// provider 错误 JSON 不能原样落入助手气泡。
/// </summary>
public sealed class StreamEventHandlerToolDisplayTests
{
    private sealed class StubTool(string name, string label) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => "";
        public override JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        ) => Task.FromResult(new AgentToolResult());
    }

    private static StreamEventHandler CreateHandler(ChatMessage target)
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("manage_character", "角色管理"), "test");
        return new StreamEventHandler(target, toolDisplay: new ToolDisplayFormatter(registry));
    }

    [Fact]
    public void ToolLifecycle_ShowsFriendlyLabelAndSummary_AndKeepsFullDetails()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = CreateHandler(target);

        handler.Handle(
            new ToolExecutionStart("c1", "manage_character", """{"name":"林月如","novelId":"n1"}""")
        );

        var step = Assert.Single(target.AgentSteps);
        Assert.Equal("角色管理", step.Title);
        Assert.Equal("manage_character", step.ToolName);
        Assert.Equal("林月如", step.ArgumentsSummary);
        Assert.Contains("\"name\":\"林月如\"", step.FullArguments, StringComparison.Ordinal);

        // 进度事件携带的原始 partial（可能是整章正文）不得进入对话摘要。
        handler.Handle(new ToolExecutionUpdate("c1", new string('原', 500)));
        Assert.Null(step.ResultSummary);

        handler.Handle(
            new ToolExecutionEnd(
                "c1",
                false,
                """{"success":true,"characters":[{"name":"林月如"},{"name":"赵灵儿"}]}"""
            )
        );

        Assert.Equal(AgentStepStatus.Completed, step.Status);
        Assert.Equal("2 项", step.ResultSummary);
        Assert.Contains("characters", step.FullResult, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"", step.ResultSummary ?? "");
    }

    [Fact]
    public void ToolEnd_WithDiffDetails_AppliesStructuredDiffAndHidesRawResult()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = CreateHandler(target);

        handler.Handle(new ToolExecutionStart("c1", "edit", """{"path":"chapter.md"}"""));
        handler.Handle(
            new ToolExecutionEnd(
                "c1",
                false,
                "已应用 1/1 个编辑到 chapter.md",
                new TestDiffDetails(
                    "chapter.md",
                    [new EditHunk { OldText = "旧句子", NewText = "新句子" }]
                )
            )
        );

        var step = Assert.Single(target.AgentSteps);
        Assert.True(step.HasDiff);
        Assert.Equal("chapter.md · +1 -1", step.ResultSummary);
        Assert.False(step.ShowRawResultDetail);
        Assert.Contains(step.DiffLines, line => line is { IsDelete: true, Text: "旧句子" });
        Assert.Contains(step.DiffLines, line => line is { IsInsert: true, Text: "新句子" });
        Assert.Contains("已应用", step.FullResult, StringComparison.Ordinal);
    }

    private sealed record TestDiffDetails(string? Path, EditHunk[]? Hunks) : IDiffDetails;

    [Fact]
    public void ToolErrorWithoutResult_ShowsFailureSummary()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = CreateHandler(target);

        handler.Handle(new ToolExecutionStart("c1", "manage_character", "{}"));
        handler.Handle(new ToolExecutionEnd("c1", true, null));

        var step = Assert.Single(target.AgentSteps);
        Assert.Equal(AgentStepStatus.Failed, step.Status);
        Assert.True(step.IsError);
        Assert.Equal("失败", step.ResultSummary);
    }

    [Fact]
    public void ToolSearchResult_RedactsHiddenNamesInDetail()
    {
        const string hidden = "history_search_contents";
        var target = new ChatMessage(MessageSender.Agent);
        var handler = CreateHandler(target);

        handler.Handle(new ToolExecutionStart("c1", "tool_search", """{"query":"历史"}"""));
        handler.Handle(
            new ToolExecutionEnd("c1", false, $$"""{"results":[{"name":"{{hidden}}"}]}""")
        );

        var step = Assert.Single(target.AgentSteps);
        Assert.DoesNotContain(hidden, step.ResultSummary);
        Assert.DoesNotContain(hidden, step.FullResult);
        Assert.Contains("私有工具", step.FullResult, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentError_JsonIsFormattedForUser_NotDumpedRaw()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(
            new AgentError(
                """{"error":{"message":"Too Many Requests","type":"throttling_error"},"status":429}""",
                429,
                IsContextOverflow: false,
                Recoverable: true
            )
        );

        var rendered = target.Content.ToString();
        Assert.Contains("HTTP 429", rendered, StringComparison.Ordinal);
        Assert.Contains("Too Many Requests", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"error\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentError_PlainTextIsKeptAndTruncated()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(
            new AgentError(
                "OpenAIException - " + new string('x', 500),
                HttpStatusCode: null,
                IsContextOverflow: false,
                Recoverable: false
            )
        );

        var rendered = target.Content.ToString();
        Assert.DoesNotContain("OpenAIException", rendered, StringComparison.Ordinal);
        Assert.Contains("…", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void CircuitBreaker_UsesFriendlyLabel()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = CreateHandler(target);

        handler.Handle(
            new CircuitBreakerTriggered(
                "manage_character",
                "{}",
                RepeatCount: 3,
                Reason: "重复调用"
            )
        );

        var step = Assert.Single(target.AgentSteps);
        Assert.Equal("角色管理", step.Title);
        Assert.Equal(AgentStepStatus.Failed, step.Status);
    }
}
