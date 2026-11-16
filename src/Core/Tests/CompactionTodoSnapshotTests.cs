using System.Reflection;
using Core.Agent;
using Core.Data;
using Core.Novels;
using Core.Shared;
using Core.Tools;
using LlmTornado.Code;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AgentModel = Core.Agent.Agent;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>compact 摘要必须保留宿主 TodoStore 的权威任务清单，不能只靠对话里的 todo_write 记录。</summary>
public sealed class CompactionTodoSnapshotTests : IDisposable
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(),
        $"myth-compact-todos-{Guid.NewGuid():N}"
    );
    private readonly FileNovelStore _novelStore;
    private readonly FileSessionStore _sessionStore;
    private readonly AgentModel _agent;

    public CompactionTodoSnapshotTests()
    {
        var novelsRoot = Path.Combine(_base, "novels");
        var storeDir = Path.Combine(_base, "store");
        Directory.CreateDirectory(novelsRoot);
        Directory.CreateDirectory(storeDir);

        _novelStore = new FileNovelStore(novelsRoot, storeDir);
        _sessionStore = new FileSessionStore(storeDir, novelsRoot);
        _agent = new AgentModel(new ToolRegistry(), null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task GenerateCompactSummary_IncludesCurrentTodoSnapshot()
    {
        var todos = new TodoStore();
        var completed = todos.Create("补写并打磨第31章", status: TodoStatus.Completed);
        var active = todos.Create(
            "第33章《善意的第二次微笑》按细纲补足",
            description: "补足善我第二次现身与纸鸢钩子",
            activeForm: "补写第33章",
            status: TodoStatus.InProgress
        );
        var pending = todos.Create(
            "第39章《深夜的第二次蓝光》补足",
            blockedBy: [active.Id.ToString()],
            status: TodoStatus.Pending
        );
        todos.Create("已删除任务", status: TodoStatus.Deleted);

        var summary = await GenerateSummaryAsync(todos, previousSummary: null);

        Assert.Equal(1, summary.Split("## Todos / 任务清单", StringSplitOptions.None).Length - 1);
        Assert.Contains($"#{completed.Id} [completed] 补写并打磨第31章", summary);
        Assert.Contains($"#{active.Id} [in_progress] 第33章", summary);
        Assert.Contains("补写第33章", summary);
        Assert.Contains("纸鸢钩子", summary);
        Assert.Contains($"#{pending.Id} [pending] 第39章", summary);
        Assert.Contains($"blocked by: #{active.Id}", summary);
        Assert.Contains("todo_write", summary);
        Assert.DoesNotContain("已删除任务", summary);
        Assert.DoesNotContain("[deleted]", summary);
    }

    [Fact]
    public async Task GenerateCompactSummary_ReplacesStalePreviousTodoSection()
    {
        var todos = new TodoStore();
        var current = todos.Create("当前任务", status: TodoStatus.Pending);
        const string previous =
            "## Todos / 任务清单\n- #99 [pending] 旧任务\n\n"
            + "## Why / 为什么\n- 旧背景\n\n"
            + "## How / 怎么写\n- 旧风格\n\n"
            + "## Next / 下一步\n- 旧下一步";

        var summary = await GenerateSummaryAsync(todos, previous);

        Assert.Contains($"#{current.Id}", summary);
        Assert.DoesNotContain("#99", summary);
        Assert.DoesNotContain("旧任务", summary);
    }

    [Fact]
    public async Task GenerateCompactSummary_EmptyStore_ClearsStaleTodoSection()
    {
        var todos = new TodoStore();
        const string previous =
            "## Todos / 任务清单\n- #99 [pending] 旧任务\n\n"
            + "## Why / 为什么\n- 旧背景\n\n"
            + "## How / 怎么写\n- 旧风格\n\n"
            + "## Next / 下一步\n- 旧下一步";

        var summary = await GenerateSummaryAsync(todos, previous);

        Assert.Contains("- 当前没有任务", summary);
        Assert.DoesNotContain("#99", summary);
        Assert.DoesNotContain("旧任务", summary);
    }

    private async Task<string> GenerateSummaryAsync(TodoStore todos, string? previousSummary)
    {
        var engine = new CompactionEngine(
            _agent,
            _sessionStore,
            _novelStore,
            NullLogger.Instance,
            todos
        );
        var messages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "修正优化第21-60章，完成后更新任务清单。"),
            new(ChatMessageRoles.Assistant, "开始处理。"),
        };
        var method = typeof(CompactionEngine).GetMethod(
            "GenerateCompactSummaryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var task =
            (Task<string>)
                method.Invoke(
                    engine,
                    [
                        messages,
                        messages.Count,
                        previousSummary,
                        "",
                        null,
                        null,
                        Array.Empty<string>(),
                        CancellationToken.None,
                    ]
                )!;
        return await task;
    }
}
