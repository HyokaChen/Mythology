using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Inference;

/// <summary>
/// System One 线协议模型（llama.cpp <c>/v1/systemone</c>：一次前向、无生成 token）。
/// 与 jegrep 的 TypeSafe Jev 请求/应答形状一致：
/// 请求 <c>{ state, model, questions{ noul|choice } }</c>，应答 <c>{ model, answers, usage }</c>。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
public abstract record SystemOneQuestion
{
    /// <summary>题目指令。</summary>
    [JsonPropertyName("instructions")]
    public required string Instructions { get; init; }
}

/// <summary>noul 题：yes/no 概率。</summary>
public sealed record NoulQuestion : SystemOneQuestion
{
    /// <summary>yes/no 判据（可选；可放 state 里一次声明）。</summary>
    [JsonPropertyName("criteria")]
    public NoulCriteria? Criteria { get; init; }
}

/// <summary>noul 判据：键名固定为 "true"/"false"（对齐线协议）。</summary>
public sealed record NoulCriteria
{
    [JsonPropertyName("true")]
    public required string Yes { get; init; }

    [JsonPropertyName("false")]
    public required string No { get; init; }
}

/// <summary>choice 题：在命名选项上的分布。</summary>
public sealed record ChoiceQuestion : SystemOneQuestion
{
    /// <summary>选项名 → 描述（描述可为 null）。</summary>
    [JsonPropertyName("criteria")]
    public required Dictionary<string, string?> Criteria { get; init; }
}

/// <summary>单条应答（容错视图：字段按实测填充，缺项保持 null）。</summary>
public sealed record SystemOneAnswer
{
    public string? Type { get; init; }
    public double? Noul { get; init; }
    public string? Choice { get; init; }
    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }
    public double? Confidence { get; init; }
    public double? Score { get; init; }
}

/// <summary>一次 /v1/systemone 应答。</summary>
public sealed record SystemOneResponse
{
    public required string Model { get; init; }
    public required IReadOnlyDictionary<string, SystemOneAnswer> Answers { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }

    /// <summary>
    /// 取 noul 概率；缺项/越界/非法值返回 null（调用方按"未知"处理——绝不构成负判）。
    /// </summary>
    public double? Noul(string key) =>
        Answers.TryGetValue(key, out var answer)
        && answer.Noul is { } p
        && double.IsFinite(p)
        && p >= 0
        && p <= 1
            ? p
            : null;
}

/// <summary>System One 请求体的构造与应答容错解析（手写 JsonDocument：字段名以实测为准）。</summary>
public static class SystemOneWire
{
    private static readonly JsonSerializerOptions s_requestOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>构造请求体 JSON（questions 值按 polymorphic "type" 序列化）。</summary>
    public static string BuildRequestJson(
        object state,
        string model,
        IReadOnlyDictionary<string, SystemOneQuestion> questions
    ) =>
        JsonSerializer.Serialize(
            new SystemOneRequest
            {
                State = state,
                Model = model,
                Questions = questions,
            },
            s_requestOptions
        );

    /// <summary>容错解析应答；JSON 非法/结构不符 → null（调用方降级，不抛）。</summary>
    public static SystemOneResponse? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var model =
                root.TryGetProperty("model", out var modelEl)
                && modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString() ?? ""
                    : "";

            var answers = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
            if (
                root.TryGetProperty("answers", out var answersEl)
                && answersEl.ValueKind == JsonValueKind.Object
            )
            {
                foreach (var prop in answersEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    answers[prop.Name] = ParseAnswer(prop.Value);
                }
            }

            long inputTokens = 0;
            long outputTokens = 0;
            if (
                root.TryGetProperty("usage", out var usageEl)
                && usageEl.ValueKind == JsonValueKind.Object
            )
            {
                inputTokens = ReadLong(usageEl, "input_tokens");
                outputTokens = ReadLong(usageEl, "output_tokens");
            }

            return new SystemOneResponse
            {
                Model = model,
                Answers = answers,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SystemOneAnswer ParseAnswer(JsonElement el) =>
        new()
        {
            Type = ReadString(el, "type"),
            Noul = ReadDouble(el, "noul"),
            Choice = ReadString(el, "choice"),
            Probabilities = ReadProbabilities(el),
            Confidence = ReadDouble(el, "confidence"),
            Score = ReadDouble(el, "score"),
        };

    /// <summary>概率对象：实测字段名可能是 probabilities 或 p（两种都接）。</summary>
    private static IReadOnlyDictionary<string, double>? ReadProbabilities(JsonElement el)
    {
        foreach (var name in new[] { "probabilities", "p" })
        {
            if (
                !el.TryGetProperty(name, out var probEl)
                || probEl.ValueKind != JsonValueKind.Object
            )
                continue;
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var prop in probEl.EnumerateObject())
            {
                if (ReadNumber(prop.Value) is { } v)
                    map[prop.Name] = v;
            }
            if (map.Count > 0)
                return map;
        }
        return null;
    }

    private static double? ReadNumber(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String
                when double.TryParse(
                    el.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var s
                ) => s,
            _ => null,
        };

    private static double? ReadDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? ReadNumber(v) : null;

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long ReadLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var l)
            ? l
            : 0;
}

internal sealed record SystemOneRequest
{
    [JsonPropertyName("state")]
    public required object State { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("questions")]
    public required IReadOnlyDictionary<string, SystemOneQuestion> Questions { get; init; }
}
