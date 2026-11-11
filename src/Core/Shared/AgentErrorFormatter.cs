using System.Text;
using System.Text.Json;

namespace Core.Shared;

/// <summary>
/// provider 原始错误 → 一行用户可读文本。会话恢复和实时链路共用，
/// 避免把整段 JSON / 重复的 ❌ 429 前缀直接展示给用户。
/// </summary>
public static class AgentErrorFormatter
{
    private const int MaxMessageLength = 300;

    public static string Format(string? raw, int? statusCode = null)
    {
        var text = StripDecorations(raw?.Trim() ?? "");
        var prefixStatus = TakeStatusPrefix(ref text);
        var status = statusCode is > 0 ? statusCode : prefixStatus;

        if (text.StartsWith('{'))
            status = TryReadJson(ref text, status);
        else if (text.StartsWith('['))
            text = TryReadJsonArray(text);

        text = text.Trim();
        if (text.StartsWith("OpenAIException - ", StringComparison.Ordinal))
            text = text["OpenAIException - ".Length..];

        text = CollapseWhitespace(text);
        if (text.Length == 0)
            text = status is null ? "未知错误" : "";
        else if (text.Length > MaxMessageLength)
            text = text[..(MaxMessageLength - 1)].TrimEnd() + "…";

        if (status is not > 0)
            return text;

        return text.Length == 0 ? $"HTTP {status}" : $"HTTP {status} · {text}";
    }

    private static string StripDecorations(string text)
    {
        while (true)
        {
            text = text.TrimStart();
            if (text.StartsWith("⚠️", StringComparison.Ordinal))
                text = text[2..];
            else if (text.StartsWith('❌') || text.StartsWith('⛔') || text.StartsWith('❗'))
                text = text[1..];
            else
                return text;
        }
    }

    private static int? TakeStatusPrefix(ref string text)
    {
        var span = text.AsSpan().TrimStart();

        if (span.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase))
        {
            span = span[5..];
            var digits = CountAsciiDigits(span);
            if (digits == 3 && int.TryParse(span[..3], out var httpStatus))
            {
                text = span[3..].ToString().TrimStart(' ', ':', '·', '-');
                return httpStatus;
            }
            return null;
        }

        var count = CountAsciiDigits(span);
        if (
            count == 3
            && (span.Length == 3 || !char.IsAsciiDigit(span[3]))
            && int.TryParse(span[..3], out var status)
        )
        {
            text = span[3..].ToString().TrimStart(' ', ':', '·', '-');
            return status;
        }

        return null;
    }

    private static int CountAsciiDigits(ReadOnlySpan<char> span)
    {
        var count = 0;
        while (count < span.Length && char.IsAsciiDigit(span[count]))
            count++;
        return count;
    }

    private static int? TryReadJson(ref string text, int? status)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return status;

            if (status is null && root.TryGetProperty("status", out var rootStatus))
                status = ReadStatus(rootStatus);

            if (
                root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
            )
            {
                if (status is null && error.TryGetProperty("status", out var errorStatus))
                    status = ReadStatus(errorStatus);
                if (
                    error.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.String
                )
                {
                    text = nested.GetString() ?? "";
                    return status;
                }
            }

            if (
                root.TryGetProperty("message", out var direct)
                && direct.ValueKind == JsonValueKind.String
            )
                text = direct.GetString() ?? "";
        }
        catch (JsonException)
        {
            // provider 偶尔返回非 JSON 的 JSON 前缀；保留原文。
        }
        return status;
    }

    private static string TryReadJsonArray(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (
                doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0
                && doc.RootElement[0].ValueKind == JsonValueKind.Object
                && doc.RootElement[0].TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
            )
                return message.GetString() ?? "";
        }
        catch (JsonException)
        {
            // 非 JSON 数组：保留原文。
        }
        return text;
    }

    private static int? ReadStatus(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');
            builder.Append(ch);
            pendingSpace = false;
        }
        return builder.ToString();
    }
}
