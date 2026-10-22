using System.Collections.ObjectModel;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Evolution;
using Core.Novels;
using Core.Shared;
using Core.Tools;
using Desktop.Controls;
using Desktop.Models;
using Desktop.Services;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Desktop.ViewModels;

/// <summary>
/// 设定面板视图模型（右侧 8-Tab，UI.md §3；顺序对齐 TUI SetUpPanel —— 大纲在首位）：
/// 📋 大纲 / 🎭 角色 / 🏰 组织 / 📍 地点 / 📜 情节 / 🎬 场景 / 🎒 道具 / 🔗 关系
/// 从 INovelStore 加载当前小说实体，实体变更后自动刷新。
/// </summary>
public sealed class SetUpPanelViewModel : ViewModelBase
{
    private readonly INovelStore _store;
    private readonly DesktopAgentBridge? _bridge;
    private readonly ToolRegistry _registry;
    private readonly IFictionHarnessClient? _cloudHarness;
    private readonly Func<string?> _novelIdProvider;
    private readonly EntityTab _outlineTab;

    /// <summary>刷新/加载失败时的诊断日志（面板不再静默留空）。</summary>
    private readonly ILogger<SetUpPanelViewModel> _log;

    /// <summary>UI 线程投递（默认 <c>Dispatcher.UIThread.Post</c>；单测可传同步执行）。</summary>
    private readonly Action<Action> _post;

    /// <summary>默认 UI 投递实现（具名方法：避免批量文本替换把默认实现改写成自递归）。</summary>
    private static void PostToUi(Action action) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(action);

    /// <summary>本体关系索引（全实体结构关系；构建失败为 null → Chip/关系窗降级）。</summary>
    private EntityRelationIndex? _relations;

    /// <summary>角色 Tab。</summary>
    public ObservableCollection<EntityItem> Characters { get; } = [];

    /// <summary>组织 Tab。</summary>
    public ObservableCollection<EntityItem> Factions { get; } = [];

    /// <summary>地点 Tab。</summary>
    public ObservableCollection<EntityItem> Locations { get; } = [];

    /// <summary>情节 Tab。</summary>
    public ObservableCollection<EntityItem> Plotlines { get; } = [];

    /// <summary>场景 Tab。</summary>
    public ObservableCollection<EntityItem> Scenes { get; } = [];

    /// <summary>道具 Tab。</summary>
    public ObservableCollection<EntityItem> Items { get; } = [];

    /// <summary>关系 Tab。</summary>
    public ObservableCollection<EntityItem> Relationships { get; } = [];

    /// <summary>
    /// 大纲 Tab 的行序列（总纲 → 卷纲 → 卷 → 章节细纲；行模型由 Core.Novels.OutlineRows 生成，与 TUI 同构）。
    /// </summary>
    public ObservableCollection<OutlineRow> OutlineRows { get; } = [];

    /// <summary>
    /// 大纲树节点（Avalonia TreeView 用）：与 TUI 同源——都由 <see cref="Core.Novels.OutlineRows"/> 的行构建，
    /// 节点携带原始行（<see cref="OutlineNodeVm.Row"/>）供各动作处理器复用。
    /// </summary>
    public ObservableCollection<OutlineNodeVm> OutlineNodes { get; } = [];

    // ─────────── 章节按卷收窄（长篇保护：上千章不平铺扫读） ───────────

    /// <summary>章节范围选项："全部" + 各卷显示名（含未分卷）。</summary>
    public ObservableCollection<string> ChapterScopeOptions { get; } = [];

    /// <summary>全部选项的显示名（默认范围）。</summary>
    public const string AllChaptersScope = "全部章节";

    /// <summary>当前章节范围（TwoWay 绑定；变更即重填章节列表）。</summary>
    public string ChapterScope
    {
        get => field;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RebuildOutlineRows();
        }
    } = AllChaptersScope;

    /// <summary>当前范围的章节数文案（"第1卷 · 风起（12 章）"）。</summary>
    public string ChapterScopeSummary
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>章节状态选项（标题过滤 + 状态过滤，长篇定位"还没写的章节"）。</summary>
    public ObservableCollection<string> ChapterStatusOptions { get; } =
    ["全部状态", "草稿", "发布"];

    /// <summary>
    /// 模糊搜索（覆盖全部 tab 的条目 + 大纲层级；空 = 不过滤）。匹配口径 = Core.Shared.FuzzySearch
    /// （子序列、忽略大小写），与 TUI 设定面板搜索框一致。
    /// </summary>
    public string SearchText
    {
        get => field;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasSearchText));
            this.RaisePropertyChanged(nameof(HasNoSearchText));
            this.RaisePropertyChanged(nameof(SearchEmptyHint));
            NotifyEmptyStateChanged();
            _post(ApplyEntitySearch); // 七个实体 tab 集合按新搜索词重填（UI 线程；末尾刷新占位文案）
            RebuildOutlineRows(); // 大纲树章节（Core 行构建里做模糊过滤；同步刷新占位文案）
        }
    } = "";

    /// <summary>是否有搜索词（空态在「还没有 X」与「未命中 X」之间切换；TUI 同口径）。</summary>
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>无搜索词（<see cref="HasSearchText"/> 的补，供 XAML 可见性直接绑定）。</summary>
    public bool HasNoSearchText => !HasSearchText;

    /// <summary>搜索无命中时的空态文案。</summary>
    public string SearchEmptyHint => $"未命中「{SearchText.Trim()}」";

    /// <summary>分区纯名称（无图标；搜索框占位文案用；索引 = <see cref="SectionTypes"/>）。</summary>
    private static readonly string[] SectionNames =
    [
        "章节",
        "角色",
        "组织",
        "地点",
        "情节",
        "场景",
        "道具",
        "关系",
    ];

    /// <summary>
    /// 分区顺序（唯一真源；索引 = TabControl.SelectedIndex）：大纲在首位
    /// （对齐 TUI SetUpPanel —— 进面板就能看到最新进度）。Chapter 即大纲分区（卷 / 章在大纲内编辑）。
    /// </summary>
    public static readonly EntityType[] SectionTypes =
    [
        EntityType.Chapter, // 大纲
        EntityType.Character, // 角色
        EntityType.Faction, // 组织
        EntityType.Location, // 地点
        EntityType.Plotline, // 情节
        EntityType.Scene, // 场景
        EntityType.Item, // 道具
        EntityType.Relationship, // 关系
    ];

    /// <summary>当前分区序号（= TabControl.SelectedIndex；视图切换分区时回写）。</summary>
    public int ActiveSection
    {
        get;
        set
        {
            if (field == value)
                return;
            this.RaiseAndSetIfChanged(ref field, value);
            RefreshSearchPlaceholder();
            NotifyEmptyStateChanged();
        }
    }

    /// <summary>
    /// 搜索框占位文案（分区相关；筛选生效时附「筛选中 N/M」——N = 搜索 + 筛选后条数，M = 总数）。
    /// 口径对齐 TUI SetUpPanel：占位符不带放大镜（放大镜只出现在「筛选」入口）。
    /// </summary>
    public string SearchPlaceholder
    {
        get
        {
            var index = Math.Clamp(ActiveSection, 0, SectionTypes.Length - 1);
            if (index == 0)
            {
                var filterActive =
                    _scopeToVolumeId.TryGetValue(ChapterScope, out var scopeVolumeId)
                        && !ReferenceEquals(scopeVolumeId, AllChaptersScope)
                    || ParseStatusFilter(ChapterStatusFilter) is not null;
                return filterActive
                    ? $"搜索章节标题 · 筛选中 {_visibleChapterCount}/{_structure?.Chapters.Count ?? 0}"
                    : "搜索章节标题";
            }
            var type = SectionTypes[index];
            var filter = _entityFilters.GetValueOrDefault(type);
            return filter.IsActive
                ? $"搜索 {SectionNames[index]} · 筛选中 {_entityFilteredCounts.GetValueOrDefault(type)}/{_allEntityItems.GetValueOrDefault(type)?.Count ?? 0}"
                : $"搜索 {SectionNames[index]}（名字 / 关键词）";
        }
    }

    /// <summary>占位文案刷新（搜索 / 筛选 / 分区 / 数据任一变化后调用；属性变更由绑定消费）。</summary>
    private void RefreshSearchPlaceholder() => this.RaisePropertyChanged(nameof(SearchPlaceholder));

    /// <summary>当前分区对应的实体类型（大纲分区 = Chapter，无实体筛选）。</summary>
    private EntityType ActiveSectionType =>
        SectionTypes[Math.Clamp(ActiveSection, 0, SectionTypes.Length - 1)];

    /// <summary>当前分区是否有生效的筛选（空态文案在「暂无 X」与「筛选后无命中」之间切换）。</summary>
    public bool HasActiveEntityFilter =>
        _entityFilters.GetValueOrDefault(ActiveSectionType).IsActive;

    /// <summary>显示「暂无 X」空态（无搜索词且无生效筛选）。</summary>
    public bool ShowEntityEmptyHint => HasNoSearchText && !HasActiveEntityFilter;

    /// <summary>显示「筛选后无命中」空态（无搜索词但有生效筛选）。</summary>
    public bool ShowFilterEmptyHint => HasNoSearchText && HasActiveEntityFilter;

    /// <summary>筛选无命中时的空态文案（与「没有实体」/「搜索未命中」区分）。</summary>
    public string FilterEmptyHint => "（筛选后无命中）";

    /// <summary>空态相关属性变化（搜索词 / 筛选 / 分区任一变化后调用）。</summary>
    private void NotifyEmptyStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasActiveEntityFilter));
        this.RaisePropertyChanged(nameof(ShowEntityEmptyHint));
        this.RaisePropertyChanged(nameof(ShowFilterEmptyHint));
    }

    /// <summary>大纲筛选命中数（占位文案「筛选中 N/M」的分子；RebuildOutlineRows 维护）。</summary>
    private int _visibleChapterCount;

    // ── 实体筛选（与 TUI EntityFilterDialog 同口径：1~2 个维度，与搜索叠加，先于展示重建） ──

    /// <summary>各实体 tab 的筛选条件（空条件 = 不过滤）。</summary>
    private readonly Dictionary<EntityType, EntityListFilter> _entityFilters = [];

    /// <summary>各实体 tab 搜索 + 筛选后的条数（占位文案「筛选中 N/M」的分子）。</summary>
    private readonly Dictionary<EntityType, int> _entityFilteredCounts = [];

    /// <summary>某类型当前筛选条件（筛选对话框预选；无 = <see cref="EntityListFilter.None"/>）。</summary>
    internal EntityListFilter GetEntityFilter(EntityType type) =>
        _entityFilters.GetValueOrDefault(type, EntityListFilter.None);

    /// <summary>
    /// 应用筛选条件（internal 测试缝：无 app 环境直接驱动同一条路径，同 TUI ApplyEntityFilter）。
    /// 空条件 = 清除该类型筛选；条件保留到用户再次更改（刷新不丢）。
    /// </summary>
    internal void ApplyEntityFilter(EntityType type, EntityListFilter filter)
    {
        if (filter.IsActive)
            _entityFilters[type] = filter;
        else
            _entityFilters.Remove(type);
        NotifyEmptyStateChanged();
        _post(() =>
        {
            FillCollection(type, _allEntityItems.GetValueOrDefault(type) ?? []);
            RefreshSearchPlaceholder();
        });
    }

    /// <summary>
    /// 该实体类型的筛选维度（1~2 个；选项与编辑窗字段同源——选项值 = 落盘枚举名，label = 中文）。
    /// Scene = 所属卷（按结构索引构建）；Character 次级 = 性别（按当前数据出现的值构建，无数据 = 无此维度）。
    /// </summary>
    internal (EntityFilterField? Primary, EntityFilterField? Secondary) BuildEntityFilterFields(
        EntityType type
    ) =>
        type switch
        {
            EntityType.Character => (FieldOptions(type, "Role", "定位"), GenderFilterField()),
            EntityType.Faction or EntityType.Location or EntityType.Item => (
                FieldOptions(type, "Type", "类型"),
                null
            ),
            EntityType.Plotline => (
                FieldOptions(type, "Type", "类型"),
                FieldOptions(type, "Tension", "紧张度")
            ),
            EntityType.Scene => (SceneVolumeFilterField(), null),
            EntityType.Relationship => (
                FieldOptions(type, "Type", "类型"),
                FieldOptions(type, "Strength", "强度")
            ),
            _ => (null, null),
        };

    /// <summary>筛选维度选项（首项固定「全部」= null；与编辑窗字段注册表同源）。</summary>
    private static EntityFilterField? FieldOptions(EntityType type, string prop, string label)
    {
        var def = EntityFieldRegistry
            .GetTabs(type)
            .SelectMany(t => t.Fields)
            .FirstOrDefault(f => f.Prop == prop);
        if (def?.Options is null || def.OptionValues is null)
            return null;
        return new EntityFilterField(
            label,
            [("全部", null), .. def.Options.Zip(def.OptionValues, (l, v) => (l, (string?)v))]
        );
    }

    /// <summary>角色性别筛选项（按当前数据出现过的性别归一集；无数据 = 无此维度）。</summary>
    private EntityFilterField? GenderFilterField()
    {
        var genders =
            _allEntityItems
                .GetValueOrDefault(EntityType.Character)
                ?.Select(i => i.FilterSecondary ?? "")
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(g => g, StringComparer.Ordinal)
                .ToArray()
            ?? [];
        return genders.Length == 0
            ? null
            : new EntityFilterField(
                "性别",
                [("全部", null), .. genders.Select(g => (g, (string?)g))]
            );
    }

    /// <summary>场景筛选「所属卷」：全部 / 各卷 / （未分卷）。</summary>
    private EntityFilterField SceneVolumeFilterField()
    {
        var options = new List<(string Label, string? Value)> { ("全部", null) };
        foreach (var volume in StructureVolumes.OrderBy(v => v.Order))
            options.Add(
                (
                    string.IsNullOrWhiteSpace(volume.Name)
                        ? $"第{volume.Order}卷"
                        : $"第{volume.Order}卷 · {volume.Name}",
                    volume.Id
                )
            );
        options.Add(("（未分卷）", ""));
        return new EntityFilterField("所属卷", options);
    }

    /// <summary>
    /// 性别存储值 → 中文展示标签。存储值有两种历史格式：编辑窗写的英文枚举
    /// （"Male"/"Female"/"Other"，跨仓线协议值，与 TUI EntityEditDialog 同约定）与
    /// 种子数据 / 早期版本的中文（"男"/"女"）。展示侧统一归一到中文；
    /// 未识别的非空值原样返回（让用户看见自己存了什么）。
    /// </summary>
    internal static string GenderLabel(string? gender) =>
        gender switch
        {
            "男" or "Male" => "男",
            "女" or "Female" => "女",
            "其他" or "Other" => "其他",
            _ => gender ?? "",
        };

    /// <summary>各实体 tab 的全量项（搜索过滤前的基准；过滤只改集合内容，不重读存储）。</summary>
    private readonly Dictionary<EntityType, List<EntityItem>> _allEntityItems = [];

    /// <summary>某类型对应的集合（<see cref="EntityTab.Items"/> 引用的就是它）。</summary>
    private ObservableCollection<EntityItem> CollectionFor(EntityType type) =>
        type switch
        {
            EntityType.Character => Characters,
            EntityType.Faction => Factions,
            EntityType.Location => Locations,
            EntityType.Plotline => Plotlines,
            EntityType.Scene => Scenes,
            EntityType.Item => Items,
            EntityType.Relationship => Relationships,
            _ => Characters,
        };

    /// <summary>发布某 tab 的实体项：存全量 + 按当前搜索词过滤后填入集合（集合变更投递到 UI 线程）。</summary>
    private void PublishItems(EntityType type, List<EntityItem> all)
    {
        _allEntityItems[type] = all;
        _post(() => FillCollection(type, all));
    }

    /// <summary>把（搜索 + 筛选后的）项填入集合，并记录命中数（占位文案「筛选中 N/M」）；须在 UI 线程调用。</summary>
    private void FillCollection(EntityType type, List<EntityItem> all)
    {
        var target = CollectionFor(type);
        target.Clear();
        var filtered = FilterEntityItems(type, all);
        foreach (var item in filtered)
            target.Add(item);
        _entityFilteredCounts[type] = filtered.Count;
    }

    /// <summary>
    /// 实体过滤：筛选条件先于搜索应用（两者都在展示重建之前——被筛掉的条目不占位，
    /// 与 TUI CapRows 同口径）；检索文本 = 名称 / 摘要 / 元数据行拼接（空查询 = 不筛）。
    /// </summary>
    private List<EntityItem> FilterEntityItems(EntityType type, List<EntityItem> all)
    {
        var query = (IEnumerable<EntityItem>)all;
        if (_entityFilters.GetValueOrDefault(type) is { IsActive: true } filter)
            query = query.Where(i => filter.Matches(i.FilterPrimary, i.FilterSecondary));
        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(i =>
                Core.Shared.FuzzySearch.Matches($"{i.Name} {i.Summary} {i.MetaLine}", SearchText)
            );
        return query as List<EntityItem> ?? [.. query];
    }

    /// <summary>搜索词变化后重填七个实体集合（在 UI 线程调用；末尾刷新占位文案）。</summary>
    private void ApplyEntitySearch()
    {
        foreach (var (type, all) in _allEntityItems)
            FillCollection(type, all);
        RefreshSearchPlaceholder();
    }

    /// <summary>状态下拉（"全部状态" = 不限）。</summary>
    public string ChapterStatusFilter
    {
        get => field;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RebuildOutlineRows();
        }
    } = "全部状态";

    /// <summary>范围显示名 → 卷 id（<see cref="AllChaptersScope"/> 自身 = 全部；"" = 未分卷）。</summary>
    private readonly Dictionary<string, string?> _scopeToVolumeId = new(StringComparer.Ordinal);

    /// <summary>结构索引快照（总纲 + 卷纲 + 章节细纲元数据，不含正文）。</summary>
    private NovelStructure? _structure;

    /// <summary>当前展开的卷 id（或 <see cref="OutlineRows.UnassignedKey"/>）；默认全部折叠。</summary>
    private readonly HashSet<string> _expandedVolumes = [];

    /// <summary>根节点（小说行）是否展开（= 是否显示底下的卷）；默认展开。</summary>
    private bool _novelExpanded = true;

    /// <summary>刚创建的分卷 / 章节 id（一次性定位高亮；构建行后立即清除）。</summary>
    private string? _revealVolumeId;
    private string? _revealChapterId;

    /// <summary>当前展开的章节 id（单章展开）。</summary>
    private string? _expandedChapterId;

    /// <summary>各分组已加载的行数（分页懒加载；缺省 = <see cref="Core.Novels.OutlineRows.DefaultPageSize"/>）。</summary>
    private readonly Dictionary<string, int> _loadedRows = [];

    /// <summary>最近一次渲染的行序列指纹（内容未变则跳过重建，避免滚动位置被刷新重置）。</summary>
    private string _outlineSignature = "";

    /// <summary>情节线 / 角色 id → 名称（行内 chip 与明细用）。</summary>
    private readonly Dictionary<string, string> _plotlineNameMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _characterNameMap = new(StringComparer.Ordinal);

    /// <summary>总纲文本（大纲一层：Novel.Outline）。</summary>
    public string NovelOutlineText
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>
    /// 八个 Tab 的描述集合——视图通过单一数据模板渲染，取代原先 8 段同构 XAML。
    /// 顺序即视觉顺序（大纲→关系），与 <see cref="SectionTypes"/> / code-behind 的 CurrentTabType 一致。
    /// </summary>
    public IReadOnlyList<EntityTab> Tabs { get; }

    /// <summary>当前小说 ID（由 MainWindowViewModel 注入）。</summary>
    public string? NovelId => _novelIdProvider();

    /// <summary>实体引用点击事件（插入输入框）。</summary>
    public event Action<string>? EntityReferenceRequested;

    /// <summary>设计时构造。</summary>
    public SetUpPanelViewModel()
        : this(null!, null!, null!, null!, null!, null, a => a()) { }

    public SetUpPanelViewModel(
        INovelStore store,
        DesktopAgentBridge? bridge,
        ToolRegistry registry,
        IFictionHarnessClient? cloudHarness,
        Func<string?> novelIdProvider,
        ILogger<SetUpPanelViewModel>? log = null,
        Action<Action>? post = null
    )
    {
        _store = store;
        // UI 线程投递（默认具名方法 PostToUi；单测传同步执行，避免依赖 Avalonia 平台）
        _post = post ?? PostToUi;
        _bridge = bridge;
        _registry = registry;
        _cloudHarness = cloudHarness;
        _novelIdProvider = novelIdProvider;
        _log =
            log
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SetUpPanelViewModel>.Instance;

        // 大纲 Tab 的内容 = Parent.OutlineRows（行模型）；通用实体列表对大纲 Tab 隐藏，故传空集合
        _outlineTab = new EntityTab("大纲", "暂无章节", hasProgress: true, []) { Parent = this };
        // 顺序对齐 TUI SetUpPanel：大纲在首位（进面板就能看到最新进度），实体分区依次后移。
        // 顺序必须与 SectionTypes 一致（索引即分区号；code-behind 的 CurrentTabType 也读它）。
        Tabs =
        [
            _outlineTab,
            new EntityTab("角色", "暂无角色", hasProgress: false, Characters) { Parent = this },
            new EntityTab("组织", "暂无组织", hasProgress: false, Factions) { Parent = this },
            new EntityTab("地点", "暂无地点", hasProgress: false, Locations) { Parent = this },
            new EntityTab("情节", "暂无情节", hasProgress: false, Plotlines) { Parent = this },
            new EntityTab("场景", "暂无场景", hasProgress: false, Scenes) { Parent = this },
            new EntityTab("道具", "暂无道具", hasProgress: false, Items) { Parent = this },
            new EntityTab("关系", "暂无关系", hasProgress: false, Relationships) { Parent = this },
        ];

        // Agent 工具修改实体后自动刷新（去抖）；无桥接（设计时/单测）时跳过订阅
        _bridge?.OnStreamEvent += (_, evt) =>
        {
            if (evt is Core.Agent.ToolExecutionEnd { IsError: false })
            {
                _post(async () => await RefreshAsync());
            }
        };
    }

    /// <summary>刷新全部 Tab（导航/实体变更时调用）。</summary>
    public async Task RefreshAsync()
    {
        var novelId = _novelIdProvider();
        if (string.IsNullOrEmpty(novelId))
        {
            ClearAll();
            _outlineTab.SetProgress(0);
            _post(RefreshSearchPlaceholder);
            return;
        }

        try
        {
            // 本体关系索引（Chip 行与关系窗共用；失败置 null 不阻断面板）
            try
            {
                _relations = await EntityRelationIndex.BuildAsync(_store, novelId);
            }
            catch (Exception ex)
            {
                _relations = null;
                _log.LogWarning(ex, "本体关系索引构建失败 novelId={NovelId}", novelId);
            }

            await Task.WhenAll(
                LoadCharactersAsync(novelId),
                LoadFactionsAsync(novelId),
                LoadLocationsAsync(novelId),
                LoadPlotlinesAsync(novelId),
                LoadScenesAsync(novelId),
                LoadItemsAsync(novelId),
                LoadRelationshipsAsync(novelId),
                LoadOutlineTreeAsync(novelId)
            );
            // 各 Load 的集合重建是投递执行的：排在其后的占位文案刷新读到的是最新命中数
            _post(RefreshSearchPlaceholder);
        }
        catch (Exception ex)
        {
            // 数据层不可用时面板留空——但必须留痕，否则表现为“无提示的整片空白”
            _log.LogWarning(ex, "设定面板刷新失败 novelId={NovelId}", novelId);
        }
    }

    /// <summary>清空所有集合。</summary>
    private void ClearAll()
    {
        Characters.Clear();
        Factions.Clear();
        Locations.Clear();
        Plotlines.Clear();
        Scenes.Clear();
        Items.Clear();
        Relationships.Clear();
        OutlineRows.Clear();
        NovelOutlineText = "";
    }

    /// <summary>请求插入实体引用。</summary>
    public void RequestInsert(EntityItem item)
    {
        EntityReferenceRequested?.Invoke($"{item.ReferenceKind}「{item.Name}」");
    }

    /// <summary>
    /// 关系 Chip 行：取本体关系索引前 max 条（按 <see cref="EntityRelationIndex.OrderOf"/> 排序），
    /// 标签按视角侧选取；索引不可用或无关系时返回空列表（卡面 Chip 行隐藏）。
    /// </summary>
    private IReadOnlyList<string> BuildChips(EntityType type, string id, int max = 3)
    {
        if (_relations is null)
            return [];
        return
        [
            .. _relations
                .GetRelations(type, id)
                .OrderBy(r => EntityRelationIndex.OrderOf(r.EdgeType))
                .Take(max)
                .Select(r =>
                {
                    var isSource = r.SourceType == type && r.SourceId == id;
                    var counterpartType = isSource ? r.TargetType : r.SourceType;
                    var counterpartId = isSource ? r.TargetId : r.SourceId;
                    return $"{EntityRelationIndex.LabelFor(r, type, id)} {_relations.NameOf(counterpartType, counterpartId)}";
                }),
        ];
    }

    // ══════════ CHANGE_10 · 云端设定检查 ══════════

    /// <summary>正在执行设定检查。</summary>
    public bool IsChecking
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>检查结果摘要（如 "🔴 2 🟡 5 🔵 1"）。</summary>
    public string CheckSummary
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>书级摘要（顶部摘要条：书名 · 走向 · 卷章计数 · 已发布章数）。</summary>
    public BookSummary BookSummary
    {
        get => field;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } =
        new(
            Title: "",
            OutlineLine: "",
            VolumeCount: 0,
            ChapterCount: 0,
            DoneCount: 0,
            TotalWords: 0,
            CheckFindings: 0,
            OntologyFields: 0
        );

    /// <summary>是否有检查结果。</summary>
    public bool HasCheckResult
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>大纲结构量摘要（缺口密度/休眠区间/收束度等）。</summary>
    public string OutlineMetrics
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>是否有大纲结构量。</summary>
    public bool HasOutlineMetrics
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>锚点覆盖摘要。</summary>
    public string CoverageSummary
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>是否有锚点覆盖摘要（通知属性：随 CoverageSummary 同步刷新可见性）。</summary>
    public bool HasCoverageSummary
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>设定图来源摘要（graph 键：同步图 / 设定摘要回退；空 = 键缺席 → 隐藏）。</summary>
    public string GraphSummary
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>是否有设定图摘要。</summary>
    public bool HasGraphSummary
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>分卷大纲指标（每卷一行；空 = 键缺席/未同步卷 → 隐藏）。</summary>
    public string VolumeSummary
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>是否有分卷大纲指标。</summary>
    public bool HasVolumeSummary
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>检查结果 findings 集合。</summary>
    public ObservableCollection<SetupFindingItem> Findings { get; } = [];

    /// <summary>
    /// 执行云端设定检查（CHANGE_10 · fiction.setup.check）。
    /// 通过工具注册表获取 check_setup 工具，异步执行后反填结果。
    /// </summary>
    public async Task ExecuteCheckSetupAsync()
    {
        var novelId = _novelIdProvider();
        if (string.IsNullOrEmpty(novelId))
            return;

        IsChecking = true;
        try
        {
            var tool = _registry.TryGet("check_setup");
            if (tool is null)
            {
                HasCheckResult = true;
                CheckSummary = "云端设定检查不可用：未找到 check_setup 工具";
                return;
            }

            using var args = JsonDocument.Parse(
                $$"""{"novel-id":"{{novelId}}"}""",
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }
            );
            var result = await tool.ExecuteAsync(
                $"desktop-{Guid.NewGuid():N}",
                args.RootElement,
                CancellationToken.None
            );

            if (result.Details is CheckSetupDetails details)
            {
                RefreshCheckResult(details);
            }
            else
            {
                HasCheckResult = true;
                var text = string.Join(
                    "\n\n",
                    result.Content.OfType<TextContent>().Select(t => t.Text)
                );
                CheckSummary = string.IsNullOrWhiteSpace(text)
                    ? "云端设定检查完成（无输出）"
                    : text;
            }
        }
        catch (Exception ex)
        {
            HasCheckResult = true;
            CheckSummary = $"设定检查失败：{ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>从云端返回的 CheckSetupDetails 刷新 UI 绑定属性。</summary>
    private void RefreshCheckResult(CheckSetupDetails details)
    {
        Findings.Clear();
        foreach (var f in details.Findings)
        {
            Findings.Add(
                new SetupFindingItem
                {
                    Id = f.Id,
                    Verdict = f.Verdict,
                    Description = f.Description,
                    Evidence = f.Evidence,
                    FixCandidates = f.FixCandidates is { Count: > 0 }
                        ? string.Join(" / ", f.FixCandidates.Take(3))
                        : null,
                    Suggestion = f.Suggestion,
                    InvolvedElementIds = f.InvolvedElementIds,
                }
            );
        }

        CheckSummary =
            $"冲突 {details.Conflicts} · 异常 {details.Anomalies} · 待定 {details.Undetermined}";
        if (details.L0Only)
            CheckSummary += "  (基础档仅 L0)";

        if (details.OutlineSummary.Count > 0)
        {
            HasOutlineMetrics = true;
            OutlineMetrics = string.Join("  ·  ", details.OutlineSummary);
        }
        else
        {
            HasOutlineMetrics = false;
            OutlineMetrics = "";
        }

        CoverageSummary =
            details.CoverageSummary.Count > 0 ? string.Join(" · ", details.CoverageSummary) : "";
        HasCoverageSummary = CoverageSummary.Length > 0;

        // 设定图来源 + 分卷大纲（Phase 4 新增键；缺席 → 隐藏，文案由 Core 类型统一提供）
        GraphSummary = details.Graph?.Describe() ?? "";
        HasGraphSummary = GraphSummary.Length > 0;
        VolumeSummary =
            details.Volumes.Count > 0
                ? string.Join("\n", details.Volumes.Select(volume => volume.Describe()))
                : "";
        HasVolumeSummary = VolumeSummary.Length > 0;

        HasCheckResult = true;
    }

    /// <summary>
    /// 上报 finding 反馈（采纳/忽略），fire-and-forget。
    /// 失败静默日志，不阻塞用户操作。
    /// </summary>
    public async Task ReportSetupFeedback(string findingId, string decision)
    {
        if (_cloudHarness is null)
            return;
        try
        {
            await _cloudHarness.ReportSetupFeedbackAsync(
                [(findingId, decision)],
                CancellationToken.None
            );
            // 标记已反馈 → 隐藏按钮
            var finding = Findings.FirstOrDefault(f => f.Id == findingId);
            if (finding is not null)
                finding.FeedbackSubmitted = true;
        }
        catch
        {
            // 失败静默
        }
    }

    /// <summary>
    /// 清除检查结果（切换小说/关闭面板时调用）。
    /// </summary>
    public void ClearCheckResult()
    {
        HasCheckResult = false;
        CheckSummary = "";
        OutlineMetrics = "";
        HasOutlineMetrics = false;
        CoverageSummary = "";
        HasCoverageSummary = false;
        GraphSummary = "";
        HasGraphSummary = false;
        VolumeSummary = "";
        HasVolumeSummary = false;
        Findings.Clear();
    }

    // ══════════ Tab 加载 ══════════

    private async Task LoadCharactersAsync(string novelId)
    {
        var list = await _store.GetCharacters(novelId);
        var items = list.OrderBy(c => c.Name)
            .Select(c =>
            {
                // 性别是跨仓线协议值（"Male"/"Female"/"Other"），展示统一归一到中文（旧数据两种格式并存）
                var genderLabel = GenderLabel(c.Gender);
                return new EntityItem
                {
                    Id = c.Id,
                    Name = c.Name,
                    Summary = string.Join(
                        "·",
                        new[] { c.Personality, c.Appearance, genderLabel }
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Take(2)
                    ),
                    IconKind = LucideIconKind.Users,
                    ReferenceKind = "角色",
                    FilterPrimary = c.Role.ToString(),
                    FilterSecondary = genderLabel,
                    Chips = BuildChips(EntityType.Character, c.Id),
                    Details =
                    [
                        new KeyValuePair<string, string>("性格", c.Personality),
                        new KeyValuePair<string, string>("外貌", c.Appearance),
                        new KeyValuePair<string, string>("背景", c.Background),
                        new KeyValuePair<string, string>("动机", c.Motivation),
                    ],
                };
            })
            .ToList();
        PublishItems(EntityType.Character, items);
    }

    private async Task LoadFactionsAsync(string novelId)
    {
        var list = await _store.GetFactions(novelId);
        var items = list.OrderBy(f => f.Name)
            .Select(f => new EntityItem
            {
                Id = f.Id,
                Name = f.Name,
                Summary = f.Description,
                IconKind = LucideIconKind.Building,
                ReferenceKind = "组织",
                FilterPrimary = f.Type.ToString(),
                Chips = BuildChips(EntityType.Faction, f.Id),
                Details = [new KeyValuePair<string, string>("描述", f.Description)],
            })
            .ToList();
        PublishItems(EntityType.Faction, items);
    }

    private async Task LoadLocationsAsync(string novelId)
    {
        var list = await _store.GetLocations(novelId);
        var items = list.OrderBy(l => l.Name)
            .Select(l => new EntityItem
            {
                Id = l.Id,
                Name = l.Name,
                Summary = l.Description,
                IconKind = LucideIconKind.MapPin,
                ReferenceKind = "地点",
                FilterPrimary = l.Type.ToString(),
                Chips = BuildChips(EntityType.Location, l.Id),
                Details = [new KeyValuePair<string, string>("描述", l.Description)],
            })
            .ToList();
        PublishItems(EntityType.Location, items);
    }

    private async Task LoadPlotlinesAsync(string novelId)
    {
        var list = await _store.GetPlotlines(novelId);
        var items = list.OrderBy(p => p.Name)
            .Select(p =>
            {
                var (marker, icon) = (p.Type, p.ResolvedAtChapterId) switch
                {
                    (PlotlineType.Foreshadowing, null) => ("伏笔 · 未解决", "[!]"),
                    (PlotlineType.Side, null) => ("支线 · 未解决", "[=]"),
                    (PlotlineType.Hidden, null) => ("暗线 · 未解决", "[?]"),
                    (PlotlineType.Main, null) => ("主线 · 未解决", "[*]"),
                    (_, not null) => ("已解决", "[✓]"),
                    _ => ("", ""),
                };
                return new EntityItem
                {
                    Id = p.Id,
                    Name = p.Name,
                    Summary = p.Description,
                    IconKind = LucideIconKind.GitBranch,
                    Marker = marker,
                    Chips = BuildChips(EntityType.Plotline, p.Id),
                    MarkerIcon = icon,
                    ReferenceKind = "情节",
                    FilterPrimary = p.Type.ToString(),
                    FilterSecondary = p.Tension.ToString(),
                    Details =
                    [
                        new KeyValuePair<string, string>("描述", p.Description),
                        new KeyValuePair<string, string>("转折点", p.Turnpoints),
                    ],
                };
            })
            .ToList();
        PublishItems(EntityType.Plotline, items);
    }

    private async Task LoadScenesAsync(string novelId)
    {
        var list = await _store.GetScenesByNovel(novelId);
        // 筛选「所属卷」需要章 → 卷映射（结构索引按目录戳缓存，与大纲树共用同一真源）
        var structure = await _store.GetStructure(novelId);
        var chapterVolume =
            structure
                ?.Chapters.Where(c => !string.IsNullOrEmpty(c.VolumeId))
                .ToDictionary(c => c.Id, c => c.VolumeId!, StringComparer.Ordinal)
            ?? [];
        var items = list.OrderBy(s => s.Order)
            .Select(s => new EntityItem
            {
                Id = s.Id,
                Name = s.Title,
                Summary = s.Conflict.Length > 0 ? s.Conflict : s.Goal,
                IconKind = LucideIconKind.Theater,
                ReferenceKind = "场景",
                // 所属卷 id；章节缺失 / 未分卷 = ""（与筛选选项「（未分卷）」对齐）
                FilterPrimary =
                    s.ChapterId is { Length: > 0 } chapterId
                    && chapterVolume.TryGetValue(chapterId, out var volumeId)
                        ? volumeId
                        : "",
                Chips = BuildChips(EntityType.Scene, s.Id),
                Details =
                [
                    new KeyValuePair<string, string>("冲突", s.Conflict),
                    new KeyValuePair<string, string>("目标", s.Goal),
                    new KeyValuePair<string, string>("时间", s.TimeDescription),
                ],
            })
            .ToList();
        PublishItems(EntityType.Scene, items);
    }

    private async Task LoadItemsAsync(string novelId)
    {
        var list = await _store.GetItems(novelId);
        var items = list.OrderBy(i => i.Name)
            .Select(i => new EntityItem
            {
                Id = i.Id,
                Name = i.Name,
                Summary = i.Description,
                IconKind = LucideIconKind.Package,
                ReferenceKind = "道具",
                FilterPrimary = i.Type.ToString(),
                Chips = BuildChips(EntityType.Item, i.Id),
                Details = [new KeyValuePair<string, string>("描述", i.Description)],
            })
            .ToList();
        PublishItems(EntityType.Item, items);
    }

    private async Task LoadRelationshipsAsync(string novelId)
    {
        var list = await _store.GetRelationshipsByNovel(novelId);
        var characters = await _store.GetCharacters(novelId);
        var nameById = characters.ToDictionary(c => c.Id, c => c.Name);
        var items = list.Select(r => new EntityItem
            {
                Id = r.Id,
                Name =
                    $"{nameById.GetValueOrDefault(r.CharacterAId, "?")} ↔ {nameById.GetValueOrDefault(r.CharacterBId, "?")}",
                Summary = r.Description,
                IconKind = LucideIconKind.Link2,
                ReferenceKind = "关系",
                FilterPrimary = r.Type.ToString(),
                FilterSecondary = r.Strength.ToString(),
                Chips = BuildChips(EntityType.Relationship, r.Id),
                Details = [new KeyValuePair<string, string>("描述", r.Description)],
            })
            .ToList();
        _post(() =>
        {
            Relationships.Clear();
            foreach (var i in items)
                Relationships.Add(i);
        });
    }

    /// <summary>
    /// 大纲树数据（总纲 + 卷纲 + 章节细纲元数据）：一次结构索引读取，
    /// 不再全量读章节正文，也不再构建两份派生卡片（卷卡 / 章卡）。
    /// </summary>
    private async Task LoadOutlineTreeAsync(string novelId)
    {
        var structure = await _store.GetStructure(novelId);
        var totalWords = await _store.GetTotalWordCount(novelId);

        // chip / 明细需要名称表；两者都是小集合，独立读取（不依赖其它 loader 的完成时序）
        var plotlines = await _store.GetPlotlines(novelId);
        var characters = await _store.GetCharacters(novelId);

        EntityTimelineView? timeline = null;
        try
        {
            timeline = await EntityTimelineView.BuildAsync(_store, novelId);
        }
        catch (Exception ex)
        {
            // 演化视图不可用 → 降级为纯计划数据（无出场/变化计数）
            _log.LogWarning(ex, "演化视图构建失败 novelId={NovelId}", novelId);
        }

        // 全实体时间线（待发布 / 联动 / 地点 / 情节线摘要；失败降级为 null）
        EntityLogView? entityLogs = null;
        try
        {
            entityLogs = await EntityLogView.BuildAsync(_store, novelId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "时间线视图构建失败 novelId={NovelId}", novelId);
        }

        var outline = structure?.Novel.Outline ?? "";
        var chapters = structure?.Chapters ?? [];
        var volumes = structure?.Volumes ?? [];
        var done = chapters.Count(c => c.CharCount > 0);
        var percent = chapters.Count > 0 ? done * 100.0 / chapters.Count : 0;
        var plotlineNames = plotlines.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var characterNames = characters.ToDictionary(
            c => c.Id,
            c => c.Name,
            StringComparer.Ordinal
        );

        _post(() =>
        {
            _structure = structure;
            _plotlineNameMap.Clear();
            foreach (var kv in plotlineNames)
                _plotlineNameMap[kv.Key] = kv.Value;
            _characterNameMap.Clear();
            foreach (var kv in characterNames)
                _characterNameMap[kv.Key] = kv.Value;

            // 空总纲也要可见（与 TUI 大纲 tab 的占位一致）：整卡隐藏会让用户以为「大纲层不见了」
            NovelOutlineText = string.IsNullOrWhiteSpace(outline)
                ? "（未填写总纲：全书走向 / 主题 / 核心冲突 / 结局方向）"
                : outline;

            // 章节范围选项：全部 + 各卷（长篇按卷收窄；默认沿用当前选择，失效则回落全部）
            _scopeToVolumeId.Clear();
            ChapterScopeOptions.Clear();
            ChapterScopeOptions.Add(AllChaptersScope);
            _scopeToVolumeId[AllChaptersScope] = AllChaptersScope;
            foreach (var v in volumes)
            {
                var label = $"第{v.Order}卷 · {v.Name}";
                if (ChapterScopeOptions.Contains(label))
                    continue;
                ChapterScopeOptions.Add(label);
                _scopeToVolumeId[label] = v.Id;
            }
            if (chapters.Any(c => string.IsNullOrEmpty(c.VolumeId)))
            {
                const string unassigned = "未分卷";
                ChapterScopeOptions.Add(unassigned);
                _scopeToVolumeId[unassigned] = "";
            }

            _timeline = timeline;
            _entityLogs = entityLogs;

            if (!ChapterScopeOptions.Contains(ChapterScope))
                ChapterScope = AllChaptersScope; // 触发 RebuildOutlineRows
            else
                RebuildOutlineRows();

            _outlineTab.SetProgress(percent);
        });
    }

    /// <summary>章节展开所需的演化记录（章行明细用；不加载则为空）。</summary>
    private EntityTimelineView? _timeline;

    /// <summary>
    /// 全实体时间线（派生视图）：章行的「待发布 / 🔗联动 / 地点 / 情节线」摘要与
    /// 实体「时间线」窗口共用；构建失败为 null（降级为空摘要）。
    /// </summary>
    private EntityLogView? _entityLogs;

    /// <summary>
    /// 重建大纲行序列（范围 / 标题 / 状态过滤 + 分页窗口）：内容未变（指纹相同）时跳过，
    /// 避免每次刷新都把滚动位置打回顶部。
    /// </summary>
    public void RebuildOutlineRows()
    {
        var structure = _structure;
        if (structure is null)
            return;

        var isAll =
            !_scopeToVolumeId.TryGetValue(ChapterScope, out var scopeVolumeId)
            || ReferenceEquals(scopeVolumeId, AllChaptersScope);
        // 模型层「不过滤展开」：TreeView 需要卷/章节点常驻（展开由控件维护），
        // 因此把所有分组都标记为已展开交给行构建；UI 展开态另按 _expandedVolumes 回填。
        var allGroups = new HashSet<string>(StringComparer.Ordinal)
        {
            Core.Novels.OutlineRows.UnassignedKey,
        };
        foreach (var volume in _structure.Volumes)
            allGroups.Add(volume.Id);
        var state = new OutlineViewState(
            allGroups,
            _expandedChapterId is { Length: > 0 } expanded
                ? new HashSet<string>([expanded], StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            isAll ? null : scopeVolumeId,
            SearchText,
            ParseStatusFilter(ChapterStatusFilter),
            int.MaxValue, // 分页窗口关闭：整卷物化（TreeView 只看可视行）
            LoadedRows: null,
            RevealVolumeId: _revealVolumeId,
            RevealChapterId: _revealChapterId,
            NovelExpanded: _novelExpanded
        );

        var build = Core.Novels.OutlineRows.Build(
            structure,
            state,
            _plotlineNameMap,
            _characterNameMap,
            showOntology: false,
            showCheckResult: Findings.Count > 0
        );
        var rows = build.Rows;
        // 书级内容改由顶部摘要条承载（不再是树里的三行）
        BookSummary = build.Summary with
        {
            CheckFindings = Findings.Count,
        };

        // 定位高亮是一次性的：本轮行构建用掉后即清除，下一轮重建不再高亮
        _revealVolumeId = null;
        _revealChapterId = null;

        // 章行补上「实际出场 / 变化 / 待发布 / 联动」计数（列表元数据行）与展开时的演化记录
        if (_timeline is not null || _entityLogs is not null)
            rows = rows.Select(r => r.Kind == OutlineRowKind.Chapter ? DecorateChapterRow(r) : r)
                .ToList();

        var signature = Core.Novels.OutlineRows.Signature(rows);

        // 摘要与占位文案在指纹短路之前刷新（范围 / 状态变化但行形未变时，计数文案仍要同步）
        var filtered = rows.Count(r => r.Kind == OutlineRowKind.Chapter);
        _visibleChapterCount = filtered;
        ChapterScopeSummary =
            (ChapterScope == AllChaptersScope ? "" : $"{ChapterScope} · ") + $"{filtered} 章";
        RefreshSearchPlaceholder();

        if (string.Equals(signature, _outlineSignature, StringComparison.Ordinal))
            return;
        _outlineSignature = signature;

        OutlineRows.Clear();
        foreach (var row in rows)
            OutlineRows.Add(row);

        RebuildOutlineNodes(rows);
    }

    /// <summary>结构索引里的卷列表（「移动到卷」子菜单等只读消费）。</summary>
    public IReadOnlyList<Volume> StructureVolumes => _structure?.Volumes ?? [];

    // ── 面板内云端同步（选项 A：可视化 + 同步/拉取；结构仍以本地文件为准） ──

    /// <summary>云端同步状态文案（与顶部状态条同一批用语；空 = 未接线）。</summary>
    public string CloudSyncText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>是否显示同步行（云端未接线时隐藏）。</summary>
    public bool IsCloudConfigured
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>面板内「同步 / 拉取」请求（true = 仅拉取）；由 MainWindowViewModel 接到云端同步服务。</summary>
    public event Action<bool>? RequestCloudSync;

    /// <summary>
    /// 当前小说已在面板侧被删除（整本删除走确认弹窗；此事件告知宿主：
    /// 小说列表已失该条目，需要把选中项切到剩余小说并重载面板）。
    /// </summary>
    public event Action<string>? NovelDeleted;

    /// <summary>触发 <see cref="NovelDeleted"/>（删除成功后由面板调用）。</summary>
    public void NotifyNovelDeleted(string novelId) => NovelDeleted?.Invoke(novelId);

    /// <summary>更新面板内云端状态（MainWindowViewModel 在同步状态变化时调用）。</summary>
    public void SetCloudStatus(string text, bool configured)
    {
        CloudSyncText = text;
        IsCloudConfigured = configured;
    }

    /// <summary>面板内点击同步/拉取（视图 code-behind 调用）。</summary>
    public void RequestSync(bool pullOnly) => RequestCloudSync?.Invoke(pullOnly);

    /// <summary>行首点击：小说/卷/章切换展开；「继续加载」追加一页。</summary>
    public void ToggleOutlineRow(OutlineRow row)
    {
        switch (row.Kind)
        {
            case OutlineRowKind.Novel:
                _novelExpanded = !_novelExpanded;
                break;
            case OutlineRowKind.VolumeHeader:
            case OutlineRowKind.UnassignedHeader:
                if (!_expandedVolumes.Remove(row.GroupKey))
                    _expandedVolumes.Add(row.GroupKey);
                break;
            case OutlineRowKind.Chapter:
                _expandedChapterId = _expandedChapterId == row.Id ? null : row.Id;
                break;
            default:
                return;
        }
        RebuildOutlineRows();
    }

    /// <summary>卷 id → 树节点（展开态回写用）。</summary>
    private readonly Dictionary<string, OutlineNodeVm> _outlineVolumeNodes = [];

    /// <summary>由行序列构建树：小说 → 卷 → 章（与 TUI 同层级；展开态按 id 回填）。</summary>
    private void RebuildOutlineNodes(IReadOnlyList<OutlineRow> rows)
    {
        var novelRow = rows.FirstOrDefault(r => r.Kind == OutlineRowKind.Novel);
        OutlineNodes.Clear();
        _outlineVolumeNodes.Clear();
        if (novelRow is null)
            return;

        var novel = new OutlineNodeVm(
            OutlineRowKind.Novel,
            novelRow,
            _novelExpanded,
            expanded => _novelExpanded = expanded
        );
        OutlineNodes.Add(novel);

        OutlineNodeVm? group = null;
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case OutlineRowKind.VolumeHeader:
                    group = new OutlineNodeVm(
                        row.Kind,
                        row,
                        _expandedVolumes.Contains(row.Id),
                        expanded =>
                        {
                            if (expanded)
                                _expandedVolumes.Add(row.Id);
                            else
                                _expandedVolumes.Remove(row.Id);
                        }
                    );
                    _outlineVolumeNodes[row.Id] = group;
                    novel.Children.Add(group);
                    break;
                case OutlineRowKind.UnassignedHeader:
                    group = new OutlineNodeVm(
                        row.Kind,
                        row,
                        _expandedVolumes.Contains(row.GroupKey),
                        expanded =>
                        {
                            if (expanded)
                                _expandedVolumes.Add(row.GroupKey);
                            else
                                _expandedVolumes.Remove(row.GroupKey);
                        }
                    );
                    novel.Children.Add(group);
                    break;
                case OutlineRowKind.Chapter:
                    group?.Children.Add(new OutlineNodeVm(row.Kind, row, false, null));
                    break;
            }
        }
    }

    /// <summary>
    /// 创建分卷后定位：展开该卷并一次性高亮（行构建后即清除；视图侧再尽力滚动到可见）。
    /// </summary>
    public void RevealVolume(string volumeId)
    {
        _expandedVolumes.Add(volumeId);
        _revealVolumeId = volumeId;
        RebuildOutlineRows();
    }

    /// <summary>
    /// 创建章节后定位：展开其所在分组并一次性高亮
    /// （分页窗口外的章由 Core 的 reveal 兜底行兜住）。
    /// </summary>
    public void RevealChapter(string chapterId)
    {
        var volumeId = _structure
            ?.Chapters.FirstOrDefault(c => string.Equals(c.Id, chapterId, StringComparison.Ordinal))
            ?.VolumeId;
        _expandedVolumes.Add(
            string.IsNullOrEmpty(volumeId) ? Core.Novels.OutlineRows.UnassignedKey : volumeId
        );
        _revealChapterId = chapterId;
        RebuildOutlineRows();
    }

    // ─────────── 行内新建章节（结构树「＋」：Enter 连打，不弹窗） ───────────

    /// <summary>行内新建章节的目标分组键（卷 id 或 <see cref="OutlineRows.UnassignedKey"/>）；null = 输入条未开启。</summary>
    public string? InlineCreateGroupKey
    {
        get => field;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>行内新建章节的目标说明（「＋ 新建章节 → 第1卷 · 风起」）。</summary>
    public string InlineCreateCaption
    {
        get => field;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>是否显示行内新建章节输入条（Desktop 停靠在树脚下；Enter 连打）。</summary>
    public bool InlineCreateActive
    {
        get => field;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>打开行内新建章节输入条（Enter 提交可连打；Esc 取消）。</summary>
    public void OpenInlineCreate(string groupKey)
    {
        InlineCreateCaption = $"＋ 新建章节 → {DescribeInlineCreateTarget(groupKey)}";
        InlineCreateGroupKey = groupKey;
        InlineCreateActive = true;
    }

    /// <summary>关闭行内新建章节输入条（Esc / 提交后切目标）。</summary>
    public void CloseInlineCreate()
    {
        InlineCreateActive = false;
        InlineCreateGroupKey = null;
    }

    /// <summary>分组展示名：卷标题（「第1卷 · 风起」）/ 未分卷 / 未知卷退回「本卷」。</summary>
    private string DescribeInlineCreateTarget(string groupKey)
    {
        if (
            string.Equals(groupKey, Core.Novels.OutlineRows.UnassignedKey, StringComparison.Ordinal)
        )
            return "未分卷";
        var volume = _structure?.Volumes.FirstOrDefault(v =>
            string.Equals(v.Id, groupKey, StringComparison.Ordinal)
        );
        return volume is null ? "本卷"
            : string.IsNullOrWhiteSpace(volume.Name) ? $"第{volume.Order}卷"
            : $"第{volume.Order}卷 · {volume.Name}";
    }

    /// <summary>
    /// 章行装饰：元数据行补「出场 N · 变化 N · 待发布 N · 🔗联动 N」，展开时把演化记录
    /// 与时间线摘要（待发布 / 联动 / 地点 / 情节线）接到明细行尾
    /// （行模型来自 Core，宿主只做数据补充，不改变行结构）。
    /// </summary>
    private OutlineRow DecorateChapterRow(OutlineRow row)
    {
        var appearances = _timeline?.GetChapterAppearances(row.Id) ?? [];
        var changes = _timeline?.GetChapterStateChanges(row.Id) ?? [];
        var logEntries = _entityLogs?.GetByChapter(row.Id) ?? [];
        var presentCount = appearances.Count(a => a.Mode == AppearanceMode.Present);
        var pendingCount = logEntries.Count(e => e.Lifecycle == ChangeLifecycle.Pending);
        // 联动计数取状态变化记录（时间线里每条变化另有 journal 镜像行，按行计会翻倍）
        var cascadeCount = changes.Count(c => c.IsCascade);

        var meta = row.MetaLine;
        if (presentCount > 0)
            meta += $" · 出场 {presentCount}";
        if (changes.Count > 0)
            meta += $" · 变化 {changes.Count}";
        if (pendingCount > 0)
            meta += $" · 待发布 {pendingCount}";
        if (cascadeCount > 0)
            meta += $" · 🔗联动 {cascadeCount}";

        return row with
        {
            MetaLine = meta,
            DetailLines = row.IsExpanded
                ? [.. row.DetailLines, .. ChapterRecordLines(row.Id)]
                : row.DetailLines,
        };
    }

    /// <summary>
    /// 章节展开时的演化记录（实际出场 / 状态变化 / 待发布 / 🔗联动 / 地点 / 情节线）。
    /// </summary>
    public IReadOnlyList<string> ChapterRecordLines(string chapterId)
    {
        var appearances = _timeline?.GetChapterAppearances(chapterId) ?? [];
        var changes = _timeline?.GetChapterStateChanges(chapterId) ?? [];
        var logEntries = _entityLogs?.GetByChapter(chapterId) ?? [];
        var lines = new List<string>();
        if (appearances.Count > 0)
        {
            var grouped = appearances
                .GroupBy(a => (a.EntityType, a.EntityId))
                .Select(g =>
                    $"{NameOfEntity(g.Key.EntityType, g.Key.EntityId)}({(g.Any(x => x.Mode == AppearanceMode.Present) ? "在场" : "提及")})"
                )
                .ToList();
            lines.Add(
                "🎬 出场: "
                    + (
                        grouped.Count <= 12
                            ? string.Join(" ", grouped)
                            : string.Join(" ", grouped.Take(12)) + $" 等 {grouped.Count} 个"
                    )
            );
        }
        foreach (var change in changes)
        {
            var pending = change.Lifecycle == ChangeLifecycle.Pending ? "（待发布）" : "";
            lines.Add(
                $"变化{pending}: {NameOfEntity(change.TargetEntityType, change.TargetId)} {change.Summary}"
            );
        }
        var cascadeCount = changes.Count(c => c.IsCascade);
        if (cascadeCount > 0)
            lines.Add($"🔗 联动: {cascadeCount} 项");
        var locations = logEntries
            .Select(e => e.LocationName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (locations.Count > 0)
            lines.Add("📍 地点: " + string.Join(" · ", locations));
        var plotlines = logEntries
            .SelectMany(e => e.PlotlineNames)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (plotlines.Count > 0)
            lines.Add("🧵 情节线: " + string.Join(" · ", plotlines));
        return lines;
    }

    /// <summary>实体显示名（关系索引解析；索引不可用或未收录时原样返回 id）。</summary>
    public string NameOfEntity(EntityType type, string id) => _relations?.NameOf(type, id) ?? id;

    /// <summary>外部（卷卡点击）按卷收窄章节列表，并展开目标卷。</summary>
    public void SelectChapterScopeForVolume(string volumeId)
    {
        var label = _scopeToVolumeId
            .FirstOrDefault(kv => kv.Value is not null && kv.Value == volumeId)
            .Key;
        if (string.IsNullOrEmpty(label))
            return;
        _expandedVolumes.Add(volumeId); // 跳转后强制展开目标卷
        ChapterScope = label;
    }

    /// <summary>状态下拉显示名 → 枚举（"全部状态" = null）。</summary>
    internal static ChapterStatus? ParseStatusFilter(string? label) =>
        label switch
        {
            "草稿" => ChapterStatus.Draft,
            "发布" => ChapterStatus.Published,
            _ => null,
        };

    /// <summary>
    /// 从一组 finding InvolvedElementIds 中解析出首个匹配的实体。
    /// 解析顺序照抄 TUI SetUpPanel.TryResolveEntityType（Character→Plotline→Scene→…）。
    /// </summary>
    public bool TryResolveEntityLink(SetupFindingItem f, out EntityType type, out EntityItem? item)
    {
        item = null;
        var ids = f.InvolvedElementIds;
        if (ids is null || ids.Count == 0)
        {
            type = (EntityType)0;
            return false;
        }

        static string? TryMatch(IEnumerable<string> source, IReadOnlyList<string> ids)
        {
            foreach (var s in source)
                if (ids.Contains(s))
                    return s;
            return null;
        }

        var matchedId = TryMatch(
            Characters.Select(c => c.Id).Where(id => id is { Length: > 0 }),
            ids
        );
        if (matchedId is not null)
        {
            type = EntityType.Character;
            item = Characters.FirstOrDefault(c => c.Id == matchedId);
            return true;
        }
        matchedId = TryMatch(Plotlines.Select(p => p.Id).Where(id => id is { Length: > 0 }), ids);
        if (matchedId is not null)
        {
            type = EntityType.Plotline;
            item = Plotlines.FirstOrDefault(p => p.Id == matchedId);
            return true;
        }
        matchedId = TryMatch(Scenes.Select(s => s.Id).Where(id => id is { Length: > 0 }), ids);
        if (matchedId is not null)
        {
            type = EntityType.Scene;
            item = Scenes.FirstOrDefault(s => s.Id == matchedId);
            return true;
        }
        matchedId = TryMatch(Locations.Select(l => l.Id).Where(id => id is { Length: > 0 }), ids);
        if (matchedId is not null)
        {
            type = EntityType.Location;
            item = Locations.FirstOrDefault(l => l.Id == matchedId);
            return true;
        }
        matchedId = TryMatch(Items.Select(i => i.Id).Where(id => id is { Length: > 0 }), ids);
        if (matchedId is not null)
        {
            type = EntityType.Item;
            item = Items.FirstOrDefault(i => i.Id == matchedId);
            return true;
        }
        matchedId = TryMatch(Factions.Select(f => f.Id).Where(id => id is { Length: > 0 }), ids);
        if (matchedId is not null)
        {
            type = EntityType.Faction;
            item = Factions.FirstOrDefault(f => f.Id == matchedId);
            return true;
        }
        matchedId = TryMatch(
            Relationships.Select(r => r.Id).Where(id => id is { Length: > 0 }),
            ids
        );
        if (matchedId is not null)
        {
            type = EntityType.Relationship;
            item = Relationships.FirstOrDefault(r => r.Id == matchedId);
            return true;
        }
        // 章节：大纲行即章节集合（骨架行，Id 与卡片行同源）
        matchedId = TryMatch(
            OutlineRows.Where(r => r.Kind == OutlineRowKind.Chapter).Select(r => r.Id),
            ids
        );
        if (matchedId is not null)
        {
            type = EntityType.Chapter;
            item = OutlineRows.FirstOrDefault(r =>
                r.Kind == OutlineRowKind.Chapter && r.Id == matchedId
            )
                is { } chapterRow
                ? new EntityItem
                {
                    Id = chapterRow.Id,
                    Name = chapterRow.Title,
                    Summary = chapterRow.SummaryLine,
                    MetaLine = chapterRow.MetaLine,
                }
                : null;
            return item is not null;
        }
        type = (EntityType)0;
        return false;
    }

    /// <summary>
    /// 构造实体「时间线」Markdown：<see cref="EntityLogView.BuildForEntityAsync"/> 的状态变化 / 本体动作
    /// （含旧字段审计行）经 <see cref="EntityLogFormatter.ToMarkdown"/> 渲染为表格。
    /// </summary>
    public async Task<string> BuildAuditMarkdownAsync(EntityType type, string entityId)
    {
        try
        {
            var novelId = _novelIdProvider();
            if (string.IsNullOrEmpty(novelId))
                return "";
            var entries = await EntityLogView.BuildForEntityAsync(_store, novelId, type, entityId);
            var name =
                entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.EntityName))?.EntityName
                ?? _relations?.NameOf(type, entityId)
                ?? entityId;
            return EntityLogFormatter.ToMarkdown(name, entries);
        }
        catch (System.Exception ex)
        {
            _log.LogWarning(ex, "加载时间线失败 entityId={Id}", entityId);
            return "加载时间线失败";
        }
    }
}

/// <summary>
/// 单个设定 Tab 的描述（Header + 空态文案 + 实体集合 + 大纲专用进度）。
/// 让 8 段同构 XAML 收敛为一份数据模板；进度仅大纲 Tab 显示。
/// </summary>
/// <summary>
/// 大纲树节点（Desktop TreeView）：展示文本 + 原始行 + 展开态（回写状态集合）+ 能力开关。
/// </summary>
public sealed class OutlineNodeVm(
    OutlineRowKind kind,
    OutlineRow row,
    bool isExpanded,
    Action<bool>? onExpandedChanged
) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isExpanded = isExpanded;

    /// <summary>节点类型（决定右侧动作按钮）。</summary>
    public OutlineRowKind Kind { get; } = kind;

    /// <summary>原始行（动作处理器复用：id / groupKey / title）。</summary>
    public OutlineRow Row { get; } = row;

    /// <summary>标题（含图标与计数）。</summary>
    public string Title => Row.Title;

    /// <summary>副文本（计数 / 进度）。</summary>
    public string Meta => Row.MetaLine;

    /// <summary>摘要行（卷纲 / 细纲首行）。</summary>
    public string Summary => Row.SummaryLine;

    /// <summary>
    /// 是否渲染摘要行：小说节点的走向与上方书级摘要条重复（同一句话在 30% 宽的面板里出现两次），
    /// 不渲染；卷 / 章的摘要行是各自唯一去处，保留。
    /// </summary>
    public bool ShowsSummary => Kind != OutlineRowKind.Novel;

    /// <summary>子节点（卷 → 章）。</summary>
    public ObservableCollection<OutlineNodeVm> Children { get; } = [];

    /// <summary>展开态（双向绑定 TreeViewItem.IsExpanded；变更回写状态集合）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            onExpandedChanged?.Invoke(value);
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }

    /// <summary>是否章节点（菜单按层级显隐：细纲 / 内容 / 发布 / 撤回 / 删除）。</summary>
    public bool IsChapter => Kind == OutlineRowKind.Chapter;

    /// <summary>草稿章（菜单显示「发布」）。状态文案来自 Core 行模型（草稿 / 发布两态）。</summary>
    public bool IsDraft => IsChapter && Row.StatusLabel == DraftStatusLabel;

    /// <summary>已发布章（菜单显示「撤回」）。</summary>
    public bool IsPublished => IsChapter && Row.StatusLabel == PublishedStatusLabel;

    /// <summary>Core 行模型里的草稿状态文案（唯一真源 = OutlineRows.StatusLabelOf）。</summary>
    private const string DraftStatusLabel = "草稿";

    /// <summary>Core 行模型里的已发布状态文案。</summary>
    private const string PublishedStatusLabel = "发布";

    /// <summary>是否显示「＋」（小说 = 新建分卷；卷 = 新建章节）。</summary>
    public bool CanAdd =>
        Kind
            is OutlineRowKind.Novel
                or OutlineRowKind.VolumeHeader
                or OutlineRowKind.UnassignedHeader;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class EntityTab(
    string header,
    string emptyText,
    bool hasProgress,
    ObservableCollection<EntityItem> items
) : ViewModelBase
{
    /// <summary>Tab 标题。</summary>
    public string Header { get; } = header;

    /// <summary>空列表占位文案。</summary>
    public string EmptyText { get; } = emptyText;

    /// <summary>是否为大纲 Tab（显示章节进度卡）。</summary>
    public bool HasProgress { get; } = hasProgress;

    /// <summary>非大纲 Tab（渲染通用实体列表 / 空态）。</summary>
    public bool HasNoProgress => !HasProgress;

    /// <summary>实体集合（实时变化）。</summary>
    public ObservableCollection<EntityItem> Items { get; } = items;

    /// <summary>父视图模型（供 DataTemplate 内访问大纲专用属性）。</summary>
    public SetUpPanelViewModel? Parent { get; set; }

    /// <summary>大纲进度（0-100）。</summary>
    public double ProgressPercent
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>更新造型进度（仅大纲；0-100）。</summary>
    public void SetProgress(double percent) => ProgressPercent = percent;
}
