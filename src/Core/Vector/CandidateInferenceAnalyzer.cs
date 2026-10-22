using System.Security.Cryptography;
using System.Text;
using Core.Data;

namespace Core.Vector;

/// <summary>候选推断结果。</summary>
public sealed record CandidateInference
{
    /// <summary>稳定标识。</summary>
    public required string Id { get; init; }

    /// <summary>推断文本。</summary>
    public required string Text { get; init; }

    /// <summary>
    /// 置信度（0~1）。
    /// 候选推断不超过 0.6；经用户确认后升级为已确认推断时可提升。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>证据描述：说明该推断基于哪些观察得出。</summary>
    public required string EvidenceDescription { get; init; }

    /// <summary>观察次数：满足一致判断的章节数。</summary>
    public int ObservationCount { get; init; }

    /// <summary>首次观察到此模式的时间。</summary>
    public DateTime FirstObservedAt { get; init; }

    /// <summary>最近一次观察到此模式的时间。</summary>
    public DateTime LastObservedAt { get; init; }
}

/// <summary>
/// 候选推断分析器。
/// 分析写作模式，生成可解释、低风险的候选推断，供用户确认后写入长期记忆。
/// 每个维度需要至少 3 次一致观察才能产生推断；默认置信度不超过 0.6。
/// </summary>
public sealed class CandidateInferenceAnalyzer
{
    // ── 阈值常量 ──

    private const int ShortParagraphThreshold = 100; // 短段落：平均 ≤ 100 字符
    private const int LongParagraphThreshold = 500; // 长段落：平均 ≥ 500 字符
    private const int ShortSentenceThreshold = 30; // 短句：平均 ≤ 30 字符
    private const int LongSentenceThreshold = 80; // 长句：平均 ≥ 80 字符
    private const double EllipsisDensityThreshold = 3.0; // 省略号密度 ≥ 3/千字
    private const double DashDensityThreshold = 2.0; // 破折号密度 ≥ 2/千字
    private const int MinObservations = 3; // 最少一致观察次数
    private const double MaxConfidence = 0.6; // 候选推断置信度上限
    private const double BaseConfidence = 0.3; // 基础置信度（刚好满足最少观察）
    private const double ConfidencePerObservation = 0.1; // 每多一次观察增加的置信度
    private const double EditInconsistencyThreshold = 0.8; // 段落长度标准差与均值之比阈值

    private static readonly char[] s_sentenceEndings = ['。', '！', '？', '\n', '；'];
    private const string Ellipsis = "……";
    private const string Dash = "——";

    /// <summary>
    /// 分析写作模式，生成候选推断。
    /// 只分析可解释、低风险的稳定模式：段落长度偏好、句长分布、标点使用模式、段落改写痕迹。
    /// 每个维度需要至少 3 次一致观察才能产生推断；默认置信度不超过 0.6。
    /// </summary>
    /// <param name="profile">写作配置（用于风格上下文，当前为预留）。</param>
    /// <param name="recentChapters">最近章节列表（按序号排序的内部使用）。</param>
    /// <returns>候选推断列表。</returns>
    public List<CandidateInference> Analyze(WritingProfile profile, List<Chapter> recentChapters)
    {
        if (recentChapters == null || recentChapters.Count == 0)
            return [];

        // 过滤空内容章节，按序号排序
        var chapters = recentChapters
            .Where(c => !string.IsNullOrEmpty(c.Content))
            .OrderBy(c => c.Order)
            .ToList();

        if (chapters.Count < MinObservations)
            return [];

        var inferences = new List<CandidateInference>();

        AnalyzeParagraphLength(chapters, inferences);
        AnalyzeSentenceLength(chapters, inferences);
        AnalyzePunctuation(chapters, inferences);
        AnalyzeEditingPatterns(chapters, inferences);

        return inferences;
    }

    /// <summary>分析段落长度偏好：短段（≤100 字符）或长段（≥500 字符）。</summary>
    private static void AnalyzeParagraphLength(
        List<Chapter> chapters,
        List<CandidateInference> inferences
    )
    {
        var shortCount = 0;
        var longCount = 0;
        var shortFirstObserved = DateTime.MaxValue;
        var shortLastObserved = DateTime.MinValue;
        var longFirstObserved = DateTime.MaxValue;
        var longLastObserved = DateTime.MinValue;

        foreach (var chapter in chapters)
        {
            var paragraphs = SplitParagraphs(chapter.Content);
            if (paragraphs.Count < 2)
                continue;

            var avgLength = paragraphs.Average(p => p.Length);
            var time = chapter.UpdatedAt;

            if (avgLength <= ShortParagraphThreshold)
            {
                shortCount++;
                if (time < shortFirstObserved)
                    shortFirstObserved = time;
                if (time > shortLastObserved)
                    shortLastObserved = time;
            }
            else if (avgLength >= LongParagraphThreshold)
            {
                longCount++;
                if (time < longFirstObserved)
                    longFirstObserved = time;
                if (time > longLastObserved)
                    longLastObserved = time;
            }
        }

        if (shortCount >= MinObservations)
        {
            inferences.Add(
                new CandidateInference
                {
                    Id = StableId("para_short"),
                    Text = "倾向于使用短段落",
                    Confidence = ComputeConfidence(shortCount),
                    EvidenceDescription =
                        $"在 {shortCount} 个章节中，平均段落长度不超过 {ShortParagraphThreshold} 字符",
                    ObservationCount = shortCount,
                    FirstObservedAt = shortFirstObserved,
                    LastObservedAt = shortLastObserved,
                }
            );
        }

        if (longCount >= MinObservations)
        {
            inferences.Add(
                new CandidateInference
                {
                    Id = StableId("para_long"),
                    Text = "倾向于使用长段落",
                    Confidence = ComputeConfidence(longCount),
                    EvidenceDescription =
                        $"在 {longCount} 个章节中，平均段落长度超过 {LongParagraphThreshold} 字符",
                    ObservationCount = longCount,
                    FirstObservedAt = longFirstObserved,
                    LastObservedAt = longLastObserved,
                }
            );
        }
    }

    /// <summary>分析句长分布：短句（≤30 字符）或长句（≥80 字符）。</summary>
    private static void AnalyzeSentenceLength(
        List<Chapter> chapters,
        List<CandidateInference> inferences
    )
    {
        var shortCount = 0;
        var longCount = 0;
        var shortFirstObserved = DateTime.MaxValue;
        var shortLastObserved = DateTime.MinValue;
        var longFirstObserved = DateTime.MaxValue;
        var longLastObserved = DateTime.MinValue;

        foreach (var chapter in chapters)
        {
            var sentences = SplitSentences(chapter.Content);
            if (sentences.Count < 3)
                continue;

            var avgLength = sentences.Average(s => s.Length);
            var time = chapter.UpdatedAt;

            switch (avgLength)
            {
                case <= ShortSentenceThreshold:
                {
                    shortCount++;
                    if (time < shortFirstObserved)
                        shortFirstObserved = time;
                    if (time > shortLastObserved)
                        shortLastObserved = time;
                    break;
                }
                case >= LongSentenceThreshold:
                {
                    longCount++;
                    if (time < longFirstObserved)
                        longFirstObserved = time;
                    if (time > longLastObserved)
                        longLastObserved = time;
                    break;
                }
            }
        }

        if (shortCount >= MinObservations)
        {
            inferences.Add(
                new CandidateInference
                {
                    Id = StableId("sent_short"),
                    Text = "倾向于使用短句",
                    Confidence = ComputeConfidence(shortCount),
                    EvidenceDescription =
                        $"在 {shortCount} 个章节中，平均句长不超过 {ShortSentenceThreshold} 字符",
                    ObservationCount = shortCount,
                    FirstObservedAt = shortFirstObserved,
                    LastObservedAt = shortLastObserved,
                }
            );
        }

        if (longCount >= MinObservations)
        {
            inferences.Add(
                new CandidateInference
                {
                    Id = StableId("sent_long"),
                    Text = "倾向于使用长句",
                    Confidence = ComputeConfidence(longCount),
                    EvidenceDescription =
                        $"在 {longCount} 个章节中，平均句长超过 {LongSentenceThreshold} 字符",
                    ObservationCount = longCount,
                    FirstObservedAt = longFirstObserved,
                    LastObservedAt = longLastObserved,
                }
            );
        }
    }

    /// <summary>分析标点使用模式：省略号和破折号密度。</summary>
    private static void AnalyzePunctuation(
        List<Chapter> chapters,
        List<CandidateInference> inferences
    )
    {
        AnalyzeEllipsisUsage(chapters, inferences);
        AnalyzeDashUsage(chapters, inferences);
    }

    /// <summary>分析省略号使用频率。</summary>
    private static void AnalyzeEllipsisUsage(
        List<Chapter> chapters,
        List<CandidateInference> inferences
    )
    {
        var highCount = 0;
        var firstObserved = DateTime.MaxValue;
        var lastObserved = DateTime.MinValue;

        foreach (var chapter in chapters)
        {
            var textLength = chapter.Content.Length;
            if (textLength == 0)
                continue;

            var count = CountOccurrences(chapter.Content, Ellipsis);
            var density = count / (textLength / 1000.0);
            if (density >= EllipsisDensityThreshold)
            {
                highCount++;
                var time = chapter.UpdatedAt;
                if (time < firstObserved)
                    firstObserved = time;
                if (time > lastObserved)
                    lastObserved = time;
            }
        }

        if (highCount >= MinObservations)
        {
            inferences.Add(
                new CandidateInference
                {
                    Id = StableId("punct_ellipsis"),
                    Text = "频繁使用省略号",
                    Confidence = ComputeConfidence(highCount),
                    EvidenceDescription =
                        $"在 {highCount} 个章节中，省略号密度超过 {EllipsisDensityThreshold:F1}/千字",
                    ObservationCount = highCount,
                    FirstObservedAt = firstObserved,
                    LastObservedAt = lastObserved,
                }
            );
        }
    }

    /// <summary>分析破折号使用频率。</summary>
    private static void AnalyzeDashUsage(
        List<Chapter> chapters,
        List<CandidateInference> inferences
    )
    {
        var highCount = 0;
        var firstObserved = DateTime.MaxValue;
        var lastObserved = DateTime.MinValue;

        foreach (var chapter in chapters)
        {
            var textLength = chapter.Content.Length;
            if (textLength == 0)
                continue;

            var count = CountOccurrences(chapter.Content, Dash);
            var density = count / (textLength / 1000.0);
            if (density >= DashDensityThreshold)
            {
                highCount++;
                var time = chapter.UpdatedAt;
                if (time < firstObserved)
                    firstObserved = time;
                if (time > lastObserved)
                    lastObserved = time;
            }
        }

        if (highCount >= MinObservations)
        {
            inferences.Add(
                new CandidateInference
                {
                    Id = StableId("punct_dash"),
                    Text = "频繁使用破折号",
                    Confidence = ComputeConfidence(highCount),
                    EvidenceDescription =
                        $"在 {highCount} 个章节中，破折号密度超过 {DashDensityThreshold:F1}/千字",
                    ObservationCount = highCount,
                    FirstObservedAt = firstObserved,
                    LastObservedAt = lastObserved,
                }
            );
        }
    }

    /// <summary>
    /// 分析段落改写痕迹。
    /// 通过段落长度标准差与均值之比检测段落间长度差异是否显著不均。
    /// 如果段落长度分布严重不均，可能暗示用户对某些段落做了大量改写。
    /// 使用更保守的置信度上限（0.5）。
    /// </summary>
    private static void AnalyzeEditingPatterns(
        List<Chapter> chapters,
        List<CandidateInference> inferences
    )
    {
        var inconsistentCount = 0;
        var firstObserved = DateTime.MaxValue;
        var lastObserved = DateTime.MinValue;

        foreach (var chapter in chapters)
        {
            var paragraphs = SplitParagraphs(chapter.Content);
            if (paragraphs.Count < 4)
                continue;

            var lengths = paragraphs.Select(p => p.Length).ToList();
            var avg = lengths.Average();
            if (avg < 1)
                continue;

            var stdDev = StandardDeviation(lengths, avg);

            // 段落长度标准差超过平均值的 EditInconsistencyThreshold 倍
            // 认为段落间长度差异显著，可能暗示存在改写模式
            if (stdDev > avg * EditInconsistencyThreshold)
            {
                inconsistentCount++;
                var time = chapter.UpdatedAt;
                if (time < firstObserved)
                    firstObserved = time;
                if (time > lastObserved)
                    lastObserved = time;
            }
        }

        if (inconsistentCount >= MinObservations)
        {
            // 改写模式推断更保守：额外限制上限为 0.5
            var confidence = Math.Min(ComputeConfidence(inconsistentCount), 0.5);
            inferences.Add(
                new CandidateInference
                {
                    Id = StableId("edit_inconsistent"),
                    Text = "段落长度分布不均，可能存在反复改写",
                    Confidence = confidence,
                    EvidenceDescription =
                        $"在 {inconsistentCount} 个章节中，段落长度标准差超过平均值的 "
                        + $"{EditInconsistencyThreshold * 100:F0}%",
                    ObservationCount = inconsistentCount,
                    FirstObservedAt = firstObserved,
                    LastObservedAt = lastObserved,
                }
            );
        }
    }

    /// <summary>
    /// 计算置信度。
    /// 从基础置信度起步，每次额外观察增加步长，上限 MaxConfidence。
    /// </summary>
    private static double ComputeConfidence(int observationCount)
    {
        var extra = observationCount - MinObservations;
        var confidence = BaseConfidence + extra * ConfidencePerObservation;
        return Math.Min(confidence, MaxConfidence);
    }

    /// <summary>按换行符分割段落，过滤空行。</summary>
    private static List<string> SplitParagraphs(string text)
    {
        var paragraphs = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                if (current.Length > 0)
                {
                    paragraphs.Add(current.ToString().Trim());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            paragraphs.Add(current.ToString().Trim());

        return paragraphs;
    }

    /// <summary>按句末标点分割句子。</summary>
    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            current.Append(ch);
            if (Array.IndexOf(s_sentenceEndings, ch) >= 0)
            {
                var s = current.ToString().Trim();
                if (s.Length > 0)
                    sentences.Add(s);
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            var s = current.ToString().Trim();
            if (s.Length > 0)
                sentences.Add(s);
        }

        return sentences;
    }

    /// <summary>统计子串出现次数。</summary>
    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while (true)
        {
            idx = text.IndexOf(pattern, idx, StringComparison.Ordinal);
            if (idx < 0)
                break;
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    /// <summary>计算标准差。</summary>
    private static double StandardDeviation(List<int> values, double mean)
    {
        if (values.Count < 2)
            return 0;
        var sumSq = 0.0;
        foreach (var v in values)
        {
            var diff = v - mean;
            sumSq += diff * diff;
        }
        return Math.Sqrt(sumSq / values.Count);
    }

    /// <summary>从分析维度键生成稳定推断 ID。</summary>
    private static string StableId(string dimensionKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"candidate_inference:{dimensionKey}"));
        var hex = Convert.ToHexString(bytes[..6]).ToLowerInvariant();
        return $"ci_{hex}";
    }
}
