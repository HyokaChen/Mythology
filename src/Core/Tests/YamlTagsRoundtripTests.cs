using System.Text.Json;
using Core.Data;
using Core.Shared;
using Xunit;

namespace Core.Tests;

public class YamlTagsRoundtripTests
{
    [Fact]
    public void Serialize_ListString_ProducesYamlSequence()
    {
        var tags = new List<string> { "warrior", "brave" };
        var dict = new Dictionary<string, object?> { ["tags"] = tags };
        var yaml = YamlHelper.Serialize(dict);

        Console.WriteLine($"YAML output:\n{yaml}");

        // Should contain YAML sequence format with - prefix
        Assert.Contains("- warrior", yaml);
        Assert.Contains("- brave", yaml);
    }

    [Fact]
    public void Serialize_EmptyList_ProducesEmptySequence()
    {
        var tags = new List<string>();
        var dict = new Dictionary<string, object?> { ["tags"] = tags };
        var yaml = YamlHelper.Serialize(dict);

        Console.WriteLine($"YAML output:\n{yaml}");

        // Empty sequence should be: tags: []
        Assert.Contains("tags: []", yaml);
    }

    [Fact]
    public void Deserialize_YamlSequence_ReturnsListObject()
    {
        var yaml = "tags:\n  - warrior\n  - brave";
        var parsed = YamlHelper.Deserialize(yaml);
        var val = parsed.GetValueOrDefault("tags");

        Console.WriteLine($"Value type: {val?.GetType().Name ?? "null"}");

        // FromYamlNode converts YamlSequenceNode to List<object?>
        Assert.IsType<List<object?>>(val);
        var list = (List<object?>)val!;
        Assert.Equal(2, list.Count);
        Assert.Equal("warrior", list[0]?.ToString());
        Assert.Equal("brave", list[1]?.ToString());
    }

    [Fact]
    public void GetString_OnYamlSequence_ReturnsGarbage()
    {
        // This demonstrates the old bug: GetString on a List<object?> returns type name
        var yaml = "tags:\n  - warrior\n  - brave";
        var parsed = YamlHelper.Deserialize(yaml);
        var result = StoreHelper.GetString(parsed, "tags");

        Console.WriteLine($"GetString result: '{result}'");
        // This returns "System.Collections.Generic.List`1[System.Object]" - garbage!
        Assert.Contains("List", result ?? "");
    }

    [Fact]
    public void Full_Roundtrip_ListString_Yaml_ListString()
    {
        // Full roundtrip: List<string> → Serialize → Deserialize → GetTags-like logic
        var original = new List<string> { "warrior", "brave" };
        var dict = new Dictionary<string, object?> { ["tags"] = original };
        var yaml = YamlHelper.Serialize(dict);

        Console.WriteLine($"Serialized YAML:\n{yaml}");

        var parsed = YamlHelper.Deserialize(yaml);
        var val = parsed.GetValueOrDefault("tags");

        // Simulate GetTags logic
        List<string> recovered;
        if (val is List<object?> list)
            recovered = list.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToList();
        else
            recovered = [];

        Console.WriteLine($"Original: [{string.Join(", ", original)}]");
        Console.WriteLine($"Recovered: [{string.Join(", ", recovered)}]");

        Assert.Equal(original, recovered);
    }

    [Fact]
    public void Legacy_JsonArrayString_Compat()
    {
        // Old format: tags stored as JSON array string in YAML
        // e.g. tags: '["warrior","brave"]'
        var yaml = "tags: '[\"warrior\",\"brave\"]'";
        var parsed = YamlHelper.Deserialize(yaml);
        var val = parsed.GetValueOrDefault("tags");

        Console.WriteLine($"Value type: {val?.GetType().Name ?? "null"}");
        Console.WriteLine($"Value: '{val}'");

        // Quoted string stays as string
        Assert.IsType<string>(val);

        // Simulate GetTags legacy compat: parse JSON array string
        if (val is string s && s.StartsWith('['))
        {
            var recovered = JsonSerializer.Deserialize<List<string>>(s);
            Assert.Equal(2, recovered?.Count);
            Assert.Contains("warrior", recovered!);
            Assert.Contains("brave", recovered!);
        }
    }

    [Fact]
    public void Empty_YamlSequence_ReturnsEmptyList()
    {
        var yaml = "tags: []";
        var parsed = YamlHelper.Deserialize(yaml);
        var val = parsed.GetValueOrDefault("tags");

        Console.WriteLine($"Value type: {val?.GetType().Name ?? "null"}");

        // Empty flow sequence is parsed as YamlSequenceNode with no children
        Assert.IsType<List<object?>>(val);
        Assert.Empty((List<object?>)val!);
    }
}
