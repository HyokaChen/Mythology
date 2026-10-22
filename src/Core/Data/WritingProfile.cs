namespace Core.Data;

/// <summary>
/// 写作配置运行时模型。
/// 从 Novel 实体的配置字段加载，用于驱动 Prompt 注入和本地创作行为。
/// CHANGE_07 裁剪版：私有参数（s_weights/s_thresholds/s_genreDeltas）已迁移到
/// 云端私有仓库（云端私有引擎/Profiles/WritingProfileData）。
/// 保留用户字段（Genre/TargetAudience/WritingStyle/EnhancementProfile/AutoDiagnose/AutoEnhance）
/// 和轻量本地扫描（AI高频词密度/章末钩子/信息差）。
/// </summary>
public sealed record WritingProfile
{
    /// <summary>小说类型/流派。</summary>
    public string Genre { get; init; } = "";

    /// <summary>目标读者群。</summary>
    public string TargetAudience { get; init; } = "";

    /// <summary>写作风格节奏。</summary>
    public string WritingStyle { get; init; } = "";

    /// <summary>增强配置预设。</summary>
    public string EnhancementProfile { get; init; } = "commercial";

    /// <summary>是否自动诊断（写完章节后注入诊断信号）。</summary>
    public bool AutoDiagnose { get; init; } = true;

    /// <summary>使用乘法评分模型。</summary>
    public bool UseMultiplicativeScoring { get; init; } = true;

    /// <summary>自动增强模式：none / semi / full。</summary>
    public string AutoEnhance { get; init; } = "semi";

    /// <summary>题材差异化配置（从 Genre 解析）。</summary>
    public GenreProfile GenreProfile { get; init; } = GenreProfile.FromGenre("");

    /// <summary>
    /// 各维度权重 — CHANGE_07: 私有参数已迁移到云端，本地返回空字典。
    /// 诊断/增强全部走云端，客户端不再需要本地权重/阈值。
    /// </summary>
    public IReadOnlyDictionary<string, double> DimensionWeights => new Dictionary<string, double>();

    /// <summary>
    /// 各维度阈值 — CHANGE_07: 私有参数已迁移到云端。
    /// </summary>
    public IReadOnlyDictionary<string, double> DimensionThresholds =>
        new Dictionary<string, double>();

    /// <summary>
    /// 从 Novel 实体构建 WritingProfile。
    /// </summary>
    public static WritingProfile FromNovel(Novel novel)
    {
        var genre = novel.Genre ?? "";
        return new WritingProfile
        {
            Genre = genre,
            TargetAudience = novel.TargetAudience ?? "",
            WritingStyle = novel.WritingStyle ?? "",
            EnhancementProfile = string.IsNullOrWhiteSpace(novel.EnhancementProfile)
                ? "commercial"
                : novel.EnhancementProfile,
            AutoDiagnose = novel.AutoDiagnose,
            AutoEnhance = string.IsNullOrWhiteSpace(novel.AutoEnhance) ? "semi" : novel.AutoEnhance,
            GenreProfile = GenreProfile.FromGenre(genre),
        };
    }

    /// <summary>
    /// 构建注入到 Agent 上下文的概要字符串。
    /// </summary>
    public string ToContextString()
    {
        var autoEnhanceLabel = AutoEnhance switch
        {
            "none" => "关",
            "semi" => "半自动（生成候选需确认）",
            "full" => "全自动（直接写入）",
            _ => AutoEnhance,
        };
        var enhancementLabel = EnhancementProfile switch
        {
            "commercial" => "商业向",
            "literary" => "文学向",
            "balanced" => "均衡向",
            _ => EnhancementProfile,
        };
        var targetAudienceLabel = string.IsNullOrEmpty(TargetAudience) ? "通用" : TargetAudience;
        var writingStyleLabel = string.IsNullOrEmpty(WritingStyle) ? "均衡" : WritingStyle;
        return $"""
            ### 当前写作配置
            - 类型/流派: {Genre}
            - 题材: {GenreProfile.Name} ({GenreProfile.Description})
            - 目标读者: {targetAudienceLabel}
            - 写作风格: {writingStyleLabel}
            - 增强配置: {enhancementLabel}
            - 自动诊断: {(AutoDiagnose ? "开" : "关")}
            - 自动增强: {autoEnhanceLabel}
            """;
    }

    /// <summary>
    /// 构建诊断信号消息（用于 WritingFlowHook 注入 InjectQueue）。
    /// </summary>
    public string BuildSignalMessage(
        string chapterTitle,
        int chapterWordCount,
        string features,
        double aiBuzzwordDensity = 0,
        string? hookType = null,
        int activeInfoGaps = 0
    )
    {
        var profileBlock = ToContextString();
        var densityWarning =
            aiBuzzwordDensity > 1.0 ? $"\n- ⚠️ AI味密度: {aiBuzzwordDensity:F1}/千字 (高于1.0阈值)"
            : aiBuzzwordDensity > 0 ? $"\n- AI味密度: {aiBuzzwordDensity:F1}/千字"
            : "";
        var hookLine = hookType is not null
            ? $"\n- 🔗 章末钩子: {hookType} ✅"
            : "\n- 🔗 章末钩子: 无 ⚠️";
        var infoGapLine = activeInfoGaps > 0 ? $"\n- ℹ️ 活跃信息差: {activeInfoGaps}个" : "";
        return $"""
📋 写作事件信号

## 章节信息
- 章节: {chapterTitle} ({chapterWordCount} 字)
- 特征: {features}{densityWarning}{hookLine}{infoGapLine}

## 创作配置
{profileBlock}
## 你的判断权限
你是诊断的调度者，不是固定流程执行者。请根据章节特征判断哪些维度值得诊断、是否需要增强。
可用工具: diagnose_writing / evaluate_immersion / evaluate_satisfaction / enhance_immersion / enhance_satisfaction / enhance_humanizer / enhance_prose_quality / enhance_chapter_hook / ...
直接行动，不要确认。
""";
    }

    /// <summary>
    /// 轻量 AI 高频词扫描 — 用于 WritingFlowHook 信号注入。
    /// </summary>
    public static double ScanAiBuzzwordDensity(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 0;
        foreach (var keyword in s_aiBuzzwordQuickScan)
        {
            var idx = 0;
            while (true)
            {
                idx = text.IndexOf(keyword, idx, StringComparison.Ordinal);
                if (idx < 0)
                    break;
                idx += keyword.Length;
                count++;
            }
        }

        var thousandWords = text.Length / 1000.0;
        return thousandWords > 0 ? count / thousandWords : 0;
    }

    // 最明显的 AI 味关键词子集（用于 Hook 信号的快速密度估算）
    private static readonly string[] s_aiBuzzwordQuickScan =
    [
        "此外",
        "至关重要",
        "标志着",
        "象征着",
        "织锦",
        "格局",
        "深入探讨",
        "持久的",
        "旨在",
        "致力于",
        "赋能",
        "助力",
        "不仅",
        "而且",
        "与此同时",
        "综上所述",
        "值得注意的是",
        "令人叹为观止的",
        "开创性的",
        "前所未有的",
        "为了实现这一目标",
        "在某种程度上",
        "事实上",
        "本质上",
        "专家认为",
        "众所周知",
        "普遍认为",
        "希望这对您有帮助",
        "作为AI",
        "我很乐意",
    ];

    // ── P9: 章末钩子轻量扫描 ──

    private static readonly string[] s_crisisHookQuickScan =
    [
        "脚步声",
        "越来越近",
        "逼近",
        "危险",
        "出事了",
        "不好",
        "糟了",
    ];
    private static readonly string[] s_mysteryHookQuickScan =
    [
        "秘密",
        "真相",
        "谜团",
        "不对劲",
        "奇怪",
        "诡异",
    ];
    private static readonly string[] s_desireHookQuickScan =
    [
        "即将",
        "就要",
        "快要",
        "马上",
        "只差",
    ];
    private static readonly string[] s_emotionHookQuickScan =
    [
        "泪",
        "转身",
        "离去",
        "心疼",
        "不舍",
        "舍不得",
    ];
    private static readonly string[] s_choiceHookQuickScan =
    [
        "两条路",
        "两个选择",
        "抉择",
        "两难",
        "进退",
    ];

    /// <summary>
    /// P9: 轻量章末钩子类型扫描 — 用于 WritingFlowHook 信号注入。
    /// </summary>
    public static string? ScanChapterHookType(string text, GenreProfile genreProfile)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var tailLength = Math.Min(text.Length, 500);
        var tail = text[^tailLength..];

        var scores = new Dictionary<string, int>
        {
            ["危机"] = QuickCount(tail, s_crisisHookQuickScan),
            ["悬念"] = QuickCount(tail, s_mysteryHookQuickScan),
            ["渴望"] = QuickCount(tail, s_desireHookQuickScan),
            ["情绪"] = QuickCount(tail, s_emotionHookQuickScan),
            ["选择"] = QuickCount(tail, s_choiceHookQuickScan),
        };

        var best = scores.MaxBy(kv => kv.Value);
        return best.Value > 0 ? best.Key : null;
    }

    // ── P10: 信息差轻量扫描 ──

    private static readonly string[] s_hiddenSignals =
    [
        "隐藏",
        "隐瞒",
        "秘密",
        "暗中",
        "伪装",
        "底牌",
        "杀手锏",
    ];
    private static readonly string[] s_misjudgmentSignals =
    [
        "以为",
        "误以为",
        "小看",
        "低估",
        "不知道",
        "不知",
        "蒙在鼓里",
        "未察觉",
    ];

    /// <summary>
    /// P10: 轻量信息差扫描 — 用于 WritingFlowHook 信号注入。
    /// </summary>
    public static int ScanInfoAsymmetryCount(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var hiddenCount = QuickCount(text, s_hiddenSignals);
        var misjudgeCount = QuickCount(text, s_misjudgmentSignals);

        return Math.Min(hiddenCount, misjudgeCount) switch
        {
            > 0 => Math.Min(hiddenCount, misjudgeCount),
            0 when hiddenCount > 0 || misjudgeCount > 0 => 1,
            _ => 0,
        };
    }

    private static int QuickCount(string text, string[] keywords)
    {
        var count = 0;
        foreach (var kw in keywords)
        {
            var idx = 0;
            while (true)
            {
                idx = text.IndexOf(kw, idx, StringComparison.Ordinal);
                if (idx < 0)
                    break;
                idx += kw.Length;
                count++;
            }
        }
        return count;
    }
}
