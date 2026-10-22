using Avalonia;
using ClientPlatform;
using CloudDiagnosis;
using Core;
using Core.Data;
using Core.Forecast;
using Core.Shared;
using Core.Stability;
using Core.Tools;
using Core.Vector;
using Desktop.Services;
using Desktop.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI.Avalonia.Splat;

namespace Desktop;

/// <summary>
/// 桌面端入口：构建 DI 容器（Core 共享服务 + Desktop 特有服务），启动 Avalonia 应用。
/// 参考 LittleKingKnows.App（LKK）的启动模式：UseReactiveUIWithMicrosoftDependencyResolver。
/// </summary>
public static class Program
{
    // ── 命令行选项常量 ──
    private const string OptDebug = "--debug";
    private const string OptMock = "--mock";
    private const string OptNovel = "--novel";
    private const string OptNovels = "--novels"; // 指定小说项目目录（与 TUI 的 cwd/novels 语义对齐）
    private const string OptSession = "--session"; // 启动直达指定会话（对齐 Host --session）

    /// <summary>应用级服务提供器（App.OnFrameworkInitializationCompleted 中解析视图模型）。</summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>云端诊断调度器（宿主退出时结算预算用；InitializeRuntime 装配，Shutdown 消费）。</summary>
    public static DiagnosisScheduler? CloudScheduler { get; private set; }

    /// <summary>
    /// 上下文管理模式热切换器（InitializeRuntime 装配：需要运行期创建的 PluginLoader / ToolSearchEngine）。
    /// 设置页「上下文」卡片读取它做 compact ↔ continuation 即时应答；null = 未接线（设计时/测试）。
    /// </summary>
    public static Core.Plugins.ContextModeSwitcher? ContextModeSwitcher { get; private set; }

    // 运行参数（App 构造前已解析）
    public static bool UseDebug { get; private set; }
    public static bool UseMock { get; private set; }
    public static string? StartupNovelId { get; private set; }

    /// <summary>--novels 指定的项目目录（打包后 cwd 不可控，必须有显式入口）。</summary>
    public static string? StartupNovelsPath { get; private set; }

    /// <summary>--session 指定的会话 ID（JSONL 目录 MANIFEST 的 sessionId）。</summary>
    public static string? StartupSessionId { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party API or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // ── 加载 .env（本地开发环境变量，已 export 的环境变量不受影响）──
        DotEnv.TryLoad();

        // ── 解析命令行参数 ──
        var argsList = args.ToList();
        UseDebug = argsList.Contains(OptDebug);
        UseMock = argsList.Contains(OptMock);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == OptNovel && i + 1 < args.Length)
                StartupNovelId = args[++i];
            if (args[i] == OptNovels && i + 1 < args.Length)
                StartupNovelsPath = args[++i];
            if (args[i] == OptSession && i + 1 < args.Length)
                StartupSessionId = args[++i];
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fatal] 应用异常: {ex}");
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUIWithMicrosoftDependencyResolver(
                ConfigureServices,
                withResolver: provider =>
                {
                    Services = provider;
                    InitializeRuntime(provider!).GetAwaiter().GetResult();
                }
            );

    /// <summary>
    /// 运行时初始化（provider 就绪后）：加载插件、ToolInjector 装配、记忆工具注册、云端诊断链路、检索同步。
    /// </summary>
    private static async Task InitializeRuntime(IServiceProvider provider)
    {
        try
        {
            var config = provider.GetRequiredService<IConfiguration>();
            var registry = provider.GetRequiredService<Core.Tools.ToolRegistry>();
            var agent = provider.GetRequiredService<Core.Agent.Agent>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

            // ══════════════════════════════════════════════════════════
            //  子步骤 1：插件加载（Desktop bin/plugins + ~/.mythology/plugins + 本地 + 环境变量）
            // ══════════════════════════════════════════════════════════
            var pluginLoader = CoreServiceExtensions.LoadPlugins(
                (ServiceProvider)provider,
                config,
                UseDebug
            );
            agent.SetTools(registry.GetAll());

            // ══════════════════════════════════════════════════════════
            //  子步骤 2：Tool Search 基础设施（镜像 HostBootstrapper §6.2）
            //  延迟加载 → ToolInjector → tool_search → 索引构建
            // ══════════════════════════════════════════════════════════
            var embeddingService =
                provider.GetService<IEmbeddingService>() ?? new Core.Vector.DummyEmbeddingService();
            var toolSearchEngine = new ToolSearchEngine(
                embeddingService,
                loggerFactory.CreateLogger<ToolSearchEngine>()
            );
            // 插件卸载后重建搜索索引
            pluginLoader.OnPluginUnloaded = _ => toolSearchEngine.InvalidateIndex();

            // 延迟加载插件名单：配置优先；未配置时 = 内置默认 + 第三方默认延迟
            var deferredPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var configuredDeferred = config[ConfigurationKeys.AgentToolSearchDeferredPlugins];
            if (!string.IsNullOrWhiteSpace(configuredDeferred))
            {
                foreach (
                    var name in configuredDeferred.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                )
                    deferredPlugins.Add(name);
            }
            else
            {
                deferredPlugins.Add("Semantic Retrieval Tool");
                deferredPlugins.Add("Web Search Tools");
                var builtinNames = new HashSet<string>(
                    [
                        "FileSystem Tools",
                        "Novel Writing Tools",
                        "Semantic Retrieval Tool",
                        "Web Search Tools",
                        "Writing Enhancement Tools",
                    ],
                    StringComparer.OrdinalIgnoreCase
                );
                foreach (var plugin in pluginLoader.LoadedPlugins)
                {
                    if (!builtinNames.Contains(plugin.Name))
                        deferredPlugins.Add(plugin.Name);
                }
            }

            // 灰度开关：dynamic（默认，按需注入）| all（全量注入）
            var dynamicMode = !string.Equals(
                config[ConfigurationKeys.AgentToolSearchMode],
                "all",
                StringComparison.OrdinalIgnoreCase
            );

            var toolInjector = new ToolInjector(
                registry,
                toolSearchEngine,
                deferredPluginNames: deferredPlugins,
                dynamicMode: dynamicMode
            );

            // tool_search 熔断阈值（默认 8）
            var searchRateLimit = int.TryParse(
                config[ConfigurationKeys.AgentToolSearchRateLimit],
                out var rl
            )
                ? rl
                : 8;
            var toolSearchTool = new ToolSearchTool(
                toolSearchEngine,
                toolInjector,
                searchRateLimit
            );
            registry.Register(toolSearchTool, "Core");
            agent.ToolInjector = toolInjector;

            // ── 外部 MCP 服务器连接 ──
            try
            {
                var mcpConfigData = provider.GetRequiredService<ConfigStore>().Load();
                var mcpConfig = mcpConfigData.Mcp;
                if (mcpConfig.Servers is { Count: > 0 })
                {
                    var mcpLogger = loggerFactory.CreateLogger<Core.Mcp.ExternalMcpBridge>();
                    var externalMcp = new Core.Mcp.ExternalMcpBridge(mcpLogger);
                    var mcpTools = await externalMcp.ConnectAsync(
                        mcpConfig.Servers,
                        CancellationToken.None
                    );
                    if (mcpTools.Count > 0)
                    {
                        registry.RegisterRange(mcpTools, "External MCP");
                    }
                }
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("Desktop.Program")
                    .LogWarning(ex, "外部 MCP 服务器连接失败");
            }

            // 在全部工具（含 tool_search 自身 + 外部 MCP 工具）注册后构建索引
            await toolSearchEngine.BuildIndexAsync(registry, CancellationToken.None);

            // ══════════════════════════════════════════════════════════
            //  子步骤 3：decompress + AgentMemory 工具（镜像 Host B2/VEC-LOCAL）
            // ══════════════════════════════════════════════════════════
            var sessionStore = provider.GetRequiredService<FileSessionStore>();
            registry.Register(new DecompressTool(agent, sessionStore), "Core");
            // compress 工具（模型驱动压缩主通道）不在 Desktop 注册：persist 委托依赖 UI 装配
            // （与 /compact 共用引擎、含防重入），由桌面 UI 入口在构造时注册

            var retrievalKernel = provider.GetService<RetrievalKernel>();
            if (retrievalKernel?.AgentMemory is { } memStore)
            {
                registry.Register(new StoreMemoryTool(agent, memStore), "Core");
                registry.Register(new RecallMemoryTool(agent, memStore), "Core");
                registry.Register(new ReinforceMemoryTool(memStore), "Core");
                registry.Register(new ForgetMemoryTool(memStore), "Core");
                registry.Register(new MarkUsefulTool(memStore), "Core");
                registry.Register(new MarkUselessTool(memStore), "Core");
                registry.Register(new FindConflictsTool(agent, memStore), "Core");
                registry.Register(new ResolveConflictTool(memStore), "Core");
                registry.Register(new SnapshotTool(memStore), "Core");
                registry.Register(new AsOfTool(agent, memStore), "Core");
                registry.Register(new DiffTool(agent, memStore), "Core");
                registry.Register(new PackTool(agent, memStore), "Core");
                registry.Register(new ConsolidateTool(memStore), "Core");

                // VEC-LOCAL §3.6 用户记忆域任务导向工具（偏好/规则）
                registry.Register(new RecallPreferenceTool(agent, retrievalKernel), "Core");
                registry.Register(new SavePreferenceTool(agent, memStore), "Core");
                registry.Register(new ListPreferencesTool(agent, memStore), "Core");
                registry.Register(new DeletePreferenceTool(memStore), "Core");

                // 候选推断：风格分析
                registry.Register(
                    new AnalyzeStyleTool(agent, provider.GetRequiredService<INovelStore>()),
                    "Core"
                );

                agent.MemoryStore = memStore;
            }

            // 注册后重建索引（包含 decompress + memory 工具）
            toolSearchEngine.InvalidateIndex();
            await toolSearchEngine.BuildIndexAsync(registry, CancellationToken.None);

            // ── 上下文接续（context-continuation 插件，PluginLoader 阶段注册；见 src/Plugins/ContextContinuation）──
            // 本处只做索引失效：插件在 LoadPlugins 之前已注册完工具，索引重建以保证延迟工具可被 tool_search 发现。
            toolSearchEngine.InvalidateIndex();
            await toolSearchEngine.BuildIndexAsync(registry, CancellationToken.None);

            // 上下文管理模式热切换（设置页「上下文」卡片）：工具重注册 + 索引重建，无需重启
            ContextModeSwitcher = new Core.Plugins.ContextModeSwitcher(
                registry,
                agent,
                provider.GetRequiredService<ConfigStore>(),
                toolSearchEngine,
                Core.Plugins.ContextModeSwitcher.Find(pluginLoader)
            );

            // ══════════════════════════════════════════════════════════
            //  子步骤 3.5：稳定性装配 + Agent 基础配置（移植 HostBootstrapper.ConfigureAgentStability）
            //  GrowthRatioGuard / VSA / RGDecimator 阈值全部由 ModelContextProfile 动态计算。
            //  会话 ID 由对话切换时设置（SessionEngine），此处不绑定。
            // ══════════════════════════════════════════════════════════
            agent.EntryIdProvider = m => m.NativeObject as string;
            var providerConfigForStability = provider.GetRequiredService<ConfigStore>().Load();
            var stabilityProfile = AgentProfile.BuildProfile(
                providerConfigForStability.Providers.DefaultModel,
                providerConfigForStability.Providers.ContextWindowSize,
                providerConfigForStability.Providers.ThinkingLevel
            );
            AgentProfile.Apply(agent, stabilityProfile);

            // 本地判定客户端（可选；与检索共用同一 DI 单例 → 设置页保存即时热更新）
            agent.SystemOne = provider.GetService<Core.Inference.ISystemOneClient>();

            var todoStoreForStability = provider.GetRequiredService<TodoStore>();
            var mythologyDirForStability = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mythology"
            );
            agent.OnEvent += async (evt, ct) =>
            {
                if (evt is Core.Agent.AgentEnd && agent.StabilityMonitor is IGrowthRatioGuard guard)
                {
                    try
                    {
                        var report = StabilityHarness.StabilityHarness.Analyze(guard);
                        if (report.IsTripped)
                        {
                            loggerFactory
                                .CreateLogger<Core.Agent.Agent>()
                                .LogWarning(
                                    "Agent 稳定性诊断 | Pattern={Pattern} Confidence={Confidence:P0} | "
                                        + "Tokens={Tokens} Steps={Steps} | Cost=${Cost:F4} | "
                                        + "Suggestions: {Suggestions}",
                                    report.Pattern,
                                    report.Confidence,
                                    report.TotalTokens,
                                    report.TotalSteps,
                                    report.CostEstimateUsd ?? 0,
                                    string.Join(
                                        ";",
                                        report.Suggestions.Select(s => $"[{s.Priority}] {s.Action}")
                                    )
                                );
                        }
                    }
                    catch (Exception stabilityEx)
                    {
                        loggerFactory
                            .CreateLogger<Core.Agent.Agent>()
                            .LogWarning(stabilityEx, "稳定性诊断失败");
                    }
                }

                // AgentInterrupted → 未完成任务回退 pending；AgentEnd / AgentInterrupted → 保存任务快照
                if (evt is Core.Agent.AgentInterrupted or Core.Agent.AgentEnd)
                {
                    if (evt is Core.Agent.AgentInterrupted)
                        todoStoreForStability.RevertInProgressToPending();
                    if (!string.IsNullOrEmpty(agent.SessionId))
                    {
                        try
                        {
                            var todosDir = Path.Combine(mythologyDirForStability, "todos");
                            Directory.CreateDirectory(todosDir);
                            todoStoreForStability.SaveTo(
                                Path.Combine(todosDir, $"{agent.SessionId}.json")
                            );
                        }
                        catch (Exception todoEx)
                        {
                            loggerFactory
                                .CreateLogger<Core.Agent.Agent>()
                                .LogWarning(
                                    todoEx,
                                    "Todo 快照保存失败 (SessionId={SessionId})",
                                    agent.SessionId
                                );
                        }
                    }
                }
                await Task.CompletedTask;
            };

            // ══════════════════════════════════════════════════════════
            //  子步骤 4：小说上下文钩子 + HARNESS-CLOUD 云端诊断链路
            // ══════════════════════════════════════════════════════════
            var store = provider.GetRequiredService<INovelStore>();
            var cloudOptions = CloudDiagnosisOptions.FromConfiguration(config);
            // 始终装配：跨会话摘要（digest）不需要云端开关；信号计算由 RunAsync 内部按开关短路。
            agent.BeforeRun = new OntologyHarness(
                agent,
                store,
                cloudOptions,
                loggerFactory.CreateLogger<OntologyHarness>()
            ).RunAsync;
            var cloudHarness = provider.GetService<Core.Cloud.IFictionHarnessClient>();
            var questionStore = provider.GetRequiredService<UserQuestionStore>();

            var vectorsDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mythology",
                "vectors.db"
            );
            var snapshotCache = cloudHarness is not null
                ? new ContextSnapshotCache(
                    cloudHarness,
                    vectorsDbPath,
                    loggerFactory.CreateLogger<ContextSnapshotCache>(),
                    cloudOptions.RequestTimeoutSeconds
                )
                : null;
            var budgetStore = new NovelBudgetStore(
                vectorsDbPath,
                loggerFactory.CreateLogger<NovelBudgetStore>()
            );
            var cloudUsage = provider.GetService<ClientPlatform.UsageRestClient>();
            var usageReconciler =
                cloudHarness is not null && cloudUsage is not null
                    ? new UsageReconciler(
                        cloudUsage,
                        cloudOptions,
                        loggerFactory.CreateLogger<UsageReconciler>()
                    )
                    : null;
            var scheduler =
                cloudHarness is not null && questionStore is not null
                    ? new DiagnosisScheduler(
                        cloudHarness,
                        cloudOptions,
                        snapshotCache,
                        loggerFactory.CreateLogger<DiagnosisScheduler>(),
                        budgetStore,
                        provider.GetService<ClientPlatform.CloudCapabilityCatalogStore>()
                    )
                    : null;
            CloudScheduler = scheduler;
            var decisionBridge = questionStore is not null
                ? new HumanDecisionBridge(
                    questionStore,
                    cloudOptions.DecisionTimeoutSeconds,
                    skipAnswerInjection: false,
                    persistenceDbPath: vectorsDbPath,
                    loggerFactory.CreateLogger<HumanDecisionBridge>()
                )
                : null;
            var monotonyWatcher = new MonotonyWatcher(cloudOptions);

            // 双速自进化账本（单账本折叠）：与 TUI 同款装配（DI 单例；默认关闭零行为变化）。
            var tacticLedger = provider.GetService<Core.Agent.TacticLedger>();

            // 记忆自动钩子：memory_recall/memory_find_conflicts 成功后自动标注有用性
            var novelHook = new NovelContextHook(agent, store);
            var writingFlowHook = new WritingFlowHook(
                agent,
                store,
                scheduler,
                decisionBridge,
                cloudOptions,
                snapshotCache,
                monotonyWatcher,
                questionStore,
                usageReconciler,
                tacticLedger
            );

            if (retrievalKernel?.AgentMemory is { } memStore2)
            {
                var memoryLogger = loggerFactory.CreateLogger<AgentMemoryHook>();
                agent.AfterToolCall = new CompositeToolHook(
                    novelHook,
                    writingFlowHook,
                    new AgentMemoryHook(memoryLogger),
                    new AssociativeTriggerHook(
                        agent,
                        store,
                        cloudHarness,
                        loggerFactory.CreateLogger<AssociativeTriggerHook>()
                    ),
                    new PlotForecastHook(
                        agent,
                        store,
                        retrievalKernel,
                        loggerFactory.CreateLogger<PlotForecastHook>()
                    )
                );
            }
            else
            {
                agent.AfterToolCall = new CompositeToolHook(
                    novelHook,
                    writingFlowHook,
                    new AssociativeTriggerHook(
                        agent,
                        store,
                        cloudHarness,
                        loggerFactory.CreateLogger<AssociativeTriggerHook>()
                    ),
                    new PlotForecastHook(
                        agent,
                        store,
                        retrievalKernel,
                        loggerFactory.CreateLogger<PlotForecastHook>()
                    )
                );
            }

            // ── 检索索引增量同步 + 后台 Worker（失败不阻断启动） ──
            _ = CoreServiceExtensions.SyncRetrievalIndexAsync(
                (ServiceProvider)provider,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            Services!
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Desktop.Program")
                .LogWarning(ex, "运行时初始化失败");
        }
    }

    /// <summary>
    /// 注册全部服务：Core 共享服务（日志/存储/检索/Agent/插件）+ Desktop 特有服务。
    /// 同步阻塞完成（迁移/检索初始化在 UI 启动前就绪，与 LKK 的启动模式一致）。
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // ── 加载配置（appsettings.json + 环境变量，与 TUI 一致） ──
        var baseConfiguration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        // 小说项目目录工作区（解析链收口在 Core，与 TUI/Host 共用）：
        // --novels > MYTHOLOGY_NOVELS_PATH > Novels:Path > 清单活动目录 > cwd/novels > bin 上溯 > ~/.mythology/novels
        var workspace = NovelWorkspace.Open(baseConfiguration, StartupNovelsPath);
        var novelsRoot = workspace.ActiveRoot;
        var mythologyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mythology"
        );
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(
                new Dictionary<string, string?> { [ConfigurationKeys.NovelsPath] = novelsRoot }
            )
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        // ── 云端账号基础设施（先于 Core 装配创建：云端 embedding 客户端在 DI 容器构建前构造，
        //    必须与容器内消费者共享同一凭据存储 / 吊销总线实例）──
        var cloudSessionStore = new ClientPlatform.CloudSessionStore();
        var cloudInvalidationBus = new ClientPlatform.CloudAuthInvalidationBus();

        // ── Core 共享服务（与 TUI 相同的初始化路径；云端 embedding 显式 opt-in：
        //    config.yaml Embedding.Provider = "cloud" 时经工厂注入，否则按本地/Dummy 选择） ──
        services
            .AddMythologyCoreServicesAsync(
                configuration,
                UseDebug,
                CancellationToken.None,
                embeddingFactory: CreateCloudEmbeddingFactory(
                    mythologyDir,
                    cloudSessionStore,
                    cloudInvalidationBus
                ),
                workspace: workspace
            )
            .GetAwaiter()
            .GetResult();

        // ── 双速自进化账本（单账本折叠架构）：与 TUI/Host 同款单例（写作钩子与
        //    get_writing_stats(tactics) 共用同一实例；默认关闭零行为变化）。──
        services.AddSingleton(_ => new Core.Agent.TacticLedger(mythologyDir));

        // ── Desktop 特有服务 ──
        services.AddSingleton<DesktopAgentBridge>();
        services.AddSingleton<DesktopThemeService>();
        services.AddSingleton<ContextTokenCalibrator>();
        // 会话引擎：每对话一个（Transient），共享单例 Agent/FileSessionStore/桥接
        services.AddTransient(sp => new SessionEngine(
            sp.GetRequiredService<DesktopAgentBridge>(),
            sp.GetRequiredService<FileSessionStore>(),
            sp.GetRequiredService<INovelStore>(),
            sp.GetRequiredService<TodoStore>(),
            sp.GetRequiredService<ContextTokenCalibrator>(),
            sp.GetRequiredService<ConfigStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SessionEngine>>(),
            sp.GetService<Core.Vector.RetrievalKernel>() // vector_retrieval 通道（可选）
        ));
        // ── 云端账号：唯一 CloudSessionStore 单例 + 吊销事件总线（§13.6 DI 单例）+ 状态门面 ──
        // 实例在 Core 装配前创建并传入：云端 embedding 客户端与容器消费者共享同一实例。
        services.AddSingleton(cloudSessionStore);
        services.AddSingleton(cloudInvalidationBus);
        services.AddSingleton<CloudAuthService>();
        // 能力目录缓存（探活成功写入 last-known-good 快照；诊断调度按服务端声明组装请求）。
        // CloudAuthService / CloudConnectionService 由容器按类型构造 → 自动注入本单例。
        services.AddSingleton<ClientPlatform.CloudCapabilityCatalogStore>();

        // ── 微信通道（DI 单例）：凭证与状态在 <mythologyDir>/wechat/，ownerLabel 用于诊断与锁归属 ──
        services.AddSingleton(sp => new Channels.WeChat.WeChatChannelService(
            Path.Combine(mythologyDir, "wechat"),
            "desktop",
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
        ));

        // ── 云端客户端（§13.6）：与云端账号共用同一总线与端点，真实调用 401/403 → 立即吊销 ──
        RegisterCloudClients(services, mythologyDir, workspace);

        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<ConversationViewModel>();
        services.AddTransient<SetUpPanelViewModel>();
        services.AddTransient(sp => new SettingsViewModel(
            sp.GetRequiredService<ConfigStore>(),
            sp.GetRequiredService<Core.Agent.Agent>(),
            sp.GetRequiredService<ToolRegistry>(),
            sp.GetService<Core.Vector.RetrievalKernel>(),
            sp.GetRequiredService<CloudAuthService>(),
            sp.GetRequiredService<Channels.WeChat.WeChatChannelService>(),
            sp.GetService<UpdateState>()
        ));

        // ── 自动更新（GitHub Releases 检测 + 静默下载 + 弹窗/设置页「关于」入口）──
        // 公开仓库匿名直连 GitHub REST（无需令牌）；不可达/私有/无 release 一律按「无更新」静默处理。
        services.AddHttpClient(
            "GitHub",
            client =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                client.Timeout = TimeSpan.FromMinutes(10); // 安装包体积大，留足下载时间
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mythology-Desktop"); // GitHub API 要求 UA
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            }
        );
        services.AddSingleton<IUpdateCheckService>(sp => new UpdateCheckService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHub"),
            sp.GetRequiredService<ILogger<UpdateCheckService>>(),
            Path.Combine(mythologyDir, "updates")
        ));
        services.AddSingleton<UpdateState>();
        services.AddSingleton<UpdateCheckScheduler>();
    }

    /// <summary>
    /// 云端 embedding 工厂（显式 opt-in）：config.yaml Embedding.Provider = "cloud" 时返回
    /// <see cref="CloudEmbeddingClient"/> 工厂（与容器共享同一凭据存储/吊销总线实例），
    /// 否则返回 null（Core 按本地 LlmTornado / Dummy 选择）。
    /// </summary>
    private static Func<IServiceProvider, IEmbeddingService>? CreateCloudEmbeddingFactory(
        string mythologyDir,
        ClientPlatform.CloudSessionStore cloudSessionStore,
        ClientPlatform.CloudAuthInvalidationBus cloudInvalidationBus
    )
    {
        string? provider = null;
        try
        {
            provider = new ConfigStore(mythologyDir).Load()?.Embedding.Provider;
        }
        catch
        {
            // config.yaml 缺失/损坏：按未配置处理（与 Core 侧回退语义一致）
        }

        if (!string.Equals(provider, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _ => new CloudEmbeddingClient(
            CloudEndpoints.ResolveCloudBaseUrl(),
            () => cloudSessionStore.Load()?.ApiKey,
            invalidationBus: cloudInvalidationBus
        );
    }

    /// <summary>
    /// 注册云端凭据消费者客户端（检索 REST / Harness），与 <see cref="CloudAuthService"/> 共用同一
    /// 吊销总线单例与云端端点：真实调用 401/403 → 立即触发吊销（§13.4/§13.6），不等 5 分钟心跳。
    /// 端点/Key 与 CloudAuthService 对齐（同一 baseUrl + 内存会话 Key，登录/登出即时生效），
    /// 注入的吊销总线与 CloudAuthService 订阅的是同一单例实例。
    /// 独立方法便于单元测试（Desktop.Tests 直接调用，验证总线单例一致性）。
    /// 依赖已注册的 <see cref="CloudAuthService"/> 与 <see cref="ClientPlatform.CloudAuthInvalidationBus"/>。
    /// </summary>
    public static void RegisterCloudClients(
        IServiceCollection services,
        string mythologyDir,
        NovelWorkspace workspace
    )
    {
        // C4 收口：IHttpClientFactory 统一管理云端 HTTP 连接（handler 池化 + DNS 轮换）。
        services.AddHttpClient();

        // 数据面命名客户端：官方 Microsoft.Extensions.Http.Resilience 重试流水线（Host 同构）。
        services
            .AddHttpClient("MythologyCloud")
            .AddResilienceHandler(
                "mythology-cloud",
                static b => ClientPlatform.CloudHttpResilience.ConfigureRetry(b)
            );

        // 登录/探活命名客户端：OAuth/设备码一次性凭证交换非幂等，不重试（Host 同构）。
        services.AddHttpClient("MythologyAuth");

        // ── 云端小说同步（cloud-novel-sync v3）：与 TUI 同构的客户端 + 引擎注册 ──
        services.AddSingleton<ClientPlatform.ICloudNovelSyncClient>(
            sp => new ClientPlatform.CloudNovelSyncClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                () => sp.GetRequiredService<CloudAuthService>().Session?.ApiKey,
                ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl(),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.CloudNovelSyncClient>>()
            )
        );
        services.AddSingleton(sp => new ClientPlatform.CloudNovelSyncService(
            sp.GetRequiredService<ClientPlatform.ICloudNovelSyncClient>(),
            new ClientPlatform.CloudSyncStateStore(mythologyDir),
            workspace,
            mythologyDir,
            logger: sp.GetService<ILogger<ClientPlatform.CloudNovelSyncService>>()
        ));

        services.AddSingleton<ClientPlatform.RetrievalRestClient>(sp =>
        {
            var baseUrl = ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl();
            return new ClientPlatform.RetrievalRestClient(
                baseUrl,
                () => sp.GetRequiredService<CloudAuthService>().Session?.ApiKey,
                http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.RetrievalRestClient>>()
            );
        });
        services.AddSingleton<Core.Cloud.IFictionHarnessClient>(sp =>
        {
            var baseUrl = ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl();
            return new ClientPlatform.HttpFictionHarnessClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                () => sp.GetRequiredService<CloudAuthService>().Session?.ApiKey,
                baseUrl,
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>()
            );
        });

        // ── 云端素材知识库（KnowledgeRestClient：knowledge.search/browse/categories/document）──
        services.AddSingleton<ClientPlatform.KnowledgeRestClient>(sp =>
        {
            var baseUrl = ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl();
            return new ClientPlatform.KnowledgeRestClient(
                baseUrl,
                () => sp.GetRequiredService<CloudAuthService>().Session?.ApiKey,
                http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.KnowledgeRestClient>>()
            );
        });

        // ── 云端爬取任务（CrawlRestClient：提交/快照/取消，Idempotency-Key 必填）──
        services.AddSingleton<ClientPlatform.CrawlRestClient>(sp =>
        {
            var baseUrl = ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl();
            return new ClientPlatform.CrawlRestClient(
                baseUrl,
                () => sp.GetRequiredService<CloudAuthService>().Session?.ApiKey,
                http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.CrawlRestClient>>()
            );
        });

        // ── 云端用量（UsageRestClient：:cloud status 积分余量 + 账本，零价查询）──
        services.AddSingleton<ClientPlatform.UsageRestClient>(sp =>
        {
            var baseUrl = ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl();
            return new ClientPlatform.UsageRestClient(
                baseUrl,
                () => sp.GetRequiredService<CloudAuthService>().Session?.ApiKey,
                http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.UsageRestClient>>()
            );
        });
    }
}
