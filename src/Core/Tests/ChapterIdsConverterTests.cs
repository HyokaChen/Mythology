using System.Text;
using System.Text.Json;
using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ChapterIdsConverter 测试：云端工具 chapter_ids 参数必须兼容 LLM 双重 JSON 序列化。
/// 覆盖三种 LLM 输入：原生数组、字符串化数组（双重转义）、单字符串（回退）。
/// </summary>
public class ChapterIdsConverterTests
{
    private static List<string>? Read(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes);
        Assert.True(reader.Read());
        return new ChapterIdsConverter().Read(
            ref reader,
            typeof(List<string>),
            JsonSerializerOptions.Default
        );
    }

    [Fact]
    public void NativeArray_Deserializes()
    {
        var result = Read("""["80c45d176c1c","13a959cf40f8"]""");
        Assert.Equal(["80c45d176c1c", "13a959cf40f8"], result);
    }

    [Fact]
    public void DoubleSerializedArray_Deserializes()
    {
        // LLM 常见错误：整个数组再 JSON 序列化一次 → 字符串值里是数组字面量
        var result = Read("\"[\\\"80c45d176c1c\\\",\\\"13a959cf40f8\\\"]\"");
        Assert.Equal(["80c45d176c1c", "13a959cf40f8"], result);
    }

    [Fact]
    public void SingleIdString_FallsBackToSingleItem()
    {
        var result = Read("\"80c45d176c1c\"");
        Assert.Equal(["80c45d176c1c"], result);
    }

    [Fact]
    public void EmptyString_ReturnsNull()
    {
        var result = Read("\"\"");
        Assert.Null(result);
    }

    [Fact]
    public void NullValue_ReturnsNull()
    {
        var result = Read("null");
        Assert.Null(result);
    }
}
