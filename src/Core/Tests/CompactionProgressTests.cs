using Core.Agent;
using Core.Data;
using Core.Novels;
using LlmTornado.Chat;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>进度必须来自工具事实，不能从章节字数或模型叙述推断。</summary>
public sealed class CompactionProgressTests
{
    private const string RevisionInstruction =
        "修正优化整个第二卷 分卷[ID:v2]:夏夜的萤火 的所有章节内容！！";

    [Fact]
    public void FormatAndParse_RoundTrips()
    {
        var progress = CompactionTaskProgress.Create(
            [21, 22, 24, 30],
            [23, 25],
            currentOrder: 30,
            nextOrder: 23,
            outOfScopeOrders: [54]
        );

        var parsed = CompactionTaskProgress.TryParse("## 其他\n\n" + progress.FormatSection());

        Assert.NotNull(parsed);
        Assert.Equal([21, 22, 24, 30], parsed!.EditedOrders);
        Assert.Equal([23, 25], parsed.InspectedOrders);
        Assert.Equal(30, parsed.CurrentOrder);
        Assert.Equal(23, parsed.NextOrder);
        Assert.Equal([54], parsed.OutOfScopeOrders);
    }

    [Fact]
    public void Extract_UsesSuccessfulToolCallsAndKeepsPreviousProgress()
    {
        var structure = BuildStructure();
        var scope = CompactionTaskScope.TryCreate(RevisionInstruction, structure)!;
        var previous = CompactionTaskProgress.Create([30], [], 30, 23, []);
        var messages = new List<LlmChatMessage>();

        AddCall(messages, "edit-21", "edit_text", """{"chapter-id":"v2-c21"}""", true);
        AddCall(messages, "edit-22", "edit_text", """{"chapter-id":"v2-c22"}""", true);
        AddCall(messages, "edit-23-failed", "edit_text", """{"chapter-id":"v2-c23"}""", false);
        AddCall(messages, "edit-24", "edit_text", """{"chapter-id":"v2-c24"}""", true);
        AddCall(messages, "read-25", "read_chapter", """{"chapter-id":"v2-c25"}""", true);
        AddCall(messages, "write-54", "write_chapter", """{"chapter-id":"c54"}""", true);
        messages.Add(
            new LlmChatMessage(
                ChatMessageRoles.User,
                "[远期推演] 已写至第53章，下一章：第54章《恶意的瞥见》"
            )
        );

        var progress = CompactionProgressExtractor.Extract(messages, structure, scope, previous);

        Assert.NotNull(progress);
        Assert.Equal([21, 22, 24, 30], progress!.EditedOrders);
        Assert.Equal([25], progress.InspectedOrders);
        Assert.Equal(24, progress.CurrentOrder);
        Assert.Equal(23, progress.NextOrder);
        Assert.Equal([54], progress.OutOfScopeOrders);
        Assert.DoesNotContain(23, progress.EditedOrders);
    }

    [Fact]
    public void Extract_OutOfScopeWriteOnly_RecordsDeviation()
    {
        var structure = BuildStructure();
        var scope = CompactionTaskScope.TryCreate(RevisionInstruction, structure)!;
        var messages = new List<LlmChatMessage>();
        AddCall(messages, "write-54", "write_chapter", """{"chapter-id":"c54"}""", true);

        var progress = CompactionProgressExtractor.Extract(
            messages,
            structure,
            scope,
            previous: null
        );

        Assert.NotNull(progress);
        Assert.Empty(progress!.EditedOrders);
        Assert.Equal([54], progress.OutOfScopeOrders);
        Assert.Null(progress.CurrentOrder);
        Assert.Equal(21, progress.NextOrder);
    }

    [Fact]
    public void BuildCompactSummaryWithScope_EnforcesProgressAndNext()
    {
        var structure = BuildStructure();
        var scope = CompactionTaskScope.TryCreate(RevisionInstruction, structure)!;
        var progress = CompactionTaskProgress.Create(
            [21, 22, 24, 30],
            [25],
            currentOrder: 30,
            nextOrder: 23,
            outOfScopeOrders: [54]
        );
        var messages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, RevisionInstruction),
            new(ChatMessageRoles.Assistant, "开始处理。"),
        };
        const string snapshot =
            "## Scope / 任务边界\n- 旧 Scope\n\n"
            + "## Progress / 进度\n- 旧进度\n\n"
            + "## Where / 写到哪里\n"
            + "- 当前章（任务焦点）：第30章《萤火30》\n"
            + "- 下一章（任务焦点）：第23章《萤火23》\n"
            + "- 全局下一章（不是本任务 Next）：第54章《恶意的瞥见》\n\n"
            + "## Next / 下一步\n- 写第54章《恶意的瞥见》";
        const string previous = "## Next / 下一步\n- 写第54章《恶意的瞥见》";

        var method = typeof(CompactionEngine).GetMethod(
            "BuildCompactSummaryWithScope",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        )!;
        var summary = (string)
            method.Invoke(
                null,
                [messages, messages.Count, previous, snapshot, scope, progress, RevisionInstruction]
            )!;

        Assert.StartsWith("> 作者指令（逐字，最高优先级）：", summary);
        Assert.Contains("已实际编辑：21、22、24、30", summary);
        Assert.Contains("当前章（任务焦点）：第30章", summary);
        var nextIndex = summary.IndexOf("## Next / 下一步", StringComparison.Ordinal);
        Assert.True(nextIndex >= 0);
        Assert.Contains("第23章", summary[nextIndex..]);
        Assert.DoesNotContain("写第54章", summary[nextIndex..]);
    }

    [Fact]
    public void Extract_WithoutScope_ReturnsNull()
    {
        var progress = CompactionProgressExtractor.Extract(
            [],
            BuildStructure(),
            scope: null,
            previous: CompactionTaskProgress.Create([21], [], 21, 22, [])
        );

        Assert.Null(progress);
    }

    [Fact]
    public void EnforceTaskScope_ContradictingInScopeNext_AlignedWithProgress()
    {
        var structure = BuildStructure();
        var scope = CompactionTaskScope.TryCreate(RevisionInstruction, structure)!;
        var progress = CompactionTaskProgress.Create(
            [21, 22, 24, 30],
            [],
            currentOrder: 30,
            nextOrder: 23,
            outOfScopeOrders: []
        );
        const string summary = "## Next / 下一步\n- 优化第21章《萤火21》；范围内但进度显示已编辑过";

        var enforced = CompactionEngine.EnforceTaskScope(
            summary,
            scope,
            previousSummary: null,
            progress
        );

        var nextIndex = enforced.IndexOf("## Next / 下一步", StringComparison.Ordinal);
        Assert.True(nextIndex >= 0);
        Assert.Contains("第23章", enforced[nextIndex..]);
        Assert.DoesNotContain("第21章", enforced[nextIndex..]);
    }

    [Fact]
    public void EnforceTaskScope_NextMatchingProgress_Kept()
    {
        var structure = BuildStructure();
        var scope = CompactionTaskScope.TryCreate(RevisionInstruction, structure)!;
        var progress = CompactionTaskProgress.Create([21], [], 21, 22, []);
        const string summary = "## Next / 下一步\n- 优先优化第22章《萤火22》开头五百字";

        var enforced = CompactionEngine.EnforceTaskScope(
            summary,
            scope,
            previousSummary: null,
            progress
        );

        Assert.Contains("开头五百字", enforced);
    }

    [Fact]
    public void BuildCompactSummary_InheritsProgressWithScope()
    {
        var messages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "继续"),
            new(ChatMessageRoles.Assistant, "好的。"),
        };
        const string previous =
            "## Scope / 任务边界\n- 任务类型：修订/优化已有章节（不是续写新章）\n\n"
            + "## Progress / 进度\n- 已实际编辑：21、22\n- 当前：第22章\n- 下一章：第23章\n\n"
            + "## Next / 下一步\n- 优化第23章《萤火23》";

        var method = typeof(CompactionEngine).GetMethod(
            "BuildCompactSummaryWithScope",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        )!;
        var summary = (string)
            method.Invoke(null, [messages, messages.Count, previous, "", null, null, null])!;

        Assert.Contains("## Scope / 任务边界", summary);
        Assert.Contains("已实际编辑：21、22", summary);
        Assert.Contains("下一章：第23章", summary);
    }

    [Fact]
    public void Extract_WriteChapterNewChapterWithOrder_CountsAsEdited()
    {
        var structure = BuildStructure();
        var scope = CompactionTaskScope.TryCreate(RevisionInstruction, structure)!;
        var messages = new List<LlmChatMessage>();
        AddCall(
            messages,
            "write-23",
            "write_chapter",
            """{"order":23,"title":"萤火23","volume-id":"v2","content":"新写一章"}""",
            true
        );

        var progress = CompactionProgressExtractor.Extract(
            messages,
            structure,
            scope,
            previous: null
        );

        Assert.NotNull(progress);
        Assert.Contains(23, progress!.EditedOrders);
        Assert.Equal(21, progress.NextOrder);
    }

    [Fact]
    public void Extract_ChapterRefForms_ResolveLikeTools()
    {
        var structure = BuildStructure();
        var scope = CompactionTaskScope.TryCreate(RevisionInstruction, structure)!;
        var messages = new List<LlmChatMessage>();
        AddCall(messages, "edit-cn", "edit_text", """{"chapter-id":"第23章"}""", true);
        AddCall(messages, "edit-title", "edit_text", """{"chapter-id":"萤火25"}""", true);
        AddCall(messages, "edit-ch", "edit_text", """{"chapter-id":"ch24"}""", true);

        var progress = CompactionProgressExtractor.Extract(
            messages,
            structure,
            scope,
            previous: null
        );

        Assert.NotNull(progress);
        Assert.Equal([23, 24, 25], progress!.EditedOrders);
    }

    private static void AddCall(
        List<LlmChatMessage> messages,
        string callId,
        string toolName,
        string arguments,
        bool success
    )
    {
        messages.Add(
            new LlmChatMessage(ChatMessageRoles.Assistant, "")
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = callId,
                        FunctionCall = new FunctionCall { Name = toolName, Arguments = arguments },
                    },
                ],
            }
        );
        messages.Add(
            new LlmChatMessage(ChatMessageRoles.Tool, success ? "ok" : "未找到匹配的旧文本")
            {
                ToolCallId = callId,
                ToolInvocationSucceeded = success,
            }
        );
    }

    private static NovelStructure BuildStructure()
    {
        const string novelId = "n-progress";
        var novel = new Novel
        {
            Id = novelId,
            NovelId = novelId,
            Title = "进度测试",
            Genre = "都市",
            WritingStyle = "克制",
        };
        var volume2 = new Volume
        {
            Id = "v2",
            NovelId = novelId,
            Name = "夏夜的萤火",
            Order = 2,
        };
        var volume3 = new Volume
        {
            Id = "v3",
            NovelId = novelId,
            Name = "第三卷",
            Order = 3,
        };
        var chapters = new List<Chapter>();
        for (var order = 21; order <= 40; order++)
        {
            chapters.Add(
                new Chapter
                {
                    Id = $"v2-c{order}",
                    NovelId = novelId,
                    Title = $"萤火{order}",
                    Order = order,
                    VolumeId = volume2.Id,
                    Status = ChapterStatus.Draft,
                    Content = new string('文', 100),
                }
            );
        }
        chapters.Add(
            new Chapter
            {
                Id = "c53",
                NovelId = novelId,
                Title = "工地之夜",
                Order = 53,
                VolumeId = volume3.Id,
                Status = ChapterStatus.Draft,
                Content = new string('文', 500),
            }
        );
        chapters.Add(
            new Chapter
            {
                Id = "c54",
                NovelId = novelId,
                Title = "恶意的瞥见",
                Order = 54,
                VolumeId = volume3.Id,
                Status = ChapterStatus.Draft,
            }
        );
        return new NovelStructure(novel, [volume2, volume3], chapters, []);
    }
}
