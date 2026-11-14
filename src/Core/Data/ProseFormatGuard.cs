using System.Text;

namespace Core.Data;

/// <summary>
/// 正文格式硬约束：独立成行的分隔符（---、***、___、—— 等）不是小说正文。
/// 这是模型把 Markdown 水平线/场景分隔符写进正文的高信号痕迹；
/// 保存前统一清理，检测逻辑同时供本地诊断与测试使用。
/// </summary>
public static class ProseFormatGuard
{
    /// <summary>
    /// 删除正文中独立成行的分隔符，并把分隔符位置收敛为一个空行。
    /// 只处理整行都是分隔符的情况；句内破折号（如「你——」）保持原样。
    /// 没有命中时返回原字符串，不改变换行、空格或段落。
    /// </summary>
    public static ProseFormatResult Normalize(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return new ProseFormatResult(content ?? string.Empty, 0);

        var lineEnding = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        var removed = 0;

        foreach (var line in lines)
        {
            if (!IsSeparatorLine(line))
            {
                kept.Add(line);
                continue;
            }

            removed++;
            // 分隔符替换为空行；连续空行在下方合并，避免段落粘连。
            if (kept.Count > 0 && kept[^1].Length > 0)
                kept.Add(string.Empty);
        }

        if (removed == 0)
            return new ProseFormatResult(content, 0);

        var builder = new StringBuilder(content.Length);
        var previousBlank = false;
        foreach (var line in kept)
        {
            var isBlank = line.Length == 0;
            if (isBlank && previousBlank)
                continue;

            if (builder.Length > 0)
                builder.Append(lineEnding);

            builder.Append(line);
            previousBlank = isBlank;
        }

        return new ProseFormatResult(builder.ToString().Trim('\n', '\r'), removed);
    }

    /// <summary>统计独立成行的分隔符数量；不分配新正文。</summary>
    public static int CountSeparatorLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        var count = 0;
        foreach (var line in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (IsSeparatorLine(line))
                count++;
        }

        return count;
    }

    /// <summary>
    /// 判断整行是否只有分隔符符号（允许符号之间有空格）。
    /// 单字符的句内标点不算；至少两个符号才视为分隔线。
    /// </summary>
    public static bool IsSeparatorLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var span = line.AsSpan().Trim();
        if (span.Length < 2)
            return false;

        var symbols = 0;
        foreach (var ch in span)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            if (!IsSeparatorSymbol(ch))
                return false;

            symbols++;
        }

        return symbols >= 2;
    }

    private static bool IsSeparatorSymbol(char ch) =>
        ch
            is '-'
                or '–'
                or '—'
                or '―'
                or '－'
                or '─'
                or '_'
                or '*'
                or '＊'
                or '·'
                or '•'
                or '='
                or '※'
                or '◆'
                or '◇'
                or '●'
                or '○'
                or '■'
                or '□';
}

/// <summary>清理结果：正文与删除的独立分隔符行数。</summary>
public readonly record struct ProseFormatResult(string Content, int RemovedSeparatorLines)
{
    /// <summary>可直接拼到工具成功消息后的清理说明。</summary>
    public string CleanupNote =>
        RemovedSeparatorLines > 0 ? $"；已清理 {RemovedSeparatorLines} 处独立分隔符" : "";
}
