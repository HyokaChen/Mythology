namespace Core.Prompts;

/// <summary>
/// Prompt 骨架装配器。取代 ConversationLoop 中的静态字符串拼接。
/// 支持按 Key 排除段（逐段回退 / AB 对比），并提供字符量估算用于大小监控。
/// 冷路径：Build 仅在 ConversationLoop 静态初始化时调用一次。
/// </summary>
public static class PromptAssembly
{
    /// <summary>骨架段（Key, Content）有序集合。Key 为段标识，Content 为 Prompt 全文。</summary>
    private static readonly (string Key, string Content)[] s_segments =
    [
        ("role", RoleDefinitionPrompt.Full),
        ("conversation", ConversationStylePrompt.Full),
        ("tooling", ToolCallingPrompt.Full),
        ("context", ContextUnderstandingPrompt.Full),
        ("planning", PlanningLoopPrompt.Full),
        ("errors", ErrorHandlingPrompt.Full),
        ("safety", SafetyPolicyPrompt.Full),
        ("writing", WritingProcessPrompt.Full),
        ("humanizer", HumanizerPrompt.Full),
    ];

    /// <summary>全部段 Key（有序），用于测试与文档。</summary>
    public static IReadOnlyList<string> SegmentKeys { get; } = [.. s_segments.Select(s => s.Key)];

    /// <summary>
    /// 按默认顺序拼接全部骨架段。
    /// </summary>
    public static string Build() => Build(excludeKeys: null);

    /// <summary>
    /// 拼接骨架，可排除指定 Key 的段（调试 / 灰度回退）。
    /// </summary>
    /// <param name="excludeKeys">要排除的段 Key 集合；null 表示不排除。</param>
    /// <param name="continuation">
    /// 上下文管理是否为接续模式。只影响 writing 段的第 16 条：compact 让模型建议 /compact，
    /// 接续让模型直接调用 new_context（两模式互斥，prompt 不得交叉）。
    /// </param>
    public static string Build(IEnumerable<string>? excludeKeys, bool continuation = false)
    {
        var excluded = excludeKeys?.ToHashSet(StringComparer.Ordinal);
        return string.Join(
            "\n\n",
            s_segments
                .Where(s => excluded is null || !excluded.Contains(s.Key))
                .Select(s =>
                    continuation && s.Key == WritingSegmentKey
                        ? WritingProcessPrompt.ForContinuation
                        : s.Content
                )
        );
    }

    /// <summary>writing 段的 Key（模式相关内容的定位锚点）。</summary>
    private const string WritingSegmentKey = "writing";

    /// <summary>
    /// 骨架字符量估算（汉字≈1 token，用于粗略上限监控，非精确计费）。
    /// </summary>
    public static int EstimateTokenCount() => s_segments.Sum(s => s.Content.Length);
}
