using Core.Data;
using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// TUI 侧 `#` 引用契约：类型表、规范显示名、插入文本格式必须与 Desktop 同口径。
/// （补全/内联的具体路径由 SetUpPanelLayoutTests 的实体名册用例与
/// Core 的 EntityReferenceMatcherTests 共同锁定。）
/// </summary>
public sealed class TuiEntityRefTests
{
    [Fact]
    public void HashDisplayTypes_CoverAllReferenceableTabTypes()
    {
        Assert.Equal(
            ["角色", "组织", "地点", "情节", "场景", "道具", "关系", "章节", "分卷", "小说"],
            EntityDisplayHelper.HashDisplayTypes
        );
    }

    [Fact]
    public void ToEntityType_MapsNewTypesAndLegacyAlias()
    {
        Assert.Equal(EntityType.Scene, EntityDisplayHelper.ToEntityType("场景"));
        Assert.Equal(EntityType.Chapter, EntityDisplayHelper.ToEntityType("章节"));
        Assert.Equal(EntityType.Chapter, EntityDisplayHelper.ToEntityType("大纲"));
        Assert.Equal(EntityType.Volume, EntityDisplayHelper.ToEntityType("分卷"));
        Assert.Equal(EntityType.Novel, EntityDisplayHelper.ToEntityType("小说"));
        Assert.Null(EntityDisplayHelper.ToEntityType("世界规则"));
    }

    [Fact]
    public void FormatEntityReference_EmitsHashSyntaxForEveryEntryType()
    {
        Assert.Equal(
            "#分卷:卷一 风起 ",
            TuiApp.FormatEntityReference(EntityType.Volume, "卷一 风起")
        );
        Assert.Equal(
            "#章节:雨夜 寄北 ",
            TuiApp.FormatEntityReference(EntityType.Chapter, "雨夜 寄北")
        );
        Assert.Equal(
            "#场景:初遇 在雨里 ",
            TuiApp.FormatEntityReference(EntityType.Scene, "初遇 在雨里")
        );
        Assert.Equal("#小说:测试小说 ", TuiApp.FormatEntityReference(EntityType.Novel, "测试小说"));
        Assert.Equal("#角色:张三 ", TuiApp.FormatEntityReference(EntityType.Character, "张三"));
    }
}
