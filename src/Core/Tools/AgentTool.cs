using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Core.Tools;

/// <summary>工具执行模式：并行或顺序执行。</summary>
public enum ToolExecutionMode
{
    /// <summary>并行执行模式，多个工具可同时执行。</summary>
    Parallel,

    /// <summary>顺序执行模式，工具按顺序逐个执行。</summary>
    Sequential,
}

/// <summary>
/// 工具抽象基类（无泛型版本，用于注册表查找）。
/// 所有具体工具必须继承此类。
/// </summary>
public abstract class AgentTool
{
    /// <summary>工具的唯一标识名称。</summary>
    public abstract string Name { get; }

    /// <summary>工具的显示标签（友好名称）。</summary>
    public abstract string Label { get; }

    /// <summary>工具的功能描述。</summary>
    public abstract string Description { get; }

    /// <summary>工具的 JSON Schema 参数定义。</summary>
    public abstract JsonElement? Parameters { get; }

    /// <summary>工具的执行模式，默认为并行。</summary>
    public virtual ToolExecutionMode ExecutionMode => ToolExecutionMode.Parallel;

    /// <summary>工具所属分类，默认为通用。</summary>
    public virtual ToolCategory Category => ToolCategory.Generic;

    /// <summary>关联的实体类型（如 Character、Location 等）。</summary>
    public virtual string? EntityType => null;

    /// <summary>
    /// 工具别名/同义词，用于检索召回。
    /// 中文工具需提供中文别名（如"角色完整性""人物设定检查"），
    /// 以弥补英文工具名在中文 query 下的召回不足。
    /// </summary>
    public virtual string[] Aliases => [];

    /// <summary>
    /// 调用示例查询，用于检索索引增强语义召回。
    /// 如"检查主角林晚的设定是否完整"。
    /// </summary>
    public virtual string[] ExampleQueries => [];

    /// <summary>
    /// 是否延迟加载（不预注入 LLM 上下文，仅通过 tool_search 按需发现）。
    /// true 时工具不进入 BuildChatRequest 的默认工具列表，
    /// 只在 ToolSearchTool 命中后由 ToolInjector 展开。
    /// </summary>
    public virtual bool DeferLoading => false;

    /// <summary>
    /// 如果取消令牌已请求取消，则抛出 OperationCanceledException。
    /// 子类应在每个关键 await 点前后调用此方法，确保工具执行能快速响应中断。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static void ThrowIfCancelled(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 执行工具的核心方法。
    /// </summary>
    /// <param name="toolCallId">工具调用 ID。</param>
    /// <param name="args">调用参数（JSON 格式）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="onUpdate">进度报告回调（可选）。</param>
    /// <returns>工具执行结果。</returns>
    public abstract Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        IProgress<AgentToolResult>? onUpdate = null
    );
}

/// <summary>
/// 工具抽象基类（泛型版本，类型安全）。
/// 提供强类型的参数和结果支持。
/// </summary>
/// <typeparam name="TParams">参数类型，必须是可实例化的类。</typeparam>
/// <typeparam name="TDetails">结果详情类型。</typeparam>
public abstract class AgentTool<TParams, TDetails> : AgentTool
    where TParams : class, new()
{
    /// <summary>自动生成参数的 JSON Schema。</summary>
    public override JsonElement? Parameters { get; } = JsonSchemaHelper.GenerateSchema<TParams>();

    /// <summary>
    /// 执行工具的核心方法（密封重写，自动处理参数反序列化）。
    /// </summary>
    public sealed override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        IProgress<AgentToolResult>? onUpdate = null
    )
    {
        // 反序列化参数为强类型对象
        var parsedArgs = JsonSerializer.Deserialize<TParams>(
            args.GetRawText(),
            JsonSchemaHelper.JsonOptions
        );
        if (parsedArgs is null)
            throw new ArgumentException($"无法反序列化参数到 {typeof(TParams).Name}");

        // 创建适配器：将泛型 progress 转换为非泛型 progress
        using var progressAdapter = onUpdate is not null
            ? new ProgressAdapter<TDetails>(onUpdate)
            : null;

        var result = await ExecuteAsync(toolCallId, parsedArgs, ct, progressAdapter);
        return result;
    }

    /// <summary>
    /// 执行工具的核心方法（泛型版本，由子类实现）。
    /// </summary>
    /// <param name="toolCallId">工具调用 ID。</param>
    /// <param name="args">强类型参数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="onUpdate">泛型进度报告回调（可选）。</param>
    /// <returns>工具执行结果。</returns>
    public abstract Task<AgentToolResult<TDetails>> ExecuteAsync(
        string toolCallId,
        TParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<TDetails>>? onUpdate = null
    );
}

/// <summary>
/// 进度适配器：将 IProgress&lt;AgentToolResult&lt;TDetails&gt;&gt; 适配为 IProgress&lt;AgentToolResult&gt;。
/// 用于在泛型和非泛型进度报告之间进行转换。
/// </summary>
/// <typeparam name="TDetails">结果详情类型。</typeparam>
internal sealed class ProgressAdapter<TDetails>(IProgress<AgentToolResult> inner)
    : IDisposable,
        IProgress<AgentToolResult<TDetails>>
{
    /// <summary>报告进度，将泛型结果转发给内部非泛型进度报告器。</summary>
    public void Report(AgentToolResult<TDetails> value)
    {
        inner.Report(value);
    }

    /// <summary>释放资源（无实际资源需要释放）。</summary>
    public void Dispose() { }
}
