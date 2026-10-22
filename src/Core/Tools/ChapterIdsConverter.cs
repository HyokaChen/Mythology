using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Tools;

/// <summary>
/// 兼容 LLM 双重 JSON 序列化的 chapter_ids 转换器：
/// 接受原生 JSON 数组 ["a","b"] 或字符串 "\"[\"a\",\"b\"]\""（LLM 常见错误）。
/// 用于所有带 <c>chapter_ids</c>（复数、数组）参数的云端工具参数绑定。
/// </summary>
public sealed class ChapterIdsConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartArray:
                return JsonSerializer.Deserialize<List<string>>(ref reader, options);
            case JsonTokenType.String:
            {
                var str = reader.GetString();
                if (string.IsNullOrWhiteSpace(str))
                    return null;
                // LLM 有时把 JSON 数组再序列化一次 → 字符串里是 JSON 数组
                try
                {
                    return JsonSerializer.Deserialize<List<string>>(str, options);
                }
                catch
                {
                    // 回退：把整个字符串当作单一 ID
                    return [str];
                }
            }
            default:
                return null;
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<string>? value,
        JsonSerializerOptions options
    )
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}
