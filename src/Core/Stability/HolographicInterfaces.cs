namespace Core.Stability;

/// <summary>
/// VSA 全息引擎接口 — 注册不变量、检测上下文漂移。
/// Core 层定义此抽象，StabilityHarness 项目提供具体实现（HolographicEngine）。
/// </summary>
public interface IHolographicEngine : IDisposable
{
    /// <summary>向量维度。</summary>
    int Dimensionality { get; }

    /// <summary>已注册的不变量数量。</summary>
    int InvariantCount { get; }

    /// <summary>将文本编码为 VSA 向量。</summary>
    sbyte[] EncodeText(string text);

    /// <summary>注册不变量（如系统提示词、小说类型）。</summary>
    /// <param name="name">不变量名称。</param>
    /// <param name="key">键向量。</param>
    /// <param name="value">值向量。</param>
    void RegisterInvariant(string name, ReadOnlySpan<sbyte> key, ReadOnlySpan<sbyte> value);

    /// <summary>检查指定不变量的漂移度（cosine similarity 越低 = 漂移越严重）。</summary>
    /// <returns>cosine similarity，范围 [-1, 1]；-1 表示不存在。</returns>
    double CheckDrift(string name, ReadOnlySpan<sbyte> context);

    /// <summary>检查所有已注册不变量的漂移度。</summary>
    IReadOnlyDictionary<string, double> CheckAllDrift(ReadOnlySpan<sbyte> context);

    /// <summary>列出所有已注册的不变量名称。</summary>
    string[] ListInvariants();

    /// <summary>
    /// 漂移检测的有效阈值（cosine similarity）。
    /// 低于此值表示当前上下文与不变量正交（漂移）。
    /// 由 VSAConfig.ApplyProfile 或 ModelContextProfile 计算。
    /// </summary>
    double EffectiveDriftThreshold { get; }

    /// <summary>
    /// �/漂移检查的有效频率：每 N 轮检查一次。
    /// </summary>
    int EffectiveDriftCheckInterval { get; }

    /// <summary>
    /// 漂移检测的有效上下文采样窗口：取最近 N 条 assistant 消息。
    /// </summary>
    int EffectiveDriftContextWindow { get; }

    /// <summary>
    /// 应用 ModelContextProfile 到漂移检测的动态阈值。
    /// 在模型切换时调用，使 DriftThreshold/DriftCheckInterval/DriftContextWindow
    /// 随上下文窗口和 thinking 级别自适应。
    /// </summary>
    void ApplyProfile(ModelContextProfile profile);
}

/// <summary>
/// 消息评分器接口 — 评估单条消息相对于上下文的信息价值。
/// 用于智能压缩时筛选信息密度最高的轮次。
/// Core 层定义此抽象，StabilityHarness 项目提供具体实现（RGDecimator）。
/// </summary>
public interface IMessageScorer
{
    /// <summary>评分单条消息相对于上下文的信息价值（0~1）。</summary>
    double Score(ReadOnlySpan<char> message, ReadOnlySpan<char> context);

    /// <summary>
    /// 应用 ModelContextProfile 到消息评分的动态阈值。
    /// 在模型切换时调用，使 MaxRetained/Threshold 随上下文窗口自适应。
    /// </summary>
    void ApplyProfile(ModelContextProfile profile);
}
