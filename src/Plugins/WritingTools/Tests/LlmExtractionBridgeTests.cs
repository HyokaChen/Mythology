using Plugins.WritingTools.Infrastructure;
using Xunit;

namespace WritingTools.Tests;

/// <summary>
/// LlmExtractionBridge 纯桥测试（CHANGE_07 Phase C）。
/// 保留 JSON 解析与响应解析测试；ChapterFeatures/CompositeDiagnoser 消费测试
/// 已随本地算法删除，等价版在 云端私有仓库（云端私有引擎.Tests）。
/// </summary>
public sealed class LlmExtractionBridgeTests
{
    // ═══════ JSON 解析 ═══════

    [Fact]
    public void ExtractJson_PureJson_ReturnsAsIs()
    {
        var json = """{"key": "value"}""";
        var result = LlmExtractionBridge.ExtractJson(json);
        Assert.Equal(json, result);
    }

    [Fact]
    public void ExtractJson_MarkdownCodeBlock_ExtractsContent()
    {
        var response = """
            ```json
            {"key": "value"}
            ```
            """;
        var result = LlmExtractionBridge.ExtractJson(response);
        Assert.Equal("""{"key": "value"}""", result);
    }

    [Fact]
    public void ExtractJson_BareCodeBlock_ExtractsContent()
    {
        var response = """
            ```
            {"key": "value"}
            ```
            """;
        var result = LlmExtractionBridge.ExtractJson(response);
        Assert.Equal("""{"key": "value"}""", result);
    }

    [Fact]
    public void ExtractJson_MixedText_ExtractsBraceBlock()
    {
        var response = "Here is the result: {\"key\": \"value\"} and some text after.";
        var result = LlmExtractionBridge.ExtractJson(response);
        Assert.Equal("""{"key": "value"}""", result);
    }

    [Fact]
    public void ExtractJson_NoJson_ReturnsNull()
    {
        var result = LlmExtractionBridge.ExtractJson("No JSON here.");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractJson_NestedBraces_MatchesOuterPair()
    {
        var response = """{"outer": {"inner": 1}}""";
        var result = LlmExtractionBridge.ExtractJson(response);
        Assert.Equal("""{"outer": {"inner": 1}}""", result);
    }

    // ═══════ 响应解析 ═══════

    [Fact]
    public void ParseExtractionResponse_FullResponse_ParsesAllFields()
    {
        var response = """
            ```json
            {
              "character-presence": [
                {"name": "张三", "paragraphs": [0, 2, 5], "aliases": ["三哥"]},
                {"name": "李四", "paragraphs": [1, 3], "aliases": []}
              ],
              "interactions": [
                {"from": "张三", "to": "李四", "type": "冲突", "paragraph": 0},
                {"from": "李四", "to": "王五", "type": "合作", "paragraph": 3}
              ],
              "protagonist-behaviors": {
                "rational-actions": 3,
                "irrational-actions": 1,
                "cost-paid": 2,
                "benefits-without-cost": 0,
                "challenged": 1,
                "growth-moments": 1
              },
              "keyword-corrections": [
                {"original": "攻击", "raw-count": 2, "corrected-count": 1, "reason": "攻击性不算攻击"}
              ]
            }
            ```
            """;

        var json = LlmExtractionBridge.ExtractJson(response);
        Assert.NotNull(json);
        Assert.Contains("character-presence", json);
        Assert.Contains("interactions", json);
        Assert.Contains("protagonist-behaviors", json);
        Assert.Contains("keyword-corrections", json);
    }

    [Fact]
    public void ParseExtractionResponse_EmptyArrays_ParsesCorrectly()
    {
        var response = """
            {
              "character-presence": [],
              "interactions": [],
              "keyword-corrections": []
            }
            """;

        var json = LlmExtractionBridge.ExtractJson(response);
        Assert.NotNull(json);
        Assert.Contains("character-presence", json);
    }

    [Fact]
    public void ParseExtractionResponse_MissingFields_ParsesGracefully()
    {
        var response = """{"character-presence": [{"name": "张三", "paragraphs": [0]}]}""";
        var json = LlmExtractionBridge.ExtractJson(response);
        Assert.NotNull(json);
    }
}
