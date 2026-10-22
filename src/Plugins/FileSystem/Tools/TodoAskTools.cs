using System.ComponentModel;
using System.Text;
using Core.Shared;
using Core.Tools;

namespace Plugins.FileSystem.Tools;

// ── 任务管理工具 ──

/// <summary>
/// 任务管理工具：创建、更新、删除、列出任务。
/// 支持状态流转（pending→in_progress→completed）和依赖关系（blockedBy）。
/// </summary>
public sealed class TodoWriteTool(TodoStore store) : AgentTool<TodoWriteParams, TodoWriteDetails>
{
    public override string Name => "todo_write";
    public override string Label => "管理任务";
    public override string Description =>
        "创建/更新/删除/列出任务。支持状态流转 (pending→in_progress→completed)、依赖关系 (blockedBy)";

    public override ToolCategory Category => ToolCategory.Generic;
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 解析状态字符串 → 枚举。LLM 参数用 snake_case（in_progress/completed），
    /// 枚举名是 PascalCase（InProgress/Completed）——Enum.TryParse 对下划线严格匹配，
    /// 直接解析会静默失败导致状态永不生效（todo 永远停在 Pending）。
    /// 去掉下划线后忽略大小写匹配即可双向兼容。
    /// </summary>
    private static TodoStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return null;
        var normalized = status.Replace("_", ""); // in_progress → inprogress
        return Enum.TryParse<TodoStatus>(normalized, ignoreCase: true, out var s) ? s : null;
    }

    public override Task<AgentToolResult<TodoWriteDetails>> ExecuteAsync(
        string toolCallId,
        TodoWriteParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<TodoWriteDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var sb = new StringBuilder();

        // 根据操作类型分发处理
        switch (args.Action.ToLowerInvariant())
        {
            case "create":
            {
                // 可选初始状态：LLM 常用 create + status=in_progress 一步建立进行中任务
                // （此前 status 被静默丢弃，任务永远停在 Pending，TUI 无法展示 🔄）
                var status = ParseStatus(args.Status);

                // 创建新任务
                var item = store.Create(
                    args.Subject ?? "(untitled)",
                    args.Description,
                    args.Owner,
                    args.ActiveForm,
                    args.AddBlockedBy,
                    status
                );
                sb.Append($"✅ 任务 #{item.Id} 已创建: {item.Subject}");
                // 非默认状态回显，让 LLM/用户可见状态已生效（如 [InProgress]）
                if (item.Status != TodoStatus.Pending)
                    sb.Append($" [{item.Status}]");
                if (item.BlockedBy.Length > 0)
                    sb.Append($" (blocked by: {string.Join(", ", item.BlockedBy)})");
                return Task.FromResult(
                    new AgentToolResult<TodoWriteDetails>
                    {
                        Content = [new TextContent(sb.ToString())],
                        Details = new TodoWriteDetails { Action = "create", Id = item.Id },
                    }
                );
            }

            case "update":
            {
                // 更新现有任务
                if (args.Id is null)
                {
                    return Task.FromResult(
                        new AgentToolResult<TodoWriteDetails>
                        {
                            Content = [new TextContent("update 需要指定 id")],
                            IsError = true,
                        }
                    );
                }
                var updated = store.Update(
                    args.Id.Value,
                    subject: args.Subject,
                    description: args.Description,
                    status: args.Status,
                    owner: args.Owner,
                    activeForm: args.ActiveForm,
                    addBlockedBy: args.AddBlockedBy,
                    removeBlockedBy: args.RemoveBlockedBy
                );
                if (updated is null)
                {
                    return Task.FromResult(
                        new AgentToolResult<TodoWriteDetails>
                        {
                            Content = [new TextContent($"任务 #{args.Id} 不存在")],
                            IsError = true,
                        }
                    );
                }
                sb.Append($"✅ 任务 #{updated.Id} 已更新: {updated.Subject} [{updated.Status}]");
                return Task.FromResult(
                    new AgentToolResult<TodoWriteDetails>
                    {
                        Content = [new TextContent(sb.ToString())],
                        Details = new TodoWriteDetails { Action = "update", Id = updated.Id },
                    }
                );
            }

            case "delete":
            {
                // 删除任务
                if (args.Id is null)
                {
                    return Task.FromResult(
                        new AgentToolResult<TodoWriteDetails>
                        {
                            Content = [new TextContent("delete 需要指定 id")],
                            IsError = true,
                        }
                    );
                }
                var deleted = store.Delete(args.Id.Value);
                sb.Append(deleted ? $"✅ 任务 #{args.Id} 已删除" : $"任务 #{args.Id} 不存在");
                return Task.FromResult(
                    new AgentToolResult<TodoWriteDetails>
                    {
                        Content = [new TextContent(sb.ToString())],
                        Details = new TodoWriteDetails { Action = "delete", Id = args.Id.Value },
                        IsError = !deleted,
                    }
                );
            }

            case "list":
            {
                // 列出任务，支持按状态过滤（snake_case 兼容）
                var filter = ParseStatus(args.Status);

                var items = store.List(filter);
                if (items.Count == 0)
                {
                    sb.Append("(无任务)");
                }
                else
                {
                    foreach (var item in items)
                    {
                        // 根据状态选择图标
                        var statusIcon = item.Status switch
                        {
                            TodoStatus.Pending => "⬜",
                            TodoStatus.InProgress => "🔄",
                            TodoStatus.Completed => "✅",
                            _ => "❌",
                        };
                        var blockedStr =
                            item.BlockedBy.Length > 0
                                ? $" ⛓{string.Join(",", item.BlockedBy)}"
                                : "";
                        var activeStr =
                            item.Status == TodoStatus.InProgress
                            && !string.IsNullOrEmpty(item.ActiveForm)
                                ? $" ({item.ActiveForm})"
                                : "";
                        sb.AppendLine(
                            $"{statusIcon} #{item.Id}: {item.Subject}{activeStr}{blockedStr}"
                        );
                    }
                }
                return Task.FromResult(
                    new AgentToolResult<TodoWriteDetails>
                    {
                        Content = [new TextContent(sb.ToString())],
                        Details = new TodoWriteDetails { Action = "list", Count = items.Count },
                    }
                );
            }

            case "clear":
            {
                // 清除所有任务
                store.Clear();
                return Task.FromResult(
                    new AgentToolResult<TodoWriteDetails>
                    {
                        Content = [new TextContent("✅ 所有任务已清除")],
                        Details = new TodoWriteDetails { Action = "clear" },
                    }
                );
            }

            default:
                return Task.FromResult(
                    new AgentToolResult<TodoWriteDetails>
                    {
                        Content =
                        [
                            new TextContent(
                                $"未知操作: {args.Action}。支持: create/update/delete/list/clear"
                            ),
                        ],
                        IsError = true,
                    }
                );
        }
    }
}

/// <summary>
/// 任务管理工具的参数。
/// </summary>
public sealed record TodoWriteParams
{
    [Description("操作: create / update / delete / list / clear")]
    public string Action { get; init; } = "list";

    [Description("任务 ID（update/delete 时必填）")]
    public int? Id { get; init; }

    [Description("任务标题（create/update 时使用）")]
    public string? Subject { get; init; }

    [Description("任务详细描述")]
    public string? Description { get; init; }

    [Description("任务状态: pending / in_progress / completed / deleted")]
    public string? Status { get; init; }

    [Description("负责人/所有者")]
    public string? Owner { get; init; }

    [Description("进行中时的显示标签，如 'writing tests'")]
    public string? ActiveForm { get; init; }

    [Description("添加依赖的任务 ID 列表")]
    public string[]? AddBlockedBy { get; init; }

    [Description("移除依赖的任务 ID 列表")]
    public string[]? RemoveBlockedBy { get; init; }
}

/// <summary>
/// 任务管理工具的返回详情。
/// </summary>
public sealed record TodoWriteDetails
{
    public string Action { get; init; } = "";
    public int Id { get; init; }
    public int Count { get; init; }
}

// ── 询问用户工具 ──

/// <summary>
/// 询问用户工具：向用户提出结构化问题（用于不清晰或多方案情况）。
/// TUI 会弹出交互面板，等待用户回复后返回答案。
/// </summary>
public sealed class AskUserTool(UserQuestionStore questionStore)
    : AgentTool<AskUserParams, AskUserDetails>
{
    public override string Name => "ask_user";
    public override string Label => "询问用户";
    public override string Description =>
        "向用户提出结构化问题（用于不清晰或多方案情况）。TUI 会弹出交互面板，等待用户回复后返回答案";

    public override ToolCategory Category => ToolCategory.Generic;
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入问题存储+阻塞等待用户：强制串行

    public override async Task<AgentToolResult<AskUserDetails>> ExecuteAsync(
        string toolCallId,
        AskUserParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AskUserDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 检查问题列表是否为空
        if (args.Questions is null || args.Questions.Length == 0)
        {
            return new AgentToolResult<AskUserDetails>
            {
                Content = [new TextContent("未提供问题列表")],
                IsError = true,
            };
        }

        // 验证：最多 4 个问题，每个问题 2-4 个选项
        if (args.Questions.Length > 4)
        {
            return new AgentToolResult<AskUserDetails>
            {
                Content = [new TextContent("最多 4 个问题")],
                IsError = true,
            };
        }

        foreach (var q in args.Questions)
        {
            if (q.Options is null || q.Options.Length < 2 || q.Options.Length > 4)
            {
                return new AgentToolResult<AskUserDetails>
                {
                    Content = [new TextContent($"问题「{q.Header}」需要 2-4 个选项")],
                    IsError = true,
                };
            }
        }

        // 在 store 中创建待回答问题（TUI 会检测到并弹出交互面板）
        var questionDefs = args
            .Questions.Select(q => new QuestionDef
            {
                Header = q.Header,
                Question = q.Question,
                Options = q.Options,
                MultiSelect = q.MultiSelect,
            })
            .ToList();

        var pending = questionStore.CreateQuestions(questionDefs);

        // 等待所有问题被回答
        var answerTasks = pending.Select(p => questionStore.WaitForAnswerAsync(p.Id, ct)).ToArray();
        try
        {
            await Task.WhenAll(answerTasks);
        }
        catch (OperationCanceledException)
        {
            return new AgentToolResult<AskUserDetails>
            {
                Content = [new TextContent("用户取消了回答")],
                IsError = true,
            };
        }

        // 构建答案摘要供 LLM 使用
        var sb = new StringBuilder();
        sb.AppendLine("用户回复:");
        for (var i = 0; i < pending.Count; i++)
        {
            var answers = answerTasks[i].Result;
            sb.AppendLine($"  [{pending[i].Header}] {pending[i].Question}");
            sb.AppendLine($"    → {string.Join(", ", answers)}");
        }

        return new AgentToolResult<AskUserDetails>
        {
            Content = [new TextContent(sb.ToString())],
            Details = new AskUserDetails
            {
                QuestionIds = pending.Select(p => p.Id).ToArray(),
                QuestionCount = pending.Count,
            },
        };
    }
}

/// <summary>
/// 询问用户工具的参数。
/// </summary>
public sealed record AskUserParams
{
    [Description("要向用户提出的问题列表（1-4 个）")]
    public AskQuestion[] Questions { get; init; } = [];
}

/// <summary>
/// 单个问题定义。
/// </summary>
public sealed record AskQuestion
{
    [Description("简短标签（最多 16 字符），如 'Auth method'")]
    public string Header { get; init; } = "";

    [Description("完整问题描述")]
    public string Question { get; init; } = "";

    [Description("选项列表（2-4 个）")]
    public string[] Options { get; init; } = [];

    [Description("是否允许多选")]
    public bool MultiSelect { get; init; }
}

/// <summary>
/// 询问用户工具的返回详情。
/// </summary>
public sealed record AskUserDetails
{
    public string[] QuestionIds { get; init; } = [];
    public int QuestionCount { get; init; }
}

// ── 获取待回复问题工具 ──

/// <summary>
/// 获取待回复问题工具：获取当前等待用户回复的问题列表。
/// 用于检查 ask_user 提出的问题是否已被回答。
/// </summary>
public sealed class GetPendingQuestionsTool(UserQuestionStore questionStore)
    : AgentTool<GetPendingQuestionsParams, GetPendingQuestionsDetails>
{
    public override string Name => "get_pending_questions";
    public override string Label => "获取待回复问题";
    public override string Description =>
        "获取当前等待用户回复的问题列表。用于检查 ask_user 提出的问题是否已被回答";

    public override ToolCategory Category => ToolCategory.Generic;

    public override Task<AgentToolResult<GetPendingQuestionsDetails>> ExecuteAsync(
        string toolCallId,
        GetPendingQuestionsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<GetPendingQuestionsDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var pending = questionStore.GetPending();

        if (pending.Count == 0)
        {
            return Task.FromResult(
                new AgentToolResult<GetPendingQuestionsDetails>
                {
                    Content = [new TextContent("当前没有待回复的问题")],
                    Details = new GetPendingQuestionsDetails { Count = 0 },
                }
            );
        }

        var sb = new StringBuilder();
        sb.AppendLine($"待回复问题 ({pending.Count} 个):");
        foreach (var q in pending)
        {
            sb.AppendLine($"  [{q.Header}] {q.Question}");
            sb.AppendLine($"    选项: {string.Join(", ", q.Options)}");
        }

        return Task.FromResult(
            new AgentToolResult<GetPendingQuestionsDetails>
            {
                Content = [new TextContent(sb.ToString())],
                Details = new GetPendingQuestionsDetails
                {
                    Count = pending.Count,
                    QuestionIds = pending.Select(q => q.Id).ToArray(),
                },
            }
        );
    }
}

/// <summary>
/// 获取待回复问题工具的参数（无参数）。
/// </summary>
public sealed record GetPendingQuestionsParams;

/// <summary>
/// 获取待回复问题工具的返回详情。
/// </summary>
public sealed record GetPendingQuestionsDetails
{
    public int Count { get; init; }
    public string[] QuestionIds { get; init; } = [];
}
