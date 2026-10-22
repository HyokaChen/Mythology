namespace Core.Agent;

/// <summary>上下文管理策略。同一时刻只走一条主路径。</summary>
public enum ContextMode
{
    /// <summary>分片压缩（现状默认）。</summary>
    Compact = 0,

    /// <summary>接续换页（页 + 历史检索 + 跨页笔记）。</summary>
    Continuation = 1,
}

/// <summary>当前上下文页状态（首页 w0 为隐式页）。</summary>
public sealed record ContextWindowState(
    string WindowId,
    string? PrevWindowId,
    string FirstWindowId,
    int StartOrder,
    /// <summary>
    /// 上一页正在进行的任务陈述（该页最后一条用户消息，截断）。
    /// 换页不携带历史，但**必须**让模型知道自己在做什么：没有它，新页只有「空片段 + 下一条用户消息」，
    /// 用户在跨页续做时说一句「继续」模型就无从下手。null = 无锚点（首页 / 会话恢复 / 读取失败）。
    /// </summary>
    string? TaskAnchor = null,
    /// <summary>
    /// 本页是如何被切出来的：threshold（接近上限，系统换页）| overflow（溢出恢复）| model（模型主动）。
    /// 片段据此告诉模型「这是被迫切断还是计划内切换」——被迫切断时任务很可能没做完，需要主动接续。
    /// </summary>
    string? RollReason = null,
    /// <summary>
    /// 模型自写的交接说明（`new_context(note: …)`）：做到哪 / 下一步 / 未完成项。
    /// 新页片段把它放在锚点前面——它比裸锚点更准，且不用模型再翻一遍历史。
    /// </summary>
    string? HandoffNote = null
)
{
    /// <summary>隐式首页。</summary>
    public static ContextWindowState First { get; } = new("w0", null, "w0", 0);
}

/// <summary>
/// 上下文管理运行期选项（Host 从 config.yaml 装配）。
///
/// 阈值一律是「usage 比例」而不是绝对 token 数：同一份配置要能同时服务 8K 与 1M 窗口的模型
/// （绝对阈值在 8K 上永远不可达、在 1M 上一开页就触发）。默认值全部由
/// <c>ModelContextProfile</c> 按模型窗口推导，配置只做可选覆盖。
/// </summary>
public sealed record ContextManagementOptions(
    ContextMode Mode,
    /// <summary>建议换页的 usage 百分比覆盖（0 = 用 ModelContextProfile.RollAdvicePercent）。</summary>
    int ReminderPercent,
    bool VectorRetrieval
);
