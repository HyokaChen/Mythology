using System.Reflection;
using Core.Agent;
using LlmTornado.Code;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>连续 compact 只能保留一份 5W1H 检查点，旧摘要不得嵌套。</summary>
public sealed class CompactionCheckpointTests
{
    [Fact]
    public void SummaryRequest_UsesActiveModelAndOutputBudget()
    {
        var request = CompactionEngine.BuildSummaryRequest("DeepSeek-V4-Flash", 8_192);

        Assert.Equal("DeepSeek-V4-Flash", (string)request.Model!);
        Assert.Equal(8_192, request.MaxTokens);
    }

    [Fact]
    public void CompactSummary_TwoPasses_AreSingleCheckpoint()
    {
        var messages = new List<LlmChatMessage>();
        for (var i = 0; i < 8; i++)
        {
            messages.Add(
                new LlmChatMessage(
                    i % 2 == 0 ? ChatMessageRoles.User : ChatMessageRoles.Assistant,
                    $"第 {i} 条：{new string('文', 500)}"
                )
            );
        }

        var method = typeof(CompactionEngine).GetMethod(
            "BuildCompactSummary",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var first = (string)method.Invoke(null, [messages, 6, null, ""])!;
        var second = (string)method.Invoke(null, [messages, 6, first, ""])!;

        foreach (
            var heading in new[]
            {
                "## Where / 写到哪里",
                "## Who / 谁出场",
                "## What / 发生了什么",
                "## Why / 为什么",
                "## When / 时间",
                "## How / 怎么写",
                "## Next / 下一步",
            }
        )
        {
            Assert.Equal(1, second.Split(heading, StringSplitOptions.None).Length - 1);
        }
        Assert.DoesNotContain("## 压缩概览", second);
        Assert.DoesNotContain("## 之前的摘要", second);
        Assert.DoesNotContain("## 文件操作", second);
        Assert.DoesNotContain("## 目标", second);

        const string legacy =
            "## 压缩概览\n压缩前 31 条，保留 30 条\n"
            + "## 之前的摘要\n## 目标\n旧目标\n## 文件操作\nread(...)\n## 下一步\n旧下一步\n"
            + "[上下文摘要 b6]\n## 进展\n旧进展";
        var migrated = (string)method.Invoke(null, [messages, 6, legacy, ""])!;
        Assert.DoesNotContain("## 压缩概览", migrated);
        Assert.DoesNotContain("## 之前的摘要", migrated);
        Assert.DoesNotContain("## 文件操作", migrated);
        Assert.Contains("继承背景", migrated);
        Assert.Equal(1, migrated.Split("## Next / 下一步", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void CompactSummary_KeepsAuthorInstruction_AndDemotesForecastToSystemNote()
    {
        var messages = new List<LlmChatMessage>
        {
            new(
                ChatMessageRoles.User,
                "修正优化整个第二卷 分卷[ID:0a009638c126]:夏夜的萤火 的所有章节内容！！"
            ),
            new(ChatMessageRoles.Assistant, "开始处理第二卷。"),
            new(
                ChatMessageRoles.User,
                "[远期推演] 已写至第53章，下一章：第54章《皮皮龙的灵觉与恶意的瞥见》"
            ),
            new(ChatMessageRoles.Assistant, "收到。"),
        };

        var dialogueMethod = typeof(CompactionEngine).GetMethod(
            "BuildCompressedDialogue",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var dialogue = (string)dialogueMethod.Invoke(null, [messages, messages.Count])!;

        Assert.Contains("[user]: 修正优化整个第二卷", dialogue);
        Assert.Contains("[system-note]: [远期推演]", dialogue);
        Assert.DoesNotContain("[user]: [远期推演]", dialogue);

        var summaryMethod = typeof(CompactionEngine).GetMethod(
            "BuildCompactSummary",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var summary = (string)summaryMethod.Invoke(null, [messages, messages.Count, null, ""])!;

        Assert.StartsWith("> 作者指令（逐字，最高优先级）：修正优化整个第二卷", summary);
        Assert.Contains("修正优化整个第二卷", summary);
    }
}
