using System.Runtime.CompilerServices;

namespace Core.Shared;

/// <summary>
/// 文本处理工具 — 消除各处重复的 SplitParagraphs / IsInDialogue 等方法。
/// </summary>
public static class TextHelper
{
    /// <summary>
    /// 按双换行或单换行分割段落，返回非空段落列表。
    /// Span 实现，零中间字符串分配。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<string> SplitParagraphs(ReadOnlySpan<char> text)
    {
        var paragraphs = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r'))
                continue;

            var para = text[start..i].Trim();
            if (para.Length > 0)
                paragraphs.Add(para.ToString());

            // 跳过 \r\n 或连续换行
            start = i + 1;
            while (start < text.Length && text[start] is '\n' or '\r')
                start++;

            i = start - 1; // for 循环会 i++
        }

        if (start < text.Length)
        {
            var last = text[start..].Trim();
            if (last.Length > 0)
                paragraphs.Add(last.ToString());
        }

        return paragraphs;
    }

    /// <summary>
    /// string 重载 — 内部转 Span 调用。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<string> SplitParagraphs(string text) => SplitParagraphs(text.AsSpan());

    /// <summary>
    /// 判断文本中指定位置是否在对话引号内。
    /// Span 实现，避免逐字符索引器开销。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInDialogue(ReadOnlySpan<char> text, int position)
    {
        var quoteCount = 0;
        var limit = Math.Min(position, text.Length);

        for (var i = 0; i < limit; i++)
        {
            var c = text[i];
            switch (c)
            {
                // " 「 『
                case '"' or '\u300C' or '\u300E':
                    quoteCount++;
                    break;
                // " 」 』
                case '\u201D' or '\u300D' or '\u300F':
                    quoteCount--;
                    break;
            }
        }

        return quoteCount > 0;
    }

    /// <summary>
    /// string 重载 — 内部转 Span 调用。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInDialogue(string text, int position) =>
        IsInDialogue(text.AsSpan(), position);
}
