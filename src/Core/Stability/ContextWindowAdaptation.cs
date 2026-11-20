using LlmTornado.Chat;

namespace Core.Stability;

/// <summary>
/// 溢出后的上下文窗口自校正。
///
/// 自定义 OpenAI-compatible 网关经常服务与 LlmTornado 元数据不一致的模型：
/// 元数据写 1M，实际只接受 128K。此时 profile 的 pre-flight 永远不触发，
/// 只能等 provider 抛出 overflow；压缩后重试仍会在同一上限再次溢出。
/// 用最近一次成功请求的 context-tokens 作为实测上限，把 profile 收紧到
/// 实测值附近，并降低输出预留，给输入留出安全余量。
/// </summary>
public static class ContextWindowAdaptation
{
    /// <summary>实测上限之上保留的少量余量（token）。</summary>
    private const long HeadroomTokens = 2_048;

    /// <summary>低于此值不认为是可信的窗口上限，避免异常小样本把窗口压得过低。</summary>
    private const long MinObservedTokens = 8_000;

    /// <summary>溢出恢复时输出预留的上限；仍可生成完整章节，但给输入留出空间。</summary>
    private const int RecoveryMaxOutputTokens = 16_384;

    /// <summary>
    /// 最近一次 API 调用留下的完整上下文 token 锚点（prompt + completion）。
    /// 没有锚点时返回 0。
    /// </summary>
    public static long LastObservedContextTokens(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Tokens is int tokens && tokens > 0)
                return tokens;
        }
        return 0;
    }

    /// <summary>
    /// 根据实测上下文收紧 profile；无法收紧时返回 null。
    /// </summary>
    public static ModelContextProfile? ReduceToObserved(
        ModelContextProfile profile,
        long observedTokens
    )
    {
        if (observedTokens < MinObservedTokens || observedTokens >= profile.ContextWindowTokens)
            return null;

        var ceiling = Math.Min(profile.ContextWindowTokens, observedTokens + HeadroomTokens);
        return profile with
        {
            ContextWindowTokens = ceiling,
            MaxOutputTokens = Math.Min(profile.MaxOutputTokens, RecoveryMaxOutputTokens),
        };
    }
}
