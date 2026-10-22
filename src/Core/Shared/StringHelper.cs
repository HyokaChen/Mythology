namespace Core.Shared;

/// <summary>
/// 字符串工具类 — 统一截断逻辑，使用 Span 避免中间分配。
/// 项目中存在 4+ 种不同的截断实现，统一到此处。
/// </summary>
public static class StringHelper
{
    /// <summary>
    /// 截断字符串并在超出时添加后缀（如省略号）。
    /// 使用 Span 避免中间字符串分配。
    /// </summary>
    /// <param name="s">输入字符串</param>
    /// <param name="maxLength">最大长度（含后缀）</param>
    /// <param name="suffix">截断后缀，默认 "…"</param>
    /// <returns>截断后的字符串</returns>
    public static string Truncate(this string s, int maxLength, string suffix = "…")
    {
        if (s.Length <= maxLength)
            return s;
        var contentLength = maxLength - suffix.Length;
        return contentLength <= 0 ? suffix : string.Concat(s.AsSpan(0, contentLength), suffix);
    }
}
