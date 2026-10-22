namespace Core.Stability;

/// <summary>
/// 稳定性状态枚举。由 IStabilityMonitor 返回，
/// 表示 Agent 循环当前是否应继续运行或需要干预。
/// </summary>
public enum StabilityStatus
{
    /// <summary>稳定状态，Agent 循环正常继续。</summary>
    Stable,

    /// <summary>警告状态，需要注意但尚未需要干预。</summary>
    Warning,

    /// <summary>不稳定状态，建议进行干预。</summary>
    Unstable,

    /// <summary>已触发断路（Tripped），需要停止或重置。</summary>
    Tripped,

    /// <summary>已冻结（Frozen），暂停执行。</summary>
    Frozen,
}
