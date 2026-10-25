using System.Collections.Concurrent;
using System.Text.Json;
using Core.Shared;
using Core.Tools;

namespace Desktop.Services;

/// <summary>
/// 工具调用的桌面展示格式：友好名称、可读参数摘要、可读结果摘要、完整详情。
/// 参数键优先级与 TUI <c>ToolParameterKeys.SubtitlePriority</c> 保持一致
/// （Desktop 不引用 TUI，故此处保留一份；改优先级时两边同步）。
/// </summary>
public sealed class ToolDisplayFormatter
{
    public const int DefaultArgumentLength = 72;
    public const int DefaultResultLength = 140;
    public const int MaxDetailLength = 8_000;

    /// <summary>第一个命中的键作为参数摘要（与 TUI 对齐）。</summary>
    private static readonly string[] SubtitlePriority =
    [
        "path",
        "filePath",
        "file_path",
        "url",
        "query",
        "name",
        "description",
        "pattern",
        "chapter-title",
        "title",
        "character_name",
        "command",
        "chapterId",
        "chapter-id",
        "characterId",
        "character-id",
        "factionId",
        "faction-id",
        "locationId",
        "location-id",
        "itemId",
        "item-id",
        "plotlineId",
        "plotline-id",
    ];

    /// <summary>参数摘要里永远跳过的键（内部标识，对用户无意义）。</summary>
    private static readonly HashSet<string> Skip =
    [
        .. SubtitlePriority,
        "novelId",
        "novel-id",
        "sessionId",
        "session_id",
    ];

    /// <summary>结果 JSON 中优先展示的文本字段。</summary>
    private static readonly string[] ResultTextKeys =
    [
        "message",
        "summary",
        "detail",
        "content",
        "text",
        "result",
    ];

    /// <summary>结果 JSON 中可折叠为「N 项」的数组字段。</summary>
    private static readonly string[] ResultListKeys =
    [
        "items",
        "results",
        "data",
        "chapters",
        "characters",
        "entities",
        "logs",
        "matches",
    ];

    private readonly ToolRegistry? _registry;
    private readonly ConcurrentDictionary<string, string> _labels = new(StringComparer.Ordinal);

    public ToolDisplayFormatter(ToolRegistry? registry = null)
    {
        _registry = registry;
    }

    /// <summary>工具原始名 → 中文友好名；未注册或标签为空时回退原始名。</summary>
    public string Label(string? rawName)
    {
        var name = rawName?.Trim() ?? "";
        if (name.Length == 0)
            return "";
        if (_labels.TryGetValue(name, out var cached))
            return cached;
        var label = _registry?.TryGet(name)?.Label;
        if (string.IsNullOrWhiteSpace(label))
            return name;
        // 只缓存命中项：插件/工具可能晚于首次渲染注册，未命中时不能把原始名永久钉住。
        _labels.TryAdd(name, label);
        return label;
    }

    /// <summary>工具参数 → 一行可读摘要（不含 JSON 花括号/引号）。</summary>
    public static string Arguments(string? argumentsJson, int max = DefaultArgumentLength)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return root.GetArrayLength() == 0 ? "空" : $"{root.GetArrayLength()} 项";
            if (root.ValueKind != JsonValueKind.Object)
                return Truncate(ExtractValue(root), max);

            var props = root.EnumerateObject().ToArray();
            foreach (var key in SubtitlePriority)
            {
                foreach (var prop in props)
                {
                    if (prop.Name != key)
                        continue;
                    var subtitle = ExtractValue(prop.Value);
                    if (subtitle.Length > 0)
                        return Truncate(subtitle, max);
                    break;
                }
            }

            var parts = new List<string>(2);
            foreach (var prop in props)
            {
                if (parts.Count >= 2)
                    break;
                if (Skip.Contains(prop.Name))
                    continue;
                var value = ExtractValue(prop.Value);
                if (value.Length > 0)
                    parts.Add($"{prop.Name}={value}");
            }
            return Truncate(string.Join(" · ", parts), max);
        }
        catch (JsonException)
        {
            // 参数不是 JSON（历史/异常）：去掉外层符号后按单行显示。
            return Truncate(argumentsJson.Trim().Trim('{', '}', '"'), max);
        }
    }

    /// <summary>工具结果 → 一行可读摘要（优先语义字段，不直出 JSON）。</summary>
    public static string Result(string? rawResult, int max = DefaultResultLength)
    {
        if (string.IsNullOrWhiteSpace(rawResult))
            return "";

        var trimmed = rawResult.Trim();
        // ask_user 的结构化回复（问题 + 选项 + 答案）本身就是可读文本：
        // 保留整块，让对话里的「User answers:」按满宽换行展示，而不是只剩第一行标题。
        if (
            trimmed.StartsWith(AskUserReplyFormatter.Header, StringComparison.Ordinal)
            || trimmed.StartsWith("用户回复:", StringComparison.Ordinal)
        )
            return trimmed;
        if (trimmed[0] is '{' or '[')
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return SummarizeJson(doc.RootElement, max);
            }
            catch (JsonException)
            {
                // 非合法 JSON：落到纯文本分支。
            }
        }

        return SummarizeText(trimmed, max);
    }

    /// <summary>
    /// 纯文本结果摘要：diff 围栏 → 文件与增删行数；Markdown 表格 → 表头；
    /// 其他代码围栏 → 语言与行数；否则取首个非空行。
    /// </summary>
    private static string SummarizeText(string text, int max)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        if (TrySummarizeDiff(lines, 0, max, out var diff))
            return diff;

        var first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (first >= 0 && IsFence(lines[first]))
        {
            var language = FenceLanguage(lines[first]);
            var contentStart = first + 1;
            var contentEnd = Array.FindIndex(lines, contentStart, IsFence);
            if (contentEnd < 0)
                contentEnd = lines.Length;

            var content = lines[contentStart..contentEnd];
            if (
                language is "diff" or "patch"
                && TrySummarizeDiff(content, 0, max, out var fencedDiff)
            )
                return fencedDiff;

            var lineCount = content.Count(l => !string.IsNullOrWhiteSpace(l));
            var label = string.IsNullOrEmpty(language) ? "代码块" : $"代码块（{language}）";
            return Truncate($"{label} · {lineCount} 行", max);
        }

        if (TrySummarizeTable(lines, max, out var table))
            return table;

        var firstLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        return Truncate(firstLine ?? "", max);
    }

    private static bool TrySummarizeDiff(string[] lines, int start, int max, out string summary)
    {
        summary = "";
        for (var i = start; i + 1 < lines.Length; i++)
        {
            if (
                !lines[i].StartsWith("--- ", StringComparison.Ordinal)
                || !lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal)
            )
                continue;

            var hasHunk = false;
            var added = 0;
            var removed = 0;
            for (var j = i + 2; j < lines.Length; j++)
            {
                var line = lines[j];
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    hasHunk = true;
                    continue;
                }
                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith('+'))
                    added++;
                else if (line.StartsWith('-') && !line.StartsWith("--- ", StringComparison.Ordinal))
                    removed++;
            }
            if (!hasHunk)
                continue;

            var path = lines[i + 1][4..].Split('\t')[0].Trim();
            if (path.StartsWith("b/", StringComparison.Ordinal))
                path = path[2..];
            summary = Truncate(DiffSummary(path, added, removed), max);
            return true;
        }
        return false;
    }

    /// <summary>统一 diff 的行内摘要；结构化 diff 与文本 diff 共用同一格式。</summary>
    public static string DiffSummary(string? path, int added, int removed)
    {
        var fileName = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path.Trim());
        return fileName.Length > 0 ? $"{fileName} · +{added} -{removed}" : $"+{added} -{removed}";
    }

    private static bool TrySummarizeTable(string[] lines, int max, out string summary)
    {
        summary = "";
        var first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (first < 0 || first + 1 >= lines.Length)
            return false;

        var header = lines[first].Trim();
        var separator = lines[first + 1].Trim();
        if (!header.StartsWith('|') || !IsTableSeparator(separator))
            return false;

        var cells = header
            .Trim('|')
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (cells.Length == 0)
            return false;

        summary = Truncate($"表格 · {string.Join(" / ", cells)}", max);
        return true;
    }

    private static bool IsTableSeparator(string line)
    {
        if (line.Length == 0 || !line.Contains('|') || !line.Contains('-'))
            return false;
        return line.All(c => c is '|' or '-' or ':' or ' ' or '\t');
    }

    private static bool IsFence(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal)
            || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static string FenceLanguage(string line)
    {
        var trimmed = line.Trim();
        var fence = trimmed.StartsWith("~~~", StringComparison.Ordinal) ? '~' : '`';
        var language = trimmed.TrimStart(fence).Trim();
        var space = language.IndexOfAny([' ', '\t', '{']);
        return space >= 0 ? language[..space] : language;
    }

    /// <summary>完整参数/结果详情（保留换行），超长截断避免 UI 卡顿。</summary>
    public static string Detail(string? text, int max = MaxDetailLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "\n…（已截断）";
    }

    private static string SummarizeJson(JsonElement root, int max)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.GetArrayLength() == 0 ? "空" : $"{root.GetArrayLength()} 项";
        if (root.ValueKind != JsonValueKind.Object)
            return Truncate(ExtractValue(root), max);

        if (TryGetProperty(root, "error", out var error))
        {
            var message = ExtractError(error);
            if (message.Length > 0)
                return Truncate($"错误：{message}", max);
        }

        if (
            TryGetProperty(root, "success", out var success)
            && success.ValueKind == JsonValueKind.False
        )
            return "失败";

        foreach (var key in ResultTextKeys)
        {
            if (!TryGetProperty(root, key, out var value))
                continue;
            var text = ExtractValue(value);
            if (text.Length > 0)
                return SummarizeText(text, max);
        }

        foreach (var key in new[] { "count", "total" })
        {
            if (
                TryGetProperty(root, key, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var count)
            )
                return $"{count} 项";
        }

        foreach (var key in ResultListKeys)
        {
            if (TryGetProperty(root, key, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.GetArrayLength() == 0 ? "空" : $"{value.GetArrayLength()} 项";
        }

        if (TryGetProperty(root, "success", out success) && success.ValueKind == JsonValueKind.True)
            return "完成";

        // 未知结构：取第一个短字符串字段，避免把整个对象倒出来。
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                continue;
            var text = prop.Value.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text))
                return Truncate(text, max);
        }

        return "完成";
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
            return true;

        // 历史/工具实现可能使用不同大小写或 kebab-case。
        foreach (var prop in root.EnumerateObject())
        {
            if (
                string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, ToKebab(name), StringComparison.OrdinalIgnoreCase)
            )
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string ToKebab(string name) =>
        string.Concat(
            name.Select(c => char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : c.ToString())
        );

    private static string ExtractError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString()?.Trim() ?? "";
        if (error.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "message", "detail", "error" })
            {
                if (
                    TryGetProperty(error, key, out var value)
                    && value.ValueKind == JsonValueKind.String
                )
                {
                    var text = value.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
            }
        }
        return ExtractValue(error);
    }

    private static string ExtractValue(JsonElement value, int depth = 0) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => value.GetArrayLength() == 0
                ? "空"
                : $"{value.GetArrayLength()} 项",
            JsonValueKind.Object when depth >= 1 => $"{value.EnumerateObject().Count()} 个字段",
            JsonValueKind.Object => SummarizeObject(value, depth),
            _ => "",
        };

    private static string SummarizeObject(JsonElement value, int depth)
    {
        // 常见错误对象直接展示 message；其余对象只报字段数，避免摘要变成 JSON。
        foreach (var key in new[] { "message", "name", "title" })
        {
            if (
                TryGetProperty(value, key, out var field)
                && field.ValueKind == JsonValueKind.String
                && field.GetString() is { Length: > 0 } text
            )
                return text;
        }
        var count = value.EnumerateObject().Count();
        return depth >= 1 ? $"{count} 个字段" : $"{count} 个字段";
    }

    private static string Truncate(string value, int max)
    {
        var oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Length <= max)
            return oneLine;
        return oneLine[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }
}
