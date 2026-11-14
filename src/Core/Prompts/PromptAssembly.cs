namespace Core.Prompts;

/// <summary>
/// Prompt 骨架装配器。取代 ConversationLoop 中的静态字符串拼接。
/// 支持按 Key 排除段（逐段回退 / AB 对比），并提供字符量估算用于大小监控。
/// 冷路径：Build 仅在 ConversationLoop 静态初始化时调用一次。
/// </summary>
public static class PromptAssembly
{
    private const string WritingStyleTag = "writing_style";

    /// <summary>骨架段（Key, Tag, Content）有序集合。Key 为段标识，Tag 为装配时的 XML 包裹标签。</summary>
    private static readonly (string Key, string Tag, string Content)[] s_segments =
    [
        ("role", "role_definition", RoleDefinitionPrompt.Full),
        ("safety", "safety_policy", SafetyPolicyPrompt.Full),
        ("conversation", "conversation_style", ConversationStylePrompt.Full),
        ("zones", "output_contract", OutputZonesPrompt.Full),
        ("tooling", "tool_calling", ToolCallingPrompt.Full),
        ("context", "context_understanding", ContextUnderstandingPrompt.Full),
        ("planning", "planning_loop", PlanningLoopPrompt.Full),
        ("errors", "error_handling", ErrorHandlingPrompt.Full),
        ("writing", "writing_process", WritingProcessPrompt.Full),
        ("humanizer", WritingStyleTag, HumanizerPrompt.Full),
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
    /// 上下文管理是否为接续模式。只影响 writing 段的第 9 条：compact 由宿主自动处理；
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
                {
                    var content =
                        continuation && s.Key == WritingSegmentKey
                            ? WritingProcessPrompt.ForContinuation
                            : s.Content;
                    return $"<{s.Tag}>\n{content}\n</{s.Tag}>";
                })
        );
    }

    /// <summary>writing 段的 Key（模式相关内容的定位锚点）。</summary>
    private const string WritingSegmentKey = "writing";

    /// <summary>
    /// 工具内部独立调用 LLM 时的系统提示：用户/小说上下文 + 写作风格段。
    /// 增强（EnhancementEngine）和探索（ExploreTool）不经过主对话骨架，
    /// 必须显式带上 HumanizerPrompt，否则云端审查不可用时本地改写会失去去 AI 味约束。
    /// </summary>
    public static string BuildAuxiliaryWritingSystemPrompt(string? systemPrompt)
    {
        var writingStyle = $"<{WritingStyleTag}>\n{HumanizerPrompt.Full}\n</{WritingStyleTag}>";
        return string.IsNullOrWhiteSpace(systemPrompt)
            ? writingStyle
            : string.Concat(systemPrompt, "\n\n", writingStyle);
    }

    /// <summary>
    /// 骨架字符量估算（汉字≈1 token，用于粗略上限监控，非精确计费）。
    /// 统计实际装配结果（含段标签与分隔符），避免低估。
    /// </summary>
    public static int EstimateTokenCount() => Build(null).Length;
}
