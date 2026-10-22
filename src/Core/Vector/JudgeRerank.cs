using System.Text.Json;
using Core.Inference;

namespace Core.Vector;

/// <summary>
/// 重排结果：<see cref="Results"/> 为（可能重排后的）列表；
/// <see cref="Aborted"/> 表示首个判定请求失败、本次判定已整体中止（列表=原列表）。
/// </summary>
public readonly record struct JudgeRerankOutcome(
    List<RetrievalSearchResult> Results,
    int Judged,
    bool Aborted
);

/// <summary>
/// 本地判定重排（jegrep 提炼）：RRF 融合后的候选交给 SystemOne 逐条 noul 判定，
/// 按概率对融合分数做乘性调节后重排。
///
/// 安全语义（不可破坏）：
/// - 只重排不丢弃：失败/缺答案/未判定的条目**保持原分**（"失败是未知，绝不构成负判"）。
/// - 首个请求失败 → 本次判定整体中止，返回原列表（调用方记警告）。
/// - UserMemory 域永不参与（显式规则遗漏率必须为 0）。
/// </summary>
public static class JudgeRerank
{
    private const string YesCriteria =
        "该段落含有与检索意图直接相关的具体内容（设定/事件/对白/描写/事实）；部分相关也算。";
    private const string NoCriteria = "与检索意图无关，或仅有无关紧要的字面词重叠。";

    /// <summary>判定是否适用于当前请求。</summary>
    public static bool Applies(ISystemOneClient? client, RetrievalSearchRequest request) =>
        client is { Enabled: true }
        && client.Options.Rerank.Enabled
        && request.Domain != RetrievalDomain.UserMemory
        && !string.IsNullOrWhiteSpace(request.Query)
        && request.FinalTopK > 0;

    /// <summary>候选放大倍数（1 = 不放大；clamp 1..5）。</summary>
    public static int Overfetch(ISystemOneClient client) =>
        Math.Clamp(client.Options.Rerank.Overfetch, 1, 5);

    /// <summary>判定并重排；任何失败回退原列表（绝不抛）。</summary>
    public static async Task<JudgeRerankOutcome> ApplyAsync(
        ISystemOneClient client,
        string query,
        List<RetrievalSearchResult> results,
        CancellationToken ct
    )
    {
        if (results.Count <= 1)
            return new JudgeRerankOutcome(results, Judged: 0, Aborted: false);

        var options = client.Options.Rerank;
        var maxCandidates = Math.Clamp(options.MaxCandidates, 1, results.Count);
        var passageChars = Math.Max(options.PassageChars, 32);
        var candidates = new List<Passage>(maxCandidates);
        for (var i = 0; i < maxCandidates; i++)
        {
            var result = results[i];
            candidates.Add(
                new Passage(
                    i,
                    $"p{i:00}",
                    Truncate(result.Text, passageChars),
                    FormatChapter(result)
                )
            );
        }

        var budget = Math.Max(client.Options.MaxStateChars, 256);
        var batches = SystemOnePacking.Pack(candidates, budget, RenderPassage);

        var probabilities = new Dictionary<int, double>();
        foreach (var batch in batches)
        {
            var response = await client.AskAsync(
                BuildState(query, batch),
                BuildQuestions(batch),
                ct
            );
            if (response is null)
                // 首个失败 → 本次判定整体中止（其余批不发），返回原列表
                return new JudgeRerankOutcome(results, Judged: 0, Aborted: true);

            foreach (var passage in batch)
            {
                if (response.Noul(passage.Key) is { } p)
                    probabilities[passage.Index] = p;
            }
        }

        if (probabilities.Count == 0)
            return new JudgeRerankOutcome(results, Judged: 0, Aborted: false);

        var weight = Math.Clamp(options.Weight, 0, 1);
        var blended = new List<RetrievalSearchResult>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (!probabilities.TryGetValue(i, out var p))
            {
                blended.Add(result); // 未判定：保持原分、不置判定概率
                continue;
            }
            var factor = 1 + weight * (2 * p - 1); // w=0.5 → 值域 [0.5, 1.5]
            blended.Add(result with { Score = result.Score * factor, JudgeProbability = p });
        }

        // 稳定排序：同分保持原相对序
        return new JudgeRerankOutcome(
            blended.OrderByDescending(r => r.Score).ToList(),
            probabilities.Count,
            Aborted: false
        );
    }

    private static Dictionary<string, object?> BuildState(string query, List<Passage> batch)
    {
        var passages = new Dictionary<string, object?>(batch.Count, StringComparer.Ordinal);
        foreach (var passage in batch)
        {
            passages[passage.Key] = new Dictionary<string, object?>
            {
                ["text"] = passage.Text,
                ["chapter"] = passage.Chapter,
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["task"] = query,
            ["criteria"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["yes"] = YesCriteria,
                ["no"] = NoCriteria,
            },
            ["passages"] = passages,
        };
    }

    private static Dictionary<string, SystemOneQuestion> BuildQuestions(List<Passage> batch)
    {
        var questions = new Dictionary<string, SystemOneQuestion>(
            batch.Count,
            StringComparer.Ordinal
        );
        foreach (var passage in batch)
        {
            questions[passage.Key] = new NoulQuestion
            {
                Instructions =
                    $"段落 {passage.Key} 是否应作为该检索意图的上下文素材？按 criteria 判断。",
            };
        }
        return questions;
    }

    private static string RenderPassage(Passage passage) =>
        string.Concat(
            "{\"text\":",
            JsonSerializer.Serialize(passage.Text),
            ",\"chapter\":",
            JsonSerializer.Serialize(passage.Chapter),
            "}"
        );

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static string? FormatChapter(RetrievalSearchResult result) =>
        result.ChapterOrder is { } order
            ? string.IsNullOrEmpty(result.ChapterTitle)
                ? $"第{order}章"
                : $"第{order}章 {result.ChapterTitle}"
            : null;

    private sealed record Passage(int Index, string Key, string Text, string? Chapter);
}
