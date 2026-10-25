using System.Text;
using Channels.WeChat;
using ClientPlatform;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Infrastructure;
using Core.Plugins;
using Core.Shared;
using Core.Stability;
using Core.Tools;
using Core.Vector;
using LlmTornado;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tui.Controls;
using Tui.Services;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui;

using Data;

/// <summary>
/// TUI 应用程序组合根。
/// 负责组装各个控件（ConversationPanel、SetUpPanel、StatusBar、SettingsDialog），
/// 处理用户输入、斜杠命令和主题切换。
/// 业务逻辑拆分到 partial 文件：Events / Commands / Completion / Variant / Compact / Session / Indexing / Crud / Input。
/// </summary>
public sealed partial class TuiApp : IDisposable
{
    /// <summary>LLM Agent 实例，负责对话循环和工具调用。</summary>
    private readonly Agent _agent;

    /// <summary>结构化日志；缺失时使用无输出 logger，便于独立构造 TUI。</summary>
    private readonly ILogger<TuiApp> _logger;

    /// <summary>小说数据存储（文件系统）。</summary>
    private readonly INovelStore _store;

    /// <summary>会话数据存储（JSONL）。</summary>
    private readonly FileSessionStore _sessionStore;

    /// <summary>工具注册表，管理所有可用工具。</summary>
    private readonly ToolRegistry _registry;
    private readonly IFictionHarnessClient? _cloudHarness; // CHANGE_10 §4.3 反馈上报

    /// <summary>插件加载器（可选，无插件时为 null）。</summary>
    private readonly PluginLoader? _pluginLoader;

    /// <summary>Agent 事件适配器，将 Core 层事件转换为 UI 回调。</summary>
    private readonly AgentEventAdapter _adapter;

    /// <summary>主题服务，管理深色/浅色切换。</summary>
    private readonly ThemeService _themeService;

    /// <summary>设置服务，管理持久化配置。</summary>
    private readonly SettingsService _settingsService;

    /// <summary>上下文管理模式热切换器（可选：插件未加载 / 测试环境为 null）。</summary>
    private readonly ContextModeSwitcher? _contextModeSwitcher;

    /// <summary>ask_user 工具的待处理问题存储（可选）。</summary>
    private readonly UserQuestionStore? _questionStore;

    /// <summary>Todo 任务存储，管理 AI 任务列表。</summary>
    private readonly TodoStore _todoStore;

    /// <summary>检索内核（可选，用于实时索引和 OnEntityChanged 事件）。</summary>
    private readonly RetrievalKernel? _retrievalKernel;

    /// <summary>云端连接门面（D5 统一）：凭据存储（ClientPlatform）+ 连接状态。</summary>
    private readonly CloudConnectionService _cloudSession;

    /// <summary>云端心跳 CTS（§8：每 5 分钟 GET /v1/capabilities 刷新状态）。</summary>
    private CancellationTokenSource? _heartbeatCts;

    /// <summary>微信通道（可选：Host 未接线时 null，微信相关命令降级为提示）。</summary>
    private readonly WeChatChannelService? _weChat;

    /// <summary>微信 ↔ Agent 桥接（微信通道可用时才有）。</summary>
    private readonly WeChatAgentChannel? _weChatBridge;

    /// <summary>是否已有微信绑定对话框打开（模态框不能叠层）。</summary>
    private bool _weChatDialogOpen;

    /// <summary>云端 Toast 节流：探活降级 / 心跳 401 / 登录失败共用同一闸门（同一原因冷却窗口内只提示一次）。</summary>
    private readonly CloudToastThrottle _cloudToastThrottle = new();

    /// <summary>提供商 API 工厂，用于运行时切换 LLM 提供商。</summary>
    private readonly Func<
        ProviderConfig,
        (TornadoApi api, string displayName, CancellationHttpHandler handler)
    >? _providerFactory;

    /// <summary>当前面板调色板（深色或浅色）。</summary>
    private PanelColors _colors;

    // ── 提取的控件实例 ──
    /// <summary>对话面板：显示用户/AI 消息和工具调用。</summary>
    private readonly ConversationPanel _conversationPanel;

    /// <summary>侧边栏面板：显示小说实体大纲。</summary>
    private readonly SetUpPanel _setUpPanel;

    /// <summary>状态栏：显示 Agent 状态、模型信息和 Token 统计。</summary>
    private readonly MythStatusBar _statusBar;

    /// <summary>任务面板：显示 AI 执行的任务列表。</summary>
    private readonly TodoPanel _todoPanel;

    // ── 顶部标题栏和布局分割器 ──
    /// <summary>顶部标题栏。</summary>
    private readonly BackgroundPanel _headerBar;

    /// <summary>顶部标题栏右侧：云端连接状态（与底部状态栏同步）。</summary>
    private readonly State<string?> _cloudHeaderText = new(null);

    /// <summary>主内容分割器（左右分栏）。</summary>
    private readonly ThinSplitter _mainContent;

    /// <summary>左列分割器（对话/输入）。</summary>
    private readonly ThinSplitter _leftColumn;

    /// <summary>右列分割器（SetUp/Todo）。</summary>
    private readonly ThinSplitter _rightColumn;

    // ── 输入区域 ──
    /// <summary>输入框文本状态。</summary>
    private readonly State<string?> _inputText = new(null);

    /// <summary>输入编辑器控件。</summary>
    private readonly MythPromptEditor _promptEditor;

    /// <summary>输入卡面板（背景色容器）。</summary>
    private readonly BackgroundPanel _promptCardPanel;

    /// <summary>上一次输入文本（用于检测变化触发补全）。</summary>
    private string? _prevInputText;

    /// <summary>是否已设置初始焦点。</summary>
    private bool _initialFocusSet;

    /// <summary>
    /// 上一帧模态对话框的边界（null = 没有对话框）。
    /// 开/关、首次布局、终端 resize、内容撑高都可能让边界变化；这些时刻需要补帧：
    /// 框架的脏区只按新边界重画，旧边界外紧邻的格子可能保留上一帧的对话框底色
    /// （用户实机：输入框右上角残留 1 格）。
    /// 只认 Dialog：补全弹层 / 下拉也是 WindowLayer 窗口，不能在它们打开时触发补帧
    /// （否则会把弹层自己关掉）。
    /// </summary>
    private Rectangle? _dialogBounds;

    /// <summary>上一帧的对话框边界是否已连续两帧不变（用于区分首次布局占位与真实移动）。</summary>
    private bool _dialogBoundsStable;

    /// <summary>对话框刚打开、边界尚未稳定：等下一帧布局落定后再补，避免补到占位大矩形。</summary>
    private bool _dialogRepairPending;

    /// <summary>
    /// 最近一次对话框边界补帧的请求区域（0×0 = 未补）。
    /// 仅用于测试/诊断：确认补帧范围是「旧/新对话框边界 + 1 格外扩」而不是整屏。
    /// </summary>
    internal Rectangle LastDialogRepairRegion { get; private set; }

    /// <summary>是否待显示启动对话框。</summary>
    private bool _startupDialogPending;

    /// <summary>是否已启用 Ctrl+C 中断（stty isig）。</summary>
    private bool _ctrlCEnabled;

    /// <summary>实体变更刷新防抖 CTS。</summary>
    private CancellationTokenSource? _refreshCts;

    /// <summary>终端标签页标题转圈：agent loop 运行期间在标签页标题前滚动 loading 动画。</summary>
    private readonly TerminalTitleSpinner _titleSpinner = new();

    // ── 根视觉元素 ──
    /// <summary>根 Visual（ToastHost 包裹的 Grid）。</summary>
    private readonly Visual _rootVisual;

    /// <summary>脏区重绘 halo 兜底守卫（0×0，Poke 一下强制下一帧整帧重绘；库修复后删除）。</summary>
    /// <summary>启动时指定的小说 ID（可选）。</summary>
    private readonly string? _novelId;
    private readonly CloudNovelSyncService? _novelSync;

    /// <summary>云端用量查询客户端（:cloud status 积分余量；null = 未接线，仅显示连接态）。</summary>
    private readonly UsageRestClient? _cloudUsage;

    /// <summary>启动时恢复的会话 ID（可选）。</summary>
    private readonly string? _resumeSessionId;

    /// <summary>
    /// 初始化 TUI 应用程序的所有组件。
    /// </summary>
    /// <param name="agent">LLM Agent 实例，负责对话循环。</param>
    /// <param name="store">小说数据存储。</param>
    /// <param name="sessionStore">会话数据存储（JSONL）。</param>
    /// <param name="registry">工具注册表。</param>
    /// <param name="todoStore">Todo 任务存储。</param>
    /// <param name="questionStore">ask_user 工具的问题存储（可选）。</param>
    /// <param name="novelId">启动时自动打开的小说 ID（可选）。</param>
    /// <param name="sessionId">启动时恢复的会话 ID（可选，需同时指定 novelId）。</param>
    /// <param name="providerFactory">提供商 API 工厂，用于运行时切换 LLM 提供商（可选）。</param>
    /// <param name="configStore">配置存储（可选，缺失时使用默认路径）。</param>
    /// <param name="pluginLoader">插件加载器（可选）。</param>
    /// <param name="retrievalKernel">检索内核（可选，用于 UI 实时索引）。</param>
    /// <param name="cloudUsage">云端用量客户端（可选，:cloud status 显示积分余量）。</param>
    public TuiApp(
        Agent agent,
        INovelStore store,
        FileSessionStore sessionStore,
        ToolRegistry registry,
        TodoStore todoStore,
        UserQuestionStore? questionStore = null,
        string? novelId = null,
        string? sessionId = null,
        Func<
            ProviderConfig,
            (TornadoApi api, string displayName, CancellationHttpHandler handler)
        >? providerFactory = null,
        ConfigStore? configStore = null,
        PluginLoader? pluginLoader = null,
        ContextModeSwitcher? contextModeSwitcher = null,
        RetrievalKernel? retrievalKernel = null,
        CloudConnectionService? cloudConnection = null,
        WeChatChannelService? weChat = null,
        ILoggerFactory? loggerFactory = null,
        ILogger<TuiApp>? logger = null,
        IFictionHarnessClient? cloudHarness = null,
        CloudNovelSyncService? novelSync = null,
        UsageRestClient? cloudUsage = null
    )
    {
        _agent = agent;
        _logger = logger ?? NullLogger<TuiApp>.Instance;
        _store = store;
        _sessionStore = sessionStore;
        _registry = registry;
        _contextModeSwitcher = contextModeSwitcher; // 设置页「上下文」标签页热切换（null = 只写配置）
        _cloudHarness = cloudHarness; // CHANGE_10 §4.3 设定检查反馈上报
        // §13.6 云端连接门面（Host DI 单例注入，与客户端共享容器；null 时兜底新建）
        _cloudSession = cloudConnection ?? new CloudConnectionService();
        // 微信通道（Host DI 单例注入）：构造时恢复绑定并取得轮询权（幂等）。
        // 长轮询循环由任务的下一步接入；此处只负责状态真实（/wechat status 不会谎报未绑定）。
        _weChat = weChat;
        _weChat?.TryRestore();
        // 桥接：入站 -> RunPromptAsync（微信 turn 落在当前会话），出站 <- agent.OnEvent
        _weChatBridge = weChat is null
            ? null
            : new WeChatAgentChannel(
                weChat,
                // 方法组不能带可选参数转委托，包一层
                (text, ct) => RunPromptAsync(text, ct),
                // 终端里正在跑时，微信消息回"正忙"而不是撞进 Agent 抛异常
                isAgentBusy: () => _agent.IsRunning,
                // 裁决回路：Agent 的 ask_user 在手机上以编号选项呈现
                questionStore: _questionStore,
                loggerFactory?.CreateLogger<WeChatAgentChannel>()
            );
        _novelSync = novelSync; // cloud-novel-sync v3（:cloud sync / :cloud pull；null = 未接线）
        _cloudUsage = cloudUsage; // :cloud status 积分余量（null = 未接线，仅显示连接态）
        if (_novelSync is not null)
        {
            // 同步状态 → 状态栏 ⇅ 标识 + 设定面板大纲 tab 的云端状态行（事件可能在后台线程触发，经 PostToUi 回 UI）
            _novelSync.StateChanged += state =>
                PostToUi(() =>
                {
                    _statusBar?.SetCloudSyncState(state);
                    _setUpPanel?.SetCloudStatus(DescribeCloudSyncState(state));
                });
        }
        _cloudSession.CredentialsInvalidated += OnCloudCredentialsInvalidated;
        _pluginLoader = pluginLoader;
        _questionStore = questionStore;
        _todoStore = todoStore;
        _novelId = novelId;
        _resumeSessionId = sessionId;
        _providerFactory = providerFactory;
        _retrievalKernel = retrievalKernel;

        // 本地判定客户端（可选）：循环判定与检索共用同一实例（设置页保存即时热更新）
        if (retrievalKernel?.SystemOne is { } systemOneClient)
            _agent.SystemOne = systemOneClient;
        _adapter = new AgentEventAdapter();
        _settingsService = new SettingsService(configStore ?? new ConfigStore());
        _settingsService.LoadAll();
        _themeService = new ThemeService(_settingsService);
        _colors = _themeService.Current.Colors;

        SyncPluginState();

        // ── 初始化控件 ──
        _conversationPanel = new ConversationPanel(_adapter, registry, _colors);
        _setUpPanel = new SetUpPanel(store, _colors);
        _todoPanel = new TodoPanel(_todoStore, _colors);
        var modelInfo = $"{agent.State.ModelName} │ Ctrl+Q 退出";
        _statusBar = new MythStatusBar(modelInfo, _colors);

        // ── 绑定事件 ──
        WireEvents();

        // 标签页标题初始为应用名（打开小说后由 SetNovelName 联动更新）
        _titleSpinner.SetBaseTitle("Mythology");

        // ★ 及时持久化：每轮（含工具调用轮）消息就绪即写入 FileSessionStore，
        // 不等 Agent loop 停止（ConversationLoop 在用户消息入队和每轮结束后触发）。
        _agent.OnMessagesReady = PersistReadyMessagesAsync;

        // ★ 上下文接续：换页请求交给 Host 执行（写 window 条目 + 清空模型侧 + 记账）。
        // 必须在首次 prompt 之前装配——循环首次创建时会取走该回调与上下文管理选项。
        // compact 模式下 agent.ContextManagement == null，走原压缩路径（换页分支不触发）。
        _agent.OnNewContextRequested = (reason, _) => RollContextWindowAsync(reason);

        // ── 构建布局 ──
        _headerBar = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(_colors.HeaderBg),
            Content = new Footer()
                .Style(
                    new FooterStyle { Background = _colors.HeaderBg, Foreground = _colors.HeaderFg }
                )
                .Left(
                    new HStack(
                        new Markup("[primary]📖[/]"),
                        new Markup("[bold primary]Mythology[/]"),
                        new Markup("[dim]│[/]"),
                        new Markup("[dim]✨ AI 小说写作助手[/]")
                    ).Spacing(1)
                )
                // 右上：云端连接状态（登录成功可见），与底部状态栏同步
                .Right(new Markup(_cloudHeaderText)),
            HorizontalAlignment = Align.Stretch,
        };

        SetCloudState(_cloudSession.ConnectionState);

        // ── 配置输入 ──
        _promptEditor = ConfigureInput();
        _promptCardPanel = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(_colors.PromptBg),
            Content = _promptEditor,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        // ── 组装分割器 ──
        _leftColumn = new ThinSplitter(_conversationPanel.Visual, _promptCardPanel)
        {
            Ratio = 0.7,
            Direction = SplitDirection.Vertical,
            BarSize = 0,
        };
        _rightColumn = new ThinSplitter(_setUpPanel.Visual, _todoPanel.Visual)
        {
            Ratio = 0.6,
            Direction = SplitDirection.Vertical,
            BackgroundColor = _colors.SidePanelBg,
            LineColor = _colors.ConversationBg,
            HorizontalGlyph = new Rune('▄'),
            BarSize = 1,
        };
        // 主分隔条（对话列 ↔ 右列）的**底色取右列面板色**，与 _rightColumn 同规则：
        // bar 底色必须与它合并的区域一致——用 ConversationBg（暗=夜空 #1F1F1F / 浅=暖奶油 #FBF1C7）
        // 会在面板一侧留下一条纯对比色的竖带（实机表现：对话框右缘一条「黑块/白块」，
        // 跟随主题变色、在对话框打开时被分割成可见残段，看起来像渲染错误）。
        _mainContent = new ThinSplitter(_leftColumn, _rightColumn)
        {
            Ratio = 0.70,
            Direction = SplitDirection.Horizontal,
            BackgroundColor = _colors.SidePanelBg,
            LineColor = _colors.ConversationBg,
            BarSize = 1,
        };

        // ── Root + Toast ──
        _rootVisual = BuildRootVisual();

        RegisterCommands();
        _themeService.SetRootVisual(_rootVisual);
        // 启动即应用主题到 Markdown 渲染（持久化主题可能非默认 Desert）
        MarkdownConverter.ApplyTheme(_themeService.Current.Colors, _themeService.Current.UiTheme);
    }

    /// <summary>
    /// 绑定所有跨组件事件：Agent 状态变更、实体变更、工具调用、Token 追踪、ask_user、变体选择等。
    /// </summary>
    /// <summary>
    /// 设置云端连接状态：底部状态栏 + 顶部标题栏右侧同步展示。
    /// 所有云端状态变更统一走这里，避免两处漂移。
    /// </summary>
    private void SetCloudState(CloudConnectionState state)
    {
        _statusBar.SetCloudState(state);
        _cloudHeaderText.Value = state switch
        {
            CloudConnectionState.Connected => "[green]🌐 已连接[/]",
            CloudConnectionState.Authenticating => "[yellow]⏳ 登录中…[/]",
            CloudConnectionState.RateLimited => "[yellow]🌐 限流[/]",
            CloudConnectionState.QuotaExceeded => "[red]🌐 配额用尽[/]",
            CloudConnectionState.Unavailable => "[red]✕ 离线[/]",
            _ => "[dim]🌐 未连接[/]",
        };
    }

    private void WireEvents()
    {
        _conversationPanel.AgentStateChanged += OnAgentStateChanged;
        _conversationPanel.AgentTurnEnded += OnAgentTurnEnded;

        // Entity change event: debounce refresh + system bubble + Agent steering
        _store.OnEntityChanged += OnStoreEntityChanged;

        // Track tool calls: AI vs manual distinction + real-time panel refresh
        _adapter.OnToolEnd += OnAdapterToolEnd;

        // Token tracking: update StatusBar with the current session's context usage
        _adapter.OnContextTokens += OnTokenUpdate;

        // ask_user 回答弹窗统一触发：所有问题（AskUserTool / 云端裁决）均经
        // UserQuestionStore.CreateQuestions 创建并触发 PendingQuestionsCreated，
        // 单一入口避免重复弹窗造成的幽灵对话框（多条流程各自关闭自己的 Dialog）。
        if (_questionStore is not null)
            _questionStore.PendingQuestionsCreated += OnPendingQuestionsCreated;

        // explore variant selection handler
        _conversationPanel.OnExploreVariant += OnExploreVariant;

        // Entity link from conversation: navigate to side panel
        _conversationPanel.OnNavigateToEntity += OnNavigateToEntity;

        _agent.OnEvent += OnAgentEvent;
        _themeService.ThemeChanged += ApplyTheme;

        // SetUpPanel CRUD wiring
        WireSidePanelCrud();

        // Entity click-to-reference: inject entity name into prompt
        _setUpPanel.OnEntityClicked += OnEntityClicked;

        // Chapter status toggle

        // 完结状态切换 + 云端设定检查（CHANGE_10 · fiction.setup.check）
        _setUpPanel.OnCloudSetupCheck += OnCloudSetupCheck;
        _setUpPanel.OnSetupFeedback += OnSetupFeedbackPosted;

        // Audit log viewer
        _setUpPanel.OnViewAuditLog += OnViewAuditLog;

        // 面板内云端同步（大纲 tab 状态行）：同步服务未接线时整行隐藏
        _setUpPanel.OnCloudSyncRequested += pullOnly => _ = RunNovelSyncAsync(pullOnly);
        _setUpPanel.SetCloudConfigured(_novelSync is not null);
        if (_novelSync is not null)
            _setUpPanel.SetCloudStatus(DescribeCloudSyncState(_novelSync.State));
    }

    /// <summary>同步状态 → 面板内文案（与 <see cref="StatusBar.SetCloudSyncState"/> 同一批用语）。</summary>
    private static string DescribeCloudSyncState(CloudSyncState state) =>
        state switch
        {
            CloudSyncState.Syncing => "[aqua]⇅…[/] 云端同步中…",
            CloudSyncState.Synced => "[green]⇅✓[/] 已与云端同步",
            CloudSyncState.Conflicts => "[amber]⇅![/] 有未合并冲突（cloud-conflicts/ 下有副本）",
            CloudSyncState.Offline => "[dim]⇅ø[/] 云端不可达（网络/服务异常）",
            _ => "[dim]⇅[/] 尚未同步",
        };

    /// <summary>
    /// 配置输入编辑器：MythPromptEditor 样式、ESC/Ctrl+C 行为、补全处理器。
    /// </summary>
    /// <returns>配置好的 MythPromptEditor 实例。</returns>
    private MythPromptEditor ConfigureInput()
    {
        var promptEditor = new MythPromptEditor()
            .Text(_inputText)
            .PromptMarkup("[primary]❯[/] ")
            .ContinuationPromptMarkup("")
            .Placeholder("聊天 | /命令 | #角色 | @文件【ESC/Ctrl+C 中断回复】")
            .CompletionPresentation(PromptEditorCompletionPresentation.PopupList)
            .CompletionHandler(HandleCompletion)
            .Accepted(SubmitInput);

        promptEditor.Style(
            new PromptEditorStyle
            {
                Background = _colors.PromptBg,
                PromptSidebarBackground = _colors.PromptBg,
                PromptForeground = _colors.PromptFg,
                PlaceholderForeground = _colors.Dim,
                ShowPromptSeparator = false,
                Padding = new Thickness(1, 0, 0, 0),
            }
        );
        promptEditor.VerticalAlignment = Align.Stretch;

        // ESC 行为：运行中中断 Agent，否则清空输入
        promptEditor.EscapeBehavior = PromptEditorEscapeBehavior.CancelCompletionOnly;
        promptEditor.OnEscapePressed = () =>
        {
            if (_agent.IsRunning)
                AbortAgent();
            else
                _inputText.Value = "";
            return true;
        };
        promptEditor.OnCtrlCPressed = () =>
        {
            if (_agent.IsRunning)
                AbortAgent();
            else
                _inputText.Value = "";
            return true;
        };

        return promptEditor;
    }

    /// <summary>
    /// 组装根视觉元素：Grid(header/main/status) + ToastHost + ToastStyle 覆盖。
    /// </summary>
    /// <returns>根 Visual。</returns>
    private ToastHost BuildRootVisual()
    {
        var rootGrid = new Grid()
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star() },
                new RowDefinition { Height = GridLength.Auto },
                // 第 4 行 0 高：只放脏区兜底守卫（0×0，不占布局）
                new RowDefinition { Height = GridLength.Auto }
            )
            .Cell(_headerBar, 0, 0)
            .Cell(_mainContent, 1, 0)
            .Cell(_statusBar.Footer, 2, 0);

        rootGrid.ObserveInput();

        var toastHost = new ToastHost(rootGrid)
            .Position(ToastPosition.TopRight)
            .MaxVisible(3)
            .DefaultDuration(TimeSpan.FromSeconds(1.5));

        // Override ToastStyle: 图标宽度账本收口（纯 emoji 宽字形 + ✕ 关闭键），避免 Toast 边框错位
        toastHost.SetStyle(
            ToastStyle.Default with
            {
                InfoIcon = "\U0001F4A1", // 💡
                SuccessIcon = "\u2705", // ✅
                WarningIcon = "\u26A1", // ⚡
                ErrorIcon = "\u274C", // ❌
                CloseIcon = "\u2715", // ✕（U+2715，宽度 1 细十字；U+274E 卸任防黑块）
            }
        );

        return toastHost;
    }

    /// <summary>
    /// 取当前焦点所在顶层窗口的 <see cref="Dialog"/>（没有对话框时返回 null）。
    /// <para>
    /// 窗口是 WindowLayer 的子节点、与应用根视觉是<em>兄弟</em>，指针 / 键盘事件不会冒泡到应用根，
    /// 所以弹窗内的交互没法用 <see cref="InputActivity.ObserveInput{T}"/> 观察到，
    /// 只能用焦点位置反推：从焦点一路往上走到 WindowLayer 的子层，顶层是 Dialog ⇒ 焦点在对话框里。
    /// （ShowWindow 会把焦点移进窗口内第一个可聚焦控件，所以对话框开着时焦点必然在窗口里。）
    /// </para>
    /// <para>
    /// 只认 <see cref="Dialog"/>：补全弹层 / 下拉（<c>Popup</c>）同样是 WindowLayer 窗口，
    /// 但它们开合频繁且由编辑器自己管理，不能触发「整屏重发 + 关闭编辑器弹层」。
    /// 对话框内的弹层（下拉/菜单）会拿走焦点：用弹层锚点回推它属于哪个顶层窗口。
    /// </para>
    /// </summary>
    private Dialog? GetOpenDialog(TerminalApp? app)
    {
        if (app is null)
            return null;

        var focused = app.FocusedElement;
        if (focused is null)
            return null;

        var top = TopLevel(focused, app.Root);
        if (top is Popup { Anchor: { } anchor })
            top = TopLevel(anchor, app.Root);

        return top as Dialog;
    }

    /// <summary>按视口裁掉越界部分并请求区域补帧；0×0 区域忽略。</summary>
    private void RequestDialogRepair(TerminalApp? app, Rectangle region)
    {
        if (app is null || region.Width <= 0 || region.Height <= 0)
            return;

        var size = Terminal.Instance.Size;
        var left = Math.Clamp(region.X, 0, size.Columns);
        var top = Math.Clamp(region.Y, 0, size.Rows);
        var right = Math.Clamp(region.Right, 0, size.Columns);
        var bottom = Math.Clamp(region.Bottom, 0, size.Rows);
        if (right <= left || bottom <= top)
            return;

        var clamped = new Rectangle(left, top, right - left, bottom - top);
        app.RequestRegionRender(clamped);
        LastDialogRepairRegion = clamped;
    }

    /// <summary>矩形四边各外扩 1 格；负坐标由调用方按视口裁掉。</summary>
    private static Rectangle ExpandByOne(Rectangle rect) =>
        new(Math.Max(0, rect.X - 1), Math.Max(0, rect.Y - 1), rect.Width + 2, rect.Height + 2);

    private static Visual TopLevel(Visual visual, Visual root)
    {
        var top = visual;
        while (top.Parent is { } parent && !ReferenceEquals(parent, root))
            top = parent;
        return top;
    }

    public void Dispose()
    {
        // 退出前保存任务快照，确保下次恢复会话时任务不丢
        SaveCurrentSessionTodos();
        _conversationPanel.AgentStateChanged -= OnAgentStateChanged;
        _conversationPanel.AgentTurnEnded -= OnAgentTurnEnded;
        _store.OnEntityChanged -= OnStoreEntityChanged;
        _adapter.OnToolEnd -= OnAdapterToolEnd;
        _adapter.OnContextTokens -= OnTokenUpdate;
        _cloudSession.CredentialsInvalidated -= OnCloudCredentialsInvalidated;
        _conversationPanel.OnExploreVariant -= OnExploreVariant;
        _conversationPanel.OnNavigateToEntity -= OnNavigateToEntity;
        if (_questionStore is not null)
            _questionStore.PendingQuestionsCreated -= OnPendingQuestionsCreated;
        _agent.OnEvent -= OnAgentEvent;
        _themeService.ThemeChanged -= ApplyTheme;
        _setUpPanel.OnEntityClicked -= OnEntityClicked;
        _setUpPanel.OnViewAuditLog -= OnViewAuditLog;
        _setUpPanel.OnCloudSetupCheck -= OnCloudSetupCheck;
        foreach (var u in _crudUnsubscribers)
        {
            u();
        }
        _crudUnsubscribers.Clear();
        _todoPanel.Dispose();
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        // 取消防抖续体，避免卸载窗口内 Dispatcher.Post 抛未观察异常；
        // 置空防止在途事件分发（竞态）对已释放 CTS 调 Cancel 抛 ObjectDisposedException
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        _titleSpinner.Dispose();
    }

    // ── 主循环 ──

    /// <summary>
    /// 启动 TUI 主循环：加载输入历史、路由启动参数（--novel/--session）、运行终端渲染循环。
    /// 渲染循环内处理：事件分发、焦点设置、Ctrl+C 启用、启动对话框、输入补全触发。
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _appCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var exit = false;
        ct.Register(() => exit = true);

        try
        {
            var dbHistory = await _sessionStore.GetInputHistory(_sessionId, limit: 100, ct);
            dbHistory.Reverse();
            _inputHistory.AddRange(dbHistory);
            _promptEditor.SetHistory(_inputHistory);
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "load input history");
        }

        // ── Startup: route by --novel / --session args ──
        try
        {
            if (_resumeSessionId is not null && _novelId is not null)
            {
                // --novel X --session Y → 恢复指定会话
                await OpenNovelAsync(_novelId, _resumeSessionId);
            }
            else if (_novelId is not null)
            {
                // --novel X → 打开指定小说，新建会话
                var novel = await _store.GetNovel(_novelId, ct);
                if (novel is not null)
                    await OpenNovelAsync(_novelId);
                else
                    ToastService.Warning($"小说 {_novelId} 不存在");
            }
            else
            {
                // 无参数 → 首帧后弹窗选择项目
                _startupDialogPending = true;
            }
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "route TUI startup");
        }
        // ── CHANGE_07 §12.3.1: 启动时检查云端凭据 ──
        // 注意：必须等 Terminal.RunAsync 绑定 Dispatcher 之后再初始化——探活续体在线程池线程
        // 上经 PostToUi 刷新状态栏，若早于 app 创建则 Dispatcher.Post 抛
        // InvalidOperationException（"not attached to a running TerminalApp"），且探活是
        // fire-and-forget，异常被吞 → 状态栏永远停在「登录中」。云端初始化已移到首帧
        // （onUpdate 的 _initialFocusSet 分支，此时 app 已就绪）。

        // Ctrl+C 中断 Agent：在 CBreak 模式下 Ctrl+C 触发 SIGINT → Console.CancelKeyPress →
        // Program.OnCancelKeyPress 调用 Agent.Abort() + CancellationHttpHandler.CancelActiveRequests()。
        // 全局命令注册作为备用路径（在 Raw 模式终端上 Ctrl+C 作为 \x03 到达命令系统）。
        // 终端格宽探测（UI 接管屏幕之前）：EAW=A 字形在部分终端/字体下占 2 格，
        // 框架默认按 1 格记账 ⇒ 整行错位/行尾露底/字形被顶掉。探测失败按现状回退。
        // 终端默认底色/前景对齐应用主题（OSC 10/11）：
        // 框架的增量输出只写「变化过的格子」，从未写过的格子、以及 BCE 擦除（ESC[2J/K）填的格子
        // 用的都是**终端默认底**——默认底若是黑，就会在对话框/面板交界留下一块块黑区，
        // 宽约一个字符（用户实机：悬浮重绘时出现、遮住下层文字、CJK 字间有空档）。
        // 对齐后这些格子与页面同色，黑块消失。退出时用 OSC 110/111 复位。
        ApplyTerminalDefaultColors(Terminal.Instance);

        var widthProbe = TerminalWidthProbe.Probe(Terminal.Instance);
        // 探测结论落日志（用户可回传：能直接看出终端把哪些字形画成了 2 格）
        if (widthProbe is null)
        {
            TuiLogger.Diagnostic(_logger, "terminal-cell-width", "探测未生效（终端未应答 DSR）");
            ToastService.Warning("终端字形宽度探测未生效（终端未应答），字形错位可能仍存在");
        }
        else
        {
            var detail =
                $"EAW=A 整类={widthProbe.AmbiguousClassWide}；额外加宽 {widthProbe.ExtraWideCodePoints.Count} 个 "
                + $"[{string.Join(",", widthProbe.ExtraWideCodePoints.Select(cp => $"U+{cp:X4}"))}]";
            TuiLogger.Diagnostic(_logger, "terminal-cell-width", detail);

            // 实测到不一致（终端把某些字形画得比框架记账宽）时给出可见提示，
            // 便于用户回传定位；没有不一致则不打扰。
            if (widthProbe.CjkRenderedNarrow)
                ToastService.Warning(
                    "终端把汉字渲染成 1 格宽（字体/宽度设置问题）：界面会出现空档与色块，"
                        + "请改用等宽 CJK 字体（如 Sarasa Mono CJK / Noto Sans Mono CJK）"
                );

            if (widthProbe.AmbiguousClassWide || widthProbe.ExtraWideCodePoints.Count > 0)
                ToastService.Info(
                    $"终端字形宽度：已按实测加宽 {widthProbe.ExtraWideCodePoints.Count} 个字形"
                );
        }

        // ── 微信通道：启动长轮询（仅当本进程持有轮询权）──
        // 另一个入口已持锁时不启：同一 bot 双进程轮询 = 消息重复或丢失。
        if (_weChatBridge is not null && _weChat?.HasOwnership == true)
        {
            _ = _weChatBridge.RunAsync(_appCts.Token);
            ToastService.Info("微信通道已启动，可以直接在微信里发消息。");
        }

        await Terminal.RunAsync(
            _rootVisual,
            onUpdate: (TerminalRunningContext _ctx) =>
            {
                // 对话框开/关/边界变化是脏区边界：旧边界不再参与布局，框架脏区只按新边界重画，
                // 旧边界外紧邻的格子可能保留上一帧的对话框底色（用户实机：输入框右上角残留 1 格）。
                // 只补「旧边界 ∪ 新边界」并各向外扩 1 格（相邻格），不整屏重发。
                // 只认 Dialog：补全弹层 / 下拉也是 WindowLayer 窗口，不能让它们触发补帧/自杀。
                var dialog = GetOpenDialog(_ctx.App);
                var dialogBounds = dialog?.Bounds;
                if (dialogBounds != _dialogBounds)
                {
                    var opened = dialogBounds is not null;
                    var oldBounds = _dialogBounds;
                    var oldStable = _dialogBoundsStable;
                    _dialogBounds = dialogBounds;
                    _dialogBoundsStable = false;
                    // 弹层是焦点锁定的：对话框打开后补全弹层不能再留在下面（会遮挡/残留）。
                    if (opened)
                        _promptEditor.CloseMyPopup();

                    // 首次出现 / 仍在稳定过程中：先等一帧，避免把首次布局的占位大矩形当补帧区域。
                    if (opened && (oldBounds is null || _dialogRepairPending))
                    {
                        _dialogRepairPending = true;
                    }
                    else
                    {
                        _dialogRepairPending = false;
                        // 新边界补 1 格 halo；旧边界只在上一帧已稳定（真实移动/关闭）时补。
                        var repair = default(Rectangle);
                        if (dialogBounds is { } cur)
                            repair = ExpandByOne(cur);
                        if (oldBounds is { } old && oldStable)
                        {
                            var expanded = ExpandByOne(old);
                            repair =
                                repair.Width > 0 ? Rectangle.Union(repair, expanded) : expanded;
                        }
                        RequestDialogRepair(_ctx.App, repair);
                    }
                }
                else
                {
                    if (dialogBounds is not null && !_dialogBoundsStable)
                        _dialogBoundsStable = true;
                    if (_dialogRepairPending && dialogBounds is { } cur)
                    {
                        _dialogRepairPending = false;
                        RequestDialogRepair(_ctx.App, ExpandByOne(cur));
                    }
                }

                // 终端侧损伤兜底（Apple Terminal 实测：中文输入法内联组词逐格保存/恢复，
                // 恢复只还原字符不还原每格颜色，把已上色的格子擦成基色）。
                // 差分渲染器认为那些格子自己已经发过，永远不补；需要 RequestRegionRender()
                // 强制重发指定区域。
                //
                // 只重发右列（侧栏），绝不重发对话区：组词文字就在对话区，重发它会覆盖
                // 终端正在绘制的文字、双方抢画面就是闪烁（全屏重发方案已被用户否决）。
                // 侧栏约 58x44 ≈ 6.5KB/帧，200ms 一帧约 32KB/s，可忽略。
                // 打字期间按 200ms 限流持续补（否则连续输入时损伤会一直增长）。
                //
                // 注：库的脏区 halo 缺陷（宽字形被脏区边界切开时留下 1 格基色）已在 fork 里
                // 修复（ExpandDirtyRectAroundWideGlyphs），原先的 DirtyRepairGuard 兜底已删除。
                if (
                    InputActivity.TakeRegionRenderSlot(
                        TimeSpan.FromMilliseconds(500),
                        TimeSpan.FromMilliseconds(200)
                    )
                )
                {
                    var bounds = _rightColumn.Bounds;
                    if (bounds.Width > 0 && bounds.Height > 0)
                        _ctx.App?.RequestRegionRender(bounds);
                }

                _adapter.DispatchAll();

                if (!_initialFocusSet)
                {
                    _initialFocusSet = true;

                    // 首帧：TerminalApp 已绑定 Dispatcher → 重放启动窗口积压的 UI 更新
                    // （PostToUi 兜底），再初始化云端（探活结果能正常刷新状态栏）。
                    DrainPendingUiActions();
                    _ = InitializeCloudAsync();

                    var app = _promptEditor.App;
                    app?.Focus(_promptEditor);

                    // ★ 在 TerminalApp 完全初始化后（raw mode 已设置），
                    // 通过 stty isig 重新启用 ISIG，让 Ctrl+C 产生 SIGINT。
                    if (!_ctrlCEnabled)
                    {
                        _ctrlCEnabled = true;
                        EnableCtrlCInterrupt();
                    }

                    // 光标形状：块光标在部分终端（实机：Ghostty）会整格盖住文字——
                    // 用户看到的就是「恰好一个字符宽的黑块」，悬浮/焦点一动它就跟过去、
                    // 点别处又消失。改成细竖线：占位极小、不遮字，仍是清楚的编辑指示。
                    Terminal.Instance.CursorStyle(AnsiCursorStyle.SteadyBar);
                }

                // 首帧渲染后执行启动引导（云端登录 → 项目选择/新建）
                if (_startupDialogPending)
                {
                    _startupDialogPending = false;
                    _ = RunStartupOnboardingAsync();
                }

                var tabCompletion = _promptEditor.ConsumeTabRequestedCompletion();
                var current = _inputText.Value;
                if (current != _prevInputText || tabCompletion)
                {
                    _prevInputText = current;
                    var selectionApplied = _promptEditor.ConsumeSelectionApplied();

                    var isSlashCmd =
                        current is not null
                        && current.StartsWith('/')
                        && !current.Contains('\n')
                        && (!current.Contains(' ') || current.StartsWith("/tools "));

                    var isEntityMention =
                        current is not null && current.Contains('@') && !current.Contains('\n');

                    var isHashRef =
                        current is not null && current.Contains('#') && !current.Contains('\n');

                    if (
                        !selectionApplied
                        && (isSlashCmd || isEntityMention || isHashRef)
                        && current is not null
                    )
                        TriggerCompletion(current);
                    else if (_promptEditor.IsMyPopupOpen)
                        _promptEditor.CloseMyPopup();
                }

                return exit ? TerminalLoopResult.StopAndKeepVisual : TerminalLoopResult.Continue;
            },
            options: new TerminalRunOptions
            {
                WideRuneResolver = TerminalWidthProbe.BuildResolver(widthProbe),
            },
            cancellationToken: ct
        );

        ResetTerminalDefaultColors(Terminal.Instance);

        // 微信桥接：取消后台循环（与 _appCts 同生共死）
        if (_weChatBridge is not null)
            _ = _weChatBridge.DisposeAsync();

        _titleSpinner.SetBusy(null); // 退出渲染循环：清除可能残留的转圈帧，恢复纯标题
        _appCts.Dispose();
    }

    /// <summary>
    /// 将终端默认底色（OSC 11）设为当前主题的对话区底色。
    /// 未绘制格与 BCE 擦除格取默认底；弹窗和输入控件的颜色由各自样式管理。
    /// </summary>
    internal static void ApplyTerminalDefaultColors(TerminalInstance terminal)
    {
        // OSC 11 is the fallback for unpainted cells and BCE erases; align it with the app's base surface.
        terminal.Write($"\u001b]11;rgb:{ToRgbSpec(PanelThemes.Current.ConversationBg)}\a");
        terminal.Flush();
    }

    /// <summary>复位终端默认前景/底色（OSC 110 / 111）。</summary>
    internal static void ResetTerminalDefaultColors(TerminalInstance terminal)
    {
        terminal.Write("\u001b]111\a");
        terminal.Flush();
    }

    private static string ToRgbSpec(Color color) => $"{color.R:X2}/{color.G:X2}/{color.B:X2}";

    // ── 操作辅助方法 ──

    /// <summary>
    /// 清除当前对话：重置 Agent 上下文、token 显示和对话面板。
    /// </summary>
    private void ClearConversation()
    {
        _agent.Reset();
        // ★ 上下文已清空：token 显示重置为 0 / 窗口
        InvalidateContextMeasure();
        UpdateTokenDisplay();
        _conversationPanel.Clear();
        _statusBar.SetAgentState(TuiState.Idle);
        ToastService.Info("已清除");
    }

    /// <summary>
    /// 中断运行中的 Agent：中止 LLM 调用、清空 UI 事件队列和等待占位。
    /// </summary>
    private void AbortAgent()
    {
        if (_agent.IsRunning)
        {
            _agent.Abort();
            _adapter.Cancel(); // 清空积压的 UI 事件队列，防止取消后 UI 继续更新
            _agent.ClearAllQueues(); // 清空所有队列（含系统通知），防止旧消息混入新 prompt
            _conversationPanel.ClearAllWaiting(); // 清空所有 ⏳ waiting 占位
            ToastService.Warning("Agent 已中断");
        }
        else
            ToastService.Info("Agent 未在运行");
    }

    /// <summary>
    /// 会话上下文切换（打开其他会话 / /resume / 分支导航）前中止运行中的 Agent。
    /// 与 AbortAgent 不同：静默处理未运行的情况，且重置状态栏到 idle。
    /// 目的：旧会话的 LLM 调用不得继续写回新会话的上下文（数据污染），
    /// 其 TokenUsageEvent 也不得污染新会话的 token 显示。
    /// </summary>
    private void AbortRunningAgent()
    {
        if (!_agent.IsRunning)
            return;
        _agent.Abort();
        _adapter.Cancel(); // 丢弃已入队的旧会话 UI 事件
        _agent.ClearAllQueues();
        _conversationPanel.ClearAllWaiting();
        _statusBar.SetAgentState(TuiState.Idle);
        ToastService.Warning("Agent 已中断（切换会话）");
    }

    /// <summary>循环到下一个主题（/theme toggle、命令面板）。</summary>
    private void ToggleTheme() => _themeService.NextTheme();

    /// <summary>
    /// 应用主题到所有控件：更新调色板、XenoAtom 主题、分割线颜色、输入框样式。
    /// 浅色主题下分割线退回 Border 色（ConversationBg 与 SidePanelBg 对比度不足）。
    /// </summary>
    private void ApplyTheme()
    {
        _colors = _themeService.Current.Colors;
        // 控件主题随当前主题的 XenoAtom Theme（16 色 ColorScheme 推导，含浅色主题）
        _rootVisual.Style(_themeService.Current.UiTheme);
        // Markdown 渲染随主题
        MarkdownConverter.ApplyTheme(_colors, _themeService.Current.UiTheme);

        _conversationPanel.ApplyTheme(_colors);
        _setUpPanel.ApplyTheme(_colors);
        // 重建侧栏树：树节点/折叠项颜色为构建时快照，重建以跟随新主题（保留展开状态）
        _ = RefreshOutline();
        // 重建对话流：气泡（含工具卡片）颜色为构建时快照，重建以跟随新主题。
        // 非流式才重建——流式中重建会破坏进行中的输出。
        if (!_agent.IsRunning)
        {
            _conversationPanel.Clear();
            _conversationPanel.RestoreMessages(_agent.State.Messages.ToArray());
        }
        _statusBar.ApplyTheme(_colors);
        _todoPanel.ApplyTheme(_colors);

        _headerBar.FillStyle = Style.None.WithBackground(_colors.HeaderBg);
        _promptCardPanel.FillStyle = Style.None.WithBackground(_colors.PromptBg);
        _headerBar.Refresh(); // FillStyle 普通属性不触发渲染，显式强制重绘
        _promptCardPanel.Refresh();
        // 分隔线字形使用主题对比色；主分隔条的单元格底色保持右侧面板色，避免
        // 主题重应用后在 SetUpPanel 一侧留下整列黑/白底（实体弹窗右缘会露出残段）。
        // 右列横带的 ▄ 下半显示对比色，上半继续透出上面板色。
        var barColor = _themeService.IsDark ? _colors.ConversationBg : _colors.Border;
        _mainContent.BackgroundColor = _colors.SidePanelBg;
        _mainContent.LineColor = barColor;
        _leftColumn.BackgroundColor = _colors.ConversationBg; // BarSize=0 不渲染，仅占位
        _leftColumn.LineColor = barColor;
        _rightColumn.BackgroundColor = _colors.SidePanelBg; // ▄ 上半 = 上面板色延伸
        _rightColumn.LineColor = barColor;

        _promptEditor.Style(
            new PromptEditorStyle
            {
                Background = _colors.PromptBg,
                PromptSidebarBackground = _colors.PromptBg,
                PromptForeground = _colors.PromptFg,
                PlaceholderForeground = _colors.Dim,
                ShowPromptSeparator = false,
                Padding = new Thickness(1, 0, 0, 0),
            }
        );

        ToastService.Info($"主题: {_themeService.Current.Name}");
    }

    /// <summary>
    /// 同步插件禁用状态：从 SettingsService 读取配置，更新 ToolRegistry 和 Agent。
    /// </summary>
    private void SyncPluginState()
    {
        var disabledNames = _settingsService
            .DiscoveredPlugins.Where(p => !_settingsService.IsPluginEnabled(p.Id))
            .Select(p => p.Name)
            .ToList();

        _registry.SetDisabledPlugins(disabledNames);
        _agent.RefreshTools();
    }

    /// <summary>
    /// 运行时删除插件：卸载 ALC + 从 ToolRegistry 移除 + 删除磁盘文件 + 刷新 UI。
    /// 内置插件（Builtin 信任级别）不可删除。
    /// </summary>
    private void RemovePlugin(string pluginId)
    {
        // 0. 内置插件保护
        var loaded = _pluginLoader?.LoadedPlugins.FirstOrDefault(p =>
            string.Equals(p.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase)
        );
        if (loaded is { Trust: PluginTrustLevel.Builtin })
        {
            ToastService.Error("内置插件不可删除");
            return;
        }

        // 1. 从 PluginLoader 卸载（移除工具 + Dispose ALC）
        if (_pluginLoader is not null)
        {
            if (!_pluginLoader.UnloadPlugin(pluginId, _registry))
            {
                ToastService.Error($"未找到插件: {pluginId}");
                return;
            }
        }
        else
        {
            // 没有 PluginLoader 时，仅从 registry 移除
            var pluginName = _registry.GetPluginNameById(pluginId);
            if (pluginName is not null)
                _registry.UnregisterByPlugin(pluginName);
        }

        // 2. 刷新 Agent 工具快照
        _agent.RefreshTools();
        _agent.ToolInjector?.InvalidateIndex();

        // 3. 删除磁盘文件
        var pluginDirs = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mythology",
                "plugins"
            ),
            Path.Combine(Directory.GetCurrentDirectory(), ".mythology", "plugins"),
        };

        foreach (var baseDir in pluginDirs)
        {
            var subDir = Path.Combine(baseDir, pluginId);
            if (Directory.Exists(subDir))
            {
                try
                {
                    Directory.Delete(subDir, recursive: true);
                    break;
                }
                catch
                {
                    // DLL 已被 ALC 卸载但 GC 可能尚未回收，文件可能仍被锁定
                    // 不阻断流程，用户可稍后手动删除
                }
            }
        }

        // 4. 刷新 SettingsService 的插件发现列表
        _settingsService.ReloadPlugins();

        // 5. 重新同步禁用状态
        SyncPluginState();

        ToastService.Success($"插件 {pluginId} 已删除");
    }

    /// <summary>
    /// 运行时切换 LLM 提供商/模型：重建 API 实例、同步 ModelContextProfile、
    /// 刷新 token 显示、持久化 model_change 事件。
    /// </summary>
    private void ReloadProvider()
    {
        if (_providerFactory is null)
            return;

        var cfg = _settingsService.Provider;
        var oldModelName = _agent.State.ModelName;
        _agent.InvalidateConversationLoop();
        var (api, displayName, handler) = _providerFactory(cfg);
        var newModelName = cfg.DefaultModel;
        _agent.TornadoApi = api;
        _agent.SetHttpHandler(handler);
        _agent.SetModelName(newModelName);
        _agent.SetThinkingLevel(cfg.ThinkingLevel);

        // 重建 ModelContextProfile 并同步所有稳定器阈值
        // 自动同步：如果模型声明了 ContextTokens，使用模型声明的值
        var effectiveContextSize = ModelCapabilityResolver.ResolveEffectiveContextSize(
            newModelName,
            cfg.ContextWindowSize
        );
        var thinkingLevel = ThinkingLevelBudgets.FromString(cfg.ThinkingLevel);
        var isAdaptiveThinking = ModelCapabilityResolver.IsAdaptiveThinkingModel(newModelName);

        // Adaptive-only thinking 模型不接受 manual budget_tokens，
        // ThinkingBudgetTokens 语义为"未知/不可预测"，设为 0。
        // HasThinking 通过 IsAdaptiveThinking 联合判断，不依赖此数值。
        var thinkingBudget = isAdaptiveThinking
            ? 0
            : ThinkingLevelBudgets.GetBudgetTokens(thinkingLevel, effectiveContextSize);

        var newProfile = new ModelContextProfile
        {
            ContextWindowTokens = effectiveContextSize,
            ThinkingBudgetTokens = thinkingBudget,
            IsAdaptiveThinking = isAdaptiveThinking,
        };
        _agent.ApplyModelProfile(newProfile);

        // ★ 模型切换后上下文窗口可能变化：刷新 token 显示的分母（实测值仍有效，不失效）
        // 同时清空口径校准（新模型 tokenizer 换算可能不同，下一轮结束重新学习）
        ResetCalibration();
        UpdateTokenDisplay();

        // 持久化 model_change 事件（记录模型切换历史，用于审计和上下文理解）
        if (
            !string.IsNullOrEmpty(_sessionId)
            && !string.IsNullOrEmpty(oldModelName)
            && oldModelName != newModelName
        )
        {
            var novelId = _agent.CurrentNovelId;
            if (!string.IsNullOrEmpty(novelId))
            {
                _ = _sessionStore.SaveSessionEvent(
                    _sessionId,
                    novelId,
                    new SessionEvent
                    {
                        EventType = SessionEventType.ModelChange,
                        ParentEntryId = _lastParentEntryId,
                        Order = _nextOrder,
                        From = oldModelName,
                        To = newModelName,
                    }
                );
                _nextOrder++;
            }
        }

        _statusBar.SetModelInfo($"{displayName} │ Ctrl+Q quit");
    }

    /// <summary>
    /// 保存 System One（本地判定）配置后热生效（SettingsDialog 是模态对话框，
    /// 保存时 ConversationLoop 停止，无并发风险）；未接线时提示下次启动生效。
    /// </summary>
    private void ReloadSystemOne()
    {
        var config = _settingsService.SystemOne;
        if (_retrievalKernel?.SystemOne is { } client)
        {
            client.UpdateConfig(config);
            if (config.Enabled)
                ToastService.Success($"本地判定已生效（{config.BaseUrl}）");
            else
                ToastService.Success("本地判定已停用");
        }
        else
        {
            ToastService.Warning("检索内核未接线，配置将在下次启动生效");
        }
    }

    /// <summary>
    /// 保存 embedding 配置后冷重配（SettingsDialog 是模态对话框，
    /// 保存时 ConversationLoop 必然处于停止状态，无并发风险）：
    /// provider=cloud 时切云端 /v1/embedding；否则优先使用独立 embedding 连接器，
    /// 无独立密钥时回退到聊天 API。
    /// 让 RetrievalKernel + AgentMemoryStore 在 loop 停止期重配置。
    /// 维度一致 → 即时生效；维度不一致 → 提示下次启动重建。
    /// </summary>
    private void ReloadEmbedding()
    {
        if (_retrievalKernel is null)
            return;

        var embConfig = _settingsService.Embedding;

        // 云端 opt-in（与启动路径一致）：不依赖本地模型名/密钥；401/403 由心跳兜底吊销。
        if (string.Equals(embConfig.Provider, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            var cloudService = new CloudEmbeddingClient(
                CloudEndpoints.ResolveCloudBaseUrl(),
                () => _cloudSession.ApiKey,
                logger: _logger
            );
            var cloudResult = _retrievalKernel.UpdateEmbeddingService(cloudService);
            if (cloudResult == EmbeddingReplaceResult.SameDimension)
                ToastService.Success("embedding 已切换为云端 /v1/embedding");
            else
                ToastService.Warning(
                    "云端 embedding 维度与本地向量库不一致，新设置将在下次启动时重建向量库后生效"
                );
            return;
        }

        var newModel = embConfig.Model;
        if (string.IsNullOrWhiteSpace(newModel))
        {
            ToastService.Warning("embedding 模型名为空");
            return;
        }

        TornadoApi? embeddingApi;
        try
        {
            // 优先使用独立 embedding 连接器
            if (embConfig.ApiKeys.Count > 0)
            {
                var (api, _, _) = LlmApiFactory.CreateFromEmbeddingConfig(embConfig);
                embeddingApi = api;
            }
            else
            {
                embeddingApi = _agent.TornadoApi;
            }
        }
        catch
        {
            embeddingApi = _agent.TornadoApi;
        }

        if (embeddingApi is null)
        {
            ToastService.Warning("未配置 LLM 提供商，无法启用 LlmTornado embedding");
            return;
        }

        try
        {
            var newService = new EmbeddingService(embeddingApi, newModel);
            var result = _retrievalKernel.UpdateEmbeddingService(newService);
            if (result == EmbeddingReplaceResult.SameDimension)
                ToastService.Success($"embedding 已切换为 {newModel}");
            else
                ToastService.Warning(
                    $"embedding 维度已变化（新服务维度 {newService.Dimension}），"
                        + $"新设置将在下次启动时重建向量库后生效"
                );
        }
        catch (Exception ex)
        {
            ToastService.Error($"embedding 重配失败: {ex.Message}");
        }
    }

    // ── 辅助方法 ──

    /// <summary>
    /// 格式化字段变更字典为字符串。
    /// </summary>
    private static string FormatFieldChanges(Dictionary<string, string> changes) =>
        string.Join("; ", changes.Select(kv => $"{kv.Key}: {kv.Value}"));

    /// <summary>
    /// 截断字符串到指定长度。
    /// </summary>
    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";

    /// <summary>
    /// HARNESS-CLOUD §5.4 触发桥：Hook 侧（云端裁决回路）经
    /// UserQuestionStore.PendingQuestionsCreated 通知新问题已创建——
    /// 直接复用 ask_user 弹窗与答案追加逻辑（问题已在 store 中，轮询立即命中）。
    /// </summary>
    private void OnPendingQuestionsCreated(IReadOnlyList<PendingQuestion> questions)
    {
        if (questions.Count == 0)
            return;

        // 微信回合里创建的问题由通道接管（发到手机上答）；两边都弹会让用户重复答同一题
        if (_weChatBridge?.IsTurnActive == true)
            return;

        _ = ShowAskUserDialogAsync();
    }

    /// <summary>
    /// 等待 AskUserTool 创建待处理 questions，然后显示对话框。
    /// 将 ask_user 的答案追加到 conversation 作为 user message，
    /// 这样用户可以在对话历史中看到自己的选择，LLM 也可以继续基于这个选择。
    /// </summary>
    /// <summary>
    /// 等待 AskUserTool 创建待处理 questions，然后显示对话框。
    /// 将 ask_user 的答案追加到 conversation 作为 user message，
    /// 这样用户可以在对话历史中看到自己的选择，LLM 也可以继续基于这个选择。
    /// </summary>
    private async Task ShowAskUserDialogAsync()
    {
        if (_questionStore is null)
            return;

        // AskUserTool.ExecuteAsync 在 ToolExecutionStart 之后才运行，
        // 需要轮询等待 questions 被创建出来（通常 < 100ms）
        IReadOnlyList<PendingQuestion> pending = [];
        for (var i = 0; i < 50; i++) // 最多等 500ms
        {
            await Task.Delay(10);
            _questionStore.GetAll();
            pending = _questionStore.GetPending();
            if (pending.Count > 0)
                break;
        }

        if (pending.Count == 0)
        {
            return;
        }

        await AskUserDialog.Show(_questionStore, pending);
        _ = AppendAskUserAnswersToConversation(pending);
    }

    /// <summary>
    /// 将 ask_user 的答案追加到 agent 消息列表。
    /// 语义上是 user message（LLM 需要看到），视觉上已由 tool result 的 QA 卡片展示。
    /// </summary>
    private async Task AppendAskUserAnswersToConversation(IReadOnlyList<PendingQuestion> questions)
    {
        // 等待一小段时间确保答案已写入 store
        await Task.Delay(50);

        // 重新从 store 读取：Show 期间 Answer 会替换记录，调用方持有的 pending 快照没有 SelectedAnswers。
        var answered = questions.Select(q => _questionStore?.Get(q.Id) ?? q).ToArray();
        var answerText = AskUserReplyFormatter.Format(answered);
        if (string.IsNullOrEmpty(answerText))
            return; // 没有有效答案

        // 追加到 agent 的消息列表；格式与 ask_user 工具结果完全一致，
        // MessageRestorer / Desktop 历史映射据此跳过气泡（Q&A 卡片已展示完整信息）。
        _agent.AppendMessage(new LlmChatMessage(ChatMessageRoles.User, answerText));
    }
}
