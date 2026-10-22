using Core.Data;
using Core.Evolution;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 发布预览视图模型（纯 VM，不触 Avalonia）：默认勾选口径 + 回传 <see cref="PublishOptions"/>。
/// 默认口径 = 检测到的死亡全选；联动建议按 <see cref="CascadeSuggestion.DefaultSelected"/>，
/// 自动项恒勾选且不可取消。
/// </summary>
public sealed class ChapterPublishViewModelTests
{
    private static PublishPreview BuildPreview() =>
        new(
            NovelId: "n1",
            ChapterId: "c1",
            ChapterOrder: 3,
            ChapterTitle: "风起",
            PendingChanges:
            [
                new EntityStateChange
                {
                    Id = "chg1",
                    TargetEntityType = EntityType.Character,
                    TargetId = "char1",
                    ChapterId = "c1",
                    Sequence = 1,
                    Summary = "对师兄态度转冷",
                    Lifecycle = ChangeLifecycle.Pending,
                },
            ],
            DetectedDeaths:
            [
                new DetectedDeathCandidate("d1", "char2", "林砚", "「他倒在血泊里」", "林砚死亡"),
                new DetectedDeathCandidate("d2", "char3", "苏禾", "「气息全无」", "苏禾死亡"),
            ],
            Suggestions:
            [
                new CascadeSuggestion(
                    "s-auto",
                    EntityType.Character,
                    "char2",
                    "林砚",
                    "status",
                    StateEffectOperation.Set,
                    "已死亡",
                    "林砚状态改为已死亡",
                    DefaultSelected: false,
                    IsAutomatic: true,
                    SourceChangeId: "chg1"
                ),
                new CascadeSuggestion(
                    "s-default",
                    EntityType.Relationship,
                    "rel1",
                    "林砚-苏禾",
                    "description",
                    StateEffectOperation.Set,
                    "生死相隔",
                    "关系改为生死相隔",
                    DefaultSelected: true,
                    IsAutomatic: false,
                    SourceChangeId: "chg1"
                ),
                new CascadeSuggestion(
                    "s-optional",
                    EntityType.Location,
                    "loc1",
                    "乱葬岗",
                    "description",
                    StateEffectOperation.Set,
                    "新坟",
                    "新增纪念地",
                    DefaultSelected: false,
                    IsAutomatic: false,
                    SourceChangeId: null,
                    IsCreate: true
                ),
            ],
            ValidationIssues: [new ValidationIssue("warn", "第 3 章尚无正文", false)]
        );

    [Fact]
    public void DefaultSelection_SelectsAllDeaths_DefaultAndAutomaticSuggestions()
    {
        var vm = new ChapterPublishViewModel(BuildPreview());

        Assert.Equal("发布「风起」", vm.Title);
        Assert.Contains("第 3 章", vm.Subtitle);
        Assert.Single(vm.PendingChangeLines);
        Assert.Contains("对师兄态度转冷", vm.PendingChangeLines[0]);
        Assert.True(vm.CanPublish); // 只有警告，没有阻断
        Assert.True(vm.HasDetectedDeaths);
        Assert.True(vm.HasSuggestions);

        Assert.All(vm.DetectedDeaths, item => Assert.True(item.IsSelected));
        Assert.All(vm.DetectedDeaths, item => Assert.True(item.CanToggle));

        var auto = vm.Suggestions.Single(s => s.Id == "s-auto");
        var byDefault = vm.Suggestions.Single(s => s.Id == "s-default");
        var optional = vm.Suggestions.Single(s => s.Id == "s-optional");
        Assert.True(auto.IsSelected);
        Assert.False(auto.CanToggle); // 自动项不可取消
        Assert.True(byDefault.IsSelected);
        Assert.True(byDefault.CanToggle);
        Assert.False(optional.IsSelected); // 默认不选
        Assert.True(optional.CanToggle);
    }

    [Fact]
    public void BuildOptions_ReturnsOnlyCheckedItems()
    {
        var vm = new ChapterPublishViewModel(BuildPreview());

        var defaults = vm.BuildOptions();
        Assert.Equal(["d1", "d2"], defaults.SelectedDetectedDeathIds);
        Assert.Equal(["s-auto", "s-default"], defaults.SelectedSuggestionIds);

        // 取消一个检测死亡 + 勾选可选联动 → 只回传改后的选择
        vm.DetectedDeaths.Single(d => d.Id == "d2").IsSelected = false;
        vm.Suggestions.Single(s => s.Id == "s-optional").IsSelected = true;

        var changed = vm.BuildOptions();
        Assert.Equal(["d1"], changed.SelectedDetectedDeathIds);
        Assert.Equal(["s-auto", "s-default", "s-optional"], changed.SelectedSuggestionIds);
    }

    [Fact]
    public void BlockingIssues_DisablePublish_AndAreMarkedInLines()
    {
        var vm = new ChapterPublishViewModel(
            BuildPreview() with
            {
                ValidationIssues =
                [
                    new ValidationIssue("sequence-duplicate", "章内顺序重复：1", true),
                ],
            }
        );

        Assert.False(vm.CanPublish);
        Assert.Contains(vm.Issues, line => line.StartsWith('❌'));
    }

    [Fact]
    public void NameResolver_ReplacesRawEntityIds()
    {
        var vm = new ChapterPublishViewModel(
            BuildPreview(),
            (type, id) => type == EntityType.Character && id == "char1" ? "沈砚" : id
        );

        Assert.Contains("沈砚 · 对师兄态度转冷", vm.PendingChangeLines[0]);
    }
}
