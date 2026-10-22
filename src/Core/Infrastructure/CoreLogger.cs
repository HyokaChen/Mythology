using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Core.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Core.Infrastructure;

/// <summary>
/// 核心日志记录器 — 使用 LoggerMessage 源生成器实现零分配日志。
/// 编译时生成 Log 调用，避免运行时字符串插值和装箱。
/// 使用 ILogger 非 null 参数（调用方传入 logger ?? NullLogger.Instance）。
/// </summary>
[ExcludeFromCodeCoverage]
internal static partial class CoreLogger
{
    // ── 日志方法由源生成器在编译时自动生成，无需手动实现 ──
    // ── AgentLoop 相关日志 ──

    /// <summary>记录 Agent 循环迭代开始。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent 循环开始，迭代 {Iteration}")]
    public static partial void LoopIteration(ILogger logger, int iteration);

    /// <summary>记录循环退出（自然收尾：模型未调用工具且无待处理消息）。</summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "循环退出 reason={Reason} iteration={Iteration} toolCalls={ToolCallCount} hadText={HadText}"
    )]
    public static partial void LoopExited(
        ILogger logger,
        string reason,
        int iteration,
        int toolCallCount,
        bool hadText
    );

    /// <summary>记录空回合（流无输出且无工具调用）。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "空回合（无输出/无工具调用），iteration={Iteration}"
    )]
    public static partial void LoopEmptyTurn(ILogger logger, int iteration);

    /// <summary>记录工具已执行但循环未继续的异常。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "工具调用未继续：{ToolCallCount} 个工具已执行但循环中断，iteration={Iteration}"
    )]
    public static partial void LoopToolCallsNotContinued(
        ILogger logger,
        int toolCallCount,
        int iteration
    );

    /// <summary>记录工具集热刷新（注册表版本变化导致 Conversation 重建）。</summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "工具集已热刷新（registry 版本 {Version}），Conversation 已重建"
    )]
    public static partial void LoopToolSetRefreshed(ILogger logger, int version);

    /// <summary>循环判定：未完成复核命中，注入提醒并继续一轮。</summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "循环判定：上一条回复未完成，注入提醒并继续（第 {Count} 次）"
    )]
    public static partial void LoopJudgeContinued(ILogger logger, int count);

    /// <summary>循环判定：VSA 漂移复核为误报，压制提醒。</summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "循环判定：漂移复核认定仍在轨道，跳过提醒（similarity={Similarity}）"
    )]
    public static partial void LoopJudgeDriftVetoed(ILogger logger, double similarity);

    /// <summary>循环判定：稳定性断路被复核推翻，本轮继续。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "循环判定：稳定性断路被复核推翻，继续本轮（p={Probability}）"
    )]
    public static partial void LoopJudgeStabilityOverridden(ILogger logger, double probability);

    /// <summary>记录断路器触发事件。</summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "断路器触发：工具={ToolName}，重复={RepeatCount}，原因={Reason}"
    )]
    public static partial void CircuitBreakerTriggered(
        ILogger logger,
        string toolName,
        int repeatCount,
        string reason
    );

    /// <summary>记录 LLM 调用瞬态失败进入有界重试（429/5xx/网络瞬断，且本轮尚未产出任何内容）。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "LLM 瞬时失败重试：attempt={Attempt} status={Status} delayMs={DelayMs:F0}"
    )]
    public static partial void LlmTurnTransientRetry(
        ILogger logger,
        int attempt,
        int? status,
        double delayMs
    );

    /// <summary>记录工具执行开始。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "工具执行：{ToolName}，调用ID={ToolCallId}")]
    public static partial void ToolExecutionStart(
        ILogger logger,
        string toolName,
        string toolCallId
    );

    /// <summary>记录工具执行失败。</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "工具执行失败：{ToolName}，错误={Error}")]
    public static partial void ToolExecutionFailed(ILogger logger, string toolName, string error);

    /// <summary>记录工具被 before hook 拦截阻止。</summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "工具被 before hook 阻止：{ToolName}，原因={Reason}"
    )]
    public static partial void ToolBlockedByHook(ILogger logger, string toolName, string reason);

    /// <summary>记录未找到指定工具。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "未找到工具：{ToolName}")]
    public static partial void ToolNotFound(ILogger logger, string toolName);

    /// <summary>记录 run 边界换页执行失败（换页是增强能力，失败不影响已完成的 run）。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "run 边界换页失败：reason={Reason}，错误={Error}"
    )]
    public static partial void ContextRollFlushFailed(ILogger logger, string reason, string error);

    /// <summary>记录稳定性监控触发。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "稳定性监控触发：{Reason}")]
    public static partial void StabilityMonitorTriggered(ILogger logger, string reason);

    // ── SqliteRetrievalStore 相关日志 ──

    /// <summary>记录 simple 中文分词扩展加载成功。</summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "中文分词扩展 simple 加载成功，FTS5 将使用 jieba 分词"
    )]
    public static partial void RetrievalSimpleTokenizerLoaded(ILogger logger);

    /// <summary>记录 simple 中文分词扩展不可用，回退到 unicode61。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "中文分词扩展 simple 不可用，FTS5 回退到 unicode61 tokenizer"
    )]
    public static partial void RetrievalSimpleTokenizerNotAvailable(ILogger logger);

    /// <summary>记录 vec0 表维度与当前配置不一致，DROP 重建。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "vec0 表 {TableName} 维度不匹配（表 {ExistingDim} ≠ 配置 {NewDim}），重建向量索引"
    )]
    public static partial void RetrievalVecDimensionMismatch(
        ILogger logger,
        string tableName,
        int existingDim,
        int newDim
    );

    // ── PluginLoader 相关日志 ──

    /// <summary>记录插件签名验证失败。</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "签名验证失败：{AssemblyPath}")]
    public static partial void PluginSignatureFailed(ILogger logger, string assemblyPath);

    /// <summary>记录插件加载失败。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "插件加载失败：{AssemblyPath}，错误={Error}"
    )]
    public static partial void PluginLoadFailed(ILogger logger, string assemblyPath, string error);

    /// <summary>记录插件签名验证异常。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "签名验证异常：{AssemblyPath}，错误={Error}")]
    public static partial void PluginSignatureException(
        ILogger logger,
        string assemblyPath,
        string error
    );

    /// <summary>记录插件无强名称签名。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "无强名称签名：{AssemblyPath}")]
    public static partial void PluginNoStrongName(ILogger logger, string assemblyPath);

    /// <summary>记录 plugin.json 与 DLL Attribute 权限声明不一致。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "插件 {PluginId} 权限声明不一致 — DLL Attribute: [{AttrPerms}], plugin.json: [{JsonPerms}]"
    )]
    public static partial void PluginManifestMismatch(
        ILogger logger,
        string pluginId,
        string attrPerms,
        string jsonPerms
    );

    /// <summary>记录共享依赖版本不兼容（主版本不一致，阻断加载）。</summary>
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "插件 {PluginId} 共享依赖 {AssemblyName} 版本不兼容：Host={HostVersion}, 插件编译={PluginVersion}，主版本不一致，加载已跳过"
    )]
    public static partial void PluginSharedDependencyVersionIncompatible(
        ILogger logger,
        string pluginId,
        string assemblyName,
        string hostVersion,
        string pluginVersion
    );

    /// <summary>记录共享依赖版本较新警告（插件编译版本 > Host 版本，运行时可能缺失 API）。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "插件 {PluginId} 共享依赖 {AssemblyName} 版本较新：Host={HostVersion}, 插件编译={PluginVersion}，运行时可能因缺少 API 而崩溃"
    )]
    public static partial void PluginSharedDependencyVersionNewer(
        ILogger logger,
        string pluginId,
        string assemblyName,
        string hostVersion,
        string pluginVersion
    );

    /// <summary>记录共享依赖版本较旧（信息级，接口二进制兼容通常安全）。</summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "插件 {PluginId} 共享依赖 {AssemblyName} 版本较旧：Host={HostVersion}, 插件编译={PluginVersion}"
    )]
    public static partial void PluginSharedDependencyVersionOlder(
        ILogger logger,
        string pluginId,
        string assemblyName,
        string hostVersion,
        string pluginVersion
    );

    /// <summary>记录插件信任级别判定结果（安全审计用）。</summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "插件 {PluginId} 信任级别: {TrustLevel}"
    )]
    public static partial void PluginTrustResolved(
        ILogger logger,
        string pluginId,
        PluginTrustLevel trustLevel
    );

    /// <summary>记录插件已卸载。</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "插件已卸载：{PluginId}")]
    public static partial void PluginUnloaded(ILogger logger, string pluginId);

    /// <summary>记录插件卸载失败。</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "插件卸载失败：{PluginId}，错误={Error}")]
    public static partial void PluginUnloadFailed(ILogger logger, string pluginId, string error);

    // ── Agent 相关日志 ──

    /// <summary>记录 Agent Prompt 开始。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent Prompt 开始")]
    public static partial void AgentPromptStart(ILogger logger);

    /// <summary>记录 Agent Prompt 完成。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent Prompt 完成，新增消息={MessageCount}")]
    public static partial void AgentPromptComplete(ILogger logger, int messageCount);

    /// <summary>记录回合停止决策结果。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ShouldStopAfterTurn={ShouldStop}，迭代={Iteration}"
    )]
    public static partial void ShouldStopAfterTurnResult(
        ILogger logger,
        bool shouldStop,
        int iteration
    );

    /// <summary>记录 Agent 已在运行中，拒绝新操作。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent 已在运行中，拒绝新操作")]
    public static partial void AgentAlreadyRunning(ILogger logger);

    /// <summary>记录工具调用参数 JSON 被修复。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "工具调用参数 JSON 修复：工具={ToolName}，成功={Success}，原始长度={OriginalLength}，修复后长度={RepairedLength}"
    )]
    public static partial void ToolCallArgsRepaired(
        ILogger logger,
        string toolName,
        bool success,
        int originalLength,
        int repairedLength
    );

    /// <summary>记录工具调用参数 JSON 解析失败（修复前）。</summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "工具调用参数 JSON 解析失败：工具={ToolName}，将尝试修复"
    )]
    public static partial void ToolCallArgsParseFailed(ILogger logger, string toolName);

    /// <summary>记录 GuardRail tripwire 触发（阻断循环）。</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "GuardRail {Direction} 触发：{Reason}")]
    public static partial void GuardRailTriggered(ILogger logger, string direction, string reason);

    /// <summary>记录 GuardRail 警告（不阻断循环）。</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "GuardRail {Direction} 警告：{Reason}")]
    public static partial void GuardRailWarning(ILogger logger, string direction, string reason);

    // ── Null-safe 访问辅助 ──
    // LoggerMessage 源生成器要求 ILogger 参数非 null。
    // 统一提供 NullLogger.Instance 作为默认值。

    /// <summary>
    /// 获取有效 Logger：如果为 null 则返回 NullLogger.Instance（零开销 no-op）。
    /// NullLogger 的所有方法都是空操作，IsEnabled 返回 false，因此
    /// LoggerMessage 生成的代码在 NullLogger 上不会执行任何格式化或分配。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ILogger Safe(this ILogger? logger) => logger ?? NullLogger.Instance;
}
