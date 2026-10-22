using Core.Data;
using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// JsonSchemaHelper, MarkdownHelper, and Data Models 综合测试。
/// </summary>
public class DataInfrastructureTests
{
    // ══════════════════════════════════════════════════════
    //  JsonSchemaHelper
    // ══════════════════════════════════════════════════════

    [Fact]
    public void GenerateSchema_SimpleType_ContainsProperties()
    {
        var schema = JsonSchemaHelper.GenerateSchema<SimpleParams>();
        Assert.NotNull(schema);

        var raw = schema!.Value.GetRawText();
        Assert.Contains("name", raw);
        Assert.Contains("count", raw);
        Assert.Contains("object", raw); // JSON Schema type
    }

    [Fact]
    public void GenerateSchema_StringProperty_HasStringType()
    {
        var schema = JsonSchemaHelper.GenerateSchema<StringOnlyParams>();
        var raw = schema!.Value.GetRawText();

        Assert.Contains("\"type\":\"string\"", raw);
    }

    [Fact]
    public void GenerateSchema_IntProperty_HasIntegerType()
    {
        var schema = JsonSchemaHelper.GenerateSchema<IntOnlyParams>();
        var raw = schema!.Value.GetRawText();

        Assert.Contains("\"type\":\"integer\"", raw);
    }

    [Fact]
    public void GenerateSchema_BoolProperty_HasBooleanType()
    {
        var schema = JsonSchemaHelper.GenerateSchema<BoolOnlyParams>();
        var raw = schema!.Value.GetRawText();

        Assert.Contains("\"type\":\"boolean\"", raw);
    }

    // ══════════════════════════════════════════════════════
    //  MarkdownHelper
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ParseMarkdown_WithMetadata_ExtractsBoth()
    {
        var md = """
            ---
            id: chapter_1
            title: 第一章
            order: "1"
            ---

            这是正文内容。
            """;

        var (meta, body) = MarkdownHelper.ParseMarkdown(md);
        Assert.Equal("chapter_1", meta["id"]);
        Assert.Equal("第一章", meta["title"]);
        Assert.Equal("1", meta["order"]);
        Assert.Contains("这是正文内容。", body);
    }

    [Fact]
    public void ParseMarkdown_NoMetadata_ReturnsEmptyMeta()
    {
        var md = "纯正文内容，无元数据。";

        var (meta, body) = MarkdownHelper.ParseMarkdown(md);
        Assert.Empty(meta);
        Assert.Equal("纯正文内容，无元数据。", body);
    }

    [Fact]
    public void WriteMarkdown_AndParse_Roundtrip()
    {
        var meta = new Dictionary<string, string>
        {
            ["id"] = "test001",
            ["title"] = "测试",
            ["order"] = "5",
        };
        var body = "正文\n多行\n内容";

        var md = MarkdownHelper.WriteMarkdown(meta, body);
        var (parsedMeta, parsedBody) = MarkdownHelper.ParseMarkdown(md);

        Assert.Equal("test001", parsedMeta["id"]);
        Assert.Equal("测试", parsedMeta["title"]);
        Assert.Equal("5", parsedMeta["order"]);
        Assert.Equal(body, parsedBody);
    }

    // ══════════════════════════════════════════════════════
    //  Data Models — Defaults
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Entity_Id_DefaultsTo12CharGuid()
    {
        var entity = new Novel();
        Assert.NotNull(entity.Id);
        Assert.Equal(12, entity.Id.Length);
    }

    [Fact]
    public void Novel_DefaultLists_AreEmpty()
    {
        var novel = new Novel();
        Assert.Empty(novel.Chapters);
        Assert.Empty(novel.Characters);
        Assert.Empty(novel.Plotlines);
        Assert.Empty(novel.Factions);
        Assert.Empty(novel.Locations);
        Assert.Empty(novel.Items);
        Assert.Empty(novel.TimelineEvents);
        Assert.Empty(novel.Relationships);
        Assert.Empty(novel.Scenes);
    }

    [Fact]
    public void Character_DefaultRole_IsSupporting()
    {
        var character = new Character();
        Assert.Equal(CharacterRole.Supporting, character.Role);
    }

    [Fact]
    public void Character_DefaultTags_IsEmpty()
    {
        var character = new Character();
        Assert.Empty(character.Tags);
    }

    [Fact]
    public void Chapter_DefaultStatus_IsDraft()
    {
        var chapter = new Chapter();
        Assert.Equal(ChapterStatus.Draft, chapter.Status);
    }

    [Fact]
    public void Plotline_DefaultType_IsMain()
    {
        var plotline = new Plotline();
        Assert.Equal(PlotlineType.Main, plotline.Type);
    }

    [Fact]
    public void Plotline_DefaultTension_IsMedium()
    {
        var plotline = new Plotline();
        Assert.Equal(PlotlineTension.Medium, plotline.Tension);
    }

    [Fact]
    public void Relationship_DefaultType_IsFriend()
    {
        var rel = new Relationship();
        Assert.Equal(RelationshipType.Friend, rel.Type);
    }

    [Fact]
    public void Relationship_DefaultStrength_IsMedium()
    {
        var rel = new Relationship();
        Assert.Equal(RelationshipStrength.Medium, rel.Strength);
    }

    [Fact]
    public void SceneDefinition_DefaultType_IsDialogue()
    {
        var scene = new SceneDefinition();
        Assert.Equal(SceneType.Dialogue, scene.SceneType);
    }

    [Fact]
    public void SceneDefinition_DefaultLists_AreEmpty()
    {
        var scene = new SceneDefinition();
        Assert.Empty(scene.ParticipantIds);
        Assert.Empty(scene.PlotlineIds);
    }

    [Fact]
    public void Faction_DefaultType_IsSect()
    {
        var faction = new Faction();
        Assert.Equal(FactionType.Sect, faction.Type);
    }

    [Fact]
    public void Faction_DefaultMembers_IsEmpty()
    {
        var faction = new Faction();
        Assert.Empty(faction.Members);
    }

    [Fact]
    public void Location_DefaultType_IsCity()
    {
        var location = new Location();
        Assert.Equal(LocationType.City, location.Type);
    }

    [Fact]
    public void Item_DefaultType_IsArtifact()
    {
        var item = new Item();
        Assert.Equal(ItemType.Artifact, item.Type);
    }

    [Fact]
    public void AgentSession_DefaultId_Is12CharGuid()
    {
        var session = new AgentSession();
        Assert.Equal(12, session.Id.Length);
    }

    [Fact]
    public void ChatMessage_DefaultRole_IsUser()
    {
        var msg = new ChatMessage();
        Assert.Equal(MessageRole.User, msg.Role);
    }

    [Fact]
    public void EntityAuditLog_DefaultAction_IsCreate()
    {
        var log = new EntityAuditLog();
        Assert.Equal(AuditAction.Create, log.Action);
    }

    [Fact]
    public void EntityAuditLog_DefaultSource_IsAi()
    {
        var log = new EntityAuditLog();
        Assert.Equal(AuditSource.Ai, log.Source);
    }

    // ══════════════════════════════════════════════════════
    //  EntityTypeExtensions
    // ══════════════════════════════════════════════════════

    [Fact]
    public void EntityType_ToSnakeCase_Roundtrip()
    {
        foreach (var type in Enum.GetValues<EntityType>())
        {
            var kebab = type.ToKebabCaseString();
            var parsed = EntityTypeExtensions.ParseEntityType(kebab);
            Assert.Equal(type, parsed);
        }
    }

    [Fact]
    public void ParseEntityType_Invalid_ReturnsNull()
    {
        Assert.Null(EntityTypeExtensions.ParseEntityType("not_a_real_type"));
        Assert.Null(EntityTypeExtensions.ParseEntityType(null));
    }

    // ══════════════════════════════════════════════════════
    //  Helper parameter classes for schema tests
    // ══════════════════════════════════════════════════════

    private class SimpleParams
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private class StringOnlyParams
    {
        public string Value { get; set; } = "";
    }

    private class IntOnlyParams
    {
        public int Number { get; set; }
    }

    private class BoolOnlyParams
    {
        public bool Flag { get; set; }
    }
}
