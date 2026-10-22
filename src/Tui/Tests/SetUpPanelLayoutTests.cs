using System.Reflection;
using System.Text;
using Core.Cloud;
using Core.Data;
using Core.Novels;
using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 布局回归测试：SetUpPanel 每个 tab 的内容区必须是紧凑的折叠项排布。
///
/// 背景（两次被破坏，用测试锁定）：
/// 1. Accordion 若 VerticalAlignment = Stretch，内容短于视口时 ScrollViewer 会把整个
///    视口高度交给 Accordion，展开项（growY=1）吸收全部剩余高度撑满面板，
///    后续折叠项被挤出可视区域 → 折叠项必须 Align.Start（自然高度）。
/// 2. 工具栏 HStack 默认 Stretch（Panel 基类），外层 VStack 拉伸时 FlexAllocator
///    按 grow 权重分剩余高度 → 工具栏被拉成十几行、折叠项被挤到面板底部 →
///    工具栏必须 Align.Start。
/// </summary>
public sealed class SetUpPanelLayoutTests
{
    internal sealed class FakeStore : INovelStore
    {
        public List<Character> Characters { get; } = [];
        public List<Chapter> Chapters { get; } = [];
        public List<Volume> Volumes { get; } = [];
        public List<Faction> Factions { get; } = [];
        public List<Plotline> Plotlines { get; } = [];
        public List<Location> Locations { get; } = [];
        public List<Item> Items { get; } = [];
        public List<Relationship> Relationships { get; } = [];
        public List<SceneDefinition> Scenes { get; } = [];
        public List<AuthorNote> Notes { get; } = [];
        public Novel? Novel { get; set; }

        public event Func<EntityChangeEvent, Task>? OnEntityChanged
        {
            add { }
            remove { }
        }

        public Task<Novel?> GetNovel(string id, CancellationToken ct = default) =>
            Task.FromResult(Novel);

        public Task<Novel?> GetNovelSummary(string id, CancellationToken ct = default) =>
            Task.FromResult(Novel);

        public Task<List<Novel>> GetAllNovels(CancellationToken ct = default) =>
            Task.FromResult<List<Novel>>([]);

        public Task SaveNovel(Novel novel, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteNovel(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Chapter?> GetChapter(string id, CancellationToken ct = default) =>
            Task.FromResult<Chapter?>(null);

        public Task<Chapter?> GetChapter(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Chapter?>(null);

        public Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default) =>
            Task.FromResult(Chapters);

        public Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default)
        {
            var headers = Chapters
                .Select(c => new Chapter
                {
                    Id = c.Id,
                    NovelId = c.NovelId,
                    Title = c.Title,
                    Order = c.Order,
                    Status = c.Status,
                    VolumeId = c.VolumeId,
                    Summary = c.Summary,
                    PlotlineIds = c.PlotlineIds,
                    CharacterIds = c.CharacterIds,
                    Notes = c.Notes,
                    CharCount = c.CharCount,
                })
                .OrderBy(c => c.Order)
                .ToList();
            var volumeIds = Volumes.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
            var unassigned = headers
                .Where(c => string.IsNullOrEmpty(c.VolumeId) || !volumeIds.Contains(c.VolumeId!))
                .ToList();
            return Task.FromResult<NovelStructure?>(
                new NovelStructure(
                    Novel ?? new Novel { Id = novelId },
                    Volumes,
                    headers,
                    unassigned
                )
            );
        }

        public Task<Chapter?> GetChapterByOrder(
            string novelId,
            int order,
            CancellationToken ct = default
        ) => Task.FromResult(Chapters.FirstOrDefault(c => c.Order == order));

        public Task SaveChapter(Chapter chapter, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReorderAllChapters(
            string novelId,
            IReadOnlyList<ChapterOrderAssignment> assignments,
            CancellationToken ct = default
        )
        {
            foreach (var assignment in assignments)
            {
                var chapter = Chapters.FirstOrDefault(c => c.Id == assignment.ChapterId);
                if (chapter is not null)
                    chapter.Order = assignment.Order;
            }
            return Task.CompletedTask;
        }

        public Task DeleteChapter(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteChapter(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> GetChapterCount(string novelId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<Character?> GetCharacter(string id, CancellationToken ct = default) =>
            Task.FromResult<Character?>(null);

        public Task<Character?> GetCharacter(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Character?>(null);

        public Task<List<Character>> GetCharacters(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(Characters);

        public Task SaveCharacter(Character character, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteCharacter(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteCharacter(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> GetCharacterCount(string novelId, CancellationToken ct = default) =>
            Task.FromResult(Characters.Count);

        public Task<Plotline?> GetPlotline(string id, CancellationToken ct = default) =>
            Task.FromResult<Plotline?>(null);

        public Task<Plotline?> GetPlotline(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Plotline?>(null);

        public Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default) =>
            Task.FromResult(Plotlines);

        public Task<List<Plotline>> GetOpenPlotlines(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<Plotline>>([]);

        public Task SavePlotline(Plotline plotline, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeletePlotline(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeletePlotline(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<Faction?> GetFaction(string id, CancellationToken ct = default) =>
            Task.FromResult<Faction?>(null);

        public Task<Faction?> GetFaction(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Faction?>(null);

        public Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default) =>
            Task.FromResult(Factions);

        public Task SaveFaction(Faction faction, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteFaction(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteFaction(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveFactionRelationship(
            FactionRelationship rel,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task<List<WorldRule>> GetWorldRules(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<WorldRule>());

        public Task SaveWorldRule(string novelId, WorldRule rule, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteWorldRule(
            string novelId,
            string ruleId,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task<List<FactionRelationship>> GetFactionRelationships(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<FactionRelationship>>([]);

        public Task<Location?> GetLocation(string id, CancellationToken ct = default) =>
            Task.FromResult<Location?>(null);

        public Task<Location?> GetLocation(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Location?>(null);

        public Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default) =>
            Task.FromResult(Locations);

        public Task SaveLocation(Location location, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteLocation(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteLocation(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<Item?> GetItem(string id, CancellationToken ct = default) =>
            Task.FromResult<Item?>(null);

        public Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default) =>
            Task.FromResult<Item?>(null);

        public Task<List<Item>> GetItems(string novelId, CancellationToken ct = default) =>
            Task.FromResult(Items);

        public Task SaveItem(Item item, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteItem(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteItem(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<TimelineEvent>> GetTimelineEvents(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<TimelineEvent>>([]);

        public Task SaveTimelineEvent(TimelineEvent evt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default) =>
            Task.FromResult(Notes.Where(n => n.NovelId == novelId).ToList());

        public Task SaveNote(AuthorNote note, CancellationToken ct = default)
        {
            Notes.RemoveAll(n => n.Id == note.Id);
            Notes.Add(note);
            return Task.CompletedTask;
        }

        public Task<Relationship?> GetRelationship(string id, CancellationToken ct = default) =>
            Task.FromResult<Relationship?>(null);

        public Task<Relationship?> GetRelationship(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Relationship?>(null);

        public Task<List<Relationship>> GetRelationshipsByCharacter(
            string characterId,
            CancellationToken ct = default
        ) => Task.FromResult<List<Relationship>>([]);

        public Task<List<Relationship>> GetRelationshipsByNovel(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(Relationships);

        public Task SaveRelationship(Relationship relationship, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteRelationship(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteRelationship(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<EntityAuditLog>> GetEntityAuditLog(
            string entityId,
            CancellationToken ct = default
        ) => Task.FromResult<List<EntityAuditLog>>([]);

        public Task WriteAuditLogs(
            string novelId,
            EntityType entityType,
            string entityId,
            AuditAction action,
            Dictionary<string, string>? fieldChanges,
            AuditSource source = AuditSource.Ai,
            string? toolName = null,
            string? sessionId = null,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task AppendOntologyActions(
            string novelId,
            IReadOnlyList<OntologyAction> actions,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task<List<OntologyAction>> GetOntologyActions(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<OntologyAction>());

        public Task<long> GetOntologyJournalStamp(string novelId, CancellationToken ct = default) =>
            Task.FromResult(0L);

        public Task<SceneDefinition?> GetScene(string id, CancellationToken ct = default) =>
            Task.FromResult<SceneDefinition?>(null);

        public Task<SceneDefinition?> GetScene(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<SceneDefinition?>(null);

        public Task<List<SceneDefinition>> GetScenesByChapter(
            string chapterId,
            CancellationToken ct = default
        ) => Task.FromResult<List<SceneDefinition>>([]);

        public Task<List<SceneDefinition>> GetScenesByNovel(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(Scenes);

        public Task SaveScene(SceneDefinition scene, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteScene(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteScene(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> GetSceneCount(string novelId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> GetTotalWordCount(string novelId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<string?> GetCharacterNameById(
            string novelId,
            string characterId,
            CancellationToken ct = default
        ) => Task.FromResult<string?>(null);

        // ── Entity Evolution（entity-evolution-lifecycle：布局测试桩不消费）──
        public Task<List<AppearanceRecord>> GetAppearances(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<AppearanceRecord>());

        public Task<List<AppearanceRecord>> GetAppearancesByChapter(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<AppearanceRecord>());

        public Task SaveChapterAppearances(
            string novelId,
            string chapterId,
            IReadOnlyList<AppearanceRecord> records,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task DeleteChapterAppearances(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task<List<EntityStateChange>> GetEntityStateChanges(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<EntityStateChange>());

        public Task<List<EntityStateChange>> GetEntityStateChangesByChapter(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<EntityStateChange>());

        public Task<List<EntityStateChange>> GetEntityStateChangesByEntity(
            string novelId,
            EntityType entityType,
            string entityId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<EntityStateChange>());

        public Task SaveEntityStateChanges(
            string novelId,
            string chapterId,
            IReadOnlyList<EntityStateChange> changes,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task DeleteEntityStateChanges(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        // Volume（大纲二层：卷纲）— 测试桩：内存实现最小可用
        public Task<Volume?> GetVolume(string id, CancellationToken ct = default) =>
            Task.FromResult<Volume?>(null);

        public Task<Volume?> GetVolume(string novelId, string id, CancellationToken ct = default) =>
            Task.FromResult<Volume?>(null);

        public Task<List<Volume>> GetVolumes(string novelId, CancellationToken ct = default) =>
            Task.FromResult(Volumes);

        public Task SaveVolume(Volume volume, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteVolume(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteVolume(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    internal const int PanelWidth = 46;
    internal const int PanelHeight = 42;

    /// <summary>
    /// 模糊搜索框（信息量增长后的定位入口）：输入即过滤当前 tab 的实体列表（子序列匹配、
    /// 忽略大小写），清空恢复；过滤发生在行数上限裁剪之前（第 61 条以后也搜得到）。
    /// </summary>
    [Fact]
    public async Task Search_FiltersEntityList_And_AppliesBeforeRowCap()
    {
        var store = new FakeStore
        {
            Novel = new Novel { Id = "n1", Title = "搜索测试" },
        };
        for (var i = 1; i <= 70; i++)
            store.Characters.Add(
                new Character
                {
                    Id = $"c{i}",
                    NovelId = "n1",
                    Name = $"角色{i}号",
                    Role = CharacterRole.Supporting,
                }
            );
        // 第 65 条（行数上限 60 之外）：命中「独目」子序列
        store.Characters.Add(
            new Character
            {
                Id = "c71",
                NovelId = "n1",
                Name = "独目行者",
                Role = CharacterRole.Minor,
            }
        );
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);
        // 71 条 → 上限 60 条 + 1 条「…还有 N 条」
        Assert.Equal(MaxAccordionRowsOf(panel) + 1, GetCharacterAccordion(panel).Children.Count);

        // 输入搜索文本（DynamicUpdate 在下一轮 measure 应用过滤；测试无 app，直接调同路径）
        panel.ApplySearchText("独目");
        ArrangePanel(panel);

        var match = Assert.Single(GetCharacterAccordion(panel).Children);
        Assert.Contains("独目行者", HeaderText((Collapsible)match));

        // 清空 → 恢复（仍受行数上限约束）
        panel.ApplySearchText("");
        ArrangePanel(panel);
        Assert.Equal(MaxAccordionRowsOf(panel) + 1, GetCharacterAccordion(panel).Children.Count);
    }

    /// <summary>搜索未命中：列表给出「未命中「X」」而不是空列表或旧内容。</summary>
    [Fact]
    public async Task Search_NoMatch_ShowsNoMatchRow()
    {
        var store = new FakeStore
        {
            Novel = new Novel { Id = "n1", Title = "搜索测试" },
        };
        store.Characters.Add(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林长歌",
                Role = CharacterRole.Protagonist,
            }
        );
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        panel.ApplySearchText("zzz");
        ArrangePanel(panel);

        var row = Assert.Single(GetCharacterAccordion(panel).Children);
        Assert.Contains("未命中", HeaderText((Collapsible)row));
    }

    /// <summary>
    /// 性别图标回归（用户报障）：切换角色性别后，折叠项名字后面的 ♂/♀ 图标消失。
    ///
    /// 根因：<see cref="Character.Gender"/> 是跨仓线协议值——对话框存英文枚举
    /// （"Male"/"Female"/"Other"，与 Role/Faction.Type 同约定，HarnessContextBuilder
    /// 会原样送进云端契约），而种子数据 / 早期版本存中文（"男"/"女"）。渲染侧的图标
    /// switch 只认中文 → Gender="Female" 的角色图标落空 → 图标没了。
    ///
    /// 修法：只修渲染侧——图标 + 详情行文字都先把存储值归一成中文标签
    /// （SetUpPanel.GenderLabel）。存储格式不动（改它就是改线协议）。
    /// </summary>
    [Fact]
    public async Task CharacterRow_ShowsGenderIcon_ForBothStoredFormats()
    {
        var store = new FakeStore();
        store.Characters.Add(
            new Character
            {
                Id = "c1",
                Name = "中文值",
                Role = CharacterRole.Supporting,
                Gender = "女",
            }
        );
        store.Characters.Add(
            new Character
            {
                Id = "c2",
                Name = "英文旧值",
                Role = CharacterRole.Supporting,
                Gender = "Female",
            }
        );
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        var accordion = GetCharacterAccordion(panel);
        Assert.Equal(2, accordion.Children.Count);
        Assert.Contains("♀", HeaderText((Collapsible)accordion.Children[0]));
        Assert.Contains("♀", HeaderText((Collapsible)accordion.Children[1]));
    }

    /// <summary>
    /// 性别详情行：英文枚举值与中文旧值都必须显示成中文标签（用户选择：展示统一中文，
    /// 存储保持英文线协议值不动）。
    /// </summary>
    [Theory]
    [InlineData("女", "♀ 女")]
    [InlineData("Female", "♀ 女")]
    [InlineData("男", "♂ 男")]
    [InlineData("Male", "♂ 男")]
    [InlineData("Other", "🧑 其他")]
    [InlineData("其他", "🧑 其他")]
    public void CharacterDetail_NormalizesGenderLabelToChinese(string stored, string expected)
    {
        var detail = (string)
            typeof(SetUpPanel)
                .GetMethod("BuildCharacterDetail", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(
                    null,
                    [
                        new Character
                        {
                            Id = "c1",
                            Name = "测试",
                            Gender = stored,
                        },
                        null, // locNameMap（当前地点解析器；本用例不涉及）
                    ]
                )!;

        Assert.Contains(expected, detail);
    }

    /// <summary>角色卡片显示生存状态与当前地点（地点名由 locNameMap 解析；缺失回显 id）。</summary>
    [Fact]
    public void CharacterDetail_ShowsStatusAndCurrentLocation()
    {
        var detail = (string)
            typeof(SetUpPanel)
                .GetMethod("BuildCharacterDetail", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(
                    null,
                    [
                        new Character
                        {
                            Id = "c1",
                            Name = "林晚",
                            Status = CharacterStatus.Missing,
                            CurrentLocationId = "loc1",
                        },
                        new Dictionary<string, string> { ["loc1"] = "破庙" },
                    ]
                )!;

        Assert.Contains("失踪", detail);
        Assert.Contains("📍 破庙", detail);
    }

    /// <summary>四类实体的状态标签（发布事务/联动会改这些字段，卡片必须看得见）。</summary>
    [Fact]
    public void EntityStatusLabels_MapToChinese()
    {
        object Label(string method, object value) =>
            typeof(SetUpPanel)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [value])!;

        Assert.Contains("覆灭", (string)Label("FactionStatusLabel", FactionStatus.Dissolved));
        Assert.Contains("遗失", (string)Label("ItemStatusLabel", ItemStatus.Lost));
        Assert.Contains("封印", (string)Label("LocationStatusLabel", LocationStatus.Sealed));
        Assert.Contains("已解决", (string)Label("PlotlineStatusLabel", PlotlineStatus.Resolved));
    }

    /// <summary>面板行数上限（与 SetUpPanel.MaxAccordionRows 保持一致）。</summary>
    private static int MaxAccordionRowsOf(SetUpPanel panel) =>
        (int)
            typeof(SetUpPanel)
                .GetField("MaxAccordionRows", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

    /// <summary>
    /// 实体列表滚轮步进（修复「项目多了无法滚动」）：一格滚轮 = 3 行，上下都钳在可滚范围。
    /// 注意：未布局的 ScrollViewer 视口为 0 → max=0，偏移恒 0；测试先 Arrange 出真实视口。
    /// </summary>
    [Fact]
    public void WheelScroll_StepsThreeRows_AndClampsAtTop()
    {
        var scroll = new ScrollViewer { Content = MakeTallContent(30) };
        Arrange(scroll, 20, 10);
        scroll.VerticalOffset = 4;
        SetUpPanel.ApplyWheelScroll(scroll, 1); // 上滚一格
        Assert.Equal(1, scroll.VerticalOffset);
        SetUpPanel.ApplyWheelScroll(scroll, 1); // 到顶钳 0
        Assert.Equal(0, scroll.VerticalOffset);
        SetUpPanel.ApplyWheelScroll(scroll, 1); // 顶后再滚不回弹
        Assert.Equal(0, scroll.VerticalOffset);
    }

    /// <summary>下滚不得越过内容底部（extent - viewport）。</summary>
    [Fact]
    public void WheelScroll_ClampsAtBottom()
    {
        var scroll = new ScrollViewer { Content = MakeTallContent(30) };
        Arrange(scroll, 20, 10);
        for (var i = 0; i < 10; i++)
            SetUpPanel.ApplyWheelScroll(scroll, -1);
        var max = SetUpPanel.MaxVerticalOffset(scroll);
        Assert.Equal(max, scroll.VerticalOffset);
        SetUpPanel.ApplyWheelScroll(scroll, -1); // 底部后再滚不越界
        Assert.Equal(max, scroll.VerticalOffset);
    }

    private static VStack MakeTallContent(int rows)
    {
        var stack = new VStack();
        for (var i = 0; i < rows; i++)
            stack.Children.Add(new Markup($"row {i}"));
        return stack;
    }

    private static void Arrange(Visual visual, int width, int height)
    {
        visual.Measure(new LayoutConstraints(0, width, 0, height));
        visual.Arrange(new Rectangle(0, 0, width, height));
    }

    /// <summary>
    /// 检查结果开关：行序列不变时必须重建（旧指纹不含开关态，点击静默无反应）；
    /// 检查回填后空态引导必须被结果正文替换。
    /// </summary>
    [Fact]
    public async Task SetupCheckToggle_RebuildsWithEmptyState_AndBackfillsResult()
    {
        var store = new FakeStore
        {
            Novel = new Novel
            {
                Id = "n1",
                NovelId = "n1",
                Title = "开关重建",
                Outline = "全书走向：复仇",
            },
        };
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        typeof(SetUpPanel)
            .GetMethod("SelectSection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [7]); // 大纲分区
        ArrangePanel(panel);

        // 默认：书级分节折叠（先给全局）；小说行常驻（书名 + ＋ / ⋯，详情在 ⋯ 菜单里）
        var texts = CollectTexts(panel.Visual);
        Assert.DoesNotContain(texts, t => t.Contains("尚未运行"));
        Assert.Contains(texts, t => t.Contains("开关重建"));
        Assert.Contains("📄 详情", RowMenuLabels(panel, (OutlineRow)NovelNode(panel).Data!));
        Assert.Contains(
            NovelNode(panel).Children,
            child =>
                NodeHeaderText(child).Contains("总纲：全书走向：复仇", StringComparison.Ordinal)
        );

        // 展开检查分节（原为点「🔍 检查」菜单项，入口已移除，改直接驱动）→ 空态引导出现
        panel.ToggleSetupCheckResults();
        ArrangePanel(panel);
        texts = CollectTexts(panel.Visual);
        Assert.Contains(texts, t => t.Contains("尚未运行"));

        // 检查结果回填：空态让位给结果正文（Findings 为空 → 结构自洽）
        panel.ShowSetupCheckResult(new CheckSetupDetails());
        ArrangePanel(panel);
        texts = CollectTexts(panel.Visual);
        Assert.DoesNotContain(texts, t => t.Contains("尚未运行"));
        Assert.Contains(texts, t => t.Contains("结构自洽"));
    }

    /// <summary>
    /// 展开检查分节后，「▶ 运行」按钮触发云端检查事件。
    /// 注：书/卷 ⋯ 菜单里的「🔍 检查」入口已按用户要求移除，这里改经
    /// <see cref="SetUpPanel.ToggleSetupCheckResults"/> 直接驱动分节展开。
    /// </summary>
    [Fact]
    public async Task SetupCheck_SectionExpands_AndRunButtonFiresEvent()
    {
        var (_, panel) = await ArrangeOutlineFixtureAsync();
        var novel = NovelNode(panel);
        novel.IsExpanded = false;
        ArrangePanel(panel);

        var setupChecks = new List<string>();
        panel.OnCloudSetupCheck += setupChecks.Add;
        panel.ToggleSetupCheckResults();
        ArrangePanel(panel);

        var texts = CollectTexts(panel.Visual);
        Assert.True(NovelNode(panel).IsExpanded, "「检查」必须展开小说根节点");
        Assert.Contains(texts, t => t.Contains("尚未运行"));
        Assert.Empty(setupChecks); // 展开分节不触发运行

        var checkNode = FindNode(NovelNode(panel), "检查结果");
        Assert.NotNull(checkNode);
        var runButton = NodeActionButtons(checkNode!).Single(b => Label(b).Contains("运行"));
        ClickButton(runButton);

        Assert.Equal(["n1"], setupChecks);
    }

    /// <summary>
    /// 大纲 tab 的搜索区不得出现两个放大镜（用户报障：占位符一个、右侧筛选按钮又一个）。
    /// 规则：占位符不带 🔍，🔍 只留在右侧「🔍 筛选」按钮上。
    /// </summary>
    [Fact]
    public void OutlineSearchPlaceholder_HasNoDuplicateMagnifier()
    {
        var store = new FakeStore
        {
            Novel = new Novel { Id = "n1", Title = "去重" },
        };
        var panel = new SetUpPanel(store, PanelThemes.Dark) { CurrentNovelId = "n1" };
        ArrangePanel(panel);

        var placeholder = GetPrivate<TextBox>(panel, "_searchBox").Placeholder ?? "";
        Assert.DoesNotContain("🔍", placeholder);
        Assert.Contains("搜索章节标题", placeholder);

        // 放大镜只在按钮上：现在每个分区的工具带都常驻「🔍 筛选」（用户要求各 tab 统一）
        var filterLabel = string.Join("", Labels(GetPrivate<Button>(panel, "_filterButton")));
        Assert.Contains("🔍 筛选", filterLabel);
    }

    /// <summary>
    /// 设定检查入口在大纲工具带（书/卷 ⋯ 菜单里已按用户要求移除）：按钮存在，
    /// 且点它真能展开「🔍 检查结果」分节（不是只剩一个死按钮）。
    /// 开关式：再点一次必须收起（用户报障：展开后关不掉）。
    /// </summary>
    [Fact]
    public async Task SetupCheck_ToolbarButton_ExpandsSection()
    {
        var (_, panel) = await ArrangeOutlineFixtureAsync();
        var novel = NovelNode(panel);
        novel.IsExpanded = false;
        ArrangePanel(panel);

        var cloudRow = GetPrivate<WrapHStack>(panel, "_cloudStatusRow");
        var checkButton = cloudRow
            .EnumerateVisualsDepthFirst()
            .OfType<Button>()
            .Single(b => (b.Content as Markup)?.Text?.Contains("检查") == true);

        ClickButton(checkButton);
        ArrangePanel(panel);

        Assert.True(NovelNode(panel).IsExpanded, "工具带「🔍 检查」必须展开小说根节点");
        Assert.NotNull(FindNode(NovelNode(panel), "检查结果"));

        // 再点一次 → 收起（开关式，不能只进不出）
        ClickButton(checkButton);
        ArrangePanel(panel);
        Assert.Null(FindNode(NovelNode(panel), "检查结果"));
    }

    /// <summary>复现用户报障：未分卷行显示「N 章 · M 字」，展开却看不到章行。</summary>
    [Fact]
    public async Task UnassignedGroup_Expanded_ShowsChapterRows()
    {
        var store = new FakeStore
        {
            Novel = new Novel
            {
                Id = "n1",
                NovelId = "n1",
                Title = "复现",
                Outline = "走向",
            },
        };
        for (var i = 1; i <= 11; i++)
            store.Chapters.Add(
                new Chapter
                {
                    Id = $"c{i}",
                    NovelId = "n1",
                    Title = $"第{i}章",
                    Order = i,
                    Content = "正文",
                    CharCount = i * 3,
                    VolumeId = null,
                }
            );
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        panel.CurrentNovelId = "n1";
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        var novel = NovelNode(panel);
        var unassigned = novel.Children.Single(n => NodeHeaderText(n).Contains("未分卷"));
        Assert.Contains("11 章", NodeHeaderText(unassigned));
        unassigned.IsExpanded = true;
        ArrangePanel(panel);

        var texts = unassigned.Children.Select(NodeHeaderText).ToList();
        Assert.True(
            texts.Count > 0,
            $"未分卷展开后没有章行！children={texts.Count} header={NodeHeaderText(unassigned)}"
        );
    }

    /// <summary>折叠项头部文本（容忍 Markup 直接作 header 的占位行，如「(无章节)」）。</summary>
    private static string HeaderText(Collapsible collapsible) =>
        collapsible.Header switch
        {
            Markup markup => markup.Text,
            Grid grid when grid.Cells.Count > 0 && grid.Cells[0].Content is Markup cellMarkup =>
                cellMarkup.Text,
            _ => "",
        };

    /// <summary>调用私有实例方法（测试驱动重排）。</summary>
    private static void InvokePrivate(object target, string method) =>
        typeof(SetUpPanel)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, null);

    /// <summary>
    /// 大目录行数上限（TUI 侧规模化）：70 个角色 → 默认只渲染 60 行 + 「还有 10 条（点击显示全部）」；
    /// 置位「显示全部」后刷新 → 全量 70 行、提示行消失。
    /// </summary>
    [Fact]
    public async Task LargeList_CapsRows_UntilShowAll()
    {
        var store = new FakeStore();
        for (var i = 1; i <= 70; i++)
            store.Characters.Add(
                new Character
                {
                    Id = $"c{i}",
                    NovelId = "n1",
                    Name = $"角色{i:00}",
                }
            );

        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        var accordion = GetCharacterAccordion(panel);
        // 60 行 + 1 个「还有 10 条」提示行
        Assert.Equal(61, accordion.Children.Count);
        Assert.Contains(CollectTexts(panel.Visual), t => t.Contains("还有 10 条（点击显示全部）"));

        // 「点击显示全部」的等价状态（真实点击经 _headerActions 注册，测试直接置位）
        var showAll =
            (HashSet<EntityType>)
                typeof(SetUpPanel)
                    .GetField("_showAllLists", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(panel)!;
        showAll.Add(EntityType.Character);
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        Assert.Equal(70, GetCharacterAccordion(panel).Children.Count); // 全量，且无提示行
        Assert.DoesNotContain(CollectTexts(panel.Visual), t => t.Contains("点击显示全部"));
    }

    /// <summary>
    /// AI 跳转（NavigateToEntity）必须切到对应分区——分区宿主改为「全子面板常驻 + 仅切
    /// IsVisible」（ContentSwitcher 会让非活动子树脱离 App），下标映射（章节/卷 → 大纲 = 7）不变。
    /// </summary>
    [Fact]
    public async Task NavigateToEntity_SwitchesSection()
    {
        var store = new FakeStore();
        store.Novel = new Novel { Id = "n1", Title = "测试" };
        store.Chapters.Add(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "第1章",
                Order = 1,
                Summary = "细纲",
            }
        );
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        // 大纲排在第一个分区（用户报障：大纲在最后一个 tab，进面板看不到最新进度）
        Assert.Equal(0, GetPrivate<int>(panel, "_sectionIndex")); // 默认大纲分区
        var panels = GetPrivate<Visual[]>(panel, "_sectionPanels");
        Assert.True(panels[0].IsVisible);
        Assert.False(panels[1].IsVisible);

        panel.NavigateToEntity(EntityType.Chapter, "ch1"); // 章节 → 大纲分区（第 0 个）
        Assert.Equal(0, GetPrivate<int>(panel, "_sectionIndex"));
        Assert.True(panels[0].IsVisible);
        Assert.False(panels[1].IsVisible);

        panel.NavigateToEntity(EntityType.Location, "loc1"); // 地点 → 第 3 个分区
        Assert.Equal(3, GetPrivate<int>(panel, "_sectionIndex"));
        Assert.True(panels[3].IsVisible);
        Assert.False(panels[0].IsVisible);
    }

    /// <summary>
    /// <summary>
    /// 分区栏（8 个分区：角色/组织/地点/情节/场景/道具/关系/大纲）在窄面板必须折行，
    /// 且每个分区按钮保持自然宽度——旧 TabControl 单行 strip 在 30% 面板下会溢出，
    /// 最后一个分区只露图标（用户实测截图）。
    /// </summary>
    [Fact]
    public async Task SectionBar_WrapsAtNarrowWidth_WithoutShrinkingTitles()
    {
        var store = new FakeStore();
        store.Novel = new Novel { Id = "n1", Title = "测试" };
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");

        // 大纲排第一个（用户报障：大纲在最后一个 tab，进面板看不到最新进度）
        var expected = new[]
        {
            "📋 大纲",
            "🎭 角色",
            "🏰 组织",
            "📍 地点",
            "📜 情节",
            "🎬 场景",
            "🎒 道具",
            "🔗 关系",
        };

        foreach (var (width, minRows) in new[] { (26, 2), (46, 2), (140, 1) })
        {
            var visual = panel.Visual;
            visual.Measure(new LayoutConstraints(0, width, 0, 9999));
            visual.Arrange(new Rectangle(0, 0, width, 42));

            var bar = GetPrivate<WrapHStack>(panel, "_sectionBar");
            var buttons = bar.EnumerateVisualsDepthFirst().OfType<Button>().ToList();
            Assert.Equal(expected.Length, buttons.Count);
            Assert.Equal(expected, buttons.Select(b => string.Join("", Labels(b))).ToArray());
            Assert.True(
                bar.Bounds.Height >= minRows,
                $"面板宽 {width} 时分区栏高度={bar.Bounds.Height}，应 >= {minRows}"
            );
            for (var i = 0; i < buttons.Count; i++)
            {
                // 折行而不是压缩：宽度不足时应换行，按钮保持自然宽度（文字不截断）
                Assert.True(
                    buttons[i].Bounds.Width >= buttons[i].DesiredSize.Width,
                    $"面板宽 {width} 时「{expected[i]}」被压缩：{buttons[i].Bounds.Width} < {buttons[i].DesiredSize.Width}"
                );
                Assert.True(
                    buttons[i].Bounds.Right <= bar.Bounds.Right,
                    $"面板宽 {width} 时「{expected[i]}」超出分区栏右边界"
                );
            }
        }
    }

    /// <summary>
    /// 工具带 = 每分区唯一一行 chrome：搜索框 + 唯一的**上下文动作**按钮。
    /// 实体分区是「➕ 新建」（点它直接新建该类型，不依赖任何展开状态）；
    /// 大纲分区同一位置换成「🔍 筛选」（书级新建在树节点的 ＋ / ⋯ 上）。
    /// 旧的「📝 编辑 / ❌ 删除」工具栏按钮作用于不可见的「当前展开项」——已删；
    /// 编辑 / 引用 / 历史 / 删除全部下沉到每行行首（目标 = 你点的那一行）。
    /// </summary>
    [Fact]
    public async Task ToolBand_HasSingleContextAction_PerSection()
    {
        var store = new FakeStore();
        store.Novel = new Novel { Id = "n1", Title = "测试" };
        store.Characters.AddRange(MakeCharacters(3));
        var panel = new SetUpPanel(store, PanelThemes.Dark) { CurrentNovelId = "n1" };
        await panel.RefreshAsync("n1");
        panel.NavigateToEntity(EntityType.Character, "c1"); // 大纲排第一了，实体分区要显式切
        ArrangePanel(panel);

        // 实体行右侧只有一个 ⋯（查看/编辑 · 插入引用 · 历史 · 删除都在 popup 里），
        // 旧工具栏（作用于「当前展开项」）与行上那排抽象小图标都不得回流
        var raw = CollectRawMarkup(panel.Visual);
        Assert.Contains(raw, t => t.Contains("⋯"));
        Assert.DoesNotContain(raw, t => t.Contains("📝"));
        Assert.DoesNotContain(raw, t => t.Contains("🗑"));
        Assert.DoesNotContain(raw, t => t.Contains("📝 编辑"));
        Assert.DoesNotContain(raw, t => t.Contains("❌ 删除"));

        // 实体分区：上下文动作 = 「＋ 新建角色」文字按钮 → 直接新建该类型（novelId, null）
        var context = panel
            .Visual.EnumerateVisualsDepthFirst()
            .OfType<Button>()
            .Single(b => Labels(b).Any(t => t.Contains("新建")));
        string? createdFor = "sentinel";
        panel.OnAddCharacter += (novelId, id) => createdFor = $"{novelId}/{id ?? "null"}";
        ClickButton(context);
        Assert.Equal("n1/null", createdFor);

        // 空框时清空键隐藏：搜索框吃掉右侧按钮之外的整行宽度（右侧 = 「＋ 新建」+「🔍 筛选」）
        var searchBox = GetPrivate<TextBox>(panel, "_searchBox");
        var clearButton = GetPrivate<Button>(panel, "_clearSearchButton");
        Assert.False(clearButton.IsVisible);
        Assert.True(
            context.Bounds.X - searchBox.Bounds.Right <= 3,
            $"搜索框右缘 {searchBox.Bounds.Right} 到「＋ 新建」左缘 {context.Bounds.X} 之间不应留大段空白"
        );
        // 搜索框与右侧按钮必须同一行（用户报障：给搜索框外包 Border 的那版，Border 的
        // Inset 让这一行变成 3 行高，而按钮默认顶对齐 → 按钮落在上边框行、搜索文字在中间行，
        // 「🔍 筛选」看起来不垂直居中）。撤掉这行断言即红。
        Assert.Equal(searchBox.Bounds.Y, context.Bounds.Y);

        // 大纲分区：「＋ 新建」隐藏（书级新建在树节点 ＋ / ⋯ 上），「🔍 筛选」常驻（章节筛选）
        panel.NavigateToEntity(EntityType.Chapter, "ch1");
        Assert.False(GetPrivate<Button>(panel, "_contextButton").IsVisible);
        Assert.True(GetPrivate<Button>(panel, "_filterButton").IsVisible);
    }

    /// <summary>实体筛选匹配规则（纯函数，可单测）：空条件全通过；两个槽位是 AND 语义。</summary>
    [Theory]
    [InlineData(null, null, "Protagonist", "男", true)]
    [InlineData("Protagonist", null, "Protagonist", "女", true)]
    [InlineData("Protagonist", null, "Minor", "男", false)]
    [InlineData(null, "女", "Minor", "女", true)]
    [InlineData(null, "女", "Minor", "男", false)]
    [InlineData("Protagonist", "女", "Protagonist", "女", true)]
    [InlineData("Protagonist", "女", "Protagonist", null, false)]
    public void EntityListFilter_Matches_AndSemantics(
        string? primary,
        string? secondary,
        string actualPrimary,
        string? actualSecondary,
        bool expected
    )
    {
        var filter = new EntityListFilter(primary, secondary);
        Assert.Equal(expected, filter.Matches(actualPrimary, actualSecondary));
    }

    /// <summary>
    /// 工具带按钮（用户报障：实体 tab 少了「🔍 筛选」，右侧只有「＋ 新建XX」）：
    /// 实体分区 = 「＋ 新建」（不带类型名）+「🔍 筛选」（该类型的实体筛选）并排；
    /// 大纲分区 = 只有「🔍 筛选」。
    /// </summary>
    [Fact]
    public async Task ToolBand_EntitySection_HasPlainNewAndFilterButtons()
    {
        var store = new FakeStore
        {
            Novel = new Novel { Id = "n1", Title = "按钮" },
        };
        store.Characters.Add(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "阿黎",
                Role = CharacterRole.Protagonist,
                Gender = "女",
            }
        );
        var panel = new SetUpPanel(store, PanelThemes.Dark) { CurrentNovelId = "n1" };
        await panel.RefreshAsync("n1");
        panel.NavigateToEntity(EntityType.Character, "c1");
        ArrangePanel(panel);

        var newButton = GetPrivate<Button>(panel, "_contextButton");
        var filterButton = GetPrivate<Button>(panel, "_filterButton");
        Assert.True(newButton.IsVisible);
        Assert.True(filterButton.IsVisible);
        Assert.Equal("＋ 新建", string.Join("", Labels(newButton)).Trim()); // 不带「角色」
        Assert.Contains("🔍 筛选", string.Join("", Labels(filterButton)));

        // 点「＋ 新建」= 新建当前类型（novelId, null）
        string? createdFor = "sentinel";
        panel.OnAddCharacter += (novelId, id) => createdFor = $"{novelId}/{id ?? "null"}";
        ClickButton(newButton);
        Assert.Equal("n1/null", createdFor);

        // 大纲分区：新建按钮隐藏（书级新建在树节点），筛选按钮常驻
        panel.NavigateToEntity(EntityType.Chapter, "ch1");
        ArrangePanel(panel);
        Assert.False(newButton.IsVisible);
        Assert.True(filterButton.IsVisible);
    }

    /// <summary>
    /// 实体筛选：条件在行数上限裁剪之前应用（第 61 条以后的命中也必须筛得到），
    /// 命中数写进搜索框占位；清除恢复全量。
    /// </summary>
    [Fact]
    public async Task EntityFilter_AppliesBeforeRowCap_AndShowsHitCount()
    {
        var store = new FakeStore
        {
            Novel = new Novel { Id = "n1", Title = "筛选" },
        };
        for (var i = 1; i <= 70; i++)
            store.Characters.Add(
                new Character
                {
                    Id = $"c{i}",
                    NovelId = "n1",
                    Name = $"角色{i}",
                    Role = i % 2 == 0 ? CharacterRole.Protagonist : CharacterRole.Minor,
                    Gender = i % 2 == 0 ? "男" : "女",
                }
            );
        var panel = new SetUpPanel(store, PanelThemes.Dark) { CurrentNovelId = "n1" };
        await panel.RefreshAsync("n1");
        panel.NavigateToEntity(EntityType.Character, "c1");
        ArrangePanel(panel);

        // 35 个「主角」：默认受 60 行上限影响看不到全部，筛选后必须全部可见
        panel.ApplyEntityFilter(EntityType.Character, new EntityListFilter("Protagonist", null));
        ArrangePanel(panel);

        var accordion = GetCharacterAccordion(panel);
        Assert.Equal(35, accordion.Children.Count); // 无「还有 N 条」行（35 < 上限）
        Assert.DoesNotContain(
            accordion.Children,
            child => child is Markup { Text: not null } m && m.Text.Contains("还有")
        );
        var placeholder = GetPrivate<TextBox>(panel, "_searchBox").Placeholder ?? "";
        Assert.Contains("筛选中 35/70", placeholder);

        // 二级维度（性别）+ 清除：再筛「女」= 只有 35 个「龙套」女角色
        panel.ApplyEntityFilter(EntityType.Character, new EntityListFilter(null, "女"));
        ArrangePanel(panel);
        Assert.Equal(35, GetCharacterAccordion(panel).Children.Count);

        panel.ApplyEntityFilter(EntityType.Character, EntityListFilter.None);
        ArrangePanel(panel);
        Assert.Equal(
            60 + 1, // 60 行 + 「… 还有 10 条」行
            GetCharacterAccordion(panel).Children.Count
        );
        Assert.Contains(
            "（名字 / 关键词）",
            GetPrivate<TextBox>(panel, "_searchBox").Placeholder ?? ""
        );
    }

    /// <summary>
    /// 主题切换：整面板的构建时快照配色必须立即换成新主题色（用户报障：输入区域色块不随主题切换）。
    /// 快照点：搜索框填充（TextBoxStyle）、清空/上下文/云端行按钮（ButtonStyle）、
    /// 列表滚动条（ScrollViewerStyle）、大纲树树线（TreeViewStyle）与树行内容（⋯ 芯片 / ＋ 按钮）。
    /// 树行内容要在切主题后由随后的 RefreshAsync 重建（TuiApp 的刷新路径），所以这里也调一次。
    /// </summary>
    [Fact]
    public async Task ApplyTheme_RefreshesPanelChromeColors()
    {
        var store = new FakeStore
        {
            Novel = new Novel { Id = "n1", Title = "主题测试书" },
        };
        var panel = new SetUpPanel(store, PanelThemes.Dark) { CurrentNovelId = "n1" };
        await panel.RefreshAsync("n1");
        panel.SetCloudConfigured(true); // 云端行按钮（⇅ 同步 / ↓ 拉取 / 🔍 检查）也在快照清单里

        var darkPanel = PanelMarkup(panel);
        Assert.Contains(
            OnHex(PanelThemes.Dark.InputFill),
            darkPanel,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            OnHex(PanelThemes.Dark.Border),
            darkPanel,
            StringComparison.OrdinalIgnoreCase
        );

        panel.ApplyTheme(PanelThemes.Light);
        await panel.RefreshAsync("n1"); // 镜像 App：切主题后 RefreshOutline 会重建大纲树行

        var lightPanel = PanelMarkup(panel);
        Assert.Contains(
            OnHex(PanelThemes.Light.InputFill),
            lightPanel,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            OnHex(PanelThemes.Light.Border),
            lightPanel,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            OnHex(PanelThemes.Dark.InputFill),
            lightPanel,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            OnHex(PanelThemes.Dark.Border),
            lightPanel,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            OnHex(PanelThemes.Dark.SidePanelBg),
            lightPanel,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            OnHex(PanelThemes.Dark.SidePanelFg),
            lightPanel,
            StringComparison.OrdinalIgnoreCase
        );

        // 树行内容与配色也要在（防两类假绿：树没重建 / 重建但树空了）。离屏渲染不保证重建行的
        // 排版（真实 App 下由布局循环重建，已用实机帧验证标题与行菜单照常渲染），
        // 所以这里用模型文本 + 行动作按钮（＋ / ⋯，色在 ButtonStyle 里而不是 markup 文本里）钉住。
        Assert.Contains(
            CollectTexts(panel.Visual),
            t => t.Contains("主题测试书", StringComparison.Ordinal)
        );
        var treeButtons = panel.Visual.EnumerateVisualsDepthFirst().OfType<Button>().ToList();
        var menuChip = treeButtons.First(b =>
            Labels(b).Any(t => t.Contains("⋯", StringComparison.Ordinal))
        );
        Assert.Equal(
            ThemeStyles.BuildButton(PanelThemes.Light).Normal,
            menuChip.GetStyle<XenoAtom.Terminal.UI.Styling.ButtonStyle>().Normal
        );
        var addButton = treeButtons.First(b =>
            Labels(b).Any(t => t.Contains("＋", StringComparison.Ordinal))
        );
        Assert.Equal(
            ThemeStyles.BuildButtonAccent(PanelThemes.Light).Normal,
            addButton.GetStyle<XenoAtom.Terminal.UI.Styling.ButtonStyle>().Normal
        );
    }

    /// <summary>把整块面板渲染成 markup 文本（主题切换断言用；颜色以小写 `on #RRGGBB` 出现）。</summary>
    private static string PanelMarkup(SetUpPanel panel)
    {
        ArrangePanel(panel);
        return string.Join(
            "\n",
            XenoAtom
                .Terminal.UI.Rendering.VisualSnapshotRenderer.Render(
                    panel.Visual,
                    PanelWidth,
                    PanelHeight
                )
                .ToMarkupLines()
        );
    }

    /// <summary>颜色 → markup 里的 `on #RRGGBB` 匹配串（markup 输出小写，忽略大小写比较）。</summary>
    private static string OnHex(Color c) => $"on #{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>按钮内容里的纯文本（剥离 Markup 样式标签）。</summary>
    private static IEnumerable<string> Labels(Visual button) =>
        button
            .EnumerateVisualsDepthFirst()
            .OfType<Markup>()
            .Select(m =>
                System.Text.RegularExpressions.Regex.Replace(m.Text ?? "", @"\[[^\[\]]*\]", "")
            );

    /// <summary>收集视觉树中全部文本（Markup/TextBlock）；剥离 Markup 样式标签，返回纯文本便于断言。</summary>
    internal static List<string> CollectTexts(Visual root)
    {
        var texts = new List<string>();
        foreach (var visual in root.EnumerateVisualsDepthFirst())
        {
            var text = visual switch
            {
                Markup markup => markup.Text,
                TextBlock textBlock => textBlock.Text,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text))
                continue;
            texts.Add(System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\[\]]*\]", ""));
        }
        return texts;
    }

    /// <summary>原始 Markup 文本（不剥标签）——用于断言转义形态（[[red]] 等倍增写法）。</summary>
    internal static List<string> CollectRawMarkup(Visual root)
    {
        var texts = new List<string>();
        foreach (var visual in root.EnumerateVisualsDepthFirst())
        {
            var text = visual switch
            {
                Markup markup => markup.Text,
                TextBlock textBlock => textBlock.Text,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add(text);
        }
        return texts;
    }

    private static Visual ArrangePanel(SetUpPanel panel)
    {
        var visual = panel.Visual;
        visual.Measure(new LayoutConstraints(0, PanelWidth, 0, 9999));
        visual.Arrange(new Rectangle(0, 0, PanelWidth, PanelHeight));
        return visual;
    }

    private static List<Character> MakeCharacters(int count)
    {
        var chars = new List<Character>();
        for (var i = 1; i <= count; i++)
        {
            chars.Add(
                new Character
                {
                    Id = $"c{i}",
                    Name = $"角色{i}号",
                    Role = CharacterRole.Supporting,
                    Gender = "女",
                    Personality = $"性格描述{i}：冷静果断，外冷内热。",
                }
            );
        }
        return chars;
    }

    internal static Accordion GetCharacterAccordion(SetUpPanel panel)
    {
        var field = typeof(SetUpPanel).GetField(
            "_characterAccordion",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (Accordion)field.GetValue(panel)!;
    }

    /// <summary>
    /// 全部折叠：首项必须出现在内容区顶部（工具栏下方），不能有大段空白后被挤到底部。
    /// </summary>
    [Fact]
    public async Task CollapsedItems_StartNearTop_NotPushedToBottom()
    {
        var store = new FakeStore();
        store.Characters.AddRange(MakeCharacters(5));
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        panel.NavigateToEntity(EntityType.Character, "c1"); // 大纲排第一了，实体分区要显式切
        ArrangePanel(panel);

        var accordion = GetCharacterAccordion(panel);
        Assert.Equal(5, accordion.Children.Count);

        // XenoAtom 的 Bounds 是根坐标系（Arrange 逐层传绝对 rect），
        // 直接用 Bounds 而不是 GetAbsoluteBounds()（库内双重计数祖先偏移）。
        // 顶部结构：搜索行(1) + 分区间隔 + tab strip(1) + padding(1) + toolbar(1) + spacing = 前若干行；
        // 首项若被推到第 10 行以后，说明工具栏/列表被拉伸，布局不再紧凑。
        var firstY = accordion.Children[0].Bounds.Y;
        Assert.True(
            firstY <= 8,
            $"首项 Y={firstY}，应 ≤8（紧凑排布应紧贴工具栏下方），面板可能被拉伸"
        );

        // 折叠项必须单行紧凑：相邻两项间距为 1 行
        var secondY = accordion.Children[1].Bounds.Y;
        Assert.Equal(1, secondY - firstY);
    }

    /// <summary>
    /// 展开一项：展开项保持自然高度（头 + 内容区），后续折叠项仍留在可视区域内，
    /// 不能被展开项挤出面板底部。
    /// </summary>
    [Fact]
    public async Task ExpandedItem_KeepsNaturalHeight_SiblingsStayVisible()
    {
        var store = new FakeStore();
        store.Characters.AddRange(MakeCharacters(5));
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        panel.SetHighlightEntity(EntityType.Character, "c2");
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        var accordion = GetCharacterAccordion(panel);
        Assert.Equal(5, accordion.Children.Count);
        Assert.True(((Collapsible)accordion.Children[1]).IsExpanded);

        // 展开项高度 = 头 1 行 + 内容区 12 行（MinHeight），最多允许 1 行误差；
        // 若 Accordion 被 Stretch，展开项会吸收全部剩余高度（>30 行）。
        var expandedBounds = accordion.Children[1].Bounds;
        Assert.True(
            expandedBounds.Height <= 14,
            $"展开项高度={expandedBounds.Height}，应 ≤14（自然高度），Accordion 被拉伸了"
        );

        // 最后一个折叠项必须完整落在面板可视区内（Bounds 为根坐标）
        var lastBounds = accordion.Children[^1].Bounds;
        Assert.True(
            lastBounds.Bottom <= PanelHeight,
            $"最后一项 bottom={lastBounds.Bottom} > 面板高度 {PanelHeight}，被挤出可视区"
        );
    }

    private static (Grid Header, Visual Name, Visual Actions) GetHeaderParts(
        Collapsible collapsible
    )
    {
        var header = collapsible.Header;
        Assert.True(header is Grid, $"header 类型={header?.GetType().Name}，应为 Grid");
        var grid = (Grid)header!;
        var name = grid.Cells[0].Content!;
        var actions = grid.Cells[1].Content!;
        return (grid, name, actions);
    }

    /// <summary>
    /// 头部操作标签（📝 📋 📜）必须靠右对齐：行占满整宽、
    /// 标签贴近右缘、且所有行的标签 X 完全一致（统一对齐）。
    /// </summary>
    [Fact]
    public async Task EntityRow_ActionLabels_RightAligned_UniformAcrossRows()
    {
        var store = new FakeStore();
        store.Characters.AddRange(MakeCharacters(5));
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        panel.NavigateToEntity(EntityType.Character, "c1"); // 大纲排第一了，实体分区要显式切
        ArrangePanel(panel);

        var accordion = GetCharacterAccordion(panel);
        var (header, name, actions) = GetHeaderParts((Collapsible)accordion.Children[0]);

        // 行必须占满整行宽（面板 46 − 左右 padding 各 1 = 44）
        var rowWidth = accordion.Children[0].Bounds.Width;
        Assert.True(
            rowWidth >= PanelWidth - 4,
            $"行宽={rowWidth}，应占满整行（面板宽 {PanelWidth}）"
        );

        // 操作标签在名字右侧，且贴近行右缘
        Assert.True(
            actions.Bounds.X > name.Bounds.X + name.Bounds.Width,
            $"actions.X={actions.Bounds.X} name.X={name.Bounds.X}+{name.Bounds.Width} "
                + $"header={header.Bounds} collapsible={accordion.Children[0].Bounds}"
        );
        var rowRight = accordion.Children[0].Bounds.X + rowWidth;
        var actionsRight = actions.Bounds.X + actions.Bounds.Width;
        Assert.True(
            actionsRight >= rowRight - 6,
            $"操作标签右缘={actionsRight}，行右缘={rowRight}，应靠右对齐"
        );

        // 统一对齐：每一行的操作标签 X 必须一致
        var expectedX = actions.Bounds.X;
        for (var i = 1; i < accordion.Children.Count; i++)
        {
            var (_, _, otherActions) = GetHeaderParts((Collapsible)accordion.Children[i]);
            Assert.Equal(expectedX, otherActions.Bounds.X);
        }
    }

    /// <summary>行尾 ⋯ 菜单标签（反射调用私有构建器，取 ValueTuple.Item1）。</summary>
    private static List<string> RowMenuLabels(SetUpPanel panel, OutlineRow row)
    {
        var method = typeof(SetUpPanel).GetMethod(
            "BuildOutlineRowMenuItems",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var items = (System.Collections.IEnumerable)method.Invoke(panel, [row])!;
        var labels = new List<string>();
        foreach (var item in items)
            labels.Add((string)item!.GetType().GetField("Item1")!.GetValue(item)!);
        return labels;
    }

    /// <summary>调用带参私有实例方法。</summary>
    private static void InvokePrivateWith(object target, string method, params object?[] args) =>
        typeof(SetUpPanel)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    // ── 大纲树（TreeView）测试缝 ──

    /// <summary>大纲树控件（反射）。</summary>
    private static TreeView GetOutlineTree(SetUpPanel panel) =>
        GetPrivate<TreeView>(panel, "_outlineTree");

    /// <summary>小说根节点。</summary>
    private static TreeNode NovelNode(SetUpPanel panel) => GetOutlineTree(panel).Roots[0];

    /// <summary>节点右侧动作按钮（Always 可见的那些）。</summary>
    private static List<Button> NodeActionButtons(TreeNode node) =>
        [.. node.RightVisuals.Select(rv => rv.Visual).OfType<Button>()];

    /// <summary>节点右侧动作按钮的文案（去掉 markup 标签）。</summary>
    private static List<string> NodeActionLabels(TreeNode node) =>
        [.. NodeActionButtons(node).Select(b => (b.Content as Markup)?.Text ?? "")];

    /// <summary>动作按钮文案（去 markup 标签；用于按文案挑按钮）。</summary>
    private static string Label(Button button) => (button.Content as Markup)?.Text ?? "";

    /// <summary>触发按钮 Click（框架 RaiseEvent 是 protected internal，测试经反射调用）。</summary>
    private static void ClickButton(Button button)
    {
        var clickEvent = typeof(Button)
            .GetField(
                "ClickEvent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            )!
            .GetValue(null)!;
        typeof(Visual)
            .GetMethod(
                "RaiseEvent",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            )!
            .MakeGenericMethod(typeof(XenoAtom.Terminal.UI.Input.ClickEventArgs))
            .Invoke(button, [clickEvent, new XenoAtom.Terminal.UI.Input.ClickEventArgs()]);
    }

    /// <summary>按标题找节点（深度优先）。</summary>
    /// <summary>触发行 ⋯ 菜单里第一个含 <paramref name="labelFragment"/> 的菜单项动作。</summary>
    private static void InvokeRowMenuItem(SetUpPanel panel, OutlineRow row, string labelFragment)
    {
        var method = typeof(SetUpPanel).GetMethod(
            "BuildOutlineRowMenuItems",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var items = (System.Collections.IEnumerable)method.Invoke(panel, [row])!;
        foreach (var item in items)
        {
            var type = item.GetType();
            var label = (string)type.GetField("Item1")!.GetValue(item)!;
            if (!label.Contains(labelFragment, StringComparison.Ordinal))
                continue;
            ((Action)type.GetField("Item2")!.GetValue(item)!)();
            return;
        }
        Assert.Fail($"⋯ 菜单里没有含「{labelFragment}」的项");
    }

    private static TreeNode? FindNode(TreeNode root, string textFragment)
    {
        if (NodeHeaderText(root).Contains(textFragment, StringComparison.Ordinal))
            return root;
        foreach (var child in root.Children)
        {
            var hit = FindNode(child, textFragment);
            if (hit is not null)
                return hit;
        }
        return null;
    }

    /// <summary>节点标题的纯文本（还原 Markup 的 [[ / ]] 转义；无则空串）。</summary>
    /// <summary>卷节点（按行类型，不靠标题里的图标字形）。</summary>
    private static bool IsVolumeNode(TreeNode node) =>
        node.Data is OutlineRow { Kind: OutlineRowKind.VolumeHeader };

    private static string NodeHeaderText(TreeNode node) =>
        node.Header is Markup markup ? markup.Text.Replace("[[", "[").Replace("]]", "]") : "";

    /// <summary>节点序号（与 Desktop 同构的兄弟序）。</summary>
    private static int IndexOfNode(TreeNode parent, TreeNode node)
    {
        for (var i = 0; i < parent.Children.Count; i++)
            if (ReferenceEquals(parent.Children[i], node))
                return i;
        return -1;
    }

    /// <summary>当前展开的卷集合（测试驱动钻取状态）。</summary>
    private static HashSet<string> ExpandedVolumesOf(SetUpPanel panel) =>
        GetPrivate<HashSet<string>>(panel, "_expandedVolumes");

    private static T GetPrivate<T>(SetUpPanel panel, string field) =>
        (T)
            typeof(SetUpPanel)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel)!;

    /// <summary>大纲夹具：1 卷 + 1 卷内章 + 1 未分卷章；返回已刷新并排版的面板。</summary>
    private static async Task<(FakeStore Store, SetUpPanel Panel)> ArrangeOutlineFixtureAsync()
    {
        var store = new FakeStore
        {
            Novel = new Novel
            {
                Id = "n1",
                NovelId = "n1",
                Title = "结构操作",
                Outline = "走向",
            },
        };
        store.Volumes.Add(
            new Volume
            {
                Id = "vol1",
                NovelId = "n1",
                Name = "风起",
                Order = 1,
            }
        );
        store.Chapters.Add(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "雨夜寄北",
                Order = 1,
                Content = "正文",
                VolumeId = "vol1",
            }
        );
        store.Chapters.Add(
            new Chapter
            {
                Id = "ch2",
                NovelId = "n1",
                Title = "未分卷章",
                Order = 2,
                Content = "正文",
            }
        );

        var panel = new SetUpPanel(store, PanelThemes.Dark);
        panel.CurrentNovelId = "n1";
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);
        return (store, panel);
    }

    /// <summary>
    /// 大纲树（TreeView）三层：根 = 当前小说，二层 = 卷，三层 = 章（卷节点未展开也物化，展开是控件行为）。
    /// </summary>
    [Fact]
    public async Task OutlineTree_RendersNovelVolumeChapterHierarchy()
    {
        var (store, panel) = await ArrangeOutlineFixtureAsync();
        store.Chapters.Add(
            new Chapter
            {
                Id = "ch3",
                NovelId = "n1",
                Title = "再会",
                Order = 3,
                VolumeId = "vol1",
            }
        );
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        var novel = NovelNode(panel);
        Assert.Contains("结构操作", NodeHeaderText(novel), StringComparison.Ordinal);
        // 图标不再进标题文本：层级图标由 IconResolver 放进框架图标槽（见 OutlineTree_LevelIcons_PerKind）
        Assert.Contains(
            novel.Children,
            child => NodeHeaderText(child).Contains("1 卷 · 3 章", StringComparison.Ordinal)
        );
        Assert.Contains(
            novel.Children,
            child => NodeHeaderText(child).Contains("总纲：走向", StringComparison.Ordinal)
        );

        var volumeNodes = novel.Children.Where(n => IsVolumeNode(n)).ToList();
        Assert.True(
            volumeNodes.Count == 1,
            $"children={novel.Children.Count} texts={string.Join(" | ", novel.Children.Select(NodeHeaderText))}"
        );
        var volume = volumeNodes[0];
        Assert.Contains("第1卷 · 风起", NodeHeaderText(volume), StringComparison.Ordinal);
        var chapters = volume.Children.Where(n => NodeHeaderText(n).Contains("雨夜寄北")).ToList();
        Assert.Single(chapters);
        // 章节号按卷内自增（换卷从 1 重新计数）：这里只有一卷，首章即「第 1 章」
        Assert.Contains("第 1 章", NodeHeaderText(chapters[0]), StringComparison.Ordinal);

        // 未分卷组：组头 + 其下章节点
        var unassigned = novel.Children.Single(n => NodeHeaderText(n).Contains("未分卷"));
        Assert.Contains(
            unassigned.Children,
            n => NodeHeaderText(n).Contains("未分卷章", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// 层级图标只由行类型决定（框架的节点图标槽）：小说 📖 / 卷 📦 / 未分卷 📄 / 章 = 状态圆点。
    /// 标题文本里不再拼 emoji —— 否则会和框架默认图标叠成两个（用户报障）。
    /// </summary>
    [Fact]
    public async Task OutlineTree_LevelIcons_PerKind()
    {
        var (_, panel) = await ArrangeOutlineFixtureAsync();
        var novel = NovelNode(panel);
        Assert.Equal(new Rune(0x1F4D6), SetUpPanel.OutlineIcon((OutlineRow)novel.Data!)); // 📖

        var volume = novel.Children.First(IsVolumeNode);
        // 卷 / 未分卷统一 folder 图标（用户报障：卷该用 folder）
        Assert.Equal(new Rune(0x1F4C1), SetUpPanel.OutlineIcon((OutlineRow)volume.Data!)); // 📁

        var chapter = FindNode(novel, "雨夜寄北");
        Assert.NotNull(chapter);
        // 草稿章：📝（原 ⚪ 圆点已改为按发布状态分的图标）
        Assert.Equal(new Rune(0x1F4DD), SetUpPanel.OutlineIcon((OutlineRow)chapter!.Data!)); // 📝

        var unassigned = novel.Children.First(n =>
            n.Data is OutlineRow { Kind: OutlineRowKind.UnassignedHeader }
        );
        Assert.Equal(new Rune(0x1F4C1), SetUpPanel.OutlineIcon((OutlineRow)unassigned.Data!)); // 📁

        // 标题文本里不得再出现这些图标字形
        foreach (var node in new[] { novel, volume, chapter })
            Assert.DoesNotContain("📖", NodeHeaderText(node), StringComparison.Ordinal);
    }

    /// <summary>
    /// 小说/卷节点提供下一层新建与菜单；未分卷组只有新建入口；章节点只有菜单。
    /// ⋯ 菜单按层级收敛：书/卷 = 详情·检查·删除；章 = 细纲·内容·删除。
    /// </summary>
    [Fact]
    public async Task OutlineTree_NodeActions_PerLevel()
    {
        var (_, panel) = await ArrangeOutlineFixtureAsync();

        var novel = NovelNode(panel);
        Assert.Equal(["＋", "⋯"], NodeActionLabels(novel));

        var volume = novel.Children.Single(n => IsVolumeNode(n));
        Assert.Equal(["＋", "⋯"], NodeActionLabels(volume));
        var unassigned = novel.Children.Single(n =>
            n.Data is OutlineRow { Kind: OutlineRowKind.UnassignedHeader }
        );
        Assert.Equal(["＋"], NodeActionLabels(unassigned));

        var chapter = FindNode(novel, "雨夜寄北");
        Assert.NotNull(chapter);
        Assert.Equal(["⋯"], NodeActionLabels(chapter!));

        // ⋯ 菜单按层级收敛：书 / 卷 = 时间线 · 详情 · 删除；章 = 时间线 · 发布|撤回 · 详情 · 内容 · 删除
        // （书/卷的「🔍 检查」入口按用户要求移除——设定检查改由工具带等处以书级动作进入）
        Assert.Equal(
            ["🧾 时间线", "📄 详情", "❌ 删除"],
            RowMenuLabels(panel, (OutlineRow)novel.Data!)
        );

        var volumeMenu = RowMenuLabels(panel, (OutlineRow)volume.Data!);
        Assert.Equal(["🧾 时间线", "📄 详情", "❌ 删除"], volumeMenu);

        var chapterRow = (OutlineRow)chapter!.Data!;
        // 草稿章：可发布；已发布章才给「撤回」（见 OutlineTree_PublishMenu_FollowsChapterStatus）
        Assert.Equal(
            ["🧾 时间线", "📤 发布", "📄 详情", "📖 内容", "❌ 删除"],
            RowMenuLabels(panel, chapterRow)
        );
    }

    /// <summary>删除走 ⋯ 菜单项 → 宿主事件（确认弹窗由宿主 TuiApp 统一处理，防误删）。</summary>
    [Fact]
    public async Task OutlineTree_DeleteMenuItems_RaiseHostEvents()
    {
        var (_, panel) = await ArrangeOutlineFixtureAsync();
        panel.CurrentNovelId = "n1";

        var deletedNovels = new List<string?>();
        var deletedVolumes = new List<string?>();
        var deletedChapters = new List<string?>();
        panel.OnDeleteNovel += (_, id) => deletedNovels.Add(id);
        panel.OnDeleteVolume += (_, id) => deletedVolumes.Add(id);
        panel.OnDeleteChapter += (_, id) => deletedChapters.Add(id);

        var novel = NovelNode(panel);
        var volume = novel.Children.Single(n => IsVolumeNode(n));
        var chapter = FindNode(novel, "雨夜寄北")!;

        InvokeRowMenuItem(panel, (OutlineRow)novel.Data!, "删除");
        InvokeRowMenuItem(panel, (OutlineRow)volume.Data!, "删除");
        InvokeRowMenuItem(panel, (OutlineRow)chapter.Data!, "删除");

        Assert.Equal(["n1"], deletedNovels);
        Assert.Equal(["vol1"], deletedVolumes);
        Assert.Equal(["ch1"], deletedChapters);
    }

    /// <summary>
    /// 章行状态动作随发布状态切换：草稿给「📤 发布」、已发布给「↩️ 撤回」，
    /// 两者都只经宿主事件（发布/撤回事务由 TuiApp 调 ChapterLifecycleService）；
    /// 时间线入口按层级指向对应实体（小说/卷/章都可查看本体时间线）。
    /// </summary>
    [Fact]
    public async Task OutlineTree_PublishMenu_FollowsChapterStatus_AndRaisesHostEvents()
    {
        var (store, panel) = await ArrangeOutlineFixtureAsync();
        panel.CurrentNovelId = "n1";

        var published = new List<string>();
        var unpublished = new List<string>();
        var timelines = new List<(EntityType Type, string Id)>();
        panel.OnPublishChapter += id => published.Add(id);
        panel.OnUnpublishChapter += id => unpublished.Add(id);
        panel.OnViewAuditLog += (type, id) => timelines.Add((type, id));

        var chapterRow = (OutlineRow)FindNode(NovelNode(panel), "雨夜寄北")!.Data!;
        Assert.Equal(ChapterStatus.Draft, panel.ChapterStatusOf(chapterRow));
        Assert.Contains("📤 发布", RowMenuLabels(panel, chapterRow));
        Assert.DoesNotContain("↩ 撤回", RowMenuLabels(panel, chapterRow));

        InvokeRowMenuItem(panel, chapterRow, "发布");
        Assert.Equal(["ch1"], published);

        // 已发布章：菜单换成撤回，发布项消失
        store.Chapters.Single(c => c.Id == "ch1").Status = ChapterStatus.Published;
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        var publishedRow = (OutlineRow)FindNode(NovelNode(panel), "雨夜寄北")!.Data!;
        Assert.Equal(ChapterStatus.Published, panel.ChapterStatusOf(publishedRow));
        var labels = RowMenuLabels(panel, publishedRow);
        Assert.Contains("↩ 撤回", labels);
        Assert.DoesNotContain("📤 发布", labels);

        InvokeRowMenuItem(panel, publishedRow, "撤回");
        Assert.Equal(["ch1"], unpublished);

        // 时间线入口：章行 → 章实体；小说行 → 小说实体
        InvokeRowMenuItem(panel, publishedRow, "时间线");
        InvokeRowMenuItem(panel, (OutlineRow)NovelNode(panel).Data!, "时间线");
        Assert.Contains((EntityType.Chapter, "ch1"), timelines);
        Assert.Contains((EntityType.Novel, "n1"), timelines);
    }

    /// <summary>＋ 按钮：小说行 = 新建分卷（volumeId null），卷行 = 该卷新建章节。</summary>
    [Fact]
    public async Task OutlineTree_AddButtons_TargetTheRightLevel()
    {
        var (_, panel) = await ArrangeOutlineFixtureAsync();
        panel.CurrentNovelId = "n1";

        var addedVolumes = new List<(string? Novel, string? Volume)>();
        var addedChapters = new List<(string? Novel, string? Volume)>();
        panel.OnAddVolume += (n, v) => addedVolumes.Add((n, v));
        panel.OnAddChapter += (n, v) => addedChapters.Add((n, v));

        var novel = NovelNode(panel);
        var volume = novel.Children.Single(n => IsVolumeNode(n));
        ClickButton(NodeActionButtons(novel).Single(b => Label(b) == "＋"));
        ClickButton(NodeActionButtons(volume).Single(b => Label(b) == "＋"));

        Assert.Equal([("n1", null)], addedVolumes);
        Assert.Equal([("n1", "vol1")], addedChapters);

        static string Label(Button b) => (b.Content as Markup)?.Text ?? "";
    }

    /// <summary>搜索 / 筛选：只保留命中的章节点（模糊匹配在 Core 行构建里）。</summary>
    [Fact]
    public async Task OutlineTree_Search_NarrowsChapters()
    {
        var (_, panel) = await ArrangeOutlineFixtureAsync();

        panel.ApplySearchText("雨夜");
        ArrangePanel(panel);

        var novel = NovelNode(panel);
        Assert.NotNull(FindNode(novel, "雨夜寄北"));
        Assert.Null(FindNode(novel, "未分卷章"));

        // 清空恢复
        panel.ApplySearchText("");
        ArrangePanel(panel);
        Assert.NotNull(FindNode(NovelNode(panel), "未分卷章"));
    }

    /// <summary>创建后定位：RevealChapter 展开所在卷并选中该章节点。</summary>
    [Fact]
    public async Task OutlineTree_RevealChapter_ExpandsAncestorAndSelects()
    {
        var (_, panel) = await ArrangeOutlineFixtureAsync();

        panel.RevealChapter("ch1");
        Assert.Contains("vol1", ExpandedVolumesOf(panel));

        var volume = NovelNode(panel).Children.Single(n => IsVolumeNode(n));
        Assert.True(volume.IsExpanded);
        Assert.Contains(
            "雨夜寄北",
            NodeHeaderText(GetOutlineTree(panel).SelectedNode ?? new TreeNode(new Markup(""))),
            StringComparison.Ordinal
        );
    }

    /// <summary>节点标题按原文渲染：名字含方括号时不出现 markup 字面量。</summary>
    [Fact]
    public async Task OutlineTree_NodeHeaders_RenderBracketedNamesVerbatim()
    {
        var store = new FakeStore();
        store.Novel = new Novel { Id = "n1", Title = "测试[上]" };
        store.Volumes.Add(
            new Volume
            {
                Id = "v1",
                NovelId = "n1",
                Name = "卷[一]",
                Order = 1,
            }
        );
        store.Chapters.Add(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "[系统]觉醒",
                Order = 1,
                VolumeId = "v1",
                Summary = "细纲[一]",
            }
        );
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);

        var novel = NovelNode(panel);
        // 节点标题把取值按 Markup 转义规则处理（[[ / ]]），不会被当成样式标签吞掉
        Assert.Contains("测试[上]", NodeHeaderText(novel), StringComparison.Ordinal);
        Assert.Contains(
            "卷[一]",
            NodeHeaderText(novel.Children.First(IsVolumeNode)),
            StringComparison.Ordinal
        );
        Assert.NotNull(FindNode(novel, "[系统]觉醒"));

        // 渲染不崩（离屏画一帧，确保转义文本可被控件解析）
        Assert.NotEmpty(RenderHeaderText(novel));
    }

    /// <summary>
    /// 把节点标题的 markup 文本离屏渲染成纯文本（断言「屏上真正看到什么」）：
    /// 渲染器对文本里的方括号再转义为 [[ / ]]，样式令牌则是单个 [..]——按此规则逐字符还原。
    /// </summary>
    private static string RenderHeaderText(TreeNode node)
    {
        var raw = XenoAtom
            .Terminal.UI.Rendering.VisualSnapshotRenderer.Render(
                new Markup(NodeHeaderText(node)) { IsSelectable = false },
                80,
                1
            )
            .ToMarkupLines()[0];
        var text = new System.Text.StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '[' && i + 1 < raw.Length && raw[i + 1] == '[')
            {
                text.Append('[');
                i++;
                continue;
            }
            if (raw[i] == ']' && i + 1 < raw.Length && raw[i + 1] == ']')
            {
                text.Append(']');
                i++;
                continue;
            }
            if (raw[i] == '[')
            {
                while (i < raw.Length && raw[i] != ']')
                    i++;
                continue;
            }
            text.Append(raw[i]);
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// 规模探针（1259 章 / 60 卷）：整树构建（节点 + 行模型过滤）需在预算内完成，
    /// 且根节点下 60 个卷节点、巨卷下 1200 个章节点齐备（展开是控件行为，不再分页重建）。
    /// </summary>
    [Fact]
    public async Task OutlineTree_ThousandsOfChapters_BuildsUnderBudget()
    {
        const int volumeCount = 60;
        const int bigVolumeChapters = 1200;
        var store = new FakeStore
        {
            Novel = new Novel
            {
                Id = "n1",
                Title = "千章规模",
                Outline = "总纲",
            },
        };
        for (var v = 1; v <= volumeCount; v++)
            store.Volumes.Add(
                new Volume
                {
                    Id = $"vol{v}",
                    NovelId = "n1",
                    Name = $"卷{v}",
                    Order = v,
                }
            );
        var order = 0;
        for (var i = 1; i <= bigVolumeChapters; i++)
            store.Chapters.Add(
                new Chapter
                {
                    Id = $"ch{++order}",
                    NovelId = "n1",
                    Title = $"第{order}章",
                    Order = order,
                    VolumeId = "vol1",
                    Summary = $"细纲{order}",
                }
            );
        for (var v = 2; v <= volumeCount; v++)
            store.Chapters.Add(
                new Chapter
                {
                    Id = $"ch{++order}",
                    NovelId = "n1",
                    Title = $"第{order}章",
                    Order = order,
                    VolumeId = $"vol{v}",
                }
            );

        var panel = new SetUpPanel(store, PanelThemes.Dark);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await panel.RefreshAsync("n1");
        ArrangePanel(panel);
        stopwatch.Stop();

        var novel = NovelNode(panel);
        var volumes = novel.Children.Where(n => IsVolumeNode(n)).ToList();
        Assert.Equal(volumeCount, volumes.Count);
        var bigVolume = volumes[0];
        Assert.Equal(bigVolumeChapters, bigVolume.Children.Count);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 3000,
            $"{bigVolumeChapters + volumeCount} 章刷新耗时 {stopwatch.ElapsedMilliseconds}ms，超出 3s 预算"
        );
    }
}
