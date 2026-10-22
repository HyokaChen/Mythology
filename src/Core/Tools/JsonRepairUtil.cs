using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Core.Infrastructure;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;

namespace Core.Tools;

/// <summary>
/// JSON 修复与预处理工具类 — 处理 LLM 输出中的各类 JSON 问题。
///
/// 职责一：预处理（从 Markdown 代码块等包裹中提取纯净 JSON）
/// 职责二：修复（截断导致的括号未闭合、多余逗号等）
///
/// 设计原则：
/// - 高内聚：JSON 预处理与修复逻辑集中管理
/// - 低耦合：独立工具类，不依赖特定业务逻辑
/// - 防御性编程：多层验证和错误处理
/// - 性能优化：正则表达式预编译，避免重复计算
/// </summary>
public static partial class JsonRepairUtil
{
    #region 公开 API — 预处理

    /// <summary>
    /// 预处理 JSON 响应
    /// 从可能包含 Markdown 代码块的文本中提取纯净的 JSON
    /// </summary>
    /// <param name="jsonResponse">原始 JSON 响应</param>
    /// <returns>处理后的纯净 JSON 字符串</returns>
    public static string PreprocessJsonResponse(string? jsonResponse)
    {
        if (string.IsNullOrWhiteSpace(jsonResponse))
            return string.Empty;

        // 第〇层：已经是合法 JSON → 无需任何提取，直接返回（跳过 markdown 提取）
        // 注意：这个检查必须在 JsonBlockRegex 之前，否则 JSON 字符串值中嵌入的
        // 代码块标记（如 ``` 会导致误提取，破坏原本有效的 JSON。
        var trimmed = jsonResponse.Trim();
        if (SimpleJsonRegex().IsMatch(jsonResponse) && IsValidJson(trimmed))
            return SanitizeInvalidEscapes(trimmed);

        // 第一层：尝试提取代码块中的 JSON
        var jsonBlockMatch = JsonBlockRegex().Match(jsonResponse);
        if (jsonBlockMatch is { Success: true, Groups.Count: > 1 })
        {
            var extractedJson = jsonBlockMatch.Groups[1].Value.Trim();
            return SanitizeInvalidEscapes(extractedJson);
        }

        // 第二层：尝试提取粘贴块格式的 JSON
        var answerBlockMatch = AnswerBlockRegex().Match(jsonResponse);
        if (answerBlockMatch is { Success: true, Groups.Count: > 1 })
        {
            var extractedJson = answerBlockMatch.Groups[1].Value.Trim();
            if (IsValidJson(extractedJson)) { }

            return SanitizeInvalidEscapes(extractedJson);
        }

        // 第三层（降级）：SimpleJsonRegex 在第〇层通过了但 IsValidJson 失败
        // → 尝试 FixEscapeSymbol 修复未转义引号
        if (SimpleJsonRegex().IsMatch(jsonResponse))
        {
            var fixedJson = FixEscapeSymbol(trimmed);
            if (IsValidJson(fixedJson))
                return SanitizeInvalidEscapes(fixedJson);
        }

        // 第四层：智能提取 JSON
        var extractedObject = ExtractJsonStructure(jsonResponse);
        if (IsValidJson(extractedObject))
            return SanitizeInvalidEscapes(extractedObject);

        // 尝试修复字符串值中未转义的双引号
        extractedObject = FixEscapeSymbol(extractedObject);
        if (IsValidJson(extractedObject))
            return SanitizeInvalidEscapes(extractedObject);

        // 第五层：清理 markdown 标记后返回
        var cleaned = jsonResponse
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "")
            .Trim();
        return SanitizeInvalidEscapes(cleaned);
    }

    #endregion

    #region 公开 API — 截断修复

    /// <summary>
    /// 尝试修复被截断的 JSON 字符串。
    /// </summary>
    /// <param name="json">可能被截断的 JSON 字符串。</param>
    /// <returns>修复后的 JSON 字符串，或原始字符串（无法修复时）。</returns>
    public static string TryRepairJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || IsValidJson(json))
            return json;

        var s = json.TrimEnd();

        // 修复1：移除末尾多余的逗号和引号
        var trimPatterns = new[] { ",\"", "\",", ",\"}", "\",\"}", ",\"}]", "\",\"}]" };
        foreach (var pattern in trimPatterns)
        {
            if (s.EndsWith(pattern, StringComparison.Ordinal))
            {
                var candidate = CloseBrackets(s[..^pattern.Length]);
                if (IsValidJson(candidate))
                    return candidate;
            }
        }

        // 修复2：通用尾部修剪 — 逐字符从末尾移除，然后补全括号
        for (var trim = 1; trim <= Math.Min(20, s.Length); trim++)
        {
            var candidate = CloseBrackets(s[..^trim]);
            if (IsValidJson(candidate))
                return candidate;
        }

        // 修复3：直接补全括号
        var closed = CloseBrackets(s);
        return IsValidJson(closed) ? closed : json;
    }

    /// <summary>
    /// 修复 assistant 消息中 ToolCalls 的损坏 arguments。
    /// 如果不修复，下一轮 API 调用会将非法 JSON 发回 LLM API，导致 500 错误。
    /// </summary>
    /// <param name="newMessages">本轮新增的消息列表。</param>
    /// <param name="logger">Logger 实例。</param>
    public static void RepairToolCallArguments(List<ChatMessage> newMessages, ILogger? logger)
    {
        foreach (var msg in newMessages)
        {
            if (msg.Role != ChatMessageRoles.Assistant || msg.ToolCalls is null)
                continue;

            foreach (var tc in msg.ToolCalls)
            {
                if (tc.FunctionCall is null)
                    continue;

                var args = tc.FunctionCall.Arguments;
                if (string.IsNullOrEmpty(args))
                    continue;

                if (IsValidJson(args))
                    continue;

                var repaired = TryRepairJson(args);
                var success = IsValidJson(repaired);
                if (!success)
                    repaired = "{}";

                CoreLogger.ToolCallArgsRepaired(
                    logger.Safe(),
                    tc.FunctionCall.Name,
                    success,
                    args.Length,
                    repaired.Length
                );

                tc.FunctionCall.Arguments = repaired;
            }
        }
    }

    #endregion

    #region 私有方法 — 预处理

    /// <summary>
    /// 智能提取 JSON 对象或数组
    /// 分别追踪花括号和方括号的深度，两者都归零时才认为结构完整，
    /// 避免裸 ]（未被 [ 匹配）提前截断提取。
    /// </summary>
    private static string ExtractJsonStructure(string text)
    {
        // 查找第一个可能是 JSON 起始的字符：{ 或 [
        var startBracket = text.IndexOf('{');
        var startArray = text.IndexOf('[');

        int startIndex;

        switch (startBracket)
        {
            // 确定到底谁在前面
            case < 0 when startArray < 0:
                return text.Trim();
            case < 0:
                startIndex = startArray;
                break;
            default:
            {
                if (startArray < 0)
                {
                    startIndex = startBracket;
                }
                else
                {
                    // 如果都有，谁在前面用谁
                    startIndex = startArray < startBracket ? startArray : startBracket;
                }

                break;
            }
        }

        var curlyCount = 0;
        var squareCount = 0;
        var endIndex = -1;

        for (var i = startIndex; i < text.Length; i++)
        {
            switch (text[i])
            {
                // 跳过字符串内容，避免字符串内的括号干扰计数
                case '"':
                {
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\')
                            i++; // 跳过转义字符
                        i++;
                    }
                    continue;
                }
                case '{':
                    curlyCount++;
                    break;
                case '}':
                    if (curlyCount > 0)
                        curlyCount--;
                    break;
                case '[':
                    squareCount++;
                    break;
                case ']':
                    if (squareCount > 0)
                        squareCount--;
                    break;
            }

            // 只有花括号和方括号都归零时，才认为 JSON 结构完整结束
            // 避免裸 ]（未被 [ 匹配）提前截断提取
            if (curlyCount == 0 && squareCount == 0)
            {
                endIndex = i;
                break;
            }
        }

        return endIndex > startIndex
            ? text.Substring(startIndex, endIndex - startIndex + 1).Trim()
            : text.Trim();
    }

    /// <summary>
    /// 清理 JSON 字符串中无效的转义序列
    /// LLM 生成的 JSON 可能包含 Windows 路径（如 C:\Users\...），
    /// 其中的 \U、\c 等不是合法的 JSON 转义，会导致 System.Text.Json 解析失败。
    /// 该方法将单反斜杠 \ 替换为双反斜杠 \\，保留合法的转义序列。
    /// </summary>
    private static string SanitizeInvalidEscapes(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var i = 0;

        while (i < json.Length)
        {
            var c = json[i];

            if (c == '"')
            {
                // 检查引号是否被转义（前面有奇数个反斜杠）
                var backslashCount = 0;
                var j = i - 1;
                while (j >= 0 && json[j] == '\\')
                {
                    backslashCount++;
                    j--;
                }

                // 仅当引号未被转义时切换字符串模式
                if (backslashCount % 2 == 0)
                    inString = !inString;

                sb.Append(c);
                i++;
                continue;
            }

            if (inString && c == '\\' && i + 1 < json.Length)
            {
                var next = json[i + 1];
                if (IsValidEscape(next, json, i + 1))
                {
                    // 合法转义序列，原样保留
                    sb.Append(c);
                    sb.Append(next);
                    i += 2;
                    if (next == 'u')
                    {
                        // 追加 \uXXXX 的 4 个十六进制数字
                        sb.Append(json[i]);
                        sb.Append(json[i + 1]);
                        sb.Append(json[i + 2]);
                        sb.Append(json[i + 3]);
                        i += 4;
                    }
                }
                else
                {
                    // 非法转义：将 \ 替换为 \\
                    sb.Append('\\');
                    sb.Append('\\');
                    i++; // 跳过原始反斜杠，下一个字符将在下一轮处理
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 判断字符是否为合法的 JSON 转义字符
    /// </summary>
    private static bool IsValidEscape(char c, string json, int index)
    {
        return c switch
        {
            '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' => true,
            'u' => index + 4 < json.Length
                && IsHexDigit(json[index + 1])
                && IsHexDigit(json[index + 2])
                && IsHexDigit(json[index + 3])
                && IsHexDigit(json[index + 4]),
            _ => false,
        };
    }

    /// <summary>
    /// 判断字符是否为十六进制数字
    /// </summary>
    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>
    /// 修复 JSON 字符串值中未转义的双引号
    /// LLM 常在 thought 等字段中输出中文语境的双引号（如 已明确"这个游戏"指代），
    /// 导致 JSON 解析器提前结束字符串值。该方法将字符串值内部的内容双引号自动转义为 \"，
    /// 保留结构性的闭合引号（后跟 , } ] : 或 EOF 的引号）。
    /// </summary>
    private static string FixEscapeSymbol(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var i = 0;

        while (i < json.Length)
        {
            var c = json[i];

            // 在字符串内遇到反斜杠时，跳过整个转义序列（避免把 \" 误判为裸引号）
            if (inString && c == '\\' && i + 1 < json.Length)
            {
                var next = json[i + 1];
                sb.Append(c);
                sb.Append(next);
                i += 2;

                if (next == 'u' && i + 3 < json.Length)
                {
                    sb.Append(json, i, 4);
                    i += 4;
                }

                continue;
            }

            if (c == '"')
            {
                // 检查该引号是否已被转义（前面有奇数个反斜杠）
                var backslashCount = 0;
                var j = i - 1;
                while (j >= 0 && json[j] == '\\')
                {
                    backslashCount++;
                    j--;
                }

                if (backslashCount % 2 == 1)
                {
                    // 已转义，原样保留
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (inString)
                {
                    // 在字符串值内部：判断是内容引号还是结构闭合引号
                    var nextIdx = FindNextNonWhitespace(json, i + 1);
                    if (!nextIdx.HasValue || IsJsonStructuralCloseChar(json[nextIdx.Value]))
                    {
                        // 后跟 , } ] : 或 EOF → 结构闭合引号
                        inString = false;
                        sb.Append(c);
                    }
                    else
                    {
                        // 后跟其他字符 → 内容引号，添加转义
                        sb.Append('\\');
                        sb.Append(c);
                    }
                }
                else
                {
                    // 进入新的字符串
                    inString = true;
                    sb.Append(c);
                }

                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从指定位置开始查找第一个非空白字符的索引
    /// </summary>
    private static int? FindNextNonWhitespace(string json, int startIndex)
    {
        for (var i = startIndex; i < json.Length; i++)
        {
            if (!char.IsWhiteSpace(json[i]))
                return i;
        }

        return null;
    }

    /// <summary>
    /// 判断字符是否为 JSON 结构闭合字符（可紧跟在字符串闭引号之后）
    /// </summary>
    private static bool IsJsonStructuralCloseChar(char c) => c is ',' or '}' or ']' or ':';

    #endregion

    #region 私有方法 — 截断修复

    /// <summary>
    /// 补全 JSON 字符串的未闭合括号。
    /// 处理字符串转义，统计未闭合的 { [ " 并按序补全。
    /// </summary>
    private static string CloseBrackets(string json)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escape = false;

        foreach (var c in json)
        {
            if (escape)
            {
                escape = false;
                continue;
            }

            switch (c)
            {
                case '\\' when inString:
                    escape = true;
                    continue;
                case '"':
                {
                    if (inString)
                    {
                        if (stack.Count > 0 && stack.Peek() == '"')
                            stack.Pop();
                        else
                            stack.Push(c);
                        inString = false;
                    }
                    else
                    {
                        stack.Push(c);
                        inString = true;
                    }

                    continue;
                }
            }

            if (inString)
                continue;

            switch (c)
            {
                case '{' or '[':
                    stack.Push(c);
                    break;
                case '}' when stack.Count > 0 && stack.Peek() == '{':
                case ']' when stack.Count > 0 && stack.Peek() == '[':
                    stack.Pop();
                    break;
            }
        }

        var sb = new StringBuilder(json);

        if (inString)
            sb.Append('"');

        while (sb.Length > 0 && sb[^1] == ',')
            sb.Length--;

        var remaining = new List<char>();
        while (stack.Count > 0)
        {
            var ch = stack.Pop();
            remaining.Add(
                ch switch
                {
                    '{' => '}',
                    '[' => ']',
                    _ => ch,
                }
            );
        }

        foreach (var c in remaining)
            sb.Append(c);

        return sb.ToString();
    }

    #endregion

    #region 私有方法 — 通用

    /// <summary>
    /// 验证 JSON 格式有效性
    /// </summary>
    private static bool IsValidJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    #endregion

    #region 预编译正则

    // 匹配以 { 或 [ 开头的纯 JSON
    [GeneratedRegex(@"^\s*[\[\{].*[\]\}]\s*$", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex SimpleJsonRegex();

    // 匹配 Markdown 代码块中的 JSON
    [GeneratedRegex(@"```(?:json)?\s*(.*?)\s*```", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex JsonBlockRegex();

    // 匹配 <answer> 标签中的 JSON
    [GeneratedRegex(
        @"<answer>\s*(.*?)\s*</answer>",
        RegexOptions.Compiled | RegexOptions.Singleline
    )]
    private static partial Regex AnswerBlockRegex();

    #endregion
}
