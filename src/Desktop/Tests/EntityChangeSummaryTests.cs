using Core.Data;
using Xunit;

namespace Desktop.Tests;

/// <summary>实体变更状态行文案：TUI 与 Desktop 共用，字段名/小说级过滤是契约。</summary>
public class EntityChangeSummaryTests
{
    [Fact]
    public void Create_DescribesEntityAndName()
    {
        var evt = new EntityChangeEvent(
            EntityType.Character,
            "char_1",
            AuditAction.Create,
            "novel_1",
            "林月如"
        );

        Assert.Equal("新建角色「林月如」", EntityChangeSummary.Describe(evt));
    }

    [Fact]
    public void UpdateWithFields_ListsChangedKeys()
    {
        var evt = new EntityChangeEvent(
            EntityType.Character,
            "char_1",
            AuditAction.Update,
            "novel_1",
            "林月如",
            new Dictionary<string, string> { ["Personality"] = "旧 → 新", ["Goal"] = "旧 → 新" }
        );

        Assert.Equal("修改角色「林月如」: Personality, Goal", EntityChangeSummary.Describe(evt));
    }

    [Fact]
    public void UpdateWithoutFields_OmitsColonSuffix()
    {
        var evt = new EntityChangeEvent(
            EntityType.Location,
            "loc_1",
            AuditAction.Update,
            "novel_1",
            "苏州城"
        );

        Assert.Equal("修改地点「苏州城」", EntityChangeSummary.Describe(evt));
    }

    [Fact]
    public void Delete_DescribesEntity()
    {
        var evt = new EntityChangeEvent(
            EntityType.Faction,
            "faction_1",
            AuditAction.Delete,
            "novel_1",
            "林家堡"
        );

        Assert.Equal("删除组织「林家堡」", EntityChangeSummary.Describe(evt));
    }

    [Fact]
    public void NovelLevelChange_IsHidden()
    {
        var evt = new EntityChangeEvent(
            EntityType.Novel,
            "novel_1",
            AuditAction.Update,
            "novel_1",
            "仙剑"
        );

        Assert.Null(EntityChangeSummary.Describe(evt));
        Assert.False(EntityChangeSummary.ShouldShowInChat(evt));
    }

    [Fact]
    public void ToolDrivenChange_IsCoveredByToolStep_NotDuplicatedInChat()
    {
        var evt = new EntityChangeEvent(
            EntityType.Plotline,
            "pl_1",
            AuditAction.Update,
            "novel_1",
            "卷二·夏夜的萤火",
            ToolName: "manage_plotline"
        );

        Assert.True(EntityChangeSummary.IsToolStepDuplicate(evt));
        Assert.False(EntityChangeSummary.ShouldShowInChat(evt));
    }

    [Fact]
    public void ManualChange_HasNoToolName_IsShownInChat()
    {
        var evt = new EntityChangeEvent(
            EntityType.Character,
            "char_1",
            AuditAction.Update,
            "novel_1",
            "林月如",
            Source: AuditSource.User
        );

        Assert.False(EntityChangeSummary.IsToolStepDuplicate(evt));
        Assert.True(EntityChangeSummary.ShouldShowInChat(evt));
    }

    [Fact]
    public void AiScannerChange_HasNoToolName_IsShownInChat()
    {
        // NoteCaptureScanner 这类 AI 写盘没有工具步骤，仍需对话状态行。
        var evt = new EntityChangeEvent(
            EntityType.Note,
            "note_1",
            AuditAction.Create,
            "novel_1",
            "候选笔记"
        );

        Assert.True(EntityChangeSummary.ShouldShowInChat(evt));
    }
}
