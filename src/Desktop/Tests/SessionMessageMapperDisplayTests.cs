using System.Text.Json;
using Core.Data;
using Core.Tools;
using Desktop.Models;
using Desktop.Services;
using Xunit;
using ChatMessage = Desktop.Models.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// 会话历史映射：注入消息不得伪装成用户气泡；工具步骤使用友好名称与语义摘要。
/// </summary>
public sealed class SessionMessageMapperDisplayTests
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

    private static ToolDisplayFormatter CreateFormatter()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("manage_character", "角色管理"), "test");
        registry.Register(new StubTool("edit", "编辑文件"), "test");
        return new ToolDisplayFormatter(registry);
    }

    [Theory]
    [InlineData("[本体 harness] 当前章节违反设定")]
    [InlineData("[云端诊断] 素材参考")]
    [InlineData("[远期推演] 下一章")]
    [InlineData("[联想记忆] 呼应前文")]
    [InlineData("[写作战术 · 历史验证]\n- 示例")]
    [InlineData("[风格观察] 收敛度偏高")]
    [InlineData("[人工裁决] 你选择了继续")]
    [InlineData("[系统通知] 用户手动中断")]
    [InlineData("[系统提醒] 检测到对话方向偏离")]
    [InlineData("[上下文页]\n首页 ID: w1")]
    [InlineData("📋 上下文使用率 >70%，建议压缩已消耗内容。")]
    [InlineData("📋 写作事件信号")]
    [InlineData("📋 审计未通过，请继续完成")]
    [InlineData("User answers:\n方向: 身世")]
    [InlineData("**用户回复:**\n方向: 身世")]
    public void MapUi_HidesInternalInjectedUserMessages(string text)
    {
        var ui = SessionMessageMapper.MapUi([
            new Core.Data.ChatMessage { Role = MessageRole.User, Content = text },
        ]);

        Assert.Empty(ui);
    }

    [Fact]
    public void MapUi_KeepsRealUserMessage_AndDropsEmptyUser()
    {
        var ui = SessionMessageMapper.MapUi([
            new Core.Data.ChatMessage { Role = MessageRole.User, Content = "帮我看看林月如" },
            new Core.Data.ChatMessage { Role = MessageRole.User, Content = "   " },
        ]);

        var msg = Assert.Single(ui);
        Assert.Equal(MessageSender.User, msg.Sender);
        Assert.Equal("帮我看看林月如", msg.Content.ToString());
    }

    [Fact]
    public void MapUi_AssistantSystemNotice_IsRenderedAsSystemRow()
    {
        var ui = SessionMessageMapper.MapUi([
            new Core.Data.ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = "[系统] 上下文已压缩",
            },
            new Core.Data.ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = "[系统提醒] 检测到对话方向偏离",
            },
        ]);

        Assert.Equal(2, ui.Count);
        Assert.All(ui, msg => Assert.Equal(MessageSender.System, msg.Sender));
        Assert.Equal("上下文已压缩", ui[0].Content.ToString());
        Assert.Equal("检测到对话方向偏离", ui[1].Content.ToString());
    }

    [Fact]
    public void MapUi_AssistantReasoning_IsExpandedByDefault()
    {
        var assistant = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "答案正文",
            ReasoningContent = "历史思考正文",
        };

        var ui = SessionMessageMapper.MapUi([assistant]);

        var bubble = Assert.Single(ui);
        Assert.Equal("答案正文", bubble.Content.ToString());
        var step = Assert.Single(bubble.AgentSteps);
        Assert.Equal(StepKind.Thinking, step.Kind);
        Assert.Equal("历史思考正文", step.ThinkingText);
        Assert.True(step.HasThinking);
        Assert.True(step.HasDetails);
        Assert.True(step.IsDetailsExpanded);
        Assert.Equal("收起", step.DetailsToggleLabel);

        step.IsDetailsExpanded = false;
        Assert.Equal("展开", step.DetailsToggleLabel);
    }

    [Fact]
    public void MapUi_InterruptedMarker_BecomesInterruptedFlagWithoutQuoteContent()
    {
        var withBody = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "写好的正文\n\n> ⏹ 已中断",
        };
        var markerOnly = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "> ⏹ 已中断",
        };

        var ui = SessionMessageMapper.MapUi([withBody, markerOnly]);

        Assert.Equal(2, ui.Count);
        Assert.Equal("写好的正文", ui[0].Content.ToString());
        Assert.True(ui[0].IsInterrupted);
        Assert.True(ui[1].IsInterrupted);
        Assert.Equal("", ui[1].Content.ToString());
    }

    [Fact]
    public void MapUi_ToolStep_UsesFriendlyLabelAndSemanticSummaries()
    {
        var assistant = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "我先查一下角色。",
            ToolCallsJson = JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        id = "c1",
                        name = "manage_character",
                        arguments = """{"action":"list","name":"林月如","novelId":"n1"}""",
                    },
                }
            ),
        };
        var result = new Core.Data.ChatMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = "c1",
            ToolName = "manage_character",
            Content = """{"success":true,"characters":[{"name":"林月如"},{"name":"赵灵儿"}]}""",
        };

        var ui = SessionMessageMapper.MapUi([assistant, result], CreateFormatter());

        var bubble = Assert.Single(ui);
        Assert.Equal(2, bubble.AgentSteps.Count);
        var content = bubble.AgentSteps[0];
        Assert.Equal(StepKind.Content, content.Kind);
        Assert.Equal("我先查一下角色。", content.ContentText);
        Assert.Equal("", bubble.Content.ToString());
        var step = bubble.AgentSteps[1];
        Assert.Equal("角色管理", step.Title);
        Assert.Equal("manage_character", step.ToolName);
        Assert.Equal("林月如", step.ArgumentsSummary);
        Assert.Contains("\"name\":\"林月如\"", step.FullArguments, StringComparison.Ordinal);
        Assert.Equal("2 项", step.ResultSummary);
        Assert.Contains("characters", step.FullResult, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"", step.ArgumentsSummary ?? "");
        Assert.DoesNotContain("{\"", step.ResultSummary ?? "");
        Assert.False(step.IsDetailsExpanded);
        Assert.True(step.HasDetails);
        Assert.Equal("详情", step.DetailsToggleLabel);

        step.IsDetailsExpanded = true;
        Assert.Equal("收起", step.DetailsToggleLabel);
    }

    [Fact]
    public void MapUi_IntermediateText_TrimsSurroundingBlankLines()
    {
        var assistant = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "\n\n我先查一下角色。\n\n",
            ToolCallsJson = JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        id = "c1",
                        name = "manage_character",
                        arguments = """{"action":"list"}""",
                    },
                }
            ),
        };

        var ui = SessionMessageMapper.MapUi([assistant], CreateFormatter());

        Assert.Equal(2, ui[0].AgentSteps.Count);
        var step = ui[0].AgentSteps[0];
        Assert.Equal(StepKind.Content, step.Kind);
        Assert.Equal("我先查一下角色。", step.ContentText);
    }

    [Fact]
    public void MapUi_ReasoningAndTextBeforeTool_KeepChronologicalSteps()
    {
        var assistant = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "先查一下角色。",
            ReasoningContent = "我需要先确认角色现状。",
            ToolCallsJson = JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        id = "c1",
                        name = "manage_character",
                        arguments = """{"action":"list","novelId":"n1"}""",
                    },
                }
            ),
        };

        var bubble = Assert.Single(SessionMessageMapper.MapUi([assistant], CreateFormatter()));

        Assert.Collection(
            bubble.AgentSteps,
            thinking =>
            {
                Assert.Equal(StepKind.Thinking, thinking.Kind);
                Assert.Equal("我需要先确认角色现状。", thinking.ThinkingText);
            },
            content =>
            {
                Assert.Equal(StepKind.Content, content.Kind);
                Assert.Equal("先查一下角色。", content.ContentText);
            },
            tool => Assert.Equal(StepKind.ToolCall, tool.Kind)
        );
        Assert.Equal("", bubble.Content.ToString());
        Assert.False(bubble.HasVisibleContent);
    }

    [Fact]
    public void MapUi_ToolResultWithDiff_ShowsFileAndChangeCounts()
    {
        var diff =
            "```diff\n--- a/chapter.md\n+++ b/chapter.md\n@@ -1,3 +1,3 @@\n-旧句子\n+新句子\n 上下文\n```";
        var assistant = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            ToolCallsJson = JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        id = "c1",
                        name = "edit",
                        arguments = """{"path":"chapter.md","edits":[{"oldText":"旧句子","newText":"新句子"}]}""",
                    },
                }
            ),
        };
        var result = new Core.Data.ChatMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = "c1",
            ToolName = "edit",
            Content = diff,
        };

        var step = Assert.Single(
            Assert
                .Single(SessionMessageMapper.MapUi([assistant, result], CreateFormatter()))
                .AgentSteps
        );

        Assert.Equal("编辑文件", step.Title);
        Assert.Equal("chapter.md", step.ArgumentsSummary);
        Assert.Equal("chapter.md · +1 -1", step.ResultSummary);
        Assert.Contains("-旧句子", step.FullResult, StringComparison.Ordinal);
        Assert.Contains("+新句子", step.FullResult, StringComparison.Ordinal);
        Assert.False(step.IsDetailsExpanded);
        Assert.True(step.HasDiff);
        Assert.False(step.ShowRawResultDetail);
        Assert.Contains(step.DiffLines, line => line is { IsDelete: true, Text: "旧句子" });
        Assert.Contains(step.DiffLines, line => line is { IsInsert: true, Text: "新句子" });
    }

    [Theory]
    [InlineData("已应用 1/1 个编辑到 chapter.md:\n✅ 旧句子 → 新句子", true)]
    [InlineData("已应用 1/2 个编辑到 chapter.md:\n✅ 旧句子 → 新句子", false)]
    [InlineData("未应用任何编辑到 chapter.md:\n❌ 未找到匹配", false)]
    public void MapUi_EditDiff_OnlyRebuildsWhenAllEditsApplied(string result, bool expectDiff)
    {
        var assistant = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            ToolCallsJson = JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        id = "c1",
                        name = "edit",
                        arguments = """{"path":"chapter.md","edits":[{"oldText":"旧句子","newText":"新句子"}]}""",
                    },
                }
            ),
        };
        var tool = new Core.Data.ChatMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = "c1",
            ToolName = "edit",
            Content = result,
        };

        var step = Assert.Single(
            Assert
                .Single(SessionMessageMapper.MapUi([assistant, tool], CreateFormatter()))
                .AgentSteps
        );

        Assert.Equal(expectDiff, step.HasDiff);
        if (!expectDiff)
            Assert.DoesNotContain("旧句子", step.ResultSummary ?? "");
    }

    [Fact]
    public void MapUi_ToolSearchResult_RedactsHiddenToolNamesInSummaryAndDetail()
    {
        const string hidden = "history_search_contents";
        var assistant = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            ToolCallsJson = JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        id = "c1",
                        name = "tool_search",
                        arguments = """{"query":"历史"}""",
                    },
                }
            ),
        };
        var result = new Core.Data.ChatMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = "c1",
            ToolName = "tool_search",
            Content = $$"""{"results":[{"name":"{{hidden}}"}]}""",
        };

        var ui = SessionMessageMapper.MapUi([assistant, result]);

        var step = Assert.Single(Assert.Single(ui).AgentSteps);
        Assert.DoesNotContain(hidden, step.ResultSummary);
        Assert.DoesNotContain(hidden, step.FullResult);
        Assert.Contains("私有工具", step.FullResult, StringComparison.Ordinal);
    }

    [Fact]
    public void MapUi_OrphanToolResult_UsesFriendlyLabelAndSummary()
    {
        var result = new Core.Data.ChatMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = "missing",
            ToolName = "manage_character",
            Content = """{"success":true,"message":"已更新角色"}""",
        };

        var msg = Assert.Single(SessionMessageMapper.MapUi([result], CreateFormatter()));
        Assert.Equal(MessageSender.System, msg.Sender);
        Assert.Contains("角色管理", msg.Content.ToString(), StringComparison.Ordinal);
        Assert.Contains("已更新角色", msg.Content.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("{\"", msg.Content.ToString(), StringComparison.Ordinal);
    }
}
