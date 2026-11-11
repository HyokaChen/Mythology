using Core.Data;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 章节预览范围（书 / 卷 / 未分卷）：只决定上一章 / 下一章的可达集合，
/// 不重排全书 Order；卷内章节保持全局顺序。
/// </summary>
public sealed class ChapterPreviewScopeTests
{
    private static Chapter Ch(string id, int order, string? volumeId = null) =>
        new()
        {
            Id = id,
            NovelId = "n1",
            Title = id,
            Order = order,
            VolumeId = volumeId,
        };

    private static readonly List<Chapter> Chapters =
    [
        Ch("ch1", 1, "v1"),
        Ch("ch2", 2, "v1"),
        Ch("ch3", 3, "v2"),
        Ch("ch4", 4, null),
        Ch("ch5", 5, "deleted-volume"),
    ];

    private static readonly HashSet<string> KnownVolumes = new(StringComparer.Ordinal)
    {
        "v1",
        "v2",
    };

    [Fact]
    public void NovelScope_KeepsEveryChapterInGlobalOrder()
    {
        var selected = ChapterReaderWindow.SelectChapters(
            Chapters,
            ChapterPreviewScope.Novel,
            volumeId: null,
            KnownVolumes
        );

        Assert.Equal(["ch1", "ch2", "ch3", "ch4", "ch5"], selected.Select(c => c.Id));
    }

    [Fact]
    public void VolumeScope_OnlyReachesChaptersOfThatVolume()
    {
        var selected = ChapterReaderWindow.SelectChapters(
            Chapters,
            ChapterPreviewScope.Volume,
            "v1",
            KnownVolumes
        );

        Assert.Equal(["ch1", "ch2"], selected.Select(c => c.Id));
    }

    [Fact]
    public void UnassignedScope_IncludesEmptyAndOrphanVolumeIds_ButNotKnownVolumes()
    {
        var selected = ChapterReaderWindow.SelectChapters(
            Chapters,
            ChapterPreviewScope.Unassigned,
            volumeId: null,
            KnownVolumes
        );

        // 空 VolumeId + 指向已删除卷的章节才算未分卷；v1 / v2 不进入。
        Assert.Equal(["ch4", "ch5"], selected.Select(c => c.Id));
    }

    [Fact]
    public void VolumeScope_UnknownVolume_YieldsEmptyPageSet()
    {
        var selected = ChapterReaderWindow.SelectChapters(
            Chapters,
            ChapterPreviewScope.Volume,
            "missing",
            KnownVolumes
        );

        Assert.Empty(selected);
    }

    [Fact]
    public void VolumeScope_MissingVolumeId_DoesNotFallBackToUnassignedChapters()
    {
        var selected = ChapterReaderWindow.SelectChapters(
            Chapters,
            ChapterPreviewScope.Volume,
            volumeId: null,
            KnownVolumes
        );

        Assert.Empty(selected);
    }

    [Theory]
    [InlineData(Core.Novels.OutlineRowKind.Novel, true)]
    [InlineData(Core.Novels.OutlineRowKind.VolumeHeader, true)]
    [InlineData(Core.Novels.OutlineRowKind.UnassignedHeader, true)]
    [InlineData(Core.Novels.OutlineRowKind.Chapter, true)]
    [InlineData(Core.Novels.OutlineRowKind.More, false)]
    [InlineData(Core.Novels.OutlineRowKind.Empty, false)]
    public void CanOpenContent_OnlyForBookVolumeAndChapterRows(
        Core.Novels.OutlineRowKind kind,
        bool expected
    )
    {
        var row = new Core.Novels.OutlineRow { Kind = kind };

        Assert.Equal(expected, row.CanOpenContent);
    }
}
