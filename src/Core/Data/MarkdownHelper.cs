using System.Text;
using System.Text.RegularExpressions;

namespace Core.Data;

/// <summary>
/// Markdown Front Matter 解析/生成辅助类。
/// 支持 YAML front matter (--- ... ---) 的解析和生成。
/// </summary>
public static partial class MarkdownHelper
{
    /// <summary>
    /// 解析 Markdown 文件内容，分离 front matter 和正文。
    /// </summary>
    /// <param name="content">文件完整内容。</param>
    /// <returns>(frontMatter字典, 正文)</returns>
    public static (Dictionary<string, string> meta, string body) ParseMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (new Dictionary<string, string>(), "");

        var match = FrontMatterRegex().Match(content);
        if (!match.Success)
            return (new Dictionary<string, string>(), content);

        var metaText = match.Groups[1].Value.Trim();
        var body = match.Groups[2].Value.TrimStart();

        var meta = new Dictionary<string, string>();
        foreach (var line in metaText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = trimmed[..colonIdx].Trim();
                var value = trimmed[(colonIdx + 1)..].Trim();
                // Remove surrounding quotes（双引号值还原转义；单引号值仅剥引号）
                if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
                {
                    value = UnescapeMetaValue(value[1..^1]);
                }
                else if (value.Length >= 2 && value.StartsWith("'") && value.EndsWith("'"))
                {
                    value = value[1..^1];
                }
                meta[key] = value;
            }
        }

        return (meta, body);
    }

    /// <summary>
    /// 将 front matter 和正文组合成 Markdown 文件内容。
    /// </summary>
    public static string WriteMarkdown(Dictionary<string, string> meta, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        foreach (var (key, value) in meta)
        {
            sb.AppendLine($"{key}: {EscapeMetaValue(value)}");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>
    /// frontmatter 单行值转义：反斜杠/双引号/换行必须转义，否则破坏行式解析。
    /// 转义序列为 YAML 双引号风格（\n / \" / \\），云端 YamlDotNet 可直接还原。
    /// </summary>
    private static string EscapeMetaValue(string value)
    {
        if (value.IndexOfAny([':', '#', '\n', '\r', '"', '\\']) < 0)
            return value;

        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r\n", "\\n")
            .Replace('\r', '\n')
            .Replace("\n", "\\n");
        return $"\"{escaped}\"";
    }

    /// <summary>双引号值的转义还原：单遍左到右扫描，\n → 换行、\" → 引号、\\ → 反斜杠。</summary>
    private static string UnescapeMetaValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            i++;
            sb.Append(
                value[i] switch
                {
                    'n' => '\n',
                    '"' => '"',
                    '\\' => '\\',
                    var c => c,
                }
            );
        }
        return sb.ToString();
    }

    [GeneratedRegex(
        @"^---\s*\n(.*?)\n---\s*\n?(.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline
    )]
    private static partial Regex FrontMatterRegex();
}
