using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Channels.WeChat;
using Core.Agent;
using Core.Data;
using Core.Infrastructure;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Desktop.Services;
using LlmTornado;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace Desktop.ViewModels;

/// <summary>
/// 设置视图模型（功能对齐 TUI SettingsDialog，风格 Adaline）：
/// 连接器（提供商/模型/思考级别/上下文窗口/多 API Key）· MCP 服务器 · 工具开关。
/// 绑定 ConfigStore，保存 YAML 即时生效。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ConfigStore? _configStore;
    private readonly Agent? _agent;
    private readonly ToolRegistry? _registry;
    private readonly RetrievalKernel? _retrievalKernel;
    private ConfigData? _config;
    private int _chatModelDiscoveryVersion;
    private int _embeddingModelDiscoveryVersion;

    /// <summary>提供商下拉选项（显示名）。</summary>
    public ObservableCollection<string> ProviderOptions { get; } = [];

    /// <summary>思考级别选项（与 TUI 共用 Core.ThinkingLevelBudgets.Options，排除 Off）。</summary>
    public ObservableCollection<string> ThinkingLevelOptions { get; } =
    [.. ThinkingLevelBudgets.Options.Select(o => o.Key)];

    /// <summary>MCP 传输选项。</summary>
    public IReadOnlyList<string> TransportOptions { get; } = ["stdio", "streamable-http", "sse"];

    /// <summary>上下文窗口选项（label → tokens，与 TUI ContextWindowSizes 对齐）。</summary>
    public ObservableCollection<string> ContextWindowOptions { get; } =
    ["8K", "16K", "32K", "64K", "128K", "200K", "256K", "512K", "1M"];

    /// <summary>API Key 卡片（多提供商）。</summary>
    public ObservableCollection<ApiKeyRow> ApiKeys { get; } = [];

    /// <summary>MCP 服务器。</summary>
    public ObservableCollection<McpServerRow> McpServers { get; } = [];

    // ─────────── 上下文管理（compact | continuation） ───────────

    /// <summary>上下文管理模式选项（显示名 ↔ config 值）。</summary>
    public ObservableCollection<string> ContextModeOptions { get; } =
    ["压缩（摘要，保留最近若干条）", "接续（换页，不丢细节）"];

    /// <summary>当前选中的上下文管理模式显示名。</summary>
    public string ContextModeDisplay
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "压缩（摘要，保留最近若干条）";

    /// <summary>接续模式：封页时建立向量索引（旧页语义检索通道）。</summary>
    public bool ContextVectorRetrieval
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>上下文管理状态说明（保存后回显实际生效结果 / 不可用的原因）。</summary>
    public string ContextModeStatus
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>接续插件是否已加载（未加载时只能压缩）。</summary>
    public bool IsContextContinuationAvailable =>
        Program.ContextModeSwitcher?.IsPluginAvailable ?? false;

    /// <summary>插件（工具 Tab）。</summary>
    public ObservableCollection<SkillRow> Plugins { get; } = [];

    public string ProviderDisplay
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "自定义";

    /// <summary>Embedding 提供商下拉选项（显示名）。</summary>
    public ObservableCollection<string> EmbeddingProviderOptions { get; } = [];

    /// <summary>Embedding API Key 卡片（独立 embedding 连接器）。</summary>
    public ObservableCollection<ApiKeyRow> EmbeddingApiKeys { get; } = [];

    public string EmbeddingProviderDisplay
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "OpenAI";

    public string Model
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    // ─────────── 模型发现（连接器 Tab「发现模型」按钮） ───────────

    /// <summary>发现到的模型列表（下拉选项；空 → 手动输入回退）。</summary>
    public ObservableCollection<string> ModelOptions { get; } = [];

    /// <summary>模型下拉是否可见（发现成功 true；手动输入 false）。</summary>
    public bool IsModelListVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>模型发现进行中（按钮禁用）。</summary>
    public bool IsDiscoveringModels
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>模型发现状态文案（错误提示 / 结果计数）。</summary>
    public string ModelDiscoverStatus
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    // ─────────── 模型发现（嵌入 Tab，与连接器共用 DiscoverAsync 实现） ───────────

    /// <summary>发现到的 embedding 模型列表（下拉选项）。</summary>
    public ObservableCollection<string> EmbeddingModelOptions { get; } = [];

    /// <summary>embedding 模型下拉是否可见。</summary>
    public bool IsEmbeddingModelListVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>embedding 模型发现进行中（按钮禁用）。</summary>
    public bool IsDiscoveringEmbeddingModels
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>embedding 模型发现状态文案。</summary>
    public string EmbeddingModelDiscoverStatus
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string ThinkingLevel
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "medium";

    public string ContextWindow
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "128K";

    // 空 = 未配置（占位符提示 text-embedding-3-small），保存空值即保持纯 BM25 检索
    public string EmbeddingModel
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string VectorDbPath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    // ── 本地判定（System One / d1-omni）· 可选（默认关闭） ──

    public bool SystemOneEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string SystemOneBaseUrl
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string SystemOnePath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string SystemOneApiKey
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string SystemOneModel
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string SystemOneTimeoutMs
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string SystemOneMaxStateChars
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public bool SystemOneRerankEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool SystemOneConflictsEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool SystemOneRelationsEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>循环判定·停止复核（有效值 = 主开关 ∧ 子开关）。</summary>
    public bool SystemOneStopCheckEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>循环判定·漂移复核。</summary>
    public bool SystemOneDriftCheckEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>循环判定·稳定性复核。</summary>
    public bool SystemOneStabilityCheckEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string SystemOneRelationsMinProbability
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string SaveStatus
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    // ── Cloud 账号 ──

    public bool IsCloudLoggedIn
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string CloudDisplayId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string CloudPlan
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string CloudSavedAt
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string CloudStatus
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>云端账号门面（登录/登出/状态由 CloudAuthService 统一管理）。</summary>
    public CloudAuthService? CloudAuth { get; }

    /// <summary>保存命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

    /// <summary>设计时构造。</summary>
    public SettingsViewModel()
        : this(null, null, null, null) { }

    // ─────────── 外观（主题即时生效，独立于「保存」按钮） ───────────

    /// <summary>主题服务（从 DI 惰性解析，设计时为 null）。</summary>
    private static DesktopThemeService? ThemeService =>
        Program.Services?.GetService<DesktopThemeService>();

    /// <summary>深色主题开关（切换即时生效并持久化，无需点保存）。</summary>
    public bool IsDarkTheme
    {
        get => ThemeService?.IsDark ?? false;
        set
        {
            if (ThemeService is not { } svc || svc.IsDark == value)
                return;
            svc.SetDark(value);
            this.RaisePropertyChanged();
        }
    }

    /// <summary>
    /// 从当前聊天提供商 API 发现可用模型（对齐 TUI ProviderTab.LoadModelsAsync）。
    /// </summary>
    public Task DiscoverModelsAsync()
    {
        var version = ++_chatModelDiscoveryVersion;
        return DiscoverAsync(
            ProviderDisplay,
            ApiKeys,
            ModelOptions,
            isDiscovering: v => IsDiscoveringModels = v,
            isListVisible: v => IsModelListVisible = v,
            status: v => ModelDiscoverStatus = v,
            currentModel: () => Model,
            setModel: v => Model = v,
            isCurrent: () => version == _chatModelDiscoveryVersion
        );
    }

    /// <summary>
    /// 从当前 embedding 提供商 API 发现可用模型（对齐 TUI EmbeddingTab.LoadModelsAsync，
    /// 与聊天连接器共用同一发现逻辑与 ModelDiscoveryService）。
    /// </summary>
    public Task DiscoverEmbeddingModelsAsync()
    {
        var version = ++_embeddingModelDiscoveryVersion;
        return DiscoverAsync(
            EmbeddingProviderDisplay,
            EmbeddingApiKeys,
            EmbeddingModelOptions,
            isDiscovering: v => IsDiscoveringEmbeddingModels = v,
            isListVisible: v => IsEmbeddingModelListVisible = v,
            status: v => EmbeddingModelDiscoverStatus = v,
            currentModel: () => EmbeddingModel,
            setModel: v => EmbeddingModel = v,
            isCurrent: () => version == _embeddingModelDiscoveryVersion
        );
    }

    /// <summary>
    /// 发现共享实现（聊天 / embedding 两处调用）：
    /// 自定义提供商不支持（无 per-custom 选择，取首行会错配 BaseUrl）→ 状态提示手动输入；
    /// 无该提供商密钥 → 提示先填密钥；空结果 → 手动输入回退；成功 → 填下拉并保留当前模型（IgnoreCase）。
    /// </summary>
    private async Task DiscoverAsync(
        string providerDisplay,
        IReadOnlyList<ApiKeyRow> rows,
        ObservableCollection<string> options,
        Action<bool> isDiscovering,
        Action<bool> isListVisible,
        Action<string> status,
        Func<string> currentModel,
        Action<string> setModel,
        Func<bool> isCurrent
    )
    {
        // 新请求开始时先归零上一次请求的进行中状态；旧请求返回后会被 isCurrent() 丢弃。
        isDiscovering(false);

        var configKey = ResolveProviderConfigKey(providerDisplay, rows);
        var isCustom = ProviderDefinition.IsCustom(configKey);
        var def = isCustom ? null : ProviderDefinition.FindByConfigKey(configKey);

        // 找当前连接的密钥行：自定义按名称匹配，预设按 ConfigKey 匹配
        var customName = isCustom ? ProviderDefinition.GetCustomName(configKey) : null;
        var row = isCustom
            ? rows.FirstOrDefault(r =>
                ProviderDefinition.IsCustom(r.Provider)
                && string.Equals(
                    string.IsNullOrWhiteSpace(r.Name) ? null : r.Name.Trim(),
                    string.IsNullOrWhiteSpace(customName) ? null : customName,
                    StringComparison.Ordinal
                )
            )
            : rows.FirstOrDefault(r => r.Provider == configKey);

        if (row is null || string.IsNullOrWhiteSpace(row.ApiKey))
        {
            status("请先在下方为该提供商填写 API Key");
            isListVisible(false);
            return;
        }

        // 自定义端点必须有 BaseUrl 才能拼 /v1/models
        if (isCustom && string.IsNullOrWhiteSpace(row.BaseUrl))
        {
            status("自定义提供商需先填写 Base URL 才能发现模型");
            isListVisible(false);
            return;
        }

        isDiscovering(true);
        status("发现中…");
        var entry = new ApiKeyEntry
        {
            Provider = configKey,
            ApiKey = row.ApiKey,
            BaseUrl = string.IsNullOrWhiteSpace(row.BaseUrl) ? null : row.BaseUrl.Trim(),
        };
        var models = await ModelDiscoveryService.GetModelsAsync(entry, def);

        // Provider 已切换，或已发起更新的发现请求：旧响应不得覆盖当前下拉/模型状态。
        if (!isCurrent())
            return;

        isDiscovering(false);

        if (models.Count == 0)
        {
            status("未获取到模型列表，请手动输入");
            isListVisible(false);
            return;
        }

        // 必须在清空 options 前读取当前模型：ComboBox 双向绑定在 Clear() 时会先把 Model 写回 null。
        var current = currentModel();
        options.Clear();
        foreach (var m in models)
            options.Add(m);
        // 优先保留当前已填模型（IgnoreCase 匹配），否则选首个（镜像 TUI ProviderTab）
        var match = models.FirstOrDefault(m =>
            string.Equals(m, current, StringComparison.OrdinalIgnoreCase)
        );
        setModel(match ?? models[0]);
        isListVisible(true);
        status($"发现 {models.Count} 个模型");
    }

    /// <summary>
    /// 聊天提供商变化后，旧模型列表不再对应当前连接，必须退回手动输入。
    /// 保留 Model 作为手动兜底；版本号自增使在途请求结果失效。
    /// </summary>
    private void ResetChatModelDiscovery()
    {
        _chatModelDiscoveryVersion++;
        IsDiscoveringModels = false;

        if (ModelOptions.Count == 0 && !IsModelListVisible)
        {
            ModelDiscoverStatus = "";
            return;
        }

        IsModelListVisible = false;
        ModelDiscoverStatus = "提供商已更改，请重新发现模型";
    }

    /// <summary>embedding 提供商变化后的旧列表失效逻辑（同聊天连接器）。</summary>
    private void ResetEmbeddingModelDiscovery()
    {
        _embeddingModelDiscoveryVersion++;
        IsDiscoveringEmbeddingModels = false;

        if (EmbeddingModelOptions.Count == 0 && !IsEmbeddingModelListVisible)
        {
            EmbeddingModelDiscoverStatus = "";
            return;
        }

        IsEmbeddingModelListVisible = false;
        EmbeddingModelDiscoverStatus = "embedding 提供商已更改，请重新发现模型";
    }

    public SettingsViewModel(
        ConfigStore? configStore,
        Agent? agent,
        ToolRegistry? registry,
        RetrievalKernel? retrievalKernel = null,
        CloudAuthService? cloudAuth = null,
        WeChatChannelService? weChat = null,
        UpdateState? updateState = null
    )
    {
        _configStore = configStore;
        _agent = agent;
        _registry = registry;
        _retrievalKernel = retrievalKernel;
        CloudAuth = cloudAuth;
        _weChat = weChat;
        _updateState = updateState;
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);

        ApiKeys.CollectionChanged += (_, _) => SyncProviderOptions();
        EmbeddingApiKeys.CollectionChanged += (_, _) => SyncProviderOptions();
        RebuildProviderOptions(ProviderOptions, ApiKeys);
        RebuildProviderOptions(EmbeddingProviderOptions, EmbeddingApiKeys);

        // 切换提供商后，旧下拉列表不再对应当前连接；退出手动输入并废弃在途发现结果。
        this.WhenAnyValue(x => x.ProviderDisplay).Subscribe(_ => ResetChatModelDiscovery());
        this.WhenAnyValue(x => x.EmbeddingProviderDisplay)
            .Subscribe(_ => ResetEmbeddingModelDiscovery());
    }

    /// <summary>微信通道（Host 未接线时为 null，卡片随之隐藏）。</summary>
    private readonly WeChatChannelService? _weChat;

    /// <summary>共享更新状态单例（「关于」Tab 更新卡片绑定；设计时为 null）。</summary>
    private readonly UpdateState? _updateState;

    /// <summary>更新卡片绑定源（与弹窗共享同一单例，状态不可能不一致）。</summary>
    public UpdateState? Update => _updateState;

    private WeChatChannelViewModel? _weChatViewModel;

    /// <summary>微信卡片（惰性创建；无通道服务时为 null）。</summary>
    public WeChatChannelViewModel? WeChat =>
        _weChat is null ? null : _weChatViewModel ??= new WeChatChannelViewModel(_weChat);

    /// <summary>是否显示微信卡片（未接线 = 隐藏）。</summary>
    public bool HasWeChatCard => _weChat is not null;

    /// <summary>刷新（打开设置窗口时调用）。</summary>
    public SettingsViewModel Refresh()
    {
        _config = _configStore?.Load();
        var providers = _config?.Providers;

        ApiKeys.Clear();
        McpServers.Clear();
        Plugins.Clear();

        if (providers is not null)
        {
            Model = providers.DefaultModel;
            ThinkingLevel = providers.ThinkingLevel;
            ContextWindow =
                ContextWindowOptions.FirstOrDefault(o =>
                    ParseWindow(o) == providers.ContextWindowSize
                ) ?? "128K";

            ApiKeys.Clear();
            foreach (var key in providers.ApiKeys)
                ApiKeys.Add(CreateApiKeyRow(key));
            if (ApiKeys.Count == 0)
                ApiKeys.Add(new ApiKeyRow("openai", "", "", ""));
            RebuildProviderOptions(ProviderOptions, ApiKeys);
            ProviderDisplay = ToProviderDisplay(ProviderOptions, providers.Default);
        }

        if (_config is not null)
        {
            foreach (var server in _config.Mcp.Servers)
            {
                McpServers.Add(
                    new McpServerRow(
                        server.Name,
                        server.Transport,
                        server.Url ?? "",
                        server.Command,
                        string.Join(" ", server.Args),
                        server.Enabled,
                        server.Auth,
                        server.BearerToken,
                        server.AuthHeader,
                        server.AuthValue
                    )
                );
            }

            // 新格式写入 DefaultModel（Model 为遗留兼容字段）：优先读 DefaultModel，回退 Model
            EmbeddingModel = !string.IsNullOrWhiteSpace(_config.Embedding.DefaultModel)
                ? _config.Embedding.DefaultModel
                : _config.Embedding.Model;
            EmbeddingApiKeys.Clear();
            foreach (var key in _config.Embedding.ApiKeys)
                EmbeddingApiKeys.Add(CreateApiKeyRow(key));
            if (EmbeddingApiKeys.Count == 0 && !string.IsNullOrWhiteSpace(EmbeddingModel))
                EmbeddingApiKeys.Add(new ApiKeyRow("openai", "", "", ""));
            RebuildProviderOptions(EmbeddingProviderOptions, EmbeddingApiKeys);
            EmbeddingProviderDisplay = ToProviderDisplay(
                EmbeddingProviderOptions,
                _config.Embedding.Default
            );

            var systemOne = _config.SystemOne;
            SystemOneEnabled = systemOne.Enabled;
            SystemOneBaseUrl = systemOne.BaseUrl;
            SystemOnePath = systemOne.Path;
            SystemOneApiKey = systemOne.ApiKey;
            SystemOneModel = systemOne.Model;
            SystemOneTimeoutMs = systemOne.TimeoutMs.ToString(CultureInfo.InvariantCulture);
            SystemOneMaxStateChars = systemOne.MaxStateChars.ToString(CultureInfo.InvariantCulture);
            SystemOneRerankEnabled = systemOne.Rerank.Enabled;
            SystemOneConflictsEnabled = systemOne.Conflicts.Enabled;
            SystemOneRelationsEnabled = systemOne.Relations.Enabled;
            SystemOneRelationsMinProbability = systemOne.Relations.MinProbability.ToString(
                CultureInfo.InvariantCulture
            );
            SystemOneStopCheckEnabled = systemOne.Loop.Enabled && systemOne.Loop.StopCheck.Enabled;
            SystemOneDriftCheckEnabled =
                systemOne.Loop.Enabled && systemOne.Loop.DriftCheck.Enabled;
            SystemOneStabilityCheckEnabled =
                systemOne.Loop.Enabled && systemOne.Loop.StabilityCheck.Enabled;
        }

        if (_registry is not null)
        {
            var disabled = _registry.GetDisabledPlugins();
            foreach (var (pluginName, tools) in _registry.GetAllByPlugin())
            {
                Plugins.Add(new SkillRow(pluginName, tools.Count, !disabled.Contains(pluginName)));
            }
        }

        var contextManagement = _config?.ContextManagement;
        ContextModeDisplay = string.Equals(
            contextManagement?.Mode?.Trim(),
            "continuation",
            StringComparison.OrdinalIgnoreCase
        )
            ? ContextModeOptions[1]
            : ContextModeOptions[0];
        ContextVectorRetrieval = contextManagement?.VectorRetrieval ?? false;
        ContextModeStatus = IsContextContinuationAvailable
            ? "接续工具已加载；切换立即生效（工具与检索索引就地重载）。"
            : "接续插件未加载：当前只能使用压缩模式。";
        this.RaisePropertyChanged(nameof(IsContextContinuationAvailable));

        VectorDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mythology",
            "vectors.db"
        );
        SaveStatus = "";

        // Cloud 账号状态
        LoadCloudSession();

        return this;
    }

    /// <summary>保存配置并应用到 Agent。</summary>
    private async Task SaveAsync()
    {
        if (_configStore is null || _config is null)
            return;

        try
        {
            var providers = _config.Providers;
            var newKey = ResolveProviderConfigKey(ProviderDisplay, ApiKeys);
            providers.Default = newKey;
            providers.DefaultModel = Model;
            providers.ThinkingLevel = ThinkingLevel;
            providers.ContextWindowSize = ParseWindow(ContextWindow);

            // 清理空行，保留有效密钥
            providers.ApiKeys.Clear();
            foreach (var row in ApiKeys)
            {
                if (string.IsNullOrWhiteSpace(row.ApiKey) && string.IsNullOrWhiteSpace(row.BaseUrl))
                    continue;
                providers.ApiKeys.Add(
                    new ApiKeyEntry
                    {
                        Provider = row.Provider,
                        Name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim(),
                        ApiKey = row.ApiKey,
                        BaseUrl = row.BaseUrl.Length > 0 ? row.BaseUrl.Trim() : null,
                        Protocol = row.Protocol,
                    }
                );
            }

            // MCP
            _config.Mcp.Servers.Clear();
            foreach (var row in McpServers)
            {
                if (string.IsNullOrWhiteSpace(row.Name))
                    continue;
                _config.Mcp.Servers.Add(
                    new McpServerEntry
                    {
                        Name = row.Name,
                        Transport = row.Transport.Length > 0 ? row.Transport : "stdio",
                        Url = row.Url.Length > 0 ? row.Url : null,
                        Command = row.Command,
                        Args = row.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                        Enabled = row.Enabled,
                        Auth = row.Auth,
                        BearerToken = string.IsNullOrWhiteSpace(row.BearerToken)
                            ? null
                            : row.BearerToken.Trim(),
                        AuthHeader = string.IsNullOrWhiteSpace(row.AuthHeader)
                            ? null
                            : row.AuthHeader.Trim(),
                        AuthValue = string.IsNullOrWhiteSpace(row.AuthValue)
                            ? null
                            : row.AuthValue.Trim(),
                    }
                );
            }

            // 工具开关
            _config.Tools.Tools.Clear();
            foreach (var plugin in Plugins)
            {
                _config.Tools.Tools.Add(
                    new ToolEntry { Name = plugin.PluginName, Enabled = plugin.IsEnabled }
                );
            }

            // Embedding 配置
            _config.Embedding.Default = ResolveProviderConfigKey(
                EmbeddingProviderDisplay,
                EmbeddingApiKeys
            );
            _config.Embedding.DefaultModel = EmbeddingModel;
            _config.Embedding.Model = EmbeddingModel;
            _config.Embedding.ApiKeys.Clear();
            foreach (var row in EmbeddingApiKeys)
            {
                if (string.IsNullOrWhiteSpace(row.ApiKey) && string.IsNullOrWhiteSpace(row.BaseUrl))
                    continue;
                _config.Embedding.ApiKeys.Add(
                    new ApiKeyEntry
                    {
                        Provider = row.Provider,
                        Name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim(),
                        ApiKey = row.ApiKey,
                        BaseUrl = row.BaseUrl.Length > 0 ? row.BaseUrl.Trim() : null,
                        Protocol = row.Protocol,
                    }
                );
            }

            // 本地判定（System One）：回写（未编辑/非法数值保留配置现值）
            var systemOnePath =
                string.IsNullOrWhiteSpace(SystemOnePath) ? _config.SystemOne.Path
                : SystemOnePath.Trim().StartsWith('/') ? SystemOnePath.Trim()
                : "/" + SystemOnePath.Trim();
            // 循环判定：三开关全关 → 只关主开关（子开关原值保留）；任一打开 → 子开关按 UI 值写入
            var systemOneLoopOn =
                SystemOneStopCheckEnabled
                || SystemOneDriftCheckEnabled
                || SystemOneStabilityCheckEnabled;
            _config.SystemOne = _config.SystemOne with
            {
                Enabled = SystemOneEnabled,
                BaseUrl = string.IsNullOrWhiteSpace(SystemOneBaseUrl)
                    ? _config.SystemOne.BaseUrl
                    : SystemOneBaseUrl.Trim(),
                Path = systemOnePath,
                ApiKey = SystemOneApiKey.Trim(),
                Model = string.IsNullOrWhiteSpace(SystemOneModel)
                    ? _config.SystemOne.Model
                    : SystemOneModel.Trim(),
                TimeoutMs =
                    int.TryParse(SystemOneTimeoutMs, out var soTimeoutMs) && soTimeoutMs > 0
                        ? soTimeoutMs
                        : _config.SystemOne.TimeoutMs,
                MaxStateChars =
                    int.TryParse(SystemOneMaxStateChars, out var soMaxStateChars)
                    && soMaxStateChars > 0
                        ? soMaxStateChars
                        : _config.SystemOne.MaxStateChars,
                Rerank = _config.SystemOne.Rerank with { Enabled = SystemOneRerankEnabled },
                Conflicts = _config.SystemOne.Conflicts with
                {
                    Enabled = SystemOneConflictsEnabled,
                },
                Relations = _config.SystemOne.Relations with
                {
                    Enabled = SystemOneRelationsEnabled,
                    MinProbability = double.TryParse(
                        SystemOneRelationsMinProbability,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var soMinProbability
                    )
                        ? Math.Clamp(soMinProbability, 0, 1)
                        : _config.SystemOne.Relations.MinProbability,
                },
                Loop = systemOneLoopOn
                    ? (
                        _config.SystemOne.Loop with
                        {
                            Enabled = true,
                            StopCheck = _config.SystemOne.Loop.StopCheck with
                            {
                                Enabled = SystemOneStopCheckEnabled,
                            },
                            DriftCheck = _config.SystemOne.Loop.DriftCheck with
                            {
                                Enabled = SystemOneDriftCheckEnabled,
                            },
                            StabilityCheck = _config.SystemOne.Loop.StabilityCheck with
                            {
                                Enabled = SystemOneStabilityCheckEnabled,
                            },
                        }
                    )
                    : _config.SystemOne.Loop with
                    {
                        Enabled = false,
                    },
            };

            // 上下文管理模式：随配置一起落盘（先写盘，再热切换——任何一步失败都能重启回到已保存的模式）
            var contextConfig = new ContextManagementConfig
            {
                Mode = ContextModeDisplay == ContextModeOptions[1] ? "continuation" : "compact",
                ReminderPercent = _config.ContextManagement.ReminderPercent,
                VectorRetrieval = ContextVectorRetrieval,
            };
            _config.ContextManagement = contextConfig;

            await _configStore.SaveAsync(_config);

            // 热切换：重注册接续工具 + 重建检索索引（无需重启；未接线时仅落盘）
            if (Program.ContextModeSwitcher is { } switcher)
            {
                var (ok, message) = await switcher.SwitchAsync(contextConfig);
                ContextModeStatus = message;
                if (!ok)
                    SaveStatus = message;
            }

            // 立即应用到 Agent
            TornadoApi? reapplyApi = null;
            if (_agent is not null)
            {
                var (api, model, handler) = LlmApiFactory.CreateFromProviderConfig(providers);
                // ★ 冻结的 ConversationLoop 持有旧 TornadoApi/模型名（readonly），必须失效重建
                // （对齐 TuiApp.ReloadProvider；下次 Prompt 从 _messages 恢复上下文，无损）
                _agent.InvalidateConversationLoop();
                _agent.TornadoApi = api;
                _agent.SetHttpHandler(handler);
                _agent.SetModelName(model);
                _agent.SetThinkingLevel(ThinkingLevel);
                // 模型/上下文窗口/thinking 变更后重算稳定器阈值（GrowthRatioGuard/VSA/RGDecimator）
                Desktop.Services.AgentProfile.Reapply(_agent, _configStore);
                // 状态栏模型名即时刷新（单例；测试环境 Program.Services 为 null → 安全跳过）
                Program.Services?.GetService<MainWindowViewModel>()?.RefreshModelStatus();
                reapplyApi = api;
            }

            // 即时热替换 Embedding 服务（维度一致则即时生效，否则提示重启）
            TornadoApi? embeddingApi = null;
            if (_config.Embedding.ApiKeys.Count > 0)
            {
                try
                {
                    var (embApi, _, _) = LlmApiFactory.CreateFromEmbeddingConfig(_config.Embedding);
                    embeddingApi = embApi;
                }
                catch
                {
                    // 创建失败时回退到聊天 API
                }
            }
            if (_retrievalKernel is not null && !string.IsNullOrWhiteSpace(EmbeddingModel))
            {
                try
                {
                    var api = embeddingApi ?? reapplyApi;
                    if (api is not null)
                    {
                        var newEmb = new EmbeddingService(api, EmbeddingModel);
                        var result = _retrievalKernel.UpdateEmbeddingService(newEmb);
                        SaveStatus =
                            result == EmbeddingReplaceResult.SameDimension
                                ? "✓ 已保存并生效（embedding 即时切换）"
                                : "✓ 已保存；embedding 维度变化将在下次启动重建索引后生效";
                    }
                }
                catch (Exception embEx)
                {
                    SaveStatus = $"✓ 已保存；embedding 重配失败: {embEx.Message}";
                }
            }
            else
            {
                SaveStatus = "✓ 已保存并生效";
            }

            // 本地判定（System One）：热更新（Loop 停止期保存，无并发风险）
            if (_retrievalKernel?.SystemOne is { } systemOneClient)
            {
                systemOneClient.UpdateConfig(_config.SystemOne);
                SaveStatus = string.IsNullOrEmpty(SaveStatus)
                    ? "✓ 已保存（本地判定即时生效）"
                    : SaveStatus + "；本地判定即时生效";
            }

            // 应用到 ToolRegistry
            _registry?.SetDisabledPlugins(
                Plugins.Where(p => !p.IsEnabled).Select(p => p.PluginName)
            );

            SaveStatus = "✓ 已保存并生效";

            // 模型/窗口/thinking 变更 → 校准参数作废，下次发消息重新学习
            Program.Services?.GetRequiredService<ContextTokenCalibrator>()?.Reset();
        }
        catch (Exception ex)
        {
            SaveStatus = $"✗ 保存失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 重建提供商下拉选项：预设显示名 + 每张具名自定义卡片的「自定义: 名称」+ 兜底「自定义」。
    /// 与 TUI BuildProviderDisplayItems 一致；复用同一集合实例以保证 ComboBox 选中项稳定。
    /// </summary>
    private static void RebuildProviderOptions(
        ObservableCollection<string> options,
        IReadOnlyList<ApiKeyRow> rows
    )
    {
        options.Clear();
        foreach (var def in ProviderDefinition.All)
            options.Add(def.DisplayName);

        foreach (
            var name in rows.Where(r =>
                    ProviderDefinition.IsCustom(r.Provider) && !string.IsNullOrWhiteSpace(r.Name)
                )
                .Select(r => r.Name.Trim())
                .Distinct(StringComparer.Ordinal)
        )
            options.Add($"自定义: {name}");

        options.Add("自定义");
    }

    /// <summary>配置键 → 下拉显示名（自定义带名 → 「自定义: 名称」）。</summary>
    private static string ToProviderDisplay(ObservableCollection<string> options, string configKey)
    {
        var display = ProviderDefinition.IsCustom(configKey)
            ? ProviderDefinition.GetCustomName(configKey) is { Length: > 0 } name
                ? $"自定义: {name.Trim()}"
                : "自定义"
            : ProviderDefinition.FindByConfigKey(configKey).DisplayName;

        // 兜底：配置里的自定义名称已无对应卡片时仍要能显示
        if (!options.Contains(display))
            options.Add(display);
        return display;
    }

    /// <summary>卡片增删 / 改名后同步两个下拉，选中项消失时回退到首个自定义项。</summary>
    private void SyncProviderOptions()
    {
        var chatSelected = ProviderDisplay;
        var embeddingSelected = EmbeddingProviderDisplay;

        RebuildProviderOptions(ProviderOptions, ApiKeys);
        RebuildProviderOptions(EmbeddingProviderOptions, EmbeddingApiKeys);

        // 重建时 Clear() 会让 ComboBox 回写 null，故以回写后的实际值为准
        if (!string.IsNullOrEmpty(ProviderDisplay))
            chatSelected = ProviderDisplay;
        if (!string.IsNullOrEmpty(EmbeddingProviderDisplay))
            embeddingSelected = EmbeddingProviderDisplay;

        if (string.IsNullOrEmpty(chatSelected) || !ProviderOptions.Contains(chatSelected))
            ProviderDisplay = FallbackCustomDisplay(ProviderOptions);
        if (
            string.IsNullOrEmpty(embeddingSelected)
            || !EmbeddingProviderOptions.Contains(embeddingSelected)
        )
            EmbeddingProviderDisplay = FallbackCustomDisplay(EmbeddingProviderOptions);
    }

    private static string FallbackCustomDisplay(ObservableCollection<string> options) =>
        options.FirstOrDefault(o => o.StartsWith("自定义: ", StringComparison.Ordinal)) ?? "自定义";

    /// <summary>由配置条目创建密钥行（订阅变更以同步提供商下拉）。</summary>
    private ApiKeyRow CreateApiKeyRow(ApiKeyEntry entry)
    {
        var row = new ApiKeyRow(
            entry.Provider,
            entry.Name ?? "",
            entry.ApiKey ?? "",
            entry.BaseUrl ?? "",
            entry.Protocol
        );
        row.PropertyChanged += OnApiKeyRowPropertyChanged;
        return row;
    }

    private void OnApiKeyRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApiKeyRow.Name) or nameof(ApiKeyRow.Provider))
            SyncProviderOptions();
    }

    /// <summary>
    /// 提供商下拉显示名 → 持久化 ConfigKey（"自定义: 名称" → "custom:名称"）。
    /// LlmApiFactory 按 Provider=="custom" 且 Name 匹配查找密钥，裸 "custom" 只匹配未具名密钥，
    /// 写错会导致 API 静默为空，故具名自定义必须写回复合键。
    /// </summary>
    private static string ResolveProviderConfigKey(string display, IReadOnlyList<ApiKeyRow> rows)
    {
        if (display.StartsWith("自定义: ", StringComparison.Ordinal))
            return "custom:" + display["自定义: ".Length..].Trim();

        if (display.StartsWith("自定义", StringComparison.Ordinal))
        {
            // 泛化「自定义」：未具名卡片优先，其次首张具名卡片
            if (
                rows.Any(r =>
                    ProviderDefinition.IsCustom(r.Provider) && string.IsNullOrWhiteSpace(r.Name)
                )
            )
                return "custom";

            var named = rows.FirstOrDefault(r =>
                ProviderDefinition.IsCustom(r.Provider) && !string.IsNullOrWhiteSpace(r.Name)
            );
            return named is null ? "custom" : $"custom:{named.Name.Trim()}";
        }

        return ProviderDefinition.FindByDisplayName(display).ConfigKey;
    }

    private static int ParseWindow(string label) =>
        label switch
        {
            "8K" => 8_192,
            "16K" => 16_384,
            "32K" => 32_768,
            "64K" => 65_536,
            "128K" => 131_072,
            "200K" => 200_000,
            "256K" => 262_144,
            "512K" => 524_288,
            "1M" => 1_048_576,
            _ => 100_000,
        };

    // ── Cloud 账号方法（状态源：CloudAuthService 单例） ──

    /// <summary>仅刷新云端账号状态（登录窗关闭后调用，不重载整个设置表单）。</summary>
    public void RefreshCloudSession()
    {
        LoadCloudSession();
        CloudStatus = "";
    }

    /// <summary>从 CloudAuthService 读取当前状态（设置窗口打开时快照）。</summary>
    private void LoadCloudSession()
    {
        var session = CloudAuth?.Session;
        var signedIn = CloudAuth?.IsSignedIn ?? false;
        IsCloudLoggedIn = signedIn;
        if (signedIn && session is not null)
        {
            CloudDisplayId = session.DisplayName is { Length: > 0 } dn ? dn : session.PublicId;
            CloudPlan = session.Plan.Length > 0 ? session.Plan : "free";
            CloudSavedAt = session.SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        else
        {
            CloudDisplayId = "";
            CloudPlan = "";
            CloudSavedAt = "";
        }
        CloudStatus = "";
    }

    /// <summary>退出登录。</summary>
    public void Logout()
    {
        CloudAuth?.Logout();
        LoadCloudSession();
        CloudStatus = "已退出登录";
    }
}

/// <summary>两态卡片行（只读摘要 ↔ 编辑表单），设置页密钥卡 / MCP 卡共用。</summary>
public interface IEditToggleRow
{
    /// <summary>是否处于编辑态。</summary>
    bool IsEditing { get; set; }
}

/// <summary>API Key 行（可编辑）。自定义提供商显示为「自定义: 名称」（对齐 TUI）。</summary>
public sealed class ApiKeyRow(
    string provider,
    string name,
    string apiKey,
    string baseUrl,
    string protocol = "openai-completion"
) : ReactiveObject, IEditToggleRow
{
    /// <summary>卡片提供商下拉选项（预设显示名 + 末位自定义项，标签随名称变化）。</summary>
    public ObservableCollection<string> ProviderOptions { get; } =
    [.. ProviderDefinition.All.Select(d => d.DisplayName), CustomOption(provider, name)];

    /// <summary>线路协议选项（决定请求-响应格式）。</summary>
    public static readonly string[] ProtocolOptions =
    [
        "openai-completion",
        "openai-responses",
        "anthropic-message",
    ];

    public string Provider
    {
        get;
        set
        {
            if (value == field)
                return;
            this.RaiseAndSetIfChanged(ref field, value);
            SyncCustomOption();
        }
    } = provider;

    /// <summary>是否自定义提供商（决定名称 / 地址字段可见性）。</summary>
    public bool IsCustom => ProviderDefinition.IsCustom(Provider);

    /// <summary>下拉显示名：自定义显示「自定义: 名称」，预设显示定义名。</summary>
    public string ProviderDisplay
    {
        get =>
            ProviderDefinition.IsCustom(Provider)
                ? CustomOption(Provider, Name)
                : ProviderDefinition.FindByConfigKey(Provider).DisplayName;
        set
        {
            // ComboBox 换项时可能回写 null/空值
            if (string.IsNullOrEmpty(value))
                return;
            var configKey = value.StartsWith("自定义", StringComparison.Ordinal)
                ? "custom"
                : ProviderDefinition.FindByDisplayName(value).ConfigKey;
            Provider = configKey;
        }
    }

    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(NameLine));
            SyncCustomOption();
        }
    } = name;

    public string ApiKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(MaskedKey));
        }
    } = apiKey;

    public string BaseUrl
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(BaseUrlLine));
        }
    } = baseUrl;

    /// <summary>卡片是否处于编辑态（默认只读展示，右侧「修改」进入）。</summary>
    public bool IsEditing
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>展示态的脱敏密钥（保留末 4 位便于辨认）。</summary>
    public string MaskedKey =>
        string.IsNullOrWhiteSpace(ApiKey) ? "未填写密钥"
        : ApiKey.Length <= 4 ? new string('•', ApiKey.Length)
        : $"••••••••{ApiKey[^4..]}";

    /// <summary>展示态的名称行（仅自定义提供商显示）。</summary>
    public string NameLine => $"名称  {Name}";

    /// <summary>展示态的地址行（仅自定义提供商显示）。</summary>
    public string BaseUrlLine => $"地址  {BaseUrl}";

    /// <summary>展示态的协议行。</summary>
    public string ProtocolLine => $"协议  {Protocol}";

    /// <summary>线路协议（openai-completion / openai-responses / anthropic-message）。</summary>
    public string Protocol
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ProtocolLine));
        }
    } = protocol;

    private static string CustomOption(string provider, string? name) =>
        ProviderDefinition.IsCustom(provider) && !string.IsNullOrWhiteSpace(name)
            ? $"自定义: {name.Trim()}"
            : "自定义";

    /// <summary>末位自定义项标签跟随提供商 / 名称，保证下拉选中项与绑定值一致。</summary>
    private void SyncCustomOption()
    {
        var label = CustomOption(Provider, Name);
        if (ProviderOptions.Count > 0 && ProviderOptions[^1] != label)
            ProviderOptions[^1] = label;
        this.RaisePropertyChanged(nameof(IsCustom));
        this.RaisePropertyChanged(nameof(ProviderDisplay));
    }
}

/// <summary>MCP 服务器行（两态：只读摘要 / 编辑表单）。</summary>
public sealed class McpServerRow(
    string name,
    string transport,
    string url,
    string command,
    string args,
    bool enabled,
    string auth = "none",
    string? bearerToken = null,
    string? authHeader = null,
    string? authValue = null
) : ReactiveObject, IEditToggleRow
{
    /// <summary>传输方式选项（x:Static 绑定用）。</summary>
    public static readonly string[] TransportOptions = ["stdio", "streamable-http", "sse"];

    /// <summary>鉴权方式选项（x:Static 绑定用）。</summary>
    public static readonly string[] AuthOptions = ["none", "bearer", "header"];

    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(TitleLine));
        }
    } = name;

    public string Transport
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(TransportLine));
            this.RaisePropertyChanged(nameof(EndpointLine));
            this.RaisePropertyChanged(nameof(IsStdio));
        }
    } = transport;

    public string Url
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(EndpointLine));
        }
    } = url;

    public string Command
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(EndpointLine));
        }
    } = command;

    public string Args
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(EndpointLine));
        }
    } = args;

    public bool Enabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = enabled;

    /// <summary>卡片是否处于编辑态（默认只读摘要，右侧「修改」进入）。</summary>
    public bool IsEditing
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>展示态标题（空名称给占位，避免出现无标题卡片）。</summary>
    public string TitleLine => string.IsNullOrWhiteSpace(Name) ? "未命名服务器" : Name;

    /// <summary>展示态传输方式（mono 元数据）。</summary>
    public string TransportLine => $"传输  {Transport}";

    /// <summary>展示态端点：stdio 显示命令 + 参数，其余显示 URL。</summary>
    public string EndpointLine =>
        IsStdio
            ? string.IsNullOrWhiteSpace(Command)
                ? "命令  未填写"
                : $"命令  {Command} {Args}".TrimEnd()
            : string.IsNullOrWhiteSpace(Url)
                ? "地址  未填写"
                : $"地址  {Url}";

    public bool IsStdio => Transport.Equals("stdio", StringComparison.OrdinalIgnoreCase);

    /// <summary>鉴权方式：none / bearer / header（仅 HTTP 传输使用）。</summary>
    public string Auth
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsBearerAuth));
            this.RaisePropertyChanged(nameof(IsHeaderAuth));
            this.RaisePropertyChanged(nameof(AuthLine));
        }
    } = auth;

    /// <summary>Bearer 令牌（Auth=bearer）。</summary>
    public string BearerToken
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(AuthLine));
        }
    } = bearerToken ?? "";

    /// <summary>自定义鉴权头名（Auth=header）。</summary>
    public string AuthHeader
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(AuthLine));
        }
    } = authHeader ?? "";

    /// <summary>自定义鉴权头的值（Auth=header）。</summary>
    public string AuthValue
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(AuthLine));
        }
    } = authValue ?? "";

    public bool IsBearerAuth => Auth.Equals("bearer", StringComparison.OrdinalIgnoreCase);

    public bool IsHeaderAuth => Auth.Equals("header", StringComparison.OrdinalIgnoreCase);

    /// <summary>展示态鉴权行（mono 元数据；none 时给中文占位）。</summary>
    public string AuthLine =>
        Auth.ToLowerInvariant() switch
        {
            "bearer" => string.IsNullOrWhiteSpace(BearerToken)
                ? "鉴权  bearer · 未填写令牌"
                : "鉴权  bearer · 已配置令牌",
            "header" => string.IsNullOrWhiteSpace(AuthHeader)
                ? "鉴权  自定义头 · 未填写"
                : $"鉴权  {AuthHeader} · 已配置",
            _ => "鉴权  无",
        };
}

/// <summary>插件行（工具 Tab）。</summary>
public sealed class SkillRow(string pluginName, int toolCount, bool isEnabled) : ReactiveObject
{
    public string PluginName { get; } = pluginName;
    public int ToolCount { get; } = toolCount;

    public bool IsEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = isEnabled;
}
