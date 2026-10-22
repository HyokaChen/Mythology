using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Shared;

/// <summary>
/// 容错字符串列表转换器 — LLM 工具调用常把 List&lt;string&gt; 参数误传为字符串：
/// 既可能是 JSON 数组字符串（"[\"a\",\"b\"]"），也可能是逗号/空格分隔列表（"a, b"）。
/// 读取时同时接受 JSON 数组与任意字符串形式，避免反序列化抛异常。
/// </summary>
public sealed class FlexibleStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartArray:
            {
                var list = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                        break;
                    list.Add(reader.GetString() ?? "");
                }
                return list;
            }
            case JsonTokenType.String:
            {
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    return [];

                // 优先尝试 JSON 数组字符串
                var trimmed = raw.Trim();
                if (trimmed.StartsWith('['))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<string>>(trimmed, options);
                        if (parsed is not null)
                            return parsed;
                    }
                    catch (JsonException)
                    {
                        // 不是合法 JSON 数组 → 掉到分隔符解析
                    }
                }

                // 逗号/分号/空白/换行分隔列表
                return trimmed
                    .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().Trim('"'))
                    .Where(s => s.Length > 0)
                    .ToList();
            }
            default:
                throw new JsonException(
                    "Cannot deserialize non-string, non-array JSON token as List<string>."
                );
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<string>? value,
        JsonSerializerOptions options
    )
    {
        // 手动写数组，避免经 options 递归回本转换器导致栈溢出。
        writer.WriteStartArray();
        if (value is not null)
        {
            foreach (var item in value)
                writer.WriteStringValue(item);
        }
        writer.WriteEndArray();
    }
}
