using Core.Data;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 输入补全语法单测：@ 文件路径（MentionParser）与 # 实体引用（EntityRefParser）。
/// 覆盖「裸 @ / 裸 # 必须仍触发弹窗」「Enter 换行必须关弹窗」「含空格路径不断词」三条回归。
/// </summary>
public sealed class InputCompletionParserTests
{
    // ─────────── @ 文件路径 ───────────

    [Fact]
    public void Mention_AtEnd_IsFound()
    {
        var mention = MentionParser.TryGetMention("@卷一", 3);
        Assert.True(mention.Found);
        Assert.Equal("卷一", mention.Query);
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
    public void Mention_PathWithSpaces_IsFound()
    {
        // B2：含空格目录名（macOS 常见）不断词，光标紧贴即命中
        var mention = MentionParser.TryGetMention("@卷一 风起/", 8);
        Assert.True(mention.Found);
        Assert.Equal("卷一 风起/", mention.Query);
    }

    [Fact]
    public void Mention_AbsoluteAndHomePaths_AreFound()
    {
        Assert.True(MentionParser.TryGetMention("@/usr/l", 7).Found);
        Assert.True(MentionParser.TryGetMention("@~/Docum", 8).Found);
        Assert.True(MentionParser.TryGetMention("@./rel", 6).Found);
    }

    [Fact]
    public void Mention_IsPathIntent_Classifies()
    {
        Assert.True(MentionParser.IsPathIntent(""));
        Assert.True(MentionParser.IsPathIntent("/usr"));
        Assert.True(MentionParser.IsPathIntent("./rel"));
        Assert.True(MentionParser.IsPathIntent("~/x"));
        Assert.True(MentionParser.IsPathIntent("卷一/细"));
        Assert.False(MentionParser.IsPathIntent("卷一"));
    }

    [Fact]
    public void Mention_CaretAfterNewline_IsNotFound()
    {
        // 回归：光标在换行之后（如粘贴多行后光标在第二行）→ @ 在上一行，不能跨行匹配
        Assert.False(MentionParser.TryGetMention("@卷一\n", 4).Found);
        Assert.False(MentionParser.TryGetMention("@卷一\n后", 5).Found);
    }

    [Fact]
    public void Mention_OnlyAtCaretEnd_IsNotFound_BeforeAtSign()
    {
        // 光标在 @ 之前 → 不构成提及
        Assert.False(MentionParser.TryGetMention("看这 @卷", 2).Found);
    }

    [Fact]
    public void InsertSuggestion_DirectoryKeepsSlash_FileGetsSpace()
    {
        // "请看 @卷"：@=3，卷=4，光标在卷后=5（query=卷，Start=3）
        var (dirText, dirCaret) = MentionParser.InsertSuggestion("请看 @卷", 5, 3, "卷一 风起/");
        Assert.Equal("请看 @卷一 风起/", dirText);
        Assert.Equal("请看 @卷一 风起/".Length, dirCaret);

        var (fileText, fileCaret) = MentionParser.InsertSuggestion("请看 @序", 5, 3, "序章.txt");
        Assert.Equal("请看 @序章.txt ", fileText);
        Assert.Equal("请看 @序章.txt ".Length, fileCaret);
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
    public void StartRegex_MatchesTypePrefixes()
    {
        var types = EntityRefParser
            .StartRegex()
            .Matches("见 #角色:张三 与 #地点:凌霄峰")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(["角色", "地点"], types);
        Assert.Empty(EntityRefParser.StartRegex().Matches("#123"));
    }

    [Fact]
    public void DisplayTypes_AreAllReferenceableTabTypes()
    {
        Assert.Equal(
            ["角色", "组织", "地点", "情节", "场景", "道具", "关系", "章节", "分卷", "小说"],
            EntityRefParser.DisplayTypes
        );
    }

    [Fact]
    public void ToEntityType_MapsAllDisplayTypesAndLegacyAlias()
    {
        Assert.Equal(EntityType.Character, EntityRefParser.ToEntityType("角色"));
        Assert.Equal(EntityType.Faction, EntityRefParser.ToEntityType("组织"));
        Assert.Equal(EntityType.Location, EntityRefParser.ToEntityType("地点"));
        Assert.Equal(EntityType.Plotline, EntityRefParser.ToEntityType("情节"));
        Assert.Equal(EntityType.Scene, EntityRefParser.ToEntityType("场景"));
        Assert.Equal(EntityType.Item, EntityRefParser.ToEntityType("道具"));
        Assert.Equal(EntityType.Relationship, EntityRefParser.ToEntityType("关系"));
        Assert.Equal(EntityType.Chapter, EntityRefParser.ToEntityType("章节"));
        Assert.Equal(EntityType.Chapter, EntityRefParser.ToEntityType("大纲"));
        Assert.Equal(EntityType.Volume, EntityRefParser.ToEntityType("分卷"));
        Assert.Equal(EntityType.Novel, EntityRefParser.ToEntityType("小说"));
    }

    [Fact]
    public void DisplayName_MapsEveryReferenceableType()
    {
        Assert.Equal("章节", EntityRefParser.DisplayName(EntityType.Chapter));
        Assert.Equal("分卷", EntityRefParser.DisplayName(EntityType.Volume));
        Assert.Equal("小说", EntityRefParser.DisplayName(EntityType.Novel));
        Assert.Equal("场景", EntityRefParser.DisplayName(EntityType.Scene));
    }
}
