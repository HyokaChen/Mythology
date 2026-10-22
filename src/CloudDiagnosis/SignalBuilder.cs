using System.Text.Json;
using Core.Cloud;

namespace CloudDiagnosis;

/// <summary>云端信号类别（HARNESS-CLOUD §4.1 三类动作导向信号）。</summary>
public enum CloudSignalKind
{
    /// <summary>继续类：质量达标，免重复诊断。</summary>
    Pass,

    /// <summary>行动类：命中处方规则，需按证据行动。</summary>
    Action,

    /// <summary>决策类：requiresHumanDecision=true，路由 HumanDecisionBridge。</summary>
    Decision,
}

/// <summary>
/// fiction.scene.plan 结果解析产物（G0 最小版）：
/// 只解析 features.overallScore 与 repairIntent（Q1 定案字段）。
/// </summary>
/// <param name="Kind">信号类别。</param>
/// <param name="OverallScore">综合分，归一化为百分制（云端 0-1 × 100，四舍五入）。</param>
/// <param name="RepairAction">repairIntent.action；无 repairIntent 时为 null。</param>
/// <param name="RequiresHumanDecision">是否需人工裁决。</param>
/// <param name="Reason">human_decision_required 携带的英文原因（可空）。</param>
/// <param name="FocusAreas">choose_focus 携带的 top3 维度名（可空）。</param>
/// <param name="ReasonCode">U-1：human_decision_required 的稳定原因码（kebab）；
/// 本地化映射优先于英文 Reason 文本匹配，缺省 null（存量服务端）。</param>
public sealed record ScenePlanOutcome(
    CloudSignalKind Kind,
    double OverallScore,
    string? RepairAction,
    bool RequiresHumanDecision,
    string? Reason,
    IReadOnlyList<string> FocusAreas,
    string? ReasonCode = null
);

/// <summary>
/// 信号翻译层（HARNESS-CLOUD §4 组件 B）— G0 最小版。
/// 只消费 fiction.scene.plan 的返回结构（Q1/Q3 定案契约），产出三类信号文本。
/// F1 阶段补全 §4.2 全量规则表（S-1/A-1..A-8 与 story.check/context.retrieve 消费）。
/// </summary>
public static class SignalBuilder
{
    /// <summary>所有注入信号的识别锚点（§4.3，Prompt 层与测试依赖此前缀）。</summary>
    public const string Prefix = "[云端诊断]";

    /// <summary>
    /// 解析 fiction.scene.plan 的 HarnessResult。解析失败（Result 缺失/非 JSON/结构不符）
    /// 返回 null → 调用方降级本地路径（§3.6）。
    /// </summary>
    public static ScenePlanOutcome? ParseScenePlan(HarnessResult result, double passThreshold)
    {
        if (result.Result is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return null;

        // features.overallScore：云端 0-1 归一化分数 → 百分制（与 PassScoreThreshold 对齐）
        double score = 0;
        if (
            root.TryGetProperty("features", out var features)
            && features.ValueKind == JsonValueKind.Object
            && features.TryGetProperty("overall-score", out var scoreEl)
            && scoreEl.ValueKind == JsonValueKind.Number
        )
        {
            score = Math.Round(scoreEl.GetDouble() * 100, 1);
        }

        // repairIntent（Q1 定案）：{ action, requiresHumanDecision, reason?, reason-code?, focusAreas? }
        string? action = null;
        bool requiresHuman = false;
        string? reason = null;
        string? reasonCode = null;
        var focusAreas = Array.Empty<string>();
        if (
            root.TryGetProperty("repair-intent", out var intent)
            && intent.ValueKind == JsonValueKind.Object
        )
        {
            if (intent.TryGetProperty("action", out var actionEl))
                action = actionEl.GetString();
            if (
                intent.TryGetProperty("requires-human-decision", out var humanEl)
                && humanEl.ValueKind == JsonValueKind.True
            )
                requiresHuman = true;
            if (intent.TryGetProperty("reason", out var reasonEl))
                reason = reasonEl.GetString();
            if (intent.TryGetProperty("reason-code", out var reasonCodeEl))
                reasonCode = reasonCodeEl.GetString();
            if (
                intent.TryGetProperty("focus-areas", out var focusEl)
                && focusEl.ValueKind == JsonValueKind.Array
            )
            {
                focusAreas = focusEl
                    .EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? "")
                    .Where(s => s.Length > 0)
                    .ToArray();
            }
        }

        var kind =
            requiresHuman ? CloudSignalKind.Decision
            : score >= passThreshold ? CloudSignalKind.Pass
            : CloudSignalKind.Action;

        return new ScenePlanOutcome(
            kind,
            score,
            action,
            requiresHuman,
            reason,
            focusAreas,
            reasonCode
        );
    }

    /// <summary>PASS 单行信号（§4.3 模板）。</summary>
    public static string BuildPassMessage(ScenePlanOutcome outcome) =>
        $"{Prefix} status=pass 综合={outcome.OverallScore} 可按计划推进（本轮免重复诊断）";

    /// <summary>
    /// 解析 fiction.context.retrieve 结果（基础档；Q3 定案契约）。
    /// 解析失败返回 null → 调用方降级。
    /// </summary>
    public static ContextRetrieveOutcome? ParseContextRetrieve(HarnessResult result)
    {
        if (result.Result is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return null;

        double score = 0;
        if (
            root.TryGetProperty("features", out var features)
            && features.ValueKind == JsonValueKind.Object
            && features.TryGetProperty("overall-score", out var scoreEl)
            && scoreEl.ValueKind == JsonValueKind.Number
        )
        {
            score = Math.Round(scoreEl.GetDouble() * 100, 1); // 云端 0-1 → 百分制
        }

        var signals = new List<CloudSignalItem>();
        if (
            root.TryGetProperty("signals", out var signalsEl)
            && signalsEl.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var s in signalsEl.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object)
                    continue;
                signals.Add(
                    new CloudSignalItem(
                        s.TryGetProperty("dimension", out var d) ? d.GetString() ?? "" : "",
                        SignalRules.NormalizeSeverity(
                            s.TryGetProperty("severity", out var sev) ? sev.GetString() : null
                        ),
                        s.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                        s.TryGetProperty("suggestion", out var sug) ? sug.GetString() : null,
                        s.TryGetProperty("impact-score", out var imp)
                        && imp.ValueKind == JsonValueKind.Number
                            ? imp.GetDouble()
                            : 0,
                        s.TryGetProperty("confidence", out var conf)
                        && conf.ValueKind == JsonValueKind.Number
                            ? conf.GetDouble()
                            : 1.0,
                        s.TryGetProperty("suggested-action", out var sa) ? sa.GetString() : null
                    )
                );
            }
        }

        var dimensionScores = new Dictionary<string, double>();
        if (
            root.TryGetProperty("dimension-scores", out var dims)
            && dims.ValueKind == JsonValueKind.Object
        )
        {
            foreach (
                var prop in dims.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Number)
            )
                dimensionScores[prop.Name] = prop.Value.GetDouble();
        }

        var trends = ParseTrends(root);
        return new ContextRetrieveOutcome(
            score,
            signals,
            dimensionScores,
            trends,
            ParseAbsentCharacters(root),
            ParseMaterialHints(root)
        );
    }

    /// <summary>解析 context.retrieve 根部的 material-hints（缺失/空 prompt-block → null）。</summary>
    public static MaterialHintsOutcome? ParseMaterialHints(JsonElement root)
    {
        if (
            !root.TryGetProperty("material-hints", out var element)
            || element.ValueKind != JsonValueKind.Object
        )
        {
            return null;
        }
        var status = element.TryGetProperty("status", out var statusEl)
            ? statusEl.GetString() ?? ""
            : "";
        var promptBlock = element.TryGetProperty("prompt-block", out var promptEl)
            ? promptEl.GetString() ?? ""
            : "";
        return string.IsNullOrWhiteSpace(promptBlock)
            ? null
            : new MaterialHintsOutcome(status, promptBlock);
    }

    /// <summary>
    /// HARNESS-CLOUD Q13：从 context.retrieve 结果读取 features.absentCharacters
    /// （云端由 novel_features character_appearances 全书序列推导的最近 K 章未出场角色；
    /// 灵感方向①候选优先数据源，缺失返回空列表由客户端回退本地扫描）。
    /// </summary>
    public static IReadOnlyList<string> ParseAbsentCharacters(JsonElement root)
    {
        if (
            root.TryGetProperty("features", out var features)
            && features.ValueKind == JsonValueKind.Object
        )
            return ParseAbsentCharactersFromFeatures(features);
        return [];
    }

    /// <summary>从 features 对象直接读取 absentCharacters（快照路径复用；root 路径经 ParseAbsentCharacters）。</summary>
    public static IReadOnlyList<string> ParseAbsentCharactersFromFeatures(JsonElement features)
    {
        if (
            features.ValueKind == JsonValueKind.Object
            && features.TryGetProperty("absent-characters", out var absent)
            && absent.ValueKind == JsonValueKind.Array
        )
        {
            return absent
                .EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }
        return [];
    }

    /// <summary>
    /// HARNESS-CLOUD §15.1/§15.5 信息透传：从 features.trends 提取下降/上升趋势摘要，
    /// 并附 云端成本元数据。仅作信息性，不触发处方。
    /// </summary>
    private static IReadOnlyList<string>? ParseTrends(JsonElement root)
    {
        var notes = new List<string>();
        if (
            root.TryGetProperty("features", out var features)
            && features.ValueKind == JsonValueKind.Object
            && features.TryGetProperty("trends", out var trends)
            && trends.ValueKind == JsonValueKind.Object
        )
        {
            foreach (var prop in trends.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (
                    !prop.Value.TryGetProperty("direction", out var dirEl)
                    || dirEl.ValueKind != JsonValueKind.String
                )
                    continue;
                var direction = dirEl.GetString();
                if (direction == CloudContractLiterals.TrendDeclining)
                    notes.Add($"云端趋势：{prop.Name} 连续 {ReadWindow(prop.Value)} 章走低");
                else if (direction == CloudContractLiterals.TrendRising)
                    notes.Add($"云端趋势：{prop.Name} 连续 {ReadWindow(prop.Value)} 章走高");
            }
        }
        if (
            root.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("total-compute-units", out var units)
            && units.ValueKind == JsonValueKind.Number
        )
        {
            notes.Add($"本次诊断消耗 {units.GetDouble()} units");
        }
        return notes.Count == 0 ? null : notes;

        static string ReadWindow(JsonElement trend)
        {
            return trend.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Number
                ? w.GetInt32().ToString()
                : "?";
        }
    }

    /// <summary>
    /// 解析 fiction.story.check 结果（深度档；23 键 dimensions，camelCase）。
    /// network.characterPageRank 单章数据缺失时为 null（A-1/A-2 容错跳过）。
    /// </summary>
    public static StoryCheckOutcome? ParseStoryCheck(HarnessResult result)
    {
        if (result.Result is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return null;
        if (
            !root.TryGetProperty("dimensions", out var dimensions)
            || dimensions.ValueKind != JsonValueKind.Object
        )
            return null;

        NetworkPageRank? pageRank = null;
        if (
            dimensions.TryGetProperty("network", out var network)
            && network.ValueKind == JsonValueKind.Object
            && network.TryGetProperty("character-page-rank", out var cpr)
            && cpr.ValueKind == JsonValueKind.Object
        )
        {
            pageRank = new NetworkPageRank(
                TopCharacter: cpr.TryGetProperty("top-character", out var top)
                    ? top.GetString()
                    : null,
                N1ProtagonistEclipsed: cpr.TryGetProperty("n1-protagonist-eclipsed", out var n1)
                    && n1.ValueKind == JsonValueKind.True,
                N2NetworkConcentrated: cpr.TryGetProperty("n2-network-concentrated", out var n2)
                    && n2.ValueKind == JsonValueKind.True
            );
        }

        return new StoryCheckOutcome
        {
            Dimensions = dimensions.Clone(),
            PageRank = pageRank,
            RuleFindings = ParseRuleFindings(root),
            ContinuityAudit = ParseContinuityAudit(root),
            PlanConformance = ParsePlanConformance(root),
        };
    }

    /// <summary>
    /// 顶层 rule-findings[]（规则引擎 findings，服务端 standard/deep 才产出）。
    /// 容忍缺失/畸形条目（按 key 读、未知键忽略；旧服务端无该键 → 空表）。
    /// </summary>
    private static IReadOnlyList<StoryRuleFinding> ParseRuleFindings(JsonElement root)
    {
        if (
            !root.TryGetProperty("rule-findings", out var array)
            || array.ValueKind != JsonValueKind.Array
        )
            return [];
        var findings = new List<StoryRuleFinding>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            findings.Add(
                new StoryRuleFinding(
                    Dimension: ReadString(item, "dimension") ?? "",
                    Severity: ReadString(item, "severity") ?? "",
                    Description: ReadString(item, "description") ?? "",
                    Evidence: ReadString(item, "evidence"),
                    Suggestion: ReadString(item, "suggestion"),
                    ImpactScore: ReadNumber(item, "impact-score")
                )
            );
        }
        return findings;
    }

    /// <summary>顶层 continuity-audit（findings + revision-candidates）；键缺席 → null。</summary>
    private static ContinuityAuditView? ParseContinuityAudit(JsonElement root)
    {
        if (
            !root.TryGetProperty("continuity-audit", out var audit)
            || audit.ValueKind != JsonValueKind.Object
        )
            return null;

        var findings = new List<ContinuityFindingView>();
        if (
            audit.TryGetProperty("findings", out var findingsJson)
            && findingsJson.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var item in findingsJson.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                findings.Add(
                    new ContinuityFindingView(
                        Kind: ReadString(item, "kind") ?? "",
                        Rule: ReadString(item, "rule") ?? "",
                        Severity: ReadString(item, "severity") ?? "",
                        Chapters: ReadStringArray(item, "chapters"),
                        Description: ReadString(item, "description") ?? "",
                        Evidence: ReadString(item, "evidence"),
                        Suggestion: ReadString(item, "suggestion"),
                        ImpactScore: ReadNumber(item, "impact-score")
                    )
                );
            }
        }

        var revisionChapters = new List<string>();
        if (
            audit.TryGetProperty("revision-candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (
                    candidate.ValueKind == JsonValueKind.Object
                    && ReadString(candidate, "chapter") is { Length: > 0 } chapter
                )
                    revisionChapters.Add(chapter);
            }
        }

        return findings.Count == 0 && revisionChapters.Count == 0
            ? null
            : new ContinuityAuditView(findings, revisionChapters);
    }

    /// <summary>顶层 plan-conformance（键缺席/计划为空 → null）。</summary>
    private static PlanConformanceView? ParsePlanConformance(JsonElement root)
    {
        if (
            !root.TryGetProperty("plan-conformance", out var plan)
            || plan.ValueKind != JsonValueKind.Object
        )
            return null;
        return new PlanConformanceView(
            PlannedBeatCount: (int)ReadNumber(plan, "planned-beat-count"),
            MissedBeatCount: (int)ReadNumber(plan, "missed-beat-count")
        );
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double ReadNumber(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(text => text.Length > 0)
            .ToList();
    }

    /// <summary>三段式 ACTION 信号组装（§4.3 模板；≤3 条行动 + Warnings 透传段）。</summary>
    public static string FormatAction(double overallScore, RuleOutcome ruleOutcome)
    {
        var text = $"{Prefix} status=action 综合={overallScore}";
        foreach (var action in ruleOutcome.Actions)
        {
            text +=
                $"\n证据[{action.Code}]: {OneLine(action.Evidence)}"
                + $"\n处方: {OneLine(action.Prescription)}"
                + $"\n边界: {OneLine(action.Boundary)}";
        }
        if (ruleOutcome.PassthroughWarnings.Count > 0)
            text += $"\n备注: {string.Join("；", ruleOutcome.PassthroughWarnings.Select(OneLine))}";
        return text;
    }

    /// <summary>
    /// U-4：段内自由文本单行化——云端证据/处方/边界可能含换行，直拼会伪造出
    /// 「处方:」「边界:」等行首标记，破坏 LLM 消费的分段结构；空白折叠为单空格。
    /// </summary>
    private static string OneLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
