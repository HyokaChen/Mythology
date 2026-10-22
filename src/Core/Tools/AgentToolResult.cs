using Core.Shared;

namespace Core.Tools;

/// <summary>
/// 工具执行结果基类。
/// 包含执行内容、详情、错误状态和终止标志。
/// </summary>
public record AgentToolResult
{
    /// <summary>执行结果的内容块列表（如文本、图片等）。</summary>
    public IReadOnlyList<ContentBlock> Content { get; init; } = [];

    /// <summary>执行结果的详情对象（可为任意类型）。</summary>
    public virtual object? Details { get; init; }

    /// <summary>是否发生错误。</summary>
    public bool IsError { get; init; }

    /// <summary>是否终止后续操作。</summary>
    public bool Terminate { get; init; }
}

/// <summary>
/// 带类型化 Details 的工具执行结果。
/// 唯一存储在 _details，override 基类 Details 暴露同一个值。
/// 基类引用和派生类引用读到的 Details 是同一个值（无装箱、无反射、无丢失）。
/// </summary>
/// <typeparam name="TDetails">详情对象的强类型。</typeparam>
public record AgentToolResult<TDetails> : AgentToolResult
{
    /// <summary>内部存储的强类型详情字段。</summary>
    private readonly TDetails? _details;

    /// <summary>
    /// 唯一存储。init 时写入 TDetails 值，通过 override 基类 Details 暴露。
    /// 基类引用和派生类引用读到的 Details 是同一个值（无装箱、无反射、无丢失）。
    /// </summary>
    public override object? Details
    {
        get => _details;
        init => _details = (TDetails?)value;
    }

    /// <summary>强类型访问 Details，等价于 (TDetails?)Details，但更简洁。</summary>
    public TDetails? TypedDetails => _details;
}
