using Core.Data;
using Desktop.Models;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设定面板实体筛选（对齐 TUI EntityFilterDialog 语义）：维度与选项按类型 / 当前数据构建；
/// 筛选与搜索叠加、先于展示重建；占位文案带「筛选中 N/M」；条件跨刷新保留。
///
/// 用真实 <see cref="FileNovelStore"/> 落盘 + 同步 UI 投递（<c>post: a =&gt; a()</c>），
/// 不依赖 Avalonia 平台即可断言 VM 的集合与占位文案。
/// </summary>
public sealed class SetUpPanelEntityFilterTests : IDisposable
{
    private const string NovelId = "n1";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public SetUpPanelEntityFilterTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"desktop-filter-{Guid.NewGuid():N}");
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

    private async Task SeedNovelAsync() =>
        await _store.SaveNovel(new Novel { Id = NovelId, Title = "筛选测试" });

    private Task SaveCharacterAsync(string id, string name, CharacterRole role, string gender) =>
        _store.SaveCharacter(
            new Character
            {
                Id = id,
                NovelId = NovelId,
                Name = name,
                Role = role,
                Gender = gender,
            }
        );

    /// <summary>角色按定位筛选：集合只剩命中项；占位文案带命中数；清除后恢复全量与默认文案。</summary>
    [Fact]
    public async Task CharacterFilter_NarrowsList_AndShowsHitCountInPlaceholder()
    {
        await SeedNovelAsync();
        await SaveCharacterAsync("c1", "阿风", CharacterRole.Protagonist, "Male");
        await SaveCharacterAsync("c2", "阿雨", CharacterRole.Protagonist, "女");
        await SaveCharacterAsync("c3", "路人", CharacterRole.Supporting, "");

        var vm = CreateViewModel();
        await vm.RefreshAsync();
        vm.ActiveSection = 1; // 角色分区（SectionTypes 顺序：0=大纲）
        Assert.Equal("搜索 角色（名字 / 关键词）", vm.SearchPlaceholder);

        vm.ApplyEntityFilter(EntityType.Character, new EntityListFilter("Protagonist", null));

        Assert.Equal(2, vm.Characters.Count);
        Assert.All(vm.Characters, i => Assert.Contains(i.Id, new[] { "c1", "c2" }));
        Assert.Equal("搜索 角色 · 筛选中 2/3", vm.SearchPlaceholder);

        vm.ApplyEntityFilter(EntityType.Character, EntityListFilter.None);

        Assert.Equal(3, vm.Characters.Count);
        Assert.Equal("搜索 角色（名字 / 关键词）", vm.SearchPlaceholder);
    }

    /// <summary>筛选与搜索叠加：两个条件同时生效，命中数 = 交集。</summary>
    [Fact]
    public async Task Filter_CombinesWithSearchText()
    {
        await SeedNovelAsync();
        await SaveCharacterAsync("c1", "阿风", CharacterRole.Protagonist, "Male");
        await SaveCharacterAsync("c2", "阿雨", CharacterRole.Protagonist, "女");
        await SaveCharacterAsync("c3", "风叔", CharacterRole.Supporting, "男");

        var vm = CreateViewModel();
        await vm.RefreshAsync();
        vm.ActiveSection = 1;

        vm.ApplyEntityFilter(EntityType.Character, new EntityListFilter("Protagonist", null));
        vm.SearchText = "风";

        Assert.Single(vm.Characters);
        Assert.Equal("c1", vm.Characters[0].Id);
        Assert.Equal("搜索 角色 · 筛选中 1/3", vm.SearchPlaceholder);
    }

    /// <summary>
    /// 角色次级维度 = 性别：选项按当前数据构建（不含空值）；匹配按归一后的中文标签
    /// （英文线协议值与旧中文值都归一到同一个维度值）。
    /// </summary>
    [Fact]
    public async Task CharacterGenderDimension_NormalizesLegacyChineseStoredValues()
    {
        await SeedNovelAsync();
        await SaveCharacterAsync("c1", "阿风", CharacterRole.Protagonist, "Male");
        await SaveCharacterAsync("c2", "阿雨", CharacterRole.Protagonist, "男");
        await SaveCharacterAsync("c3", "阿雪", CharacterRole.Supporting, "Female");

        var vm = CreateViewModel();
        await vm.RefreshAsync();

        var (primary, secondary) = vm.BuildEntityFilterFields(EntityType.Character);
        Assert.NotNull(primary);
        Assert.NotNull(secondary);
        Assert.Equal("性别", secondary!.Label);
        Assert.Equal(["全部", "女", "男"], secondary.Options.Select(o => o.Label));

        vm.ApplyEntityFilter(EntityType.Character, new EntityListFilter(null, "男"));

        // "Male"（线协议）与 "男"（旧数据）归一到同一维度值
        Assert.Equal(2, vm.Characters.Count);
    }

    /// <summary>场景筛选「所属卷」：选项来自结构索引（全部 / 各卷 / 未分卷），按章 → 卷命中。</summary>
    [Fact]
    public async Task SceneFilter_MatchesByOwningVolume()
    {
        await SeedNovelAsync();
        await _store.SaveVolume(
            new Volume
            {
                Id = "vol1",
                NovelId = NovelId,
                Name = "风起",
                Order = 1,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "vol1",
            }
        );
        await _store.SaveScene(
            new SceneDefinition
            {
                Id = "s1",
                NovelId = NovelId,
                Title = "场景一",
                ChapterId = "ch1",
                Order = 1,
            }
        );
        await _store.SaveScene(
            new SceneDefinition
            {
                Id = "s2",
                NovelId = NovelId,
                Title = "场景二",
                Order = 2,
            }
        );

        var vm = CreateViewModel();
        await vm.RefreshAsync();

        var (primary, _) = vm.BuildEntityFilterFields(EntityType.Scene);
        Assert.NotNull(primary);
        Assert.Equal("所属卷", primary!.Label);
        Assert.Contains(("第1卷 · 风起", "vol1"), primary.Options);
        Assert.Contains(("（未分卷）", ""), primary.Options);

        vm.ApplyEntityFilter(EntityType.Scene, new EntityListFilter("vol1", null));
        Assert.Single(vm.Scenes);
        Assert.Equal("s1", vm.Scenes[0].Id);

        vm.ApplyEntityFilter(EntityType.Scene, new EntityListFilter("", null));
        Assert.Single(vm.Scenes);
        Assert.Equal("s2", vm.Scenes[0].Id);
    }

    /// <summary>筛选维度选项与编辑窗字段同源（关系类型 = 编辑窗的全量枚举，含扩展类型）。</summary>
    [Fact]
    public async Task RelationshipFilterOptions_MirrorEditorRegistry()
    {
        await SeedNovelAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        var (primary, secondary) = vm.BuildEntityFilterFields(EntityType.Relationship);

        Assert.NotNull(primary);
        Assert.Contains(("徒弟", "Disciple"), primary!.Options);
        Assert.Contains(("盟友", "Ally"), primary.Options);
        Assert.NotNull(secondary);
        Assert.Equal("强度", secondary!.Label);
        Assert.Equal(["全部", "弱", "中", "强", "极强"], secondary.Options.Select(o => o.Label));
    }

    /// <summary>筛选条件跨刷新保留（刷新只重算命中数，不清掉条件）。</summary>
    [Fact]
    public async Task Filter_PersistsAcrossRefresh()
    {
        await SeedNovelAsync();
        await SaveCharacterAsync("c1", "阿风", CharacterRole.Protagonist, "Male");
        await SaveCharacterAsync("c2", "路人", CharacterRole.Supporting, "");

        var vm = CreateViewModel();
        await vm.RefreshAsync();
        vm.ActiveSection = 1;
        vm.ApplyEntityFilter(EntityType.Character, new EntityListFilter("Protagonist", null));

        await vm.RefreshAsync();

        Assert.Single(vm.Characters);
        Assert.Equal("搜索 角色 · 筛选中 1/2", vm.SearchPlaceholder);
    }

    /// <summary>大纲分区占位：范围 / 状态生效时附「筛选中 N/M」（口径 = 可见章行 / 总章数）。</summary>
    [Fact]
    public async Task OutlinePlaceholder_ShowsHitCountWhenScoped()
    {
        await SeedNovelAsync();
        await _store.SaveVolume(
            new Volume
            {
                Id = "vol1",
                NovelId = NovelId,
                Name = "风起",
                Order = 1,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "vol1",
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch2",
                NovelId = NovelId,
                Title = "第二章",
                Order = 2,
            }
        );

        var vm = CreateViewModel();
        await vm.RefreshAsync();

        Assert.Equal("搜索章节标题", vm.SearchPlaceholder); // 默认分区 = 大纲（ActiveSection 0）

        vm.ChapterStatusFilter = "发布";

        Assert.Equal("搜索章节标题 · 筛选中 0/2", vm.SearchPlaceholder);
    }
}
