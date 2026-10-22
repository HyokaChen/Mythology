using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// YamlHelper null 往返测试：验证 C# null → YAML → C# null 的正确转换。
/// </summary>
public class YamlHelperNullRoundtripTests
{
    [Fact]
    public void NullValue_Roundtrip_NullKeysAbsent()
    {
        var dict = new Dictionary<string, object?>
        {
            ["name"] = "test",
            ["optional"] = null,
            ["empty"] = "",
            ["number"] = 42,
        };

        var yaml = YamlHelper.Serialize(dict);
        var deserialized = YamlHelper.Deserialize(yaml);

        // Non-null values survive
        Assert.Equal("test", deserialized["name"]);
        Assert.Equal("", deserialized["empty"]);
        Assert.Equal("42", deserialized["number"]);
        // Null values are absent after roundtrip (not written to YAML)
        Assert.False(deserialized.ContainsKey("optional"));
    }

    [Fact]
    public void NullValue_List_Roundtrip_NullKeysAbsent()
    {
        var list = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "a", ["value"] = null },
            new() { ["id"] = "b", ["value"] = "present" },
        };

        var yaml = YamlHelper.SerializeList(list);
        var deserialized = YamlHelper.DeserializeList(yaml);

        Assert.Equal(2, deserialized.Count);
        // First dict: null value key is absent
        Assert.False(deserialized[0].ContainsKey("value"));
        Assert.Equal("a", deserialized[0]["id"]);
        // Second dict: non-null value survives
        Assert.Equal("present", deserialized[1]["value"]);
    }

    [Fact]
    public void NullValue_StringLiteralNull_Preserved()
    {
        var dict = new Dictionary<string, object?> { ["literal"] = "null" };
        var yaml = YamlHelper.Serialize(dict);
        var deserialized = YamlHelper.Deserialize(yaml);
        Assert.Equal("null", deserialized["literal"]);
    }

    [Fact]
    public void NullValue_CSharpNull_KeyAbsent()
    {
        var dict = new Dictionary<string, object?> { ["absent"] = null };
        var yaml = YamlHelper.Serialize(dict);
        var deserialized = YamlHelper.Deserialize(yaml);
        // Null keys are not written → key won't exist after roundtrip
        Assert.False(deserialized.ContainsKey("absent"));
    }

    [Fact]
    public void NullValue_Mixed_AllCorrect()
    {
        var dict = new Dictionary<string, object?>
        {
            ["str"] = "hello",
            ["nil"] = null,
            ["num"] = 123,
        };

        var yaml = YamlHelper.Serialize(dict);
        var deserialized = YamlHelper.Deserialize(yaml);

        Assert.Equal("hello", deserialized["str"]);
        Assert.Equal("123", deserialized["num"]);
        // Null key is absent
        Assert.False(deserialized.ContainsKey("nil"));
    }

    [Fact]
    public void YamlNullToken_ParsedAsNull_QuotedNullStaysString()
    {
        // 手写/旧版配置里的裸 null（如 mcp url: null）必须解析为空值，而不是字符串 "null"
        var deserialized = YamlHelper.Deserialize("a: null\nb: ~\nc: \"null\"\nd: 'null'\ne:\n");

        Assert.Null(deserialized["a"]);
        Assert.Null(deserialized["b"]);
        Assert.Null(deserialized["e"]);
        Assert.Equal("null", deserialized["c"]);
        Assert.Equal("null", deserialized["d"]);
    }

    [Fact]
    public void NullLookingString_RoundtripsAsString()
    {
        var dict = new Dictionary<string, object?>
        {
            ["empty"] = "",
            ["literal"] = "null",
            ["tilde"] = "~",
        };

        var deserialized = YamlHelper.Deserialize(YamlHelper.Serialize(dict));

        Assert.Equal("", deserialized["empty"]);
        Assert.Equal("null", deserialized["literal"]);
        Assert.Equal("~", deserialized["tilde"]);
    }

    // ══════════════════════════════════════════════════════
    //  Enum Yaml serialization
    // ══════════════════════════════════════════════════════

    [Fact]
    public void EnumToYaml_Roundtrip()
    {
        Assert.Equal("published", YamlHelper.EnumToYaml(ChapterStatus.Published));

        var dict = new Dictionary<string, string> { ["status"] = "published" };
        var parsed = YamlHelper.YamlToEnum(dict, "status", ChapterStatus.Draft);
        Assert.Equal(ChapterStatus.Published, parsed);
    }

    [Fact]
    public void YamlToEnum_MissingKey_ReturnsDefault()
    {
        var dict = new Dictionary<string, string>();
        var parsed = YamlHelper.YamlToEnum(dict, "status", ChapterStatus.Draft);
        Assert.Equal(ChapterStatus.Draft, parsed);
    }

    [Fact]
    public void YamlToEnum_InvalidValue_ReturnsDefault()
    {
        var dict = new Dictionary<string, string> { ["status"] = "not_a_real_status" };
        var parsed = YamlHelper.YamlToEnum(dict, "status", ChapterStatus.Draft);
        Assert.Equal(ChapterStatus.Draft, parsed);
    }
}
