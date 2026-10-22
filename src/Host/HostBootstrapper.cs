using System.Collections.Concurrent;
using CloudDiagnosis;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Data.Migrations;
using Core.Forecast;
using Core.Infrastructure;
using Core.Plugins;
using Core.Shared;
using Core.Stability;
using Core.Tools;
using Core.Vector;
using LlmTornado;
using LlmTornado.Code;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Host;

/// <summary>
/// 应用程序引导器：配置 DI 容器、数据库、向量存储、插件加载和 Agent 配置。
/// 从 Program.cs 中分离，保持入口点简洁。
/// </summary>
public static class HostBootstrapper
{
    /// <summary>
    /// 初始化应用程序的所有服务。
    /// </summary>
    /// <param name="config">配置对象</param>
    /// <param name="novelId">小说 ID（可选）</param>
    /// <param name="sessionId">会话 ID（可选）</param>
    /// <param name="useDebug">是否使用调试模式</param>
    /// <param name="useMock">是否使用 Mock 模式</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>Agent、工具注册表、小说存储、会话存储、问题存储、配置存储、插件加载器、检索内核、云端吊销事件总线、TUI 云端连接门面、云端诊断调度器（宿主退出结算用）、harness 客户端、小说同步服务、用量客户端的元组</returns>
    public static async Task<(
        Agent Agent,
        ToolRegistry Registry,
        FileNovelStore Store,
        FileSessionStore SessionStore,
        UserQuestionStore QuestionStore,
        TodoStore TodoStore,
        ConfigStore ConfigStore,
        PluginLoader PluginLoader,
        ContextModeSwitcher ContextModeSwitcher,
        RetrievalKernel? RetrievalKernel,
        ClientPlatform.CloudAuthInvalidationBus CloudAuthBus,
        Tui.Services.CloudConnectionService CloudConnection,
        ILogger<Tui.TuiApp> TuiLogger,
        CloudDiagnosis.DiagnosisScheduler? CloudScheduler,
        Core.Cloud.IFictionHarnessClient? CloudHarness,
        ClientPlatform.CloudNovelSyncService? NovelSync,
        ClientPlatform.UsageRestClient? CloudUsage,
        Channels.WeChat.WeChatChannelService WeChat,
        ILoggerFactory LoggerFactory
    )> InitializeAsync(
        IConfiguration config,
        string? novelId,
        string? sessionId,
        bool useDebug,
        bool useMock,
        CancellationToken ct,
        string? explicitNovelsPath = null
    )
    {
        // ── 日志配置 ──
        var (serilogLogger, logsDir) = ConfigureLogging();

        // ── 存储路径：项目目录工作区（与 Desktop 共用 Core 解析链 + workspaces.json 清单）──
        var workspace = NovelWorkspace.Open(config, explicitNovelsPath);
        var novelsRoot = workspace.ActiveRoot;
        var mythologyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mythology"
        );

        // ── 数据迁移（优先于所有服务构建） ──
        var migrationLogger = new LoggerFactory()
            .AddSerilog(serilogLogger)
            .CreateLogger<MigrationRunner>();
        await new MigrationRunner(mythologyDir, novelsRoot, migrationLogger).RunAsync(ct);

        // ── DI 容器注册 ──
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddLogging(builder => builder.AddSerilog(serilogLogger, dispose: true));
        // 云端基础设施实例先于注册创建：云端 embedding 客户端在容器构建前构造，
        // 必须与容器内消费者共享同一吊销总线 / 凭据存储实例。
        var cloudInvalidationBus = new ClientPlatform.CloudAuthInvalidationBus();
        var cloudSessionStore = new ClientPlatform.CloudSessionStore(mythologyDir);
        RegisterStores(services, workspace, mythologyDir, cloudSessionStore, cloudInvalidationBus);

        // ── LLM 提供商配置（appsettings → 环境变量，启动期初始 API） ──
        var (tornadoApi, llmModel) = ConfigureLlm(config, useDebug);

        // ── config.yaml 覆盖：用户在 TUI 设置中保存的 Provider/Embedding 优先 ──
        var bootConfigStore = new ConfigStore(mythologyDir);
        ConfigData? savedConfig = null;
        try
        {
            savedConfig = bootConfigStore.Load();
        }
        catch
        {
            // config.yaml 不存在或损坏时回退到 appsettings 的 tornadoApi
        }
        if (
            savedConfig is { } cfg
            && cfg.Providers.ApiKeys.Exists(k => !string.IsNullOrEmpty(k.ApiKey))
        )
        {
            var (savedApi, savedModel, _) = LlmApiFactory.CreateFromProviderConfig(cfg.Providers);
            tornadoApi = savedApi;
            llmModel = savedModel;
        }
        TornadoApi? embeddingApi = null;
        string? savedEmbeddingModel = null;
        if (savedConfig?.Embedding is { ApiKeys.Count: > 0 } emb)
        {
            var (embApi, embModel, _) = LlmApiFactory.CreateFromEmbeddingConfig(emb);
            embeddingApi = embApi;
            savedEmbeddingModel = embModel;
        }
        else if (!string.IsNullOrWhiteSpace(savedConfig?.Embedding?.Model))
        {
            savedEmbeddingModel = savedConfig.Embedding.Model;
        }
        // 注意：Model 为空 = 未配置 embedding（纯 BM25/FTS，不发起任何远程调用）。
        // 不能把 DefaultModel（占位默认 text-embedding-3-small）当作已配置——否则未配置
        // 用户也会对聊天连接器发 embedding 请求，远端无此模型时每次启动拖爆 15s 同步预算。

        // ── 云端 embedding（显式 opt-in：config.yaml Embedding.Provider = "cloud"）──
        IEmbeddingService? cloudEmbedding = null;
        if (
            string.Equals(
                savedConfig?.Embedding.Provider,
                "cloud",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            cloudEmbedding = new ClientPlatform.CloudEmbeddingClient(
                ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl(),
                () => cloudSessionStore.Load()?.ApiKey,
                invalidationBus: cloudInvalidationBus,
                logger: new LoggerFactory()
                    .AddSerilog(serilogLogger)
                    .CreateLogger<ClientPlatform.CloudEmbeddingClient>()
            );
            if (useDebug)
                Console.WriteLine("[Debug] Embedding: 云端 /v1/embedding（provider=cloud）");
        }

        // ── 本地判定模型（System One / d1-omni；可选，默认关闭，不启用时零请求）──
        var systemOneClient = new Core.Inference.SystemOneClient(
            savedConfig?.SystemOne ?? new SystemOneConfig()
        );
        services.AddSingleton<Core.Inference.ISystemOneClient>(systemOneClient);
        if (useDebug && systemOneClient.Enabled)
            Console.WriteLine($"[Debug] System One: {systemOneClient.Options.BaseUrl}");

        // ── Embedding / Vector / 检索内核 ──
        var (embeddingService, retrievalStore, retrievalKernel) = await ConfigureRetrievalAsync(
            config,
            services,
            mythologyDir,
            useDebug,
            tornadoApi,
            embeddingApi,
            savedEmbeddingModel,
            cloudEmbedding,
            systemOneClient,
            ct
        );

        // ── Agent 注册到 DI ──
        services.AddSingleton<Agent>(sp =>
        {
            var registry = sp.GetRequiredService<ToolRegistry>();
            return new Agent(registry, tornadoApi);
        });

        // ── 单次 BuildServiceProvider，避免单例撕裂 ──
        var provider = services.BuildServiceProvider();

        // ── 核心服务解析 ──
        var store = provider.GetRequiredService<FileNovelStore>();
        var semantic =
            provider.GetService<NovelSemanticService>() ?? new NovelSemanticService(workspace);
        var questionStore = provider.GetRequiredService<UserQuestionStore>();
        var agent = provider.GetRequiredService<Agent>();
        var registry = provider.GetRequiredService<ToolRegistry>();
        var todoStore = provider.GetRequiredService<TodoStore>();

        // ── 插件加载 ──
        var pluginLoader = LoadPlugins(registry, provider, config, useDebug);
        agent.SetTools(registry.GetAll());

        // ── Tool Search 基础设施 ──
        // 在插件加载后构建索引（需要所有工具已注册）
        var toolSearchEngine = new ToolSearchEngine(
            embeddingService,
            provider.GetService<ILogger<ToolSearchEngine>>()
        );
        pluginLoader.OnPluginUnloaded = _ => toolSearchEngine.InvalidateIndex();

        // 延迟加载插件名单：默认延迟辅助插件（检索/网络搜索），核心写作与分析工具常驻注入。
        // 仅通过 tool_search 按需发现，削减上下文膨胀。可用 Agent:ToolSearchDeferredPlugins 配置覆盖。
        // 名单匹配 PluginManifest.Name（registry 按 manifest.Name 分组）。
        // 插件体系动态：未配置时 = 内置默认（Semantic Retrieval Tool, Web Search Tools）
        // + 所有非内置插件（第三方）默认延迟——插件不固定，未知插件一律不预注入，经 tool_search 发现。
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

        // 灰度开关：Agent:ToolSearchMode = dynamic（默认，按需注入）| all（全量注入，仅 DeferLoading 延迟）
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

        // tool_search 熔断阈值可配置（Agent:ToolSearchRateLimit，默认 8）
        var searchRateLimit = int.TryParse(
            config[ConfigurationKeys.AgentToolSearchRateLimit],
            out var rl
        )
            ? rl
            : 8;
        var toolSearchTool = new ToolSearchTool(toolSearchEngine, toolInjector, searchRateLimit);
        registry.Register(toolSearchTool, "Core");
        agent.ToolInjector = toolInjector;

        // ── 外部 MCP 服务器连接 ──
        // 连接启用的 MCP 服务器，注册工具到 registry（插件名 "External MCP"）。
        // 第三方插件默认延迟策略自动使 MCP 工具仅经 tool_search 发现。
        try
        {
            var mcpConfigData = provider.GetRequiredService<ConfigStore>().Load();
            var mcpConfig = mcpConfigData.Mcp;
            if (mcpConfig.Servers is { Count: > 0 })
            {
                var mcpLogger = provider.GetService<ILogger<Core.Mcp.ExternalMcpBridge>>();
                var externalMcp = new Core.Mcp.ExternalMcpBridge(mcpLogger);
                var mcpTools = await externalMcp.ConnectAsync(mcpConfig.Servers, ct);
                if (mcpTools.Count > 0)
                {
                    registry.RegisterRange(mcpTools, "External MCP");
                }
            }
        }
        catch (Exception ex)
        {
            serilogLogger.Warning(ex, "外部 MCP 服务器连接失败");
        }

        // 在全部工具（含 tool_search 自身 + 外部 MCP 工具）注册后构建索引——索引包含完整工具集
        await toolSearchEngine.BuildIndexAsync(registry, ct);

        // ── 上下文管理模式热切换（设置页「上下文」标签页；compact ↔ continuation）──
        var contextModeSwitcher = new ContextModeSwitcher(
            registry,
            agent,
            provider.GetRequiredService<ConfigStore>(),
            toolSearchEngine,
            ContextModeSwitcher.Find(pluginLoader)
        );
        if (useDebug)
            Console.WriteLine(
                $"[Debug] Tool Search: 索引 {registry.Count} 个工具, 每轮注入 {toolInjector.BuildRequestTools().Count} 个（模式: {(dynamicMode ? "dynamic" : "all")}, 延迟插件: {string.Join(",", deferredPlugins)}）"
            );

        // ── 稳定性监控 + Agent 配置 ──
        ConfigureAgentStability(agent, provider, mythologyDir, sessionId);
        var cloudHarness = provider.GetService<Core.Cloud.IFictionHarnessClient>(); // CHANGE_10 §4.3
        var novelSync = provider.GetService<ClientPlatform.CloudNovelSyncService>(); // cloud-novel-sync v3
        var cloudUsage = provider.GetService<ClientPlatform.UsageRestClient>(); // :cloud status 积分余量
        var weChat = provider.GetRequiredService<Channels.WeChat.WeChatChannelService>(); // 微信通道（单例）
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>(); // TuiApp 构造微信桥接需要
        var cloudScheduler = ConfigureAgentHooks(
            agent,
            store,
            retrievalKernel,
            provider,
            semantic,
            config,
            questionStore,
            Path.Combine(mythologyDir, "vectors.db")
        );
        ConfigureAgentAuditLoop(agent, provider, semantic);

        // ── 基本参数 ──
        agent.SetSystemPrompt(config[ConfigurationKeys.AgentSystemPrompt] ?? "");
        agent.SetModelName(llmModel);
        agent.SetThinkingLevel(config[ConfigurationKeys.AgentThinkingLevel] ?? "medium");

        // ── 检索索引增量同步 ──
        await SyncRetrievalIndexAsync(provider, store, ct);

        var sessionStore = provider.GetRequiredService<FileSessionStore>();
        var configStore = provider.GetRequiredService<ConfigStore>();
        var cloudAuthBus = provider.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>();
        var cloudConnection = provider.GetRequiredService<Tui.Services.CloudConnectionService>();
        var tuiLogger = provider.GetRequiredService<ILogger<Tui.TuiApp>>();

        // ── B2: decompress 工具（恢复被压缩消息原文）──
        registry.Register(new DecompressTool(agent, sessionStore), "Core");

        // ── 上下文接续（context-continuation 插件）：换页 + 历史检索 + 跨页笔记 ──
        // 工具由插件（src/Plugins/ContextContinuation）在 PluginLoader 阶段注册；
        // mode = compact 时插件不注册任何工具，注册表与引入接续前完全一致。

        // ── COMPACT 主通道：compress 工具（模型驱动压缩）──
        // 不在 Host 注册：persist 委托依赖 TuiApp 的 PersistCompactModelAsync（与 /compact
        // 共用引擎、含 _isCompacting 防重入），由 TuiApp 构造函数注册（见 TuiApp.cs）。

        // ── AgentMemory 工具（14 个记忆操作工具，全部 DeferLoading）──
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

            // ── VEC-LOCAL §3.6 用户记忆域任务导向工具（偏好/规则）──
            registry.Register(new RecallPreferenceTool(agent, retrievalKernel), "Core");
            registry.Register(new SavePreferenceTool(agent, memStore), "Core");
            registry.Register(new ListPreferencesTool(agent, memStore), "Core");
            registry.Register(new DeletePreferenceTool(memStore), "Core");

            // ── VEC-LOCAL 域三（候选推断）：章节 → 候选 → 用户确认 → memory_store 保存──
            registry.Register(
                new AnalyzeStyleTool(agent, provider.GetRequiredService<INovelStore>()),
                "Core"
            );
        }

        // ── B3: ref 稳定——从 LlmChatMessage.NativeObject 提取 EntryId（TuiApp 透传）──
        // Core 不依赖 NativeObject 字段，只依赖 Func 委托；TuiApp 负责写入（MapDbMessageToChatMessage）。
        agent.EntryIdProvider = msg => msg.NativeObject as string;

        return (
            agent,
            registry,
            store,
            sessionStore,
            questionStore,
            todoStore,
            configStore,
            pluginLoader,
            contextModeSwitcher,
            retrievalKernel,
            cloudAuthBus,
            cloudConnection,
            tuiLogger,
            cloudScheduler,
            cloudHarness,
            novelSync,
            cloudUsage,
            weChat,
            loggerFactory
        );
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：日志
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 配置 Serilog 日志：仅滚动文件（Information+，保留 14 天），走 <see cref="RedactingTextFormatter"/>
    /// 避免 SDK 异常里的 Authorization / api-key 请求头把用户 API Key 写进日志文件。
    /// 不配置控制台 sink：TUI 运行在 alternate screen 上，任何运行期 Warning 裸写 stdout
    /// 都会直接叠画在界面上（糊屏、无法阅读也无法复制）；诊断一律查看日志文件。
    /// </summary>
    /// <returns>Serilog logger 实例和日志目录路径。</returns>
    private static (Serilog.ILogger Logger, string LogsDir) ConfigureLogging()
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mythology",
            "logs"
        );
        Directory.CreateDirectory(logsDir);

        const string fileTemplate =
            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                new RedactingTextFormatter(fileTemplate),
                Path.Combine(logsDir, "mythology-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14
            )
            .CreateLogger();

        return (logger, logsDir);
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：DI 存储
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 注册所有数据存储到 DI 容器：ConfigStore、FileNovelStore、FileSessionStore、PluginStore、ToolRegistry 等。
    /// </summary>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="workspace">小说项目目录工作区（读取 = 清单并集，写入 = ActiveRoot）。</param>
    /// <param name="mythologyDir">~/.mythology 目录。</param>
    /// <param name="cloudSessionStore">云端凭据存储实例（由 InitializeAsync 创建，与容器外消费者共享）。</param>
    /// <param name="cloudInvalidationBus">云端吊销事件总线实例（同上，云端 embedding 客户端经它送达 401/403）。</param>
    private static void RegisterStores(
        ServiceCollection services,
        NovelWorkspace workspace,
        string mythologyDir,
        ClientPlatform.CloudSessionStore cloudSessionStore,
        ClientPlatform.CloudAuthInvalidationBus cloudInvalidationBus
    )
    {
        services.AddSingleton(workspace);
        services.AddSingleton<ConfigStore>(_ => new ConfigStore(mythologyDir));
        services.AddSingleton<FileNovelStore>(_ => new FileNovelStore(workspace, mythologyDir));
        services.AddSingleton<INovelStore>(sp => sp.GetRequiredService<FileNovelStore>());
        services.AddSingleton<FileSessionStore>(_ => new FileSessionStore(workspace, mythologyDir));
        services.AddSingleton<PluginStore>(sp => new PluginStore(
            sp.GetRequiredService<INovelStore>(),
            sp.GetService<ILogger<PluginStore>>()
        ));
        services.AddSingleton<IPluginStore>(sp => sp.GetRequiredService<PluginStore>());
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<TodoStore>();
        services.AddSingleton<UserQuestionStore>();

        // ── 双速自进化账本（单账本折叠架构）：append-only ledger.jsonl 的唯一运行时单例，
        //    写作钩子（结算/注入）与 get_writing_stats(tactics) 共用同一实例。──
        services.AddSingleton(_ => new TacticLedger(mythologyDir));

        // ── C4 收口：IHttpClientFactory 统一管理云端 HTTP 连接（handler 池化 + DNS 轮换）──
        services.AddHttpClient();

        // ── 数据面命名客户端：官方 Microsoft.Extensions.Http.Resilience 重试流水线 ──
        // 只重试 429/502/503/504 + HttpRequestException（500 直接上抛），优先 Retry-After（≤30s），
        // 否则 500ms 指数退避（封顶 4s）+ 20% jitter；1 原始 + 2 重试。不设总/尝试超时，避免掐断
        // SSE 长连接（HttpFictionHarnessClient / DeviceAuthorizationClient 流式路径共享本客户端）。
        services
            .AddHttpClient("MythologyCloud")
            .AddResilienceHandler(
                "mythology-cloud",
                static b => ClientPlatform.CloudHttpResilience.ConfigureRetry(b)
            );

        // ── 登录/探活命名客户端：OAuth/设备码一次性凭证交换非幂等，不重试（文章：只重试幂等操作）──
        services.AddHttpClient("MythologyAuth");

        // ── §13.6 云端吊销事件总线（DI 单例）：云端客户端 Raise，TUI 认证门面订阅 ──
        // 实例由 InitializeAsync 创建后传入：云端 embedding 客户端在 DI 容器构建前构造，
        // 与容器内消费者共享同一实例，401/403 才能送达认证门面。
        services.AddSingleton(cloudInvalidationBus);

        // ── 云端凭据存储 + TUI 连接门面（DI 单例，与客户端共享同一容器）──
        // mythologyDir 与 ConfigStore/FileSessionStore 等一致（~/.mythology）；store 负责
        // Windows DPAPI 加密/解密 cloud-session.json，客户端一律经 Load() 取 key，禁止明文解析。
        services.AddSingleton(cloudSessionStore);
        services.AddSingleton<Tui.Services.CloudConnectionService>();
        // 能力目录缓存（探活成功写入 last-known-good 快照；诊断调度按服务端声明组装请求）。
        // CloudConnectionService 由容器按类型构造 → 自动注入本单例。
        services.AddSingleton<ClientPlatform.CloudCapabilityCatalogStore>();

        // ── 微信通道（DI 单例）：凭证与状态在 <mythologyDir>/wechat/，ownerLabel 用于诊断与锁归属。
        // 不注册 IHostedService：TUI 没有 IHost 概念，通道由 TuiApp 构造时接管（见 TuiApp 构造函数）。
        services.AddSingleton(sp => new Channels.WeChat.WeChatChannelService(
            Path.Combine(mythologyDir, "wechat"),
            "tui",
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>()
        ));

        // ── 云端检索（Retrieval REST 客户端）──
        // 端点：共享收口 CloudEndpoints.ResolveCloudBaseUrl()（MYTHOLOGY_CLOUD_URL，默认生产地址 https://api.mythology.work，
        // 与 Desktop 统一），与 TUI :cloud 命令一致。
        // API Key：每次调用从 DI 单例 CloudSessionStore 动态读取（登录/登出后自动刷新），
        // 经 Load() 解密（Windows 上文件为 DPAPI 密文），禁止直接解析明文。
        services.AddSingleton<ClientPlatform.RetrievalRestClient>(sp =>
        {
            var store = sp.GetRequiredService<ClientPlatform.CloudSessionStore>();
            var baseUrl = ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl();
            return new ClientPlatform.RetrievalRestClient(
                baseUrl,
                () => store.Load()?.ApiKey,
                http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.RetrievalRestClient>>()
            );
        });

        // ── 云端素材知识库（KnowledgeRestClient：knowledge.search/browse/categories/document）──
        // 与检索 REST 同款：动态 Key（DI 单例 CloudSessionStore）+ 共享吊销总线 + 命名客户端。
        services.AddSingleton<ClientPlatform.KnowledgeRestClient>(sp =>
        {
            var store = sp.GetRequiredService<ClientPlatform.CloudSessionStore>();
            return new ClientPlatform.KnowledgeRestClient(
                ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl(),
                () => store.Load()?.ApiKey,
                http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.KnowledgeRestClient>>()
            );
        });

        // ── 云端爬取任务（CrawlRestClient：提交/快照/取消，Idempotency-Key 必填）──
        services.AddSingleton<ClientPlatform.CrawlRestClient>(sp =>
        {
            var store = sp.GetRequiredService<ClientPlatform.CloudSessionStore>();
            return new ClientPlatform.CrawlRestClient(
                ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl(),
                () => store.Load()?.ApiKey,
                http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.CrawlRestClient>>()
            );
        });

        // ── 云端用量（UsageRestClient：:cloud status 积分余量 + 账本，零价查询）──
        // 与检索/知识库/爬取同款：动态 Key（DI 单例 CloudSessionStore）+ 共享吊销总线 + 命名客户端。
        services.AddSingleton<ClientPlatform.UsageRestClient>(sp =>
        {
            var store = sp.GetRequiredService<ClientPlatform.CloudSessionStore>();
            return new ClientPlatform.UsageRestClient(
                ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl(),
                () => store.Load()?.ApiKey,
                http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.UsageRestClient>>()
            );
        });

        // ── 云端小说同步（cloud-novel-sync v3：:cloud sync / :cloud sync all / :cloud pull）──
        // 端点：共享收口 CloudEndpoints.ResolveCloudBaseUrl() + /v1/sync（与 :cloud 命令一致）。
        // API Key 每次调用从 DI 单例 CloudSessionStore 动态读取；novel-sync 状态文件在 mythologyDir 下。
        services.AddSingleton<ClientPlatform.ICloudNovelSyncClient>(
            sp => new ClientPlatform.CloudNovelSyncClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                () => sp.GetRequiredService<ClientPlatform.CloudSessionStore>().Load()?.ApiKey,
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

        // ── 云端分析 Harness（IFictionHarnessClient）──
        // 端点：共享收口 CloudEndpoints.ResolveCloudBaseUrl() + /v1/agent/runs（与 :cloud 命令一致）。
        // API Key 每次调用从 DI 单例 CloudSessionStore 动态读取（登录/登出后自动刷新）。
        services.AddSingleton<IFictionHarnessClient>(sp =>
        {
            var store = sp.GetRequiredService<ClientPlatform.CloudSessionStore>();
            var baseUrl = ClientPlatform.CloudEndpoints.ResolveCloudBaseUrl();
            return new ClientPlatform.HttpFictionHarnessClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("MythologyCloud"),
                () => store.Load()?.ApiKey,
                baseUrl,
                invalidationBus: sp.GetRequiredService<ClientPlatform.CloudAuthInvalidationBus>(),
                logger: sp.GetService<ILogger<ClientPlatform.HttpFictionHarnessClient>>()
            );
        });
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：LLM 提供商
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 配置 LLM 提供商：从配置文件和环境变量读取 API Key，创建 TornadoApi 实例。
    /// </summary>
    /// <param name="config">配置对象。</param>
    /// <param name="useDebug">是否输出调试日志。</param>
    /// <returns>TornadoApi 实例和默认模型名称。</returns>
    private static (TornadoApi? Api, string Model) ConfigureLlm(
        IConfiguration config,
        bool useDebug
    )
    {
        var llmProvider = config[ConfigurationKeys.LlmProvider]?.ToLowerInvariant();
        var llmModel = config[ConfigurationKeys.LlmModel] ?? "gpt-4o-mini";
        var apiKeyEnv = ResolveKey(config[ConfigurationKeys.LlmApiKey]) ?? "";

        // 遍历所有支持 LLM 的提供商定义，读取其 API Key
        var providerKeys = new Dictionary<string, string?>();
        foreach (var def in ProviderDefinition.All.Where(d => d.LlmProvider is not null))
        {
            var pascalKey = char.ToUpperInvariant(def.ConfigKey[0]) + def.ConfigKey[1..];
            var configKey = $"LLM:Providers:{pascalKey}:ApiKey";
            providerKeys[def.ConfigKey] = ResolveKey(config[configKey]);
        }

        // 旧格式兼容：LLM:ApiKey 作为全局 key，当该提供商无独立 key 时回退使用
        if (!string.IsNullOrEmpty(apiKeyEnv) && llmProvider is not null and not "mock")
        {
            if (string.IsNullOrEmpty(providerKeys.GetValueOrDefault(llmProvider)))
                providerKeys[llmProvider] = apiKeyEnv;
        }

        var authentications = new List<ProviderAuthentication>();
        foreach (var (name, key) in providerKeys)
        {
            if (string.IsNullOrEmpty(key))
                continue;
            var def = ProviderDefinition.FindByConfigKey(name);
            if (def.LlmProvider is not null)
                authentications.Add(new ProviderAuthentication(def.LlmProvider.Value, key));
        }

        TornadoApi? tornadoApi = null;

        if (authentications.Count > 0)
        {
            // 多提供商模式：按模型名自动路由
            tornadoApi = LlmProvider.CreateApi(authentications);
            LlmProvider.DebugMode = useDebug;
            if (useDebug)
            {
                var providerNames = string.Join(", ", authentications.Select(a => a.Provider));
                Console.WriteLine($"[Debug] LLM: {providerNames}/{llmModel}");
            }
        }
        else if (!string.IsNullOrEmpty(apiKeyEnv) && llmProvider is not null and not "mock")
        {
            // 单提供商回退：仅配置了全局 LLM:ApiKey
            var def = ProviderDefinition.FindByConfigKey(llmProvider);
            var providerEnum = def.LlmProvider ?? LLmProviders.OpenAi;
            tornadoApi = LlmProvider.CreateApi(providerEnum, apiKeyEnv);
            LlmProvider.DebugMode = useDebug;
            if (useDebug)
                Console.WriteLine($"[Debug] LLM: {llmProvider}/{llmModel}");
        }
        else if (llmProvider != "mock")
        {
            // 无 API Key 警告（mock 模式除外）
            Console.WriteLine("[Warning] 未配置 LLM API Key，Agent 将无法调用 LLM");
        }

        return (tornadoApi, llmModel);
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：Embedding / 检索内核
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 配置 Embedding 服务、向量存储、检索内核和用户记忆存储。
    /// 初始化失败时回退到 DummyEmbeddingService / InMemoryRetrievalStore。
    /// </summary>
    /// <param name="config">配置对象。</param>
    /// <param name="services">DI 服务集合（注册单例）。</param>
    /// <param name="mythologyDir">~/.mythology 目录。</param>
    /// <param name="useDebug">是否输出调试日志。</param>
    /// <param name="tornadoApi">TornadoApi 实例（已被 config.yaml 覆盖过 provider）。</param>
    /// <param name="savedEmbeddingModel">从 config.yaml 读取的 embedding 模型名（可为 null）。</param>
    /// <param name="cloudEmbedding">云端 embedding 客户端（Embedding.Provider="cloud" 时非 null，优先级最高）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>Embedding 服务、检索存储和检索内核。</returns>
    private static async Task<(
        IEmbeddingService EmbeddingService,
        IRetrievalStore RetrievalStore,
        RetrievalKernel? RetrievalKernel
    )> ConfigureRetrievalAsync(
        IConfiguration config,
        ServiceCollection services,
        string mythologyDir,
        bool useDebug,
        TornadoApi? tornadoApi,
        TornadoApi? embeddingApi,
        string? savedEmbeddingModel,
        IEmbeddingService? cloudEmbedding,
        Core.Inference.ISystemOneClient? systemOne,
        CancellationToken ct
    )
    {
        var vectorDbPath = Path.Combine(mythologyDir, "vectors.db");
        // 向量维度固定为 VectorDefaults.Dimension（1024，与主流 embedding 模型对齐）——
        // 不启动探测：那是启动链路上的远程调用，云端不可达时会把入口拖到提供商超时。
        // 模型维度不一致会在真实索引时显式失败并记录日志。
        const int effectiveDimension = VectorDefaults.Dimension;

        // ── Embedding 服务 ──
        // 显式 opt-in：仅当用户配置了 embedding 模型名（config.yaml Embedding.Model，
        // appsettings Embedding:Model 作回退）才走远程 embedding，且复用聊天 Provider 的
        // BaseUrl/API Key；未配置时用 DummyEmbeddingService（纯 BM25/FTS），不发任何远程请求。
        IEmbeddingService embeddingService;
        var embeddingModel = !string.IsNullOrWhiteSpace(savedEmbeddingModel)
            ? savedEmbeddingModel
            : config[ConfigurationKeys.EmbeddingModel];

        if (cloudEmbedding is not null)
        {
            // 显式 opt-in 优先：config.yaml Embedding.Provider = "cloud"（初始化时已构造并打印调试行）。
            embeddingService = cloudEmbedding;
        }
        else if (embeddingApi is not null && !string.IsNullOrWhiteSpace(embeddingModel))
        {
            embeddingService = new EmbeddingService(embeddingApi, embeddingModel);
            if (useDebug)
                Console.WriteLine($"[Debug] Embedding: LlmTornado ({embeddingModel})");
        }
        else if (tornadoApi is not null && !string.IsNullOrWhiteSpace(embeddingModel))
        {
            embeddingService = new EmbeddingService(tornadoApi, embeddingModel);
            if (useDebug)
                Console.WriteLine($"[Debug] Embedding: 回退到聊天连接器 ({embeddingModel})");
        }
        else
        {
            embeddingService = new DummyEmbeddingService();
            if (useDebug)
                Console.WriteLine(
                    "[Debug] Embedding: 未配置模型（设置 → Embedding），降级为 DummyEmbeddingService（纯 BM25/FTS）"
                );
        }

        // ── 检索存储（SQLite 优先，失败回退内存） ──
        IRetrievalStore retrievalStore;
        try
        {
            var sqliteRetrievalStore = new SqliteRetrievalStore(vectorDbPath, effectiveDimension);
            await sqliteRetrievalStore.InitializeAsync(ct);
            retrievalStore = sqliteRetrievalStore;
            if (useDebug)
                Console.WriteLine($"[Debug] 检索内核: SQLite ({vectorDbPath})");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"警告: 检索内核初始化失败 ({ex.Message})，使用内存回退存储"
            );
            retrievalStore = new InMemoryRetrievalStore();
        }
        services.AddSingleton(retrievalStore);
        services.AddSingleton(new DocumentBuilder());

        // ── Agent 记忆存储 ──
        // 注入 embeddingService 让 AgentMemoryStore 使用与 SqliteRetrievalStore 同一来源的，
        // 而不是各自默认 HashEmbedder（2024-05 回归修复）。
        AgentMemoryStore? agentMemoryStore = null;
        try
        {
            var agentMemory = new AgentMemoryStore(
                vectorDbPath,
                effectiveDimension,
                embeddingService: embeddingService,
                systemOne: systemOne
            );
            await agentMemory.InitializeAsync(ct);
            agentMemoryStore = agentMemory;
            if (useDebug)
                Console.WriteLine($"[Debug] Agent 记忆存储: SQLite ({vectorDbPath})");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"警告: Agent 记忆存储初始化失败 ({ex.Message})，agent 记忆功能不可用"
            );
        }
        if (agentMemoryStore is not null)
            services.AddSingleton(agentMemoryStore);

        var retrievalKernel = new RetrievalKernel(
            retrievalStore,
            embeddingService,
            agentMemoryStore: agentMemoryStore,
            systemOne: systemOne
        );
        services.AddSingleton(retrievalKernel);

        return (embeddingService, retrievalStore, retrievalKernel);
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：插件加载
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 从多个目录加载插件（Builtin / 全局 / 项目级 / 环境变量），支持三级信任模型。
    /// </summary>
    /// <param name="registry">工具注册表。</param>
    /// <param name="provider">DI 服务提供者。</param>
    /// <param name="useDebug">是否输出调试日志。</param>
    /// <returns>PluginLoader 实例。</returns>
    private static PluginLoader LoadPlugins(
        ToolRegistry registry,
        ServiceProvider provider,
        IConfiguration config,
        bool useDebug
    )
    {
        var builtinDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var envPluginsDir = Environment.GetEnvironmentVariable("NOVEL_PLUGINS_DIR");
        var localPluginsDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".mythology",
            "plugins"
        );
        var globalPluginsDir = Path.Combine(homeDir, ".mythology", "plugins");

        // 收集所有插件目录（Builtin 由目录位置判定，External 默认级别可被 DLL 签名升级为 Verified）
        var pluginDirs = new List<(string Directory, PluginTrustLevel Trust)>();
        if (Directory.Exists(builtinDir))
            pluginDirs.Add((builtinDir, PluginTrustLevel.Builtin));
        if (Directory.Exists(globalPluginsDir))
            pluginDirs.Add((globalPluginsDir, PluginTrustLevel.External));
        if (Directory.Exists(localPluginsDir))
            pluginDirs.Add((localPluginsDir, PluginTrustLevel.External));
        if (envPluginsDir is not null)
            pluginDirs.Add((envPluginsDir, PluginTrustLevel.External));

        var pluginLoader = new PluginLoader(logger: provider.GetService<ILogger<PluginLoader>>());
        if (pluginDirs.Count > 0)
        {
            // 从配置读取 trustedPublicKeyToken；未配置时为 null，Verified 信任级别不可用
            var trustedToken = config?["Plugins:TrustedPublicKeyToken"];
            pluginLoader.LoadAll(
                registry,
                pluginDirs,
                provider,
                trustedPublicKeyToken: trustedToken
            );
            if (useDebug)
            {
                foreach (var (dir, trust) in pluginDirs)
                    Console.WriteLine($"[Debug] 插件目录: {dir} ({trust})");
                Console.WriteLine(
                    $"[Debug] 加载了 {registry.Count} 个工具，来自 {registry.PluginCount} 个插件"
                );
            }
        }
        else
        {
            if (useDebug)
                Console.WriteLine("[Debug] 无插件目录");
        }

        return pluginLoader;
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：稳定性监控 + Agent 基础配置
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 配置 Agent 的稳定性监控（GrowthRatioGuard、VSA、RGDecimator）和 ModelContextProfile。
    /// 所有阈值从 ModelContextProfile 动态计算，不硬编码。
    /// </summary>
    /// <param name="agent">Agent 实例。</param>
    /// <param name="provider">DI 服务提供者。</param>
    /// <param name="mythologyDir">~/.mythology 目录（Todo 快照路径）。</param>
    /// <param name="sessionId">会话 ID。</param>
    private static void ConfigureAgentStability(
        Agent agent,
        ServiceProvider provider,
        string mythologyDir,
        string? sessionId
    )
    {
        var providerConfig = provider.GetRequiredService<ConfigStore>().Load();
        var modelName = providerConfig.Providers.DefaultModel;
        var contextWindowSize = providerConfig.Providers.ContextWindowSize;
        var thinkingLevelStr = providerConfig.Providers.ThinkingLevel;

        var effectiveContextSize = ModelCapabilityResolver.ResolveEffectiveContextSize(
            modelName,
            contextWindowSize
        );
        var thinkingLevel = ThinkingLevelBudgets.FromString(thinkingLevelStr);
        var isAdaptiveThinking = ModelCapabilityResolver.IsAdaptiveThinkingModel(modelName);

        // Adaptive-only thinking 模型不接受 manual budget_tokens，
        // ThinkingBudgetTokens 语义为"未知/不可预测"，设为 0。
        // HasThinking 通过 IsAdaptiveThinking 联合判断，不依赖此数值。
        var thinkingBudget = isAdaptiveThinking
            ? 0
            : ThinkingLevelBudgets.GetBudgetTokens(thinkingLevel, effectiveContextSize);

        var modelProfile = new ModelContextProfile
        {
            ContextWindowTokens = effectiveContextSize,
            ThinkingBudgetTokens = thinkingBudget,
            IsAdaptiveThinking = isAdaptiveThinking,
        };

        // GrowthRatioGuard：阈值从 ModelContextProfile 计算
        var stabilityConfig = new StabilityHarness.Lyapunov.StabilityConfig();
        stabilityConfig.ApplyProfile(modelProfile);
        agent.StabilityMonitor = StabilityHarness.StabilityHarness.CreateGuard(stabilityConfig);

        // 本地判定客户端（可选；与检索共用同一 DI 单例 → 设置页保存即时热更新）
        agent.SystemOne = provider.GetService<Core.Inference.ISystemOneClient>();

        // 全息引擎：漂移检测阈值从 ModelContextProfile 计算
        var vsaConfig = new StabilityHarness.VSA.VSAConfig();
        vsaConfig.ApplyProfile(modelProfile);
        agent.HolographicEngine = StabilityHarness.StabilityHarness.CreateHolographicEngine(
            vsaConfig
        );

        // 消息评分器：MaxRetained 和 Threshold 从 ModelContextProfile 计算
        var rgConfig = new StabilityHarness.RGDecimator.RGConfig();
        rgConfig.ApplyProfile(modelProfile);
        agent.MessageScorer = (IMessageScorer)
            StabilityHarness.StabilityHarness.CreateDecimator(rgConfig);

        // 同步 ConversationLoop 的 ModelContextProfile
        agent.ModelProfile = modelProfile;

        // 会话 ID
        agent.SessionId = sessionId;

        // ── Agent 结束后诊断 + Todo 生命周期 ──
        agent.OnEvent += async (evt, ct) =>
        {
            if (evt is AgentEnd && agent.StabilityMonitor is IGrowthRatioGuard guard)
            {
                var report = StabilityHarness.StabilityHarness.Analyze(guard);
                if (report.IsTripped)
                {
                    var diagLogger = provider.GetRequiredService<ILogger<Agent>>();
                    diagLogger.LogWarning(
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

            // AgentInterrupted → 未完成任务回退 pending；AgentEnd / AgentInterrupted → 保存任务快照
            if (evt is AgentInterrupted or AgentEnd)
            {
                var todoStore = provider.GetRequiredService<TodoStore>();
                if (evt is AgentInterrupted)
                    todoStore.RevertInProgressToPending();
                if (!string.IsNullOrEmpty(agent.SessionId))
                {
                    try
                    {
                        var todosDir = Path.Combine(mythologyDir, "todos");
                        Directory.CreateDirectory(todosDir);
                        todoStore.SaveTo(Path.Combine(todosDir, $"{agent.SessionId}.json"));
                    }
                    catch (Exception ex)
                    {
                        var todoLogger = provider.GetRequiredService<ILogger<Agent>>();
                        todoLogger.LogWarning(
                            ex,
                            "Todo 快照保存失败 (SessionId={SessionId})",
                            agent.SessionId
                        );
                    }
                }
            }
        };
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：Agent 钩子
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 配置 Agent 的 AfterToolCall 钩子（加载小说上下文 + 写作流程信号）。
    /// </summary>
    /// <param name="agent">Agent 实例</param>
    /// <param name="store">小说存储（供钩子读取小说数据）</param>
    /// <param name="provider">DI 服务提供者（预留扩展用）</param>
    /// <returns>云端诊断调度器（宿主退出时结算预算用；未装配时 null）。</returns>
    private static CloudDiagnosis.DiagnosisScheduler? ConfigureAgentHooks(
        Agent agent,
        INovelStore store,
        RetrievalKernel? retrievalKernel,
        ServiceProvider provider,
        NovelSemanticService semantic,
        IConfiguration config,
        UserQuestionStore? questionStore,
        string vectorsDbPath
    )
    {
        // ── HARNESS-CLOUD P1-G0：云端诊断组件（基础档 scene.plan 裁决回路）──
        // cloudHarness 缺失（未注册/测试环境）→ WritingFlowHook 保持纯本地行为。
        var cloudOptions = CloudDiagnosisOptions.FromConfiguration(config);
        // 始终装配：跨会话摘要（digest）不需要云端开关；信号计算由 RunAsync 内部按开关短路。
        agent.BeforeRun = new OntologyHarness(
            agent,
            store,
            cloudOptions,
            provider.GetService<ILogger<OntologyHarness>>()
        ).RunAsync;
        var cloudHarness = provider.GetService<IFictionHarnessClient>();
        var cloudLogger = provider.GetService<ILogger<DiagnosisScheduler>>();
        var snapshotCache = cloudHarness is not null
            ? new ContextSnapshotCache(
                cloudHarness,
                vectorsDbPath,
                provider.GetService<ILogger<ContextSnapshotCache>>(),
                cloudOptions.RequestTimeoutSeconds
            )
            : null;
        var budgetStore = new NovelBudgetStore(
            vectorsDbPath,
            provider.GetService<ILogger<NovelBudgetStore>>()
        );
        var cloudUsage = provider.GetService<ClientPlatform.UsageRestClient>();
        var usageReconciler =
            cloudHarness is not null && cloudUsage is not null
                ? new UsageReconciler(
                    cloudUsage,
                    cloudOptions,
                    provider.GetService<ILogger<UsageReconciler>>()
                )
                : null;
        var scheduler =
            cloudHarness is not null && questionStore is not null
                ? new DiagnosisScheduler(
                    cloudHarness,
                    cloudOptions,
                    snapshotCache,
                    cloudLogger,
                    budgetStore,
                    provider.GetService<ClientPlatform.CloudCapabilityCatalogStore>()
                )
                : null;
        var decisionBridge = questionStore is not null
            ? new HumanDecisionBridge(
                questionStore,
                cloudOptions.DecisionTimeoutSeconds,
                skipAnswerInjection: true, // TUI 复用 ask_user 渲染链路，答案已自动追加为 user 消息
                persistenceDbPath: vectorsDbPath,
                provider.GetService<ILogger<HumanDecisionBridge>>()
            )
            : null;
        var monotonyWatcher = new MonotonyWatcher(cloudOptions);

        // 双速自进化账本（单账本折叠）：DI 单例注入写作钩子；未注册（测试/精简宿主）→ null 降级。
        var tacticLedger = provider.GetService<TacticLedger>();

        // 记忆自动钩子：memory_recall/memory_find_conflicts 成功后自动标注有用性
        if (retrievalKernel?.AgentMemory is { } memStore)
        {
            agent.MemoryStore = memStore;
            var logger = provider.GetService<ILogger<AgentMemoryHook>>();
            agent.AfterToolCall = new CompositeToolHook(
                new LoadNovelHook(agent, store, semantic),
                new WritingFlowHook(
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
                ),
                new AgentMemoryHook(logger),
                new AssociativeTriggerHook(
                    agent,
                    store,
                    cloudHarness,
                    provider.GetService<ILogger<AssociativeTriggerHook>>()
                ),
                new PlotForecastHook(
                    agent,
                    store,
                    retrievalKernel,
                    provider.GetService<ILogger<PlotForecastHook>>()
                )
            );
        }
        else
        {
            agent.AfterToolCall = new CompositeToolHook(
                new LoadNovelHook(agent, store, semantic),
                new WritingFlowHook(
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
                ),
                new AssociativeTriggerHook(
                    agent,
                    store,
                    cloudHarness,
                    provider.GetService<ILogger<AssociativeTriggerHook>>()
                ),
                new PlotForecastHook(
                    agent,
                    store,
                    retrievalKernel,
                    provider.GetService<ILogger<PlotForecastHook>>()
                )
            );
        }

        // 宿主退出结算：scheduler 返回给 InitializeAsync → Host/Program.cs 在 TUI 退出后同步结算预算
        return scheduler;
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：完成度审计 + 反循环追踪
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 配置回合结束检查：完成度审计（草稿章节、孤立角色、未回收伏笔）+ 反循环追踪。
    /// 审计改走方案 2B 的 Tacos L3 聚合值；聚合值未变化时不重复注入信号。
    /// </summary>
    /// <param name="agent">Agent 实例</param>
    /// <param name="provider">DI 服务提供者（获取日志器等）</param>
    /// <param name="novelsRoot">小说根目录（Tacos 旁路数据源）</param>
    /// <param name="semantic"></param>
    private static void ConfigureAgentAuditLoop(
        Agent agent,
        ServiceProvider provider,
        NovelSemanticService semantic
    )
    {
        var auditLogger = provider.GetRequiredService<ILogger<AgentAuditHook>>();
        // 方案 2B · Phase 2：审计改走 Tacos L3 聚合值。
        // 移除手写 processedIssues 消息去重，改为按“聚合值是否变化”决定是否提醒。
        // C1 冷路径缓解：启动时后台预热/对账，避免首次审计在调用线程上做完整 YAML 解析。
        _ = Task.Run(() =>
        {
            try
            {
                semantic.ProbeAll();
            }
            catch
            {
                // 预热失败不阻断启动；后续每次查询仍会做 mtime 探测。
            }
        });
        var lastAuditState =
            new ConcurrentDictionary<string, (int Draft, int Orphan, int Overdue)>();

        agent.ShouldStopAfterTurn = (_, _, _, _) =>
        {
            if (string.IsNullOrEmpty(agent.CurrentNovelId))
                return Task.FromResult(false);

            var currentNovelId = agent.CurrentNovelId;
            var gaps = new List<string>();

            try
            {
                var draftCount = semantic.DraftChapterCount(currentNovelId);
                var chapterCount = semantic.ChapterIds(currentNovelId).Length;
                if (draftCount > 0 && draftCount < chapterCount)
                    gaps.Add($"有 {draftCount} 章仍为草稿状态");

                var orphanCount = semantic.OrphanCharacterCount(currentNovelId);
                if (orphanCount > 0)
                    gaps.Add($"{orphanCount} 个角色无任何关系");

                var overdueCount = semantic.OverdueForeshadowCount(currentNovelId);
                if (overdueCount > 3)
                    gaps.Add($"有 {overdueCount} 条未回收伏笔");

                var state = (Draft: draftCount, Orphan: orphanCount, Overdue: overdueCount);
                if (
                    gaps.Count > 0
                    && lastAuditState.TryGetValue(currentNovelId, out var last)
                    && last == state
                )
                    gaps.Clear();
                lastAuditState[currentNovelId] = state;
            }
            catch (Exception ex)
            {
                auditLogger.LogWarning(
                    ex,
                    "完成度审计查询失败 (NovelId={NovelId})",
                    currentNovelId
                );
            }

            if (gaps.Count > 0)
            {
                // ★ P0d: 审计提醒改走 InjectQueue（系统生成，静默注入，不渲染为 👤 气泡）
                agent.InjectQueue.Enqueue(
                    new LlmChatMessage(
                        ChatMessageRoles.User,
                        "📋 审计未通过，请继续完成：\n"
                            + string.Join("\n", gaps.Select(g => $"- {g}"))
                            + "\n\n直接行动，不要确认或道歉。"
                    )
                );
            }
            return Task.FromResult(false);
        };
    }

    // ══════════════════════════════════════════════════════════
    //  子步骤：检索索引同步
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 启动时执行检索索引增量同步，然后启动后台 Worker。
    /// 同步失败仅输出警告，不阻断启动。
    /// </summary>
    private static async Task SyncRetrievalIndexAsync(
        ServiceProvider provider,
        INovelStore store,
        CancellationToken ct
    )
    {
        try
        {
            var kernel = provider.GetService<RetrievalKernel>();
            if (kernel is not null)
            {
                var vectorSync = new VectorSyncService(
                    store,
                    kernel.Store,
                    kernel,
                    kernel.Builder,
                    provider.GetRequiredService<ILogger<VectorSyncService>>()
                );
                // 启动预算收口（CloudTimings.StartupIndexSyncTimeout）：同步会对变更实体调
                // embedding——远端不可达时每个调用都可能等到提供商超时，拖住 TUI 入口。
                // 超时 → 警告（检索内核按块捕获 OCE 后快速空转收尾，不阻塞；实时编辑由
                // Worker 增量补齐，遗漏的补偿对账下次启动自动完成）。
                using (var syncCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    syncCts.CancelAfter(ClientPlatform.CloudTimings.StartupIndexSyncTimeout);
                    await vectorSync.SyncAsync(syncCts.Token);
                    if (syncCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        await Console.Error.WriteLineAsync(
                            "警告: 检索索引同步超过启动预算，以部分索引启动（实时变更由 Worker 补齐，完整对账下轮启动重跑）"
                        );
                    }
                }
                kernel.StartWorker();
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"检索索引同步失败: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  工具方法
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 解析配置键，支持环境变量引用（格式：${ENV_NAME}）。
    /// </summary>
    /// <param name="value">配置值</param>
    /// <returns>解析后的值</returns>
    private static string? ResolveKey(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.StartsWith("${") && value.EndsWith('}'))
        {
            var envName = value[2..^1];
            return Environment.GetEnvironmentVariable(envName);
        }
        return value;
    }

    // ── 提供商 API 工厂（统一入口） ──

    /// <summary>
    /// 根据提供商配置创建 TornadoApi 实例（逻辑自 Core.Infrastructure.LlmApiFactory，两入口共享）。
    /// </summary>
    /// <param name="cfg">提供商配置</param>
    /// <returns>TornadoApi 实例、模型名称和 CancellationHttpHandler</returns>
    public static (TornadoApi Api, string Model, CancellationHttpHandler Handler) CreateProviderApi(
        ProviderConfig cfg
    ) => LlmApiFactory.CreateFromProviderConfig(cfg);
}

/// <summary>
/// 标记类，用于 ILogger&lt;AgentAuditHook&gt; 日志分类。
/// </summary>
internal sealed class AgentAuditHook;
