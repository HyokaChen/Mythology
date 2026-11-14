using System.Text.Json;
using Core.Cloud;
using Core.Shared;

namespace Plugins.WritingTools;

/// <summary>
/// 把 fiction.story.check 的 dimensions 结果映射为本地 DiagnosticReport。
/// 后端已收敛为单一 opaque 能力，旧 features/dimensionScores/signals 不再出现。
/// </summary>
internal static class StoryCheckResultMapper
{
    /// <summary>
    /// story.check 维度分组 → 本地诊断维度键 + 分数属性。
    /// </summary>
    private static readonly (string Group, string Key, string ScoreProperty)[] ScoreMap =
    [
        ("immersion", DimensionKeys.Immersion, "overall-score"),
        ("payoff", DimensionKeys.Satisfaction, "overall-score"),
        ("tension", DimensionKeys.Conflict, "overall-score"),
        ("pace", DimensionKeys.Pacing, "overall-score"),
        ("description", DimensionKeys.Description, "overall-score"),
        ("dialogue", DimensionKeys.Dialogue, "overall-score"),
        ("threads", DimensionKeys.Foreshadowing, "overall-score"),
        ("opening", DimensionKeys.Opening, "overall-score"),
        ("plausibility", DimensionKeys.Mistake, "overall-score"),
        ("excitement", DimensionKeys.Excitement, "excitement-score"),
        ("engagement", DimensionKeys.ReaderEngagement, "overall-engagement"),
    ];

    /// <summary>把服务端公开报告映射为工具详情；不再读取原始 dimensions。</summary>
    public static DiagnosticReport Map(DiagnosisReport report, DiagnosticDepth depth)
    {
        var issues = report
            .Findings.Select(finding => new DiagnosticIssue
            {
                Dimension = finding.Category == "文字" ? DimensionKeys.Humanizer : finding.Category,
                Severity = finding.Severity switch
                {
                    "high" => Severity.High,
                    "medium" => Severity.Medium,
                    _ => Severity.Low,
                },
                Description = finding.Problem,
                Evidence = finding.Evidence ?? "",
                Suggestion = finding.Suggestion,
            })
            .ToList();
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        if (report.Score is { } score)
            scores["综合"] = score;
        if (depth == DiagnosticDepth.Quick && issues.Count > 0)
            scores[DimensionKeys.Humanizer] = 0;

        return new DiagnosticReport
        {
            Depth = depth,
            OverallScore = report.Score ?? 0,
            DimensionScores = scores,
            Issues = issues,
            Strengths = report.Strengths?.ToList() ?? [],
        };
    }

    public static DiagnosticReport Map(JsonElement root, DiagnosticDepth depth)
    {
        var scores = new Dictionary<string, double>();
        var issues = new List<DiagnosticIssue>();
        var strengths = new List<string>();

        if (
            !root.TryGetProperty("dimensions", out var dimensions)
            || dimensions.ValueKind != JsonValueKind.Object
        )
        {
            return new DiagnosticReport { Depth = depth };
        }

        foreach (var (group, key, scoreProperty) in ScoreMap)
        {
            if (!dimensions.TryGetProperty(group, out var dim))
                continue;

            var score = dim.ValueKind == JsonValueKind.Object ? GetScore(dim, scoreProperty) : 0;

            if (dim.ValueKind == JsonValueKind.Object)
                AddIssues(issues, dim, key);
            else if (dim.ValueKind == JsonValueKind.Array)
                AddIssues(issues, dim, key);

            scores[key] = score;
        }

        // characters 是数组：取所有角色 arc 完整度的均值，作为“角色弧”维度分
        if (
            dimensions.TryGetProperty("characters", out var characters)
            && characters.ValueKind == JsonValueKind.Array
        )
        {
            var arcScores = new List<double>();
            foreach (var item in characters.EnumerateArray())
            {
                if (
                    item.TryGetProperty("arc", out var arc)
                    && arc.ValueKind == JsonValueKind.Object
                )
                {
                    arcScores.Add(GetScore(arc, "arc-completeness"));
                    AddIssues(issues, arc, DimensionKeys.CharacterArc);
                }
                if (item.TryGetProperty("completeness", out var completeness))
                    AddIssues(issues, completeness, DimensionKeys.CharacterArc);
            }

            if (arcScores.Count > 0)
                scores[DimensionKeys.CharacterArc] = arcScores.Average();
        }

        // 汇总所有分组里的 strengths（对象维度）
        foreach (var group in dimensions.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (
                group.Value.TryGetProperty("strengths", out var arr)
                && arr.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            strengths.Add(text);
                    }
                }
            }
        }

        AddNetworkIssues(issues, strengths, dimensions);

        // humanizer 不计入历史 OverallScore（避免既有综合分漂移）；在算完综合分后
        // 单独挂到 DimensionScores/Issues，供 diagnose_writing(mode=deai) 与增强使用。
        var overall = scores.Count > 0 ? scores.Values.Average() : 0;
        if (dimensions.TryGetProperty("humanizer", out var humanizer))
        {
            scores[DimensionKeys.Humanizer] = GetScore(humanizer, "overall-score");
            AddIssues(issues, humanizer, DimensionKeys.Humanizer);
        }

        return new DiagnosticReport
        {
            Depth = depth,
            OverallScore = overall,
            DimensionScores = scores,
            Issues = issues,
            Strengths = strengths,
        };
    }

    private static void AddNetworkIssues(
        List<DiagnosticIssue> issues,
        List<string> strengths,
        JsonElement dimensions
    )
    {
        if (
            !dimensions.TryGetProperty("network", out var network)
            || network.ValueKind != JsonValueKind.Object
        )
            return;

        if (
            network.TryGetProperty("character-page-rank", out var pr)
            && pr.ValueKind == JsonValueKind.Object
        )
        {
            var topCharacter = pr.TryGetProperty("top-character", out var top)
                ? top.GetString() ?? ""
                : "";
            var protagonistRank = pr.TryGetProperty("protagonist-rank", out var rank)
                ? rank.GetInt32()
                : 0;
            if (
                pr.TryGetProperty("n1-protagonist-eclipsed", out var n1)
                && n1.ValueKind == JsonValueKind.True
            )
                issues.Add(
                    new DiagnosticIssue
                    {
                        Dimension = DimensionKeys.CharacterArc,
                        Severity = Severity.High,
                        Description =
                            $"PageRank 榜首为「{topCharacter}」（主角排名第 {protagonistRank}），配角喧宾夺主",
                    }
                );
            if (
                pr.TryGetProperty("n2-network-concentrated", out var n2)
                && n2.ValueKind == JsonValueKind.True
            )
                issues.Add(
                    new DiagnosticIssue
                    {
                        Dimension = DimensionKeys.CharacterArc,
                        Severity = Severity.Medium,
                        Description = "PageRank 网络过度集中，单角色垄断结构，群像有崩塌风险",
                    }
                );
        }

        if (
            network.TryGetProperty("chapter-hubs", out var hubs)
            && hubs.ValueKind == JsonValueKind.Object
            && hubs.TryGetProperty("n4-hub-poverty", out var n4)
            && n4.ValueKind == JsonValueKind.True
        )
            issues.Add(
                new DiagnosticIssue
                {
                    Dimension = DimensionKeys.Pacing,
                    Severity = Severity.Medium,
                    Description = "章节枢纽枯竭：缺少结构性锚点章，存在流水账风险",
                }
            );

        if (
            network.TryGetProperty("foreshadowing-network", out var fs)
            && fs.ValueKind == JsonValueKind.Object
            && fs.TryGetProperty("n5-plotline-drift", out var n5)
            && n5.ValueKind == JsonValueKind.True
        )
            issues.Add(
                new DiagnosticIssue
                {
                    Dimension = DimensionKeys.Foreshadowing,
                    Severity = Severity.Low,
                    Description = "支线漂移：支线伏笔 PR 超过主线，暗线悄悄变主线",
                }
            );

        if (
            network.TryGetProperty("protagonist-arc", out var arc)
            && arc.ValueKind == JsonValueKind.Object
        )
        {
            var declineChapters = arc.TryGetProperty("sliding-decline-chapters", out var decline)
                ? decline.GetInt32()
                : 0;
            if (
                arc.TryGetProperty("n3-protagonist-marginalized", out var n3)
                && n3.ValueKind == JsonValueKind.True
            )
                issues.Add(
                    new DiagnosticIssue
                    {
                        Dimension = DimensionKeys.CharacterArc,
                        Severity = Severity.High,
                        Description = $"主角 PR 排名连续 {declineChapters} 章下滑，存在边缘化风险",
                    }
                );
        }

        if (
            network.TryGetProperty("golden-sentences", out var golden)
            && golden.ValueKind == JsonValueKind.Object
        )
        {
            var density = golden.TryGetProperty("golden-sentence-density-per-thousand", out var d)
                ? d.GetDouble()
                : 0;
            var hook = golden.TryGetProperty("last-sentence-hook-score", out var h)
                ? h.GetDouble()
                : 0;
            if (density > 0)
                strengths.Add($"金句密度 {density:F2} 句/千字");
            if (hook > 0)
                strengths.Add($"章末钩子势能 {hook:F2}");
        }
    }

    private static double GetScore(JsonElement dim, string property)
    {
        if (
            dim.ValueKind == JsonValueKind.Object
            && dim.TryGetProperty(property, out var score)
            && score.ValueKind == JsonValueKind.Number
        )
            return score.GetDouble();
        return 0;
    }

    private static void AddIssues(
        List<DiagnosticIssue> issues,
        JsonElement dimensionOrArray,
        string dimensionKey
    )
    {
        var array =
            dimensionOrArray.ValueKind == JsonValueKind.Array ? dimensionOrArray
            : dimensionOrArray.ValueKind == JsonValueKind.Object
            && dimensionOrArray.TryGetProperty("issues", out var issuesProp)
                ? issuesProp
            : default;

        if (array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            issues.Add(
                new DiagnosticIssue
                {
                    Dimension = dimensionKey,
                    SubType = item.TryGetProperty("sub-type", out var subType)
                        ? subType.GetString()
                        : null,
                    Level = item.TryGetProperty("level", out var level) ? level.GetString() : null,
                    Severity = item.TryGetProperty("severity", out var severity)
                        ? severity.GetString() ?? ""
                        : "",
                    Description = item.TryGetProperty("description", out var description)
                        ? description.GetString() ?? ""
                        : "",
                    Evidence = item.TryGetProperty("evidence", out var evidence)
                        ? evidence.GetString() ?? ""
                        : "",
                    Suggestion = item.TryGetProperty("suggestion", out var suggestion)
                        ? suggestion.GetString()
                        : null,
                    ParagraphIndex = item.TryGetProperty("paragraph-index", out var para)
                        ? para.GetInt32()
                        : -1,
                    ImpactScore = item.TryGetProperty("impact-score", out var impact)
                        ? impact.GetDouble()
                        : 0,
                }
            );
        }
    }
}
