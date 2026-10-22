using Core.Data;
using Core.Evolution;
using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 章节生命周期 UI 的纯逻辑：发布预览行、撤回阻断文案、实体详情时间线单行。
/// 对话框本体是终端控件（无法在无 TUI host 的测试里弹），可测的是它们的数据投影。
/// </summary>
public sealed class ChapterLifecycleUiTests
{
    private static PublishPreview Preview(
        IReadOnlyList<EntityStateChange>? changes = null,
        IReadOnlyList<CascadeSuggestion>? suggestions = null
    ) =>
        new(
            NovelId: "n1",
            ChapterId: "c1",
            ChapterOrder: 3,
            ChapterTitle: "雨夜寄北",
            PendingChanges: changes ?? [],
            DetectedDeaths: [],
            Suggestions: suggestions ?? [],
            ValidationIssues: []
        );

    [Fact]
    public void PublishPreviewLines_ShowTargetAspectSummaryLocationAndPlotline()
    {
        var change = new EntityStateChange
        {
            Id = "s1",
            NovelId = "n1",
            ChapterId = "c1",
            TargetEntityType = EntityType.Character,
            TargetId = "ch1",
            Aspect = EvolutionAspect.Status,
            Summary = "受伤 → 痊愈",
            LocationId = "loc1",
            PlotlineIds = ["p1"],
        };
        var preview = Preview([change]);

        var lines = ChapterPublishDialog.DescribePendingChanges(
            preview,
            (_, _) => "林晚",
            id => id == "loc1" ? "破庙" : id,
            id => id == "p1" ? "主线" : id
        );

        var line = Assert.Single(lines);
        Assert.Contains("林晚", line);
        Assert.Contains("状态", line);
        Assert.Contains("受伤 → 痊愈", line);
        Assert.Contains("📍破庙", line);
        Assert.Contains("情节线 主线", line);
    }

    [Fact]
    public void PublishPreviewLines_FallBackToFromTo_WhenSummaryMissing()
    {
        var change = new EntityStateChange
        {
            Id = "s1",
            NovelId = "n1",
            ChapterId = "c1",
            TargetEntityType = EntityType.Character,
            TargetId = "ch1",
            Aspect = EvolutionAspect.Personality,
            FromSummary = "谨慎",
            ToSummary = "多疑",
        };

        var line = Assert.Single(ChapterPublishDialog.DescribePendingChanges(Preview([change])));
        Assert.Contains("性格", line);
        Assert.Contains("谨慎 → 多疑", line);
        // 无地点/情节线时不产生空占位
        Assert.DoesNotContain("📍", line);
        Assert.DoesNotContain("情节线", line);
    }

    [Fact]
    public void UnpublishBlockers_ReportLaterChaptersAndConflicts()
    {
        var blocked = new UnpublishPreview(
            HasAppliedChangeSets: true,
            LaterPublishedChapters: ["第4章", "第5章"],
            Conflicts: [new ChangeConflict(EntityType.Character, "ch1", "发布后被修改")]
        );

        var blockers = ChapterUnpublishDialog.DescribeBlockers(blocked);
        Assert.Contains(blockers, b => b.Contains("后续章节已发布", StringComparison.Ordinal));
        Assert.Contains(blockers, b => b.Contains("第4章", StringComparison.Ordinal));
        Assert.Contains(
            blockers,
            b => b.Contains("角色 ch1", StringComparison.Ordinal) && b.Contains("发布后被修改")
        );
    }

    [Fact]
    public void UnpublishBlockers_EmptyForCleanPreview()
    {
        var clean = new UnpublishPreview(true, [], []);
        Assert.Empty(ChapterUnpublishDialog.DescribeBlockers(clean));
    }

    [Fact]
    public void EntityLogLine_MarksPendingCascadeSecret_WithChapterSummaryPlace()
    {
        var entry = new EntityLogEntry(
            EntityType: EntityType.Character,
            EntityId: "ch1",
            EntityName: "林晚",
            ChapterOrder: 3,
            ChapterId: "c1",
            ChapterTitle: "雨夜寄北",
            LocationId: "loc1",
            LocationName: "破庙",
            SceneId: null,
            SceneTitle: null,
            PlotlineNames: ["复仇线"],
            TimeDescription: "黄昏",
            Cause: null,
            Effects: [],
            Visibility: ChangeVisibility.Secret,
            WitnessIds: [],
            Sequence: 2,
            Timestamp: DateTime.UtcNow,
            Element: OntologyElement.State,
            Operation: OntologyOperation.Update,
            Kind: "state",
            Field: "state-change",
            From: "旧",
            To: "新",
            Summary: "受伤 → 痊愈",
            Source: AuditSource.Ai,
            SourceSessionId: null,
            Lifecycle: ChangeLifecycle.Pending,
            IsCascade: true,
            ChangeSetId: null
        );

        var line = SetUpPanel.FormatEntityLogLine(entry);

        Assert.Contains("✏", line);
        Assert.Contains("⏳", line); // 待发布
        Assert.Contains("🔗", line); // 联动
        Assert.Contains("🔒", line); // 秘密
        Assert.Contains("第3章", line);
        Assert.Contains("受伤 → 痊愈", line);
        Assert.Contains("📍破庙", line);
        Assert.Contains("复仇线", line);
    }

    [Fact]
    public void EntityLogLine_UsesOperationIcons_AndSettingLabel()
    {
        static EntityLogEntry Make(OntologyOperation operation) =>
            new(
                EntityType.Character,
                "ch1",
                "林晚",
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                null,
                null,
                [],
                ChangeVisibility.Public,
                [],
                0,
                DateTime.UtcNow,
                OntologyElement.Object,
                operation,
                "Element",
                null,
                null,
                null,
                "设定变更",
                AuditSource.User,
                null,
                ChangeLifecycle.Committed,
                false,
                null
            );

        Assert.Contains("➕", SetUpPanel.FormatEntityLogLine(Make(OntologyOperation.Create)));
        Assert.Contains("🗑", SetUpPanel.FormatEntityLogLine(Make(OntologyOperation.Delete)));
        Assert.Contains("↩", SetUpPanel.FormatEntityLogLine(Make(OntologyOperation.Revert)));
        // 无章节归属 → 「设定」而不是「第0章」
        Assert.Contains("设定", SetUpPanel.FormatEntityLogLine(Make(OntologyOperation.Update)));
    }
}
