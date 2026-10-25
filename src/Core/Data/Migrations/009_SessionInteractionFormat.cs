using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Core.Shared;

namespace Core.Data.Migrations;

/// <summary>
/// 009：统一历史会话里的交互文本格式。
///
/// 1. ask_user 回复：旧 <c>**用户回复:**</c> / <c>用户回复:</c> 块 →
///    OMP 风格 <see cref="AskUserReplyFormatter"/>（<c>User answers:</c> + 每组一行）。
///    多选信息优先从同文件内 assistant 的 <c>ask_user</c> 工具调用参数还原，
///    找不到元数据时按答案数量/分隔符保守推断。
/// 2. 变体注入 prompt：refine prompt 与结构化提取 prompt 的变体正文 →
///    <see cref="VariantXml"/> 包裹，属性从旧 prompt 的标题/探索类型/应用目标提取。
///
/// 幂等：已是新格式的条目跳过；单文件失败跳过并记日志，不阻断启动。
/// </summary>
public sealed class SessionInteractionFormat : IDataMigration
{
    public int Version => 9;
    public string Name => "009_SessionInteractionFormat";
    public string Description => "统一历史 ask_user 回复格式并给变体 prompt 正文加 <variant> 包裹";

    public TextWriter? Log { get; set; }

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex s_refineHead = new(
        @"变体「(?<label>[^」]*)」（ID: (?<id>[^）]*)）",
        RegexOptions.Compiled
    );

    private static readonly Regex s_variantLabel = new(
        @"变体「(?<label>[^」]*)」",
        RegexOptions.Compiled
    );

    private static readonly Regex s_structuredType = new(
        @"——一个(?<type>[^方\n]*)方案",
        RegexOptions.Compiled
    );

    private static readonly Regex s_target = new(
        @"目标章节:\s*(?<title>.+?)\s*\(序号\s*(?<order>\d+)\)",
        RegexOptions.Compiled
    );

    public async Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var sessionsDir = Path.Combine(mythologyDir, "sessions");
        if (!Directory.Exists(sessionsDir))
        {
            Log?.WriteLine("  sessions 目录不存在，跳过迁移。");
            return;
        }

        var files = Directory.EnumerateFiles(
            sessionsDir,
            "*.jsonl",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.PlatformDefault,
            }
        );
        var changed = 0;
        var failed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (IsAuditPath(file, sessionsDir))
                continue;

            try
            {
                if (await MigrateFileAsync(file, ct))
                    changed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                Log?.WriteLine($"  [warn] 会话文件迁移失败，已跳过：{file}（{ex.Message}）");
            }
        }

        Log?.WriteLine($"  ask_user/变体格式迁移完成：{changed} 个文件已更新，{failed} 个失败。");
    }

    private static bool IsAuditPath(string file, string sessionsDir)
    {
        var relative = Path.GetRelativePath(sessionsDir, file);
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.StartsWith("audit_", StringComparison.Ordinal));
    }

    private async Task<bool> MigrateFileAsync(string path, CancellationToken ct)
    {
        var original = await AtomicFile.ReadAllTextAsync(path, ct);
        if (string.IsNullOrEmpty(original))
            return false;

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var rawLines = original.Split('\n');

        var nodes = new List<JsonObject?>(rawLines.Length);
        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                nodes.Add(null);
                continue;
            }

            try
            {
                nodes.Add(JsonNode.Parse(line) as JsonObject);
            }
            catch (JsonException)
            {
                nodes.Add(null);
            }
        }

        var metadata = CollectAskMetadata(nodes);
        var changedIndexes = new HashSet<int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not { } entry || !IsMessageEntry(entry))
                continue;

            var role = GetString(entry, "role");
            var content = GetString(entry, "content");
            if (string.IsNullOrEmpty(role) || content is null)
                continue;

            string? migrated = null;
            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                if (IsLegacyToolResult(content))
                    migrated = ConvertAskReply(content, metadata);
            }
            else if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                if (IsLegacyUserReply(content))
                    migrated = ConvertAskReply(content, metadata);
                else
                    migrated = MigrateVariantPrompt(content);
            }

            if (migrated is null || string.Equals(migrated, content, StringComparison.Ordinal))
                continue;

            SetString(entry, "content", migrated);
            nodes[i] = entry;
            changedIndexes.Add(i);
        }

        if (changedIndexes.Count == 0)
            return false;

        var output = new StringBuilder(original.Length + 512);
        for (var i = 0; i < rawLines.Length; i++)
        {
            if (i > 0)
                output.Append(newline);

            var raw = rawLines[i].TrimEnd('\r');
            if (changedIndexes.Contains(i) && nodes[i] is { } entry)
                output.Append(entry.ToJsonString(s_writeOptions));
            else
                output.Append(raw);
        }

        await AtomicFile.WriteAllTextAsync(path, output.ToString(), ct);
        return true;
    }

    // ═══════════════════════════════════════════════════════════
    //  ask_user 回复
    // ═══════════════════════════════════════════════════════════

    private static bool IsLegacyToolResult(string content) =>
        content.TrimStart().StartsWith("用户回复:", StringComparison.Ordinal);

    private static bool IsLegacyUserReply(string content) =>
        content.TrimStart().StartsWith("**用户回复:**", StringComparison.Ordinal);

    private static string? ConvertAskReply(string content, IReadOnlyList<AskMeta> metadata)
    {
        var groups = ParseLegacyReply(content);
        if (groups.Count == 0)
            return null;

        var sb = new StringBuilder(AskUserReplyFormatter.Header);
        foreach (var group in groups)
        {
            var header = string.IsNullOrWhiteSpace(group.Header) ? "?" : group.Header.Trim();
            var meta = FindMetadata(group, metadata);
            var raw = group.Answers.Trim();
            var value = FormatAnswerValue(raw, meta?.MultiSelect);
            sb.Append('\n').Append(header).Append(": ").Append(value);
        }
        return sb.ToString();
    }

    private static string FormatAnswerValue(string raw, bool? multiSelect)
    {
        if (raw.Length == 0)
            return multiSelect == true ? "[]" : "(未回答)";
        if (string.Equals(raw, "(已取消)", StringComparison.Ordinal))
            return raw;

        var multi =
            multiSelect ?? (raw.Contains(", ", StringComparison.Ordinal) || raw.Contains('、'));
        if (!multi)
            return raw;

        var answers = raw.Split([", ", "、"], StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .ToArray();
        return answers.Length == 0 ? "[]" : $"[{string.Join(", ", answers)}]";
    }

    private static AskMeta? FindMetadata(AnswerGroup group, IReadOnlyList<AskMeta> metadata)
    {
        var header = group.Header.Trim();
        var question = group.Question.Trim();
        var exact = metadata.FirstOrDefault(m =>
            string.Equals(m.Header, header, StringComparison.OrdinalIgnoreCase)
            && (
                question.Length == 0
                || string.Equals(m.Question, question, StringComparison.OrdinalIgnoreCase)
            )
        );
        return exact
            ?? metadata.FirstOrDefault(m =>
                string.Equals(m.Header, header, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static List<AnswerGroup> ParseLegacyReply(string content)
    {
        var firstBreak = content.IndexOf('\n');
        var body = firstBreak >= 0 ? content[(firstBreak + 1)..] : "";
        var groups = new List<AnswerGroup>();
        AnswerGroup? current = null;

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('→') || line.StartsWith("->", StringComparison.Ordinal))
            {
                if (current is null)
                    continue;
                var answer = line.StartsWith('→') ? line[1..].Trim() : line[2..].Trim();
                if (answer.Length == 0)
                    continue;
                if (current.Answers.Length > 0)
                    current.Answers += ", " + answer;
                else
                    current.Answers = answer;
                continue;
            }

            if (line.StartsWith('['))
            {
                var close = line.IndexOf(']');
                if (close > 1)
                {
                    current = new AnswerGroup
                    {
                        Header = line[1..close].Trim(),
                        Question = line[(close + 1)..].Trim(),
                    };
                    groups.Add(current);
                    continue;
                }
            }

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                current = new AnswerGroup
                {
                    Header = line[..colon].Trim(),
                    Question = line[(colon + 1)..].Trim(),
                };
                groups.Add(current);
            }
        }

        return groups;
    }

    private static List<AskMeta> CollectAskMetadata(IEnumerable<JsonObject?> nodes)
    {
        var result = new List<AskMeta>();
        foreach (var entry in nodes)
        {
            if (entry is null || !IsMessageEntry(entry))
                continue;
            if (
                !string.Equals(
                    GetString(entry, "role"),
                    "assistant",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            var calls = ParseArray(GetNode(entry, "tool-calls", "toolCalls", "tool_calls"));
            if (calls is null)
                continue;

            foreach (var call in calls.OfType<JsonObject>())
            {
                if (
                    !string.Equals(
                        GetString(call, "name"),
                        "ask_user",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    continue;

                var args = ParseObject(GetNode(call, "arguments", "args"));
                var questions = ParseArray(GetNode(args, "questions"));
                if (questions is null)
                    continue;

                foreach (var question in questions.OfType<JsonObject>())
                {
                    var header = GetString(question, "header") ?? "";
                    if (header.Length == 0)
                        continue;
                    result.Add(
                        new AskMeta(
                            header.Trim(),
                            (GetString(question, "question") ?? "").Trim(),
                            GetBool(question, "multiSelect", "multi_select", "multi-select")
                        )
                    );
                }
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    //  变体 prompt
    // ═══════════════════════════════════════════════════════════

    private static string? MigrateVariantPrompt(string content)
    {
        var refine = MigrateRefinePrompt(content);
        return refine ?? MigrateStructuredPrompt(content);
    }

    private static bool IsVariantBlock(string body)
    {
        var trimmed = body.Trim();
        return trimmed.StartsWith("<variant", StringComparison.Ordinal)
            && trimmed.EndsWith("</variant>", StringComparison.Ordinal);
    }

    private static string? MigrateRefinePrompt(string content)
    {
        const string contentHeading = "===== 选中的变体内容 =====";
        const string requirementHeading = "===== 用户修改要求 =====";
        if (
            !content
                .TrimStart()
                .StartsWith("用户从 explore 工具的结果中选择了变体", StringComparison.Ordinal)
            || !content.Contains(contentHeading, StringComparison.Ordinal)
            || !content.Contains(requirementHeading, StringComparison.Ordinal)
        )
            return null;

        var headingIndex = content.IndexOf(contentHeading, StringComparison.Ordinal);
        var endIndex = content.IndexOf(requirementHeading, headingIndex, StringComparison.Ordinal);
        if (endIndex < 0)
            return null;

        var bodyStart = headingIndex + contentHeading.Length;
        while (bodyStart < endIndex && (content[bodyStart] == '\n' || content[bodyStart] == '\r'))
            bodyStart++;
        var body = content[bodyStart..endIndex].TrimEnd('\r', '\n');
        if (body.Length == 0 || IsVariantBlock(body))
            return null;

        var head = s_refineHead.Match(content[..headingIndex]);
        var label = head.Success ? head.Groups["label"].Value : null;
        var id = head.Success ? head.Groups["id"].Value : null;
        var type = ExtractSection(content, "===== 探索类型 =====");
        var targetSection = ExtractSection(content, "===== 应用目标 =====");
        string? target = null;
        if (targetSection is not null)
        {
            var match = s_target.Match(targetSection);
            if (match.Success)
                target =
                    $"第{match.Groups["order"].Value}章「{match.Groups["title"].Value.Trim()}」";
        }

        var prefix = content[..bodyStart].TrimEnd('\r', '\n');
        var suffix = content[endIndex..];
        return prefix + "\n\n" + VariantXml.Wrap(body, id, label, type, target) + "\n\n" + suffix;
    }

    private static string? MigrateStructuredPrompt(string content)
    {
        if (!content.TrimStart().StartsWith("用户从探索结果中选择了变体", StringComparison.Ordinal))
            return null;

        var introEnd = content.IndexOf("\n\n", StringComparison.Ordinal);
        if (introEnd <= 0)
            return null;
        var bodyStart = introEnd + 2;
        var bodyEnd = content.IndexOf("\n\n请根据以上内容", bodyStart, StringComparison.Ordinal);
        if (bodyEnd < 0)
            return null;

        var body = content[bodyStart..bodyEnd].TrimEnd('\r', '\n');
        if (body.Length == 0 || IsVariantBlock(body))
            return null;

        var labelMatch = s_variantLabel.Match(content[..introEnd]);
        var label = labelMatch.Success ? labelMatch.Groups["label"].Value : null;
        var typeMatch = s_structuredType.Match(content[..introEnd]);
        var type = typeMatch.Success ? MapVariantType(typeMatch.Groups["type"].Value) : null;

        return content[..bodyStart]
            + VariantXml.Wrap(body, label: label, type: type)
            + content[bodyEnd..];
    }

    private static string? MapVariantType(string value) =>
        value switch
        {
            "角色" => "character",
            "情节线" => "plot",
            "大纲" => "outline",
            "风格" => "style",
            _ => null,
        };

    private static string? ExtractSection(string content, string heading)
    {
        var start = content.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += heading.Length;
        var end = content.IndexOf("\n=====", start, StringComparison.Ordinal);
        var section = end < 0 ? content[start..] : content[start..end];
        var trimmed = section.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    // ═══════════════════════════════════════════════════════════
    //  JSON 辅助
    // ═══════════════════════════════════════════════════════════

    private static bool IsMessageEntry(JsonObject entry)
    {
        var type = GetNode(entry, "type");
        if (type is not JsonValue value)
            return false;
        if (value.TryGetValue<string>(out var text))
            return string.Equals(text, "message", StringComparison.OrdinalIgnoreCase);
        return value.TryGetValue<int>(out var number) && number == 0;
    }

    private static JsonNode? GetNode(JsonObject? obj, params string[] names)
    {
        if (obj is null)
            return null;
        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var node))
                return node;
        }
        foreach (var pair in obj)
        {
            if (names.Any(n => string.Equals(n, pair.Key, StringComparison.OrdinalIgnoreCase)))
                return pair.Value;
        }
        return null;
    }

    private static string? GetString(JsonObject? obj, params string[] names)
    {
        var node = GetNode(obj, names);
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool? GetBool(JsonObject? obj, params string[] names)
    {
        var node = GetNode(obj, names);
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<bool>(out var flag))
            return flag;
        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag)
            ? flag
            : null;
    }

    private static void SetString(JsonObject obj, string name, string value)
    {
        if (obj.ContainsKey(name))
        {
            obj[name] = value;
            return;
        }
        var existing = obj.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        obj[existing ?? name] = value;
    }

    private static JsonArray? ParseArray(JsonNode? node)
    {
        if (node is JsonArray array)
            return array;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return null;
        try
        {
            return JsonNode.Parse(text) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? ParseObject(JsonNode? node)
    {
        if (node is JsonObject obj)
            return obj;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return null;
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record AskMeta(string Header, string Question, bool? MultiSelect);

    private sealed class AnswerGroup
    {
        public string Header { get; init; } = "";
        public string Question { get; init; } = "";
        public string Answers { get; set; } = "";
    }
}
