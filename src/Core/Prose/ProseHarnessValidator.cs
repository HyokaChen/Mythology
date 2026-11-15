namespace Core.Prose;

/// <summary>
/// 只回答一个问题：AI 修订后的正文是否仍然是原正文，只是独立分隔符行发生了变化。
///
/// 该校验不评价分隔符数量、间距、位置或密度——那些判断完全由 harness 做出。
/// 它只把「整行两个及以上分隔符号」当作可增删的分隔符语法，
/// 并强制其余段落逐字、逐段、按顺序保持一致。
/// </summary>
public static class ProseHarnessValidator
{
    private const string SeparatorSymbols = "-–—―－─_*＊·•=※◆◇●○■□";

    public static bool TryValidate(
        string original,
        string revised,
        out string content,
        out ProseHarnessSummary summary,
        out string? error
    )
    {
        var originalLines = SplitLines(original);
        var revisedLines = SplitLines(revised);
        summary = new ProseHarnessSummary(
            CountSeparatorLines(original),
            CountSeparatorLines(revised)
        );

        var originalProse = ProjectProseLines(originalLines);
        var revisedProse = ProjectProseLines(revisedLines);
        var mismatch = FindFirstMismatch(originalProse, revisedProse);
        if (mismatch >= 0)
        {
            content = original;
            error =
                $"正文第 {mismatch + 1} 个非分隔符段落不一致："
                + $"原文「{Preview(originalProse, mismatch)}」/ 修订「{Preview(revisedProse, mismatch)}」";
            return false;
        }

        var lineEnding = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        content = string.Join(lineEnding, revisedLines).TrimEnd('\r', '\n');
        error = null;
        return true;
    }

    /// <summary>
    /// 整行只由空格与分隔符号组成，且至少两个分隔符号。
    /// 单字符破折号行（如「—」）以及句内破折号（如「你——」）都按正文处理；
    /// 句内破折号是合法正文，常用于强调、语音延长、话语中断或插入解释。
    /// </summary>
    public static bool IsStandaloneSeparatorLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var symbols = 0;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            if (!IsSeparatorSymbol(ch))
                return false;
            symbols++;
        }

        return symbols >= 2;
    }

    public static int CountSeparatorLines(string? content)
    {
        var count = 0;
        foreach (var line in SplitLines(content))
        {
            if (IsStandaloneSeparatorLine(line))
                count++;
        }

        return count;
    }

    private static List<string> ProjectProseLines(IReadOnlyList<string> lines)
    {
        var projected = new List<string>(lines.Count);
        var blankRun = 0;
        foreach (var line in lines)
        {
            if (IsStandaloneSeparatorLine(line))
                continue;

            if (string.IsNullOrWhiteSpace(line))
            {
                blankRun++;
                continue;
            }

            if (blankRun > 0 && projected.Count > 0)
                projected.Add("");

            blankRun = 0;
            projected.Add(line);
        }

        return projected;
    }

    private static int FindFirstMismatch(
        IReadOnlyList<string> original,
        IReadOnlyList<string> revised
    )
    {
        var shared = Math.Min(original.Count, revised.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(original[i], revised[i], StringComparison.Ordinal))
                return i;
        }

        return original.Count == revised.Count ? -1 : shared;
    }

    private static string Preview(IReadOnlyList<string> lines, int index)
    {
        if (index >= lines.Count)
            return "（无）";

        var value = lines[index];
        return value.Length <= 40 ? value : value[..40] + "…";
    }

    private static string[] SplitLines(string? content) =>
        string.IsNullOrEmpty(content)
            ? []
            : content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static bool IsSeparatorSymbol(char ch) => SeparatorSymbols.Contains(ch);
}
