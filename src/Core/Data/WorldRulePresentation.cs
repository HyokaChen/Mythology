using System.Globalization;

namespace Core.Data;

/// <summary>
/// 世界规则/设定的共享展示与校验（TUI 与 Desktop 共用，避免两份标签/规则漂移）。
///
/// 只做展示文案与输入校验；不参与 RuleEvaluator 的求值语义。
/// </summary>
public static class WorldRulePresentation
{
    /// <summary>领域筛选的「全部」文案。</summary>
    public const string AllCategoriesLabel = "全部领域";

    /// <summary>领域分类（展示顺序固定）。</summary>
    public static readonly (WorldRuleCategory Value, string Label)[] Categories =
    [
        (WorldRuleCategory.Natural, "自然法则"),
        (WorldRuleCategory.Power, "力量体系"),
        (WorldRuleCategory.Social, "社会制度"),
        (WorldRuleCategory.Narrative, "叙事约束"),
        (WorldRuleCategory.Cognitive, "信息认知"),
        (WorldRuleCategory.Other, "其他"),
    ];

    /// <summary>规则机制（展示顺序固定）。</summary>
    public static readonly (RuleKind Value, string Label)[] RuleKinds =
    [
        (RuleKind.Numeric, "数值边界"),
        (RuleKind.Requires, "前置条件"),
        (RuleKind.Forbids, "禁止"),
        (RuleKind.Triggers, "触发"),
    ];

    /// <summary>数值比较符。</summary>
    public static readonly (string Value, string Label)[] Operators =
    [
        (">", ">"),
        (">=", ">="),
        ("<", "<"),
        ("<=", "<="),
        ("==", "=="),
    ];

    /// <summary>规则严重度（展示顺序固定）。</summary>
    public static readonly (RuleSeverity Value, string Label)[] RuleSeverities =
    [
        (RuleSeverity.Info, "提示"),
        (RuleSeverity.Warning, "警告"),
        (RuleSeverity.Constraint, "硬约束"),
    ];

    /// <summary>关系前置 token 白名单。</summary>
    public static readonly string[] RuleRelationTypes =
    [
        "member-of",
        "has-member",
        "relationship",
        "owns",
        "owned-by",
        "parent",
        "adjacent",
        "headquarters",
    ];

    /// <summary>后果 delta 类型。</summary>
    public static readonly (PlannedDeltaKind Value, string Label)[] DeltaKinds =
    [
        (PlannedDeltaKind.StateChange, "状态变化"),
        (PlannedDeltaKind.RelationChange, "关系变化"),
        (PlannedDeltaKind.RuleTrigger, "规则触发"),
        (PlannedDeltaKind.Action, "行动"),
        (PlannedDeltaKind.PlotlineReveal, "情节揭示"),
    ];

    /// <summary>变化面（null = 不指定）。</summary>
    public static readonly (EvolutionAspect? Value, string Label)[] Aspects =
    [
        (null, "（无）"),
        (EvolutionAspect.Status, "状态"),
        (EvolutionAspect.Relationship, "关系"),
        (EvolutionAspect.Ability, "能力"),
        (EvolutionAspect.Possession, "持有"),
        (EvolutionAspect.Location, "位置"),
        (EvolutionAspect.Knowledge, "认知"),
        (EvolutionAspect.Behavior, "行为"),
        (EvolutionAspect.Other, "其他"),
    ];

    /// <summary>规则主体类型（RuleEvaluator 只对这些类型构造 facts）。</summary>
    public static readonly (EntityType Value, string Label)[] SubjectTypes =
    [
        (EntityType.Character, "角色"),
        (EntityType.Faction, "组织"),
        (EntityType.Location, "地点"),
        (EntityType.Item, "道具"),
    ];

    /// <summary>规则后果目标类型。</summary>
    public static readonly (EntityType Value, string Label)[] TargetTypes =
    [
        (EntityType.Character, "角色"),
        (EntityType.Faction, "组织"),
        (EntityType.Location, "地点"),
        (EntityType.Item, "道具"),
        (EntityType.Plotline, "情节"),
        (EntityType.Chapter, "章节"),
    ];

    /// <summary>领域标签；未知值原样返回。</summary>
    public static string CategoryLabel(WorldRuleCategory value)
    {
        foreach (var (candidate, label) in Categories)
        {
            if (candidate == value)
                return label;
        }
        return value.ToString();
    }

    /// <summary>机制标签；未知值原样返回。</summary>
    public static string RuleKindLabel(RuleKind value)
    {
        foreach (var (candidate, label) in RuleKinds)
        {
            if (candidate == value)
                return label;
        }
        return value.ToString();
    }

    /// <summary>是否命中云端精确检查的规范类型（travel-speed / location-distance）。</summary>
    public static bool IsCloudPreset(WorldRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Type))
            return false;
        var key = WorldRuleTypes.Normalize(rule.Type);
        foreach (var (canonical, _, _) in WorldRuleTypes.All)
        {
            if (string.Equals(canonical, key, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>列表卡片一行摘要（不含领域/机制徽章）。</summary>
    public static string SummaryFor(WorldRule rule)
    {
        if (rule.EntryKind == WorldRuleEntryKind.Setting)
            return $"【设定】{TitleOf(rule)}";

        string summary;
        if (IsCloudPreset(rule))
        {
            summary =
                $"{WorldRuleTypes.LabelFor(rule.Type)} " + $"{FormatNumber(rule.Value)}{rule.Unit}";
        }
        else
        {
            summary = rule.RuleKind switch
            {
                RuleKind.Numeric =>
                    $"{rule.Metric} {rule.Operator} {FormatNullable(rule.Threshold)}".Trim(),
                RuleKind.Requires => $"{rule.Action} 需要 {rule.RequiredState}".Trim(),
                RuleKind.Forbids => $"禁止 {rule.ForbiddenAction ?? rule.Action}".Trim(),
                RuleKind.Triggers => $"当 {rule.Action}".Trim(),
                _ => TitleOf(rule),
            };
            if (string.IsNullOrWhiteSpace(summary))
                summary = TitleOf(rule);
        }

        return string.IsNullOrWhiteSpace(rule.Effect?.Summary)
            ? summary
            : $"{summary} → {rule.Effect.Summary}";
    }

    /// <summary>
    /// 校验一条规则/设定。返回 null = 通过；否则是可直接展示给用户的中文错误。
    /// 数值格式由表单在解析阶段处理，这里只校验已解析的模型值。
    /// </summary>
    public static string? Validate(WorldRule rule)
    {
        if (rule.EntryKind == WorldRuleEntryKind.Setting)
        {
            return
                string.IsNullOrWhiteSpace(rule.Description)
                || string.IsNullOrWhiteSpace(rule.Content)
                ? "设定需要填写标题和正文"
                : null;
        }

        if (string.IsNullOrWhiteSpace(rule.Description))
            return "请填写规则说明";

        if (
            !string.IsNullOrWhiteSpace(rule.RequiredRelationType)
            && !RuleRelationTypes.Contains(rule.RequiredRelationType.Trim(), StringComparer.Ordinal)
        )
        {
            return "关系前置类型无效";
        }

        if (IsCloudPreset(rule))
        {
            if (rule.Value <= 0)
                return "云端检查需要大于 0 的数值";
            if (string.IsNullOrWhiteSpace(rule.Unit))
                return "云端检查需要单位";
            return null;
        }

        switch (rule.RuleKind)
        {
            case RuleKind.Numeric:
                if (
                    string.IsNullOrWhiteSpace(rule.Metric)
                    || string.IsNullOrWhiteSpace(rule.Operator)
                    || !Operators.Any(op =>
                        string.Equals(op.Value, rule.Operator.Trim(), StringComparison.Ordinal)
                    )
                    || rule.Threshold is null
                )
                {
                    return "数值规则需要填写指标、比较符和阈值";
                }
                break;

            case RuleKind.Requires:
                if (
                    string.IsNullOrWhiteSpace(rule.Action)
                    || (
                        string.IsNullOrWhiteSpace(rule.RequiredState)
                        && string.IsNullOrWhiteSpace(rule.RequiredRelationType)
                    )
                )
                {
                    return "前置条件需要填写行动，以及所需状态或关系";
                }
                break;

            case RuleKind.Forbids:
                if (string.IsNullOrWhiteSpace(rule.ForbiddenAction ?? rule.Action))
                    return "禁止规则需要填写被禁止的行动";
                break;

            case RuleKind.Triggers:
                if (string.IsNullOrWhiteSpace(rule.Action))
                    return "触发规则需要填写触发条件";
                break;
        }

        return string.IsNullOrWhiteSpace(rule.Effect?.Summary) ? "该机制必须配置后果摘要" : null;
    }

    /// <summary>编辑既有规则时保留面板未暴露的本体字段，避免 UI 保存把高级定义清空。</summary>
    public static void CopyAdvanced(WorldRule source, WorldRule target)
    {
        target.Severity = source.Severity;
        target.EffectiveFromChapterId = source.EffectiveFromChapterId;
        target.EffectiveUntilChapterId = source.EffectiveUntilChapterId;
        target.RequiredRelationType = source.RequiredRelationType;
        target.RequiredRelationTargetId = source.RequiredRelationTargetId;
    }

    private static string TitleOf(WorldRule rule) =>
        string.IsNullOrWhiteSpace(rule.Description)
            ? rule.EntryKind == WorldRuleEntryKind.Setting
                ? "未命名设定"
                : "未命名规则"
            : rule.Description;

    private static string FormatNumber(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatNullable(double? value) =>
        value is null ? "" : FormatNumber(value.Value);
}
