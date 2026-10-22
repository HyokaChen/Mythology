using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Channels.WeChat;
using Core.Agent;
using Core.Data;
using Core.Infrastructure;
using Core.Shared;
using Core.Tools;
using Desktop.Controls;
using Desktop.Models;
using Desktop.Services;
using Lucide.Avalonia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace Desktop.ViewModels;

/// <summary>
/// 主窗口视图模型（工作台布局，UI.md）：
/// Header（小说/对话切换）+ 对话流 + 右侧设定面板 + 状态栏。
/// 无通用工具导航（技能/Agent 不占主导航）。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly DesktopAgentBridge _bridge;
    private readonly FileSessionStore _sessionStore;
    private readonly INovelStore _store;
    private readonly Agent _agent;
    private readonly IConfiguration _configuration;
    private readonly CloudAuthService _cloudAuth;

    /// <summary>微信通道（Host 未接线时为 null，相关功能降级）。</summary>
    private readonly WeChatChannelService? _weChat;

    /// <summary>微信 ↔ Agent 桥接。</summary>
    private readonly WeChatAgentChannel? _weChatBridge;

    /// <summary>长轮询循环任务（启动后持引用，便于退出时观察）。</summary>
    private Task? _weChatLoop;
    private readonly UserQuestionStore _questionStore;
    private readonly Dictionary<string, ConversationViewModel> _activeVms = [];

    /// <summary>自动更新共享状态单例（弹窗与设置页「关于」同源）；设计时/容器缺失为 null。</summary>
    private readonly UpdateState? _updateState;

    /// <summary>更新检查调度器（Desktop 无 IHost，由 InitializeAsync 手动启动循环）。</summary>
    private readonly UpdateCheckScheduler? _updateScheduler;

    private Task? _updateLoop;
    private Task? _cloudInitTask;
    private long _totalTokens;
    private bool _hasContextMeasure;
    private long _measuredContextTokens;
    private bool _isCompacting;
    private readonly ContextTokenCalibrator _calibrator;
    private readonly ILogger _log;

    /// <summary>小说列表（Header 选择器）。</summary>
    public ObservableCollection<Novel> Novels { get; } = [];

    /// <summary>最近对话列表（JSONL 会话，每小说）。</summary>
    public ObservableCollection<Core.Data.AgentSession> RecentConversations { get; } = [];

    /// <summary>右侧设定面板（8-Tab）。</summary>
    public SetUpPanelViewModel SetUpPanel { get; }

    /// <summary>右侧 Todo 任务面板（SetUpPanel 下方）。</summary>
    public TodoPanelViewModel TodoPanel { get; }

    public Novel? SelectedNovel
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(CurrentNovelLabel));
            this.RaisePropertyChanged(nameof(NovelPillTip));
        }
    }

    /// <summary>Header 小说按钮文案：当前小说名，未选择时占位。</summary>
    public string CurrentNovelLabel => SelectedNovel?.Title ?? "选择小说";

    /// <summary>Header 小说胶囊 tooltip：完整书名（列表截断时补全）+ 动作提示。</summary>
    public string NovelPillTip =>
        SelectedNovel is { } novel
            ? $"当前小说：{novel.Title} · 切换 / 新建（Ctrl+O）"
            : "未选择小说 · 点击选择或新建（Ctrl+O）";

    public Core.Data.AgentSession? SelectedRecentConversation
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ConversationViewModel? ActiveConversationVm
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsProcessing
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>忙碌态（写作中或压缩中）：驱动状态栏 spinner（压缩期同转,对齐 TUI）。</summary>
    public bool IsBusy => IsProcessing || _isCompacting;

    /// <summary>自动更新弹窗状态（宿主见 MainWindow.axaml 的 controls:UpdatePromptDialog）。</summary>
    public UpdateState? UpdatePrompt => _updateState;

    public string StatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "idle";

    /// <summary>状态栏 tooltip：运行期 = 完整古诗词（对截断展示的补全）；空闲为空不加提示。</summary>
    public string? StatusTooltip
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string TokenText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "0 tok";

    /// <summary>状态栏上下文占用文案（如 12.3k / 128k）。</summary>
    public string ContextGaugeText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>上下文占用着色：正常 SageGray / ≥warning AmberPin / ≥100% CrimsonSpecimen。</summary>
    public IBrush ContextGaugeBrush
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = DialogTheme.SageGray;

    public string ModelText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "-";

    public string ToolsText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string ConversationTitle
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SessionPillTip));
        }
    } = "新对话";

    /// <summary>Header 会话胶囊 tooltip：完整会话名（列表截断时补全）+ 动作提示。</summary>
    public string SessionPillTip => $"当前会话：{ConversationTitle} · 切换 / 管理（Ctrl+E）";

    public bool IsCloudLoggedIn
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public LucideIconKind CloudButtonIcon
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = LucideIconKind.Cloud;

    public string CloudButtonTip
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "云端账号（点击登录/管理）";

    public string CloudStatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string CloudStatusHeader
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public IBrush CloudStatusForeground
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = DialogTheme.SageGray;

    public bool IsCloudInvalidBannerVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>云端同步标识（顶栏 ☁ 胶囊旁 ⇅）：同步中/已同步/冲突/离线；未登录隐藏。</summary>
    public string SyncStatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public IBrush SyncStatusForeground
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = DialogTheme.SageGray;

    public bool IsSyncVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>用户手动关闭过吊销横幅（状态离开 Invalid 时自动复位，重登成功横幅自动隐藏）。</summary>
    private bool _cloudBannerDismissed;

    /// <summary>云端小说同步引擎（cloud-novel-sync v3，未注册时为 null → 同步标识隐藏）。</summary>
    private readonly ClientPlatform.CloudNovelSyncService? _novelSync;

    /// <summary>云端用量客户端（:cloud status 积分余量；null = 未注册）。</summary>
    private readonly ClientPlatform.UsageRestClient? _cloudUsage;

    /// <summary>主题服务（浅/深色切换 + 持久化）。</summary>
    private readonly DesktopThemeService _theme;

    /// <summary>Header 主题按钮图标（浅色显示月亮 = 可切深色）。</summary>
    public LucideIconKind ThemeIcon => _theme.IsDark ? LucideIconKind.Sun : LucideIconKind.Moon;

    /// <summary>Header 主题按钮提示（指向切换目标，随主题状态翻转）。</summary>
    public string ThemeToggleTip =>
        _theme.IsDark ? "切换到浅色主题（Ctrl+T）" : "切换到深色主题（Ctrl+T）";

    /// <summary>切换主题（Ctrl+T / ⌘ Header 按钮 / /theme）。</summary>
    public void ToggleTheme() => _theme.Toggle();

    /// <summary>执行 /theme 命令：toggle | light | dark。</summary>
    private void ApplyThemeCommand(string mode)
    {
        switch (mode.Trim().ToLowerInvariant())
        {
            case "dark":
                _theme.SetDark(true);
                break;
            case "light":
                _theme.SetDark(false);
                break;
            default:
                _theme.Toggle();
                break;
        }
    }

    /// <summary>新建对话命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewConversationCommand { get; }

    /// <summary>构造函数。</summary>
    public MainWindowViewModel(
        IServiceProvider services,
        DesktopAgentBridge bridge,
        FileSessionStore sessionStore,
        INovelStore store,
        ToolRegistry registry,
        Agent agent,
        IConfiguration configuration,
        CloudAuthService cloudAuth,
        Core.Cloud.IFictionHarnessClient? cloudHarness = null,
        ILogger<MainWindowViewModel>? log = null,
        WeChatChannelService? weChat = null
    )
    {
        _services = services;
        _bridge = bridge;
        _sessionStore = sessionStore;
        _store = store;
        _log =
            log
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance;
        _agent = agent;
        _configuration = configuration;
        _cloudAuth = cloudAuth;
        _questionStore = services.GetRequiredService<UserQuestionStore>();
        _calibrator = services.GetRequiredService<ContextTokenCalibrator>();

        // 自动更新：解析共享状态并注入忙探针（有任务在跑时不弹、只亮红点，回合结束补弹）
        _updateState = services.GetService<UpdateState>();
        _updateScheduler = services.GetService<UpdateCheckScheduler>();
        if (_updateState is not null)
            _updateState.IsBusyProbe = () => IsBusy;

        // 微信桥接：入站跑在当前会话（与 TUI 一致），出站经 settle 聚合回微信。
        // Desktop 没有 IHost：循环由 InitializeAsync 手动启动、Shutdown 手动停止。
        _weChat = weChat;
        _weChatBridge = weChat is null
            ? null
            : new WeChatAgentChannel(
                weChat,
                RunWeChatTurnAsync,
                isAgentBusy: () => _bridge.IsProcessing,
                questionStore: _questionStore,
                logger: services.GetService<ILogger<WeChatAgentChannel>>()
            );

        SetUpPanel = new SetUpPanelViewModel(
            store,
            bridge,
            registry,
            cloudHarness,
            () => SelectedNovel?.Id,
            services.GetService<ILogger<SetUpPanelViewModel>>()
        );
        SetUpPanel.EntityReferenceRequested += text => ActiveConversationVm?.AppendInput(text);
        // 面板内整本删除（大纲书级 ⋯「删除」，已弹确认）→ 重列小说并切换到剩余一本
        SetUpPanel.NovelDeleted += async deletedId =>
        {
            await RefreshNovelsAsync();
            SelectedNovel = Novels.FirstOrDefault(n => n.Id != deletedId);
            if (SelectedNovel is null)
                await SetUpPanel.RefreshAsync(); // 空态：列表已空，provider 返回 null
        };
        // 面板内「⇅ 同步 / ⬇ 拉取」→ 当前小说双向同步 / 仅拉取，完成后重读面板（磁盘可能已变）
        SetUpPanel.RequestCloudSync += async pullOnly =>
        {
            if (_novelSync is null || SelectedNovel?.Id is not { Length: > 0 } novelId)
                return;
            try
            {
                await _novelSync.SyncAsync(novelId, pullOnly);
                await SetUpPanel.RefreshAsync();
            }
            catch (Exception ex)
            {
                ActiveConversationVm?.AddSystemNotice($"云端同步失败: {ex.Message}");
            }
        };

        TodoPanel = new TodoPanelViewModel(services.GetRequiredService<TodoStore>());

        // 主题服务（深/浅色切换）
        _theme = services.GetRequiredService<DesktopThemeService>();
        _theme.ThemeChanged += () =>
            Dispatcher.UIThread.Post(() =>
            {
                this.RaisePropertyChanged(nameof(ThemeIcon));
                this.RaisePropertyChanged(nameof(ThemeToggleTip));
            });

        // 状态栏：Token 计数（累计，步骤 5 替换为仪表）
        _bridge.OnTokenUsage += (prompt, completion) =>
            Dispatcher.UIThread.Post(() =>
            {
                _totalTokens += prompt + completion;
                TokenText = FormatTokens(_totalTokens);
                // 上下文占用仪表：以 API 实测 PromptTokens 为采样点校准
                _hasContextMeasure = true;
                _measuredContextTokens = prompt;
                _calibrator.AddSample(
                    Core.Agent.ConversationLoop.EstimateMessagesTokens(_agent.State.Messages),
                    prompt
                );
                UpdateContextGauge();
            });

        // Agent 事件单一入口扇出（全局决策 4） — 仅活跃对话处理流式事件；仪表/状态栏侧效果同 handler 内
        _bridge.OnStreamEvent += (_, evt) =>
            Dispatcher.UIThread.Post(() =>
            {
                // 微信回合的事件由通道自己处理（发到手机）；桌面 UI 只渲染本地对话
                if (_weChatBridge?.IsTurnActive == true)
                    return;
                ActiveConversationVm?.HandleAgentEvent(evt);
            });

        // 处理状态变更（活跃对话 SetProcessing 转发 + 状态栏：运行期随机古诗词，对齐 TUI StatusBar）
        _bridge.OnProcessingChanged += processing =>
            Dispatcher.UIThread.Post(() =>
            {
                IsProcessing = processing;
                this.RaisePropertyChanged(nameof(IsBusy));
                if (processing)
                {
                    var poem = PickPoem();
                    StatusText = poem;
                    StatusTooltip = poem;
                }
                else
                {
                    StatusText = IdleStatusText;
                    StatusTooltip = null;
                }
                ActiveConversationVm?.SetProcessing(processing);

                // 回合结束补弹（忙时只亮红点；无更新/已稍后提醒时自身 no-op）
                _updateState?.TryShowPrompt();
            });

        // HARNESS-CLOUD §5.4：Hook 侧（云端裁决）创建的问题也弹 AskUserWindow
        _questionStore.PendingQuestionsCreated += _ =>
            Dispatcher.UIThread.Post(async () =>
            {
                // 微信回合里创建的问题由通道接管（发到手机上答）；两边都弹会让用户重复答同一题
                if (_weChatBridge?.IsTurnActive == true)
                    return;

                if (ActiveConversationVm is not null)
                    await ActiveConversationVm.ShowAskUserDialogAsync();
            });

        // 云端登录状态（StateChanged → UI 线程刷新 Header ☁️ / 状态栏）
        _cloudAuth.StateChanged += _ => Dispatcher.UIThread.Post(RefreshCloudStatus);

        // 云端小说同步（cloud-novel-sync v3）：状态 → 顶栏 ⇅ 标识；启动 pull 在 InitializeCloudAsync 触发
        _novelSync = services.GetService<ClientPlatform.CloudNovelSyncService>();
        SetUpPanel.SetCloudStatus("", _novelSync is not null);
        if (_novelSync is not null)
        {
            _novelSync.StateChanged += state =>
                Dispatcher.UIThread.Post(() => UpdateSyncStatus(state));
        }

        // 云端用量（:cloud status 积分余量；未注册时为 null → 仅显示连接态）
        _cloudUsage = services.GetService<ClientPlatform.UsageRestClient>();

        // 设定面板实体刷新由 SetUpPanelViewModel 自订阅 OnStreamEvent（避免双重刷新），此处不重复订阅

        NewConversationCommand = ReactiveCommand.Create(NewConversation);

        // 小说选择 → 注入 Agent 上下文 + 刷新设定面板
        this.WhenAnyValue(x => x.SelectedNovel)
            .Where(x => x is not null)
            .Subscribe(async novel =>
            {
                if (novel is null)
                    return;
                await ApplyNovelAsync(novel);
            });

        // Header 对话下拉 → 打开对话
        this.WhenAnyValue(x => x.SelectedRecentConversation)
            .Where(x => x is not null)
            .Subscribe(session =>
            {
                if (session is null)
                    return;
                _ = OpenConversationAsync(session);
                SelectedRecentConversation = null;
            });
    }

    /// <summary>初始化（UI 就绪后调用）。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var configStore = _services.GetRequiredService<ConfigStore>();
            Core.CoreServiceExtensions.ApplySavedProviderConfig(_agent, configStore);

            _agent.SetSystemPrompt(_configuration["Agent:SystemPrompt"] ?? "");

            var registry = _services.GetRequiredService<ToolRegistry>();
            ToolsText = $"{registry.Count} tools";
            ModelText = _agent.State.ModelName ?? "-";

            // 云端自动登录校验（异步启动，与小说加载并行；不阻塞 UI）
            _cloudInitTask = InitializeCloudAsync();

            await RefreshNovelsAsync();
            _log.LogInformation("Novels loaded: {Count}", Novels.Count);

            if (Program.StartupNovelId is not null)
                SelectedNovel = Novels.FirstOrDefault(n => n.Id == Program.StartupNovelId);
            SelectedNovel ??= Novels.FirstOrDefault();
            _log.LogInformation("Selected novel: {Id}", SelectedNovel?.Id ?? "(null)");

            RefreshRecentConversations();

            // 微信通道：启动长轮询（仅当本进程持有轮询权；另一入口在用就让它跑）
            if (_weChatBridge is not null && _weChat?.HasOwnership == true)
            {
                _weChatLoop = _weChatBridge.RunAsync();
                _log.LogInformation("微信通道已启动（手机可直接对话）");
            }

            // 自动更新：启动检查循环（启动 +20s 首查、此后每日东八区 03:00；进程退出即终止）
            _updateLoop = _updateScheduler?.RunAsync();

            // 恢复小说最新会话（含 --session 直达）；无小说则留新会话
            await RestoreLatestSessionForNovelAsync(SelectedNovel?.Id ?? "");

            StatusText = IdleStatusText;

            await SetUpPanel.RefreshAsync();

            // ── 启动引导（云端登录 → LLM 配置 → 新建项目，对齐 TUI） ──
            await ShowStartupGuidanceAsync();
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "窗口初始化失败");
        }
    }

    /// <summary>
    /// 启动引导（docs/DESKTOP-LOGIN-FLOW.md §6）：
    /// 1. 云端：新用户（无小说+无凭据）或凭据失效 → 弹 CloudLoginWindow（可跳过）；
    /// 2. 未配置 LLM API Key → 弹 WelcomeWindow（引导去设置）；
    /// 3. 无任何小说 → 弹 ProjectPickerWindow（引导新建项目）。
    /// 已登录 / 离线老用户保持静默，不打扰。
    /// </summary>
    private async Task ShowStartupGuidanceAsync()
    {
        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: { } mw
            }
            ? mw
            : null;
        if (owner is null)
            return;

        try
        {
            // 0. 等待云端校验结果（最多 3 秒；超时按未登录处理，不阻塞引导）
            if (_cloudInitTask is not null)
            {
                try
                {
                    await _cloudInitTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException) { }
                catch (Exception) { }
            }

            // 1. 云端：新用户（无小说+无凭据）或凭据失效 → 登录窗（可跳过）
            var isNewUser = Novels.Count == 0 && _cloudAuth.State == CloudAuthState.SignedOut;
            var needsRelogin = _cloudAuth.State is CloudAuthState.Invalid;
            if (isNewUser || needsRelogin)
            {
                var login = new CloudLoginWindow(
                    _cloudAuth,
                    offerProjectCreation: Novels.Count == 0
                );
                await login.ShowDialog(owner);

                // §6.4 onboarding：登录成功且选择创建第一个项目 → 直达项目引导
                if (login.ProjectCreationRequested)
                {
                    ShowNovelPicker();
                    return;
                }
            }

            // 2. 未配置 API Key → 欢迎/引导
            if (_agent.TornadoApi is null)
            {
                var welcome = new WelcomeWindow(() =>
                {
                    // 打开设置窗口
                    var vm = _services.GetRequiredService<SettingsViewModel>().Refresh();
                    var window = new Views.SettingsWindow { DataContext = vm };
                    window.ShowDialog(owner);
                });
                await welcome.ShowDialog(owner);
            }

            // 3. 无任何小说 → 引导新建项目
            if (Novels.Count == 0 && Program.StartupNovelId is null)
            {
                var picker = new ProjectPickerWindow(
                    _store,
                    novelId =>
                    {
                        _ = ApplyNovelAndRefreshAsync(novelId);
                        RefreshRecentConversations();
                    },
                    _services.GetRequiredService<NovelWorkspace>(),
                    showLlmHint: _agent.TornadoApi is null // LLM 未配置 → 顶部提醒配置连接器
                );
                await picker.ShowDialog(owner);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "启动引导失败");
        }
    }

    /// <summary>创建项目后应用并进入该小说。</summary>
    private async Task ApplyNovelAndRefreshAsync(string novelId)
    {
        await RefreshNovelsAsync();
        var novel = Novels.FirstOrDefault(n => n.Id == novelId);
        if (novel is not null)
        {
            SelectedNovel = novel;
            await ApplyNovelAsync(novel);
            await RestoreLatestSessionForNovelAsync(novel.Id);
        }
    }

    /// <summary>刷新小说列表。</summary>
    public async Task RefreshNovelsAsync()
    {
        var novels = await _store.GetAllNovels();
        Novels.Clear();
        foreach (var novel in novels)
            Novels.Add(novel);
    }

    /// <summary>应用小说到 Agent。</summary>
    private async Task ApplyNovelAsync(Novel novel)
    {
        try
        {
            _agent.CurrentNovelId = novel.Id;
            var profile = WritingProfile.FromNovel(novel);
            _agent.SetNovelContext(profile.ToContextString());
            foreach (var vm in _activeVms.Values)
                vm.CurrentNovelTitle = novel.Title;
            ActiveConversationVm?.CurrentNovelTitle = novel.Title;
            await SetUpPanel.RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"加载小说失败: {ex.Message}";
        }
    }

    /// <summary>刷新最近对话列表。</summary>
    public void RefreshRecentConversations()
    {
        _ = RefreshRecentConversationsAsync();
    }

    private async Task RefreshRecentConversationsAsync()
    {
        var sessions = await _sessionStore.GetSessions(SelectedNovel?.Id ?? "");
        RecentConversations.Clear();
        foreach (var s in sessions)
            RecentConversations.Add(s);
    }

    /// <summary>/read：打开只读章节阅读器（窗口）。</summary>
    private void OpenChapterReader(string chapterId)
    {
        var novelId = SelectedNovel?.Id;
        if (string.IsNullOrEmpty(novelId))
            return;
        var owner = GetMainWindow();
        if (owner is null)
            return;
        new Controls.ChapterReaderWindow(_store, novelId, chapterId).Show(owner);
    }

    /// <summary>
    /// /chapter：在设定面板「大纲」Tab 定位章节——收窄到该章所属卷 + 标题过滤到该章
    /// （长篇不铺全量列表，用「范围 + 过滤」把目标顶到眼前）。
    /// </summary>
    private async Task JumpToChapterAsync(string chapterId)
    {
        var novelId = SelectedNovel?.Id;
        if (string.IsNullOrEmpty(novelId))
            return;

        var chapter = await _store.GetChapter(novelId, chapterId);
        if (chapter is null)
            return;

        if (chapter.VolumeId is { Length: > 0 } volumeId)
            SetUpPanel.SelectChapterScopeForVolume(volumeId);
        SetUpPanel.SearchText = chapter.Title;
        await SetUpPanel.RefreshAsync();
        SetUpPanel.SearchText = chapter.Title; // Refresh 重建列表后重新应用过滤
        ActiveConversationVm?.AddSystemNotice(
            $"已定位到第 {chapter.Order} 章「{chapter.Title}」（设定面板 · 大纲）"
        );
    }

    /// <summary>/search：打开全书检索结果窗口。</summary>
    private void ShowChapterSearchResults(
        string query,
        IReadOnlyList<Core.Novels.ChapterSearchHit> hits
    )
    {
        var novelId = SelectedNovel?.Id;
        if (string.IsNullOrEmpty(novelId))
            return;
        var owner = GetMainWindow();
        if (owner is null)
            return;
        new Controls.ChapterSearchWindow(_store, novelId, query, hits).Show(owner);
    }

    /// <summary>创建对话 VM 并注册。</summary>
    private ConversationViewModel CreateConversationVm()
    {
        var vm = _services.GetRequiredService<ConversationViewModel>();
        vm.CurrentNovelId = SelectedNovel?.Id;
        vm.CurrentNovelTitle = SelectedNovel?.Title ?? "未选择小说";
        vm.ConversationCompleted += () => Dispatcher.UIThread.Post(RefreshRecentConversations);
        vm.CompactStateChanged += active =>
            Dispatcher.UIThread.Post(() =>
            {
                _isCompacting = active;
                this.RaisePropertyChanged(nameof(IsBusy));
            });

        // 斜杠命令请求 → 主窗口动作
        vm.RequestNovelSwitch += () => Dispatcher.UIThread.Post(ShowNovelPicker);
        vm.RequestSessionPicker += () => Dispatcher.UIThread.Post(ShowSessionPicker);
        vm.RequestSettings += () => Dispatcher.UIThread.Post(() => OpenSettings());
        vm.ToggleThemeRequested += mode => Dispatcher.UIThread.Post(() => ApplyThemeCommand(mode));
        vm.RequestCloudLogin += () => Dispatcher.UIThread.Post(OpenCloudLogin);
        vm.RequestCloudLogout += () => Dispatcher.UIThread.Post(() => _cloudAuth.Logout());

        // 步骤 10/12 接线（原 TODO.md 五窗口落地）
        vm.RequestTree += () => Dispatcher.UIThread.Post(() => _ = ShowTreeWindow(vm));
        vm.RequestMode += () => Dispatcher.UIThread.Post(() => _ = CycleEnhanceModeAsync(vm));
        vm.ExploreVariantRaised += ed =>
            Dispatcher.UIThread.Post(() => _ = HandleVariantReviewAsync(vm, ed));
        vm.ContextStatsChanged += () => Dispatcher.UIThread.Post(UpdateContextGauge);
        vm.RequestCommandsDialog += () => Dispatcher.UIThread.Post(ShowCommandsReference);
        vm.RequestReopenVariants += () => Dispatcher.UIThread.Post(() => _ = ReopenVariants(vm));
        vm.RequestCloudColon += sub => Dispatcher.UIThread.Post(() => _ = ExecuteCloudCommand(sub));
        vm.RequestResume += novelId =>
            Dispatcher.UIThread.Post(() => _ = ResumeSessionAsync(vm, novelId));
        vm.SystemMessageRequested += text =>
            Dispatcher.UIThread.Post(() => vm.AddSystemNotice(text));
        vm.RequestChapterRead += chapterId =>
            Dispatcher.UIThread.Post(() => OpenChapterReader(chapterId));
        vm.RequestChapterJump += chapterId =>
            Dispatcher.UIThread.Post(() => _ = JumpToChapterAsync(chapterId));
        vm.RequestChapterSearchResults += (query, hits) =>
            Dispatcher.UIThread.Post(() => ShowChapterSearchResults(query, hits));
        return vm;
    }

    // ══════════════════════════════════════════════════════════
    //  分支树 / 增强模式 / 变体审阅 / 命令面板 / :cloud 子命令
    // ══════════════════════════════════════════════════════════

    /// <summary>/tree：加载树结构并弹分支树窗（前置守卫对齐 TUI TreeSelectorPanel）。</summary>
    private async Task ShowTreeWindow(ConversationViewModel vm)
    {
        var owner = GetMainWindow();
        if (owner is null)
            return;
        try
        {
            var tree = await _sessionStore.GetTreeStructure(vm.CurrentConversationId);
            if (tree.Nodes.Count == 0)
            {
                vm.AddSystemNotice("当前会话无消息，无法展示树结构");
                return;
            }
            if (!tree.HasBranches)
            {
                vm.AddSystemNotice("当前会话为线性对话，无分支点");
                return;
            }
            await new TreeWindow(tree, r => OnTreeNavigated(vm, r)).ShowDialog(owner);
        }
        catch (Exception ex)
        {
            vm.AddSystemNotice($"加载树结构失败: {ex.Message}");
        }
    }

    /// <summary>树导航回调：标签模式走 LabelAsync，否则切分支并重建消息 + 刷新会话列表（标题可能变化）。</summary>
    private void OnTreeNavigated(ConversationViewModel vm, NavigationResult r)
    {
        if (r.LabelText is not null)
        {
            // TUI 语义：BranchPoint = Target = 选中节点
            _ = vm.Engine.LabelAsync(r.EntryId, r.EntryId, r.LabelText);
            return;
        }
        _ = vm.ReloadFromBranchAsync(r.EntryId, r.GenerateSummary)
            .ContinueWith(
                _ => Dispatcher.UIThread.Post(RefreshRecentConversations),
                TaskScheduler.Default
            );
    }

    /// <summary>
    /// /resume [novel-id]：恢复指定小说的最新会话（镜像 TUI ResumeSessionAsync：
    /// 缺参提示用法；小说不存在 / 无活跃会话各有提示；否则切换小说并恢复最新会话）。
    /// </summary>
    private async Task ResumeSessionAsync(ConversationViewModel vm, string? novelId)
    {
        if (string.IsNullOrWhiteSpace(novelId))
        {
            vm.AddSystemNotice("用法: /resume <novel-id>");
            return;
        }
        try
        {
            if (await _store.GetNovel(novelId) is null)
            {
                vm.AddSystemNotice($"未找到小说 {novelId}");
                return;
            }
            var sessions = await _sessionStore.GetSessions(novelId);
            if (sessions.FirstOrDefault() is null)
            {
                vm.AddSystemNotice($"未找到小说 {novelId} 的活跃会话");
                return;
            }
            // 切换小说 + 应用上下文 + 恢复最新会话（ApplyNovelAndRefreshAsync 已含三步）
            await ApplyNovelAndRefreshAsync(novelId);
        }
        catch (Exception ex)
        {
            vm.AddSystemNotice($"恢复会话失败: {ex.Message}");
        }
    }

    /// <summary>/mode：增强模式循环 none→semi→full→none 并持久化到小说（镜像 TUI HandleModeCommandAsync）。</summary>
    private async Task CycleEnhanceModeAsync(ConversationViewModel vm)
    {
        var novelId = SelectedNovel?.Id;
        if (string.IsNullOrEmpty(novelId))
        {
            vm.AddSystemNotice("未选择小说，无法切换增强模式");
            return;
        }

        var current = "semi"; // 空/非法按 semi（对齐 TUI StatusBar 默认）
        try
        {
            var novel = await _store.GetNovel(novelId);
            if (novel is null)
            {
                vm.AddSystemNotice("未选择小说，无法切换增强模式");
                return;
            }
            if (!string.IsNullOrWhiteSpace(novel.AutoEnhance))
                current = novel.AutoEnhance;

            var newMode = current switch
            {
                "none" => "semi",
                "semi" => "full",
                _ => "none",
            };
            novel.AutoEnhance = newMode;
            using (Core.Evolution.OntologyWriteContext.BeginUser())
            {
                await _store.SaveNovel(novel);
            }

            // 重新注入 WritingProfile（单例 Agent，镜像 ApplyNovelAsync 行为）
            var profile = WritingProfile.FromNovel(novel);
            _agent.SetNovelContext(profile.ToContextString());

            vm.AddSystemNotice(
                $"增强模式: {newMode switch { "none" => "手动", "semi" => "半自动", _ => "全自动" }}"
            );
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "persist enhancement mode");
            // 模式切换已生效（UI 侧），仅持久化失败——不阻断用户（对齐 TUI）
            vm.AddSystemNotice("增强模式切换失败（持久化异常）");
        }
    }

    /// <summary>explore 变体审阅主入口：持久化变体组 → 弹窗 → 路由 Apply/Refine/Dismiss。</summary>
    private async Task HandleVariantReviewAsync(ConversationViewModel vm, ExploreDetails explore)
    {
        var owner = GetMainWindow();
        if (owner is null)
            return;
        await vm.Engine.PersistVariantSetAsync(explore);

        var window = new VariantReviewWindow(explore);
        await window.ShowDialog(owner);
        var decision = window.Decision;
        if (decision is null || decision.Action == VariantReviewAction.Dismiss)
            return; // 卡片保留在对话中，无需额外操作
        if (decision.Action == VariantReviewAction.Refine)
        {
            vm.BeginVariantRefinement(explore, decision);
            return;
        }
        await ApplyVariantAsync(vm, explore, decision);
    }

    /// <summary>/variants：重新打开最近变体组（LastVariantSet 由引擎维护）。</summary>
    private async Task ReopenVariants(ConversationViewModel vm)
    {
        if (vm.Engine.LastVariantSet is { } explore)
        {
            await HandleVariantReviewAsync(vm, explore);
            return;
        }
        vm.AddSystemNotice("没有待选择的变体");
    }

    /// <summary>
    /// 采用变体（镜像 TuiApp.Variant.cs HandleVariantApplyAsync）：
    /// 无章节目标 → 结构化实体提取 / 追加对话；有章节目标 → 确认+冲突校验 → write_chapter 工具写入。
    /// </summary>
    private async Task ApplyVariantAsync(
        ConversationViewModel vm,
        ExploreDetails explore,
        VariantReviewDecision decision
    )
    {
        if (vm.Engine.IsPersistenceFaulted)
        {
            vm.AddSystemNotice("会话保存故障尚未恢复，已拒绝采用变体以防止历史记录缺失。");
            return;
        }

        var variantContent = decision.VariantContent ?? "";
        var variantLabel = decision.VariantLabel ?? "";
        var target = explore.ApplyTarget;

        if (target is null)
        {
            // 非章节变体：根据 ExploreType 路由（对齐 TUI）
            if (
                explore.ExploreType is "character" or "plot" or "outline" or "style"
                && !string.IsNullOrWhiteSpace(variantContent)
            )
            {
                var typeName = explore.ExploreType switch
                {
                    "character" => "角色",
                    "plot" => "情节线",
                    "outline" => "大纲",
                    _ => "创作内容",
                };
                var extractPrompt =
                    $"用户从探索结果中选择了变体「{variantLabel}」——"
                    + $"一个{typeName}方案。\n\n"
                    + variantContent
                    + "\n\n请根据以上内容，使用相应的工具（如 batch_create_characters）创建结构化实体。"
                    + "如果内容为大纲/风格描述，直接将其作为参考存入上下文，不需要创建实体。";
                vm.AddSystemNotice($"📋 已采用变体「{variantLabel}」，正在提取结构化数据…");
                await vm.SendEnginePromptAsync(extractPrompt);
                return;
            }
            await vm.AppendVariantToConversationAsync(variantContent, variantLabel);
            return;
        }

        // ── 章节目标 ──
        var isCreate = string.IsNullOrEmpty(target.ChapterId);
        var chapterTitle = target.Title;
        var chapterOrder = target.Order;
        if (!isCreate && (string.IsNullOrWhiteSpace(chapterTitle) || chapterOrder <= 0))
        {
            var lookup = await _store.GetChapter(target.NovelId, target.ChapterId!);
            if (lookup is not null)
            {
                chapterTitle = lookup.Title;
                chapterOrder = lookup.Order;
            }
        }
        if (string.IsNullOrWhiteSpace(chapterTitle) || chapterOrder <= 0)
        {
            vm.AddSystemNotice("无法应用变体：缺少章节标题或序号");
            return;
        }

        var owner = GetMainWindow();
        if (owner is null)
            return;

        if (!isCreate)
        {
            var existing = await _store.GetChapter(target.NovelId, target.ChapterId!);
            if (existing is null)
            {
                vm.AddSystemNotice("无法覆盖：目标章节不存在");
                return;
            }

            // 版本保护：确认前快照 + 确认弹窗（覆盖不可逆）
            var snapshotContent = existing.Content;
            var confirm = new ConfirmDialog(
                $"将用变体覆盖「{existing.Title}」（{existing.Content.Length:N0} 字 → {variantContent.Length:N0} 字），此操作不可撤销。",
                "覆盖章节",
                "覆盖章节"
            );
            await confirm.ShowDialog(owner);
            if (!confirm.Confirmed)
                return;

            // 二次校验：确认→写入之间章节是否被外部修改
            var recheck = await _store.GetChapter(target.NovelId, target.ChapterId!);
            if (recheck?.Content != snapshotContent)
            {
                var conflict = new ConfirmDialog(
                    "章节在确认后发生了变化（外部修改）。强制覆盖将丢弃外部改动。",
                    "版本冲突",
                    "强制覆盖"
                );
                await conflict.ShowDialog(owner);
                if (!conflict.Confirmed)
                {
                    vm.AddSystemNotice("覆盖已取消：章节在确认后发生了变化");
                    return;
                }
            }
        }

        // 通过 ToolRegistry 走正式写章节路径（索引/审计/统计完整）
        var writeTool = _services.GetRequiredService<ToolRegistry>().TryGet("write_chapter");
        if (writeTool is null)
        {
            vm.AddSystemNotice("错误：未找到写章节工具");
            return;
        }

        var toolParamsJson = JsonSerializer.Serialize(
            new
            {
                novelId = target.NovelId,
                title = chapterTitle,
                order = chapterOrder,
                content = variantContent,
                chapterId = target.ChapterId,
            },
            JsonHelper.JsonOptions
        );
        using var doc = JsonDocument.Parse(toolParamsJson);

        try
        {
            var result = await writeTool.ExecuteAsync(
                Guid.NewGuid().ToString("N")[..12],
                doc.RootElement,
                CancellationToken.None
            );
            var resultText =
                (result.Content.ToList().FirstOrDefault() as TextContent)?.Text ?? "已写入";
            vm.AddSystemNotice($"✅ {resultText}（采用「{variantLabel}」）");
        }
        catch (Exception ex)
        {
            vm.AddSystemNotice($"写入失败：{ex.Message}");
        }
        finally
        {
            await SetUpPanel.RefreshAsync();
        }
    }

    /// <summary>Ctrl+K / Ctrl+L 面板入口：构建全局动作 + 斜杠命令列表并弹出。</summary>
    public void ShowCommandPalette()
    {
        var owner = GetMainWindow();
        if (owner is null)
            return;

        List<PaletteCommand> commands =
        [
            // ── 全局动作（直接调主窗口 VM；不进斜杠路由避免重复）──
            new()
            {
                Title = "新建对话",
                Shortcut = "Ctrl+N",
                Execute = () => NewConversationCommand.Execute().Subscribe(),
            },
            new()
            {
                Title = "选择 / 新建小说",
                Shortcut = "Ctrl+O",
                Execute = ShowNovelPicker,
            },
            new()
            {
                Title = "切换会话",
                Shortcut = "Ctrl+E",
                Execute = ShowSessionPicker,
            },
            .. (
                SelectedNovel is { } noteNovel
                    ? new[]
                    {
                        new PaletteCommand
                        {
                            Title = "笔记（记录 / 查看）",
                            Description = "作者随手记：确认候选、查看与编辑",
                            Execute = () => _ = ShowNotesWindowAsync(noteNovel.Id, noteNovel.Title),
                        },
                    }
                    : []
            ),
            new()
            {
                Title = "打开设置",
                Shortcut = "Ctrl+P",
                Execute = () => OpenSettings(),
            },
            new()
            {
                Title = "快速切换模型",
                Description = "当前提供商的模型列表",
                Execute = () => _ = ShowModelQuickSwitchAsync(),
            },
            new()
            {
                Title = "帮助与关于",
                Shortcut = "Ctrl+H",
                Execute = () => new AboutDialog().ShowDialog(owner),
            },
            new()
            {
                Title = "切换深色 / 浅色主题",
                Shortcut = "Ctrl+T",
                Execute = ToggleTheme,
            },
            new()
            {
                Title = "清空当前对话",
                Shortcut = "Ctrl+L",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/clear"),
            },
            new()
            {
                Title = "云端同步",
                Description = ":cloud sync — 推送并拉取全部小说",
                Execute = () => _ = ExecuteCloudCommand("sync"),
            },
            new()
            {
                Title = "仅拉取云端",
                Description = ":cloud pull",
                Execute = () => _ = ExecuteCloudCommand("pull"),
            },
            new()
            {
                Title = "云端状态",
                Description = ":cloud status",
                Execute = () => _ = ExecuteCloudCommand("status"),
            },
            new()
            {
                Title = "命令速查",
                Description = "查看全部命令与快捷键",
                Execute = ShowCommandsReference,
            },
            // ── 斜杠命令（经活跃对话 VM 路由）──
            new()
            {
                Title = "/state",
                Description = "Agent 状态 + 工具统计 + 写作统计",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/state"),
            },
            new()
            {
                Title = "/history",
                Description = "查看对话历史",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/history"),
            },
            new()
            {
                Title = "/tools",
                Description = "列出可用工具",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/tools"),
            },
            new()
            {
                Title = "/compact",
                Description = "压缩历史（保留最近 N 条）",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/compact"),
            },
            new()
            {
                Title = "/compact smart",
                Description = "智能压缩",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/compact smart"),
            },
            new()
            {
                Title = "/decompress",
                Description = "恢复被压缩的内容",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/decompress"),
            },
            new()
            {
                Title = "/tree",
                Description = "对话分支树导航",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/tree"),
            },
            new()
            {
                Title = "/mode",
                Description = "切换增强模式（手动 / 半自动 / 全自动）",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/mode"),
            },
            new()
            {
                Title = "/context",
                Description = "上下文管理模式（压缩 / 接续，立即生效）",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/context"),
            },
            new()
            {
                Title = "/read",
                Description = "读章节（序号 / ID / 标题片段；阅读器可翻章）",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/read"),
            },
            new()
            {
                Title = "/chapter",
                Description = "定位章节（设定面板 · 大纲）",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/chapter"),
            },
            new()
            {
                Title = "/search",
                Description = "全书检索（正文 / 细纲 / 标题）",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/search"),
            },
            new()
            {
                Title = "/variants",
                Description = "重新打开变体审阅",
                Execute = () => ActiveConversationVm?.TryExecuteCommand("/variants"),
            },
        ];

        new CommandPaletteWindow(commands).ShowDialog(owner);
    }

    /// <summary>/commands → AboutDialog 命令 Tab（命令速查；不回指命令面板，杜绝递归弹窗）。</summary>
    private void ShowCommandsReference()
    {
        var owner = GetMainWindow();
        if (owner is null)
            return;
        _ = new AboutDialog(1).ShowDialog(owner);
    }

    /// <summary>:cloud 子命令（sync/pull/status/login/logout）。</summary>
    private async Task ExecuteCloudCommand(string sub)
    {
        void Notice(string text)
        {
            ActiveConversationVm?.AddSystemNotice(text);
            StatusText = text; // 状态栏同步简短态
        }

        switch (sub)
        {
            case "sync":
                if (_novelSync is null)
                {
                    Notice("云端同步不可用");
                    return;
                }
                try
                {
                    var r = await _novelSync.SyncAllAsync();
                    Notice($"云端同步完成：推送 {r.Pushed} · 拉取 {r.Pulled} · 冲突 {r.Conflicts}");
                    await RefreshNovelsAsync();
                    RefreshRecentConversations();
                }
                catch (Exception ex)
                {
                    Notice($"云端同步失败: {ex.Message}");
                }
                return;

            case "pull":
                if (_novelSync is null)
                {
                    Notice("云端同步不可用");
                    return;
                }
                try
                {
                    var pulled = await _novelSync.PullAllNovelsAsync();
                    if (pulled.Count > 0)
                    {
                        await RefreshNovelsAsync();
                        Notice($"已从云端拉取 {pulled.Count} 本小说");
                    }
                    else
                        Notice("云端已是最新");
                }
                catch (Exception ex)
                {
                    Notice($"云端拉取失败: {ex.Message}");
                }
                return;

            case "status":
                Notice(CloudStatusText);
                // 积分余量（零价查询）：已登录且已注册用量客户端时追加一条；失败显式提示
                //（Desktop Notice 即状态栏，一次性命令，失败可见性优于静默）。
                if (_cloudUsage is not null && IsCloudLoggedIn)
                {
                    try
                    {
                        var usage = await _cloudUsage.GetUsageAsync();
                        Notice(
                            $"云端积分：{usage.Period} 已用 {usage.UsedUnits} / {usage.IncludedUnits}（剩余 {usage.RemainingUnits}）"
                        );
                    }
                    catch (Exception ex)
                    {
                        Notice($"云端积分查询失败: {ex.Message}");
                    }
                }
                return;

            case "login":
                OpenCloudLogin();
                return;

            case "logout":
                _cloudAuth.Logout();
                return;

            default:
                Notice($"未知 :cloud 子命令: {sub}");
                return;
        }
    }

    public void RefreshCloudStatus()
    {
        try
        {
            var state = _cloudAuth.State;
            var accountLabel = _cloudAuth.DisplayName is { Length: > 0 } dn
                ? dn
                : _cloudAuth.PublicId ?? "";
            IsCloudLoggedIn = state == CloudAuthState.SignedIn;

            // §13.6 吊销横幅：仅 Invalid 态显示，且尊重用户手动关闭；离开 Invalid 态自动复位
            if (state != CloudAuthState.Invalid)
                _cloudBannerDismissed = false;
            IsCloudInvalidBannerVisible = state == CloudAuthState.Invalid && !_cloudBannerDismissed;

            CloudButtonIcon = state switch
            {
                CloudAuthState.SignedIn => LucideIconKind.Cloud,
                CloudAuthState.Validating => LucideIconKind.LoaderCircle,
                CloudAuthState.Invalid => LucideIconKind.Lock,
                CloudAuthState.Offline
                or CloudAuthState.RateLimited
                or CloudAuthState.QuotaExceeded => LucideIconKind.TriangleAlert,
                _ => LucideIconKind.CloudOff,
            };

            CloudButtonTip = state switch
            {
                CloudAuthState.SignedIn when accountLabel is { Length: > 0 } =>
                    $"云端账号：{accountLabel}（{_cloudAuth.Plan}）· 点击管理",
                CloudAuthState.SignedIn => "已登录云端 · 点击管理",
                CloudAuthState.Validating => "正在校验登录状态…",
                CloudAuthState.Invalid => "登录已失效 · 点击重新登录",
                CloudAuthState.Offline => "云端离线 · 凭据已保留 · 点击查看",
                CloudAuthState.RateLimited => "云端限流中",
                CloudAuthState.QuotaExceeded => "云端配额不足",
                CloudAuthState.SignedOut => "未登录云端 · 点击登录",
                _ => "云端账号（点击登录/管理）",
            };

            CloudStatusText = state switch
            {
                CloudAuthState.SignedIn when accountLabel is { Length: > 0 } =>
                    $"{accountLabel} 已登录",
                CloudAuthState.SignedIn => "已登录",
                CloudAuthState.Validating => "校验登录…",
                CloudAuthState.Invalid => "登录已失效，点击云端重新登录",
                CloudAuthState.Offline => "云端离线",
                CloudAuthState.RateLimited => "云端限流",
                CloudAuthState.QuotaExceeded => "云端配额不足",
                CloudAuthState.SignedOut => "未登录云端",
                _ => "",
            };

            // 顶部标题栏紧凑版：短标签，随登录态变化
            CloudStatusHeader = state switch
            {
                CloudAuthState.SignedIn when accountLabel is { Length: > 0 } =>
                    $"{accountLabel} · 已连接",
                CloudAuthState.SignedIn => "已连接",
                CloudAuthState.Validating => "校验中",
                CloudAuthState.Invalid => "未连接",
                CloudAuthState.Offline => "离线",
                CloudAuthState.RateLimited => "限流",
                CloudAuthState.QuotaExceeded => "配额不足",
                CloudAuthState.SignedOut => "未登录",
                _ => "",
            };

            // 云端胶囊着色：连接成功绿；校验/离线/限流/配额警示橙；失效红；其余灰
            CloudStatusForeground = state switch
            {
                CloudAuthState.SignedIn => DialogTheme.SageLeaf,
                CloudAuthState.Invalid => DialogTheme.CrimsonSpecimen,
                CloudAuthState.Validating
                or CloudAuthState.Offline
                or CloudAuthState.RateLimited
                or CloudAuthState.QuotaExceeded => DialogTheme.AmberPin,
                _ => DialogTheme.SageGray,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "刷新云端状态失败");
            IsCloudLoggedIn = false;
        }
        finally
        {
            // 登录态变化 → 同步标识可见性联动（未登录隐藏 ⇅）
            UpdateSyncStatus(_novelSync?.State ?? ClientPlatform.CloudSyncState.Idle);
        }
    }

    /// <summary>⇅ 同步标识（顶栏）：状态文本与着色随 CloudSyncState 变化。</summary>
    private void UpdateSyncStatus(ClientPlatform.CloudSyncState state)
    {
        var signedIn = _cloudAuth.IsSignedIn;
        IsSyncVisible = signedIn && state != ClientPlatform.CloudSyncState.Idle;
        SetUpPanel.SetCloudStatus(
            state switch
            {
                ClientPlatform.CloudSyncState.Syncing => "⇅ 同步中…",
                ClientPlatform.CloudSyncState.Synced => "⇅✓ 已与云端同步",
                ClientPlatform.CloudSyncState.Conflicts => "⇅! 有未合并冲突",
                ClientPlatform.CloudSyncState.Offline => "⇅ø 云端不可达",
                _ => signedIn ? "⇅ 尚未同步" : "⇅ 未登录",
            },
            _novelSync is not null
        );
        SyncStatusText = state switch
        {
            ClientPlatform.CloudSyncState.Syncing => "⇅ 同步中…",
            ClientPlatform.CloudSyncState.Synced => "⇅ 已同步",
            ClientPlatform.CloudSyncState.Conflicts => "⇅ 有冲突",
            ClientPlatform.CloudSyncState.Offline => "⇅ 离线",
            _ => "",
        };
        SyncStatusForeground = state switch
        {
            ClientPlatform.CloudSyncState.Syncing => DialogTheme.SageLeaf,
            ClientPlatform.CloudSyncState.Synced => DialogTheme.SageLeaf,
            ClientPlatform.CloudSyncState.Conflicts => DialogTheme.AmberPin,
            ClientPlatform.CloudSyncState.Offline => DialogTheme.SageGray,
            _ => DialogTheme.SageGray,
        };
    }

    /// <summary>
    /// 启动自动 pull（登录成功后静默执行）：拉取云端全部小说最新到本地。
    /// pull-only 不上传——启动时尚不知本地是否有未同步编辑，避免意外 push；
    /// 冲突由三路合并保护（本地改动保留 / 冲突副本落 cloud-conflicts）。
    /// 失败仅转 ⇅ 离线标识，不打扰创作。
    /// </summary>
    private async Task RunStartupSyncAsync()
    {
        if (_novelSync is null)
        {
            return;
        }
        try
        {
            var pulled = await _novelSync.PullAllNovelsAsync(CancellationToken.None);
            if (pulled.Count > 0)
            {
                // 新小说可能被拉取 → 刷新小说列表
                await RefreshNovelsAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "启动云端同步失败（不影响本地创作）");
        }
    }

    /// <summary>Entrypoint 1：启动自动登录校验（凭据存在时静默 Probe，不打扰创作）。</summary>
    private async Task InitializeCloudAsync()
    {
        try
        {
            await _cloudAuth.InitializeAsync(CancellationToken.None);
            // 已登录 → 启动自动 pull 云端最新（静默）
            if (_cloudAuth.IsSignedIn)
            {
                _ = RunStartupSyncAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "云端初始化失败");
        }
        finally
        {
            RefreshCloudStatus();
        }
    }

    /// <summary>Entrypoint 2：打开独立云端登录窗口（无小说时提供"创建第一个项目"衔接）。</summary>
    public void OpenCloudLogin() => _ = OpenCloudLoginAsync();

    private async Task OpenCloudLoginAsync()
    {
        var owner = GetMainWindow();
        if (owner is null)
            return;
        var login = new CloudLoginWindow(_cloudAuth, offerProjectCreation: Novels.Count == 0);
        await login.ShowDialog(owner);

        // §6.4 onboarding：登录成功且选择创建第一个项目 → 直达项目引导
        if (login.ProjectCreationRequested)
            ShowNovelPicker();
    }

    /// <summary>Header ☁️ 点击分流：已登录 → 账号管理；未登录/失效 → 登录窗。</summary>
    public void HandleCloudButtonClick()
    {
        if (_cloudAuth.IsSignedIn)
            OpenSettings(initialTab: 6); // 账号 Tab（顺序：连接器0/嵌入1/上下文2/MCP3/工具4/外观5/账号6）
        else
            OpenCloudLogin();
    }

    /// <summary>吊销横幅「立即重新登录」→ 与 Header ☁️ 失效态一致：打开登录窗（非模态，不打断创作）。</summary>
    public void ReloginFromInvalidBanner() => OpenCloudLogin();

    /// <summary>关闭吊销横幅（不触发重登；状态离开 Invalid 或重登成功后自动复位并隐藏）。</summary>
    public void DismissCloudInvalidBanner()
    {
        _cloudBannerDismissed = true;
        IsCloudInvalidBannerVisible = false;
    }

    /// <summary>打开设置窗口（可选定位到指定 Tab）。</summary>
    public void OpenSettings(int initialTab = 0)
    {
        var owner = GetMainWindow();
        if (owner is null)
            return;
        var vm = _services.GetRequiredService<SettingsViewModel>().Refresh();
        var window = new Views.SettingsWindow(initialTab) { DataContext = vm };
        window.ShowDialog(owner);
    }

    /// <summary>当前连接的提供商密钥条目（镜像 LlmApiFactory 匹配：自定义按名称，预设按 ConfigKey）。</summary>
    private static ApiKeyEntry? ResolveProviderEntry(ProviderConfig cfg) =>
        ProviderDefinition.IsCustom(cfg.Default)
            ? cfg.ApiKeys.FirstOrDefault(k =>
                k.Provider == "custom" && k.Name == ProviderDefinition.GetCustomName(cfg.Default)
            )
            : cfg.ApiKeys.FirstOrDefault(k => k.Provider == cfg.Default);

    /// <summary>状态栏模型区快速切换：列出当前提供商可用模型（发现失败/无 Key 给系统提示，引导去设置）。</summary>
    public async Task ShowModelQuickSwitchAsync()
    {
        if (IsProcessing)
        {
            StatusText = "写作中…（先停止再切换模型）";
            return;
        }
        var owner = GetMainWindow();
        if (owner is null)
            return;

        var cfg = _services.GetRequiredService<ConfigStore>().Load().Providers;
        var entry = ResolveProviderEntry(cfg);
        var def = ProviderDefinition.IsCustom(cfg.Default)
            ? null
            : ProviderDefinition.FindByConfigKey(cfg.Default);
        if (entry is null || string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            ActiveConversationVm?.AddSystemNotice("该提供商未配置 API Key，请先在设置中填写");
            return;
        }
        if (def is null && string.IsNullOrWhiteSpace(entry.BaseUrl))
        {
            ActiveConversationVm?.AddSystemNotice("自定义提供商需先在设置中填写 Base URL");
            return;
        }

        var models = await ModelDiscoveryService.GetModelsAsync(entry, def);
        if (models.Count == 0)
        {
            ActiveConversationVm?.AddSystemNotice(
                "未获取到模型列表（检查网络或 API Key），也可在设置中手动输入"
            );
            return;
        }

        await new ModelQuickSwitchWindow(
            _agent.State.ModelName ?? "",
            models,
            m => _ = ApplyQuickModelAsync(m)
        ).ShowDialog(owner);
    }

    /// <summary>应用快速切换：写配置 → 重建 API/失效 Loop → 刷新稳定器阈值/校准/状态栏。</summary>
    private async Task ApplyQuickModelAsync(string newModel)
    {
        try
        {
            if (string.Equals(_agent.State.ModelName, newModel, StringComparison.OrdinalIgnoreCase))
                return;

            var configStore = _services.GetRequiredService<ConfigStore>();
            var config = configStore.Load();
            config.Providers.DefaultModel = newModel;
            await configStore.SaveAsync(config);

            var (api, _, handler) = LlmApiFactory.CreateFromProviderConfig(config.Providers);
            _agent.InvalidateConversationLoop(); // 冻结字段必须失效重建（同设置保存路径）
            _agent.TornadoApi = api;
            _agent.SetHttpHandler(handler);
            _agent.SetModelName(newModel);
            _agent.SetThinkingLevel(config.Providers.ThinkingLevel);
            Desktop.Services.AgentProfile.Reapply(_agent, configStore);

            _calibrator.Reset(); // 新模型 tokenizer 口径不同，下次发消息重新学习
            RefreshModelStatus();
            // 静默切换：状态栏模型名已更新，无需左下角额外文案（用户反馈）
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "快速切换模型失败");
            ActiveConversationVm?.AddSystemNotice($"⚠️ 模型切换失败：{ex.Message}");
        }
    }

    /// <summary>获取主窗口（作为对话框所有者）。</summary>
    private static Window? GetMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: { } mw
            }
            ? mw
            : null;

    /// <summary>显示小说项目选择器（对齐 TUI Ctrl+O / 命令面板）。</summary>
    public void ShowNovelPicker()
    {
        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: { } mw
            }
            ? mw
            : null;
        if (owner is null)
            return;
        var picker = new ProjectPickerWindow(
            _store,
            novelId =>
            {
                _ = ApplyNovelAndRefreshAsync(novelId);
            },
            _services.GetRequiredService<NovelWorkspace>(),
            showLlmHint: _agent.TornadoApi is null, // LLM 未配置 → 顶部提醒配置连接器
            currentNovelId: SelectedNovel?.Id // 「当前」标记 + 预选（回车即切回）
        );
        picker.ShowDialog(owner);
    }

    /// <summary>显示会话管理器（对齐 TUI Ctrl+E / /session）。</summary>
    public void ShowSessionPicker()
    {
        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: { } mw
            }
            ? mw
            : null;
        if (owner is null)
            return;
        var window = new SessionManagerWindow(
            loader: () => _sessionStore.GetSessions(SelectedNovel?.Id ?? ""),
            onOpen: session =>
            {
                // 切到该会话（已有 VM 则复用，否则新建加载）
                _ = OpenConversationAsync(session);
                RefreshRecentConversations();
            },
            onDelete: async session => await DeleteSessionAsync(session),
            currentSessionId: ActiveConversationVm?.CurrentConversationId // 「当前」标记 + 预选
        );
        window.ShowDialog(owner);
    }

    /// <summary>笔记窗口：作者随手记（确认候选 / 查看 / 编辑）。</summary>
    private Task ShowNotesWindowAsync(string novelId, string novelTitle)
    {
        var owner = GetMainWindow();
        if (owner is null)
            return Task.CompletedTask;
        var kernel = _services.GetService<Core.Vector.RetrievalKernel>();
        var window = new NotesWindow(_store, kernel, novelId, novelTitle);
        return window.ShowDialog(owner);
    }

    /// <summary>删除会话（Core API + 移除缓存 + 删的是当前会话 → 建新对话）。</summary>
    private async Task DeleteSessionAsync(Core.Data.AgentSession session)
    {
        var deleted = await _sessionStore.DeleteSessionAsync(session.Id);
        if (!deleted)
            return;
        _activeVms.Remove(session.Id);
        if (ActiveConversationVm?.CurrentConversationId == session.Id)
            NewConversation();
        RefreshRecentConversations();
    }

    /// <summary>切换到对话。</summary>
    public void SwitchToConversation(ConversationViewModel vm)
    {
        if (_bridge.IsProcessing)
            _bridge.Stop();
        ActiveConversationVm = vm;
        vm.Activate();
        InvalidateContextMeasure();
        _ = UpdateConversationTitleAsync(vm);
    }

    private async Task UpdateConversationTitleAsync(ConversationViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.CurrentConversationId))
        {
            ConversationTitle = "新对话";
            return;
        }
        var session = await _sessionStore.GetSession(vm.CurrentConversationId);
        ConversationTitle = string.IsNullOrEmpty(session?.Title) ? "新对话" : session!.Title;
    }

    /// <summary>侧边栏/下拉点击对话。</summary>
    public async Task OpenConversationAsync(Core.Data.AgentSession session)
    {
        if (_activeVms.TryGetValue(session.Id, out var existing))
        {
            SwitchToConversation(existing);
        }
        else
        {
            var vm = CreateConversationVm();
            await vm.LoadConversationAsync(session.Id);
            _activeVms[vm.CurrentConversationId] = vm;
            SwitchToConversation(vm);
        }
    }

    /// <summary>新建对话。</summary>
    private void NewConversation()
    {
        var vm = CreateConversationVm();
        vm.NewConversationCommand.Execute().Subscribe();
        _activeVms[vm.CurrentConversationId] = vm;
        SwitchToConversation(vm);
        InvalidateContextMeasure();
        RefreshRecentConversations();
    }

    /// <summary>
    /// 恢复小说最新会话（含 --session 启动直达）；无会话时留空（新对话由调用方处理）。
    /// </summary>
    private async Task RestoreLatestSessionForNovelAsync(string novelId)
    {
        var sessions = await _sessionStore.GetSessions(novelId);
        var target = Program.StartupSessionId is { } sid
            ? sessions.FirstOrDefault(s => s.Id == sid)
            : null;
        target ??= sessions.FirstOrDefault();
        if (target is not null)
        {
            if (!_activeVms.TryGetValue(target.Id, out var vm))
            {
                vm = CreateConversationVm();
                await vm.LoadConversationAsync(target.Id);
                _activeVms[vm.CurrentConversationId] = vm;
            }
            SwitchToConversation(vm);
        }
        else
        {
            // 无会话 → 自动创建新对话（与 TUI ResumeSessionAsync 新会话分支一致）
            NewConversation();
        }
    }

    /// <summary>上下文测量作废（切换/新建/compact/清空后调用；旧 API 实测值不适用新上下文）。</summary>
    public void InvalidateContextMeasure()
    {
        _hasContextMeasure = false;
        _measuredContextTokens = 0;
        UpdateContextGauge();
    }

    /// <summary>模型/上下文窗口变更后刷新状态栏（设置保存与快速切换共用）。</summary>
    public void RefreshModelStatus()
    {
        ModelText = _agent.State.ModelName ?? "-";
        UpdateContextGauge();
    }

    /// <summary>
    /// 刷新状态栏上下文占用（会话上下文口径 0 起算，不含固定开销）：
    /// 有实测且已校准时用 ToConversationTokens；否则回退 ProjectConversation 估算。
    /// 超 100% 变 Crimson，超 warningRatio（ModelProfile.CompactThresholdPercent）变 Amber。
    /// </summary>
    public void UpdateContextGauge()
    {
        var window =
            _agent.ModelProfile?.ContextWindowTokens
            ?? _services.GetRequiredService<ConfigStore>().Load().Providers.ContextWindowSize;
        if (window <= 0)
        {
            ContextGaugeText = "";
            return;
        }

        long used;
        if (_hasContextMeasure && _calibrator.HasCalibration)
            used = _calibrator.ToConversationTokens(_measuredContextTokens);
        else
            used = _calibrator.ProjectConversation(
                Core.Agent.ConversationLoop.EstimateMessagesTokens(_agent.State.Messages)
            );

        if (used <= 0)
        {
            ContextGaugeText = $"0 / {FormatTokens(window)}";
            ContextGaugeBrush = DialogTheme.SageGray;
            return;
        }

        var ratio = (double)used / window;
        var warningRatio = _agent.ModelProfile?.CompactThresholdPercent ?? 0.9;
        ContextGaugeText = $"{FormatTokens(used)} / {FormatTokens(window)}";
        ContextGaugeBrush =
            ratio >= 1.0 ? DialogTheme.CrimsonSpecimen
            : ratio >= warningRatio ? DialogTheme.AmberPin
            : DialogTheme.SageGray;
    }

    /// <summary>微信回合：跑在当前打开的会话上（与 TUI 一致；两侧同一套行为）。</summary>
    private async Task RunWeChatTurnAsync(string text, CancellationToken ct)
    {
        var engine = ActiveConversationVm?.Engine;
        if (engine is null)
        {
            // 没有会话可跑：给一句可操作的提示，而不是静默失败
            if (_weChat is not null)
                await _weChat.SendTextAsync("请先在桌面端打开一个会话，再从微信发消息。", ct);
            return;
        }

        await engine.RunPromptAsync(text, ct);
    }

    /// <summary>空闲态状态栏文案（未配置 LLM Key 时引导去设置）。</summary>
    private string IdleStatusText =>
        _agent.TornadoApi is null ? "未配置 LLM API Key，请点击右上角设置" : "idle";

    /// <summary>随机取一句古诗词（运行期状态栏展示；诗词库与 TUI 共享 Core.Data.PoetryStore）。</summary>
    private static string PickPoem()
    {
        var lines = Core.Data.PoetryStore.Lines;
        return lines.Length == 0 ? "写作中…" : lines[Random.Shared.Next(lines.Length)];
    }

    /// <summary>窗口关闭：结算预算 + 中断 Agent。</summary>
    public void Shutdown()
    {
        try
        {
            // 宿主退出结算（§16.5 方案 C）：同步调用 SettleSession（同步方法，Shutdown 同步上下文合法）
            var settleNovelId = _agent.CurrentNovelId;
            if (Program.CloudScheduler is not null && !string.IsNullOrEmpty(settleNovelId))
                Program.CloudScheduler.SettleSession(settleNovelId);
            _cloudAuth.Dispose();
            TodoPanel.Dispose();
            _agent.Abort();

            // 微信桥接：停长轮询 + 释放通道独占锁（不阻塞退出，锁由 OS 兜底释放）
            if (_weChatBridge is not null)
                _ = _weChatBridge.DisposeAsync();
            _weChatLoop = null;
        }
        catch
        {
            // 忽略关闭时的中断异常
        }
    }

    private static string FormatTokens(long tokens)
    {
        return tokens switch
        {
            >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M tok",
            >= 1_000 => $"{tokens / 1_000.0:F1}k tok",
            _ => $"{tokens} tok",
        };
    }
}
