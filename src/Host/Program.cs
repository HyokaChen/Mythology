using ClientPlatform;
using Core.Agent;
using Core.Data;
using Core.Plugins;
using Microsoft.Extensions.Configuration;
using Tui;

namespace Host;

public static class Program
{
    // ── 子命令常量 ──
    private const string CmdBuild = "build";
    private const string CmdPlugin = "plugin";

    // ── 命令行选项常量 ──
    private const string OptDebug = "--debug";
    private const string OptMock = "--mock";
    private const string OptNovel = "--novel";
    private const string OptSession = "--session";
    private const string OptNovels = "--novels"; // 指定小说项目目录（默认 cwd/novels）

    // 取消令牌源，用于处理 Ctrl+C
    private static CancellationTokenSource? _cts;

    /// <summary>当前活跃的 Agent 实例，用于 Ctrl+C 时调用 Abort。</summary>
    private static Agent? _agent;

    /// <summary>插件加载器，程序退出时需要 Dispose 释放 DLL 锁。</summary>
    private static PluginLoader? _pluginLoader;

    /// <summary>
    /// 应用程序入口点。
    /// 负责配置加载、命令行解析、服务初始化和 TUI 启动。
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        // ── 加载 .env（开发用本地环境变量，优先级最低；已 export 的环境变量不受影响）──
        DotEnv.TryLoad();

        // ── 强制 truecolor（256 色降级毁掉 desert dark 色板） ──
        // 实测：256 色模式下 #5B3A29 沙丘/ #6E4A36 篝火岩 被降级为中性灰
        // （237 #3A3A3A / 239 #4E4E4E）→ 整个 desert 主题变灰、面板看起来是
        // 灰块 + 黑条。XenoAtom.Terminal 检测 COLORTERM=truecolor 时输出 24-bit
        // RGB，暖棕完整保留（实测 48;2;91;58;41）。Apple Terminal / iTerm2 均支持。
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable("COLORTERM"),
                "truecolor",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        }

        // ── 加载配置 ──
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        // ── 注册 Ctrl+C 取消支持 ──
        _cts = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            // ── 解析命令行参数 ──
            var argsList = args.ToList();
            var useDebug = argsList.Contains(OptDebug);
            var useMock = argsList.Contains(OptMock);

            // 解析 --novel 和 --session 参数
            string? novelId = null;
            string? sessionId = null;
            string? novelsPath = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == OptNovel && i + 1 < args.Length)
                    novelId = args[++i];
                if (args[i] == OptSession && i + 1 < args.Length)
                    sessionId = args[++i];
                if (args[i] == OptNovels && i + 1 < args.Length)
                    novelsPath = args[++i];
            }

            // ── 子命令路由（build / plugin） ──
            if (args.Length > 0)
            {
                var first = args[0].ToLowerInvariant();
                switch (first)
                {
                    case CmdBuild:
                        return await PluginCommands.HandleBuildAsync(args[1..]);
                    case CmdPlugin:
                        return await PluginCommands.HandlePluginAsync(args[1..]);
                }
            }

            var (
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
            ) = await HostBootstrapper.InitializeAsync(
                config,
                novelId,
                sessionId,
                useDebug,
                useMock,
                _cts.Token,
                novelsPath
            );
            _pluginLoader = pluginLoader;
            _agent = agent;

            // ── 应用 config.yaml 中保存的提供商配置 ──
            ApplySavedProvider(agent, configStore);
            if (agent.TornadoApi is null)
                Console.WriteLine("[Warning] 未配置 LLM API Key。请在设置 (Ctrl+P) 中配置后重试。");

            // ── 启动 TUI 应用程序 ──
            var tui = new TuiApp(
                agent,
                store,
                sessionStore,
                registry,
                todoStore,
                questionStore,
                novelId,
                sessionId,
                HostBootstrapper.CreateProviderApi,
                configStore,
                pluginLoader,
                contextModeSwitcher: contextModeSwitcher, // 设置页「上下文」标签页热切换
                retrievalKernel: retrievalKernel, // 检索内核（可选，用于 UI 实时索引和 OnEntityChanged 事件）
                cloudConnection: cloudConnection, // §13.6 TUI 云端连接门面（DI 单例，与客户端共享容器）
                logger: tuiLogger,
                cloudHarness: cloudHarness, // CHANGE_10 §4.3 设定检查反馈上报
                novelSync: novelSync, // cloud-novel-sync v3（:cloud sync / :cloud pull）
                cloudUsage: cloudUsage, // :cloud status 积分余量（零价 usage 查询）
                weChat: weChat, // 微信通道（/wechat bind|status|unbind + 长轮询驱动 Agent）
                loggerFactory: loggerFactory
            );
            await tui.RunAsync(_cts.Token);
            // 宿主退出结算（§16.5 方案 C）：TUI 退出 = 会话结束，同步结算当前小说预算
            // （SettleSession 为同步方法，直接调用不阻塞异步路径）
            var settleNovelId = agent.CurrentNovelId;
            if (cloudScheduler is not null && !string.IsNullOrEmpty(settleNovelId))
                cloudScheduler.SettleSession(settleNovelId);
            // 退出后清屏：XenoAtom 恢复终端状态但不保证清屏，显式清屏保证 shell 干净
            // Console.Clear();
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"TUI 启动失败: {Core.Shared.SecretRedactor.Redact(ex.ToString())}"
            );
            // 不 rethrow：错误已脱敏输出，rethrow 会让运行时再打一份未脱敏的 unhandled 堆栈
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            _pluginLoader?.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Ctrl+C 事件处理：取消当前操作而不是退出程序。
    /// </summary>
    private static void OnCancelKeyPress(object? _, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _agent?.Abort();
    }

    /// <summary>
    /// 应用 config.yaml 中保存的提供商配置到 Agent。
    /// 包括默认模型、思考级别和 API 密钥。
    /// </summary>
    /// <param name="agent">Agent 实例</param>
    /// <param name="configStore">配置存储（从 DI 容器获取，确保路径一致）</param>
    private static void ApplySavedProvider(Agent agent, ConfigStore configStore)
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

        // 应用默认模型
        if (!string.IsNullOrEmpty(cfg.DefaultModel))
            agent.SetModelName(cfg.DefaultModel);
        // 应用思考级别
        if (!string.IsNullOrEmpty(cfg.ThinkingLevel))
            agent.SetThinkingLevel(cfg.ThinkingLevel);

        // 如果有 API keys，重新创建 TornadoApi
        var hasKeys = cfg.ApiKeys.Exists(k => !string.IsNullOrEmpty(k.ApiKey));
        if (hasKeys)
        {
            var (api, _, handler) = HostBootstrapper.CreateProviderApi(cfg);
            agent.TornadoApi = api;
            agent.SetHttpHandler(handler);
        }
    }
}
