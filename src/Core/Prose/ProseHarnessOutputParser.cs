namespace Core.Prose;

public sealed record ProseHarnessOutput(string Content, string Notes);

/// <summary>
/// 解析 harness 的结构化输出。优先读取 &lt;revised_chapter&gt;，
/// 其次接受 Markdown 代码围栏，最后才把整段文本当作正文。
/// </summary>
public static class ProseHarnessOutputParser
{
    public static ProseHarnessOutput Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ProseHarnessOutput("", "");

        var text = raw.Trim().TrimStart('\uFEFF');
        var content = ExtractTag(text, "<revised_chapter>", "</revised_chapter>");
        var notes = ExtractTag(text, "<separator_notes>", "</separator_notes>") ?? "";
        content ??= ExtractCodeFence(text) ?? text;

        return new ProseHarnessOutput(content.Trim('\r', '\n'), notes.Trim());
    }

    private static string? ExtractTag(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static string? ExtractCodeFence(string text)
    {
        var start = text.IndexOf("```", StringComparison.Ordinal);
        if (start < 0)
            return null;

        var firstLineEnd = text.IndexOf('\n', start);
        if (firstLineEnd < 0)
            return null;

        var bodyStart = firstLineEnd + 1;
        var end = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
        return end < 0 ? null : text[bodyStart..end];
    }
}
