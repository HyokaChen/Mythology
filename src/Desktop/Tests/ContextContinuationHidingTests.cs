using System.Text.Json;
using Core.Agent;
using Core.Data;
using Core.Tools;
using Desktop.Models;
using Desktop.Services;
using Xunit;
using ChatMessage = Desktop.Models.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// 上下文接续机制在 Desktop 侧对用户隐藏：实时步骤卡、恢复会话渲染、工具结果回填
/// 都不得出现 new_context / history_*。持久化内容不删（history_* 仍可检索），仅渲染层过滤。
/// </summary>
public sealed class ContextContinuationHidingTests
{
    private const string Hidden = "history_search_contents";
    private const string Visible = "read_file";

    // ═══════════════════════════════════════════════════════════
    //  实时流：隐藏工具不建步骤卡
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void StreamHandler_HiddenToolStart_AddsNoStepCard()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new ToolExecutionStart("call-hidden", Hidden, "{}"));
        Assert.Empty(target.AgentSteps);

        // 后续 End/Update 也不应产生任何痕迹（无步骤可回填）
        handler.Handle(new ToolExecutionUpdate("call-hidden", "命中 3 条历史"));
        handler.Handle(new ToolExecutionEnd("call-hidden", false, "命中 3 条历史"));
        Assert.Empty(target.AgentSteps);
        Assert.DoesNotContain(Hidden, target.Content.ToString());

        // 同一条消息里的普通工具照常建卡并回填
        handler.Handle(new ToolExecutionStart("call-visible", Visible, """{"path":"ch000.md"}"""));
        handler.Handle(new ToolExecutionEnd("call-visible", false, "文件内容"));
        var step = Assert.Single(target.AgentSteps);
        Assert.Equal(Visible, step.Title);
        Assert.Equal(AgentStepStatus.Completed, step.Status);
    }

    // ═══════════════════════════════════════════════════════════
    //  恢复会话：隐藏工具的调用与结果都不渲染
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void MapUi_SkipsHiddenToolCallsAndResults()
    {
        var assistant = new Core.Data.ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "我先查一下历史。",
            ToolCallsJson = JsonSerializer.Serialize(
                new[]
                {
                    // 线上 JSONL 里 toolCalls 用小写键（JsonlSerializer camelCase）
                    new
                    {
                        id = "c1",
                        name = Hidden,
                        arguments = """{"query":"上一页改过哪些文件"}""",
                    },
                    new
                    {
                        id = "c2",
                        name = Visible,
                        arguments = """{"path":"ch001.md"}""",
                    },
                }
            ),
        };
        var hiddenResult = new Core.Data.ChatMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = "c1",
            ToolName = Hidden,
            Content = "[窗口 w0 | id 0193a4b7c012 | order 128 | user]",
        };
        var visibleResult = new Core.Data.ChatMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = "c2",
            ToolName = Visible,
            Content = "第一章正文",
        };

        var ui = SessionMessageMapper.MapUi([assistant, hiddenResult, visibleResult]);

        var bubble = Assert.Single(ui);
        var step = Assert.Single(bubble.AgentSteps);
        Assert.Equal(Visible, step.Title);
        Assert.Equal("第一章正文", step.ResultSummary);

        // 隐藏工具名与其结果原文都不得出现在任何渲染文本里
        var rendered = string.Join(
            "\n",
            ui.Select(m =>
                m.Content
                + string.Join(
                    "\n",
                    m.AgentSteps.Select(s => s.Title + s.ArgumentsSummary + s.ResultSummary)
                )
            )
        );
        Assert.DoesNotContain(Hidden, rendered);
        Assert.DoesNotContain("窗口 w0", rendered);
    }

    [Fact]
    public void MapUi_HiddenToolResultWithoutOwner_ProducesNoRow()
    {
        var hiddenResult = new Core.Data.ChatMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = "c1",
            ToolName = Hidden,
            Content = "[窗口 w0 | id 0193a4b7c012 | order 128 | user]",
        };

        var ui = SessionMessageMapper.MapUi([hiddenResult]);

        Assert.Empty(ui);
    }

    // ═══════════════════════════════════════════════════════════
    //  工具清单：/state 的 Top 工具不含隐藏工具
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TopToolUsage_ExcludesHiddenTools()
    {
        var stats = new ToolUsageStats();
        stats.RecordCall(Hidden);
        stats.RecordCall(Hidden);
        stats.RecordCall(Visible);

        var summary = stats.GetSummary();

        Assert.DoesNotContain(Hidden, summary);
        Assert.Contains(Visible, summary); // 隐藏工具被过滤，而不是把整个统计清空
    }
}
