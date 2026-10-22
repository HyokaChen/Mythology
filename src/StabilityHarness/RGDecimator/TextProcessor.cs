namespace StabilityHarness.RGDecimator;

public static class TextProcessor
{
    public static string[] Tokenize(ReadOnlySpan<char> text)
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

    public static HashSet<string> GetBigrams(ReadOnlySpan<char> text)
    {
        var tokens = Tokenize(text);
        var bigrams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Length - 1; i++)
            bigrams.Add($"{tokens[i]} {tokens[i + 1]}");
        return bigrams;
    }
}
