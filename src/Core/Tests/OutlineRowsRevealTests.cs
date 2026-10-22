using Core.Data;
using Core.Novels;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 大纲行模型的新增行为契约（reveal 定位 / 新建高亮 / 分节折叠 / 分组内空态 / Depth）。
///
/// 背景：这些成员（<c>RevealVolumeId / RevealChapterId / ExpandedSections / IsNewlyCreated /
/// IsRevealOnly / ContextLine / Depth</c>）随大纲 UI 一并引入，此前无测试覆盖——
/// 本文件把用户可见语义钉死，防后续重构悄悄改坏。
/// </summary>
public sealed class OutlineRowsRevealTests
{
    private const string NovelId = "n";
    private static readonly IReadOnlyDictionary<string, string> NoNames =
        new Dictionary<string, string>();

    private static NovelStructure Structure()
    {
        var volumes = new List<Volume>
        {
            new()
            {
                Id = "vol1",
                NovelId = NovelId,
                Name = "风起",
                Order = 1,
            },
            new()
            {
                Id = "vol2",
                NovelId = NovelId,
                Name = "云涌",
                Order = 2,
            },
            new()
            {
                Id = "vol3",
                NovelId = NovelId,
                Name = "无声",
                Order = 3, // 空卷：用于「本卷还没有章节」空态
            },
        };
        var chapters = new List<Chapter>
        {
            Chapter("ch1", 1, "vol1"),
            Chapter("ch2", 2, "vol1"),
            Chapter("ch3", 3, "vol1"),
            Chapter("ch4", 4, "vol1"),
            Chapter("ch5", 5, "vol2"),
        };
        return new NovelStructure(
            new Novel
            {
                Id = NovelId,
                Title = "结构测试",
                Outline = "总纲首行\n总纲次行",
            },
            volumes,
            chapters,
            []
        );
    }

    private static Chapter Chapter(string id, int order, string volumeId) =>
        new()
        {
            Id = id,
            NovelId = NovelId,
            Title = $"第{order}章",
            Order = order,
            VolumeId = volumeId,
            Summary = $"细纲{order}",
            Content = new string('文', 100),
        };

    private static OutlineViewState State(
        IEnumerable<string>? expandedVolumes = null,
        IEnumerable<string>? expandedChapters = null,
        string? scope = null,
        string? keyword = null,
        ChapterStatus? status = null,
        int pageSize = OutlineRows.DefaultPageSize,
        IReadOnlyDictionary<string, int>? loaded = null,
        string? revealVolumeId = null,
        string? revealChapterId = null
    ) =>
        new(
            ExpandedVolumes: expandedVolumes?.ToHashSet(StringComparer.Ordinal),
            ExpandedChapters: expandedChapters?.ToHashSet(StringComparer.Ordinal),
            Scope: scope,
            Keyword: keyword,
            Status: status,
            PageSize: pageSize,
            LoadedRows: loaded,
            RevealVolumeId: revealVolumeId,
            RevealChapterId: revealChapterId
        );

    private static IReadOnlyList<OutlineRow> Build(OutlineViewState state) =>
        OutlineRows
            .Build(Structure(), state, NoNames, NoNames, showOntology: true, showCheckResult: true)
            .Rows;

    // ── 书级内容：本体/检查不再是树里的行（由 BookSummary + 两端分节承载）；根节点 = 小说行 ──

    [Fact]
    public void Build_EmitsNovelRootRow_ButNoBookContentRows()
    {
        var rows = Build(State());

        // 小说行是层级根（端上渲染标题 + 详情入口）；本体/检查仍不出行
        Assert.Equal(OutlineRowKind.Novel, rows[0].Kind);
        Assert.DoesNotContain(rows, r => r.Kind is OutlineRowKind.Ontology or OutlineRowKind.Check);
    }

    [Fact]
    public void Summary_CarriesTitleOutlineCountsAndProgress()
    {
        var result = OutlineRows.Build(
            Structure(),
            State(),
            NoNames,
            NoNames,
            showOntology: true,
            showCheckResult: true
        );

        Assert.Equal("结构测试", result.Summary.Title);
        Assert.Equal("总纲首行", result.Summary.OutlineLine);

        var empty = OutlineRows.Build(
            new NovelStructure(new Novel { Id = NovelId, Title = "空书" }, [], [], []),
            State(),
            NoNames,
            NoNames,
            showOntology: false,
            showCheckResult: false
        );
        Assert.Equal("（未填写总纲）", empty.Summary.OutlineLine);
        Assert.Equal(0, empty.Summary.ChapterCount);
        Assert.Equal(0, empty.Summary.DoneCount);
    }

    // ── reveal：新建卷/章节的定位高亮 ──

    [Fact]
    public void RevealVolume_OutOfScope_AppendsRevealOnlyHeader()
    {
        var rows = Build(State(scope: "vol1", revealVolumeId: "vol2"));

        var revealHeader = rows.Single(r =>
            r.Kind == OutlineRowKind.VolumeHeader && r.Id == "vol2"
        );
        Assert.True(revealHeader.IsNewlyCreated);
        Assert.True(revealHeader.IsRevealOnly);
        Assert.Equal("刚创建 · 当前章节范围外", revealHeader.ContextLine);
        Assert.False(revealHeader.IsExpanded);
        Assert.Equal("第2卷 · 云涌", revealHeader.Title);
    }

    [Fact]
    public void RevealVolume_InScope_IsMarkedOnceWithoutRevealCopy()
    {
        var rows = Build(State(revealVolumeId: "vol2"));

        var headers = rows.Where(r => r.Kind == OutlineRowKind.VolumeHeader && r.Id == "vol2")
            .ToList();
        var header = Assert.Single(headers);
        Assert.True(header.IsNewlyCreated);
        Assert.False(header.IsRevealOnly);
        Assert.Equal("刚创建", header.ContextLine);
    }

    [Fact]
    public void RevealChapter_BeyondLoadedPage_MarksPageOut()
    {
        var rows = Build(State(expandedVolumes: ["vol1"], pageSize: 2, revealChapterId: "ch4"));

        var reveal = rows.Single(r => r.Kind == OutlineRowKind.Chapter && r.Id == "ch4");
        Assert.True(reveal.IsRevealOnly);
        Assert.True(reveal.IsNewlyCreated);
        Assert.Equal("刚创建 · 当前页外", reveal.ContextLine);
        Assert.Equal(1, reveal.Depth); // 页外 reveal 仍挂在分组层级内（Depth=1）

        // 正常分页不受影响：2 条页内章行 + 1 条 More
        Assert.Equal(2, rows.Count(r => r.Kind == OutlineRowKind.Chapter && !r.IsRevealOnly));
        Assert.Equal(2, rows.Single(r => r.Kind == OutlineRowKind.More).HiddenCount);
    }

    [Fact]
    public void RevealChapter_FilteredOut_MarksFilterOut()
    {
        var rows = Build(
            State(expandedVolumes: ["vol1"], keyword: "第1章", revealChapterId: "ch2")
        );

        var reveal = rows.Single(r => r.Kind == OutlineRowKind.Chapter && r.Id == "ch2");
        Assert.True(reveal.IsRevealOnly);
        Assert.Equal("刚创建 · 当前筛选外", reveal.ContextLine);
    }

    [Fact]
    public void RevealChapter_GroupCollapsed_ExplainsCollapsedGroup()
    {
        var rows = Build(State(revealChapterId: "ch2"));

        var reveal = rows.Single(r => r.Kind == OutlineRowKind.Chapter && r.Id == "ch2");
        Assert.True(reveal.IsRevealOnly);
        Assert.Contains("分组已折叠", reveal.ContextLine);
        Assert.Contains("第1卷 · 风起", reveal.ContextLine);
    }

    [Fact]
    public void RevealChapter_GroupOutOfScope_ExplainsScope()
    {
        var rows = Build(State(scope: "vol2", revealChapterId: "ch1"));

        var reveal = rows.Single(r => r.Kind == OutlineRowKind.Chapter && r.Id == "ch1");
        Assert.True(reveal.IsRevealOnly);
        Assert.Equal("第1卷 · 风起 · 当前章节范围外", reveal.ContextLine);
        // 章行恒为章层级（Depth=1）：Depth 现在驱动两端缩进，0 会让章行看起来像卷头；
        // 「脱离分组上下文」这点由 IsRevealOnly + ContextLine 表达，不靠 Depth 表达。
        Assert.Equal(1, reveal.Depth);
    }

    [Fact]
    public void RevealChapter_InPage_IsMarkedNewlyCreatedWithoutExtraRow()
    {
        var rows = Build(State(expandedVolumes: ["vol1"], revealChapterId: "ch2"));

        var row = Assert.Single(rows.Where(r => r.Kind == OutlineRowKind.Chapter && r.Id == "ch2"));
        Assert.True(row.IsNewlyCreated);
        Assert.False(row.IsRevealOnly);
        Assert.Equal("刚创建", row.ContextLine);
    }

    [Fact]
    public void Signature_ChangesWithRevealState()
    {
        var withoutReveal = OutlineRows.Signature(Build(State()));
        var withReveal = OutlineRows.Signature(Build(State(revealChapterId: "ch2")));

        Assert.NotEqual(withoutReveal, withReveal);
    }

    // ── 分组内空态与 Depth ──

    [Fact]
    public void ExpandedGroupWithoutMatches_EmitsPerGroupEmpty_AndSuppressesGlobalEmpty()
    {
        var rows = Build(State(expandedVolumes: ["vol1"], keyword: "不存在的标题"));

        var groupEmpty = rows.Single(r => r.Kind == OutlineRowKind.Empty);
        Assert.Equal("empty:vol1", groupEmpty.Key);
        Assert.Equal("vol1", groupEmpty.GroupKey);
        Assert.Equal("当前筛选没有命中章节", groupEmpty.Title);
        Assert.Equal(1, groupEmpty.Depth);

        // 已有分组内空态 → 不再叠加全局空态行
        Assert.DoesNotContain(rows, r => r.Key == "empty");
    }

    [Fact]
    public void ExpandedEmptyVolume_SaysNoChaptersYet()
    {
        var rows = Build(State(expandedVolumes: ["vol3"]));

        var groupEmpty = rows.Single(r => r.Kind == OutlineRowKind.Empty);
        Assert.Equal("empty:vol3", groupEmpty.Key);
        Assert.Equal("本卷还没有章节", groupEmpty.Title);
        Assert.Equal("vol3", groupEmpty.GroupKey);
    }

    [Fact]
    public void Depth_HeadersZero_GroupChildrenOne()
    {
        var rows = Build(State(expandedVolumes: ["vol1"], pageSize: 2));

        Assert.All(
            rows.Where(r =>
                r.Kind is OutlineRowKind.VolumeHeader or OutlineRowKind.UnassignedHeader
            ),
            r => Assert.Equal(0, r.Depth)
        );
        Assert.All(
            rows.Where(r => r.Kind is OutlineRowKind.Chapter or OutlineRowKind.More),
            r => Assert.Equal(1, r.Depth)
        );
    }
}
