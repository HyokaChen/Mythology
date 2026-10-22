using System.Text.Json;
using System.Text.RegularExpressions;
using Core.Inference;

namespace Core.Vector;

/// <summary>
/// 中文冲突检测：移植 remembrane 的确定性启发式，适配中文小说场景。
///
/// 检测方式（纯字符串运算，不依赖 LLM）：
/// - 锚词重叠：两段记忆共享的内容词比例
/// - 否定标记：不/没/不再/从未 等强否定词
/// - 数值不匹配：阿拉伯数字或中文数字不同
/// - 值替换：同一模板中一个插槽被替换（"武器是长剑"→"武器是弯刀"）
/// - 封闭类：星期/月份/季节等值分类不匹配
///
/// 两级置信度：
/// - "likely"：有强否定标记 / 数值不匹配 + 剩余词相同 / 值替换
/// - "possible"：仅锚词重叠 + 弱标记
///
/// 返回结构化证据，由 LLM 做最终判断（是 bug 还是 feature）。
/// </summary>
public static partial class AgentMemoryConflict
{
    // 分词正则
    private static readonly Regex WordRe = MyRegex();
    private static readonly Regex NumRe = NumRegex();

    // 强否定标记：单独出现即可认定 likely
    private static readonly HashSet<string> StrongMarkers =
    [
        "不",
        "没",
        "没有",
        "别",
        "不要",
        "不用",
        "不必",
        "不再",
        "从未",
        "未曾",
        "尚未",
        "毫不",
        "毫无",
        "并非",
        "不是",
        "不能",
        "不会",
        "不可能",
        "拒绝",
        "停止",
        "取消",
        "废除",
        "撤销",
        "否认",
        "否定",
        "反对",
        "禁止",
        "曾经",
        "以前",
        "原先",
        "原来",
        "过去",
        "后来",
        "之后",
        "现在",
        "如今",
        "目前",
    ];

    // 弱变化标记：配合数值不匹配时升级为 likely
    private static readonly HashSet<string> WeakMarkers =
    [
        "现在",
        "新",
        "旧",
        "原来",
        "原本",
        "变了",
        "变成",
        "变为",
        "改成",
        "改为",
        "换成",
        "换为",
        "以前",
        "之前",
        "后来",
        "之后",
        "曾经",
        "开始",
        "结束",
        "继续",
        "停止",
        "仍然",
        "依旧",
        "依然",
        "还是",
    ];

    private static readonly HashSet<string> AllMarkers = [.. StrongMarkers, .. WeakMarkers];

    // 值分类（封闭类）
    private static readonly HashSet<string>[] ValueCategories =
    [
        // 星期
        new(StringComparer.OrdinalIgnoreCase)
        {
            "星期一",
            "星期二",
            "星期三",
            "星期四",
            "星期五",
            "星期六",
            "星期日",
            "周日",
            "周六",
            "周天",
        },
        // 月份
        new(StringComparer.OrdinalIgnoreCase)
        {
            "一月",
            "二月",
            "三月",
            "四月",
            "五月",
            "六月",
            "七月",
            "八月",
            "九月",
            "十月",
            "十一月",
            "十二月",
            "1月",
            "2月",
            "3月",
            "4月",
            "5月",
            "6月",
            "7月",
            "8月",
            "9月",
            "10月",
            "11月",
            "12月",
        },
        // 季节
        new(StringComparer.OrdinalIgnoreCase)
        {
            "春",
            "夏",
            "秋",
            "冬",
            "春天",
            "夏天",
            "秋天",
            "冬天",
        },
        // 方向
        new(StringComparer.OrdinalIgnoreCase)
        {
            "东",
            "南",
            "西",
            "北",
            "东方",
            "南方",
            "西方",
            "北方",
            "上",
            "下",
            "左",
            "右",
            "前",
            "后",
        },
    ];

    // 中文停用词（internal：AgentMemoryBm25 分词过滤复用同一表，避免两份漂移）
    internal static readonly HashSet<string> Stopwords =
    [
        "的",
        "了",
        "是",
        "在",
        "有",
        "和",
        "就",
        "不",
        "人",
        "都",
        "一",
        "一个",
        "上",
        "也",
        "很",
        "到",
        "说",
        "要",
        "去",
        "你",
        "会",
        "着",
        "没有",
        "看",
        "好",
        "自己",
        "这",
        "他",
        "她",
        "它",
        "们",
        "我",
        "我们",
        "你们",
        "他们",
        "她们",
        "它们",
        "那",
        "这个",
        "那个",
        "这些",
        "那些",
        "什么",
        "怎么",
        "如何",
        "为什么",
        "因为",
        "所以",
        "但是",
        "然而",
        "虽然",
        "如果",
        "而且",
        "或者",
        "还是",
        "把",
        "被",
        "让",
        "给",
        "对",
        "对于",
        "关于",
        "按照",
        "根据",
        "除了",
        "已经",
        "可以",
        "能够",
        "应该",
        "必须",
        "需要",
        "可能",
        "也许",
        "还",
        "更",
        "最",
        "太",
        "非常",
        "十分",
        "比较",
        "相当",
        "将",
        "把",
        "从",
        "以",
        "向",
        "往",
        "在",
        "当",
        "于",
        "the",
        "a",
        "an",
        "is",
        "are",
        "was",
        "were",
        "be",
        "been",
        "to",
        "of",
        "in",
        "on",
        "at",
        "for",
        "and",
        "or",
        "it",
        "its",
        "this",
        "that",
        "with",
        "by",
        "from",
        "as",
        "has",
        "have",
        "had",
        "user",
        "prefers",
        "prefer",
        "like",
        "likes",
    ];

    /// <summary>
    /// 检测记忆列表中的冲突对。
    /// </summary>
    /// <param name="memories">记忆列表。</param>
    /// <param name="vectors">对应的向量列表（用于余弦相似度计算）。</param>
    /// <param name="similarityFloor">余弦相似度下限（低于此不相关）。</param>
    /// <param name="similarityCeiling">余弦相似度上限（高于此是重复，不是冲突）。</param>
    /// <param name="minAnchorOverlap">最小锚词重叠率。</param>
    /// <returns>冲突列表，按置信度排序（likely 优先）。</returns>
    public static List<Conflict> DetectConflicts(
        List<MemoryRecord> memories,
        List<float[]> vectors,
        double similarityFloor = 0.35,
        double similarityCeiling = 0.92,
        double minAnchorOverlap = 0.15
    )
    {
        var conflicts = new List<Conflict>();
        if (memories.Count < 2)
            return conflicts;

        // 预处理：提取每段记忆的内容词和数字
        var words = new List<HashSet<string>>(memories.Count);
        var numbers = new List<HashSet<string>>(memories.Count);

        foreach (var mem in memories)
        {
            words.Add(ContentWords(mem.Content));
            numbers.Add(ExtractNumbers(mem.Content));
        }

        for (var i = 0; i < memories.Count; i++)
        {
            for (var j = i + 1; j < memories.Count; j++)
            {
                // 不同命名空间不比较
                if (memories[i].Namespace != memories[j].Namespace)
                    continue;

                // 余弦相似度过滤
                var vecA = vectors.Count > i ? vectors[i] : null;
                var vecB = vectors.Count > j ? vectors[j] : null;
                double sim;
                if (vecA is not null && vecB is not null && vecA.Length == vecB.Length)
                    sim = AgentMemoryScoring.CosineSimilarity(vecA, vecB);
                else
                    sim = 0.5; // 无向量时假设中等相似度

                if (sim < similarityFloor || sim >= similarityCeiling)
                    continue;

                // 锚词重叠率
                var union = new HashSet<string>(words[i]);
                union.UnionWith(words[j]);
                if (union.Count == 0)
                    continue;

                var overlap = (double)words[i].Intersect(words[j]).Count() / union.Count;
                if (overlap < minAnchorOverlap)
                    continue;

                // 检测信号
                var signals = new List<string> { $"anchor_overlap={overlap:F2}" };
                var confidence = "possible";
                var anchors = new HashSet<string>(words[i]);
                anchors.IntersectWith(words[j]);

                // 检测否定标记/变化标记
                var strong = new HashSet<string>();
                var weak = new HashSet<string>();

                foreach (var mem in new[] { memories[i], memories[j] })
                {
                    var tokens = WordRe
                        .Matches(mem.Content.ToLowerInvariant())
                        .Select(m => m.Value)
                        .ToList();

                    for (var idx = 0; idx < tokens.Count; idx++)
                    {
                        var tok = tokens[idx];
                        if (!AllMarkers.Contains(tok))
                            continue;

                        // 检查标记词是否在锚词窗口内（前后 3 个词）
                        var start = Math.Max(0, idx - 3);
                        var end = Math.Min(tokens.Count, idx + 4);
                        var window = new HashSet<string>(tokens.Skip(start).Take(end - start));
                        if (anchors.Overlaps(window))
                        {
                            if (StrongMarkers.Contains(tok))
                                strong.Add(tok);
                            else
                                weak.Add(tok);
                        }
                    }
                }

                // 检测数值不匹配
                var hasNumbers =
                    numbers[i].Count > 0
                    && numbers[j].Count > 0
                    && !numbers[i].SetEquals(numbers[j]);

                // 检测值分类不匹配
                var valueMismatch = false;
                foreach (var cat in ValueCategories)
                {
                    var vi = words[i].Intersect(cat).ToHashSet();
                    var vj = words[j].Intersect(cat).ToHashSet();
                    if (vi.Count > 0 && vj.Count > 0 && !vi.SetEquals(vj))
                    {
                        valueMismatch = true;
                        signals.Add(
                            $"value_mismatch={string.Join(",", vi)}vs{string.Join(",", vj)}"
                        );
                        break;
                    }
                }

                var numericMismatch = hasNumbers || valueMismatch;

                // 剩余词相同检测（用于数值不匹配升级）
                var remainderEqual = new HashSet<string>(words[i].Except(numbers[i])).SetEquals(
                    new HashSet<string>(words[j].Except(numbers[j]))
                );

                // 值替换检测
                var substitution = DetectValueSubstitution(
                    memories[i].Content,
                    memories[j].Content,
                    words[i],
                    words[j]
                );

                // 组合置信度规则
                if (strong.Count > 0)
                {
                    confidence = "likely";
                    signals.Add($"change_markers={string.Join(",", strong.Order())}");
                }

                if (numericMismatch)
                {
                    if (hasNumbers)
                        signals.Add(
                            $"numeric_mismatch={string.Join(",", numbers[i].Order())}vs{string.Join(",", numbers[j].Order())}"
                        );
                    if (remainderEqual || weak.Count > 0)
                        confidence = "likely";
                }

                if (substitution.HasValue)
                {
                    confidence = "likely";
                    signals.Add(
                        $"substitution={string.Join(",", substitution.Value.DiffI)}vs{string.Join(",", substitution.Value.DiffJ)}"
                    );
                }

                if (weak.Count > 0)
                    signals.Add($"weak_markers={string.Join(",", weak.Order())}");

                // 弱锚词重叠 + 仅 possible 时，需要更高重叠率才报告
                if (confidence == "possible" && overlap < 2 * minAnchorOverlap)
                    continue;

                conflicts.Add(
                    new Conflict
                    {
                        Confidence = confidence,
                        MemoryA = memories[i],
                        MemoryB = memories[j],
                        Reason = string.Join("; ", signals),
                        ChangeMarkers = [.. strong, .. weak],
                    }
                );
            }
        }

        // 排序：likely 优先，然后按相似度降序
        conflicts.Sort(
            (a, b) =>
            {
                var cmp = string.Compare(a.Confidence, b.Confidence, StringComparison.Ordinal);
                return cmp != 0
                    ? cmp
                    : // "likely" < "possible" 字典序，但我们要 likely 优先
                    0;
            }
        );
        // 由于 "likely" < "possible"，反转使 likely 优先
        conflicts.Reverse();
        return conflicts;
    }

    /// <summary>
    /// 提取文本中的内容词（jieba 分词后过滤停用词）。
    /// </summary>
    public static HashSet<string> ContentWords(string text)
    {
        var tokens = AgentMemoryBm25.Tokenize(text);
        var words = new HashSet<string>(tokens.Count);
        foreach (var t in tokens.Where(t => !Stopwords.Contains(t) && t.Length > 0))
        {
            words.Add(t);
        }
        return words;
    }

    /// <summary>
    /// 提取文本中的数字（阿拉伯数字 + 中文数字）。
    /// </summary>
    public static HashSet<string> ExtractNumbers(string text)
    {
        var matches = NumRe.Matches(text);
        var numbers = new HashSet<string>(matches.Count);
        foreach (Match m in matches)
            numbers.Add(m.Value);
        return numbers;
    }

    /// <summary>
    /// 值替换检测：两段记忆共享同一模板，仅一个插槽被替换。
    /// 如 "武器是长剑" → "武器是弯刀"，模板 "武器是" 相同。
    /// </summary>
    private static (HashSet<string> DiffI, HashSet<string> DiffJ)? DetectValueSubstitution(
        string contentI,
        string contentJ,
        HashSet<string> wordsI,
        HashSet<string> wordsJ
    )
    {
        var diffI = new HashSet<string>(wordsI.Except(wordsJ));
        var diffJ = new HashSet<string>(wordsJ.Except(wordsI));

        // 必须恰好 1-2 个不同词
        if (diffI.Count < 1 || diffI.Count > 2 || diffJ.Count < 1 || diffJ.Count > 2)
            return null;
        if (diffI.Count != diffJ.Count)
            return null;

        // 模板比较：去掉 diff 词后，剩余 token 序列必须相同
        var toksI = WordRe.Matches(contentI.ToLowerInvariant()).Select(m => m.Value).ToList();
        var toksJ = WordRe.Matches(contentJ.ToLowerInvariant()).Select(m => m.Value).ToList();

        var templateI = toksI.Where(t => !diffI.Contains(t)).ToList();
        var templateJ = toksJ.Where(t => !diffJ.Contains(t)).ToList();

        if (!templateI.SequenceEqual(templateJ))
            return null;

        // diff 词必须连续（不能分散）
        if (!IsContiguous(toksI, diffI) || !IsContiguous(toksJ, diffJ))
            return null;

        // diff 词不能是句首词（句首词通常是不同主语）
        if (
            (toksI.Count > 0 && diffI.Contains(toksI[0]))
            || (toksJ.Count > 0 && diffJ.Contains(toksJ[0]))
        )
            return null;

        return (diffI, diffJ);
    }

    private static bool IsContiguous(List<string> tokens, HashSet<string> diff)
    {
        var positions = new List<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (diff.Contains(tokens[i]))
                positions.Add(i);
        }
        return positions.Count > 0 && positions[^1] - positions[0] == positions.Count - 1;
    }

    // ══════════════════════════════════════════════════════
    //  本地判定（System One）：启发式证据 → 封闭集四分类
    // ══════════════════════════════════════════════════════

    /// <summary>四分类判据（state 与每题共用）。</summary>
    private static readonly Dictionary<string, string?> VerdictCriteria = new(
        StringComparer.Ordinal
    )
    {
        ["contradiction"] = "两条记忆互相矛盾，不能同时为真（事实冲突）。",
        ["refinement"] = "后者是对前者的补充或细化，可以同时为真。",
        ["duplicate"] = "两条记忆表达的是同一件事，只是措辞不同。",
        ["unrelated"] = "两条记忆没有实质关联，属于误报。",
    };

    /// <summary>
    /// 对启发式冲突做本地判定：丢弃高置信误报（unrelated 且 p ≥ DropUnrelatedProbability），
    /// 其余按 contradiction → refinement/duplicate → 未判定（原启发式顺序）重排。
    /// 判定失败 fail-open：该批保持未判定，绝不因失败而丢弃任何冲突。
    /// </summary>
    public static async Task<List<Conflict>> AdjudicateAsync(
        ISystemOneClient client,
        IReadOnlyList<Conflict> conflicts,
        CancellationToken ct
    )
    {
        var options = client.Options.Conflicts;
        if (!options.Enabled || conflicts.Count == 0)
            return conflicts.ToList();

        var maxPairs = Math.Clamp(options.MaxPairs, 1, conflicts.Count);
        var pairs = new List<(int Index, string Key, string A, string B)>(maxPairs);
        for (var i = 0; i < maxPairs; i++)
        {
            var conflict = conflicts[i];
            pairs.Add(
                (
                    i,
                    $"c{i:00}",
                    Truncate(conflict.MemoryA.Content, 300),
                    Truncate(conflict.MemoryB.Content, 300)
                )
            );
        }

        var budget = Math.Max(client.Options.MaxStateChars, 256);
        var batches = SystemOnePacking.Pack(pairs, budget, RenderPair);

        var verdicts = new Dictionary<int, (string Verdict, double? Probability)>();
        foreach (var batch in batches)
        {
            var response = await client.AskAsync(BuildState(batch), BuildQuestions(batch), ct);
            if (response is null)
                continue; // 该批判定失败：保持未判定（fail-open），其余批继续

            foreach (var pair in batch)
            {
                var answer = response.Answers.GetValueOrDefault(pair.Key);
                if (answer?.Choice is { } verdict && VerdictCriteria.ContainsKey(verdict))
                    verdicts[pair.Index] = (verdict, ResolveProbability(answer, verdict));
            }
        }

        if (verdicts.Count == 0)
            return conflicts.ToList();

        var judged = new List<Conflict>();
        var unjudged = new List<Conflict>();
        for (var i = 0; i < maxPairs; i++)
        {
            var conflict = conflicts[i];
            if (verdicts.TryGetValue(i, out var verdict))
            {
                if (
                    verdict.Verdict == "unrelated"
                    && (verdict.Probability ?? 0) >= options.DropUnrelatedProbability
                )
                    continue; // 高置信误报：从结果中移除

                judged.Add(
                    conflict with
                    {
                        JudgeVerdict = verdict.Verdict,
                        JudgeProbability = verdict.Probability,
                    }
                );
            }
            else
            {
                unjudged.Add(conflict);
            }
        }

        return judged
            .OrderBy(c => c.JudgeVerdict == "contradiction" ? 0 : 1)
            .ThenByDescending(c => c.JudgeProbability ?? 0)
            .Concat(unjudged) // 未判定：保持原启发式顺序
            .Concat(conflicts.Skip(maxPairs)) // 超出 MaxPairs 的：保持原序追尾
            .ToList();
    }

    private static Dictionary<string, object?> BuildState(
        List<(int Index, string Key, string A, string B)> batch
    )
    {
        var pairs = new Dictionary<string, object?>(batch.Count, StringComparer.Ordinal);
        foreach (var pair in batch)
        {
            pairs[pair.Key] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["a"] = pair.A,
                ["b"] = pair.B,
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["criteria"] = VerdictCriteria,
            ["pairs"] = pairs,
        };
    }

    private static Dictionary<string, SystemOneQuestion> BuildQuestions(
        List<(int Index, string Key, string A, string B)> batch
    )
    {
        var questions = new Dictionary<string, SystemOneQuestion>(
            batch.Count,
            StringComparer.Ordinal
        );
        foreach (var pair in batch)
        {
            questions[pair.Key] = new ChoiceQuestion
            {
                Instructions = $"pairs.{pair.Key} 中两条记忆的关系属于哪一类？",
                Criteria = VerdictCriteria,
            };
        }
        return questions;
    }

    private static double? ResolveProbability(SystemOneAnswer answer, string selected) =>
        answer.Probabilities is { } map && map.TryGetValue(selected, out var p)
            ? p
            : answer.Confidence;

    private static string RenderPair((int Index, string Key, string A, string B) pair) =>
        string.Concat(
            "{\"a\":",
            JsonSerializer.Serialize(pair.A),
            ",\"b\":",
            JsonSerializer.Serialize(pair.B),
            "}"
        );

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)?|[一二三四五六七八九十百千万亿零]+")]
    private static partial Regex NumRegex();
}
