using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClientPlatform;
using Core.Agent;
using Core.Data;
using Core.Novels;
using Core.Plugins;
using Core.Shared;
using Core.Tools;
using LlmTornado.Code;
using Tui.Services;
using XenoAtom.Terminal.UI;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui.Tests;

/// <summary>
/// 斜杠命令集成测试共享装配：真实 TuiApp（Agent + 临时目录 FileSessionStore + 内存
/// INovelStore + 临时 ConfigStore/CloudSessionStore）+ 反射驱动辅助。
///
/// 被两类测试复用：
/// - TuiAppSlashCommandIntegrationTests：无终端环境，反射调用 HandleSlashCommand；
/// - TuiAppTerminalSlashCommandIntegrationTests：InMemoryTerminalBackend 虚拟终端，
///   经真实 SubmitInput 输入路径驱动（见该类注释）。
/// </summary>
internal sealed class TuiSlashTestHarness : IAsyncDisposable
{
    internal const string NovelId = "nv1";
    internal const string SessionId = "s1";

    internal string TempDir { get; }

    /// <summary>ConfigStore 的实际目录（切换模式后校验 config.yaml 落盘的判据）。</summary>
    internal string ConfigStoreDirectory { get; }
    internal ToolRegistry Registry { get; }
    internal Agent Agent { get; }
    internal FakeNovelStore Store { get; }
    internal FileSessionStore SessionStore { get; }
    internal TodoStore TodoStore { get; }
    internal ConfigStore ConfigStore { get; }
    internal SettingsService SettingsService { get; }
    internal CloudSessionStore CloudSessionStore { get; }
    internal CloudConnectionService CloudConnection { get; }
    internal ContextModeSwitcher ContextModeSwitcher { get; }
    internal FakeContextModePlugin ContextModePlugin { get; }
    internal ToolSearchEngine ToolSearchEngine { get; }
    internal TuiApp App { get; }

    /// <summary>
    /// 上下文管理模式插件替身：按契约投递选项给 Agent，并给出可辨识的接续工具名。
    /// </summary>
    internal sealed class FakeContextModePlugin(Core.Agent.Agent agent) : IContextModePlugin
    {
        internal const string ToolName = "fake_new_context";

        public Core.Agent.ContextManagementOptions? CurrentOptions { get; private set; }

        public IReadOnlyList<AgentTool> ApplyMode(Core.Agent.ContextManagementOptions? options)
        {
            CurrentOptions = options;
            agent.ApplyContextManagement(options);
            return options is null ? [] : [new FakeAgentTool(ToolName, "换页")];
        }
    }

    private readonly MethodInfo _handleSlashCommand;
    private readonly FieldInfo _themeServiceField;
    private readonly FieldInfo _sessionIdField;
    private readonly FieldInfo _nextOrderField;
    private readonly FieldInfo _persistedMessageCountField;
    private readonly FieldInfo _lastParentEntryIdField;
    private readonly FieldInfo _inputTextField;
    private readonly MethodInfo _submitInput;

    /// <summary>测试小说消息（6 条：3 轮 user/assistant）。</summary>
    internal static readonly LlmChatMessage[] SampleMessages =
    [
        new(ChatMessageRoles.User, "第一章开始，主角在清晨醒来。"),
        new(ChatMessageRoles.Assistant, "好的，我来写第一章的开篇。"),
        new(ChatMessageRoles.User, "补充角色林晚的设定：冷静、理性。"),
        new(ChatMessageRoles.Assistant, "已补充角色设定。"),
        new(ChatMessageRoles.User, "接下来写第二章，场景是雨夜。"),
        new(ChatMessageRoles.Assistant, "第二章完成，雨夜场景已渲染。"),
    ];

    /// <summary>
    /// 构造装配。novelId/sessionId 传入时 TuiApp 启动路由走 OpenNovelAsync（恢复指定会话）。
    /// 会话种子不在 ctor 内执行（需 App 已构造）——由调用方在 RunAsync 前调用
    /// <see cref="SeedSessionStoreAsync"/>。
    /// </summary>
    internal TuiSlashTestHarness(
        string? novelId = null,
        string? sessionId = null,
        UsageRestClient? cloudUsage = null
    )
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"tui-slash-test-{Guid.NewGuid():N}");
        var novelsDir = Path.Combine(TempDir, "novels");
        var mythDir = Path.Combine(TempDir, "mythology");
        var configDir = Path.Combine(TempDir, "config");
        ConfigStoreDirectory = configDir;
        Directory.CreateDirectory(novelsDir);
        Directory.CreateDirectory(configDir);

        Registry = new ToolRegistry();
        Agent = new Agent(Registry, tornadoApi: null, logger: null);
        Store = new FakeNovelStore();
        SessionStore = new FileSessionStore(novelsDir, mythDir);
        TodoStore = new TodoStore();
        ConfigStore = new ConfigStore(configDir);
        SettingsService = new SettingsService(ConfigStore);
        CloudSessionStore = new CloudSessionStore(mythDir);
        CloudConnection = new CloudConnectionService(
            store: CloudSessionStore,
            invalidationBus: null,
            handler: null
        );

        // 上下文管理模式热切换：真实切换器（真实 Registry/Agent/ConfigStore/索引）+ 替身插件
        // （真实 ContextContinuationPlugin 的契约由 Plugins.ContextContinuation.Tests 覆盖）
        ToolSearchEngine = new ToolSearchEngine(embeddingService: null);
        ContextModePlugin = new FakeContextModePlugin(Agent);
        ContextModeSwitcher = new ContextModeSwitcher(
            Registry,
            Agent,
            ConfigStore,
            ToolSearchEngine,
            new ContextModePluginRef("Context Continuation", ContextModePlugin)
        );

        // 种子小说（startup OpenNovelAsync / /mode / /resume 需要）
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            Genre = "奇幻",
        };
        Store.Novels[NovelId] = novel;

        App = new TuiApp(
            Agent,
            Store,
            SessionStore,
            Registry,
            TodoStore,
            novelId: novelId,
            sessionId: sessionId,
            configStore: ConfigStore,
            contextModeSwitcher: ContextModeSwitcher,
            cloudConnection: CloudConnection,
            cloudUsage: cloudUsage
        );

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _handleSlashCommand = typeof(TuiApp).GetMethod("HandleSlashCommand", flags)!;
        _themeServiceField = typeof(TuiApp).GetField("_themeService", flags)!;
        _sessionIdField = typeof(TuiApp).GetField("_sessionId", flags)!;
        _nextOrderField = typeof(TuiApp).GetField("_nextOrder", flags)!;
        _persistedMessageCountField = typeof(TuiApp).GetField("_persistedMessageCount", flags)!;
        _lastParentEntryIdField = typeof(TuiApp).GetField("_lastParentEntryId", flags)!;
        _inputTextField = typeof(TuiApp).GetField("_inputText", flags)!;
        _submitInput = typeof(TuiApp).GetMethod("SubmitInput", flags)!;
    }

    internal ThemeService ThemeService => (ThemeService)_themeServiceField.GetValue(App)!;

    internal string? CurrentSessionId => (string?)_sessionIdField.GetValue(App);

    internal void SetSessionId(string sessionId) => _sessionIdField.SetValue(App, sessionId);

    internal void InvokeSlashCommand(string input) => _handleSlashCommand.Invoke(App, [input]);

    /// <summary>模拟用户在输入框输入并回车：设置 _inputText.Value 后走真实 SubmitInput 路径。</summary>
    internal void SubmitInput(string input)
    {
        var inputText = (State<string?>)_inputTextField.GetValue(App)!;
        inputText.Value = input;
        _submitInput.Invoke(App, null);
    }

    /// <summary>镜像 OpenNovelAsync 的会话初始化：种子 JSONL + 恢复 order/parent 追踪状态。</summary>
    internal async Task SeedSessionStoreAsync(bool seedCompactBlock = false)
    {
        var messages = new List<LlmChatMessage>(SampleMessages);

        Agent.CurrentNovelId = NovelId;
        foreach (var m in SampleMessages)
            Agent.AppendMessage(m);

        var lastEntryId = await SessionStore.SaveSessionMessages(SessionId, NovelId, messages);
        SetSessionId(SessionId);
        _nextOrderField.SetValue(App, messages.Count);
        _persistedMessageCountField.SetValue(App, messages.Count);
        _lastParentEntryIdField.SetValue(App, lastEntryId);

        if (seedCompactBlock)
            await SeedCompactBlockAsync();
    }

    /// <summary>仅存储层预置压缩块（/decompress 列表测试数据源，不动 App 内存状态）。</summary>
    internal async Task SeedCompactBlockAsync()
    {
        var active = await SessionStore.GetActiveMessages(SessionId);
        var firstKeptEntryId = active.Skip(3).First().EntryId ?? "";
        await SessionStore.CompactSession(
            SessionId,
            NovelId,
            "## 压缩概览\n压缩前 6 条消息，保留最近 3 条",
            firstKeptEntryId,
            messagesBefore: 6,
            parentEntryId: null,
            order: 6,
            reason: "manual",
            compressedMessageIds: active.Take(3).Select(m => m.EntryId ?? "").ToList()
        );
    }

    /// <summary>预置云端凭据（/logout 与 :cloud logout 测试）。</summary>
    internal void SeedConfiguredCloudSession()
    {
        CloudSessionStore.Save(
            new CloudSession("mk_live_test_key", "testpub", "ws1", "free", DateTimeOffset.UtcNow)
        );
        CloudConnection.Load();
    }

    /// <summary>轮询等待异步 fire-and-forget 命令完成（如 /compact、/mode）。</summary>
    internal static async Task WaitUntilAsync(
        Func<bool> condition,
        string message,
        int timeoutMs = 15000
    )
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new Xunit.Sdk.XunitException($"等待超时: {message}");
            await Task.Delay(25);
        }
    }

    public async ValueTask DisposeAsync()
    {
        App.Dispose();
        try
        {
            Directory.Delete(TempDir, true);
        }
        catch { }
        await Task.CompletedTask;
    }
}

/// <summary>测试用最小 AgentTool 实现（仅注册用，不执行）。</summary>
internal sealed class FakeAgentTool(string name, string label) : AgentTool
{
    public override string Name => name;
    public override string Label => label;
    public override string Description => $"测试工具 {label}";
    public override JsonElement? Parameters => null;

    public override Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        IProgress<AgentToolResult>? onUpdate = null
    ) => Task.FromResult(new AgentToolResult());
}

/// <summary>内存版 INovelStore：/mode、/state 路径使用的成员有实现，其余返回默认值。</summary>
internal sealed class FakeNovelStore : INovelStore
{
    internal Dictionary<string, Novel> Novels { get; } = new();

    internal List<AuthorNote> Notes { get; } = [];

    // TuiApp ctor 订阅此事件；测试期间从不触发（内存 fake 无实体变更）。
#pragma warning disable CS0067
    public event Func<EntityChangeEvent, Task>? OnEntityChanged;
#pragma warning restore CS0067

    public Task<Novel?> GetNovel(string id, CancellationToken ct = default) =>
        Task.FromResult(Novels.GetValueOrDefault(id));

    public Task<Novel?> GetNovelSummary(string id, CancellationToken ct = default) =>
        Task.FromResult(Novels.GetValueOrDefault(id));

    public Task<List<Novel>> GetAllNovels(CancellationToken ct = default) =>
        Task.FromResult(Novels.Values.ToList());

    public Task SaveNovel(Novel novel, CancellationToken ct = default)
    {
        Novels[novel.Id] = novel;
        return Task.CompletedTask;
    }

    public Task DeleteNovel(string id, CancellationToken ct = default)
    {
        Novels.Remove(id);
        return Task.CompletedTask;
    }

    public Task<int> GetChapterCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<int> GetCharacterCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<int> GetSceneCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<int> GetTotalWordCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<Chapter?> GetChapter(string id, CancellationToken ct = default) =>
        Task.FromResult(Chapters.FirstOrDefault(c => c.Id == id));

    public Task<Chapter?> GetChapter(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult(Chapters.FirstOrDefault(c => c.Id == id && c.NovelId == novelId));

    /// <summary>章节存储（/read、/chapter 与大纲面板用）。</summary>
    internal List<Chapter> Chapters { get; } = [];

    public Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default) =>
        Task.FromResult(Chapters.Where(c => c.NovelId == novelId).OrderBy(c => c.Order).ToList());

    public Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default)
    {
        var novel = Novels.GetValueOrDefault(novelId);
        if (novel is null)
            return Task.FromResult<NovelStructure?>(null);

        var volumes = Volumes.OrderBy(v => v.Order).ToList();
        var headers = Chapters
            .Where(c => c.NovelId == novelId)
            .OrderBy(c => c.Order)
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
            .ToList();
        var volumeIds = volumes.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var unassigned = headers
            .Where(c => string.IsNullOrEmpty(c.VolumeId) || !volumeIds.Contains(c.VolumeId!))
            .ToList();

        return Task.FromResult<NovelStructure?>(
            new NovelStructure(novel, volumes, headers, unassigned)
        );
    }

    public Task<Chapter?> GetChapterByOrder(
        string novelId,
        int order,
        CancellationToken ct = default
    ) => Task.FromResult(Chapters.FirstOrDefault(c => c.NovelId == novelId && c.Order == order));

    public Task SaveChapter(Chapter chapter, CancellationToken ct = default)
    {
        Chapters.RemoveAll(c => c.Id == chapter.Id);
        Chapters.Add(chapter);
        return Task.CompletedTask;
    }

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

    public Task DeleteChapter(string id, CancellationToken ct = default)
    {
        Chapters.RemoveAll(c => c.Id == id);
        return Task.CompletedTask;
    }

    public Task DeleteChapter(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Character?> GetCharacter(string id, CancellationToken ct = default) =>
        Task.FromResult<Character?>(null);

    public Task<Character?> GetCharacter(
        string novelId,
        string id,
        CancellationToken ct = default
    ) => Task.FromResult<Character?>(null);

    public Task<List<Character>> GetCharacters(string novelId, CancellationToken ct = default) =>
        Task.FromResult(Characters.Where(c => c.NovelId == novelId).OrderBy(c => c.Name).ToList());

    /// <summary>角色存储（探针/集成测试可直接灌数据）。</summary>
    internal List<Character> Characters { get; } = [];

    public Task SaveCharacter(Character character, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteCharacter(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteCharacter(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Plotline?> GetPlotline(string id, CancellationToken ct = default) =>
        Task.FromResult<Plotline?>(null);

    public Task<Plotline?> GetPlotline(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult<Plotline?>(null);

    public Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default) =>
        Task.FromResult<List<Plotline>>([]);

    public Task<List<Plotline>> GetOpenPlotlines(string novelId, CancellationToken ct = default) =>
        Task.FromResult<List<Plotline>>([]);

    public Task SavePlotline(Plotline plotline, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeletePlotline(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeletePlotline(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Faction?> GetFaction(string id, CancellationToken ct = default) =>
        Task.FromResult<Faction?>(null);

    public Task<Faction?> GetFaction(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult<Faction?>(null);

    public Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default) =>
        Task.FromResult<List<Faction>>([]);

    public Task SaveFaction(Faction faction, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteFaction(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteFaction(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveFactionRelationship(FactionRelationship rel, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<WorldRule>> GetWorldRules(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<WorldRule>());

    public Task SaveWorldRule(string novelId, WorldRule rule, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteWorldRule(string novelId, string ruleId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<FactionRelationship>> GetFactionRelationships(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult<List<FactionRelationship>>([]);

    public Task<Location?> GetLocation(string id, CancellationToken ct = default) =>
        Task.FromResult<Location?>(null);

    public Task<Location?> GetLocation(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult<Location?>(null);

    public Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default) =>
        Task.FromResult<List<Location>>([]);

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
        Task.FromResult<List<Item>>([]);

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
    ) => Task.FromResult<List<Relationship>>([]);

    public Task SaveRelationship(Relationship relationship, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteRelationship(string id, CancellationToken ct = default) => Task.CompletedTask;

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
    ) => Task.FromResult<List<SceneDefinition>>([]);

    public Task SaveScene(SceneDefinition scene, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteScene(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteScene(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<string?> GetCharacterNameById(
        string novelId,
        string characterId,
        CancellationToken ct = default
    ) => Task.FromResult<string?>(null);

    // ── Entity Evolution（ENTITY-EVOLUTION：TUI 桩空实现）──
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

    // Volume（大纲二层：卷纲）— 内存实现（大纲结构操作测试用）
    /// <summary>分卷存储（大纲 Tab 卷行/分组与结构操作用）。</summary>
    internal List<Volume> Volumes { get; } = [];

    public Task<Volume?> GetVolume(string id, CancellationToken ct = default) =>
        Task.FromResult(Volumes.FirstOrDefault(v => v.Id == id));

    public Task<Volume?> GetVolume(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult(Volumes.FirstOrDefault(v => v.Id == id && v.NovelId == novelId));

    public Task<List<Volume>> GetVolumes(string novelId, CancellationToken ct = default) =>
        Task.FromResult(Volumes.Where(v => v.NovelId == novelId).OrderBy(v => v.Order).ToList());

    public Task SaveVolume(Volume volume, CancellationToken ct = default)
    {
        Volumes.RemoveAll(v => v.Id == volume.Id);
        Volumes.Add(volume);
        return Task.CompletedTask;
    }

    public Task DeleteVolume(string id, CancellationToken ct = default)
    {
        Volumes.RemoveAll(v => v.Id == id);
        return Task.CompletedTask;
    }

    public Task DeleteVolume(string novelId, string id, CancellationToken ct = default)
    {
        Volumes.RemoveAll(v => v.Id == id);
        foreach (var chapter in Chapters.Where(c => c.VolumeId == id))
            chapter.VolumeId = null; // 与 FileNovelStore 一致：级联清空章节归属
        return Task.CompletedTask;
    }
}
