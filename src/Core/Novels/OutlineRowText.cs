using System.Globalization;

namespace Core.Novels;

/// <summary>
/// 大纲行的排版文本（两端共用，保证 TUI / Desktop 同一章序号、字数、进度「同一个数」）。
/// 纯函数，不触碰文件系统。
/// </summary>
public static class OutlineRowText
{
    /// <summary>章序号列：两位对齐（01 / 12），千章以上自然扩展；非章行返回空。</summary>
    public static string FormatOrder(int order) =>
        order <= 0 ? "" : order.ToString(order < 100 ? "D2" : "D3", CultureInfo.InvariantCulture);

    /// <summary>字数列：1,001 → "1.0k"、23,450 → "23k"（中文千进制缩写，读感对齐）。</summary>
    public static string FormatWords(int charCount) =>
        charCount switch
        {
            <= 0 => "0",
            < 1000 => charCount.ToString(CultureInfo.InvariantCulture),
            < 10_000 => $"{charCount / 1000.0:0.#}k",
            _ => $"{charCount / 1000}k",
        };

    /// <summary>卷进度：「终稿/总章数」；无章时返回空（避免 0/0）。</summary>
    public static string FormatProgress(int done, int total) => total <= 0 ? "" : $"{done}/{total}";
}
