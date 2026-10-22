using System.Runtime.CompilerServices;
using System.Text;
using Core.Cloud;
using Core.Data;
using Core.Evolution;
using Core.Novels;
using Core.Shared;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
using XenoAtom.Terminal.UI.Threading;

namespace Tui.Controls;

/// <summary>
/// 右侧面板：分区分栏显示角色、组织、地点、情节线、道具、关系和大纲。
/// 在 Agent 回合后调用 <see cref="RefreshAsync"/> 更新内容。
///
/// 部分类拆分（可维护性）：行菜单弹层 → SetUpPanel.RowMenu.cs；
/// 七类实体详情 Markup 构建器 → SetUpPanel.EntityDetails.cs。
/// </summary>
public sealed partial class SetUpPanel
{
    /// <summary>小说数据存储（角色、组织、地点等实体的 CRUD 来源）。</summary>
    private readonly INovelStore _store;

    /// <summary>角色折叠列表。</summary>
    private readonly Accordion _characterAccordion;

    /// <summary>组织折叠列表。</summary>
    private readonly Accordion _factionAccordion;

    /// <summary>地点折叠列表。</summary>
    private readonly Accordion _locationAccordion;

    /// <summary>情节线折叠列表。</summary>
    private readonly Accordion _plotlineAccordion;

    /// <summary>场景折叠列表。</summary>
    private readonly Accordion _sceneAccordion;

    /// <summary>道具折叠列表。</summary>
    private readonly Accordion _itemAccordion;

    /// <summary>关系折叠列表。</summary>
    private readonly Accordion _relationshipAccordion;

    /// <summary>云端同步状态文案（面板内同步可视化；TuiApp 在同步状态变化时更新）。</summary>
    private readonly State<string?> _cloudStatus = new("");

    /// <summary>云端同步状态行（+ 同步/拉取按钮）；未接线云端时整行隐藏。</summary>
    private readonly WrapHStack _cloudStatusRow;

    /// <summary>分区分栏宿主：8 个分区标题按钮，WrapHStack 自动折行。</summary>
    private readonly WrapHStack _sectionBar;

    /// <summary>
    /// 分区内容宿主：8 个子面板全部常驻挂在内部 VStack 上，只切换可见性。
    ///
    /// 不能用 <c>ContentSwitcher</c>：它只把「活动子项」暴露给树遍历（ChildrenCount/GetChild
    /// 只含选中项）→ 非活动子树永远不执行 AttachToApp（App == null）→ app 侧绑定依赖
    /// （Collapsible.IsExpanded、刷新重建等）在其上全部失效。用户症状：切过去的 tab
    /// 折叠项点不开、内容不刷新（角色 tab 正常，因为它是启动时的活动子树）。
    /// </summary>
    private readonly VStack _sectionContent;

    /// <summary>分区内容面板（下标 = 分区序号；全部常驻，仅切换 <see cref="Visual.IsVisible"/>）。</summary>
    private readonly Visual[] _sectionPanels;

    /// <summary>分区按钮（下标 = 分区序号：0=角色 … 7=大纲）。</summary>
    private readonly List<Button> _sectionButtons = [];

    /// <summary>大纲树的 Preview 宿主（展开开关绕行，见 <see cref="OutlineTreePanel"/>）。</summary>
    private OutlineTreePanel _outlineTreeHost = null!;

    // 与下面 SetStyle(new TreeViewStyle{...}) 里的取值保持一致——用于复算库的展开符命中格
    private const int _treeIndentSize = 2;
    private static readonly Rune _treeExpanderMarkerGlyph = new('→');

    /// <summary>分区标题（含 emoji；分区栏按钮文案）。</summary>
    private readonly List<string> _sectionTitles = [];

    /// <summary>分区纯名称（无 emoji）——搜索框占位文案「🔍 搜索 角色」用。</summary>
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

    /// <summary>大纲分区序号（结构树 tab；其工具带动作是「筛选」而不是「新建」）。放第一个。</summary>
    private const int OutlineSectionIndex = 0;

    /// <summary>工具带右侧的「＋ 新建」按钮（仅实体分区；大纲分区的书级新建在树节点 ＋ / ⋯ 上）。
    /// 文案不带类型名——tab 名已表达要建什么；文字按钮与工具带其它动作同风格。</summary>
    private readonly Button _contextButton;

    /// <summary>「🔍 筛选」按钮（所有分区：大纲 = 章节筛选，实体 = 该类型筛选；用户要求各 tab 统一保留）。</summary>
    private readonly Button _filterButton;

    /// <summary>各实体分区的筛选条件（空条件 = 显示全部）。</summary>
    private readonly Dictionary<EntityType, EntityListFilter> _entityFilters = [];

    /// <summary>各实体分区「搜索 + 筛选」后的条数（占位文案「筛选中 N/M」用；M = 缓存总数）。</summary>
    private readonly Dictionary<EntityType, int> _entityFilteredCounts = [];

    /// <summary>章节 id → 卷 id（场景筛选「所属卷」用；结构刷新时失效重建）。</summary>
    private Dictionary<string, string>? _chapterVolumeLookup;

    /// <summary>清空按钮（搜索框有内容才显示——空框时搜索框吃满整行）。</summary>
    private readonly Button _clearSearchButton;

    /// <summary>当前分区序号（0=角色 … 7=大纲）。</summary>
    private int _sectionIndex;
    private readonly List<BackgroundPanel> _tabContentPanels = [];
    private readonly List<ScrollViewer> _listScrolls = []; // 实体 tab 列表滚动条（构建时快照配色，主题切换时重设）

    /// <summary>云端行按钮（构造时快照主题色；主题切换时用同一个 BuildButton 重设）。</summary>
    private readonly List<Button> _cloudButtons = [];
    private PanelColors _colors; // 当前主题色（构造 + ApplyTheme 更新；启动时=传入色，不依赖静态 Current）

    /// <summary>背景面板（SidePanelBg 底色，直接包裹 TabControl）。</summary>
    private readonly BackgroundPanel _panel;

    /// <summary>当前小说完结状态（大纲 tab 工具栏切换按钮展示用）。</summary>
    private string _currentCompletionStatus = "ongoing";

    /// <summary>角色 ID 列表（与 Accordion.Children 顺序对应）。</summary>
    private readonly List<string> _characterIds = [];

    /// <summary>组织 ID 列表。</summary>
    private readonly List<string> _factionIds = [];

    /// <summary>地点 ID 列表。</summary>
    private readonly List<string> _locationIds = [];

    /// <summary>情节线 ID 列表。</summary>
    private readonly List<string> _plotlineIds = [];

    /// <summary>场景 ID 列表。</summary>
    private readonly List<string> _sceneIds = [];

    /// <summary>道具 ID 列表。</summary>
    private readonly List<string> _itemIds = [];

    /// <summary>关系 ID 列表。</summary>
    private readonly List<string> _relationshipIds = [];

    /// <summary>角色 ID→名称 映射。</summary>
    private readonly Dictionary<string, string> _characterNames = [];

    /// <summary>组织 ID→名称 映射。</summary>
    private readonly Dictionary<string, string> _factionNames = [];

    /// <summary>地点 ID→名称 映射。</summary>
    private readonly Dictionary<string, string> _locationNames = [];

    /// <summary>情节线 ID→名称 映射。</summary>
    private readonly Dictionary<string, string> _plotlineNames = [];

    /// <summary>场景 ID→名称 映射。</summary>
    private readonly Dictionary<string, string> _sceneNames = [];

    /// <summary>道具 ID→名称 映射。</summary>
    private readonly Dictionary<string, string> _itemNames = [];

    /// <summary>关系 ID→名称 映射。</summary>
    private readonly Dictionary<string, string> _relationshipNames = [];

    /// <summary>章节 ID→名称 映射。</summary>
    private readonly Dictionary<string, string> _chapterNames = [];

    // 刷新前保存各 Accordion 当前展开的实体 ID，刷新后恢复展开状态
    /// <summary>当前展开的角色实体 ID。</summary>
    private string? _expandedCharacterId;

    /// <summary>当前展开的组织实体 ID。</summary>
    private string? _expandedFactionId;

    /// <summary>当前展开的地点实体 ID。</summary>
    private string? _expandedLocationId;

    /// <summary>当前展开的情节线实体 ID。</summary>
    private string? _expandedPlotlineId;

    /// <summary>当前展开的场景实体 ID。</summary>
    private string? _expandedSceneId;

    /// <summary>当前展开的道具实体 ID。</summary>
    private string? _expandedItemId;

    /// <summary>当前展开的关系实体 ID。</summary>
    private string? _expandedRelationshipId;

    // ── 实体演化视图（entity-evolution-lifecycle §7：详情区出场摘要 + 本体时间线） ──
    /// <summary>最近一次刷新加载的演化时间线视图（只用于出场摘要：出场章序 + 强度）。</summary>
    private EntityTimelineView? _timeline;

    /// <summary>最近一次刷新构建的本体时间线（实体详情区历史轨迹数据源；失败为 null → 不渲染时间线段）。</summary>
    private EntityLogView? _entityLogs;

    // ── 本体关系索引（全实体结构关系真源，与云端图谱边词汇表一致） ──
    /// <summary>最近一次刷新构建的关系索引（失败为 null → 关系段不渲染）。</summary>
    private EntityRelationIndex? _relations;

    /// <summary>当前小说的分卷列表（大纲二层）。</summary>
    private readonly List<Volume> _volumes = [];

    /// <summary>当前小说（总纲渲染数据源）。</summary>
    private Novel? _novel;

    /// <summary>结构索引快照（总纲 + 卷纲 + 章节细纲元数据，不含正文）。</summary>
    private NovelStructure? _structure;

    // ── 大纲树：行模型状态（唯一渲染路径 = DocumentFlow） ──
    /// <summary>大纲筛选条件（长篇导航：标题关键字 + 状态 + 范围）。空条件 = 显示全部。</summary>
    private ChapterListFilter _chapterFilter = ChapterListFilter.None;

    /// <summary>最近一次刷新加载的章节骨架（Content 为空，CharCount 有值）。</summary>
    private readonly List<Chapter> _chapters = [];

    /// <summary>筛选命中数（工具栏文案用）。</summary>
    private int _visibleChapterCount;

    /// <summary>当前展开的卷 id（或 <see cref="OutlineRows.UnassignedKey"/>）；默认全部折叠。</summary>
    private readonly HashSet<string> _expandedVolumes = [];

    /// <summary>根节点（小说行）是否展开（= 是否显示底下的卷）；默认展开。</summary>
    private bool _novelExpanded = true;

    /// <summary>设定检查结果分节是否展开；默认折叠。</summary>
    private bool _checkResultsExpanded;

    /// <summary>刚创建的分卷 / 章节 id（一次性定位高亮；下一次数据刷新时清除）。</summary>
    private string? _revealVolumeId;
    private string? _revealChapterId;

    /// <summary>最近一次渲染的行序列指纹（内容未变则跳过重建，避免滚动位置被刷新重置）。</summary>
    private string _outlineSignature = "";

    /// <summary>最近一次构建的书级摘要。</summary>
    private BookSummary _bookSummary = new(
        Title: "",
        OutlineLine: "",
        VolumeCount: 0,
        ChapterCount: 0,
        DoneCount: 0,
        TotalWords: 0,
        CheckFindings: 0,
        OntologyFields: 0
    );

    /// <summary>行尾 ⋯ 溢出菜单弹层（同一时刻至多一个）。</summary>
    private AnchoredPopup<SetUpPanel>? _rowMenuPopup;

    /// <summary>已点击「显示全部」的实体类型（列表行数上限解除，刷新后保持）——仅实体 tab 使用。</summary>
    private readonly HashSet<EntityType> _showAllLists = [];

    // ── 模糊搜索（设定面板信息量增长后的定位入口；TUI/Desktop 同口径 Core.Shared.FuzzySearch） ──
    /// <summary>搜索框（实体列表 / 大纲章节统一入口）。</summary>
    private readonly TextBox _searchBox;

    /// <summary>当前搜索文本（空白 = 不过滤）。</summary>
    private string _searchText = "";

    /// <summary>最近一次刷新的小说 id（搜索重建用它，不依赖宿主是否设置了 <see cref="CurrentNovelId"/>）。</summary>
    private string _lastRefreshNovelId = "";

    // 搜索重建缓存：RefreshAsync(reload:true) 载入的原始实体；搜索变更时 reload:false 复用，
    // 做到零 I/O 重建列表（含行数上限裁剪前的过滤）。
    private List<Character> _cachedCharacters = [];
    private List<Faction> _cachedFactions = [];
    private List<FactionRelationship> _cachedFactionRels = [];
    private List<Location> _cachedLocations = [];
    private List<Plotline> _cachedPlotlines = [];
    private List<SceneDefinition> _cachedScenes = [];
    private List<Item> _cachedItems = [];
    private List<Relationship> _cachedRelationships = [];
    private List<Chapter> _cachedChaptersForPlotline = [];

    /// <summary>单个列表 tab 默认渲染行数上限（超出折叠为「显示全部」行，防大目录撑爆视口）。</summary>
    private const int MaxAccordionRows = 60;

    /// <summary>章节 Id → Order 映射（出场摘要需章序）。</summary>
    private readonly Dictionary<string, int> _chapterOrderMap = [];

    // ── Collapsible Preview 阶段指针捕获的变通方案（XenoAtom bug） ──
    // Collapsible.OnPointerPressed fires during Preview (tunnel) before child buttons,
    // capturing all clicks and preventing Button from working. Clicking a focusable
    // Button inside the header also triggers a HasFocusWithin read-write tracking
    // context crash in PrepareChildren when the Collapsible subsequently collapses.
    //
    // Fix: use non-focusable Markup labels instead of Buttons, and intercept
    // PointerPressed on the Accordion's parent panel during Preview to fire
    // the associated action before Collapsible captures the event.
    private readonly ConditionalWeakTable<Visual, Action> _headerActions = new();

    /// <summary>
    /// 包装 Accordion 以在 Preview（隧道）阶段拦截子标签点击，
    /// 防止 Collapsible 捕获它们。
    /// </summary>
    private sealed class AccordionActionPanel : Panel
    {
        private readonly ConditionalWeakTable<Visual, Action> _actions;

        public AccordionActionPanel(ConditionalWeakTable<Visual, Action> actions)
        {
            _actions = actions;
            HorizontalAlignment = Align.Stretch;
            // 高度顶对齐：与 Accordion 的 Align.Start 配套，保持折叠项紧凑排布
            VerticalAlignment = Align.Start;
            AddHandler(PointerPressedEvent, HandlePreviewAction);
        }

        private void HandlePreviewAction(object? sender, PointerEventArgs e)
        {
            // Only intercept during Preview (tunnel) — this runs BEFORE Collapsible.
            if (e.RoutingPhase != RoutingPhase.Preview)
                return;
            if (e.OriginalSource is { } source && _actions.TryGetValue(source, out var action))
            {
                e.Handled = true; // prevent Collapsible from capturing
                // Defer action to next dispatcher frame — firing during event routing
                Dispatcher.Current.Post(() => action());
            }
        }
    }

    /// <summary>
    /// 大纲树的 Preview 宿主：把「点行」也变成展开/收起开关。
    /// <para>
    /// 库缺陷：TreeView 只在点击 X 精确等于展开符那一格时才切换（contentX == expanderX），
    /// 点图标 / 标题 / 树线只算选中，用户根本点不中那 1 格（用户报障：点「未分卷」行无效果）。
    /// </para>
    /// <para>
    /// 为什么包父面板：TreeView.OnPointerPressed 会把事件标记为已处理，而
    /// PointerPressed(...) 扩展是 handledEventsToo:false，之后再也收不到；
    /// 挂在父面板的 Preview（隧道）阶段就能赶在它之前。同 <see cref="AccordionActionPanel"/>。
    /// </para>
    /// </summary>
    private sealed class OutlineTreePanel : Panel
    {
        private readonly Action<PointerEventArgs> _onPressed;

        public OutlineTreePanel(Action<PointerEventArgs> onPressed)
        {
            _onPressed = onPressed;
            HorizontalAlignment = Align.Stretch;
            VerticalAlignment = Align.Stretch;
            AddHandler(PointerPressedEvent, Handle);
        }

        private void Handle(object? sender, PointerEventArgs e)
        {
            if (e.RoutingPhase == RoutingPhase.Preview)
                _onPressed(e);
        }
    }

    /// <summary>
    /// 大纲行点击：整行（右侧动作按钮除外）都切换展开/收起。
    /// <para>
    /// 两条避开「双重切换」的规则：</para>
    /// <list type="number">
    /// <item>只在按下时响应——库的 ToggleExpand 也在 OnPointerPressed（按下）；
    ///       连抬起一起响应会让一次点击切换两次、互相抵消。</item>
    /// <item>跳过库自己那一格展开符——库里是 contentX == expanderX 才切换，
    ///       那里库已经会切换，我们再切一次就抵消了。</item>
    /// </list>
    /// <para>
    /// 用可见行命中测试找被点的节点（库只给可见行的 Header 开 IsVisible），
    /// 不用 SelectedNode——此时还在 Preview 阶段，库尚未设置选中项。
    /// 叶子节点（章）不拦：单击章行仍只选中，打开正文等行为不受影响。
    /// </para>
    /// </summary>
    private void OnOutlineRowPressed(PointerEventArgs e)
    {
        if (e.Button != TerminalMouseButton.Left || e.Kind != TerminalMouseKind.Down)
            return;

        TreeNode? hit = null;
        var depth = 0;

        // 深度：从命中节点往上数到根（库的 expanderX = markerWidth + depth * indentSize，
        // 其中 depth 已含 HierarchyLines 开启时的 +1，所以这里从 1 起算）
        static int DepthOf(TreeNode node)
        {
            var d = 1;
            for (var p = node.Parent; p is not null; p = p.Parent)
                d++;
            return d;
        }

        void Walk(TreeNode node)
        {
            if (hit is not null)
                return;
            if (node.Header.IsVisible && node.Header.Bounds.Contains(e.UiX, e.UiY))
            {
                hit = node;
                depth = DepthOf(node);
                return;
            }
            foreach (var child in node.Children)
                Walk(child);
        }
        foreach (var root in _outlineTree.Roots)
            Walk(root);

        if (hit is null || hit.Children.Count == 0)
            return;

        // 点在右侧动作按钮（＋ / ⋯）上 → 不拦，让按钮自己处理
        foreach (var rv in hit.RightVisuals)
        {
            if (rv.Visual.IsVisible && rv.Visual.Bounds.Contains(e.UiX, e.UiY))
                return;
        }

        // 跳过库自己那一格展开符（库里 contentX == expanderX 才切换，那里它已经会切）
        var markerWidth = Math.Max(1, TerminalTextUtility.GetRuneWidth(_treeExpanderMarkerGlyph));
        var expanderX = markerWidth + depth * _treeIndentSize;
        if (e.LocalX + _outlineTree.Scroll.OffsetX == expanderX)
            return;

        hit.IsExpanded = !hit.IsExpanded;
    }

    /// <summary>颜色 → markup 的 #RRGGBB 字面量（markup 只认字面量，不认 Color 对象）。</summary>
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>最近一次云端设定检查（CHANGE_10 3b）。</summary>
    private CheckSetupDetails? _lastCheckResult;

    // AI 最近创建/更新的实体 ID — 用于刷新时高亮动画（✨ 标记）
    /// <summary>高亮实体 ID。</summary>
    private string? _highlightEntityId;

    /// <summary>高亮实体类型。</summary>
    private EntityType? _highlightEntityType;

    /// <summary>根视觉元素。</summary>
    public Visual Visual => _panel;

    /// <summary>当前小说 ID（用于 CRUD 操作）。</summary>
    public string? CurrentNovelId { get; set; }

    // ── CRUD 事件：TuiApp 订阅以弹出编辑/删除对话框 ──

    public event Action<string?, string?>? OnAddCharacter;
    public event Action<string?, string?>? OnEditCharacter;
    public event Action<string?, string?>? OnDeleteCharacter;
    public event Action<string?, string?>? OnAddFaction;
    public event Action<string?, string?>? OnEditFaction;
    public event Action<string?, string?>? OnDeleteFaction;
    public event Action<string?, string?>? OnAddLocation;
    public event Action<string?, string?>? OnEditLocation;
    public event Action<string?, string?>? OnDeleteLocation;
    public event Action<string?, string?>? OnAddPlotline;
    public event Action<string?, string?>? OnEditPlotline;
    public event Action<string?, string?>? OnDeletePlotline;
    public event Action<string?, string?>? OnAddScene;
    public event Action<string?, string?>? OnEditScene;
    public event Action<string?, string?>? OnDeleteScene;
    public event Action<string?, string?>? OnAddItem;
    public event Action<string?, string?>? OnEditItem;
    public event Action<string?, string?>? OnDeleteItem;
    public event Action<string?, string?>? OnAddRelationship;
    public event Action<string?, string?>? OnEditRelationship;
    public event Action<string?, string?>? OnDeleteRelationship;

    /// <summary>用户点击实体名称以在提示中引用时触发。</summary>
    public event Action<EntityType, string>? OnEntityClicked;

    /// <summary>用户点击章节「📝」编辑标签时触发（novelId, chapterId）。</summary>
    public event Action<string?, string?>? OnEditChapter;

    /// <summary>用户点击章节 ⋯「内容」时触发（novelId, chapterId）——只编辑正文的对话框。</summary>
    public event Action<string?, string?>? OnEditChapterContent;

    /// <summary>用户点击「📦 新建分卷」时触发（novelId, null）。</summary>
    public event Action<string?, string?>? OnAddVolume;

    /// <summary>用户点击分卷「📝」编辑标签时触发（novelId, volumeId）。</summary>
    public event Action<string?, string?>? OnEditVolume;

    /// <summary>用户点击分卷「❌」删除标签时触发（novelId, volumeId）。</summary>
    public event Action<string?, string?>? OnDeleteVolume;

    /// <summary>用户点击总纲段「📖 小说信息」时触发（novelId, novelId）——打开小说信息编辑器。</summary>
    public event Action<string?, string?>? OnEditNovel;

    /// <summary>用户点击书节点 ⋯「删除」时触发（novelId, novelId）——删除整本小说（确认弹窗）。</summary>
    public event Action<string?, string?>? OnDeleteNovel;

    /// <summary>用户点击工具栏「📄 新建章节」时触发（完整对话框：标题 + 所属卷）。</summary>
    public event Action<string?, string?>? OnAddChapter;

    /// <summary>用户点击章节「❌」删除标签时触发（novelId, chapterId）。</summary>
    public event Action<string?, string?>? OnDeleteChapter;

    /// <summary>用户点击草稿章 ⋯「📤 发布」时触发（chapterId）——走发布事务（预览 → 应用）。</summary>
    public event Action<string>? OnPublishChapter;

    /// <summary>用户点击已发布章 ⋯「↩️ 撤回」时触发（chapterId）——走撤回事务（预览 → 回滚）。</summary>
    public event Action<string>? OnUnpublishChapter;

    /// <summary>用户点击「🌐 运行设定检查」时触发（novelId）。</summary>
    public event Action<string>? OnCloudSetupCheck;

    /// <summary>设定检查 finding 反馈（CHANGE_10 §4.3）：(findingId, adopted|dismissed)。</summary>
    public event Action<string, string>? OnSetupFeedback;

    /// <summary>用户点击实体上的"📜 历史"按钮时触发。</summary>
    public event Action<EntityType, string>? OnViewAuditLog; // (entityType, entityId)

    public SetUpPanel(INovelStore store, PanelColors colors)
    {
        _store = store;
        _colors = colors; // 启动时用传入色（当前主题），不依赖静态 PanelThemes.Current（此时还是 Desert）

        _characterAccordion = new Accordion
        {
            SingleExpanded = true,
            Spacing = 0,
            // 折叠项必须按自然高度顶对齐（Align.Start）——Stretch 会让展开项
            // 在 FlexAllocator 里吸收全部剩余高度、撑满整个内容区，
            // 并把后面的折叠项挤出可视区域。紧凑排布需要自然高度。
            VerticalAlignment = Align.Start,
        };
        _factionAccordion = new Accordion
        {
            SingleExpanded = true,
            Spacing = 0,
            // 折叠项必须按自然高度顶对齐（Align.Start）——Stretch 会让展开项
            // 在 FlexAllocator 里吸收全部剩余高度、撑满整个内容区，
            // 并把后面的折叠项挤出可视区域。紧凑排布需要自然高度。
            VerticalAlignment = Align.Start,
        };
        _locationAccordion = new Accordion
        {
            SingleExpanded = true,
            Spacing = 0,
            // 折叠项必须按自然高度顶对齐（Align.Start）——Stretch 会让展开项
            // 在 FlexAllocator 里吸收全部剩余高度、撑满整个内容区，
            // 并把后面的折叠项挤出可视区域。紧凑排布需要自然高度。
            VerticalAlignment = Align.Start,
        };
        _plotlineAccordion = new Accordion
        {
            SingleExpanded = true,
            Spacing = 0,
            // 折叠项必须按自然高度顶对齐（Align.Start）——Stretch 会让展开项
            // 在 FlexAllocator 里吸收全部剩余高度、撑满整个内容区，
            // 并把后面的折叠项挤出可视区域。紧凑排布需要自然高度。
            VerticalAlignment = Align.Start,
        };
        _sceneAccordion = new Accordion
        {
            SingleExpanded = true,
            Spacing = 0,
            // 折叠项必须按自然高度顶对齐（Align.Start）——Stretch 会让展开项
            // 在 FlexAllocator 里吸收全部剩余高度、撑满整个内容区，
            // 并把后面的折叠项挤出可视区域。紧凑排布需要自然高度。
            VerticalAlignment = Align.Start,
        };
        _itemAccordion = new Accordion
        {
            SingleExpanded = true,
            Spacing = 0,
            // 折叠项必须按自然高度顶对齐（Align.Start）——Stretch 会让展开项
            // 在 FlexAllocator 里吸收全部剩余高度、撑满整个内容区，
            // 并把后面的折叠项挤出可视区域。紧凑排布需要自然高度。
            VerticalAlignment = Align.Start,
        };
        _relationshipAccordion = new Accordion
        {
            SingleExpanded = true,
            Spacing = 0,
            // 折叠项必须按自然高度顶对齐（Align.Start）——Stretch 会让展开项
            // 在 FlexAllocator 里吸收全部剩余高度、撑满整个内容区，
            // 并把后面的折叠项挤出可视区域。紧凑排布需要自然高度。
            VerticalAlignment = Align.Start,
        };
        // 大纲树：XenoAtom TreeView（根=小说 → 卷 → 章，节点右侧挂层级动作）。
        // 只渲染可视行（千章规模不铺满实例化）；展开/选中/滚动交给控件。
        _outlineTree = new TreeView
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        _outlineTree.SetStyle(BuildOutlineTreeStyle());

        // 库缺陷绕行：TreeView 的展开符命中区只有 1 格——库里写的是
        //   if (contentX == expanderX) ToggleExpand(index, expand: null);
        // 点行上其他位置（图标 / 标题 / 树线）只算「选中」，用户根本点不中那 1 格
        // （用户报障：点「未分卷」行无效果、不展开）。用 Preview 宿主把整行都变成
        // 展开/收起开关（右侧动作按钮除外）。
        // 为什么必须包一层：TreeView.OnPointerPressed 会把事件标记为已处理，
        // 而 PointerPressed(...) 扩展是 handledEventsToo:false，之后再也收不到；
        // 包在父面板上走 Preview（隧道）阶段就能赶在它之前。同 AccordionActionPanel 的写法。
        _outlineTreeHost = new OutlineTreePanel(OnOutlineRowPressed) { _outlineTree };

        // 云端同步状态行（面板内同步可视化；未接线时整行隐藏）
        var cloudSyncBtn = new Button(new Markup("[bold]⇅ 同步[/]")).Style(
            ThemeStyles.BuildButton(_colors)
        );
        cloudSyncBtn.Click(() => OnCloudSyncRequested?.Invoke(false));
        var cloudPullBtn = new Button(new Markup("[bold]↓ 拉取[/]")).Style(
            ThemeStyles.BuildButton(_colors)
        );
        cloudPullBtn.Click(() => OnCloudSyncRequested?.Invoke(true));
        // 设定检查：书级动作，入口从小说/卷行尾 ⋯ 菜单搬到工具带（用户报障：菜单项过多）。
        // 检查本身是云端能力，跟着云端行走——未接线时整行隐藏，本来也跑不了。
        // 开关式：再点一次收起分节（用户报障：展开后关不掉）。真正运行走分节内的「▶ 运行」。
        var setupCheckBtn = new Button(new Markup("[bold]🔍 检查[/]")).Style(
            ThemeStyles.BuildButton(_colors)
        );
        setupCheckBtn.Click(() => ToggleSetupCheckResults());
        _cloudButtons.AddRange([cloudSyncBtn, cloudPullBtn, setupCheckBtn]);
        _cloudStatusRow = new WrapHStack(
            new Markup(_cloudStatus) { IsSelectable = false },
            setupCheckBtn,
            cloudSyncBtn,
            cloudPullBtn
        )
            .Spacing(2)
            .RunSpacing(0)
            .Justify(WrapJustify.Start)
            .MeasureMode(WrapMeasureMode.Unconstrained)
            .VerticalAlignment(Align.Start)
            .IsVisible(false);

        // 分区内容（0=角色 … 7=大纲）；内容面板只构建一次，切换只换 IsVisible。
        // 全部子面板常驻 Attach（见 _sectionContent 注释：ContentSwitcher 会让非活动子树脱离 App）。
        // 顺序必须与 _sectionTitles / SectionNames 一致（索引即分区号）
        _sectionPanels = new Visual[]
        {
            MakeOutlineTabContent(_cloudStatusRow),
            MakeTabContent(Wrap(_characterAccordion)),
            MakeTabContent(Wrap(_factionAccordion)),
            MakeTabContent(Wrap(_locationAccordion)),
            MakeTabContent(Wrap(_plotlineAccordion)),
            MakeTabContent(Wrap(_sceneAccordion)),
            MakeTabContent(Wrap(_itemAccordion)),
            MakeTabContent(Wrap(_relationshipAccordion)),
        };
        // 大纲放第一个：进面板就能看到最新进度（用户报障：大纲在最后一个 tab，
        // 每次要连点七次才到）。实体分区依次后移。
        _sectionTitles.AddRange([
            "📋 大纲",
            "🎭 角色",
            "🏰 组织",
            "📍 地点",
            "📜 情节",
            "🎬 场景",
            "🎒 道具",
            "🔗 关系",
        ]);
        for (var i = 0; i < _sectionPanels.Length; i++)
        {
            var index = i;
            var button = new Button(new Markup(_sectionTitles[index]));
            button.Click(() => SelectSection(index));
            _sectionButtons.Add(button);
        }

        // 分区分栏：WrapHStack 保持每个按钮的自然宽度（Unconstrained = 不压缩文字），
        // 一行放不下就折行 —— 8 个分区标题在任意面板宽度下都完整可见。
        // （原 TabControl 单行 strip 在 30% 面板里必然溢出，最后一个分区只露图标。）
        _sectionBar = new WrapHStack([.. _sectionButtons])
            .Spacing(1)
            .RunSpacing(0)
            .Justify(WrapJustify.Start)
            .MeasureMode(WrapMeasureMode.Unconstrained)
            .VerticalAlignment(Align.Start);
        _sectionContent = new VStack([.. _sectionPanels])
            .Spacing(0)
            .VerticalAlignment(Align.Stretch);

        // 内容区工具带（每个分区只有这一行 chrome）：
        //   [搜索 <当前分区> …] [✕ 清空] [＋ 新建（仅实体分区）] [🔍 筛选（所有分区）]
        // 旧形态是「工具栏行（新建/编辑/删除）+ 搜索行」两行，且编辑/删除的目标是
        // 「当前展开项」这种不可见状态（点下去没有明确对象）——现在行内只剩明确动作。
        // 变更经 DynamicUpdate 订阅（框架在绑定变更时回调，无需手动事件）；
        // 过滤（搜索 + 筛选）在行数上限裁剪之前应用，避免「第 61 条以后搜不到」。
        // 默认就是大纲分区，占位符用大纲那份（不带 🔍——右侧「🔍 筛选」按钮已有一个，
        // 一前一后两个放大镜很丑，用户报障）；切 tab 时由 ApplySectionChrome 重新设置。
        // 不给搜索框加边框（用户报障：加了一圈 Border 太丑；且 Border 的 Inset 把这一行
        // 变成 3 行高，而右邻按钮默认顶对齐 → 🔍 落在上边框那一行、搜索文字却在中间行，
        // 看起来不垂直居中）。只保留比面板底色更深的一层填充色，让输入区「凹」出来——
        // 整行仍是 1 行高，右侧按钮天然与文字同一行。
        _searchBox = new TextBox()
            .Placeholder("搜索章节标题")
            .HorizontalAlignment(Align.Stretch)
            .Style(BuildSearchBoxStyle());
        _searchBox.Update(_ => OnSearchTextChanged());
        _clearSearchButton = new Button(new Markup("[dim]✕[/]")).Style(
            ThemeStyles.BuildButton(_colors)
        );
        _clearSearchButton.IsVisible = false; // 空框时让搜索框独占据整行
        _clearSearchButton.Click(() =>
        {
            _searchBox.Text = "";
            OnSearchTextChanged();
        });
        // 右侧三个按钮（✕ 清空 / ＋ 新建 / 🔍 筛选）——搜索框本身占满其余宽度
        _contextButton = new Button(new Markup("[bold]＋ 新建[/]")).Style(
            ThemeStyles.BuildButtonAccent(_colors)
        );
        _contextButton.Click(OnContextAction);
        _filterButton = new Button(new Markup("[bold]🔍 筛选[/]")).Style(
            ThemeStyles.BuildButton(_colors)
        );
        _filterButton.Click(OnFilterAction);
        // Grid + Star 列（HStack 只给子项自然宽度，搜索框不会拉伸 → 用户报障「搜索框没占满」）；
        // 结构 = [搜索框 Star][✕ Auto][＋ 新建 Auto][🔍 筛选 Auto]：框吃满整行，右侧只剩小按钮。
        var searchRow = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Star() },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            )
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .ColumnGap(1)
            .Cell(_searchBox, 0, 0)
            .Cell(_clearSearchButton, 0, 1)
            .Cell(_contextButton, 0, 2)
            .Cell(_filterButton, 0, 3)
            .VerticalAlignment(Align.Start);
        SelectSection(0);

        // 单层 BackgroundPanel 直接包裹分区栏 + 内容区——
        // 消除双层 BackgroundPanel 嵌套导致底部内容无法填满、产生 gap 的问题。
        // 滚动在 MakeTabContent 内：每个分区内容区（toolbar + 列表）包 ScrollViewer。
        _panel = new BackgroundPanel
        {
            FillStyle = Style
                .None.WithBackground(_colors.SidePanelBg)
                .WithForeground(_colors.SidePanelFg),
            Content = new VStack(_sectionBar, searchRow, _sectionContent)
                .Spacing(1)
                .VerticalAlignment(Align.Stretch),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
    }

    /// <summary>将 Accordion 包裹在 AccordionActionPanel 中，拦截 Preview 阶段点击。</summary>
    private AccordionActionPanel Wrap(Accordion accordion)
    {
        var panel = new AccordionActionPanel(_headerActions) { accordion };
        return panel;
    }

    // ── 分区分栏（8 个分区：角色/组织/地点/情节/场景/道具/关系/大纲） ──

    /// <summary>切换分区（0=角色 … 7=大纲）：只换可见性与按钮样式，不重建内容面板。</summary>
    private void SelectSection(int index)
    {
        if (index < 0 || index >= _sectionButtons.Count)
            return;
        _sectionIndex = index;
        ApplySectionVisibility();
        ApplySectionStyles();
        ApplySectionChrome();
    }

    /// <summary>
    /// 分区可见性：只显示选中分区面板（全部分区面板常驻 Children → 保持 AttachToApp，绑定依赖不失效）。
    /// </summary>
    private void ApplySectionVisibility()
    {
        for (var i = 0; i < _sectionPanels.Length; i++)
            _sectionPanels[i].IsVisible = i == _sectionIndex;
    }

    /// <summary>分区按钮样式：选中 = 篝火底亮月光（对齐原 tab 选中态），其余 = 面板底色 + 月光字。</summary>
    private void ApplySectionStyles()
    {
        for (var i = 0; i < _sectionButtons.Count; i++)
            _sectionButtons[i].Style(ThemeStyles.BuildSectionButton(_colors, i == _sectionIndex));
    }

    // ── Highlight & Navigate ──

    /// <summary>标记实体以在下次刷新时高亮（由 AI 工具调用使用）。</summary>
    public void SetHighlightEntity(EntityType entityType, string entityId)
    {
        _highlightEntityType = entityType;
        _highlightEntityId = entityId;
    }

    /// <summary>高亮实体并切换到正确的分区。</summary>
    public void NavigateToEntity(EntityType entityType, string entityId)
    {
        SetHighlightEntity(entityType, entityId);
        // 切到对应分区（0=大纲 … 7=关系；大纲排第一，实体分区依次后移一位）
        var targetTab = entityType switch
        {
            EntityType.Chapter => 0,
            EntityType.Volume => 0, // 分卷归大纲 tab
            EntityType.Character => 1,
            EntityType.Faction => 2,
            EntityType.Location => 3,
            EntityType.Plotline => 4,
            EntityType.Scene => 5,
            EntityType.Item => 6,
            EntityType.Relationship => 7,
            _ => -1,
        };
        if (targetTab is >= 0 and < 8) // 8 个分区
            SelectSection(targetTab);

        // 大纲树：定位章节/分卷时自动展开其所在卷（卷/章节点都只物化在展开的卷下）
        if (entityType == EntityType.Volume)
        {
            _expandedVolumes.Add(entityId);
            _revealVolumeId = entityId;
            return;
        }
        if (entityType != EntityType.Chapter)
            return;

        // 章还没加载（如启动期先于首次刷新导航）→ 不猜归属，交给 RefreshAsync 末尾的高亮路径再展开
        var chapter = _chapters.FirstOrDefault(c => c.Id == entityId);
        if (chapter is null)
            return;

        var groupKey = chapter.VolumeId is { Length: > 0 } volumeId
            ? volumeId
            : OutlineRows.UnassignedKey;
        _expandedVolumes.Add(groupKey);
        _revealChapterId = entityId;
    }

    /// <summary>创建分卷后定位：展开并一次性高亮该卷（下一次数据刷新清除）。</summary>
    public void RevealVolume(string volumeId)
    {
        _revealVolumeId = volumeId;
        _expandedVolumes.Add(volumeId);
        RenderOutline();
    }

    /// <summary>创建章节后定位：展开其所在分组并一次性高亮该章（下一次数据刷新清除）。</summary>
    public void RevealChapter(string chapterId)
    {
        _revealChapterId = chapterId;
        var chapter = _chapters.FirstOrDefault(c => c.Id == chapterId);
        if (chapter is not null)
        {
            var groupKey = chapter.VolumeId is { Length: > 0 } volumeId
                ? volumeId
                : OutlineRows.UnassignedKey;
            _expandedVolumes.Add(groupKey);
        }
        RenderOutline();
    }

    /// <summary>获取所有实体名称用于 @ 补全。</summary>
    public List<string> GetAllEntityNames()
    {
        return _characterNames.Values.ToList();
    }

    /// <summary>获取指定类型的所有实体名称（供 Stage 2 弹出窗口使用）。</summary>
    public string[] GetEntityNamesByType(EntityType entityType) =>
        entityType switch
        {
            EntityType.Character => [.. _characterNames.Values],
            EntityType.Faction => [.. _factionNames.Values],
            EntityType.Location => [.. _locationNames.Values],
            EntityType.Plotline => [.. _plotlineNames.Values],
            EntityType.Scene => [.. _sceneNames.Values],
            EntityType.Item => [.. _itemNames.Values],
            EntityType.Relationship => [.. _relationshipNames.Values],
            EntityType.Chapter => [.. _chapterNames.Values],
            _ => [],
        };

    /// <summary>名称到 ID 反向解析（供 InlineEntityReferences 提交预处理使用）。</summary>
    public string? TryResolveEntityId(EntityType entityType, string entityName) =>
        entityType switch
        {
            EntityType.Character => _characterNames
                .FirstOrDefault(kv => kv.Value == entityName)
                .Key,
            EntityType.Faction => _factionNames.FirstOrDefault(kv => kv.Value == entityName).Key,
            EntityType.Location => _locationNames.FirstOrDefault(kv => kv.Value == entityName).Key,
            EntityType.Plotline => _plotlineNames.FirstOrDefault(kv => kv.Value == entityName).Key,
            EntityType.Scene => _sceneNames.FirstOrDefault(kv => kv.Value == entityName).Key,
            EntityType.Item => _itemNames.FirstOrDefault(kv => kv.Value == entityName).Key,
            EntityType.Relationship => _relationshipNames
                .FirstOrDefault(kv => kv.Value == entityName)
                .Key,
            EntityType.Chapter => _chapterNames.FirstOrDefault(kv => kv.Value == entityName).Key,
            _ => null,
        };

    // ── 内容区工具带（搜索 + 新建 + 筛选） ──
    //
    // 分区工具带只有一行：搜索框（唯一关键字入口）+ 右侧按钮：
    //   实体分区 = 「＋ 新建」（该类型条目的唯一主动作）+「🔍 筛选」（按该类型的维度筛）；
    //   大纲分区 = 「🔍 筛选」（范围 / 状态；书级新建在树节点的 ＋ / ⋯ 上，工具带不放）。
    // 目标永远明确：不会再出现「编辑 / 删除「当前展开项」」这种看不见对象的按钮。

    /// <summary>「＋ 新建」按钮点击：实体分区新建该类型（大纲分区不显示此按钮）。</summary>
    private void OnContextAction() =>
        AddDelegateForSection(_sectionIndex)?.Invoke(CurrentNovelId, null);

    /// <summary>「🔍 筛选」按钮点击：大纲 = 章节筛选（范围 + 状态），实体 = 该类型的实体筛选。</summary>
    private void OnFilterAction()
    {
        if (_sectionIndex == OutlineSectionIndex)
        {
            ShowChapterFilterDialog();
            return;
        }
        if (EntityTypeForSection(_sectionIndex) is { } type)
            ShowEntityFilterDialog(type);
    }

    /// <summary>分区序号 → 实体类型（0 = 大纲，无实体类型；1=角色 … 7=关系，与 SectionNames 对齐）。</summary>
    private static EntityType? EntityTypeForSection(int sectionIndex) =>
        sectionIndex switch
        {
            1 => EntityType.Character,
            2 => EntityType.Faction,
            3 => EntityType.Location,
            4 => EntityType.Plotline,
            5 => EntityType.Scene,
            6 => EntityType.Item,
            7 => EntityType.Relationship,
            _ => null,
        };

    /// <summary>分区序号 → 新建委托（1=角色 … 7=关系；大纲分区=0 的行内新建在树节点上）。</summary>
    private Action<string?, string?>? AddDelegateForSection(int index) =>
        index switch
        {
            1 => OnAddCharacter,
            2 => OnAddFaction,
            3 => OnAddLocation,
            4 => OnAddPlotline,
            5 => OnAddScene,
            6 => OnAddItem,
            7 => OnAddRelationship,
            _ => null,
        };

    /// <summary>
    /// 分区切换时刷新工具带语义：搜索框占位说明「搜哪个分区」（筛选命中数也写在这里）；
    /// 「＋ 新建」只在实体分区显示，「🔍 筛选」各分区常驻、动作随分区切换。
    /// </summary>
    private void ApplySectionChrome()
    {
        var outline = _sectionIndex == OutlineSectionIndex;
        var name = SectionNames[Math.Clamp(_sectionIndex, 0, SectionNames.Length - 1)];
        // 所有分区的占位符都不带 🔍（用户报障：大纲一前一后两个放大镜；要求各 tab 统一）——
        // 放大镜只出现在右侧「🔍 筛选」按钮上。
        _searchBox.Placeholder(
            outline ? ChapterSearchPlaceholder() : EntitySearchPlaceholder(name)
        );
        // 「＋ 新建」：实体分区（强调底 + 文字按钮，不带类型名——tab 名已表达要建什么）；
        // 大纲分区隐藏（书级新建在树节点的 ＋ / ⋯ 上）。
        _contextButton.IsVisible = !outline;
        _contextButton.Style(ThemeStyles.BuildButtonAccent(_colors));
        _filterButton.Style(ThemeStyles.BuildButton(_colors));
    }

    /// <summary>大纲分区占位文案（范围/状态筛选中时带命中数；关键字由搜索框自己表达）。</summary>
    private string ChapterSearchPlaceholder()
    {
        // 只有「范围 / 状态」生效才算筛选中（避免「盒子空着却显示筛选中」）
        var filterActive =
            _chapterFilter.Status is not null || _chapterFilter.ScopeVolumeId is not null;
        return filterActive
            ? $"搜索章节标题 · 筛选中 {_visibleChapterCount}/{_chapters.Count}"
            : "搜索章节标题";
    }

    /// <summary>实体分区占位文案：该类型筛选生效时带命中数（镜像大纲的「筛选中 N/M」）。</summary>
    private string EntitySearchPlaceholder(string name)
    {
        if (
            EntityTypeForSection(_sectionIndex) is not { } type
            || _entityFilters.GetValueOrDefault(type) is not { IsActive: true }
        )
            return $"搜索 {name}（名字 / 关键词）";
        return $"搜索 {name} · 筛选中 {_entityFilteredCounts.GetValueOrDefault(type)}/{CachedEntityCount(type)}";
    }

    /// <summary>缓存里的实体总数（占位文案分母；与列表展示口径一致）。</summary>
    private int CachedEntityCount(EntityType type) =>
        type switch
        {
            EntityType.Character => _cachedCharacters.Count,
            EntityType.Faction => _cachedFactions.Count,
            EntityType.Location => _cachedLocations.Count,
            EntityType.Plotline => _cachedPlotlines.Count,
            EntityType.Scene => _cachedScenes.Count,
            EntityType.Item => _cachedItems.Count,
            EntityType.Relationship => _cachedRelationships.Count,
            _ => 0,
        };

    /// <summary>打开该实体类型的筛选对话框（选项按当前数据构建；确认后重建列表）。</summary>
    private void ShowEntityFilterDialog(EntityType type)
    {
        var (primary, secondary) = BuildEntityFilterFields(type);
        if (primary is null)
            return;
        EntityFilterDialog.Show(
            SectionNames[Math.Clamp(_sectionIndex, 0, SectionNames.Length - 1)],
            primary,
            secondary,
            _entityFilters.GetValueOrDefault(type),
            filter => ApplyEntityFilter(type, filter)
        );
    }

    /// <summary>
    /// 应用实体筛选条件（internal 测试缝：无 app 环境直接驱动同一条路径，同
    /// <see cref="ApplySearchText"/>）。空条件 = 清除该类型的筛选。
    /// </summary>
    internal void ApplyEntityFilter(EntityType type, EntityListFilter filter)
    {
        if (filter.IsActive)
            _entityFilters[type] = filter;
        else
            _entityFilters.Remove(type);
        _ = RefreshAsync(_lastRefreshNovelId, reload: false);
        ApplySectionChrome();
    }

    /// <summary>
    /// 该实体类型的筛选字段：职责与编辑对话框的字段一致
    /// （定位 / 类型 / 紧张度 / 强度 / 所属卷），选项值 = 落盘枚举名（线协议值），label = 中文。
    /// </summary>
    private (EntityFilterField? Primary, EntityFilterField? Secondary) BuildEntityFilterFields(
        EntityType type
    ) =>
        type switch
        {
            EntityType.Character => (
                FilterField(
                    "定位",
                    ("主角", "Protagonist"),
                    ("配角", "Supporting"),
                    ("龙套", "Minor"),
                    ("反派", "Antagonist")
                ),
                GenderFilterField()
            ),
            EntityType.Faction => (
                FilterField(
                    "类型",
                    ("门派", "Sect"),
                    ("公司", "Company"),
                    ("家族", "Family"),
                    ("帮会", "Gang"),
                    ("政府", "Government"),
                    ("其他", "Other")
                ),
                null
            ),
            EntityType.Location => (
                FilterField(
                    "类型",
                    ("城市", "City"),
                    ("乡村", "Rural"),
                    ("室内", "Indoor"),
                    ("荒野", "Wilderness"),
                    ("虚构", "Fictional"),
                    ("其他", "Other")
                ),
                null
            ),
            EntityType.Plotline => (
                FilterField(
                    "类型",
                    ("主线", "Main"),
                    ("支线", "Side"),
                    ("隐藏", "Hidden"),
                    ("伏笔", "Foreshadowing")
                ),
                FilterField(
                    "紧张度",
                    ("低", "Low"),
                    ("中", "Medium"),
                    ("高", "High"),
                    ("临界", "Critical")
                )
            ),
            EntityType.Scene => (VolumeFilterField(), null),
            EntityType.Item => (
                FilterField(
                    "类型",
                    ("武器", "Weapon"),
                    ("法器", "Artifact"),
                    ("信物", "Token"),
                    ("消耗品", "Consumable"),
                    ("其他", "Other")
                ),
                null
            ),
            EntityType.Relationship => (
                FilterField(
                    "类型",
                    ("朋友", "Friend"),
                    ("对手", "Rival"),
                    ("家人", "Family"),
                    ("恋人", "Love"),
                    ("敌人", "Enemy"),
                    ("导师", "Mentor"),
                    ("主仆", "MasterServant"),
                    ("同门", "FellowDisciple")
                ),
                FilterField(
                    "强度",
                    ("弱", "Weak"),
                    ("中", "Medium"),
                    ("强", "Strong"),
                    ("极强", "Extreme")
                )
            ),
            _ => (null, null),
        };

    /// <summary>构建筛选字段（首项固定「全部」= null）。</summary>
    private static EntityFilterField FilterField(
        string label,
        params (string Label, string Value)[] pairs
    ) => new(label, [("全部", null), .. pairs.Select(p => (p.Label, (string?)p.Value))]);

    /// <summary>角色性别筛选项：按当前数据里出现过的性别（归一到 男/女/其他；无数据 = 无此维度）。</summary>
    private EntityFilterField? GenderFilterField()
    {
        var genders = _cachedCharacters
            .Select(c => GenderLabel(c.Gender))
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(g => g, StringComparer.Ordinal)
            .ToArray();
        return genders.Length == 0
            ? null
            : new EntityFilterField(
                "性别",
                [("全部", null), .. genders.Select(g => (g, (string?)g))]
            );
    }

    /// <summary>场景筛选「所属卷」：全部 / 各卷 / 未分卷（无章节或章节不属于任何卷）。</summary>
    private EntityFilterField VolumeFilterField()
    {
        var options = new List<(string Label, string? Value)> { ("全部", null) };
        foreach (var volume in _volumes.OrderBy(v => v.Order))
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

    /// <summary>场景 → 所属卷 id（章节缺失或未分卷 = ""，与「（未分卷）」选项对齐）。</summary>
    private string SceneVolumeId(SceneDefinition scene)
    {
        _chapterVolumeLookup ??= _chapters
            .Where(c => !string.IsNullOrEmpty(c.VolumeId))
            .ToDictionary(c => c.Id, c => c.VolumeId!, StringComparer.Ordinal);
        return
            scene.ChapterId is { Length: > 0 } chapterId
            && _chapterVolumeLookup.TryGetValue(chapterId, out var volumeId)
            ? volumeId
            : "";
    }

    /// <summary>打开大纲筛选对话框（范围 + 状态；关键字槽位唯一 = 搜索框）。</summary>
    private void ShowChapterFilterDialog() =>
        ChapterFilterDialog.Show(
            _chapterFilter,
            _volumes,
            filter =>
            {
                // 关键字槽位唯一：搜索框有内容时以它为准（对话框里改关键字不覆盖搜索）
                _chapterFilter = string.IsNullOrWhiteSpace(_searchText)
                    ? filter
                    : filter with
                    {
                        Keyword = _searchText.Trim(),
                    };
                if (filter.ScopeVolumeId is { Length: > 0 } scope)
                    _expandedVolumes.Add(scope);
                RenderOutline();
                ApplySectionChrome();
            }
        );

    /// <summary>工具栏「新建章节」的默认归属卷：恰好展开一个卷时用它，否则未分卷。</summary>
    private string? CurrentVolumeContext()
    {
        if (_expandedVolumes.Count != 1)
            return null;
        var only = _expandedVolumes.First();
        return only == OutlineRows.UnassignedKey ? null : only;
    }

    /// <summary>
    /// 云端设定检查结果回调（CHANGE_10 3b）：TuiApp 在 check_setup 完成后调用。
    /// 缓存结果并重建大纲树（检查结果是大纲树的一行）。
    /// </summary>
    public void ShowSetupCheckResult(CheckSetupDetails details)
    {
        _lastCheckResult = details;
        RenderOutline();
    }

    /// <summary>面板内云端同步状态文案（与 StatusBar 同一批文案；空串 = 未接线）。</summary>
    public void SetCloudStatus(string text) => _cloudStatus.Value = text;

    /// <summary>是否显示面板内同步状态行（云端未接线时隐藏）。</summary>
    public void SetCloudConfigured(bool configured) => _cloudStatusRow.IsVisible = configured;

    /// <summary>面板内「同步 / 拉取」请求（true = 仅拉取）；由 TuiApp 接到云端同步服务。</summary>
    public event Action<bool>? OnCloudSyncRequested;

    /// <summary>检查结果行的展开内容（findings + 大纲/覆盖摘要；操作按钮直接挂 Click）。</summary>
    private List<Visual> BuildSetupCheckDetail()
    {
        var children = new List<Visual>();
        if (_lastCheckResult is not { } details)
        {
            // 空态引导：检查段可展开但没有结果时不能是空白（「检查按钮啥用」的直接答案）
            children.Add(
                new Markup(
                    "[dim]  尚未运行——点右侧「▶ 运行」调用云端检查；报告追加到对话，结构化结果在这里显示（🔴 冲突 / 🟡 异常 / 🔵 待定）[/]"
                )
                {
                    IsSelectable = false,
                }.Wrap(true)
            );
            return children;
        }

        if (details.Findings.Count == 0)
        {
            children.Add(new Markup("[green]结构自洽：悬空引用/自环/环均为 0[/]"));
        }
        else
        {
            foreach (var f in details.Findings)
            {
                var icon = f.Verdict switch
                {
                    "conflict" => "🔴",
                    "anomaly" => "🟡",
                    "undetermined" => "🔵",
                    _ => "⚪",
                };
                var lines = new List<string> { $"{icon} {ToolCallRenderer.Escape(f.Description)}" };
                if (!string.IsNullOrWhiteSpace(f.Evidence))
                    lines.Add($"[dim]证据:[/] {ToolCallRenderer.Escape(f.Evidence)}");
                if (f.FixCandidates is { Count: > 0 })
                    lines.Add(
                        $"[dim]修复:[/] {ToolCallRenderer.Escape(string.Join(" / ", f.FixCandidates.Take(3)))}"
                    );
                if (!string.IsNullOrWhiteSpace(f.Suggestion))
                    lines.Add($"[dim]建议:[/] {ToolCallRenderer.Escape(f.Suggestion)}");
                children.Add(
                    new Markup(string.Join("\n", lines)) { IsSelectable = false }.Wrap(true)
                );

                // 采纳率埋点（CHANGE_10 §4.3）：👍=采纳 👎=忽略 → 云端 per-rule precision_proxy
                var fid = f.Id;
                var actions = new HStack().Spacing(1);
                var up = new Button(new Markup("[dim]👍[/]")).Style(
                    ThemeStyles.BuildButton(_colors)
                );
                up.Click(() => OnSetupFeedback?.Invoke(fid, "adopted"));
                var down = new Button(new Markup("[dim]👎[/]")).Style(
                    ThemeStyles.BuildButton(_colors)
                );
                down.Click(() => OnSetupFeedback?.Invoke(fid, "dismissed"));
                actions.Add(up, down);
                actions.Add(new Markup($"[dim]{fid}[/]") { IsSelectable = false });

                // 深链：finding 涉及实体 → 可解析的第一个实体加 🔗 跳转
                if (TryResolveEntityType(f.InvolvedElementIds, out var type, out var entityId))
                {
                    var link = new Button(new Markup("[dim]🔗[/]")).Style(
                        ThemeStyles.BuildButton(_colors)
                    );
                    link.Click(() => OnEntityClicked?.Invoke(type, entityId));
                    actions.Add(link);
                    actions.Add(new Markup($"[dim]跳转[/] {entityId}") { IsSelectable = false });
                }

                children.Add(actions);
            }
        }
        if (details.OutlineSummary.Count > 0)
        {
            children.Add(new Markup("[bold]大纲与方向[/]") { IsSelectable = false });
            // 云端返回的摘要行按数据处理（含用户小说名/文本）：必须转义
            foreach (var line in details.OutlineSummary)
                children.Add(
                    new Markup($"  {ToolCallRenderer.Escape(line)}") { IsSelectable = false }
                );
        }
        if (details.Graph is { } graph)
            children.Add(
                new Markup($"[dim]设定图:[/] {ToolCallRenderer.Escape(graph.Describe())}")
                {
                    IsSelectable = false,
                }.Wrap(true)
            );
        if (details.Volumes.Count > 0)
        {
            children.Add(new Markup("[bold]分卷大纲[/]") { IsSelectable = false });
            foreach (var volume in details.Volumes)
                children.Add(
                    new Markup($"  {ToolCallRenderer.Escape(volume.Describe())}")
                    {
                        IsSelectable = false,
                    }.Wrap(true)
                );
        }
        if (details.CoverageSummary.Count > 0)
            children.Add(
                new Markup(
                    $"[dim]锚点覆盖:[/] {ToolCallRenderer.Escape(string.Join(" · ", details.CoverageSummary))}"
                )
                {
                    IsSelectable = false,
                }
            );

        return children;
    }

    /// <summary>把 finding 涉及实体 ID 解析为已知实体类型（命中已加载 ID 表的第一个）。</summary>
    private bool TryResolveEntityType(
        IReadOnlyList<string>? ids,
        out EntityType type,
        out string entityId
    )
    {
        type = default;
        entityId = "";
        if (ids is null || ids.Count == 0)
            return false;

        bool TryMatch(IEnumerable<string> source, EntityType t, out string found)
        {
            found = source.FirstOrDefault(ids.Contains) ?? "";
            return found.Length > 0;
        }

        if (TryMatch(_characterIds, EntityType.Character, out var id))
        {
            type = EntityType.Character;
            entityId = id;
            return true;
        }
        if (TryMatch(_plotlineIds, EntityType.Plotline, out id))
        {
            type = EntityType.Plotline;
            entityId = id;
            return true;
        }
        if (TryMatch(_sceneIds, EntityType.Scene, out id))
        {
            type = EntityType.Scene;
            entityId = id;
            return true;
        }
        if (TryMatch(_locationIds, EntityType.Location, out id))
        {
            type = EntityType.Location;
            entityId = id;
            return true;
        }
        if (TryMatch(_itemIds, EntityType.Item, out id))
        {
            type = EntityType.Item;
            entityId = id;
            return true;
        }
        if (TryMatch(_factionIds, EntityType.Faction, out id))
        {
            type = EntityType.Faction;
            entityId = id;
            return true;
        }
        return false;
    }

    /// <summary>查找给定 Accordion 的第一个展开的实体 ID。</summary>
    private static string? FindExpandedId(Accordion acc, List<string> ids)
    {
        for (var i = 0; i < ids.Count && i < acc.Children.Count; i++)
        {
            if (acc.Children[i] is Collapsible { IsExpanded: true })
                return ids[i];
        }
        return null;
    }

    /// <summary>
    /// 构建带有实体名称和紧凑操作标签的可折叠面板。
    /// 布局: [▸ icon name ······················ ⋯]
    ///         [  detail text (full width)  ]
    /// 头部用 Grid(Auto, Star, Auto)：名字靠左、操作标签靠右对齐，所有行统一；
    /// Collapsible 必须 HorizontalAlignment = Stretch 才能占满整行。
    /// 行右侧只有一个 ⋯（动作进 popup）：行上不堆图标，目标仍然明确。
    /// 使用 Markup（而非 Button）+ AccordionActionPanel 拦截器来绕过
    /// Collapsible 的 Preview 阶段指针捕获和 HasFocusWithin 跟踪崩溃。
    /// </summary>
    private Collapsible MakeEntityCollapsible(
        Visual nameMarkup,
        string detailMarkup,
        EntityType entityType,
        string entityId,
        string entityName,
        bool isExpanded
    )
    {
        // 数据自带字形（关系名里的 ⇔ 等）：收集未分类的非 ASCII 字形，
        // 下次启动（UI 接管屏幕前）自动实测格宽并加宽 —— 宽字形记账不一致会让整行错位、
        // 把后面的宽字形挤掉（表现为一个汉字宽的黑块，且增量重绘永远补不回来）。
        Services.TerminalWidthProbe.NoteText(entityName);
        Services.TerminalWidthProbe.NoteText(detailMarkup);
        var editAction = entityType switch
        {
            EntityType.Character => OnEditCharacter,
            EntityType.Faction => OnEditFaction,
            EntityType.Location => OnEditLocation,
            EntityType.Plotline => OnEditPlotline,
            EntityType.Scene => OnEditScene,
            EntityType.Item => OnEditItem,
            EntityType.Relationship => OnEditRelationship,
            _ => null,
        };
        // 行内删除：目标就是鼠标这一行（旧的工具栏「❌ 删除」作用于「当前展开项」，
        // 看不见对象、未展开时还静默无效——用户报障点之一）
        var delAction = entityType switch
        {
            EntityType.Character => OnDeleteCharacter,
            EntityType.Faction => OnDeleteFaction,
            EntityType.Location => OnDeleteLocation,
            EntityType.Plotline => OnDeletePlotline,
            EntityType.Scene => OnDeleteScene,
            EntityType.Item => OnDeleteItem,
            EntityType.Relationship => OnDeleteRelationship,
            _ => null,
        };

        // 行右侧只留一个 ⋯：查看/编辑 · 插入引用 · 历史 · 删除全在 popup 里
        // （行名本身仍是「点击插入引用」的主入口）。
        // 画成按钮形状而不是裸文字（用户报障：实体行的 ⋯ 不像大纲行那样是个按钮）——
        // 用与大纲 MenuButton 相同的 BuildButton 配色（Border 底 + SidePanelFg 字 + 左右各 1 格留白）。
        // 这里不能用真 Button：Collapsible 在 Preview 阶段吞掉头部点击，真按钮点不动
        // （见 AccordionActionPanel 的注释），所以保持 Markup + _headerActions 拦截，只把外观对齐。
        var menuLabel = new Markup($"[{Hex(_colors.SidePanelFg)} on {Hex(_colors.Border)}] ⋯ [/]")
        {
            IsSelectable = false,
        };
        _headerActions.Add(
            menuLabel,
            () =>
                ShowActionMenu(
                    menuLabel,
                    [
                        ("📄 查看 / 编辑", () => editAction?.Invoke(CurrentNovelId, entityId)),
                        ("📋 插入引用", () => OnEntityClicked?.Invoke(entityType, entityName)),
                        ("📜 历史", () => OnViewAuditLog?.Invoke(entityType, entityId)),
                        ("❌ 删除", () => delAction?.Invoke(CurrentNovelId, entityId)),
                    ]
                )
        );

        // 头部 Grid：名字(Auto) | 弹性占位(Star) | 操作标签(Auto)——
        // 操作标签统一推到最右侧，长名/短名各行对齐一致。
        var header = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star() },
                new ColumnDefinition { Width = GridLength.Auto }
            )
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .ColumnGap(1)
            .Cell(nameMarkup, 0, 0)
            .Cell(menuLabel, 0, 2);
        var parts = new List<Visual>
        {
            new Markup(AppendEntityLogDetail(detailMarkup, entityType, entityId))
            {
                IsSelectable = false,
            }.Wrap(true),
        };
        parts.AddRange(BuildRelationLines(entityType, entityId));
        var content = new ScrollViewer { Content = new VStack([.. parts]) }
            .MinHeight(12)
            .HorizontalScrollEnabled(false);
        // 详情区滚动条同样用面板底色（默认样式只有前景色 → 背景直通终端默认底=黑块）
        content.SetStyle(BuildListScrollStyle());

        return new Collapsible(header, content)
        {
            IsExpanded = isExpanded,
            HorizontalAlignment = Align.Stretch,
        };
    }

    /// <summary>
    /// 追加实体历史段到详情：① 出场摘要（<see cref="EntityTimelineView"/>：出场章序，非状态变化，
    /// 与本体时间线不重复）；② 最近 <see cref="MaxEntityLogLines"/> 条本体时间线
    /// （状态变化 + 本体动作日志 + 旧审计只读合并）。
    ///
    /// 图标语义：Create ➕ / Update ✏ / Delete 🗑 / Revert ↩；⏳ 待发布、🔗 联动、🔒 秘密/私密。
    /// 两层数据都缺失时返回原详情不变（不占 UI）。
    /// </summary>
    private string AppendEntityLogDetail(
        string detailMarkup,
        EntityType entityType,
        string entityId
    )
    {
        var lines = new List<string>();

        // 出场摘要：只给「出场过」的实体，一行章序 + 次数（旧演化轨迹的出场信息保留在这里）
        if (_timeline is not null && _chapterOrderMap.Count > 0)
        {
            var appearances = _timeline.GetAppearancesAcrossChapters(
                entityType,
                entityId,
                _chapterOrderMap
            );
            if (appearances.Count > 0)
            {
                var chapterLabels = appearances
                    .Select(a => a.ChapterOrder)
                    .Distinct()
                    .Select(order => $"第{order}章");
                lines.Add(
                    $"[dim]🎬 出场: {ToolCallRenderer.Escape(string.Join("、", chapterLabels))}"
                        + $"（{appearances.Count} 次）[/]"
                );
            }
        }

        if (_entityLogs?.Get(entityType, entityId) is { Count: > 0 } entries)
        {
            lines.Add("[dim]── 本体时间线 ──[/]");
            foreach (var entry in entries.TakeLast(MaxEntityLogLines))
                lines.Add(FormatEntityLogLine(entry));
        }

        if (lines.Count == 0)
            return detailMarkup;

        var section = string.Join("\n", lines);
        return string.IsNullOrEmpty(detailMarkup) ? section : $"{detailMarkup}\n\n{section}";
    }

    /// <summary>实体详情区时间线单行（图标 + 章号 + 概要 + 地点/情节线 + 状态标记）。</summary>
    internal static string FormatEntityLogLine(EntityLogEntry entry)
    {
        var icon = entry.Operation switch
        {
            OntologyOperation.Create => "➕",
            OntologyOperation.Delete => "🗑",
            OntologyOperation.Revert => "↩",
            _ => "✏",
        };
        var marks = new System.Text.StringBuilder();
        if (entry.Lifecycle == ChangeLifecycle.Pending)
            marks.Append("⏳");
        if (entry.IsCascade)
            marks.Append("🔗");
        if (entry.Visibility != ChangeVisibility.Public)
            marks.Append("🔒");

        var summary = string.IsNullOrWhiteSpace(entry.Summary)
            ? entry.Field is null
                ? "—"
                : $"{entry.Field}: {entry.From ?? ""} → {entry.To ?? ""}"
            : entry.Summary;

        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.LocationName))
            extras.Add($"📍{entry.LocationName}");
        if (entry.PlotlineNames.Count > 0)
            extras.Add($"情节线 {string.Join("、", entry.PlotlineNames)}");

        var where = entry.ChapterOrder > 0 ? $"第{entry.ChapterOrder}章" : "设定";
        var tail =
            extras.Count == 0
                ? ""
                : $" [dim]· {ToolCallRenderer.Escape(string.Join(" · ", extras))}[/]";
        return $"{icon}{marks} [dim]{where}[/] {ToolCallRenderer.Escape(summary)}{tail}";
    }

    /// <summary>实体详情时间线段最多渲染的行数（最近 N 条）。</summary>
    private const int MaxEntityLogLines = 10;

    /// <summary>获取指定实体类型的当前展开 ID 和 CRUD 事件委托。</summary>
    private (
        string? expandedId,
        Action<string?, string?>? add,
        Action<string?, string?>? edit,
        Action<string?, string?>? del
    ) GetDelegates(EntityType entityType) =>
        entityType switch
        {
            EntityType.Character => (
                FindExpandedId(_characterAccordion, _characterIds),
                OnAddCharacter,
                OnEditCharacter,
                OnDeleteCharacter
            ),
            EntityType.Faction => (
                FindExpandedId(_factionAccordion, _factionIds),
                OnAddFaction,
                OnEditFaction,
                OnDeleteFaction
            ),
            EntityType.Location => (
                FindExpandedId(_locationAccordion, _locationIds),
                OnAddLocation,
                OnEditLocation,
                OnDeleteLocation
            ),
            EntityType.Plotline => (
                FindExpandedId(_plotlineAccordion, _plotlineIds),
                OnAddPlotline,
                OnEditPlotline,
                OnDeletePlotline
            ),
            EntityType.Scene => (
                FindExpandedId(_sceneAccordion, _sceneIds),
                OnAddScene,
                OnEditScene,
                OnDeleteScene
            ),
            EntityType.Item => (
                FindExpandedId(_itemAccordion, _itemIds),
                OnAddItem,
                OnEditItem,
                OnDeleteItem
            ),
            EntityType.Relationship => (
                FindExpandedId(_relationshipAccordion, _relationshipIds),
                OnAddRelationship,
                OnEditRelationship,
                OnDeleteRelationship
            ),
            _ => (null, null, null, null),
        };

    /// <summary>
    /// 刷新面板内容：从 Store 加载所有实体，重建 Accordion 子项。
    /// 刷新前保存展开状态，刷新后恢复；高亮实体在刷新后清除标记。
    /// </summary>
    /// <param name="novelId">小说 ID。</param>
    /// <param name="reload">
    /// true（默认）= 重新读存储；false = 复用上次载入的缓存数据（搜索框变更时零 I/O 重建列表）。
    /// </param>
    public async Task RefreshAsync(string novelId, bool reload = true)
    {
        if (string.IsNullOrEmpty(novelId))
            return;
        _lastRefreshNovelId = novelId;
        // Save expanded state before clearing
        _expandedCharacterId =
            FindExpandedId(_characterAccordion, _characterIds) ?? _expandedCharacterId;
        _expandedFactionId = FindExpandedId(_factionAccordion, _factionIds) ?? _expandedFactionId;
        _expandedLocationId =
            FindExpandedId(_locationAccordion, _locationIds) ?? _expandedLocationId;
        _expandedPlotlineId =
            FindExpandedId(_plotlineAccordion, _plotlineIds) ?? _expandedPlotlineId;
        _expandedSceneId = FindExpandedId(_sceneAccordion, _sceneIds) ?? _expandedSceneId;
        _expandedItemId = FindExpandedId(_itemAccordion, _itemIds) ?? _expandedItemId;
        _expandedRelationshipId =
            FindExpandedId(_relationshipAccordion, _relationshipIds) ?? _expandedRelationshipId;
        try
        {
            // 搜索过滤在行数上限裁剪之前应用（否则第 61 条以后搜不到）。
            var characters = reload ? await _store.GetCharacters(novelId) : _cachedCharacters;
            _cachedCharacters = characters;
            characters = FilterBySearch(characters, c => $"{c.Name} {c.Personality}");

            // ── 结构索引：总纲 + 卷纲 + 章节细纲元数据（不含正文），一次读取，缓存按目录戳失效 ──
            // reload:false（搜索重建）复用缓存：不重读存储，也不重建时间线 / 关系索引。
            var structure = _structure;
            if (reload)
            {
                structure = await _store.GetStructure(novelId);
                _structure = structure;
                _novel = structure?.Novel;
                _currentCompletionStatus = structure?.Novel.CompletionStatus ?? "ongoing";
                _volumes.Clear();
                if (structure is not null)
                    _volumes.AddRange(structure.Volumes);

                // 出场摘要数据源（章序映射 + as-of 视图；失败静默降级为空）
                _chapterOrderMap.Clear();
                foreach (var c in structure?.Chapters ?? [])
                    _chapterOrderMap[c.Id] = c.Order;
                try
                {
                    _timeline = await EntityTimelineView.BuildAsync(_store, novelId);
                }
                catch
                {
                    _timeline = null;
                }

                // 本体时间线（实体详情区历史数据源：状态变化 + 本体动作日志 + 旧审计只读合并）
                try
                {
                    _entityLogs = await EntityLogView.BuildAsync(_store, novelId);
                }
                catch
                {
                    _entityLogs = null;
                }

                // 本体关系索引（全实体结构关系；失败置 null → 关系段不渲染）
                try
                {
                    _relations = await EntityRelationIndex.BuildAsync(_store, novelId);
                }
                catch
                {
                    _relations = null;
                }
            }

            // 地点名录先于角色加载：角色卡片要显示「当前地点」名称（Faction 总部也要用）
            var locations = reload ? await _store.GetLocations(novelId) : _cachedLocations;
            _cachedLocations = locations;
            var locNameMap = locations.ToDictionary(l => l.Id, l => l.Name);

            _characterAccordion.Children.Clear();
            _characterIds.Clear();
            _characterNames.Clear();
            var (visiblecharacters, morecharacters) = CapRows(
                EntityType.Character,
                FilterBySearch(characters, c => $"{c.Name} {c.Personality}"),
                c => c.Id,
                c => (c.Role.ToString(), GenderLabel(c.Gender))
            );
            foreach (var ch in visiblecharacters)
            {
                var isHighlighted =
                    ch.Id == _highlightEntityId && _highlightEntityType == EntityType.Character;

                // 角色图标：定位 + 性别
                var roleIcon = ch.Role switch
                {
                    CharacterRole.Protagonist => "⭐",
                    CharacterRole.Supporting => "🎭",
                    CharacterRole.Minor => "👤",
                    CharacterRole.Antagonist => "👹",
                    _ => "👤",
                };
                // 性别图标：Gender 是跨仓线协议值，历史数据两种格式并存——对话框写的英文枚举
                // （"Male"/"Female"）与种子数据/早期版本的中文（"男"/"女"）。
                // 只认中文会让改过性别的角色图标消失（用户报障），先归一再取图标。
                var genderTag = GenderLabel(ch.Gender) switch
                {
                    "男" => "♂",
                    "女" => "♀",
                    _ => "",
                };
                var nameSuffix = string.IsNullOrEmpty(genderTag) ? "" : $" {genderTag}";

                var nameMarkup = isHighlighted
                    ? new Markup(
                        $"{roleIcon} [bold yellow]✨ {ToolCallRenderer.Escape(ch.Name)}[/]{nameSuffix}"
                    )
                    : new Markup(
                        $"{roleIcon} [bold]{ToolCallRenderer.Escape(ch.Name)}[/]{nameSuffix}"
                    );
                var isExpanded = isHighlighted || ch.Id == _expandedCharacterId;
                _characterAccordion.Children.Add(
                    MakeEntityCollapsible(
                        nameMarkup,
                        BuildCharacterDetail(ch, locNameMap),
                        EntityType.Character,
                        ch.Id,
                        ch.Name,
                        isExpanded
                    )
                );
                _characterIds.Add(ch.Id);
                _characterNames[ch.Id] = ch.Name;
            }

            if (morecharacters is not null)
                _characterAccordion.Children.Add(morecharacters);
            var charNameMap = characters.ToDictionary(c => c.Id, c => c.Name);
            if (visiblecharacters.Count == 0)
                _characterAccordion.Children.Add(
                    new Collapsible(new Markup(EmptyListMarkup("角色")), new Markup(""))
                    {
                        IsExpanded = false,
                        HorizontalAlignment = Align.Stretch,
                    }
                );

            var factions = reload ? await _store.GetFactions(novelId) : _cachedFactions;
            _cachedFactions = factions;
            var factionRels = reload
                ? await _store.GetFactionRelationships(novelId)
                : _cachedFactionRels;
            _cachedFactionRels = factionRels;
            var factionNameMap = factions.ToDictionary(f => f.Id, f => f.Name);

            _factionAccordion.Children.Clear();
            _factionIds.Clear();
            _factionNames.Clear();
            var (visiblefactions, morefactions) = CapRows(
                EntityType.Faction,
                FilterBySearch(factions, f => f.Name),
                f => f.Id,
                f => (f.Type.ToString(), null)
            );
            foreach (var f in visiblefactions)
            {
                var isHighlighted =
                    f.Id == _highlightEntityId && _highlightEntityType == EntityType.Faction;
                var nameMarkup = isHighlighted
                    ? new Markup($"🏰 [bold yellow]✨ {ToolCallRenderer.Escape(f.Name)}[/]")
                    : new Markup($"🏰 [bold]{ToolCallRenderer.Escape(f.Name)}[/]");
                var leaderName = charNameMap.GetValueOrDefault(f.Leader, f.Leader);
                var isExpanded = isHighlighted || f.Id == _expandedFactionId;
                _factionAccordion.Children.Add(
                    MakeEntityCollapsible(
                        nameMarkup,
                        BuildFactionDetailWithName(
                            f,
                            leaderName,
                            charNameMap,
                            factionRels,
                            factionNameMap,
                            locNameMap
                        ),
                        EntityType.Faction,
                        f.Id,
                        f.Name,
                        isExpanded
                    )
                );
                _factionIds.Add(f.Id);
                _factionNames[f.Id] = f.Name;
            }
            if (morefactions is not null)
                _factionAccordion.Children.Add(morefactions);
            if (visiblefactions.Count == 0)
                _factionAccordion.Children.Add(
                    new Collapsible(new Markup(EmptyListMarkup("组织")), new Markup(""))
                    {
                        IsExpanded = false,
                        HorizontalAlignment = Align.Stretch,
                    }
                );

            _locationAccordion.Children.Clear();
            _locationIds.Clear();
            _locationNames.Clear();
            var (visiblelocations, morelocations) = CapRows(
                EntityType.Location,
                FilterBySearch(locations, l => l.Name),
                l => l.Id,
                l => (l.Type.ToString(), null)
            );
            foreach (var l in visiblelocations)
            {
                var isHighlighted =
                    l.Id == _highlightEntityId && _highlightEntityType == EntityType.Location;
                var nameMarkup = isHighlighted
                    ? new Markup($"📍 [bold yellow]✨ {ToolCallRenderer.Escape(l.Name)}[/]")
                    : new Markup($"📍 [bold]{ToolCallRenderer.Escape(l.Name)}[/]");
                var isExpanded = isHighlighted || l.Id == _expandedLocationId;
                _locationAccordion.Children.Add(
                    MakeEntityCollapsible(
                        nameMarkup,
                        BuildLocationDetailWithName(l, locations),
                        EntityType.Location,
                        l.Id,
                        l.Name,
                        isExpanded
                    )
                );
                _locationIds.Add(l.Id);
                _locationNames[l.Id] = l.Name;
            }
            if (morelocations is not null)
                _locationAccordion.Children.Add(morelocations);
            if (visiblelocations.Count == 0)
                _locationAccordion.Children.Add(
                    new Collapsible(new Markup(EmptyListMarkup("地点")), new Markup(""))
                    {
                        IsExpanded = false,
                        HorizontalAlignment = Align.Stretch,
                    }
                );

            var plotlines = reload ? await _store.GetPlotlines(novelId) : _cachedPlotlines;
            _cachedPlotlines = plotlines;
            var scenes = reload ? await _store.GetScenesByNovel(novelId) : _cachedScenes;
            _cachedScenes = scenes;
            var chaptersForPlotline = reload
                ? await _store.GetChapters(novelId)
                : _cachedChaptersForPlotline;
            _cachedChaptersForPlotline = chaptersForPlotline;
            _plotlineAccordion.Children.Clear();
            _plotlineIds.Clear();
            _plotlineNames.Clear();
            var (visibleplotlines, moreplotlines) = CapRows(
                EntityType.Plotline,
                FilterBySearch(plotlines, p => $"{p.Name} {p.Description}"),
                p => p.Id,
                p => (p.Type.ToString(), p.Tension.ToString())
            );
            foreach (var p in visibleplotlines)
            {
                var icon = p.Type switch
                {
                    PlotlineType.Main => "📜",
                    PlotlineType.Side => "🔄",
                    PlotlineType.Hidden => "❓",
                    PlotlineType.Foreshadowing => "⚡",
                    _ => "📜",
                };
                var resolved = p.ResolvedAtChapterId != null ? " ✅" : "";
                var isHighlighted =
                    p.Id == _highlightEntityId && _highlightEntityType == EntityType.Plotline;
                var nameMarkup = isHighlighted
                    ? new Markup(
                        $"{icon} [bold yellow]✨ {ToolCallRenderer.Escape(p.Name)}[/]{resolved}"
                    )
                    : new Markup($"{icon} [bold]{ToolCallRenderer.Escape(p.Name)}[/]{resolved}");
                var isExpanded = isHighlighted || p.Id == _expandedPlotlineId;
                _plotlineAccordion.Children.Add(
                    MakeEntityCollapsible(
                        nameMarkup,
                        BuildPlotlineDetail(p, scenes, chaptersForPlotline),
                        EntityType.Plotline,
                        p.Id,
                        p.Name,
                        isExpanded
                    )
                );
                _plotlineIds.Add(p.Id);
                _plotlineNames[p.Id] = p.Name;
            }
            if (moreplotlines is not null)
                _plotlineAccordion.Children.Add(moreplotlines);
            if (visibleplotlines.Count == 0)
                _plotlineAccordion.Children.Add(
                    new Collapsible(new Markup(EmptyListMarkup("情节线")), new Markup(""))
                    {
                        IsExpanded = false,
                        HorizontalAlignment = Align.Stretch,
                    }
                );
            var plotlineNameMap = plotlines.ToDictionary(p => p.Id, p => p.Name);

            // scenes already loaded above for plotline rendering
            var chapters = (structure?.Chapters ?? []).ToList();
            var chapterMap = chapters.ToDictionary(c => c.Id, c => c.Title);
            _sceneAccordion.Children.Clear();
            _sceneIds.Clear();
            _sceneNames.Clear();
            var (visiblescenes, morescenes) = CapRows(
                EntityType.Scene,
                FilterBySearch(scenes, s => s.Title),
                s => s.Id,
                s => (SceneVolumeId(s), null)
            );
            foreach (var s in visiblescenes)
            {
                var isHighlighted =
                    s.Id == _highlightEntityId && _highlightEntityType == EntityType.Scene;
                var typeIcon = s.SceneType switch
                {
                    SceneType.Action => "💥",
                    SceneType.Dialogue => "💬",
                    SceneType.Reflection => "💭",
                    SceneType.Transition => "🔄",
                    _ => "🎬",
                };
                var nameMarkup = isHighlighted
                    ? new Markup($"{typeIcon} [bold yellow]✨ {ToolCallRenderer.Escape(s.Title)}[/]")
                    : new Markup($"{typeIcon} [bold]{ToolCallRenderer.Escape(s.Title)}[/]");
                var isExpanded = isHighlighted || s.Id == _expandedSceneId;
                _sceneAccordion.Children.Add(
                    MakeEntityCollapsible(
                        nameMarkup,
                        BuildSceneDetail(s, charNameMap, chapterMap, locations, plotlineNameMap),
                        EntityType.Scene,
                        s.Id,
                        s.Title,
                        isExpanded
                    )
                );
                _sceneIds.Add(s.Id);
                _sceneNames[s.Id] = s.Title;
            }
            if (morescenes is not null)
                _sceneAccordion.Children.Add(morescenes);
            if (visiblescenes.Count == 0)
                _sceneAccordion.Children.Add(
                    new Collapsible(new Markup(EmptyListMarkup("场景")), new Markup(""))
                    {
                        IsExpanded = false,
                        HorizontalAlignment = Align.Stretch,
                    }
                );

            var items = reload ? await _store.GetItems(novelId) : _cachedItems;
            _cachedItems = items;
            _itemAccordion.Children.Clear();
            _itemIds.Clear();
            _itemNames.Clear();
            var (visibleitems, moreitems) = CapRows(
                EntityType.Item,
                FilterBySearch(items, i => i.Name),
                i => i.Id,
                i => (i.Type.ToString(), null)
            );
            foreach (var it in visibleitems)
            {
                var isHighlighted =
                    it.Id == _highlightEntityId && _highlightEntityType == EntityType.Item;
                var nameMarkup = isHighlighted
                    ? new Markup($"🎒 [bold yellow]✨ {ToolCallRenderer.Escape(it.Name)}[/]")
                    : new Markup($"🎒 [bold]{ToolCallRenderer.Escape(it.Name)}[/]");
                var ownerName = charNameMap.GetValueOrDefault(it.OwnerId ?? "", it.OwnerId ?? "");
                var ownerStatus =
                    it.OwnerId is not null && charNameMap.ContainsKey(it.OwnerId)
                        ? characters.FirstOrDefault(c => c.Id == it.OwnerId)?.Status
                            ?? CharacterStatus.Active
                        : (CharacterStatus?)null;
                var isExpanded = isHighlighted || it.Id == _expandedItemId;
                _itemAccordion.Children.Add(
                    MakeEntityCollapsible(
                        nameMarkup,
                        BuildItemDetailWithName(it, ownerName, ownerStatus),
                        EntityType.Item,
                        it.Id,
                        it.Name,
                        isExpanded
                    )
                );
                _itemIds.Add(it.Id);
                _itemNames[it.Id] = it.Name;
            }
            if (moreitems is not null)
                _itemAccordion.Children.Add(moreitems);
            if (visibleitems.Count == 0)
                _itemAccordion.Children.Add(
                    new Collapsible(new Markup(EmptyListMarkup("道具")), new Markup(""))
                    {
                        IsExpanded = false,
                        HorizontalAlignment = Align.Stretch,
                    }
                );

            var relationships = reload
                ? await _store.GetRelationshipsByNovel(novelId)
                : _cachedRelationships;
            _cachedRelationships = relationships;
            _relationshipAccordion.Children.Clear();
            _relationshipIds.Clear();
            _relationshipNames.Clear();
            var (visiblerelationships, morerelationships) = CapRows(
                EntityType.Relationship,
                FilterBySearch(
                    relationships,
                    r =>
                        $"{charNameMap.GetValueOrDefault(r.CharacterAId, r.CharacterAId)} "
                        + $"{charNameMap.GetValueOrDefault(r.CharacterBId, r.CharacterBId)}"
                ),
                r => r.Id,
                r => (r.Type.ToString(), r.Strength.ToString())
            );
            foreach (var r in visiblerelationships)
            {
                var typeIcon = r.Type switch
                {
                    RelationshipType.Family => "👪",
                    RelationshipType.Love => "💕",
                    RelationshipType.Friend => "🤝",
                    RelationshipType.Rival => "⚡",
                    RelationshipType.Enemy => "💥",
                    RelationshipType.Mentor => "📚",
                    RelationshipType.MasterServant => "🙇",
                    RelationshipType.FellowDisciple => "👥",
                    RelationshipType.ParentOf => "👪",
                    RelationshipType.ChildOf => "👶",
                    RelationshipType.Sibling => "👫",
                    RelationshipType.Spouse => "💍",
                    RelationshipType.Sworn => "🪢",
                    RelationshipType.Disciple => "🎓",
                    RelationshipType.Ally => "🤝",
                    _ => "🔗",
                };
                var nameA = charNameMap.GetValueOrDefault(r.CharacterAId, r.CharacterAId);
                var nameB = charNameMap.GetValueOrDefault(r.CharacterBId, r.CharacterBId);
                var isHighlighted =
                    r.Id == _highlightEntityId && _highlightEntityType == EntityType.Relationship;
                var nameMarkup = isHighlighted
                    ? new Markup(
                        $"{typeIcon} [bold yellow]✨ {ToolCallRenderer.Escape(nameA)} ↔ {ToolCallRenderer.Escape(nameB)}[/]"
                    )
                    : new Markup(
                        $"{typeIcon} [bold]{ToolCallRenderer.Escape(nameA)} ↔ {ToolCallRenderer.Escape(nameB)}[/]"
                    );
                var isExpanded = isHighlighted || r.Id == _expandedRelationshipId;
                _relationshipAccordion.Children.Add(
                    MakeEntityCollapsible(
                        nameMarkup,
                        BuildRelationshipDetail(r),
                        EntityType.Relationship,
                        r.Id,
                        $"{nameA}↔{nameB}",
                        isExpanded
                    )
                );
                _relationshipIds.Add(r.Id);
                _relationshipNames[r.Id] = $"{nameA}↔{nameB}";
            }
            if (morerelationships is not null)
                _relationshipAccordion.Children.Add(morerelationships);
            if (visiblerelationships.Count == 0)
                _relationshipAccordion.Children.Add(
                    new Collapsible(new Markup(EmptyListMarkup("关系")), new Markup(""))
                    {
                        IsExpanded = false,
                        HorizontalAlignment = Align.Stretch,
                    }
                );

            // ── Outline: 三层结构（小说 → 分卷 → 章节） ──
            // 刷新期数据缓存（钻取重排只重建视觉树，不重读存储）
            _chapters.Clear();
            _chapters.AddRange(chapters.OrderBy(c => c.Order));
            _chapterVolumeLookup = null; // 章节集变了 → 场景筛选的「章节→卷」查表失效重建
            _chapterNames.Clear();
            foreach (var ch in _chapters)
                _chapterNames[ch.Id] = $"{ch.Order}. {ch.Title}";

            // 高亮章节 → 自动展开其所在卷/未分卷组，保证目标可见（AI 工具跳转场景）
            if (
                _highlightEntityType == EntityType.Chapter
                && _highlightEntityId is { } highlightedId
            )
                NavigateToEntity(EntityType.Chapter, highlightedId);

            RenderOutline();

            // Clear highlight after refresh
            _highlightEntityId = null;
            _highlightEntityType = null;
        }
        catch { }
    }

    // ── 章节详情构建（细纲 + 正文记录） ──

    /// <summary>变化面枚举 → 图标（entity-evolution-lifecycle 演化轨迹图标体系）。</summary>
    private static string AspectIcon(EvolutionAspect aspect) =>
        aspect switch
        {
            EvolutionAspect.Status => "🩸",
            EvolutionAspect.Personality => "🧠",
            EvolutionAspect.Motivation => "🎯",
            EvolutionAspect.Relationship => "🔗",
            EvolutionAspect.Ability => "⚡",
            EvolutionAspect.Possession => "🎒",
            EvolutionAspect.Location => "📍",
            EvolutionAspect.Knowledge => "💡",
            EvolutionAspect.Behavior => "🔄",
            _ => "📌",
        };

    /// <summary>实体显示名（关系索引解析；索引不可用时原样返回 id）。</summary>
    private string NameOf(EntityType type, string id) => _relations?.NameOf(type, id) ?? id;

    /// <summary>
    /// 本体关系段（全实体通用）：按视角侧标签渲染，行可点击导航到目标实体。
    /// 无关系或索引不可用时返回空列表（调用方据此不渲染该段）；
    /// 超过 <see cref="MaxRelationLines"/> 条时截断并提示剩余数量（防大实体关系溢出）。
    /// </summary>
    private List<Visual> BuildRelationLines(EntityType entityType, string entityId)
    {
        var lines = new List<Visual>();
        if (_relations is null)
            return lines;

        var relations = _relations.GetRelations(entityType, entityId);
        if (relations.Count == 0)
            return lines;

        lines.Add(new Markup("[dim]── 本体关系 ──[/]") { IsSelectable = false });
        foreach (var relation in relations.Take(MaxRelationLines))
        {
            var isSource = relation.SourceType == entityType && relation.SourceId == entityId;
            var (targetType, targetId) = isSource
                ? (relation.TargetType, relation.TargetId)
                : (relation.SourceType, relation.SourceId);
            var label = EntityRelationIndex.LabelFor(relation, entityType, entityId);
            var name = NameOf(targetType, targetId);
            // 关系标签/对端实体名/详情都来自小说数据 → 转义后再进 Markup
            var safeLabel = ToolCallRenderer.Escape(label);
            var safeName = ToolCallRenderer.Escape(name);
            var safeDetail = string.IsNullOrEmpty(relation.Detail)
                ? ""
                : ToolCallRenderer.Escape(relation.Detail);
            var text = string.IsNullOrEmpty(relation.Detail)
                ? $"{safeLabel}: {safeName}"
                : $"{safeLabel}: {safeName}（{safeDetail}）";

            var line = new Markup($"  {text}") { IsSelectable = false };
            _headerActions.Add(
                line,
                () =>
                {
                    NavigateToEntity(targetType, targetId);
                    _ = RefreshAsync(CurrentNovelId ?? "");
                }
            );
            lines.Add(line);
        }

        if (relations.Count > MaxRelationLines)
            lines.Add(
                new Markup($"[dim]  … 另有 {relations.Count - MaxRelationLines} 条关系[/]")
                {
                    IsSelectable = false,
                }
            );
        return lines;
    }

    /// <summary>关系段单实体最大行数（大实体如 60 人组织：截断 + 剩余提示，防撑爆折叠项）。</summary>
    private const int MaxRelationLines = 10;

    /// <summary>章节正文记录·单章出场实体名上限（大章数十人同场时截断）。</summary>
    private const int MaxChapterAppearanceNames = 12;

    /// <summary>章节正文记录·单章变化事件行上限。</summary>
    private const int MaxChapterChangeLines = 8;

    /// <summary>逐项列表渲染（每项一行；超过 max 行截断 + 剩余提示）。</summary>
    private static IEnumerable<string> CapLines(IEnumerable<string> lines, int max, string unit)
    {
        var list = lines.ToList();
        foreach (var line in list.Take(max))
            yield return line;
        if (list.Count > max)
            yield return $"[dim]  … 另有 {list.Count - max} {unit}[/]";
    }

    // ── 大纲树渲染（行模型 → DocumentFlow 项；两端共用 Core.Novels.OutlineRows） ──

    /// <summary>
    /// 行模型之外的刷新键：检查结果展开态 / 书级摘要 / 总纲摘要 / 检查结果。
    /// 行序列不变但任一变化（点开检查结果、检查回填、编辑小说信息）都必须重建——
    /// 旧指纹只含行序列 + 行内新建键，这是检查结果开关静默失效的直接成因。
    /// </summary>
    private string ExtraOutlineStateFingerprint()
    {
        var s = _bookSummary;
        var outline = _novel?.Outline;
        return "|sx:"
            + (_checkResultsExpanded ? "1" : "0")
            + "|bs:"
            + $"{s.Title}~{s.OutlineLine}~{s.VolumeCount}/{s.ChapterCount}/{s.DoneCount}/{s.TotalWords}"
            + "|no:"
            + $"{outline?.Length ?? 0}:{outline?.GetHashCode() ?? 0}"
            + "|ck:"
            + (
                _lastCheckResult is not { } d
                    ? "-"
                    : $"{d.Findings.Count}/{d.OutlineSummary.Count}/{d.CoverageSummary.Count}"
            );
    }

    /// <summary>切换设定检查结果分节。</summary>
    internal void ToggleSetupCheckResults()
    {
        _checkResultsExpanded = !_checkResultsExpanded;
        RenderOutline(ensureNovelRootExpanded: _checkResultsExpanded);
    }

    /// <summary>
    /// 展开设定检查结果分节（运行走分节内「▶ 运行」按钮）。
    /// 入口：大纲工具带的「🔍 检查」按钮（书/卷节点 ⋯ 菜单里的同项已按用户要求移除）。
    /// </summary>
    private void OpenSetupCheckResults()
    {
        _checkResultsExpanded = true;
        RenderOutline(ensureNovelRootExpanded: true);
    }

    /// <summary>
    /// 章行对应的章节发布状态（草稿/已发布）。
    /// <see cref="OutlineRow"/> 只携带状态图标与标签文案，状态枚举从刷新缓存查；
    /// 缓存缺失（如周期内被删除）时按标签文案回落，仍无则 null（不显示发布/撤回动作）。
    /// </summary>
    internal ChapterStatus? ChapterStatusOf(OutlineRow row)
    {
        if (row.Kind != OutlineRowKind.Chapter)
            return null;
        var chapter = _chapters.Find(c => string.Equals(c.Id, row.Id, StringComparison.Ordinal));
        if (chapter is not null)
            return chapter.Status;
        return row.StatusLabel switch
        {
            "发布" => ChapterStatus.Published,
            "草稿" => ChapterStatus.Draft,
            _ => null,
        };
    }

    /// <summary>行尾 ⋯ 菜单项（标签 + 动作）；结构操作全部收进溢出面。</summary>
    private List<(string Label, Action Action)> BuildOutlineRowMenuItems(OutlineRow row)
    {
        var items = new List<(string Label, Action Action)>();
        switch (row.Kind)
        {
            case OutlineRowKind.Novel:
            {
                // 书级行：＋ = 新增分卷；⋯ 只留 时间线 / 详情 / 删除（用户报障：菜单项过多）。
                // 总纲、世界规则、连载状态都收进详情对话框。
                items.Add(
                    (
                        "🧾 时间线",
                        () => OnViewAuditLog?.Invoke(EntityType.Novel, CurrentNovelId ?? "")
                    )
                );
                items.Add(("📄 详情", () => OnEditNovel?.Invoke(CurrentNovelId, CurrentNovelId)));
                items.Add(("❌ 删除", () => OnDeleteNovel?.Invoke(CurrentNovelId, CurrentNovelId)));
                break;
            }
            case OutlineRowKind.VolumeHeader:
            {
                // 卷行：＋ = 新增章节；⋯ 只留 时间线 / 详情 / 删除（重命名/上下移并进详情表单）。
                var volumeId = row.Id;
                items.Add(("🧾 时间线", () => OnViewAuditLog?.Invoke(EntityType.Volume, volumeId)));
                items.Add(("📄 详情", () => OnEditVolume?.Invoke(CurrentNovelId, volumeId)));
                items.Add(("❌ 删除", () => OnDeleteVolume?.Invoke(CurrentNovelId, volumeId)));
                break;
            }
            case OutlineRowKind.Chapter:
            {
                // 章行：⋯ 留 时间线 / 发布|撤回 / 详情 / 内容 / 删除
                // （标题/章节号/字数/更新时间/细纲并进详情对话框）。
                var chapterId = row.Id;
                items.Add(
                    ("🧾 时间线", () => OnViewAuditLog?.Invoke(EntityType.Chapter, chapterId))
                );
                // 状态动作只给与当前状态匹配的那一个：草稿可发布，已发布可撤回
                // （状态是发布事务的结果，只能经 ChapterLifecycleService 改，见宿主事件）。
                switch (ChapterStatusOf(row))
                {
                    case ChapterStatus.Draft:
                        items.Add(("📤 发布", () => OnPublishChapter?.Invoke(chapterId)));
                        break;
                    case ChapterStatus.Published:
                        items.Add(("↩ 撤回", () => OnUnpublishChapter?.Invoke(chapterId)));
                        break;
                }
                items.Add(("📄 详情", () => OnEditChapter?.Invoke(CurrentNovelId, chapterId)));
                items.Add(
                    ("📖 内容", () => OnEditChapterContent?.Invoke(CurrentNovelId, chapterId))
                );
                items.Add(("❌ 删除", () => OnDeleteChapter?.Invoke(CurrentNovelId, chapterId)));
                break;
            }
        }

        return items;
    }

    /// <summary>
    /// 列表行数上限裁剪：未点「显示全部」且超出上限时，返回前 <see cref="MaxAccordionRows"/> 条
    /// 与一个可点击的「还有 N 条（点击显示全部）」行（点击后重刷面板并保持显示全部）。
    /// </summary>
    private (List<T> Visible, Visual? MoreRow) CapRows<T>(
        EntityType type,
        List<T> items,
        Func<T, string> idOf,
        Func<T, (string Primary, string? Secondary)>? filterOf = null
    )
    {
        // 实体筛选条件与搜索同层：都在行数上限裁剪之前应用
        // （否则被筛掉的条目会先占满上限，第 61 条以后的命中项永远看不到）。
        if (
            filterOf is not null
            && _entityFilters.GetValueOrDefault(type) is { IsActive: true } filter
        )
            items =
            [
                .. items.Where(x =>
                {
                    var (primary, secondary) = filterOf(x);
                    return filter.Matches(primary, secondary);
                }),
            ];
        _entityFilteredCounts[type] = items.Count; // 占位文案「筛选中 N/M」的分子

        // 高亮实体落在截断尾巴里 → 自动解除上限（AI 跳转/关系导航必须可见）
        if (
            _highlightEntityType == type
            && _highlightEntityId is { } highlightId
            && items.FindIndex(x => idOf(x) == highlightId) >= MaxAccordionRows
        )
            _showAllLists.Add(type);

        if (_showAllLists.Contains(type) || items.Count <= MaxAccordionRows)
            return (items, null);

        var hidden = items.Count - MaxAccordionRows;
        var moreRow = new Markup($"[dim]  … 还有 {hidden} 条（点击显示全部）[/]");
        _headerActions.Add(
            moreRow,
            () =>
            {
                _showAllLists.Add(type);
                _ = RefreshAsync(CurrentNovelId ?? "");
            }
        );
        return ([.. items.Take(MaxAccordionRows)], moreRow);
    }

    // ── 模糊搜索（实体列表 / 大纲章节统一入口） ──

    /// <summary>搜索框文本变化 → 重建过滤结果（零 I/O；同一文本去重避免重复重建）。</summary>
    private void OnSearchTextChanged()
    {
        // 空框时不占位：清空键只在有内容时出现，搜索框始终吃满整行
        _clearSearchButton.IsVisible = !string.IsNullOrEmpty(_searchBox.Text);
        ApplySearchText(_searchBox.Text ?? "");
    }

    /// <summary>
    /// 应用搜索文本（internal 测试缝：无 app 的测试环境里绑定失效机制不运行，
    /// DynamicUpdate 回调不会被触发，测试直接调它驱动同一条路径）。
    /// </summary>
    internal void ApplySearchText(string text)
    {
        if (string.Equals(text, _searchText, StringComparison.Ordinal))
            return;
        _searchText = text;
        ApplySearch();
    }

    /// <summary>
    /// 应用搜索：大纲关键字（<see cref="ChapterListFilter"/>）+ 七类实体列表重建
    /// （<c>reload:false</c> 复用缓存数据，不重读存储）。
    /// 搜索框在内容区（tab 下面）——一个查询作用于当前 tab 的列表，并同步过滤其余 tab，
    /// 切换 tab 时看到的同样是过滤后的结果。
    /// </summary>
    private void ApplySearch()
    {
        _chapterFilter = _chapterFilter with
        {
            Keyword = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText.Trim(),
        };
        _ = RefreshAsync(_lastRefreshNovelId, reload: false);
    }

    /// <summary>搜索过滤（模糊子序列，见 <see cref="FuzzySearch"/>）；查询为空 = 原样返回。</summary>
    private List<T> FilterBySearch<T>(List<T> items, Func<T, string> searchTextOf) =>
        string.IsNullOrWhiteSpace(_searchText)
            ? items
            : [.. items.Where(x => FuzzySearch.Matches(searchTextOf(x), _searchText))];

    /// <summary>空列表文案：无数据 vs 搜索未命中（搜索态给出可理解的反馈）。</summary>
    private string EmptyListMarkup(string label) =>
        string.IsNullOrWhiteSpace(_searchText)
            ? $"[dim](无{label})[/]"
            : $"[dim](未命中「{ToolCallRenderer.Escape(_searchText.Trim())}」)[/]";

    /// <summary>搜索框样式（填充色 = 当前主题的 InputFill；构造与主题切换共用，避免两处漂移）。</summary>
    private TextBoxStyle BuildSearchBoxStyle() =>
        TextBoxStyle.Default with
        {
            Background = _colors.InputFill,
        };

    /// <summary>列表/详情滚动条样式（轨道与拇指 = 当前主题面板底色；构造与主题切换共用）。</summary>
    private ScrollViewerStyle BuildListScrollStyle() =>
        new()
        {
            ScrollBarThickness = 1,
            TrackStyle = Style
                .None.WithBackground(_colors.SidePanelBg)
                .WithForeground(_colors.SidePanelBg),
            ThumbStyle = Style
                .None.WithBackground(_colors.SidePanelBg)
                .WithForeground(_colors.SidePanelBg),
        };

    /// <summary>大纲树样式（树线随主题；构造与主题切换共用）。</summary>
    private TreeViewStyle BuildOutlineTreeStyle() =>
        new()
        {
            IndentSize = 2,
            // 图标只在这里给：IconResolver 按行类型返回层级图标（📖 小说 / 📦 卷 / 📄 未分卷 /
            // ⚪🟡🟢 章状态）——标题文本里不再拼 emoji，也就不会和框架默认图标叠成两个
            // （用户报障：「为啥有 2 个 emoji」）；也没了模型数据被当 markup 转义的麻烦。
            IconResolver = static (data, _) =>
                data is OutlineRow row ? OutlineIcon(row) : new Rune(' '),
            SpaceBetweenGlyphAndText = 1,
            HierarchyLineStyle = Style
                .None.WithForeground(_colors.SidePanelFg)
                .WithBackground(_colors.SidePanelBg),
        };

    /// <summary>应用主题颜色（运行时切换深/浅色主题时调用）。</summary>
    public void ApplyTheme(PanelColors colors)
    {
        _colors = colors; // 切换时更新当前色（后续所有构建/刷新用它）
        _panel.FillStyle = Style
            .None.WithBackground(_colors.SidePanelBg)
            .WithForeground(_colors.SidePanelFg);

        ApplySectionStyles(); // 分区按钮样式（选中 = 篝火底亮月光）
        RefreshTabContentBackgrounds(); // 同步刷新分区内容面板背景（构建时快照 → 当前主题）
        // 工具带行（搜索框 + ✕ 清空 + 上下文动作）同样是构建时快照，必须一起重设，
        // 否则切主题后只有输入框填充/按钮沿用旧主题色（用户报障：输入区域色块不随主题切换）。
        _searchBox.Style(BuildSearchBoxStyle());
        _clearSearchButton.Style(ThemeStyles.BuildButton(_colors));
        ApplySectionChrome(); // 上下文按钮（＋ 新建 / 🔍 筛选）样式 + 占位文案，幂等
        foreach (var button in _cloudButtons)
            button.Style(ThemeStyles.BuildButton(_colors));
        foreach (var scroll in _listScrolls)
            scroll.SetStyle(BuildListScrollStyle());
        // 大纲树：树线样式是构建时快照，行内容（⋯ 芯片 / ＋ 按钮 / 状态标签）也带颜色——
        // 重设样式并清掉行指纹，随后的 RefreshOutline（TuiApp 切主题本来就会调）才会真正重建行。
        _outlineTree.SetStyle(BuildOutlineTreeStyle());
        _outlineSignature = "";

        // ★ 强制面板重绘（FillStyle 普通属性不触发渲染；Refresh 通过 detach/attach 触发）
        _panel.Refresh();
    }

    // Tab content layout: toolbar + scrollable content
    // Vertical rhythm: toolbar → 2-line gap (breathing room) → scrollable list
    // No background shell — DesertButton backgrounds already provide visual anchoring.
    // Spacing(2) creates the "inner bezel" gap without background color conflicts.
    // 每个 tab 的内容区包 ScrollViewer（右栏压缩到 60% 后数据多时纵向滚动；
    // 横向保持原样——tab strip 不在滚动区内）。ScrollViewer 用 stretch 拿 VStack 剩余高度。
    // 紧凑性不变量：Accordion/AccordionActionPanel 必须 Align.Start（自然高度顶对齐），
    // 否则内容短于视口时 ScrollViewer 会把整个视口高度交给 Accordion，
    // 展开项（growY=1）会吸收全部剩余高度撑满面板，后续折叠项被挤出可视区。
    private BackgroundPanel MakeTabContent(Visual content)
    {
        // 实体列表滚轮：Preview(Tunnel) 阶段拦截——先于 ScrollViewer 自身的 Bubble 处理器，
        // 也不被展开项内部控件截胡；3 行/格步进（框架默认 1 行，同 ConversationPanel 模式）。
        var listScroll = new ScrollViewer
        {
            Content = content,
            VerticalAlignment = Align.Stretch,
            HorizontalScrollEnabled = false,
        };
        // 滚动条轨道/拇指用面板底色（同 _outlineFlow）：默认主题样式只有前景色 → 单元格背景
        // 直通终端默认底（深色终端下就是黑块），悬停高亮时更明显（用户报障：展开项后鼠标移过边缘出黑块）。
        listScroll.SetStyle(BuildListScrollStyle());
        _listScrolls.Add(listScroll);
        listScroll.PointerWheelRouted += (_, e) =>
        {
            if (e.RoutingPhase != RoutingPhase.Preview || e.WheelDelta == 0)
                return;
            ApplyWheelScroll(listScroll, e.WheelDelta);
            e.Handled = true;
        };
        return RegisterTabContent(
            new BackgroundPanel
            {
                FillStyle = Style
                    .None.WithBackground(_colors.SidePanelBg)
                    .WithForeground(_colors.SidePanelFg),
                Content = new Padder
                {
                    Content = new VStack(listScroll).Spacing(1).VerticalAlignment(Align.Stretch),
                    // 左 1 缩进：折叠项与左缘保持呼吸（此前 0 导致折叠项贴边）；
                    // 右 1 给滚动条留位
                    Padding = new Thickness(1, 1, 1, 0),
                    HorizontalAlignment = Align.Stretch,
                    VerticalAlignment = Align.Stretch,
                },
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
        );
    }

    /// <summary>
    /// 实体列表滚轮步进：一格滚轮 = 3 行；上下都钳在 [0, 可滚最大值]。
    /// internal 测试缝（Tui.Tests 经 InternalsVisibleTo 直接调用）。
    /// </summary>
    internal static void ApplyWheelScroll(ScrollViewer scroll, int wheelDelta)
    {
        const int scrollStep = 3;
        if (scroll.VerticalOffset == 0 && wheelDelta > 0)
            return; // 已在顶部还往上滚：不动（避免把偏移推成负数后被框架钳回 0 的抖动）
        var max = MaxVerticalOffset(scroll);
        var next = Math.Clamp(
            scroll.VerticalOffset + (wheelDelta > 0 ? -scrollStep : scrollStep),
            0,
            max
        );
        scroll.VerticalOffset = next;
    }

    /// <summary>
    /// 可滚最大偏移 = 内容 extent - 视口。extent 直接对内容做一次无限高度 Measure 获得
    /// （不依赖 ScrollViewer.DesiredSize——它已被视口钳制；也不碰库内部字段）。
    /// 内容未溢出时为 0。
    /// </summary>
    internal static int MaxVerticalOffset(ScrollViewer scroll)
    {
        var viewport = scroll.ViewportHeight;
        if (viewport <= 0)
            return 0;
        var extent = MeasureContentHeight(scroll);
        return Math.Max(0, extent - viewport);
    }

    private static int MeasureContentHeight(ScrollViewer scroll)
    {
        var content = scroll.Content;
        if (content is null)
            return 0;
        // 与 ScrollViewer.ArrangeCore 的非 content-scroll 分支同参：按当前视口宽重测、高度不设上限
        // （LayoutConstants 是库 internal；int.MaxValue 是等效的「无上界」约束值）
        var width = Math.Max(1, scroll.ViewportWidth);
        var hints = content.Measure(
            new XenoAtom.Terminal.UI.Layout.LayoutConstraints(0, width, 0, int.MaxValue)
        );
        return Math.Max(0, hints.Natural.Height);
    }

    /// <summary>
    /// 大纲 tab 内容区：与 <see cref="MakeTabContent(Visual)"/> 同构，
    /// 但**不包 ScrollViewer**——DocumentFlow 自带滚动，外包会让虚拟化拿到无限高度、退化为全量实例化。
    /// </summary>
    private BackgroundPanel MakeOutlineTabContent(Visual toolbar) =>
        RegisterTabContent(
            new BackgroundPanel
            {
                FillStyle = Style
                    .None.WithBackground(_colors.SidePanelBg)
                    .WithForeground(_colors.SidePanelFg),
                Content = new Padder
                {
                    Content = new VStack(toolbar, _outlineTreeHost)
                        .Spacing(1)
                        .VerticalAlignment(Align.Stretch),
                    Padding = new Thickness(1, 1, 1, 0),
                    HorizontalAlignment = Align.Stretch,
                    VerticalAlignment = Align.Stretch,
                },
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
        );

    /// <summary>注册分区内容面板（构建时快照的 FillStyle 需要主题切换时同步刷新）。</summary>
    private BackgroundPanel RegisterTabContent(BackgroundPanel panel)
    {
        _tabContentPanels.Add(panel);
        return panel;
    }

    /// <summary>
    /// 同步刷新所有分区内容面板的背景（主题切换立即生效，不依赖异步树重建）。
    /// 分区内容面板在构建时快照了 FillStyle（旧主题色），不透明覆盖 _panel——
    /// 只更新 _panel.FillStyle 看不到变化，必须逐个更新并强制重绘。
    /// </summary>
    private void RefreshTabContentBackgrounds()
    {
        foreach (var panel in _tabContentPanels)
        {
            panel.FillStyle = Style
                .None.WithBackground(_colors.SidePanelBg)
                .WithForeground(_colors.SidePanelFg);
            panel.Refresh();
        }
    }
}
