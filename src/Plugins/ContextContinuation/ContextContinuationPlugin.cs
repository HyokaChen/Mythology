using Core.Agent;
using Core.Data;
using Core.Plugins;
using Core.Tools;
using Core.Vector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 权限声明 — 编译进 DLL，受强名称签名保护
// session:read/write = 会话转录（history_* 读取被封页消息、换页时写 window 条目）
// vector:read/write  = 可选语义检索通道（vector_retrieval=true 时封页建索引 + 检索）
[assembly: PluginPermission(PluginPermissions.SessionRead)]
[assembly: PluginPermission(PluginPermissions.SessionWrite)]
[assembly: PluginPermission(PluginPermissions.VectorRead)]
[assembly: PluginPermission(PluginPermissions.VectorWrite)]

namespace Plugins.ContextContinuation;

/// <summary>
/// 上下文接续插件（Codex experimental context management 的本地实现）：
/// <list type="bullet">
/// <item><c>new_context</c> —— 模型自主换页（不生成摘要）</item>
/// <item><c>history_*</c> —— 跨页检索被封存的消息与工具输出（字面 + 可选语义）</item>
/// </list>
///
/// 门禁：只有 <c>context_management.mode = continuation</c> 才提供工具并投递运行期选项；
/// compact 模式（默认）下本插件不注册任何工具，注册表与行为与引入接续前完全一致。
///
/// 机制对用户隐藏（Codex 同款）：工具的 UI 渲染过滤由 Core 的 <see cref="HiddenTools"/> 承担，
/// 因此日志是唯一可查通道——插件加载时打印模式与工具数。
/// </summary>
public sealed class ContextContinuationPlugin : IToolPlugin, IContextModePlugin
{
    private Agent _agent = null!;
    private FileSessionStore _sessions = null!;
    private RetrievalKernel? _retrievalKernel;
    private ContextManagementOptions? _options;

    /// <summary>当前生效的选项（compact 模式为 null）。</summary>
    public ContextManagementOptions? CurrentOptions => _options;

    /// <summary>
    /// 应用模式并返回该模式下应注册的工具（compact → 空）。运行期切换由
    /// <see cref="ContextModeSwitcher"/> 调用；启动装配与运行期切换走同一实现，避免两条路径漂移。
    /// </summary>
    public IReadOnlyList<AgentTool> ApplyMode(ContextManagementOptions? options)
    {
        _options = options;
        _agent.ApplyContextManagement(options);
        return options is null ? [] : [.. GetTools()];
    }

    public void Initialize(IServiceProvider sp)
    {
        _sessions = sp.GetRequiredService<FileSessionStore>();
        _agent = sp.GetRequiredService<Agent>();
        _retrievalKernel = sp.GetService<RetrievalKernel>();

        var options = sp.GetRequiredService<ConfigStore>().Load().ContextManagement.ToOptions();
        ApplyMode(options.Mode == ContextMode.Continuation ? options : null);

        sp.GetService<ILogger<ContextContinuationPlugin>>()
            ?.LogInformation(
                "上下文接续: mode={Mode}, 工具 {Count} 个, 提醒阈值 {ReminderPercent}%（0=按模型窗口推导）, 向量检索 {Vector}",
                options.Mode,
                _options is null ? 0 : ToolNames.Length,
                options.ReminderPercent,
                options.VectorRetrieval
            );
    }

    public IEnumerable<AgentTool> GetTools()
    {
        if (_options is null)
            yield break; // compact 模式：不注册任何工具

        yield return new NewContextTool(_agent);
        yield return new HistoryListWindowsTool(_agent, _sessions);
        yield return new HistoryListItemsTool(_agent, _sessions);
        yield return new HistoryReadItemTool(_agent, _sessions);
        yield return new HistorySearchContentsTool(_agent, _sessions, _retrievalKernel);
    }

    /// <summary>
    /// 工具名（顺序固定）。与 Core 的 <see cref="HiddenTools.Names"/> 一一对应——
    /// 新增工具必须同时进隐藏名单，否则会被渲染给用户。
    /// </summary>
    public static readonly string[] ToolNames =
    [
        "new_context",
        "history_list_windows",
        "history_list_items",
        "history_read_item",
        "history_search_contents",
    ];
}
