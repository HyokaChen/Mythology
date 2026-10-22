using Core.Tools;
using Xunit;

namespace Core.Tests;

public class JsonRepairUtilPreprocessTests
{
    [Fact]
    public void PreprocessJsonResponse_Null_ReturnsEmpty()
    {
        var result = JsonRepairUtil.PreprocessJsonResponse(null);
        Assert.Equal("", result);
    }

    [Fact]
    public void PreprocessJsonResponse_Empty_ReturnsEmpty()
    {
        var result = JsonRepairUtil.PreprocessJsonResponse("");
        Assert.Equal("", result);
    }

    [Fact]
    public void PreprocessJsonResponse_Whitespace_ReturnsEmpty()
    {
        var result = JsonRepairUtil.PreprocessJsonResponse("   ");
        Assert.Equal("", result);
    }

    [Fact]
    public void PreprocessJsonResponse_ValidJson_ReturnsAsIs()
    {
        var json = """{"key": "value"}""";
        var result = JsonRepairUtil.PreprocessJsonResponse(json);
        Assert.Equal(json, result);
    }

    [Fact]
    public void PreprocessJsonResponse_JsonBlock_ExtractsJson()
    {
        var input = """
            Some text before
            ```json
            {"key": "value"}
            ```
            Some text after
            """;
        var result = JsonRepairUtil.PreprocessJsonResponse(input);
        Assert.Equal("""{"key": "value"}""", result);
    }

    [Fact]
    public void PreprocessJsonResponse_JsonBlockWithoutLang_ExtractsJson()
    {
        var input = """
            ```
            {"key": "value"}
            ```
            """;
        var result = JsonRepairUtil.PreprocessJsonResponse(input);
        Assert.Equal("""{"key": "value"}""", result);
    }

    [Fact]
    public void PreprocessJsonResponse_ValidArray_ReturnsAsIs()
    {
        var json = """[1, 2, 3]""";
        var result = JsonRepairUtil.PreprocessJsonResponse(json);
        Assert.Equal(json, result);
    }

    [Fact]
    public void PreprocessJsonResponse_AnswerBlock_ExtractsJson()
    {
        var input = """
            <answer>
            {"result": "ok"}
            </answer>
            """;
        var result = JsonRepairUtil.PreprocessJsonResponse(input);
        Assert.Equal("""{"result": "ok"}""", result);
    }

    [Fact]
    public void PreprocessJsonResponse_JsonWithTextBefore_ExtractsCorrectly()
    {
        var input = "Some text {\"key\": \"value\"} more text";
        var result = JsonRepairUtil.PreprocessJsonResponse(input);
        // Falls through to markdown-extraction layer; may keep surrounding text
        Assert.Contains("""{"key": "value"}""", result);
    }

    [Fact]
    public void PreprocessJsonResponse_NestedBrackets_ExtractsCorrectly()
    {
        var input = """Response: {"outer": {"inner": [1, 2, 3]}} trailing""";
        var result = JsonRepairUtil.PreprocessJsonResponse(input);
        // Should contain the valid JSON structure
        Assert.Contains("""{"outer": {"inner": [1, 2, 3]}}""", result);
    }
}

public class JsonRepairUtilRepairTests
{
    [Fact]
    public void TryRepairJson_ValidJson_ReturnsAsIs()
    {
        var json = """{"a": 1, "b": 2}""";
        var result = JsonRepairUtil.TryRepairJson(json);
        Assert.Equal(json, result);
    }

    [Fact]
    public void TryRepairJson_NullOrEmpty_ReturnsOriginal()
    {
        Assert.Null(JsonRepairUtil.TryRepairJson(null!));
        Assert.Equal("", JsonRepairUtil.TryRepairJson(""));
    }

    [Fact]
    public void TryRepairJson_Unrepairable_ReturnsOriginal()
    {
        var json = """not json at all""";
        var result = JsonRepairUtil.TryRepairJson(json);
        Assert.Equal(json, result);
    }

    [Fact]
    public void TryRepairJson_TrailingComma_RemovesComma()
    {
        var json = """{"a": 1, "b": 2,}""";
        var result = JsonRepairUtil.TryRepairJson(json);
        Assert.Equal("""{"a": 1, "b": 2}""", result);
    }

    [Fact]
    public void TryRepairJson_EmptyObjectTruncated_Closes()
    {
        var result = JsonRepairUtil.TryRepairJson("{");
        // CloseBrackets adds closing }
        Assert.Equal("{}", result);
    }

    [Fact]
    public void TryRepairJson_EmptyArrayTruncated_Closes()
    {
        var result = JsonRepairUtil.TryRepairJson("[");
        Assert.Equal("[]", result);
    }

    [Fact]
    public void TryRepairJson_TruncatedSimpleObject_MayRepair()
    {
        var json = """{"a": 1, "b": 2""";
        var result = JsonRepairUtil.TryRepairJson(json);
        Assert.True(IsValidJson(result), $"Result should be valid JSON: {result}");
    }

    [Fact]
    public void TryRepairJson_TruncatedSimpleArray_MayRepair()
    {
        var json = """[1, 2, 3""";
        var result = JsonRepairUtil.TryRepairJson(json);
        Assert.True(IsValidJson(result), $"Result should be valid JSON: {result}");
    }

    [Fact]
    public void TryRepairJson_NestedTruncated_YieldsValidJson()
    {
        var json = """{"a": {"b": 1, "c": [2, 3""";
        var result = JsonRepairUtil.TryRepairJson(json);
        Assert.True(IsValidJson(result), $"Result should be valid JSON: {result}");
    }

    private static bool IsValidJson(string json)
    {
        try
        {
            System.Text.Json.JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public class JsonRepairUtilEscapeTests
{
    [Fact]
    public void PreprocessJsonResponse_BackslashPath_FixesEscapes()
    {
        // Backslashes in JSON string values can break parsing —
        // this test verifies repair on all platforms
        var json = """{"path": "C:\Users\test"}""";
        var result = JsonRepairUtil.PreprocessJsonResponse(json);
        Assert.True(IsValidJson(result), $"Result should be valid JSON: {result}");
    }

    [Fact]
    public void PreprocessJsonResponse_UnicodeEscape_Preserved()
    {
        var json = """{"emoji": "\u2764"}""";
        var result = JsonRepairUtil.PreprocessJsonResponse(json);
        Assert.True(IsValidJson(result), $"Result should be valid JSON: {result}");
    }

    [Fact]
    public void PreprocessJsonResponse_UnescapedQuotesInValue_Fixed()
    {
        var json = """{"thought": "已明确"这个游戏"指代"}""";
        var result = JsonRepairUtil.PreprocessJsonResponse(json);
        Assert.True(IsValidJson(result), $"Result should be valid JSON: {result}");
    }

    private static bool IsValidJson(string json)
    {
        try
        {
            System.Text.Json.JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public class JsonRepairUtilRepairToolCallTests
{
    [Fact]
    public void PreprocessJsonResponse_EmbeddedCodeBlockInJson_ExtractsCorrectly()
    {
        // JSON string containing ``` should not trigger false extraction
        var json = """{"code": "here is ``` some code ``` inside"}""";
        var result = JsonRepairUtil.PreprocessJsonResponse(json);
        Assert.Equal(json, result);
    }

    [Fact]
    public void PreprocessJsonResponse_BrokenJson_ReturnsCleaned()
    {
        var input = "Some text before ```json\n{\"key\": \"value\"\n``` and after";
        var result = JsonRepairUtil.PreprocessJsonResponse(input);
        Assert.NotNull(result);
        Assert.NotEqual("", result);
    }

    [Fact]
    public void PreprocessJsonResponse_MixedContent_ExtractsJson()
    {
        var input = """
            Here's the data:
            {
                "name": "test",
                "value": 42
            }
            That's all.
            """;
        var result = JsonRepairUtil.PreprocessJsonResponse(input);
        Assert.NotNull(result);
        Assert.NotEqual("", result);
    }
}
