using System.Text.Json;
using Core.Cloud;

namespace CloudDiagnosis;

/// <summary>本地情节线事实（HARNESS-CLOUD §4.2 A-3/A-4 输入；Q2 定案同名映射）。</summary>
/// <param name="Name">情节线名称。</param>
/// <param name="PlannedRevealOrder">计划揭示章的 Order（无法解析时为 null）。</param>
/// <param name="FirstAppearOrder">伏笔名在章节正文首次出现的章 Order（§6.3 埋设章龄锚点；
/// 无计划揭示章时用它判定「埋设超龄」，null = 未在正文出现/未扫描）。</param>
public sealed record PlotlineFact(
    string Name,
    int? PlannedRevealOrder,
    int? FirstAppearOrder = null
);

/// <summary>
/// 本地事实输入 — 由调用方从 INovelStore/WritingProfile 组装，
/// 供纯本地可判定规则（A-3/A-4/A-5）消费。全部为廉价启发式数据。
/// </summary>
public sealed record LocalFacts
{
    /// <summary>主角名（空 = 未知，跳过 A-1 名字比较）。</summary>
    public string? ProtagonistName { get; init; }

    /// <summary>当前写入章节 Order。</summary>
    public int CurrentOrder { get; init; }

    /// <summary>全书最大章 Order（overdue 判定锚点）。</summary>
    public int MaxChapterOrder { get; init; }

    /// <summary>未解决情节线（ResolvedAtChapterId == null）。</summary>
    public IReadOnlyList<PlotlineFact> OpenPlotlines { get; init; } = [];

    /// <summary>本章信息差计数（WritingProfile.ScanInfoAsymmetryCount）。</summary>
    public int ActiveInfoGaps { get; init; }

    /// <summary>
    /// 叙事熵差值近似（§6.2：两次快照 narrativeEntropy 差，纯减法；无前值 null）。
    /// A-4 用它区分「埋新线」（熵升）与「宜收线/养」（熵降）。
    /// </summary>
    public double? EntropyDelta { get; init; }
}

/// <summary>统一问题形状 — context.retrieve 的 signals[] 与 dimensions.*.issues[] 同构。</summary>
public sealed record CloudSignalItem(
    string Dimension,
    string Severity,
    string Description,
    string? Suggestion,
    double ImpactScore,
    double Confidence = 1.0,
    string? SuggestedAction = null
);

/// <summary>fiction.context.retrieve 解析产物（基础档；Q3 定案契约）。</summary>
public sealed record ContextRetrieveOutcome(
    double OverallScore,
    IReadOnlyList<CloudSignalItem> Signals,
    IReadOnlyDictionary<string, double> DimensionScores,
    /// <summary>跨章趋势透传（HARNESS-CLOUD §15.1：features.trends 信息性摘要）。</summary>
    IReadOnlyList<string>? Trends = null,
    /// <summary>云端缺席角色（Q13：features.absentCharacters，最近 K 章未出场但全书出现过的角色；
    /// 灵感方向①候选的优先数据源，缺失时客户端回退本地扫描）。</summary>
    IReadOnlyList<string>? AbsentCharacters = null,
    /// <summary>云端捎带的素材提示（material-hints；仅请求开启时出现）。</summary>
    MaterialHintsOutcome? MaterialHints = null
);

/// <summary>云端 context.retrieve 的 material-hints 解析产物（prompt-block 已可直接注入）。</summary>
public sealed record MaterialHintsOutcome(string Status, string PromptBlock);

/// <summary>fiction.story.check 的 network.characterPageRank 解析产物（深度档；单章可为 null）。</summary>
public sealed record NetworkPageRank(
    string? TopCharacter,
    bool N1ProtagonistEclipsed,
    bool N2NetworkConcentrated
);

/// <summary>story.check 顶层 rule-findings[] 单条（规则引擎 findings；dimension 为规则维度标签）。</summary>
public sealed record StoryRuleFinding(
    string Dimension,
    string Severity,
    string Description,
    string? Evidence,
    string? Suggestion,
    double ImpactScore
);

/// <summary>story.check 顶层 continuity-audit.findings[] 单条（回溯连续性审计）。</summary>
public sealed record ContinuityFindingView(
    string Kind,
    string Rule,
    string Severity,
    IReadOnlyList<string> Chapters,
    string Description,
    string? Evidence,
    string? Suggestion,
    double ImpactScore
);

/// <summary>story.check 顶层 continuity-audit 视图（findings + 候选修订章）。</summary>
public sealed record ContinuityAuditView(
    IReadOnlyList<ContinuityFindingView> Findings,
    IReadOnlyList<string> RevisionChapters
);

/// <summary>story.check 顶层 plan-conformance 视图（计划为空/正文空 → 键缺席 → null）。</summary>
public sealed record PlanConformanceView(int PlannedBeatCount, int MissedBeatCount);

/// <summary>fiction.story.check 解析产物（23 键 dimensions 的弱类型导航句柄 + 顶层 review 段）。</summary>
public sealed record StoryCheckOutcome
{
    /// <summary>dimensions 对象原始 JsonElement（各分组 = 分析器结果对象：overallScore/issues/…）。</summary>
    public required JsonElement Dimensions { get; init; }

    /// <summary>network.characterPageRank（多章数据缺失时为 null）。</summary>
    public NetworkPageRank? PageRank { get; init; }

    /// <summary>规则引擎 findings（standard/deep 才产出；quick/无 → 空表）。</summary>
    public IReadOnlyList<StoryRuleFinding> RuleFindings { get; init; } = [];

    /// <summary>回溯连续性审计（standard/deep 且窗口 ≥2 章且 setup/情节线非空；键缺席 → null）。</summary>
    public ContinuityAuditView? ContinuityAudit { get; init; }

    /// <summary>计划符合度（计划为空/正文空/样本不可测 → 键缺席 → null）。</summary>
    public PlanConformanceView? PlanConformance { get; init; }

    /// <summary>按 key 取维度分组对象（缺省 default）。</summary>
    public JsonElement GetDimension(string key) =>
        Dimensions.ValueKind == JsonValueKind.Object
        && Dimensions.TryGetProperty(key, out var dim)
        && dim.ValueKind == JsonValueKind.Object
            ? dim
            : default;
}

/// <summary>单条行动项（§4.2 规则产出）。</summary>
/// <param name="Code">规则编号（S-1/A-1..A-8），用于测试与回复引用。</param>
/// <param name="PriorityRank">优先级序（小者优先；§4.2 排序表）。</param>
/// <param name="Evidence">证据段文本。</param>
/// <param name="Prescription">处方段文本。</param>
/// <param name="Boundary">边界段文本。</param>
public sealed record ActionItem(
    string Code,
    int PriorityRank,
    string Evidence,
    string Prescription,
    string Boundary
);

/// <summary>规则引擎输出：≤3 条有序行动 + 封闭集合外维度的透传警告。</summary>
public sealed record RuleOutcome(
    IReadOnlyList<ActionItem> Actions,
    IReadOnlyList<string> PassthroughWarnings
);

/// <summary>
/// F1 全量规则引擎（HARNESS-CLOUD §4.2）：封闭集合消费已核实的云端返回结构。
/// 信号源分两档——基础档 context.retrieve（features/signals/dimensionScores）、
/// 深度档 story.check（dimensions 23 键）；scene.plan 的 repairIntent 由 G0 路径单独处理。
/// </summary>
public static class SignalRules
{
    /// <summary>§4.2 已覆盖的维度键（其余键 → Warnings 透传）。</summary>
    private static readonly HashSet<string> CoveredKeys =
    [
        "pace",
        "network",
        "immersion",
        "threads",
        "characters",
    ];

    /// <summary>
    /// RACE 修辞维度（CHANGE_06 §10）：不进 CoveredKeys（不触发处方评估），
    /// 走独立信息性透传；云端 isShadow=false（校准通过）后自动转为 A-9 处方。
    /// </summary>
    private const string RhetoricKey = "rhetoric";

    /// <summary>A-9 结构 AI 味浓度阈值（对齐 RACE StructureAiFlavor 的 calibrated 工作点）。</summary>
    private const double RhetoricStructureThreshold = 0.5;

    /// <summary>A-6/A-7 低分阈值（百分制；dimensions.overallScore 为百分制分数空间）。</summary>
    private const double LowDimensionScore = 60;

    /// <summary>
    /// §15.4 SuggestedAction → 标准处方模板（G-3 修复 2026-08-30）。
    /// 云端 `SuggestedActionFor` 6 种动作（accelerate/intensify/enhance/rewrite_selection/
    /// plant_foreshadowing/strengthen_hook），语义对齐 WritingProcessPrompt 工具
    /// （write_chapter/rewrite_selection/enhance_*/plant_foreshadowing/enhance_chapter_hook）。
    /// 未知动作返回 null（调用方保留原文案，防止客户端逻辑漂移出契约）。
    /// </summary>
    private static string? SuggestedActionToPrescription(string? action) =>
        action switch
        {
            "accelerate" => "下一章加速节奏：插入事件/推进主线（write_chapter 调整句式长短交替）",
            "intensify" =>
                "下一章强化冲突：引入对抗或两难（evaluate_conflict_intensity 后 enhance）",
            "enhance" => "增强文笔：描写/对话/风格专项 enhance_* 工具（enhance_prose_quality 等）",
            "rewrite_selection" => "对问题段落执行 rewrite_selection 重写，保留结构改表达",
            "plant_foreshadowing" =>
                "埋设/养护伏笔：插入渐近暗示段（plant_foreshadowing 语义，维持暗线存在感）",
            "strengthen_hook" => "强化章末钩子：结尾加悬念或反转（enhance_chapter_hook）",
            _ => null,
        };

    /// <summary>A-4 暗线密度健康区（CHANGE_03 E5：同时维持 3–5 条为健康态）。</summary>
    private const int MinOpenPlotlines = 2;
    private const int MaxOpenPlotlines = 6;

    /// <summary>
    /// 严重度值域统一（KEY-1 修复）：云端 Issue.Severity 为中文（"严重/中等/轻微"，Severity.cs），
    /// 历史测试桩与旧契约用英文（"high/medium/low"）。所有消费点先过此归一为英文小写，
    /// 杜绝「云端中文实值 vs 客户端英文比较」永不命中的契约错配。
    /// </summary>
    public static string NormalizeSeverity(string? severity) =>
        severity?.Trim().ToLowerInvariant() switch
        {
            "严重" or CloudContractLiterals.SeverityHigh => CloudContractLiterals.SeverityHigh,
            "中等" or CloudContractLiterals.SeverityMedium => CloudContractLiterals.SeverityMedium,
            "轻微" or CloudContractLiterals.SeverityLow => CloudContractLiterals.SeverityLow,
            // KEY-1 补漏：未知值统一 lowercase 后返回（否则 "Critical" 保留原大小写，
            _ => string.IsNullOrEmpty(severity?.Trim())
                ? CloudContractLiterals.SeverityLow
                : severity!.Trim().ToLowerInvariant(),
        };

    /// <summary>
    /// 全量规则评估。入参均可为 null（该源本轮未调用）。返回 ≤3 条行动 + 透传警告。
    /// </summary>
    public static RuleOutcome Evaluate(
        ScenePlanOutcome? scenePlan,
        ContextRetrieveOutcome? contextRetrieve,
        StoryCheckOutcome? storyCheck,
        LocalFacts facts
    )
    {
        var items = new List<ActionItem>();

        // S-1：基础档 signals 高危直通（优先级仅次于 DECISION）
        if (contextRetrieve is not null)
        {
            foreach (
                var s in contextRetrieve.Signals.Where(s =>
                    NormalizeSeverity(s.Severity) == CloudContractLiterals.SeverityHigh
                )
            )
            {
                var prescription = string.IsNullOrWhiteSpace(s.Suggestion)
                    ? s.Description
                    : s.Suggestion;
                var boundary = "来源：基础档 context.retrieve signals";
                // G-3（§15.4）：suggestedAction 映射为标准处方模板（处方一致性），
                // 未知动作保留 suggestion 原文（封闭集合守卫）。
                if (s.SuggestedAction is not null)
                {
                    boundary += $"，建议动作:{s.SuggestedAction}";
                    if (SuggestedActionToPrescription(s.SuggestedAction) is { } mapped)
                        prescription = mapped;
                }
                if (s.Confidence < 1.0)
                    boundary += $"，置信度:{s.Confidence:F2}";
                items.Add(
                    new ActionItem(
                        $"S-1({s.Dimension})",
                        10,
                        $"[{s.Dimension}/{s.Severity}] {s.Description}",
                        prescription + "，下一章按此调整",
                        boundary
                    )
                );
            }
        }

        // F-1~F-5 基础档专属维度预警（§16.1，优先级低于 A-8）
        if (contextRetrieve is not null)
        {
            var scores = contextRetrieve.DimensionScores;
            var weakDimensions = new List<string>();
            var allWeakDimensions = new List<string>();

            // F-1 节奏预警
            if (scores.TryGetValue("节奏", out var paceScore) && paceScore < 55)
            {
                weakDimensions.Add("节奏");
                items.Add(
                    new ActionItem(
                        "F-1",
                        100,
                        $"[维度/节奏] {paceScore}（阈值 55）",
                        "节奏偏慢，下章考虑加速或插入事件",
                        "来源：基础档 context.retrieve dimensionScores"
                    )
                );
            }

            // F-2 冲突预警
            if (scores.TryGetValue("冲突", out var conflictScore) && conflictScore < 50)
            {
                weakDimensions.Add("冲突");
                items.Add(
                    new ActionItem(
                        "F-2",
                        101,
                        $"[维度/冲突] {conflictScore}（阈值 50）",
                        "冲突感不足，建议引入对抗或两难",
                        "来源：基础档 context.retrieve dimensionScores"
                    )
                );
            }

            // F-3 对话预警
            if (scores.TryGetValue("对话", out var dialogueScore) && dialogueScore < 45)
            {
                weakDimensions.Add("对话");
                items.Add(
                    new ActionItem(
                        "F-3",
                        102,
                        $"[维度/对话] {dialogueScore}（阈值 45）",
                        "对话占比偏低，考虑用对话推进信息",
                        "来源：基础档 context.retrieve dimensionScores"
                    )
                );
            }

            // F-4 章末钩子预警
            if (scores.TryGetValue("章末钩子", out var hookScore) && hookScore < 55)
            {
                weakDimensions.Add("章末钩子");
                items.Add(
                    new ActionItem(
                        "F-4",
                        103,
                        $"[维度/章末钩子] {hookScore}（阈值 55）",
                        "钩子力度不足，结尾加悬念或反转",
                        "来源：基础档 context.retrieve dimensionScores"
                    )
                );
            }

            // F-5 复合预警
            foreach (var kv in scores)
            {
                if (kv.Value < 55 && !allWeakDimensions.Contains(kv.Key))
                    allWeakDimensions.Add(kv.Key);
            }
            if (allWeakDimensions.Count >= 3)
            {
                items.Add(
                    new ActionItem(
                        "F-5",
                        104,
                        $"[多维度] {string.Join("、", allWeakDimensions)} 同时偏弱",
                        "多维度偏弱，建议考虑深度诊断（消耗更多积分）",
                        "来源：基础档 context.retrieve dimensionScores"
                    )
                );
            }
        }

        // A-3：伏笔逾期（Q2 定案：复用 GetWritingStatsTool overdue 语义）。
        // 双锚点：①计划揭示节点已过仍未回收（原逻辑）；②无计划揭示章时，
        // 用伏笔名在正文首次出现的章序（§6.3 埋设章龄锚点）判定「埋设超龄 > 30 章」。
        foreach (var p in facts.OpenPlotlines)
        {
            if (p.PlannedRevealOrder is { } planned && facts.MaxChapterOrder >= planned)
            {
                var gap = facts.MaxChapterOrder - planned;
                items.Add(
                    new ActionItem(
                        $"A-3({p.Name})",
                        20,
                        $"伏笔「{p.Name}」计划于第 {planned} 章揭示，当前已至第 {facts.MaxChapterOrder} 章"
                            + (gap > 0 ? $"（已逾期 {gap} 章）" : ""),
                        $"立即安排「{p.Name}」回收，或插入『养』段落（渐近暗示）维持存在感",
                        "判定源：本地 Plotline 计划揭示章引用"
                    )
                );
            }
            else if (p.FirstAppearOrder is { } first && facts.MaxChapterOrder - first > 30)
            {
                // §6.3：最长悬而未决 > 30 章 → 强制 A-3（无计划揭示章的伏笔不再被跳过）
                items.Add(
                    new ActionItem(
                        $"A-3({p.Name})",
                        20,
                        $"伏笔「{p.Name}」自第 {first} 章埋设已逾 {facts.MaxChapterOrder - first} 章未回收"
                            + "（无计划揭示章，按埋设章龄判定超龄）",
                        $"立即安排「{p.Name}」回收，或插入『养』段落（渐近暗示）维持存在感",
                        "判定源：本地 Plotline 埋设章龄锚点（正文首次出现章序，字符串扫描近似）"
                    )
                );
            }
        }

        // A-1：配角喧宾夺主（深度档 network 位）
        if (storyCheck?.PageRank is { } pr)
        {
            var eclipsed =
                pr.N1ProtagonistEclipsed
                || (
                    !string.IsNullOrEmpty(pr.TopCharacter)
                    && !string.IsNullOrEmpty(facts.ProtagonistName)
                    && pr.TopCharacter != facts.ProtagonistName
                );
            if (eclipsed)
            {
                // G-1（§15.3）：network 维度置信度（云端 EnrichStoryCheckDimensions 附加）；
                // 低置信度（无完整角色网络数据）→ rank 后移降级为软信号。
                var dims = storyCheck!.Dimensions;
                double confidence = 1.0;
                if (
                    dims.ValueKind == JsonValueKind.Object
                    && dims.TryGetProperty("network", out var net)
                    && net.ValueKind == JsonValueKind.Object
                    && net.TryGetProperty("confidence", out var conf)
                    && conf.ValueKind == JsonValueKind.Number
                )
                    confidence = conf.GetDouble();
                var effectiveRank = confidence < 0.5 ? 30 + 10 : 30;
                var boundary =
                    $"来源：fiction.story.check network.characterPageRank（PageRank 结构分析）";
                if (confidence < 0.5)
                    boundary += $"，置信度低({confidence:F2})，结论基于文本推测";
                items.Add(
                    new ActionItem(
                        "A-1",
                        effectiveRank,
                        $"结构权重榜首为「{pr.TopCharacter}」，高于主角「{facts.ProtagonistName}」",
                        "后续章节回收视角权重，或 rewrite_selection 弱化其戏份",
                        boundary
                    )
                );
            }
        }

        // A-7：代入感异常（immersion 分组低分或含问题）
        AppendDimensionRule(
            items,
            storyCheck,
            "immersion",
            "A-7",
            40,
            "可亲近性处方：检查主角行为合理性/付出代价/被挑战频率"
        );

        // A-7 补充：characters[].arc 角色弧光异常（云端 characters 为数组，逐角色判定）
        AppendCharacterArcRule(items, storyCheck);

        // A-6：节奏异常
        AppendDimensionRule(
            items,
            storyCheck,
            "pace",
            "A-6",
            50,
            "下一章调整节奏（过山车原理 + 句子长短调控）"
        );

        // A-4：暗线密度越界
        var openCount = facts.OpenPlotlines.Count;
        if (openCount < MinOpenPlotlines)
        {
            // §6.3 熵方向粗判：熵升 ≈ 埋线期宜埋新线；熵降 ≈ 宜收线/养
            var plantPrescription = facts.EntropyDelta switch
            {
                > 0 => "适合在本节点埋新线（Plant）",
                < 0 => "叙事正在收束，宜优先收线或『养』既有暗线，暂缓埋新",
                _ => "适合在本节点埋新线（Plant）",
            };
            items.Add(
                new ActionItem(
                    "A-4",
                    70,
                    $"活跃暗线仅 {openCount} 条（健康区 {MinOpenPlotlines}–{MaxOpenPlotlines} 条）"
                        + (facts.EntropyDelta is { } d ? $"，熵差 {d:+0.####;-0.####}" : ""),
                    plantPrescription,
                    "依据 CHANGE_03 E5 伏笔密度健康区"
                        + (facts.EntropyDelta is not null ? "；熵差为快照减法近似" : "")
                )
            );
        }
        else if (openCount > MaxOpenPlotlines)
        {
            items.Add(
                new ActionItem(
                    "A-4",
                    70,
                    $"活跃暗线达 {openCount} 条（健康区 {MinOpenPlotlines}–{MaxOpenPlotlines} 条）",
                    "暗线过载：停止新埋，优先养/收既有暗线",
                    "依据 CHANGE_03 E5 伏笔密度健康区"
                )
            );
        }

        // A-4 补充：story.check.dimensions.threads 云端伏笔密度信号（本地健康区时为第二来源）
        AppendThreadsDensitySignal(items, storyCheck, facts);

        // A-5：疑似提前泄底
        if (facts.ActiveInfoGaps == 0 && openCount > 0)
        {
            var first = facts.OpenPlotlines[0].Name;
            items.Add(
                new ActionItem(
                    "A-5",
                    80,
                    "本章信息差计数为 0，但仍有未揭晓伏笔",
                    $"疑似提前泄底：「{first}」尚未到计划揭示节点，复核本章信息投放",
                    "判定源：WritingProfile.ScanInfoAsymmetryCount + 本地未解决情节线"
                )
            );
        }

        // A-8：choose_focus（scene.plan warning 档，不触发人工）
        if (
            scenePlan?.RepairAction == CloudContractLiterals.RepairChooseFocus
            && scenePlan.FocusAreas.Count > 0
        )
        {
            items.Add(
                new ActionItem(
                    "A-8",
                    90,
                    $"场景规划建议聚焦：{string.Join("、", scenePlan.FocusAreas)}",
                    "下一章聚焦以上维度修复",
                    "来源：fiction.scene.plan repairIntent.choose_focus"
                )
            );
        }

        // ── RACE 修辞维度信息性透传（CHANGE_06 §10 shadow 证据）──
        // shadow（未校准）→ 仅透传信息，不触发处方；isShadow=false（云端校准通过）
        // → 自动转为 A-9 处方（结构 AI 味 → 加深逻辑层次）。零代码切换接入位。
        var warnings = new List<string>();
        if (storyCheck is not null)
        {
            var rhetoric = storyCheck.GetDimension(RhetoricKey);
            if (rhetoric.ValueKind == JsonValueKind.Object)
            {
                var isShadow =
                    rhetoric.TryGetProperty("is-shadow", out var shadowEl)
                    && shadowEl.ValueKind == JsonValueKind.True;
                var loopState = rhetoric.TryGetProperty("loop-state", out var loopEl)
                    ? loopEl.GetString()
                    : null;
                var structure =
                    rhetoric.TryGetProperty("structure-ai-flavor", out var structEl)
                    && structEl.ValueKind == JsonValueKind.Number
                        ? structEl.GetDouble()
                        : 0;
                var surface =
                    rhetoric.TryGetProperty("surface-ai-flavor", out var surfEl)
                    && surfEl.ValueKind == JsonValueKind.Number
                        ? surfEl.GetDouble()
                        : 0;
                var conclusion = rhetoric.TryGetProperty("conclusion", out var concEl)
                    ? concEl.GetString()
                    : null;

                if (isShadow || loopState != "calibrated")
                {
                    // 信息性透传：浓度仅供 LLM 参考，不作为处方
                    warnings.Add(
                        $"RACE 修辞证据（shadow 未校准）：结构浓度 {structure:F2}，"
                            + $"表层浓度 {surface:F2}"
                            + (string.IsNullOrEmpty(conclusion) ? "" : $"；{conclusion}")
                    );
                }
                else if (structure >= RhetoricStructureThreshold)
                {
                    // 校准通过：A-9 处方（RACE 2×2 象限 repair_required(结构) 分支）
                    items.Add(
                        new ActionItem(
                            "A-9",
                            40,
                            $"[rhetoric] 结构 AI 味浓度 {structure:F2}（阈值 {RhetoricStructureThreshold}）"
                                + (string.IsNullOrEmpty(conclusion) ? "" : $"：{conclusion}"),
                            "增加归因/时序/转折关系，加深逻辑层次",
                            "来源：fiction.story.check dimensions.rhetoric（RACE 校准后）"
                        )
                    );
                }
            }
        }

        // ── review 轴消费（2026-10-18）：story.check 顶层新增段 → 行动项 ──
        // 服务端 standard/deep 才产出；quick/旧服务端 → 段缺席 → 无行动（既有排序与配额不变）。
        AppendContinuityRule(items, storyCheck);
        AppendRuleFindingRule(items, storyCheck);
        AppendPlanConformanceRule(items, storyCheck);

        // 封闭集合守卫：规则表外的维度键 → Warnings 透传原文摘要（§4.2）
        if (storyCheck is not null && storyCheck.Dimensions.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in storyCheck.Dimensions.EnumerateObject())
            {
                if (
                    CoveredKeys.Contains(prop.Name)
                    || prop.Name == RhetoricKey
                    || prop.Value.ValueKind != JsonValueKind.Object
                )
                    continue;
                var issues =
                    prop.Value.TryGetProperty("issues", out var arr)
                    && arr.ValueKind == JsonValueKind.Array
                        ? arr.GetArrayLength()
                        : 0;
                if (issues == 0)
                    continue;
                var firstIssue = arr.EnumerateArray().FirstOrDefault();
                var desc =
                    firstIssue.ValueKind == JsonValueKind.Object
                    && firstIssue.TryGetProperty("description", out var d)
                        ? d.GetString()
                        : null;
                var severity =
                    firstIssue.ValueKind == JsonValueKind.Object
                    && firstIssue.TryGetProperty("severity", out var sevEl)
                        ? sevEl.GetString()
                        : null;
                if (NormalizeSeverity(severity) == CloudContractLiterals.SeverityHigh)
                {
                    // §16.7 动态注册：规则表外的云端新增维度，若高严重度问题 → 弱 ACTION
                    // （信息透传，不强加处方）；预设规则（rank 10-40）优先级高于动态（rank 100）。
                    items.Add(
                        new ActionItem(
                            $"DYNAMIC({prop.Name})",
                            100,
                            $"[{prop.Name}/high] {desc ?? $"共 {issues} 个问题"}",
                            "云端识别到该维度高严重度问题，建议复核本章相关内容（信息透传，不强加处方）",
                            "来源：动态维度注册（§16.7），优先级低于预设规则"
                        )
                    );
                }
                else
                {
                    warnings.Add(
                        $"维度 {prop.Name}（{issues} 个问题）：{desc ?? "详见 analyze_* 工具"}"
                    );
                }
            }
        }

        if (contextRetrieve?.Trends is { Count: > 0 } trends)
        {
            warnings.AddRange(trends);
        }
        return new RuleOutcome(
            [.. items.OrderBy(i => i.PriorityRank).ThenBy(i => i.Code).Take(3)],
            warnings
        );
    }

    /// <summary>严重度 → 序数（0 高 / 1 中 / 2 低；中文与英文实值统一归一）。</summary>
    private static int SeverityRank(string? severity) =>
        NormalizeSeverity(severity) switch
        {
            CloudContractLiterals.SeverityHigh => 0,
            CloudContractLiterals.SeverityMedium => 1,
            _ => 2,
        };

    /// <summary>
    /// C-1：回溯连续性审计（story.check 顶层 continuity-audit，PlanConformance 同批接线）。
    /// 取影响最高的一条；conflict（与设定图/正文自相矛盾）rank 12——低于基础档高危直通（S-1=10）、
    /// 高于 A-3 伏笔逾期（20）：已提交窗口内的自相矛盾是最廉价可修的客观缺陷；
    /// medium → 45、low → 75（低于 A-6 节奏等既定规则）。余下条目只计数（信号是提示，不是报告）。
    /// </summary>
    private static void AppendContinuityRule(List<ActionItem> items, StoryCheckOutcome? storyCheck)
    {
        if (storyCheck?.ContinuityAudit is not { Findings.Count: > 0 } audit)
            return;
        var top = audit
            .Findings.OrderByDescending(f => f.ImpactScore)
            .ThenBy(f => SeverityRank(f.Severity))
            .First();
        var rank =
            top.Kind == "conflict" ? 12
            : SeverityRank(top.Severity) <= 1 ? 45
            : 75;
        var chapters = top.Chapters.Count > 0 ? string.Join("、", top.Chapters) : "（章引用缺失）";
        var boundary =
            $"来源：fiction.story.check continuity-audit（规则 {top.Rule}，共 {audit.Findings.Count} 条）";
        if (audit.RevisionChapters.Count > 0)
            boundary += $"，候选修订章：{string.Join("、", audit.RevisionChapters.Take(3))}";
        items.Add(
            new ActionItem(
                $"C-1({top.Rule})",
                rank,
                $"[{top.Kind}] {chapters}：{top.Description}"
                    + (string.IsNullOrWhiteSpace(top.Evidence) ? "" : $"（证据：{top.Evidence}）"),
                top.Suggestion ?? "复核上述章节与设定图/情节线的冲突点并修订",
                boundary
            )
        );
    }

    /// <summary>
    /// R-1：规则引擎 findings（story.check 顶层 rule-findings）——取影响最高的一条。
    /// rank 55：低于 A-6（50）以降的既定维度规则，高于 A-4（70）类结构建议；
    /// 规则引擎输出是「lint」性质，不应挤掉校准过的维度处方。
    /// </summary>
    private static void AppendRuleFindingRule(List<ActionItem> items, StoryCheckOutcome? storyCheck)
    {
        if (storyCheck?.RuleFindings is not { Count: > 0 } findings)
            return;
        var top = findings.OrderByDescending(f => f.ImpactScore).First();
        items.Add(
            new ActionItem(
                $"R-1({top.Dimension})",
                55,
                $"[{top.Dimension}/{NormalizeSeverity(top.Severity)}] {top.Description}"
                    + (string.IsNullOrWhiteSpace(top.Evidence) ? "" : $"（证据：{top.Evidence}）"),
                top.Suggestion ?? "按规则说明修订本章对应问题",
                $"来源：fiction.story.check rule-findings（规则引擎，共 {findings.Count} 条，"
                    + $"最高影响 {top.ImpactScore:F1}）"
            )
        );
    }

    /// <summary>
    /// P-1：计划符合度（story.check 顶层 plan-conformance）——仅在 beat 未落地时产出。
    /// rank 60：计划漂移是流程性信号，低于内容维度规则、高于结构建议。
    /// </summary>
    private static void AppendPlanConformanceRule(
        List<ActionItem> items,
        StoryCheckOutcome? storyCheck
    )
    {
        if (storyCheck?.PlanConformance is not { MissedBeatCount: > 0 } plan)
            return;
        items.Add(
            new ActionItem(
                "P-1",
                60,
                $"计划 {plan.PlannedBeatCount} 个 beat，{plan.MissedBeatCount} 个未落地",
                "按计划补足未落地的 beat（或显式调整计划），避免计划与正文长期漂移",
                "来源：fiction.story.check plan-conformance（按章序匹配 Setup.Plotlines[].Beats）"
            )
        );
    }

    /// <summary>A-6/A-7 共用的维度低分/问题判定。</summary>
    private static void AppendDimensionRule(
        List<ActionItem> items,
        StoryCheckOutcome? storyCheck,
        string key,
        string code,
        int rank,
        string prescription
    )
    {
        if (storyCheck is null)
            return;
        var dim = storyCheck.GetDimension(key);
        if (dim.ValueKind != JsonValueKind.Object)
            return;

        double score =
            dim.TryGetProperty("overall-score", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetDouble()
                : 100;
        dim.TryGetProperty("issues", out var issuesEl);
        var issueCount = issuesEl.ValueKind == JsonValueKind.Array ? issuesEl.GetArrayLength() : 0;
        if (score >= LowDimensionScore && issueCount == 0)
            return;

        string evidence;
        string? suggestedAction = null;
        if (issueCount > 0)
        {
            var first = issuesEl.EnumerateArray().FirstOrDefault();
            var desc =
                first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("description", out var d)
                    ? d.GetString()
                    : null;
            evidence = $"[{key}] {desc ?? $"共 {issueCount} 个问题"}";
            if (
                first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("suggested-action", out var sa)
            )
                suggestedAction = sa.GetString();
        }
        else
        {
            evidence = $"[{key}] 综合得分 {score:F1}";
        }

        // HARNESS-CLOUD §15.3：低置信度诊断降级为软信号（rank 后移），边界段注明近似性。
        var effectiveRank = rank;
        var boundary = $"来源：fiction.story.check dimensions.{key}";
        double confidence =
            dim.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
                ? conf.GetDouble()
                : 1.0;
        if (confidence < 0.5)
        {
            effectiveRank = rank + 10; // 降级（更高 rank = 更后命中）
            boundary += $"，置信度低({confidence:F2})，结论基于文本推测";
        }
        // G-3（§15.4）：suggestedAction 映射为标准处方模板（处方一致性），
        // 未知动作保留调用方原文案（封闭集合守卫：不现场发明处方）。
        if (suggestedAction is not null)
        {
            boundary += $"，建议动作:{suggestedAction}";
            if (SuggestedActionToPrescription(suggestedAction) is { } mappedPrescription)
                prescription = mappedPrescription;
        }

        items.Add(new ActionItem(code, effectiveRank, evidence, prescription, boundary));
    }

    /// <summary>
    /// A-7 补充（§4.2 双源）：characters[].arc 角色弧光异常。云端 characters 为数组，
    /// 每元素含 name/arc（ArcResult：arcCompleteness 分数 + issues[]）。逐角色判定，
    /// issues 非空或弧光完整度低于 LowDimensionScore 时产出 A-7（与 immersion 同级 rank 40）。
    /// </summary>
    private static void AppendCharacterArcRule(
        List<ActionItem> items,
        StoryCheckOutcome? storyCheck
    )
    {
        if (storyCheck is null)
            return;
        var dims = storyCheck.Dimensions;
        if (
            dims.ValueKind != JsonValueKind.Object
            || !dims.TryGetProperty("characters", out var chars)
            || chars.ValueKind != JsonValueKind.Array
        )
            return;

        foreach (var ch in chars.EnumerateArray())
        {
            if (ch.ValueKind != JsonValueKind.Object)
                continue;
            var name = ch.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!ch.TryGetProperty("arc", out var arc) || arc.ValueKind != JsonValueKind.Object)
                continue;

            // arc 结果对象：issues[] 或 arc-completeness 低分
            string? issueDesc = null;
            if (
                arc.TryGetProperty("issues", out var issuesEl)
                && issuesEl.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var issue in issuesEl.EnumerateArray())
                {
                    if (
                        issue.ValueKind == JsonValueKind.Object
                        && issue.TryGetProperty("description", out var d)
                        && !string.IsNullOrWhiteSpace(d.GetString())
                    )
                    {
                        issueDesc = d.GetString();
                        break;
                    }
                }
            }
            double arcScore = 100;
            if (
                arc.TryGetProperty("arc-completeness", out var s)
                && s.ValueKind == JsonValueKind.Number
            )
                arcScore = s.GetDouble();
            if (issueDesc is null && arcScore >= LowDimensionScore)
                continue;

            // G-1（§15.3）：characters 数组元素级 confidence（云端 EnrichStoryCheckDimensions
            // 2026-08-30 起对数组维度每个元素附加）；低置信度（无结构化角色数据）→ rank 后移降级。
            var elementConfidence = 1.0;
            if (ch.TryGetProperty("confidence", out var ec) && ec.ValueKind == JsonValueKind.Number)
                elementConfidence = ec.GetDouble();
            var effectiveRank = elementConfidence < 0.5 ? 40 + 10 : 40;
            var boundary = "来源：fiction.story.check dimensions.characters[].arc";
            if (elementConfidence < 0.5)
                boundary += $"，置信度低({elementConfidence:F2})，结论基于文本推测";

            items.Add(
                new ActionItem(
                    "A-7",
                    effectiveRank,
                    $"[characters/{name ?? "?"}/arc] {issueDesc ?? $"弧光完整度 {arcScore:F1}"}",
                    "可亲近性处方：检查角色行为合理性/弧光完整度/被挑战频率",
                    boundary
                )
            );
        }
    }

    /// <summary>
    /// A-4 补充（§4.2 双源）：story.check.dimensions.threads（ForeshadowingResult）的
    /// high severity 密度问题。仅在本地暗线数处于健康区时启用——本地已越界时主 A-4 已覆盖，
    /// 避免重复。threads issues[] 为云端结构化问题（dimension/severity/description/suggestion）。
    /// </summary>
    private static void AppendThreadsDensitySignal(
        List<ActionItem> items,
        StoryCheckOutcome? storyCheck,
        LocalFacts facts
    )
    {
        if (storyCheck is null)
            return;
        var openCount = facts.OpenPlotlines.Count;
        // 本地已越界 → 主 A-4 已覆盖，不重复
        if (openCount < MinOpenPlotlines || openCount > MaxOpenPlotlines)
            return;

        var threads = storyCheck.GetDimension("threads");
        if (
            threads.ValueKind != JsonValueKind.Object
            || !threads.TryGetProperty("issues", out var issuesEl)
            || issuesEl.ValueKind != JsonValueKind.Array
        )
            return;

        foreach (var issue in issuesEl.EnumerateArray())
        {
            if (issue.ValueKind != JsonValueKind.Object)
                continue;
            var severity = issue.TryGetProperty("severity", out var sev) ? sev.GetString() : null;
            if (NormalizeSeverity(severity) != CloudContractLiterals.SeverityHigh)
                continue;
            var desc = issue.TryGetProperty("description", out var d) ? d.GetString() : null;
            var suggestion = issue.TryGetProperty("suggestion", out var sug)
                ? sug.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(desc))
                continue;
            items.Add(
                new ActionItem(
                    "A-4",
                    70,
                    $"[threads] {desc}",
                    suggestion ?? "复核暗线埋/养/收节奏，对齐 CHANGE_03 E5 密度健康区",
                    "来源：fiction.story.check dimensions.threads（Foreshadowing）"
                )
            );
            return; // 一条即可
        }
    }
}
