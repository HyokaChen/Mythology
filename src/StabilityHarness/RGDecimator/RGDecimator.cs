namespace StabilityHarness.RGDecimator;

public sealed class RGDecimator : IRGDecimator, Core.Stability.IMessageScorer
{
    private readonly RGConfig _config;
    private readonly IReadOnlyList<string> _keywords;
    private readonly ISimilarityFunction _similarity;

    public RGDecimator(RGConfig? config = null, ISimilarityFunction? similarity = null)
    {
        _config = config ?? new RGConfig();
        _keywords = _config.StructuralKeywords;
        _similarity = similarity ?? new VsaSimilarityFunction();
    }

    public ScoredMessage[] Decimate(ReadOnlySpan<string> messages)
    {
        if (messages.Length == 0)
            return [];

        var scored = new ScoredMessage[messages.Length];
        const int firstIndex = 0;
        var lastIndex = messages.Length - 1;
        var threshold = _config.EffectiveThreshold;
        var maxRetained = _config.EffectiveMaxRetained;

        for (var i = 0; i < messages.Length; i++)
        {
            var context = messages[..i];
            var score = (float)ComputeScore(messages[i], context);
            scored[i] = new ScoredMessage
            {
                Index = i,
                Score = score,
                Content = messages[i],
                Retained = score >= threshold,
            };
        }

        scored[firstIndex].Retained = true;
        if (lastIndex != firstIndex)
            scored[lastIndex].Retained = true;

        var retainedCount = scored.Count(s => s.Retained);
        if (retainedCount > maxRetained)
        {
            var middleRetained = scored
                .Select((s, i) => (Index: i, s))
                .Where(x => x.s.Retained && x.Index != firstIndex && x.Index != lastIndex)
                .OrderByDescending(x => x.s.Score)
                .ToList();

            foreach (var (i, _) in middleRetained.Skip(maxRetained - 2))
                scored[i].Retained = false;
        }

        return scored;
    }

    public string[] Compress(ReadOnlySpan<string> messages)
    {
        var scored = Decimate(messages);
        return scored.Where(s => s.Retained).OrderBy(s => s.Index).Select(s => s.Content).ToArray();
    }

    public float ScoreMessage(string message, string[] context, int lookback)
    {
        var ctxSlice =
            lookback > 0 && lookback <= context.Length
                ? context[^Math.Min(lookback, context.Length)..]
                : [];
        return (float)ComputeScore(message, ctxSlice);
    }

    public float ScoreMessage(ReadOnlySpan<char> message, ReadOnlySpan<char> context = default)
    {
        var ctxList = context.Length > 0 ? new[] { context.ToString() } : [];
        return (float)ComputeScore(message.ToString(), ctxList);
    }

    public ScoredMessage[] DecimateWithEmbeddings(
        ReadOnlySpan<string> messages,
        ReadOnlySpan<sbyte[]> embeddings
    )
    {
        if (messages.Length == 0)
            return [];
        if (embeddings.Length != messages.Length)
            return Decimate(messages);

        var scored = new ScoredMessage[messages.Length];
        const int firstIndex = 0;
        var lastIndex = messages.Length - 1;
        var threshold = _config.EffectiveThreshold;
        var maxRetained = _config.EffectiveMaxRetained;
        var lookback = _config.EffectiveLookbackWindow;

        for (var i = 0; i < messages.Length; i++)
        {
            var keywordScore = ComputeKeywordDensity(messages[i]);
            var infoScore = ComputeInfoDensity(messages[i]);

            float noveltyScore;
            if (i == 0)
            {
                noveltyScore = 1.0f;
            }
            else
            {
                var window = Math.Min(lookback, i);
                var start = Math.Max(0, i - window);
                float maxSim = 0;
                for (var j = start; j < i; j++)
                {
                    var sim = _similarity.Compute(embeddings[i], embeddings[j]);
                    var normalizedSim = (float)((sim + 1.0) / 2.0);
                    if (normalizedSim > maxSim)
                        maxSim = normalizedSim;
                }
                noveltyScore = 1.0f - maxSim;
            }

            var score =
                _config.KeywordWeight * keywordScore
                + _config.InfoWeight * infoScore
                + _config.NoveltyWeight * noveltyScore;
            scored[i] = new ScoredMessage
            {
                Index = i,
                Score = score,
                Content = messages[i],
                Retained = score >= threshold,
            };
        }

        scored[firstIndex].Retained = true;
        if (lastIndex != firstIndex)
            scored[lastIndex].Retained = true;

        var retainedCount = scored.Count(s => s.Retained);
        if (retainedCount > maxRetained)
        {
            var middleRetained = scored
                .Select((s, i) => (Index: i, s))
                .Where(x => x.s.Retained && x.Index != firstIndex && x.Index != lastIndex)
                .OrderByDescending(x => x.s.Score)
                .ToList();

            foreach (var (i, _) in middleRetained.Skip(maxRetained - 2))
                scored[i].Retained = false;
        }

        return scored;
    }

    public IReadOnlyList<string> GetKeywords() => _config.StructuralKeywords;

    public float GetThreshold() => _config.EffectiveThreshold;

    public int GetMaxRetained() => _config.EffectiveMaxRetained;

    private double ComputeScore(string message, ReadOnlySpan<string> context)
    {
        var keywordScore = ComputeKeywordDensity(message);
        var infoScore = ComputeInfoDensity(message);
        var redundancy = ComputeRedundancy(message, context);

        var raw =
            (double)_config.KeywordWeight * keywordScore
            + (double)_config.InfoWeight * infoScore
            + (double)_config.NoveltyWeight * (1.0f - (float)redundancy);
        return Math.Clamp(raw, 0.0, 1.0);
    }

    private float ComputeKeywordDensity(ReadOnlySpan<char> message)
    {
        if (message.IsEmpty)
            return 0;
        var tokens = Tokenize(message);
        if (tokens.Length == 0)
            return 0;

        var keywordCount = tokens.Count(token =>
            _keywords.Any(kw => token.Contains(kw, StringComparison.OrdinalIgnoreCase))
        );
        return Math.Min(1.0f, (float)keywordCount / tokens.Length);
    }

    private static float ComputeInfoDensity(ReadOnlySpan<char> message)
    {
        if (message.IsEmpty)
            return 0;
        var tokens = Tokenize(message);
        if (tokens.Length == 0)
            return 0;

        var uniqueTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
            uniqueTokens.Add(token);

        var diversity = (float)uniqueTokens.Count / tokens.Length;
        var lengthFactor = tokens.Length switch
        {
            <= 2 => 0.2f,
            <= 10 => 0.6f,
            <= 50 => 1.0f,
            <= 200 => 0.8f,
            _ => 0.5f,
        };

        return diversity * lengthFactor;
    }

    private double ComputeRedundancy(string message, ReadOnlySpan<string> context)
    {
        if (context.Length == 0)
            return 0.0;

        var msgBigrams = GetBigrams(message);
        if (msgBigrams.Count == 0)
            return 0.0;

        var lookbackStart = Math.Max(0, context.Length - _config.EffectiveLookbackWindow);
        var recent = context[lookbackStart..];

        double maxOverlap = 0;
        foreach (var ctxMsg in recent)
        {
            var ctxBigrams = GetBigrams(ctxMsg);
            if (ctxBigrams.Count == 0)
                continue;

            var intersection = msgBigrams.Intersect(ctxBigrams).Count();
            var union = msgBigrams.Count + ctxBigrams.Count - intersection;
            var jaccard = union > 0 ? (double)intersection / union : 0.0;
            if (jaccard > maxOverlap)
                maxOverlap = jaccard;
        }

        return maxOverlap;
    }

    private static HashSet<string> GetBigrams(ReadOnlySpan<char> text)
    {
        var tokens = Tokenize(text);
        var bigrams = new HashSet<string>();
        for (var i = 0; i < tokens.Length - 1; i++)
            bigrams.Add($"{tokens[i]} {tokens[i + 1]}");
        return bigrams;
    }

    private static string[] Tokenize(ReadOnlySpan<char> text)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]) || text[i] == '_')
            {
                if (start < 0)
                    start = i;
            }
            else
            {
                if (start >= 0)
                {
                    tokens.Add(text.Slice(start, i - start).ToString().ToLowerInvariant());
                    start = -1;
                }
            }
        }
        if (start >= 0)
            tokens.Add(text[start..].ToString().ToLowerInvariant());
        return tokens.ToArray();
    }

    public void ApplyProfile(Core.Stability.ModelContextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _config.ApplyProfile(profile);
    }

    // ── Core.Stability.IMessageScorer 适配 ──

    double Core.Stability.IMessageScorer.Score(
        ReadOnlySpan<char> message,
        ReadOnlySpan<char> context
    ) => ScoreMessage(message, context);
}
