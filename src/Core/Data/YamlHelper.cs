using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Core.Data;

/// <summary>
/// YAML 序列化/反序列化辅助类。
/// 使用 YamlDotNet 进行轻量级 YAML 读写，不依赖强类型模型。
/// </summary>
public static partial class YamlHelper
{
    /// <summary>不输出 anchor/alias 的序列化器。</summary>
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .DisableAliases()
        .Build();

    /// <summary>移除 YAML anchor 标记 (&amp;xxx)，YamlDotNet 的 DisableAliases 只禁止 alias 引用但不禁止 anchor 定义。</summary>
    private static readonly Regex AnchorPattern = AnchorPatternRegex();

    /// <summary>将字典序列化为 YAML 字符串。</summary>
    public static string Serialize(Dictionary<string, object?> dict)
    {
        // 先用 RepresentationModel 构建节点树，再用 SerializerBuilder 输出
        // 这样既能控制节点构建，又能避免 anchor 污染
        var yaml = new YamlMappingNode();
        foreach (var (key, value) in dict)
        {
            if (value is null)
                continue; // Skip null values — absence implies null on deserialization
            yaml.Add(new YamlScalarNode(key), ToYamlNode(value));
        }

        return StripAnchors(Serializer.Serialize(yaml));
    }

    /// <summary>将 YAML 字符串反序列化为字典。</summary>
    public static Dictionary<string, object?> Deserialize(string yamlText)
    {
        var result = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(yamlText))
            return result;

        using var reader = new StringReader(yamlText);
        var yaml = new YamlStream();
        yaml.Load(reader);

        if (yaml.Documents.Count == 0)
            return result;

        var root = yaml.Documents[0].RootNode;
        if (root is YamlMappingNode map)
        {
            foreach (var (key, value) in map.Children)
            {
                if (key is YamlScalarNode { Value: not null } keyScalar)
                    result[keyScalar.Value] = FromYamlNode(value);
            }
        }

        return result;
    }

    /// <summary>将对象列表序列化为 YAML 字符串（用于 relationships 等列表）。</summary>
    public static string SerializeList(List<Dictionary<string, object?>> list)
    {
        var seq = new YamlSequenceNode();
        foreach (var dict in list)
        {
            var map = new YamlMappingNode();
            foreach (var (key, value) in dict)
            {
                if (value is null)
                    continue;
                map.Add(new YamlScalarNode(key), ToYamlNode(value));
            }
            seq.Add(map);
        }

        return StripAnchors(Serializer.Serialize(seq));
    }

    /// <summary>将 YAML 字符串反序列化为对象列表。</summary>
    public static List<Dictionary<string, object?>> DeserializeList(string yamlText)
    {
        var result = new List<Dictionary<string, object?>>();
        if (string.IsNullOrWhiteSpace(yamlText))
            return result;

        using var reader = new StringReader(yamlText);
        var yaml = new YamlStream();
        yaml.Load(reader);

        if (yaml.Documents.Count == 0)
            return result;

        var root = yaml.Documents[0].RootNode;
        if (root is YamlSequenceNode seq)
        {
            foreach (var item in seq)
            {
                if (item is YamlMappingNode map)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var (key, value) in map.Children)
                    {
                        if (key is YamlScalarNode { Value: not null } keyScalar)
                            dict[keyScalar.Value] = FromYamlNode(value);
                    }
                    result.Add(dict);
                }
            }
        }

        return result;
    }

    // ── 枚举序列化辅助 ──

    /// <summary>
    /// 将枚举值序列化为 lowercase 字符串，用于 YAML front matter 写入。
    /// </summary>
    public static string EnumToYaml<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>
    /// 从 YAML 字典中读取枚举值。解析失败返回 <paramref name="defaultValue"/>。
    /// </summary>
    public static T YamlToEnum<T>(
        Dictionary<string, object?> dict,
        string key,
        T defaultValue = default
    )
        where T : struct, Enum
    {
        var str = dict.GetValueOrDefault(key) as string;
        return Enum.TryParse<T>(str, ignoreCase: true, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// 从 Markdown front matter 字典中读取枚举值。解析失败返回 <paramref name="defaultValue"/>。
    /// </summary>
    public static T YamlToEnum<T>(
        Dictionary<string, string> dict,
        string key,
        T defaultValue = default
    )
        where T : struct, Enum
    {
        var str = dict.GetValueOrDefault(key);
        return Enum.TryParse<T>(str, ignoreCase: true, out var result) ? result : defaultValue;
    }

    // ── 内部转换 ──

    private static YamlNode ToYamlNode(object? value)
    {
        switch (value)
        {
            case null:
                return new YamlScalarNode("null") { Style = ScalarStyle.Plain };
            case string s:
                // 形似 YAML 空值（""、null、~）的字符串加引号，保证往返仍是字符串
                return IsNullToken(s)
                    ? new YamlScalarNode(s) { Style = ScalarStyle.DoubleQuoted }
                    : new YamlScalarNode(s);
            case int i:
                return new YamlScalarNode(i.ToString(CultureInfo.InvariantCulture));
            case long l:
                return new YamlScalarNode(l.ToString(CultureInfo.InvariantCulture));
            case double d:
                return new YamlScalarNode(d.ToString("R", CultureInfo.InvariantCulture));
            case float f:
                return new YamlScalarNode(f.ToString("R", CultureInfo.InvariantCulture));
            case bool b:
                return new YamlScalarNode(b ? "true" : "false");
            case DateTime dt:
                return new YamlScalarNode(dt.ToString("O", CultureInfo.InvariantCulture));
            case List<string> strList:
            {
                var seq = new YamlSequenceNode();
                foreach (var item in strList)
                    seq.Add(new YamlScalarNode(item));
                return seq;
            }
            case string[] strArr:
            {
                var seq = new YamlSequenceNode();
                foreach (var item in strArr)
                    seq.Add(new YamlScalarNode(item));
                return seq;
            }
            case Dictionary<string, double> metrics:
            {
                var map = new YamlMappingNode();
                foreach (var (k, v) in metrics)
                    map.Add(
                        new YamlScalarNode(k),
                        new YamlScalarNode(v.ToString("R", CultureInfo.InvariantCulture))
                    );
                return map;
            }
            case Dictionary<string, object?> dict:
            {
                var map = new YamlMappingNode();
                foreach (var (k, v) in dict)
                    map.Add(new YamlScalarNode(k), ToYamlNode(v));
                return map;
            }
            case List<Dictionary<string, object?>> list:
            {
                var seq = new YamlSequenceNode();
                foreach (var d in list)
                {
                    var map = new YamlMappingNode();
                    foreach (var (k, v) in d)
                        map.Add(new YamlScalarNode(k), ToYamlNode(v));
                    seq.Add(map);
                }
                return seq;
            }
            // Handle List<object?> (e.g., from ConfigStore.ToDict()'s ToList<object?>())
            // Must come after more specific list types to avoid matching them first.
            case IList<object?> objList:
            {
                var seq = new YamlSequenceNode();
                foreach (var item in objList)
                    seq.Add(ToYamlNode(item));
                return seq;
            }
            default:
                // Fallback: ToString
                return new YamlScalarNode(value.ToString() ?? "");
        }
    }

    /// <summary>是否为 YAML 空值写法（空串 / null / ~，大小写不敏感）。</summary>
    private static bool IsNullToken(string? value) =>
        string.IsNullOrEmpty(value)
        || value.Equals("null", StringComparison.OrdinalIgnoreCase)
        || value.Equals("~", StringComparison.Ordinal);

    private static string StripAnchors(string yaml) => AnchorPattern.Replace(yaml, "");

    private static object? FromYamlNode(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                // 裸 null / ~ / 空标量（Plain 风格）是 YAML 空值，不是字符串 "null"；
                // 带引号（"null" / 'null'）才是字面量字符串
                if (scalar.Style == ScalarStyle.Plain && IsNullToken(scalar.Value))
                    return null;
                return scalar.Value;
            case YamlSequenceNode seq:
            {
                return seq.Select(FromYamlNode).ToList();
            }
            case YamlMappingNode map:
            {
                var dict = new Dictionary<string, object?>();
                foreach (var (key, value) in map.Children)
                {
                    if (key is YamlScalarNode { Value: not null } keyScalar)
                        dict[keyScalar.Value] = FromYamlNode(value);
                }
                return dict;
            }
            default:
                return null;
        }
    }

    [GeneratedRegex(@"&[a-zA-Z0-9]+\s+", RegexOptions.Compiled)]
    private static partial Regex AnchorPatternRegex();
}
