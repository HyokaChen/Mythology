using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 规则评估事实：数值指标 + 状态/行动 token。
/// 只有结构化事实才会被评估；正文文本不参与推断（ENTITY-EVOLUTION 红线 5）。
/// </summary>
public sealed record RuleFacts(
    IReadOnlyDictionary<string, double> Numeric,
    IReadOnlySet<string> Tokens
)
{
    public static readonly RuleFacts Empty = new(
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    );

    public bool HasToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && Tokens.Contains(token.Trim());

    /// <summary>关系 token：edge|target 或 relationship|target|type；target 为空表示任意目标。</summary>
    public IReadOnlySet<string> Relations { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>关系事实匹配：edge 精确；target 为空匹配任意目标，否则精确匹配或三段式前缀。</summary>
    public bool HasRelation(string? edgeType, string? targetId)
    {
        if (string.IsNullOrWhiteSpace(edgeType))
        {
            return false;
        }
        var edge = edgeType.Trim();
        var prefix = edge + "|";
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return Relations.Any(r => r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        var target = targetId.Trim();
        return Relations.Contains(prefix + target)
            || Relations.Any(r =>
                r.StartsWith(prefix + target + "|", StringComparison.OrdinalIgnoreCase)
            );
    }

    public bool TryNumeric(string? metric, out double value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(metric) && Numeric.TryGetValue(metric.Trim(), out value);
    }
}

/// <summary>
/// 世界规则求值器（确定性、无 LLM）。
///
/// 支持四类闭集模板：
/// - Numeric：指标满足比较符 → Effect；
/// - Requires：执行 Action 时缺少 RequiredState → Effect；
/// - Forbids：执行 Action/ForbiddenAction → Effect；
/// - Triggers：出现 Action 条件 → Effect。
///
/// SubjectType/SubjectId 不匹配时不评估。条件/后果缺字段时返回 null（宁漏不猜）。
/// </summary>
public static class RuleEvaluator
{
    /// <summary>对单个主体评估一条规则；未触发返回 null。</summary>
    public static PlannedDelta? Evaluate(
        WorldRule rule,
        EntityType subjectType,
        string subjectId,
        RuleFacts facts
    )
    {
        if (rule.EntryKind != WorldRuleEntryKind.Rule)
        {
            return null;
        }
        if (rule.Effect is null)
        {
            return null;
        }
        if (rule.SubjectType is { } requiredType && requiredType != subjectType)
        {
            return null;
        }
        if (
            !string.IsNullOrWhiteSpace(rule.SubjectId)
            && !string.Equals(rule.SubjectId, subjectId, StringComparison.Ordinal)
        )
        {
            return null;
        }

        var triggered = rule.RuleKind switch
        {
            RuleKind.Numeric => NumericTriggered(rule, facts),
            RuleKind.Requires => facts.HasToken(rule.Action) && RequiresMissing(rule, facts),
            RuleKind.Forbids => facts.HasToken(rule.ForbiddenAction ?? rule.Action),
            RuleKind.Triggers => facts.HasToken(rule.Action),
            _ => false,
        };
        return triggered ? rule.Effect : null;
    }

    private static bool RequiresMissing(WorldRule rule, RuleFacts facts)
    {
        var missingState =
            !string.IsNullOrWhiteSpace(rule.RequiredState) && !facts.HasToken(rule.RequiredState);
        var missingRelation =
            !string.IsNullOrWhiteSpace(rule.RequiredRelationType)
            && !facts.HasRelation(rule.RequiredRelationType, rule.RequiredRelationTargetId);
        return missingState || missingRelation;
    }

    /// <summary>规则是否在指定章生效（章节范围；无法解析的边界视为开放）。</summary>
    public static bool AppliesToChapter(
        WorldRule rule,
        IReadOnlyDictionary<string, int> chapterIdToOrder,
        int chapterOrder
    )
    {
        if (rule.EntryKind != WorldRuleEntryKind.Rule)
        {
            return false;
        }
        if (
            !string.IsNullOrWhiteSpace(rule.EffectiveFromChapterId)
            && chapterIdToOrder.TryGetValue(rule.EffectiveFromChapterId, out var from)
            && chapterOrder < from
        )
        {
            return false;
        }
        if (
            !string.IsNullOrWhiteSpace(rule.EffectiveUntilChapterId)
            && chapterIdToOrder.TryGetValue(rule.EffectiveUntilChapterId, out var until)
            && chapterOrder > until
        )
        {
            return false;
        }
        return true;
    }

    private static bool NumericTriggered(WorldRule rule, RuleFacts facts)
    {
        if (
            string.IsNullOrWhiteSpace(rule.Operator)
            || rule.Threshold is not { } threshold
            || !facts.TryNumeric(rule.Metric, out var value)
        )
        {
            return false;
        }

        return rule.Operator.Trim() switch
        {
            ">" => value > threshold,
            ">=" => value >= threshold,
            "<" => value < threshold,
            "<=" => value <= threshold,
            "==" => Math.Abs(value - threshold) < 1e-9,
            _ => false,
        };
    }
}
