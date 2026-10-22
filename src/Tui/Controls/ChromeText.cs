using System.Text;
using XenoAtom.Terminal;

namespace Tui.Controls;

/// <summary>
/// 外壳文本（对话框标题等）的宽度安全处理：
/// 部分终端对 VS15/ZWJ/ZWNJ 序列的宽度处理与框架记账存在分歧——
/// 先清洗易碎序列，再按显示宽度（框架 GetWidth，与 emoji 双宽一致）截断。
/// 参见 docs/UI.md §14 图形字符宽度安全。
/// </summary>
internal static class ChromeText
{
    /// <summary>移除易碎序列：VS15(U+FE0E) / ZWJ(U+200D) / ZWNJ(U+200C)。</summary>
    internal static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\uFE0E' or '\u200D' or '\u200C')
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>按终端显示宽度截断（CJK/emoji 双宽），超出以 ASCII ".." 结尾；≤ maxCells 原样返回。</summary>
    internal static string Clip(string text, int maxCells)
    {
        if (string.IsNullOrEmpty(text) || maxCells < 1)
            return text;
        if (TerminalTextUtility.GetWidth(text.AsSpan()) <= maxCells)
            return text;
        var sb = new StringBuilder(text.Length);
        var budget = maxCells - 2; // 截断标记 ".." 占 2 格（ASCII，宽度无歧义）
        if (budget <= 0)
            return maxCells >= 2 ? ".." : string.Empty;
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = TerminalTextUtility.GetWidth(rune.ToString().AsSpan());
            if (width + runeWidth > budget)
                break;
            sb.Append(rune.ToString());
            width += runeWidth;
        }
        return sb.Append("..").ToString();
    }

    /// <summary>清洗 + 截断（对话框标题等动态文本用）。</summary>
    internal static string ForTitle(string text, int maxCells) => Clip(Clean(text), maxCells);
}
