using Core.Data;
using Core.Novels;
using Core.Shared;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设定面板大纲树（Desktop 侧）：行模型驱动（总纲 → 卷纲 → 卷 → 章节细纲）+ 分页窗口 + 筛选/范围。
///
/// 用真实 <see cref="FileNovelStore"/> 落盘 + 同步 UI 投递（<c>post: a =&gt; a()</c>），
/// 不依赖 Avalonia 平台即可断言 VM 的行序列。
/// </summary>
public sealed class SetUpPanelOutlineRowsTests : IDisposable
{
    private const string NovelId = "n1";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public SetUpPanelOutlineRowsTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"desktop-outline-{Guid.NewGuid():N}");
        _root = Path.Combine(tmp, "novels");
        _myth = Path.Combine(tmp, "mythology");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_myth);
        _store = new FileNovelStore(_root, _myth);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private SetUpPanelViewModel CreateViewModel() =>
        new(_store, null, new Core.Tools.ToolRegistry(), null, () => NovelId, null, a => a());

    /// <summary>
    /// 投递路径（默认 <c>PostToUi</c> 的替身）：UI 更新必须**只**经注入的投递器排队，
    /// 执行队列后才落地——覆盖「投递被写成自递归 → 进程栈溢出」这类事故的运行期形态。
    /// </summary>
    [Fact]
    public async Task Refresh_QueuesUiUpdatesThroughInjectedPost_AndAppliesThem()
    {
        await SeedStructureAsync();
        var queue = new List<Action>();
        var vm = new SetUpPanelViewModel(
            _store,
            null,
            new Core.Tools.ToolRegistry(),
            null,
            () => NovelId,
            null,
            queue.Add
        );

        await vm.RefreshAsync();
        Assert.NotEmpty(queue); // 更新是先入队（不直接在后台线程改集合）
        Assert.Empty(vm.OutlineRows);

        foreach (var apply in queue) // 模拟 Dispatcher 泵
            apply();

        Assert.NotEmpty(vm.OutlineRows);
        // 小说根 + 2 卷头 + 1 未分卷组头 + 6 章（章节点常驻）
        Assert.Equal(10, vm.OutlineRows.Count);
        Assert.Single(vm.OutlineNodes); // 树根 = 小说
    }

    [Fact]
    public async Task OutlineNodes_DefaultCollapsed_VolumesHoldChapterChildren()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        // 节点树 = 小说 → 卷（默认折叠）→ 章（节点常驻，展开由控件维护）
        var novel = Assert.Single(vm.OutlineNodes);
        Assert.Equal(OutlineRowKind.Novel, novel.Kind);
        var volumes = novel.Children.Where(n => n.Kind == OutlineRowKind.VolumeHeader).ToList();
        Assert.Equal(2, volumes.Count);
        Assert.All(volumes, v => Assert.False(v.IsExpanded));
        Assert.Equal(4, volumes[0].Children.Count); // 第 1 卷 4 章
        Assert.Contains(novel.Children, n => n.Kind == OutlineRowKind.UnassignedHeader);

        // 行模型仍只出结构行（本体/检查不出行）
        Assert.DoesNotContain(
            vm.OutlineRows,
            r => r.Kind is OutlineRowKind.Ontology or OutlineRowKind.Check
        );
    }

    [Fact]
    public async Task OutlineRows_ToggleNovel_HidesVolumes()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        var novelRow = vm.OutlineRows[0];
        Assert.Equal(OutlineRowKind.Novel, novelRow.Kind);
        Assert.True(novelRow.IsExpanded);
        vm.ToggleOutlineRow(novelRow);

        // 小说行折叠 = 树里只剩根节点（首屏只给「当前小说」）
        var row = Assert.Single(vm.OutlineRows);
        Assert.Equal(OutlineRowKind.Novel, row.Kind);
        Assert.False(row.IsExpanded);
    }

    [Fact]
    public async Task OutlineRows_ToggleVolume_ShowsChapterRows()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        var volumeNode = vm.OutlineNodes[0]
            .Children.First(n => n.Kind == OutlineRowKind.VolumeHeader);
        volumeNode.IsExpanded = true;

        // 节点展开态回写状态集合（重建后保持）
        await vm.RefreshAsync();
        Assert.True(vm.OutlineNodes[0].Children.First().IsExpanded);

        // 行模型始终物化全部章（展开只是控件行为）
        var chapters = vm.OutlineRows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();
        Assert.Equal(6, chapters.Count); // 4 + 1 + 1（未分卷）
        Assert.Equal("1. 第1章", chapters[0].Title);
        Assert.Equal("草稿", chapters[0].MetaLine);
        Assert.Equal(1000, chapters[0].CharCount);
    }

    [Fact]
    public async Task OutlineRows_ToggleChapter_ExpandsOutlineDetail()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();
        vm.ToggleOutlineRow(vm.OutlineRows.First(r => r.Kind == OutlineRowKind.VolumeHeader));

        var chapter = vm.OutlineRows.First(r => r.Kind == OutlineRowKind.Chapter);
        Assert.False(chapter.IsExpanded);
        vm.ToggleOutlineRow(chapter);

        var expanded = vm.OutlineRows.First(r =>
            r.Kind == OutlineRowKind.Chapter && r.Id == chapter.Id
        );
        Assert.True(expanded.IsExpanded);
        Assert.Contains("细纲: 细纲1", expanded.DetailLines);
    }

    [Fact]
    public async Task OutlineRows_ScopeAndFilters_NarrowRows()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        // 范围：第 2 卷 → 只剩该卷卷头
        vm.ChapterScope = vm.ChapterScopeOptions.First(o =>
            o.StartsWith("第2卷", StringComparison.Ordinal)
        );
        var scoped = vm.OutlineRows.Where(r => r.Kind == OutlineRowKind.VolumeHeader).ToList();
        Assert.Single(scoped);
        Assert.StartsWith("第2卷", scoped[0].Title, StringComparison.Ordinal);

        // 回到全部 + 状态过滤（发布 = 偶数序号章）+ 模糊搜索（"第" 命中所有章名）
        // 注：审核/终稿状态已移除（用户报障：章节状态只该有草稿/发布两态）
        vm.ChapterScope = SetUpPanelViewModel.AllChaptersScope;
        vm.ChapterStatusFilter = "发布";
        vm.SearchText = "第";
        vm.ToggleOutlineRow(vm.OutlineRows.First(r => r.Kind == OutlineRowKind.VolumeHeader));

        var chapters = vm.OutlineRows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();
        // 过滤跨全部卷：第 1 卷偶数章 ×2 + 未分卷第 6 章（发布）+ 第 2 卷（第 5 章草稿被滤掉）
        Assert.Equal(3, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("📢", c.StatusIcon));
    }

    /// <summary>
    /// 模糊搜索（实体列表 + 大纲章节共用）：子序列匹配、清空恢复；实体集合过滤发生在全量基准上。
    /// </summary>
    [Fact]
    public async Task SearchText_FiltersEntityItems_And_OutlineChapters()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        // 实体列表：追加两个角色（SeedStructureAsync 里的角色之上）
        await _store.SaveCharacter(
            new Character
            {
                Id = "c-extra1",
                NovelId = NovelId,
                Name = "夜巡使者",
                Role = CharacterRole.Supporting,
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c-extra2",
                NovelId = NovelId,
                Name = "林长风",
                Role = CharacterRole.Protagonist,
            }
        );
        await vm.RefreshAsync();
        Assert.True(vm.Characters.Count >= 2);

        // 模糊：子序列「林风」命中「林长风」（不连续）
        vm.SearchText = "林风";
        Assert.Single(vm.Characters);
        Assert.Equal("林长风", vm.Characters[0].Name);

        // 未命中 → 空集合
        vm.SearchText = "zzz";
        Assert.Empty(vm.Characters);

        // 清空 → 恢复全量
        vm.SearchText = "";
        Assert.True(vm.Characters.Count >= 2);

        // 大纲：章节模糊过滤（子序列「1章」只命中第 1 章）
        vm.SearchText = "1章";
        vm.ToggleOutlineRow(vm.OutlineRows.First(r => r.Kind == OutlineRowKind.VolumeHeader));
        var chapters = vm.OutlineRows.Where(r => r.Kind == OutlineRowKind.Chapter).ToList();
        Assert.All(chapters, c => Assert.Contains("第1章", c.Title, StringComparison.Ordinal));
    }

    /// <summary>
    /// 大纲树（Avalonia TreeView）：小说 → 卷 → 章三层 + 节点能力开关（＋：书/卷；❌：卷/章）；
    /// 展开态回写状态集合（重建后保持）。
    /// </summary>
    /// <summary>
    /// 小说节点的摘要行与上方书级摘要条重复（同一句走向在 30% 宽面板里出现两次）→ 不渲染；
    /// 卷 / 章的摘要行是各自唯一去处 → 保留。
    /// </summary>
    [Fact]
    public async Task OutlineNodes_NovelNodeHidesSummaryLine_DuplicateOfSummaryBar()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        var novel = Assert.Single(vm.OutlineNodes);
        Assert.Equal(OutlineRowKind.Novel, novel.Kind);
        Assert.False(novel.ShowsSummary, "小说节点不得再渲染走向（摘要条已经有一份）");

        var volume = novel.Children.First(n => n.Kind == OutlineRowKind.VolumeHeader);
        Assert.True(volume.ShowsSummary);
    }

    /// <summary>
    /// 行上只有一个 ＋（下一层新建）——`CanAdd` 决定它是否出现；
    /// 详情 / 删除 / 重命名 / 移动都在 ⋯ 菜单里（XAML 结构锁见 SetUpPanelViewStructureTests）。
    /// </summary>
    [Fact]
    public async Task OutlineNodes_BuildHierarchy_WithPerLevelActions()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        var novel = Assert.Single(vm.OutlineNodes);
        Assert.Equal(OutlineRowKind.Novel, novel.Kind);
        Assert.True(novel.CanAdd); // 小说行 ＋ = 新建分卷

        var volumes = novel.Children.Where(n => n.Kind == OutlineRowKind.VolumeHeader).ToList();
        Assert.Equal(2, volumes.Count); // 夹具：2 卷
        var volume = volumes[0];
        Assert.True(volume.CanAdd); // 卷行 ＋ = 本卷新建章节
        Assert.Equal(4, volume.Children.Count); // 第 1 卷 4 章

        var chapter = volume.Children[0];
        Assert.Equal(OutlineRowKind.Chapter, chapter.Kind);
        Assert.False(chapter.CanAdd); // 章没有下一层：只有 ⋯

        // 展开态回写状态集合 → 下一轮刷新后仍保持展开
        volume.IsExpanded = true;
        await vm.RefreshAsync();
        Assert.True(vm.OutlineNodes[0].Children[0].IsExpanded);
    }

    /// <summary>2 卷（第 1 卷 4 章 / 第 2 卷 1 章）+ 1 章未分卷。</summary>
    private async Task SeedStructureAsync() => await SeedManyAsync(chaptersInFirstVolume: 4);

    private async Task SeedManyAsync(int chaptersInFirstVolume)
    {
        await _store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "结构测试",
                Outline = "全书走向",
            }
        );
        await _store.SaveVolume(
            new Volume
            {
                Id = "vol1",
                NovelId = NovelId,
                Name = "风起",
                Order = 1,
                Summary = "开篇",
            }
        );
        await _store.SaveVolume(
            new Volume
            {
                Id = "vol2",
                NovelId = NovelId,
                Name = "云涌",
                Order = 2,
            }
        );

        var order = 0;
        for (var i = 1; i <= chaptersInFirstVolume; i++)
            await SaveChapterAsync(++order, "vol1");
        await SaveChapterAsync(++order, "vol2");
        await SaveChapterAsync(++order, null); // 未分卷章
    }

    private Task SaveChapterAsync(int order, string? volumeId) =>
        _store.SaveChapter(
            new Chapter
            {
                Id = $"ch{order}",
                NovelId = NovelId,
                Title = $"第{order}章",
                Order = order,
                VolumeId = volumeId,
                Summary = $"细纲{order}",
                Status = order % 2 == 0 ? ChapterStatus.Published : ChapterStatus.Draft,
                Content = new string('文', 1000),
            }
        );

    /// <summary>
    /// 创建后定位（VM 侧）：RevealChapter 自动展开所在卷、新行进窗口并带一次性高亮；
    /// 下一轮数据刷新后高亮消费掉（防「永远高亮」）。
    /// </summary>
    [Fact]
    public async Task RevealChapter_ExpandsGroupAndHighlightsOnce()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch-new",
                NovelId = NovelId,
                Title = "新建章",
                Order = 99,
                VolumeId = "vol1",
                Status = ChapterStatus.Draft,
            }
        );
        await vm.RefreshAsync();
        // 卷默认折叠 → 节点未展开（这正是「创建后失联」的形态）
        Assert.False(vm.OutlineNodes[0].Children.First().IsExpanded);

        vm.RevealChapter("ch-new");
        Assert.True(vm.OutlineNodes[0].Children.First().IsExpanded);

        var revealed = vm.OutlineRows.Single(r =>
            r.Kind == OutlineRowKind.Chapter && r.Id == "ch-new"
        );
        Assert.True(revealed.IsNewlyCreated, "刚创建的章行应带一次性高亮标记");
        Assert.False(revealed.IsRevealOnly, "所在卷应自动展开，章行进入正常窗口");
        Assert.True(
            vm.OutlineRows.Single(r =>
                r.Kind == OutlineRowKind.VolumeHeader && r.Id == "vol1"
            ).IsExpanded,
            "所在卷必须自动展开"
        );

        await vm.RefreshAsync();
        Assert.DoesNotContain(
            vm.OutlineRows,
            r => r.Kind == OutlineRowKind.Chapter && r.Id == "ch-new" && r.IsNewlyCreated
        );
    }

    /// <summary>
    /// 行内新建章节（VM 侧）：默认收起；开 → 目标说明取自与 Core 同格式的卷标题；关 → 全清。
    /// </summary>
    [Fact]
    public async Task InlineCreate_OpenAndClose_CaptionFollowsTargetGroup()
    {
        await SeedStructureAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        Assert.False(vm.InlineCreateActive);

        vm.OpenInlineCreate(OutlineRows.UnassignedKey);
        Assert.True(vm.InlineCreateActive);
        Assert.Equal("＋ 新建章节 → 未分卷", vm.InlineCreateCaption);

        var volumeRow = vm.OutlineRows.First(r => r.Kind == OutlineRowKind.VolumeHeader);
        vm.OpenInlineCreate(volumeRow.GroupKey);
        Assert.Equal($"＋ 新建章节 → {volumeRow.Title}", vm.InlineCreateCaption);
        Assert.Equal(volumeRow.GroupKey, vm.InlineCreateGroupKey);

        vm.CloseInlineCreate();
        Assert.False(vm.InlineCreateActive);
        Assert.Null(vm.InlineCreateGroupKey);
    }
}
