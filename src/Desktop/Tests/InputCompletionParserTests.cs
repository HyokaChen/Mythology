using Core.Data;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 输入补全语法单测：@ 提及（MentionParser）与 # 实体引用（EntityRefParser）。
/// 覆盖「裸 @ / 裸 # 必须仍触发弹窗」与「Enter 换行必须关弹窗」两条回归。
/// </summary>
public sealed class InputCompletionParserTests
{
    // ─────────── @ 提及 ───────────

    [Fact]
    public void Mention_AtEnd_IsFound()
    {
        var mention = MentionParser.TryGetMention("@张", 2);
        Assert.True(mention.Found);
        Assert.Equal("张", mention.Query);
        Assert.Equal(0, mention.Start);
    }

    [Fact]
    public void Mention_EmptyQuery_IsStillFound()
    {
        var mention = MentionParser.TryGetMention("你好 @", 4);
        Assert.True(mention.Found);
        Assert.Equal("", mention.Query);
        Assert.Equal(3, mention.Start);
    }

    [Fact]
    public void Mention_BeforeNewline_IsNotFound()
    {
        // 回归：`$` 也会匹配字符串末尾 \n 之前，Enter 换行后弹窗不能残留
        Assert.False(MentionParser.TryGetMention("@张\n", 3).Found);
    }

    [Fact]
    public void Mention_NotAtCaret_IsNotFound()
    {
        Assert.False(MentionParser.TryGetMention("@张 后", 5).Found);
    }

    // ─────────── # 实体引用 ───────────

    [Fact]
    public void EntityRef_TypeStage_BareHash()
    {
        var reference = EntityRefParser.TryParse("#", 1);
        Assert.Equal(EntityRefParser.Stage.Type, reference.Stage);
        Assert.Equal("", reference.Query);
        Assert.Equal(0, reference.Start);
    }

    [Fact]
    public void EntityRef_TypeStage_PartialType()
    {
        var reference = EntityRefParser.TryParse("#角", 2);
        Assert.Equal(EntityRefParser.Stage.Type, reference.Stage);
        Assert.Equal("角", reference.Query);
    }

    [Fact]
    public void EntityRef_NameStage()
    {
        var reference = EntityRefParser.TryParse("#角色:张", 5);
        Assert.Equal(EntityRefParser.Stage.Name, reference.Stage);
        Assert.Equal("角色", reference.TypeDisplay);
        Assert.Equal("张", reference.Query);
        Assert.Equal(0, reference.Start);
    }

    [Fact]
    public void EntityRef_EmptyTypeDisplay_IsNone()
    {
        Assert.Equal(EntityRefParser.Stage.None, EntityRefParser.TryParse("#:张", 3).Stage);
    }

    [Fact]
    public void EntityRef_SpaceAfterHash_IsNone()
    {
        Assert.Equal(EntityRefParser.Stage.None, EntityRefParser.TryParse("# ", 2).Stage);
    }

    [Fact]
    public void EntityRef_TrailingSpaceAfterName_IsNone()
    {
        Assert.Equal(EntityRefParser.Stage.None, EntityRefParser.TryParse("#角色:张三 ", 7).Stage);
    }

    [Fact]
    public void EntityRef_UnknownType_ToEntityTypeIsNull()
    {
        var reference = EntityRefParser.TryParse("#xyz:1", 6);
        Assert.Equal(EntityRefParser.Stage.Name, reference.Stage);
        Assert.Null(EntityRefParser.ToEntityType(reference.TypeDisplay));
    }

    [Fact]
    public void InsertType_ReplacesPrefix()
    {
        var reference = EntityRefParser.TryParse("#角", 2);
        var (text, caret) = EntityRefParser.InsertType("#角", 2, reference, "角色");
        Assert.Equal("#角色:", text);
        Assert.Equal(4, caret);
    }

    [Fact]
    public void InsertName_ReplacesQueryAndAppendsSpace()
    {
        var reference = EntityRefParser.TryParse("#角色:张", 5);
        var (text, caret) = EntityRefParser.InsertName("#角色:张", 5, reference, "张三");
        Assert.Equal("#角色:张三 ", text);
        Assert.Equal(7, caret); // 光标落在尾随空格之后
    }

    [Fact]
    public void InlineRegex_MatchesTypeNamePairs()
    {
        var types = EntityRefParser
            .InlineRegex()
            .Matches("见 #角色:张三 与 #地点:凌霄峰")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(["角色", "地点"], types);
        Assert.Empty(EntityRefParser.InlineRegex().Matches("#123"));
    }

    [Fact]
    public void DisplayTypes_AreSevenTypesInTuiOrder()
    {
        Assert.Equal(
            ["角色", "组织", "地点", "情节", "道具", "关系", "大纲"],
            EntityRefParser.DisplayTypes
        );
    }

    [Fact]
    public void ToEntityType_MapsAllSevenDisplayTypes()
    {
        Assert.Equal(EntityType.Character, EntityRefParser.ToEntityType("角色"));
        Assert.Equal(EntityType.Faction, EntityRefParser.ToEntityType("组织"));
        Assert.Equal(EntityType.Location, EntityRefParser.ToEntityType("地点"));
        Assert.Equal(EntityType.Plotline, EntityRefParser.ToEntityType("情节"));
        Assert.Equal(EntityType.Item, EntityRefParser.ToEntityType("道具"));
        Assert.Equal(EntityType.Relationship, EntityRefParser.ToEntityType("关系"));
        Assert.Equal(EntityType.Chapter, EntityRefParser.ToEntityType("大纲"));
    }
}
