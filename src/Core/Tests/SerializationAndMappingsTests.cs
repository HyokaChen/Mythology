using System.Text.Json;
using Core.Data;
using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// JsonlDto / ContentBlock / ThinkingLevel / MessageRoleMapping 综合测试。
/// </summary>
public class SerializationAndMappingsTests
{
    // ══════════════════════════════════════════════════════
    //  ContentBlock
    // ══════════════════════════════════════════════════════

    [Fact]
    public void TextContent_CreatesWithText()
    {
        var tc = new TextContent("hello world");
        Assert.Equal("hello world", tc.Text);
    }

    [Fact]
    public void TextContent_EmptyString_Works()
    {
        var tc = new TextContent("");
        Assert.Equal("", tc.Text);
    }

    [Fact]
    public void TextContent_RecordEquality_ByValue()
    {
        var a = new TextContent("same");
        var b = new TextContent("same");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ImageContent_CreatesWithUrl()
    {
        var img = new ImageContent("https://example.com/img.png", "alt text");
        Assert.Equal("https://example.com/img.png", img.Url);
        Assert.Equal("alt text", img.AltText);
    }

    [Fact]
    public void ImageContent_AltTextOptional()
    {
        var img = new ImageContent("url");
        Assert.Null(img.AltText);
    }

    // ══════════════════════════════════════════════════════
    //  ThinkingLevel & ThinkingLevelBudgets
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData("off", ThinkingLevel.Off, 0, "medium")] // effort default
    [InlineData("minimal", ThinkingLevel.Minimal, 128, "low")]
    [InlineData("low", ThinkingLevel.Low, 512, "low")]
    [InlineData("medium", ThinkingLevel.Medium, 1024, "medium")]
    [InlineData("high", ThinkingLevel.High, 4096, "high")]
    [InlineData("xhigh", ThinkingLevel.XHigh, 8192, "high")]
    public void ThinkingLevel_FromString_MapsCorrectly(
        string input,
        ThinkingLevel level,
        int budget,
        string effort
    )
    {
        Assert.Equal(level, ThinkingLevelBudgets.FromString(input));
        Assert.Equal(budget, ThinkingLevelBudgets.GetBudgetTokens(level));
        Assert.Equal(effort, ThinkingLevelBudgets.GetReasoningEffort(level));
    }

    [Fact]
    public void ThinkingLevel_FromString_Null_DefaultsToMedium()
    {
        Assert.Equal(ThinkingLevel.Medium, ThinkingLevelBudgets.FromString(null));
    }

    [Fact]
    public void ThinkingLevel_FromString_Unknown_DefaultsToMedium()
    {
        Assert.Equal(ThinkingLevel.Medium, ThinkingLevelBudgets.FromString("ultra-high"));
    }

    [Fact]
    public void ThinkingLevel_FromString_CaseInsensitive()
    {
        Assert.Equal(ThinkingLevel.High, ThinkingLevelBudgets.FromString("HIGH"));
        Assert.Equal(ThinkingLevel.Off, ThinkingLevelBudgets.FromString("Off"));
    }

    [Fact]
    public void ThinkingLevel_Options_HasFiveNonOffLevels()
    {
        Assert.Equal(5, ThinkingLevelBudgets.Options.Length);
        Assert.All(ThinkingLevelBudgets.Options, o => Assert.NotEqual("off", o.Key));
    }

    [Fact]
    public void ThinkingLevel_AllEnumValues_Covered()
    {
        foreach (var level in Enum.GetValues<ThinkingLevel>())
        {
            var budget = ThinkingLevelBudgets.GetBudgetTokens(level);
            var effort = ThinkingLevelBudgets.GetReasoningEffort(level);
            Assert.True(budget >= 0);
            Assert.NotEmpty(effort);
        }
    }

    // ══════════════════════════════════════════════════════
    //  MessageRoleMapping
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ToMessageRole_User_Roundtrip()
    {
        Assert.Equal(
            MessageRole.User,
            MessageRoleMapping.ToMessageRole(LlmTornado.Code.ChatMessageRoles.User)
        );
    }

    [Fact]
    public void ToChatMessageRoles_System_Roundtrip()
    {
        Assert.Equal(
            LlmTornado.Code.ChatMessageRoles.System,
            MessageRoleMapping.ToChatMessageRoles(MessageRole.System)
        );
    }

    [Fact]
    public void ToRoleString_Assistant_ReturnsAssistant()
    {
        Assert.Equal(
            "assistant",
            MessageRoleMapping.ToRoleString(LlmTornado.Code.ChatMessageRoles.Assistant)
        );
    }

    [Fact]
    public void ToRoleString_Tool_ReturnsTool()
    {
        Assert.Equal(
            "tool",
            MessageRoleMapping.ToRoleString(LlmTornado.Code.ChatMessageRoles.Tool)
        );
    }

    [Fact]
    public void ToRoleString_Unknown_ReturnsUnknown()
    {
        Assert.Equal(
            "unknown",
            MessageRoleMapping.ToRoleString(LlmTornado.Code.ChatMessageRoles.Unknown)
        );
    }

    [Fact]
    public void ToMessageRole_String_User_Roundtrip()
    {
        Assert.Equal(MessageRole.User, MessageRoleMapping.ToMessageRole("user"));
    }

    [Fact]
    public void ToMessageRole_String_Unknown_ReturnsUnknown()
    {
        Assert.Equal(MessageRole.Unknown, MessageRoleMapping.ToMessageRole("unknown"));
    }

    [Fact]
    public void ToMessageRole_String_Invalid_ReturnsUser()
    {
        Assert.Equal(MessageRole.User, MessageRoleMapping.ToMessageRole("invalid_role"));
    }

    [Fact]
    public void ToMessageRole_String_CaseInsensitive()
    {
        Assert.Equal(MessageRole.Assistant, MessageRoleMapping.ToMessageRole("ASSISTANT"));
        Assert.Equal(MessageRole.Tool, MessageRoleMapping.ToMessageRole("Tool"));
    }

    [Fact]
    public void FullRoundtrip_AllRoles()
    {
        var roles = new[] { "system", "user", "assistant", "tool", "unknown" };
        foreach (var r in roles)
        {
            var messageRole = MessageRoleMapping.ToMessageRole(r);
            var chatRole = MessageRoleMapping.ToChatMessageRoles(messageRole);
            var roleStr = MessageRoleMapping.ToRoleString(chatRole);
            Assert.Equal(r, roleStr);
        }
    }

    // ══════════════════════════════════════════════════════
    //  JsonlDto — Serialization roundtrip
    // ══════════════════════════════════════════════════════

    [Fact]
    public void JsonlMessageEntry_Serialization_Roundtrip()
    {
        var entry = new JsonlMessageEntry
        {
            Id = "abc123",
            ParentId = "parent456",
            Timestamp = "2025-01-15T10:30:00Z",
            Role = "user",
            Content = "Hello, world!",
            ReasoningContent = null,
            Order = 5,
        };

        var json = JsonSerializer.Serialize(entry, JsonlSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<JsonlMessageEntry>(
            json,
            JsonlSerializer.Options
        );

        Assert.NotNull(deserialized);
        Assert.Equal("abc123", deserialized!.Id);
        Assert.Equal("parent456", deserialized.ParentId);
        Assert.Equal("user", deserialized.Role);
        Assert.Equal("Hello, world!", deserialized.Content);
        Assert.Equal(5, deserialized.Order);
        Assert.Equal(JsonlEntryType.Message, deserialized.Type);
    }

    [Fact]
    public void JsonlCompactEntry_Serialization_Roundtrip()
    {
        var entry = new JsonlCompactEntry
        {
            Id = "compact1",
            Summary = "Previous conversation summary",
            FirstKeptEntryId = "keep001",
            MessagesBefore = 42,
            Order = 100,
            Reason = "context-overflow",
        };

        var json = JsonSerializer.Serialize(entry, JsonlSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<JsonlCompactEntry>(
            json,
            JsonlSerializer.Options
        );

        Assert.NotNull(deserialized);
        Assert.Equal("compact1", deserialized!.Id);
        Assert.Equal("Previous conversation summary", deserialized.Summary);
        Assert.Equal(42, deserialized.MessagesBefore);
        Assert.Equal("context-overflow", deserialized.Reason);
        Assert.Equal(JsonlEntryType.Compact, deserialized.Type);
    }

    [Fact]
    public void JsonlEventEntry_Serialization_Roundtrip()
    {
        var entry = new JsonlEventEntry
        {
            Id = "evt1",
            EventType = SessionEventType.ModelChange,
            Order = 10,
            From = "gpt-4o",
            To = "claude-sonnet",
        };

        var json = JsonSerializer.Serialize(entry, JsonlSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<JsonlEventEntry>(
            json,
            JsonlSerializer.Options
        );

        Assert.NotNull(deserialized);
        Assert.Equal(SessionEventType.ModelChange, deserialized!.EventType);
        Assert.Equal("gpt-4o", deserialized.From);
        Assert.Equal("claude-sonnet", deserialized.To);
    }

    [Fact]
    public void JsonlLeafEntry_Serialization_Roundtrip()
    {
        var entry = new JsonlLeafEntry { TargetId = "target999" };

        var json = JsonSerializer.Serialize(entry, JsonlSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<JsonlLeafEntry>(
            json,
            JsonlSerializer.Options
        );

        Assert.NotNull(deserialized);
        Assert.Equal("target999", deserialized!.TargetId);
        Assert.Equal(JsonlEntryType.Leaf, deserialized.Type);
    }

    [Fact]
    public void JsonlLabelEntry_Serialization_Roundtrip()
    {
        var entry = new JsonlLabelEntry
        {
            Id = "lbl1",
            TargetId = "msg123",
            Label = "重要转折",
            Order = 15,
        };

        var json = JsonSerializer.Serialize(entry, JsonlSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<JsonlLabelEntry>(
            json,
            JsonlSerializer.Options
        );

        Assert.NotNull(deserialized);
        Assert.Equal("msg123", deserialized!.TargetId);
        Assert.Equal("重要转折", deserialized.Label);
    }

    [Fact]
    public void JsonlBranchSummaryEntry_Serialization_Roundtrip()
    {
        var entry = new JsonlBranchSummaryEntry
        {
            Id = "bs1",
            FromId = "branch_start",
            Summary = "分支摘要：主角选择了另一条路",
            Order = 20,
        };

        var json = JsonSerializer.Serialize(entry, JsonlSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<JsonlBranchSummaryEntry>(
            json,
            JsonlSerializer.Options
        );

        Assert.NotNull(deserialized);
        Assert.Equal("分支摘要：主角选择了另一条路", deserialized!.Summary);
    }

    [Fact]
    public void JsonlTypeProbe_DetectsMessageType()
    {
        var json = """{"type":"message","id":"x"}""";
        var probe = JsonSerializer.Deserialize<JsonlTypeProbe>(json, JsonlSerializer.Options);

        Assert.NotNull(probe);
        Assert.Equal(JsonlEntryType.Message, probe!.Type);
    }

    [Fact]
    public void ToolCallItemDto_DeserializeList_ValidJson()
    {
        var json = """[{"id":"call_1","name":"search","arguments":"{}"}]""";
        var result = ToolCallItemDto.DeserializeList(json);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("call_1", result[0].Id);
        Assert.Equal("search", result[0].Name);
    }

    [Fact]
    public void ToolCallItemDto_DeserializeList_Null_ReturnsNull()
    {
        Assert.Null(ToolCallItemDto.DeserializeList(null));
    }

    [Fact]
    public void ToolCallItemDto_DeserializeList_Empty_ReturnsNull()
    {
        Assert.Null(ToolCallItemDto.DeserializeList(""));
    }

    [Fact]
    public void ToolCallItemDto_DeserializeList_InvalidJson_ReturnsNull()
    {
        Assert.Null(ToolCallItemDto.DeserializeList("not json"));
    }

    // ══════════════════════════════════════════════════════
    //  JsonlAuditEntry
    // ══════════════════════════════════════════════════════

    [Fact]
    public void JsonlAuditEntry_Serialization_Roundtrip()
    {
        var entry = new JsonlAuditEntry
        {
            NovelId = "novel1",
            EntityType = "character",
            EntityId = "char123",
            Action = "update",
            FieldName = "name",
            OldValue = "old_name",
            NewValue = "new_name",
            Source = "ai",
            ToolName = "manage_character",
        };

        var json = JsonSerializer.Serialize(entry, JsonHelper.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<JsonlAuditEntry>(
            json,
            JsonHelper.JsonOptions
        );

        Assert.NotNull(deserialized);
        Assert.Equal("character", deserialized!.EntityType);
        Assert.Equal("update", deserialized.Action);
        Assert.Equal("old_name", deserialized.OldValue);
        Assert.Equal("new_name", deserialized.NewValue);
    }

    // ══════════════════════════════════════════════════════
    //  JsonlSerializer options
    // ══════════════════════════════════════════════════════

    [Fact]
    public void JsonlSerializer_Options_UsesKebabCase()
    {
        var entry = new JsonlMessageEntry { Id = "test", ParentId = "parent" };
        var json = JsonSerializer.Serialize(entry, JsonlSerializer.Options);

        Assert.Contains("\"parent-id\"", json); // kebab-case（全仓统一命名）
        Assert.DoesNotContain("\"parentId\"", json);
    }

    [Fact]
    public void JsonlSerializer_NullProperties_Omitted()
    {
        var entry = new JsonlMessageEntry
        {
            Id = "t1",
            Content = "c",
            ToolName = null,
        };
        var json = JsonSerializer.Serialize(entry, JsonlSerializer.Options);

        Assert.DoesNotContain("\"toolName\"", json);
    }

    // ══════════════════════════════════════════════════════
    //  JsonHelper options
    // ══════════════════════════════════════════════════════

    [Fact]
    public void JsonHelper_Options_UsesKebabCase()
    {
        var data = new { NovelId = "n1", ChapterCount = 5 };
        var json = JsonSerializer.Serialize(data, JsonHelper.JsonOptions);

        Assert.Contains("\"novel-id\"", json);
        Assert.Contains("\"chapter-count\"", json);
    }

    [Fact]
    public void JsonHelper_Options_ReadsNumbersFromStrings()
    {
        // LLM 工具调用常把 int 字段传为字符串（如 "order": "5"），必须能反序列化
        var json = """{"novelId":"n1","order":"5"}""";
        var data = JsonSerializer.Deserialize<OrderParams>(json, JsonHelper.JsonOptions);

        Assert.NotNull(data);
        Assert.Equal(5, data.Order);
    }

    [Fact]
    public void JsonHelper_ToleratesStringifiedList()
    {
        // LLM 常把 List<string> 参数误传为字符串：JSON 数组字符串、逗号分隔、或真数组，都必须容错。
        var jsonArray = """{"character-ids":["a","b","c"]}""";
        var commaSep = """{"character-ids":"a, b, c"}""";
        var stringified = """{"character-ids":"[\"a\",\"b\"]"}""";

        Assert.Equal(
            new[] { "a", "b", "c" },
            JsonSerializer.Deserialize<ListParam>(jsonArray, JsonHelper.JsonOptions)!.CharacterIds
        );
        Assert.Equal(
            new[] { "a", "b", "c" },
            JsonSerializer.Deserialize<ListParam>(commaSep, JsonHelper.JsonOptions)!.CharacterIds
        );
        Assert.Equal(
            new[] { "a", "b" },
            JsonSerializer.Deserialize<ListParam>(stringified, JsonHelper.JsonOptions)!.CharacterIds
        );

        // 回归：序列化方向不得经 options 递归回本转换器（曾导致栈溢出打崩测试宿主）。
        var roundTrip = JsonSerializer.Deserialize<ListParam>(
            JsonSerializer.Serialize(
                new ListParam { CharacterIds = ["x", "y"] },
                JsonHelper.JsonOptions
            ),
            JsonHelper.JsonOptions
        )!;
        Assert.Equal(new[] { "x", "y" }, roundTrip.CharacterIds);
    }

    private sealed record ListParam
    {
        public List<string>? CharacterIds { get; init; }
    }

    private sealed record OrderParams
    {
        public string NovelId { get; init; } = "";
        public int Order { get; init; }
    }
}
