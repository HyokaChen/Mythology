using Core.Data;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 章节/分卷编辑器的「从下级同步」汇总口径：
/// 场景是实际推进/出场的真源；章节/分卷的 PlotlineIds/CharacterIds 是计划字段。
/// 同步只做并集，不删除手工勾选，也不在打开窗口时自动落盘。
/// </summary>
public sealed class EntityEditorMultiSelectTests
{
    [Fact]
    public void ScenePlotlineIds_DedupeAndSkipBlank()
    {
        var scenes = new[]
        {
            new SceneDefinition { PlotlineIds = ["p1", "p2", ""] },
            new SceneDefinition { PlotlineIds = ["p2", "   "] },
        };

        var ids = EntityEditWindow.CollectScenePlotlineIds(scenes);

        Assert.True(ids.SetEquals(new[] { "p1", "p2" }));
    }

    [Fact]
    public void SceneCharacterIds_DedupeAndSkipBlank()
    {
        var scenes = new[]
        {
            new SceneDefinition { ParticipantIds = ["c1", "c2", ""] },
            new SceneDefinition { ParticipantIds = ["c2", "   "] },
        };

        var ids = EntityEditWindow.CollectSceneCharacterIds(scenes);

        Assert.True(ids.SetEquals(new[] { "c1", "c2" }));
    }

    [Fact]
    public void VolumePlotlineIds_UnionChapterPlanAndSceneFact()
    {
        var chapters = new[]
        {
            new Chapter { PlotlineIds = ["p1", "p2"] },
            new Chapter { PlotlineIds = ["p2", ""] },
        };
        var scenes = new[]
        {
            new SceneDefinition { PlotlineIds = ["p2", "p3"] },
            new SceneDefinition { PlotlineIds = ["p1", "   "] },
        };

        var ids = EntityEditWindow.CollectVolumePlotlineIds(chapters, scenes);

        Assert.True(ids.SetEquals(new[] { "p1", "p2", "p3" }));
    }

    [Fact]
    public void VolumeCharacterIds_UnionChapterPlanAndSceneParticipants()
    {
        var chapters = new[]
        {
            new Chapter { CharacterIds = ["c1", "c2"] },
            new Chapter { CharacterIds = ["c2", ""] },
        };
        var scenes = new[] { new SceneDefinition { ParticipantIds = ["c2", "c3"] } };

        var ids = EntityEditWindow.CollectVolumeCharacterIds(chapters, scenes);

        Assert.True(ids.SetEquals(new[] { "c1", "c2", "c3" }));
    }

    [Fact]
    public void EmptyInputs_ProduceEmptySets()
    {
        Assert.Empty(EntityEditWindow.CollectScenePlotlineIds([]));
        Assert.Empty(EntityEditWindow.CollectSceneCharacterIds([]));
        Assert.Empty(EntityEditWindow.CollectVolumePlotlineIds([], []));
        Assert.Empty(EntityEditWindow.CollectVolumeCharacterIds([], []));
    }

    [Fact]
    public void Summary_NoCandidates()
    {
        Assert.Equal(
            "无候选",
            EntityEditWindow.BuildMultiSelectSummary(0, 0, 0, 0, 0, isVolume: false)
        );
    }

    [Fact]
    public void Summary_UnselectedCandidates_UsesCandidateWording()
    {
        Assert.Equal(
            "未选择（下列为全部 6 条候选）",
            EntityEditWindow.BuildMultiSelectSummary(6, 0, 0, 0, 0, isVolume: false)
        );
    }

    [Fact]
    public void Summary_SelectedCandidates_UsesCandidateWording()
    {
        Assert.Equal(
            "已选 2 / 6 条候选",
            EntityEditWindow.BuildMultiSelectSummary(6, 2, 0, 0, 0, isVolume: false)
        );
    }

    [Fact]
    public void Summary_ChapterDerivedPending()
    {
        Assert.Equal(
            "已选 2 / 6 条候选 · 场景已标注 3 条，2 条尚未勾选",
            EntityEditWindow.BuildMultiSelectSummary(6, 2, 3, 3, 2, isVolume: false)
        );
    }

    [Fact]
    public void Summary_VolumeDerivedAllChecked()
    {
        Assert.Equal(
            "已选 5 / 6 条候选 · 章节/场景已标注 3 条，已全部勾选",
            EntityEditWindow.BuildMultiSelectSummary(6, 5, 3, 3, 0, isVolume: true)
        );
    }

    [Fact]
    public void Summary_AllDerivedReferencesStale_DoesNotDoubleCount()
    {
        Assert.Equal(
            "未选择（下列为全部 6 条候选） · 场景标注的 2 条引用已失效",
            EntityEditWindow.BuildMultiSelectSummary(6, 0, 2, 0, 0, isVolume: false)
        );
    }

    [Fact]
    public void Summary_PartiallyStaleDerivedReferences()
    {
        Assert.Equal(
            "已选 5 / 6 条候选 · 章节/场景已标注 3 条，已全部勾选 · 另有 1 条引用已失效",
            EntityEditWindow.BuildMultiSelectSummary(6, 5, 4, 3, 0, isVolume: true)
        );
    }
}
