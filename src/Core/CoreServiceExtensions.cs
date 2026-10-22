using Core.Data;
using Core.Data.Migrations;
using Core.Infrastructure;
using Core.Plugins;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using LlmTornado;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using AgentCore = Core.Agent.Agent;

namespace Core;

/// <summary>
/// 两个入口（Host TUI / Desktop GUI）共用的 Core 服务注册。
/// 从 Host.HostBootstrapper 提取：日志、数据迁移、存储、检索内核、Agent 与插件加载。
/// 入口特有服务（TUI 稳定监控 / Desktop 事件桥接等）由各自入口在调用后补充注册。
/// </summary>
public static class CoreServiceExtensions
{
    /// <summary>
    /// 注册两个入口共用的 Core 服务（无 UI 依赖，可安全用于 TUI 与 GUI）。
    /// </summary>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="config">IConfiguration（appsettings.json + 环境变量）。</param>
    /// <param name="useDebug">是否输出调试日志。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="embeddingFactory">
    /// 可选：自定义 IEmbeddingService 工厂（如云端 embedding 客户端）。
    /// 缺省使用 DummyEmbeddingService（零向量 → 纯 BM25/FTS 降级）。
    /// </param>
    /// <param name="mythologyDirOverride">可选：覆盖 ~/.mythology 数据目录（测试用）。</param>
    /// <param name="workspace">
    /// 可选：小说项目目录工作区（两端由 <c>NovelWorkspace.Open</c> 构造后传入）。
    /// 缺省时按同一解析链取单根工作区（不落盘，测试/CLI 语义）。
    /// </param>
    public static async Task AddMythologyCoreServicesAsync(
        this IServiceCollection services,
        IConfiguration config,
        bool useDebug,
        CancellationToken ct = default,
        Func<IServiceProvider, IEmbeddingService>? embeddingFactory = null,
        string? mythologyDirOverride = null,
        NovelWorkspace? workspace = null
    )
    {
        // 项目目录工作区：读取范围 = 清单并集，写入范围 = ActiveRoot
        var novelWorkspace =
            workspace ?? NovelWorkspace.Single(NovelWorkspace.ResolveNovelsRoot(config).Path);
        var novelsRoot = novelWorkspace.ActiveRoot;
        var mythologyDir =
            mythologyDirOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mythology"
            );

        // ── 日志配置（滚动文件，保留 14 天） ──
        var serilogLogger = ConfigureFileLogging(mythologyDir);
        services.AddLogging(builder => builder.AddSerilog(serilogLogger, dispose: true));
        services.AddSingleton(config);

        // ── 数据迁移（优先于所有服务构建） ──
        var migrationLogger = new LoggerFactory()
            .AddSerilog(serilogLogger)
            .CreateLogger<MigrationRunner>();
        await new MigrationRunner(mythologyDir, novelsRoot, migrationLogger).RunAsync(ct);

        // ── 数据存储（工作区单例 = UI 管理项目目录清单的入口）──
        services.AddSingleton(novelWorkspace);
        RegisterStores(services, novelWorkspace, mythologyDir, config[ConfigurationKeys.StoreMode]);

        // ── Embedding / 向量 / 检索内核 ──
        // 与 TUI 一致：从 config.yaml 读取 Provider 重建 TornadoApi + Embedding.Model
        // （appsettings / embeddingFactory 仅作回退/测试覆盖）
        var configStoreForRetrieval = new ConfigStore(mythologyDir);
        TornadoApi? effectiveTornadoApi = null;
        TornadoApi? embeddingApi = null;
        string? savedEmbeddingModel = null;
        var systemOneConfig = new SystemOneConfig();
        var savedModel = config[ConfigurationKeys.LlmModel] ?? "gpt-4o-mini";
        try
        {
            var savedCfg = configStoreForRetrieval.Load();
            systemOneConfig = savedCfg.SystemOne;
            if (savedCfg.Providers.ApiKeys.Exists(k => !string.IsNullOrEmpty(k.ApiKey)))
            {
                var (api, model, _) = LlmApiFactory.CreateFromProviderConfig(savedCfg.Providers);
                effectiveTornadoApi = api;
                savedModel = model;
            }
            if (savedCfg.Embedding is { ApiKeys.Count: > 0 } emb)
            {
                var (embApi, embModel, _) = LlmApiFactory.CreateFromEmbeddingConfig(emb);
                embeddingApi = embApi;
                savedEmbeddingModel = embModel;
            }
            else if (!string.IsNullOrWhiteSpace(savedCfg.Embedding?.Model))
            {
                savedEmbeddingModel = savedCfg.Embedding.Model;
            }
            // 注意：Model 为空 = 未配置 embedding（纯 BM25/FTS），
            // 不能把 DefaultModel（占位默认 text-embedding-3-small）当作已配置。
        }
        catch
        {
            // config.yaml 不存在/损坏 → 回退到 appsettings 的 TornadoApi
        }
        effectiveTornadoApi ??= LlmApiFactory.CreateFromAppSettings(config, useDebug).Api;

        // ── 本地判定模型（System One / d1-omni；可选，默认关闭，不启用时零请求）──
        var systemOneClient = new Core.Inference.SystemOneClient(systemOneConfig);
        services.AddSingleton<Core.Inference.ISystemOneClient>(systemOneClient);

        await RegisterRetrievalAsync(
            services,
            mythologyDir,
            useDebug,
            ct,
            embeddingFactory,
            effectiveTornadoApi,
            embeddingApi,
            savedEmbeddingModel,
            systemOneClient
        );

        // ── Agent 注册到 DI（使用 config.yaml 覆盖后的 TornadoApi） ──
        services.AddSingleton<AgentCore>(sp =>
        {
            var registry = sp.GetRequiredService<ToolRegistry>();
            return new AgentCore(registry, effectiveTornadoApi);
        });
    }

    /// <summary>
    /// 配置滚动文件日志：~/.mythology/logs/mythology-.log，Information+，保留 14 天。
    /// </summary>
    private static Serilog.Core.Logger ConfigureFileLogging(string mythologyDir)
    {
        var logsDir = Path.Combine(mythologyDir, "logs");
        Directory.CreateDirectory(logsDir);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDir, "mythology-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();
    }

    /// <summary>
    /// 注册所有数据存储到 DI 容器：ConfigStore、FileNovelStore、FileSessionStore、PluginStore、ToolRegistry 等。
    /// 云端客户端（RetrievalRestClient / IFictionHarnessClient）属于 TUI 特有功能，不在此注册。
    /// </summary>
    private static void RegisterStores(
        IServiceCollection services,
        NovelWorkspace workspace,
        string mythologyDir,
        string? storeMode = null
    )
    {
        var mode = ParseStoreMode(storeMode);
        var semantic = new NovelSemanticService(workspace);

        services.AddSingleton<NovelSemanticService>(_ => semantic);
        services.AddSingleton<ConfigStore>(_ => new ConfigStore(mythologyDir));
        services.AddSingleton<FileNovelStore>(_ =>
        {
            var store = new FileNovelStore(workspace, mythologyDir, semantic) { StoreMode = mode };
            return store;
        });
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
    }

    /// <summary>解析 Data:StoreMode 灰度开关。</summary>
    private static NovelStoreMode ParseStoreMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "incremental" => NovelStoreMode.Incremental,
            "shadow" => NovelStoreMode.Shadow,
            _ => NovelStoreMode.Direct,
        };

    /// <summary>
    /// 配置 Embedding 服务、向量存储、检索内核和用户记忆存储。
    /// 初始化失败时回退到 DummyEmbeddingService / InMemoryRetrievalStore。
    /// </summary>
    /// <param name="embeddingFactory">入口自定义 embedding 工厂（测试覆盖用）；为 null 时走 LlmTornado 路径。</param>
    /// <param name="tornadoApi">已被 config.yaml 覆盖过的聊天 TornadoApi；为 null 时无 embedding 可用。</param>
    /// <param name="embeddingApi">Embedding 专用的 TornadoApi（独立配置）；为 null 时回退到 tornadoApi。</param>
    /// <param name="savedEmbeddingModel">从 config.yaml 读取的 embedding 模型名（可为 null，无则用默认/回退）。</param>
    private static async Task<(
        IEmbeddingService EmbeddingService,
        IRetrievalStore RetrievalStore,
        RetrievalKernel? RetrievalKernel
    )> RegisterRetrievalAsync(
        IServiceCollection services,
        string mythologyDir,
        bool useDebug,
        CancellationToken ct,
        Func<IServiceProvider, IEmbeddingService>? embeddingFactory,
        TornadoApi? tornadoApi,
        TornadoApi? embeddingApi,
        string? savedEmbeddingModel,
        Core.Inference.ISystemOneClient? systemOne
    )
    {
        var vectorDbPath = Path.Combine(mythologyDir, "vectors.db");
        // 向量维度固定为 VectorDefaults.Dimension（1024）——不启动探测：
        // 那是启动链路上的远程调用，远端不可达时会拖住入口；维度不一致在真实索引时显式失败。
        const int effectiveDimension = VectorDefaults.Dimension;

        // ── Embedding 服务 ──
        // 优先级：embeddingFactory（测试覆盖） > 独立 embedding TornadoApi > 聊天 API 回退 > Dummy。
        // 显式 opt-in：未配置 embedding 模型名时不发任何远程 embedding 请求，检索降级为纯 BM25/FTS。
        IEmbeddingService embeddingService;
        if (embeddingFactory is not null)
        {
            embeddingService = embeddingFactory.Invoke(null!);
        }
        else if (embeddingApi is not null && !string.IsNullOrWhiteSpace(savedEmbeddingModel))
        {
            embeddingService = new EmbeddingService(embeddingApi, savedEmbeddingModel);
            if (useDebug)
                Console.WriteLine($"[Debug] Embedding: LlmTornado ({savedEmbeddingModel})");
        }
        else if (tornadoApi is not null && !string.IsNullOrWhiteSpace(savedEmbeddingModel))
        {
            // 回退：无独立 embedding 密钥时复用聊天连接器
            embeddingService = new EmbeddingService(tornadoApi, savedEmbeddingModel);
            if (useDebug)
                Console.WriteLine($"[Debug] Embedding: 回退到聊天连接器 ({savedEmbeddingModel})");
        }
        else
        {
            embeddingService = new DummyEmbeddingService();
            if (useDebug)
                Console.WriteLine(
                    "[Debug] Embedding: 未配置模型，降级为 DummyEmbeddingService（纯 BM25/FTS）"
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
        // 注入 embeddingService（与 SqliteRetrievalStore 同源），避免各自默认 HashEmbedder 造成向量通道分叉。
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

        return (embeddingService, retrievalStore, retrievalKernel);
    }

    /// <summary>
    /// 从多个目录加载插件（Builtin / 全局 / 项目级 / 环境变量），支持三级信任模型。
    /// 需在 BuildServiceProvider 之后调用（插件通过 DI 注入依赖）。
    /// </summary>
    public static PluginLoader LoadPlugins(
        ServiceProvider provider,
        IConfiguration config,
        bool useDebug
    )
    {
        var registry = provider.GetRequiredService<ToolRegistry>();
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
        else if (useDebug)
        {
            Console.WriteLine("[Debug] 无插件目录");
        }

        return pluginLoader;
    }

    /// <summary>
    /// 启动时执行检索索引增量同步，然后启动后台 Worker。
    /// 同步失败仅输出警告，不阻断启动。
    /// </summary>
    public static async Task SyncRetrievalIndexAsync(ServiceProvider provider, CancellationToken ct)
    {
        try
        {
            var kernel = provider.GetService<RetrievalKernel>();
            if (kernel is not null)
            {
                var store = provider.GetRequiredService<INovelStore>();
                var vectorSync = new VectorSyncService(
                    store,
                    kernel.Store,
                    kernel,
                    kernel.Builder,
                    provider.GetRequiredService<ILogger<VectorSyncService>>()
                );
                await vectorSync.SyncAsync(ct);
                kernel.StartWorker();
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"检索索引同步失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 应用 config.yaml 中保存的提供商配置到 Agent（默认模型、思考级别、API Key）。
    /// 两个入口共用：TUI 的 ApplySavedProvider 与 Desktop 启动流程一致。
    /// </summary>
    public static void ApplySavedProviderConfig(AgentCore agent, ConfigStore configStore)
    {
        ConfigData configData;
        try
        {
            configData = configStore.Load();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] 读取 config.yaml 失败: {ex.Message}");
            return;
        }

        var cfg = configData.Providers;

        if (!string.IsNullOrEmpty(cfg.DefaultModel))
            agent.SetModelName(cfg.DefaultModel);
        if (!string.IsNullOrEmpty(cfg.ThinkingLevel))
            agent.SetThinkingLevel(cfg.ThinkingLevel);

        var hasKeys = cfg.ApiKeys.Exists(k => !string.IsNullOrEmpty(k.ApiKey));
        if (hasKeys)
        {
            var (api, _, handler) = LlmApiFactory.CreateFromProviderConfig(cfg);
            agent.TornadoApi = api;
            agent.SetHttpHandler(handler);
        }
    }
}
