using Core.Data;
using Core.Novels;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 大纲树行模型（<see cref="OutlineRows"/>）：两端共用的分组 / 分页 / 筛选唯一实现。
/// 纯函数，无 UI 依赖。
/// </summary>
public sealed class OutlineRowsTests
{
    private static readonly IReadOnlyDictionary<string, string> PlotlineNames = new Dictionary<
        string,
        string
    >
    {
        ["pl-1"] = "复仇线",
        ["pl-2"] = "感情线",
    };

    private static readonly IReadOnlyDictionary<string, string> CharacterNames = new Dictionary<
        string,
        string
    >
    {
        ["cr-1"] = "林月如",
    };

    private static NovelStructure BuildStructure(int volumeCount = 3, int chaptersPerVolume = 4)
    {
        var volumes = new List<Volume>();
        var chapters = new List<Chapter>();
        var order = 0;
        for (var v = 1; v <= volumeCount; v++)
        {
            var volumeId = $"vol-{v}";
            volumes.Add(
                new Volume
                {
                    Id = volumeId,
                    NovelId = "n",
                    Name = v == 1 ? "" : $"卷{v}",
                    Order = v,
                    Summary = $"第{v}卷梗概",
                    Goal = $"第{v}卷目标",
                    PlotlineIds = ["pl-1"],
                    CharacterIds = ["cr-1"],
                    Notes = "备注",
                }
            );
            for (var c = 1; c <= chaptersPerVolume; c++)
            {
                order++;
                chapters.Add(
                    new Chapter
                    {
                        Id = $"ch-{order}",
                        NovelId = "n",
                        Title = $"第{order}章",
                        Order = order,
                        VolumeId = volumeId,
                        Summary = $"细纲{order}",
                        Status = order % 2 == 0 ? ChapterStatus.Published : ChapterStatus.Draft,
                        CharCount = 1000 + order,
                    }
                );
            }
        }

        return new NovelStructure(
            new Novel
            {
                Id = "n",
                Title = "测试小说",
                Outline = "总纲首行\n总纲次行",
            },
            volumes,
            chapters,
            []
        );
    }

    private static OutlineViewState State(
        IEnumerable<string>? expandedVolumes = null,
        string? scope = null,
        string? keyword = null,
        ChapterStatus? status = null,
        int pageSize = OutlineRows.DefaultPageSize,
        IReadOnlyDictionary<string, int>? loaded = null,
        IEnumerable<string>? expandedChapters = null,
        bool novelExpanded = true
    ) =>
        new(
            (expandedVolumes ?? []).ToHashSet(StringComparer.Ordinal),
            (expandedChapters ?? []).ToHashSet(StringComparer.Ordinal),
            scope,
            keyword,
            status,
            pageSize,
            loaded,
            NovelExpanded: novelExpanded
        );

    private static IReadOnlyList<OutlineRow> Build(
        NovelStructure structure,
        OutlineViewState state,
        bool showOntology = false,
        bool showCheck = false
    ) =>
        OutlineRows
            .Build(structure, state, PlotlineNames, CharacterNames, showOntology, showCheck)
            .Rows;

    /// <summary>
    /// 卷内序号（用户报障）：章节号在同一卷下自增，换卷从 1 重新计数。
    /// 与全书全局 Order 区分——落库/排序仍用 Order，展示用 GroupOrder。
    /// </summary>
    [Fact]
    public void Build_ChapterOrderText_RestartsPerVolume()
    {
        var structure = BuildStructure(volumeCount: 2, chaptersPerVolume: 3);

        var rows = Build(structure, State(expandedVolumes: ["vol-1", "vol-2"], pageSize: 10));
        var chapters = rows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();

        Assert.Equal(6, chapters.Count);
        // 卷 1：全局 Order 1..3 → 卷内 第 1/2/3 章
        Assert.Equal(
            ["第 1 章", "第 2 章", "第 3 章"],
            chapters.Take(3).Select(c => c.OrderText).ToArray()
        );
        Assert.Equal([1, 2, 3], chapters.Take(3).Select(c => c.GroupOrder).ToArray());
        // 卷 2：全局 Order 4..6 → 卷内重新从 第 1 章 开始
        Assert.Equal(
            ["第 1 章", "第 2 章", "第 3 章"],
            chapters.Skip(3).Select(c => c.OrderText).ToArray()
        );
        Assert.Equal([1, 2, 3], chapters.Skip(3).Select(c => c.GroupOrder).ToArray());
        // 全局 Order 保持原样（不被卷内号覆盖）
        Assert.Equal([1, 2, 3, 4, 5, 6], chapters.Select(c => c.Order).ToArray());
    }

    /// <summary>翻页不打断卷内序号：第 2 页的章节延续本卷编号，不从 1 重数。</summary>
    [Fact]
    public void Build_ChapterOrderText_ContinuousAcrossPages()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 6);

        var rows = Build(structure, State(expandedVolumes: ["vol-1"], pageSize: 3));
        var chapters = rows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();

        Assert.Equal(3, chapters.Count);
        Assert.Equal(
            ["第 1 章", "第 2 章", "第 3 章"],
            chapters.Select(c => c.OrderText).ToArray()
        );
    }

    /// <summary>
    /// 章节发布状态（用户报障：章节缺少发布状态）：yaml 里能存能读，
    /// 且大纲行的图标/标签随发布状态变化——发布一个 icon、草稿一个 icon。
    /// </summary>
    [Fact]
    public void Build_ChapterPublishedStatus_RoundTripsAndChangesIcon()
    {
        // 1) 枚举 ↔ yaml 字符串往返（status: published）
        Assert.Equal("published", Core.Data.YamlHelper.EnumToYaml(ChapterStatus.Published));
        Assert.Equal(
            ChapterStatus.Published,
            Core.Data.YamlHelper.YamlToEnum(
                new Dictionary<string, object?> { ["status"] = "published" },
                "status",
                ChapterStatus.Draft
            )
        );
        // 旧数据没有 status 字段 → 仍是草稿（不破坏兼容）
        Assert.Equal(
            ChapterStatus.Draft,
            Core.Data.YamlHelper.YamlToEnum(
                new Dictionary<string, object?>(),
                "status",
                ChapterStatus.Draft
            )
        );

        // 2) 大纲行：草稿 📝 / 发布 📢，标签也跟着变
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 2);
        structure.Chapters[0].Status = ChapterStatus.Published;
        structure.Chapters[1].Status = ChapterStatus.Draft;
        var rows = Build(structure, State(expandedVolumes: ["vol-1"], pageSize: 10));
        var chapters = rows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();

        Assert.Equal("📢", chapters[0].StatusIcon);
        Assert.Equal("发布", chapters[0].MetaLine);
        Assert.Equal("📝", chapters[1].StatusIcon);
        Assert.Equal("草稿", chapters[1].MetaLine);
    }

    [Fact]
    public void Build_DefaultCollapsed_EmitsVolumeHeadersOnly()
    {
        var rows = Build(BuildStructure(), State());

        // 根节点 = 当前小说行，其下是 3 个卷头（书级三行已移出结构树，改由 BookSummary 承载）
        Assert.Equal(4, rows.Count);
        Assert.Equal(OutlineRowKind.Novel, rows[0].Kind);
        Assert.Equal("测试小说", rows[0].Title);
        Assert.True(rows[0].IsExpanded);
        Assert.All(rows.Skip(1), r => Assert.Equal(OutlineRowKind.VolumeHeader, r.Kind));
        Assert.Equal("第1卷", rows[1].Title); // Name 为空时只出序号
        Assert.Equal("第2卷 · 卷2", rows[2].Title);
        Assert.Equal("4 章 · 4,010 字", rows[1].MetaLine);
        Assert.False(rows[1].IsExpanded);
    }

    [Fact]
    public void Build_NovelCollapsed_EmitsOnlyNovelRow()
    {
        var rows = Build(BuildStructure(), State(novelExpanded: false));

        // 小说行折叠 = 不物化任何卷/章行（首屏只给「当前小说」）
        var row = Assert.Single(rows);
        Assert.Equal(OutlineRowKind.Novel, row.Kind);
        Assert.False(row.IsExpanded);
    }

    [Fact]
    public void Build_KeywordFilter_MatchesBySubsequence()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 3);

        // 章名「第N章」：查询「1章」是子序列（不连续）→ 只命中第 1 章
        var rows = Build(structure, State(expandedVolumes: ["vol-1"], keyword: "1章"));
        var chapters = rows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();
        var chapter = Assert.Single(chapters);
        Assert.Equal("1. 第1章", chapter.Title);
    }

    [Fact]
    public void Build_ShowOntologyAndCheck_DoesNotChangeStructureRows()
    {
        var plain = Build(BuildStructure(), State());
        var withSections = Build(BuildStructure(), State(), showOntology: true, showCheck: true);

        // 本体概览 / 设定检查是否有内容，不再影响结构树的行序列
        Assert.Equal(plain.Select(r => r.Key), withSections.Select(r => r.Key));
    }

    [Fact]
    public void Build_ExpandedVolume_EmitsChaptersAndMorePage()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 12);

        var collapsed = Build(structure, State());
        Assert.Equal(2, collapsed.Count); // 小说根节点 + 卷头
        Assert.Equal(OutlineRowKind.Novel, collapsed[0].Kind);
        Assert.Equal(OutlineRowKind.VolumeHeader, collapsed[1].Kind);

        var expanded = Build(structure, State(expandedVolumes: ["vol-1"], pageSize: 5));
        var chapters = expanded.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();
        Assert.Equal(5, chapters.Count);
        Assert.Equal("1. 第1章", chapters[0].Title);
        Assert.Equal("📝", chapters[0].StatusIcon); // 草稿图标（原 ⚪ 已改为 📝）
        Assert.Equal("草稿", chapters[0].MetaLine);
        Assert.Equal(1001, chapters[0].CharCount);

        var more = Assert.Single(expanded.Where(r => r.Kind == OutlineRowKind.More));
        Assert.Equal(7, more.HiddenCount);
        Assert.Equal("vol-1", more.GroupKey);

        // 全部加载完 → More 消失
        var loadedAll = Build(
            structure,
            State(
                expandedVolumes: ["vol-1"],
                pageSize: 5,
                loaded: new Dictionary<string, int> { ["vol-1"] = 12 }
            )
        );
        Assert.Equal(12, loadedAll.Count(r => r.Kind == OutlineRowKind.Chapter));
        Assert.DoesNotContain(loadedAll, r => r.Kind == OutlineRowKind.More);
    }

    [Fact]
    public void Build_EmptyLoadedRowsMap_StillRendersFirstPage()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 12);

        // LoadedRows 非空但缺该分组键 → 必须回落 PageSize（不得退化成 0）
        var rows = Build(
            structure,
            State(expandedVolumes: ["vol-1"], pageSize: 5, loaded: new Dictionary<string, int>())
        );

        Assert.Equal(5, rows.Count(r => r.Kind == OutlineRowKind.Chapter));
        Assert.Single(rows.Where(r => r.Kind == OutlineRowKind.More));
    }

    [Fact]
    public void Build_StatusFilter_AppliesToHeadersAndChapterRows()
    {
        var structure = BuildStructure(volumeCount: 2, chaptersPerVolume: 4);

        var rows = Build(
            structure,
            State(expandedVolumes: ["vol-1"], status: ChapterStatus.Published)
        );

        var header = rows.Single(r => r.Key == "v:vol-1");
        Assert.Equal("2 章 · 2,006 字", header.MetaLine); // 过滤后计数（第2、4章 = 1002 + 1004）
        var chapters = rows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();
        Assert.Equal(2, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("📢", c.StatusIcon)); // 发布图标

        // 过滤后 0 章的卷仍保留卷头（结构可见），只是没有章行
        var header2 = rows.Single(r => r.Key == "v:vol-2");
        Assert.Equal("2 章 · 2,014 字", header2.MetaLine);
    }

    [Fact]
    public void Build_TitleKeyword_MatchesCaseInsensitively()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 3);
        structure.Chapters[1].Title = "ABC Chapter";

        var rows = Build(structure, State(expandedVolumes: ["vol-1"], keyword: "abc"));

        var chapter = Assert.Single(rows.Where(r => r.Kind == OutlineRowKind.Chapter));
        Assert.Equal("2. ABC Chapter", chapter.Title);
    }

    [Fact]
    public void Build_ScopeUnassigned_EmitsOnlyUnassignedGroup()
    {
        var structure = BuildStructure(volumeCount: 2, chaptersPerVolume: 2);
        var orphan = new Chapter
        {
            Id = "ch-orphan",
            NovelId = "n",
            Title = "游离章",
            Order = 99,
            VolumeId = null,
            CharCount = 7,
        };
        var dangling = new Chapter
        {
            Id = "ch-dangling",
            NovelId = "n",
            Title = "悬空卷章",
            Order = 98,
            VolumeId = "vol-missing",
            CharCount = 9,
        };
        var withOrphans = structure with
        {
            Chapters = [.. structure.Chapters, orphan, dangling],
            Unassigned = [orphan, dangling],
        };

        var rows = Build(
            withOrphans,
            State(expandedVolumes: [OutlineRows.UnassignedKey], scope: "")
        );

        Assert.Single(rows.Where(r => r.Kind == OutlineRowKind.UnassignedHeader));
        Assert.DoesNotContain(rows, r => r.Kind == OutlineRowKind.VolumeHeader);
        var chapters = rows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();
        Assert.Equal(2, chapters.Count);
        Assert.Equal("98. 悬空卷章", chapters[0].Title);
        Assert.All(chapters, c => Assert.Equal(OutlineRows.UnassignedKey, c.GroupKey));
    }

    [Fact]
    public void Build_ScopeSingleVolume_EmitsOnlyThatVolume()
    {
        var rows = Build(BuildStructure(volumeCount: 3), State(scope: "vol-2"));

        var header = Assert.Single(rows.Where(r => r.Kind == OutlineRowKind.VolumeHeader));
        Assert.Equal("v:vol-2", header.Key);
    }

    [Fact]
    public void Build_CharCountDrivesMetaLine_NotContent()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 1);
        var header = structure.Chapters[0]; // Content 为空、CharCount = 1001

        Assert.Equal("", header.Content);
        var rows = Build(structure, State(expandedVolumes: ["vol-1"]));
        var chapter = Assert.Single(rows.Where(r => r.Kind == OutlineRowKind.Chapter));
        Assert.Equal("草稿", chapter.MetaLine);
        Assert.Equal(1001, chapter.CharCount);
    }

    [Fact]
    public void Build_ChapterDetailLines_CapNameLists_WithEtcSuffix()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 1);
        var characters = Enumerable.Range(1, 30).Select(i => $"cr-{i}").ToList();
        structure.Chapters[0].CharacterIds = characters;
        structure.Chapters[0].PlotlineIds = characters;

        var rows = Build(structure, State(expandedVolumes: ["vol-1"]));
        var chapter = rows.Single(r => r.Kind == OutlineRowKind.Chapter);
        var planned = chapter.DetailLines.Single(l =>
            l.StartsWith("计划出场:", StringComparison.Ordinal)
        );

        Assert.Contains("等 30 个", planned);
        Assert.DoesNotContain("cr-9", planned); // 只列前 8 个
    }

    [Fact]
    public void Build_ChapterDetailLines_CarryOutlineFields()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 1);
        structure.Chapters[0].PlotlineIds = ["pl-1"];
        structure.Chapters[0].CharacterIds = ["cr-1"];
        structure.Chapters[0].Notes = "节奏提示";

        var rows = Build(structure, State(expandedVolumes: ["vol-1"], expandedChapters: ["ch-1"]));

        var chapter = rows.Single(r => r.Kind == OutlineRowKind.Chapter);
        Assert.True(chapter.IsExpanded);
        Assert.Equal(
            ["细纲: 细纲1", "计划推进: 复仇线", "计划出场: 林月如", "备注: 节奏提示"],
            chapter.DetailLines
        );

        var volume = rows.Single(r => r.Kind == OutlineRowKind.VolumeHeader);
        Assert.Equal(
            ["卷目标: 第1卷目标", "推进情节线: 复仇线", "关键角色: 林月如", "备注: 备注"],
            volume.DetailLines
        );
        Assert.Contains("推进 复仇线", volume.Chips);
    }

    [Fact]
    public void Build_EmptyVolumeOutline_SaysNotFilled()
    {
        var structure = BuildStructure(volumeCount: 1, chaptersPerVolume: 1);
        var blank = structure.Volumes[0];
        blank.Summary = "";
        blank.Goal = "";
        blank.PlotlineIds = [];
        blank.CharacterIds = [];
        blank.Notes = "";

        var rows = Build(structure, State(expandedVolumes: ["vol-1"]));

        var volume = rows.Single(r => r.Kind == OutlineRowKind.VolumeHeader);
        Assert.Equal(["（卷纲未填写）"], volume.DetailLines);
    }

    [Fact]
    public void Build_NoChapters_EmitsEmptyRow()
    {
        var structure = new NovelStructure(new Novel { Id = "n", Title = "空书" }, [], [], []);

        var rows = Build(structure, State());

        var empty = Assert.Single(rows.Where(r => r.Kind == OutlineRowKind.Empty));
        Assert.Equal("（无章节）", empty.Title);
    }

    [Fact]
    public void Build_FilterHitsNothing_EmitsEmptyRow()
    {
        var rows = Build(BuildStructure(), State(keyword: "不存在的标题"));
        var empty = Assert.Single(rows.Where(r => r.Kind == OutlineRowKind.Empty));
        Assert.Contains("没有命中章节", empty.Title);
    }

    [Fact]
    public void Signature_IsStable_AndChangesWithContent()
    {
        var structure = BuildStructure(volumeCount: 2);
        var rows = Build(structure, State());
        Assert.Equal(OutlineRows.Signature(rows), OutlineRows.Signature(Build(structure, State())));

        // Title 变化 → 指纹变化（行是值快照，先取旧指纹再改）。
        var before = OutlineRows.Signature(Build(structure, State(expandedVolumes: ["vol-1"])));
        structure.Chapters.First(c => c.Id == "ch-1").Title = "改名";
        var after = OutlineRows.Signature(Build(structure, State(expandedVolumes: ["vol-1"])));

        Assert.NotEqual(before, after);
    }
}
