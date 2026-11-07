using Core.Interactions;
using Core.Plugins;
using Core.Shared;
using Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Plugins.FileSystem.Tools;

// 权限声明 — 编译进 DLL，受强名称签名保护
[assembly: PluginPermission(PluginPermissions.FileSystem)]
[assembly: PluginPermission(PluginPermissions.Process)]
[assembly: PluginPermission(PluginPermissions.UserInteract)]

namespace Plugins.FileSystem;

/// <summary>
/// 文件系统插件：提供 read、write、edit、bash、todo_write、ask_user 等核心工具。
/// 参考 pi agent 的 read/write/edit/bash 设计，适配 Mythology 的 AgentTool 契约。
/// </summary>
public sealed class FileSystemPlugin : IToolPlugin
{
    // 任务存储实例，通过 DI 共享给 TUI 和 Agent
    private TodoStore _todoStore = null!;

    // 用户问题存储实例（兼容读取；生产交互状态在 InteractionHub）
    private UserQuestionStore _questionStore = null!;

    // Core 交互唯一状态机
    private InteractionHub _interactionHub = null!;

    /// <summary>
    /// 初始化插件，从 DI 容器中获取共享 store/hub 单例。
    /// </summary>
    public void Initialize(IServiceProvider sp)
    {
        // 使用 DI 单例确保 TUI/Desktop 与 Agent 共享同一个 Core 状态机
        _todoStore = sp.GetService<TodoStore>() ?? new TodoStore();
        _questionStore = sp.GetService<UserQuestionStore>() ?? new UserQuestionStore();
        _interactionHub = sp.GetRequiredService<InteractionHub>();
    }

    /// <summary>
    /// 获取插件提供的所有工具列表。
    /// </summary>
    public IEnumerable<AgentTool> GetTools()
    {
        yield return new ReadFileTool();
        yield return new WriteFileTool();
        yield return new EditFileTool();
        yield return new TodoWriteTool(_todoStore);
        yield return new AskUserTool(_interactionHub);
        yield return new GetPendingQuestionsTool(_interactionHub);
    }

    /// <summary>供 TUI 获取共享的 TodoStore 实例。</summary>
    public TodoStore GetTodoStore() => _todoStore;

    /// <summary>供 UI 获取共享的 InteractionHub 实例。</summary>
    public InteractionHub GetInteractionHub() => _interactionHub;

    /// <summary>兼容读取：供尚未迁移的旧调用方获取问题视图。</summary>
    public UserQuestionStore GetQuestionStore() => _questionStore;
}
