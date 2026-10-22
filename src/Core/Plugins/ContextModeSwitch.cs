using Core.Agent;
using Core.Data;
using Core.Tools;

namespace Core.Plugins;

/// <summary>
/// 运行期可切换上下文管理模式的插件契约（由 context-continuation 插件实现）。
///
/// 背景：接续工具是否注册由 <c>context_management.mode</c> 决定，而模式原先只在进程启动时读一次。
/// 宿主（TUI/Desktop 设置页）要在运行期切换，需要「当前选项」与「切换后应注册的工具」；
/// Core 不能反向引用插件程序集（插件引用 Core），因此用本接口做宿主 ↔ 插件的契约。
/// </summary>
public interface IContextModePlugin
{
    /// <summary>当前生效的上下文管理选项（compact 模式为 null）。</summary>
    ContextManagementOptions? CurrentOptions { get; }

    /// <summary>
    /// 应用新模式并<b>返回该模式下应注册的工具</b>（compact → 空列表）。
    /// 实现方负责把选项投递给 <see cref="Agent.ApplyContextManagement"/>；
    /// 工具注册表增删与搜索索引重建由 <see cref="ContextModeSwitcher"/> 完成。
    /// </summary>
    IReadOnlyList<AgentTool> ApplyMode(ContextManagementOptions? options);
}

/// <summary>
/// 接续插件的运行期引用：GroupName = PluginLoader 注册工具时使用的组名（= 插件 manifest.Name）。
/// </summary>
public readonly record struct ContextModePluginRef(string GroupName, IContextModePlugin Plugin);

/// <summary>
/// 上下文管理模式（compact | continuation）的运行期切换器：
/// 落盘 config.yaml → 插件应用选项 → 重注册该插件的工具 → 重建工具搜索索引 → 刷新 Agent 工具表。
///
/// 时序不变量：先落盘再改运行期状态——任何一步失败都可重启回落到已保存的模式。
/// </summary>
public sealed class ContextModeSwitcher(
    ToolRegistry registry,
    Core.Agent.Agent agent,
    ConfigStore configStore,
    ToolSearchEngine toolSearchEngine,
    ContextModePluginRef? plugin = null
)
{
    /// <summary>接续插件是否已加载（未加载时无法切到 continuation）。</summary>
    public bool IsPluginAvailable => plugin is not null;

    /// <summary>当前模式（以 config.yaml 为准）。</summary>
    public ContextMode CurrentMode => configStore.Load().ContextManagement.ToOptions().Mode;

    /// <summary>
    /// 从插件加载器里找出实现 <see cref="IContextModePlugin"/> 的插件（工具组名 = manifest.Name）。
    /// 找不到返回 null（插件未安装/被禁用）。
    /// </summary>
    public static ContextModePluginRef? Find(PluginLoader? loader) =>
        loader?.LoadedPlugins.FirstOrDefault(p => p.Plugin is IContextModePlugin) is { } loaded
            ? new ContextModePluginRef(loaded.Name, (IContextModePlugin)loaded.Plugin)
            : null;

    /// <summary>
    /// 只切模式，其余参数沿用 config.yaml 现值（/context 这类轻量入口用）。
    /// </summary>
    public Task<(bool Ok, string Message)> SwitchModeAsync(
        string mode,
        CancellationToken ct = default
    )
    {
        var current = configStore.Load().ContextManagement;
        return SwitchAsync(
            new ContextManagementConfig
            {
                Mode = mode,
                ReminderPercent = current.ReminderPercent,
                VectorRetrieval = current.VectorRetrieval,
            },
            ct
        );
    }

    /// <summary>
    /// 切换模式并立即生效。返回 (是否成功, 用户可读的结果说明)。
    /// <paramref name="config"/> 中的 mode 会被归一化（拼错 → compact，不静默切到接续）。
    /// </summary>
    public async Task<(bool Ok, string Message)> SwitchAsync(
        ContextManagementConfig config,
        CancellationToken ct = default
    )
    {
        var options = config.ToOptions();
        var continuation = options.Mode == ContextMode.Continuation;

        if (continuation && plugin is null)
            return (false, "接续插件未加载，无法切到接续模式。");

        // 1) 落盘（先落盘：运行期状态可由重启恢复；mode 已归一化，不把拼错写回配置）
        var data = configStore.Load();
        data.ContextManagement = new ContextManagementConfig
        {
            Mode = continuation ? "continuation" : "compact",
            ReminderPercent = options.ReminderPercent,
            VectorRetrieval = options.VectorRetrieval,
        };
        await configStore.SaveAsync(data);

        if (plugin is not { } target)
            return (true, "已切换到压缩模式（未加载接续插件，无需变更工具）。");

        // 2) 插件应用选项（compact → null：循环不注入页片段/提醒）
        var tools = target.Plugin.ApplyMode(continuation ? options : null);

        // 3) 重注册该插件的工具（组名 = manifest.Name，与 PluginLoader 一致）
        registry.UnregisterByPlugin(target.GroupName);
        if (tools.Count > 0)
            registry.RegisterRange(tools, target.GroupName);

        // 4) 刷新 Agent 工具表 + 工具搜索索引（索引决定 tool_search 能否发现新工具）
        agent.SetTools(registry.GetAll());
        toolSearchEngine.InvalidateIndex();
        string? indexWarning = null;
        try
        {
            await toolSearchEngine.BuildIndexAsync(registry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 索引重建失败不得回滚模式：ToolInjector/tool_search 会按需懒重建（IsIndexed=false 触发），
            // 而模式与工具族已经生效。此处只把原因回抛给 UI，便于用户诊断（缺分词词典等）。
            indexWarning = ex.Message;
        }

        return (
            true,
            indexWarning is null
                ? continuation
                    ? $"已切换到接续模式（{tools.Count} 个接续工具已注册）"
                    : "已切换到压缩模式（接续工具已卸载）"
                : $"已切换模式，但工具检索索引重建失败（下次检索时自动重试）：{indexWarning}"
        );
    }
}
