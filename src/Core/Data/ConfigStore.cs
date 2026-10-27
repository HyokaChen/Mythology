using System.Globalization;
using System.Runtime.InteropServices;

namespace Core.Data;

/// <summary>
/// 全局配置存储，读写 ~/.mythology/config.yaml。
/// 替代 DB 中的 AppSetting KV 表。
/// 优先级：config.yaml > appsettings.json > 环境变量
/// </summary>
public sealed class ConfigStore
{
    /// <summary>配置文件完整路径。</summary>
    private readonly string _configPath;

    /// <summary>
    /// 构造函数，初始化配置存储。
    /// </summary>
    /// <param name="mythologyDir">可选，自定义配置目录路径，默认为 ~/.mythology。</param>
    public ConfigStore(string? mythologyDir = null)
    {
        var dir =
            mythologyDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mythology"
            );
        _configPath = Path.Combine(dir, "config.yaml");
        Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// 加载完整配置。
    /// </summary>
    /// <returns>配置数据对象，若文件不存在或读取失败则返回默认配置。</returns>
    public ConfigData Load()
    {
        if (!File.Exists(_configPath))
            return new ConfigData();

        try
        {
            var text = File.ReadAllText(_configPath);
            var dict = YamlHelper.Deserialize(text);
            var config = ConfigData.FromDict(dict);
            return config;
        }
        catch
        {
            return new ConfigData();
        }
    }

    /// <summary>
    /// 保存完整配置，并设置文件权限为仅当前用户可读写（保护 API Key 等敏感信息）。
    /// </summary>
    /// <param name="config">要保存的配置数据。</param>
    public async Task SaveAsync(ConfigData config)
    {
        var dict = config.ToDict();
        var yaml = YamlHelper.Serialize(dict);
        await AtomicWriteAsync(_configPath, yaml);
        EnsureFilePermissions(_configPath);
    }

    /// <summary>
    /// 原子写入文件。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">要写入的内容。</param>
    private static Task AtomicWriteAsync(string path, string content)
    {
        return Shared.AtomicFile.WriteAllTextAsync(path, content);
    }

    /// <summary>
    /// 确保 config.yaml 文件权限为仅当前用户可读写（600），防止 API Key 泄露。
    /// 在 macOS/Linux 上使用 chmod 600，在 Windows 上设置 ACL。
    /// </summary>
    private static void EnsureFilePermissions(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // macOS / Linux: chmod 600 (owner read/write only)
                var chmod = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"600 \"{path}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    }
                );
                chmod?.WaitForExit(1000); // 1s timeout
            }
            // Windows: .NET 文件 ACL 默认继承父目录权限，
            // 对于用户级 ~/.mythology 目录通常已受限，暂不额外处理。
        }
        catch
        {
            // chmod 失败不阻塞主流程——文件已写入，只是权限未收紧
        }
    }
}

/// <summary>
/// 全局配置数据模型。
/// </summary>
public sealed class ConfigData
{
    /// <summary>LLM 提供商配置。</summary>
    public ProviderConfig Providers { get; set; } = new();

    /// <summary>MCP 服务器配置。</summary>
    public McpConfig Mcp { get; set; } = new();

    /// <summary>工具配置。</summary>
    public ToolConfig Tools { get; set; } = new();

    /// <summary>主题标识（ThemeRegistry.Id，默认 desert）。</summary>
    public string ThemeName { get; set; } = "desert";

    /// <summary>Embedding 模型配置。</summary>
    public EmbeddingConfig Embedding { get; set; } = new();

    /// <summary>本地判定模型（System One / d1-omni）配置；整块可选，默认关闭。</summary>
    public SystemOneConfig SystemOne { get; set; } = new();

    /// <summary>上下文管理策略（compact | continuation）与接续参数。</summary>
    public ContextManagementConfig ContextManagement { get; set; } = new();

    /// <summary>界面状态（桌面端设定面板列宽等；TUI 不消费）。</summary>
    public UiConfig Ui { get; set; } = new();

    /// <summary>
    /// 从字典加载配置。
    /// </summary>
    /// <param name="dict">配置字典。</param>
    /// <returns>配置数据对象。</returns>
    public static ConfigData FromDict(Dictionary<string, object?> dict)
    {
        var config = new ConfigData();

        if (
            dict.TryGetValue("providers", out var providersVal)
            && providersVal is Dictionary<string, object?> providersDict
        )
        {
            config.Providers = ProviderConfig.FromDict(providersDict);
        }

        if (
            dict.TryGetValue("mcp", out var mcpVal) && mcpVal is Dictionary<string, object?> mcpDict
        )
        {
            config.Mcp = McpConfig.FromDict(mcpDict);
        }

        if (dict.TryGetValue("tools", out var toolsVal) && toolsVal is List<object?> toolsList)
        {
            config.Tools = ToolConfig.FromList(toolsList);
        }

        if (dict.TryGetValue("theme", out var themeVal) && themeVal is string themeName)
        {
            config.ThemeName = themeName;
        }

        if (
            dict.TryGetValue("embedding", out var embVal)
            && embVal is Dictionary<string, object?> embDict
        )
        {
            config.Embedding = EmbeddingConfig.FromDict(embDict);
        }

        if (
            dict.TryGetValue("system-one", out var soVal)
            && soVal is Dictionary<string, object?> soDict
        )
        {
            config.SystemOne = SystemOneConfig.FromDict(soDict);
        }

        if (
            dict.TryGetValue("context-management", out var cmVal)
            && cmVal is Dictionary<string, object?> cmDict
        )
        {
            config.ContextManagement = ContextManagementConfig.FromDict(cmDict);
        }

        if (dict.TryGetValue("ui", out var uiVal) && uiVal is Dictionary<string, object?> uiDict)
        {
            config.Ui = UiConfig.FromDict(uiDict);
        }

        return config;
    }

    /// <summary>
    /// 转换为字典。
    /// </summary>
    /// <returns>配置字典。</returns>
    public Dictionary<string, object?> ToDict()
    {
        return new Dictionary<string, object?>
        {
            ["providers"] = Providers.ToDict(),
            ["mcp"] = Mcp.ToDict(),
            ["tools"] = Tools.ToList(),
            ["theme"] = ThemeName,
            ["embedding"] = Embedding.ToDict(),
            ["system-one"] = SystemOne.ToDict(),
            ["context-management"] = ContextManagement.ToDict(),
            ["ui"] = Ui.ToDict(),
        };
    }
}

/// <summary>提供商配置。</summary>
public sealed class ProviderConfig
{
    /// <summary>当前选中 provider 的 ConfigKey。
    /// 预设 provider 如 "openai"；自定义 provider 如 "custom:商汤"。</summary>
    public string Default { get; set; } = "custom";

    /// <summary>默认模型名称。</summary>
    public string DefaultModel { get; set; } = "deepseek-v4-flash";

    /// <summary>思考深度级别：low / medium / high。</summary>
    public string ThinkingLevel { get; set; } = "medium";

    /// <summary>上下文窗口大小（tokens），默认 100K。与 ProviderTab 下拉选项 "100K" 对齐。</summary>
    public int ContextWindowSize { get; set; } = 100_000;

    /// <summary>API 密钥列表。</summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];

    /// <summary>
    /// 从字典加载提供商配置。
    /// </summary>
    /// <param name="dict">配置字典。</param>
    /// <returns>提供商配置对象。</returns>
    public static ProviderConfig FromDict(Dictionary<string, object?> dict)
    {
        var config = new ProviderConfig();

        if (dict.TryGetValue("default", out var defaultVal))
            config.Default = defaultVal?.ToString() ?? "custom";
        // default_name 已被 Migration_013 迁移为复合键格式，不再读取此字段
        if (dict.TryGetValue("default-model", out var modelVal))
            config.DefaultModel = modelVal?.ToString() ?? "step-3.7-flash";
        if (dict.TryGetValue("thinking-level", out var thinkingVal))
            config.ThinkingLevel = thinkingVal?.ToString() ?? "medium";

        if (dict.TryGetValue("context-window-size", out var ctxVal) && ctxVal is not null)
            config.ContextWindowSize = Convert.ToInt32(ctxVal);
        if (dict.TryGetValue("api-keys", out var keysVal) && keysVal is List<object?> keysList)
        {
            config.ApiKeys = keysList
                .Select(k =>
                {
                    if (k is Dictionary<string, object?> keyDict)
                    {
                        return new ApiKeyEntry
                        {
                            Provider = keyDict.TryGetValue("provider", out var p)
                                ? p?.ToString() ?? ""
                                : "",
                            Name = keyDict.TryGetValue("name", out var n) ? n?.ToString() : null,
                            ApiKey = keyDict.TryGetValue("api-key", out var ak)
                                ? ak?.ToString()
                                : null,
                            BaseUrl = keyDict.TryGetValue("base-url", out var bu)
                                ? bu?.ToString()
                                : null,
                            Protocol = keyDict.TryGetValue("protocol", out var pr)
                                ? pr?.ToString() ?? "openai-completion"
                                : "openai-completion",
                        };
                    }
                    return new ApiKeyEntry();
                })
                .Where(k => !string.IsNullOrEmpty(k.Provider))
                .ToList();
        }

        return config;
    }

    /// <summary>
    /// 转换为字典。
    /// </summary>
    /// <returns>配置字典。</returns>
    public Dictionary<string, object?> ToDict()
    {
        var dict = new Dictionary<string, object?>
        {
            ["default"] = Default,
            ["default-model"] = DefaultModel,
            ["thinking-level"] = ThinkingLevel,
            ["context-window-size"] = ContextWindowSize,
        };

        // DefaultName 不再单独写入；自定义 provider 使用 "custom:名称" 复合键写入 Default

        if (ApiKeys.Count > 0)
        {
            dict["api-keys"] = ApiKeys
                .Select(k =>
                {
                    var keyDict = new Dictionary<string, object?> { ["provider"] = k.Provider };
                    if (k.Name is not null)
                        keyDict["name"] = k.Name;
                    if (k.ApiKey is not null)
                        keyDict["api-key"] = k.ApiKey;
                    if (k.BaseUrl is not null)
                        keyDict["base-url"] = k.BaseUrl;
                    keyDict["protocol"] = k.Protocol;
                    return keyDict;
                })
                .ToList<object?>();
        }

        return dict;
    }
}

/// <summary>
/// 上下文管理策略与接续参数（YAML 段 context-management）。
/// </summary>
/// <summary>
/// 界面状态（桌面端专用；TUI 不消费）。写入 ~/.mythology/config.yaml 的 <c>ui</c> 节。
/// </summary>
public sealed class UiConfig
{
    /// <summary>设定面板列宽（px；280–720，默认 320）。</summary>
    public double OutlinePanelWidth { get; set; } = DefaultOutlinePanelWidth;

    /// <summary>默认列宽（与 MainWindow.axaml 的初始列宽一致）。</summary>
    public const double DefaultOutlinePanelWidth = 320;

    /// <summary>列宽下限（再窄会把五级层级挤成两行折行）。</summary>
    public const double MinOutlinePanelWidth = 280;

    /// <summary>列宽上限（再宽会吃掉对话区）。</summary>
    public const double MaxOutlinePanelWidth = 720;

    /// <summary>归一列宽到合法区间（非法/缺省 → 默认值）。</summary>
    public static double ClampWidth(double width) =>
        double.IsFinite(width) && width > 0
            ? Math.Clamp(width, MinOutlinePanelWidth, MaxOutlinePanelWidth)
            : DefaultOutlinePanelWidth;

    /// <summary>从字典加载（缺省即默认值，旧 config.yaml 不需迁移）。</summary>
    public static UiConfig FromDict(Dictionary<string, object?> dict)
    {
        var config = new UiConfig();
        if (
            dict.TryGetValue("outline-panel-width", out var widthVal)
            && widthVal is not null
            && double.TryParse(
                Convert.ToString(widthVal, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var width
            )
        )
            config.OutlinePanelWidth = ClampWidth(width);
        return config;
    }

    /// <summary>转换为字典。</summary>
    public Dictionary<string, object?> ToDict() =>
        new() { ["outline-panel-width"] = OutlinePanelWidth };
}

public sealed class ContextManagementConfig
{
    /// <summary>
    /// 策略开关："compact"（默认，分片压缩，行为与改动前完全一致）
    /// 或 "continuation"（接续：换页 + 历史检索 + 跨页笔记）。
    /// 未知值归一为 "compact"（拼错不会静默切换到接续）。
    /// </summary>
    public string Mode { get; set; } = "compact";

    /// <summary>
    /// 建议换页的 usage 百分比覆盖（0 = 用模型 profile 推导值；仅 continuation 生效）。
    /// 用比例而不是绝对 token：同一份配置要能同时服务 8K 与 1M 窗口的模型。
    /// </summary>
    public int ReminderPercent { get; set; }

    /// <summary>是否启用向量语义检索通道（history_search_contents(semantic:true)）。</summary>
    public bool VectorRetrieval { get; set; }

    /// <summary>从字典加载（缺省即默认值，旧 config.yaml 不需迁移）。</summary>
    public static ContextManagementConfig FromDict(Dictionary<string, object?> dict)
    {
        var config = new ContextManagementConfig();
        if (dict.TryGetValue("mode", out var modeVal) && modeVal is not null)
            config.Mode = modeVal.ToString() ?? "compact";
        if (dict.TryGetValue("reminder-percent", out var rpVal) && rpVal is not null)
            config.ReminderPercent = Convert.ToInt32(rpVal);
        if (dict.TryGetValue("vector-retrieval", out var vrVal) && vrVal is not null)
            config.VectorRetrieval = Convert.ToBoolean(vrVal);
        return config;
    }

    /// <summary>转换为字典。</summary>
    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["mode"] = Mode,
            ["reminder-percent"] = ReminderPercent,
            ["vector-retrieval"] = VectorRetrieval,
        };

    /// <summary>
    /// 归一为运行期选项：将 mode 字符串映射为枚举（未知值 → compact）。
    /// </summary>
    public Core.Agent.ContextManagementOptions ToOptions()
    {
        var normalized = Mode?.Trim().ToLowerInvariant();
        var mode =
            normalized == "continuation"
                ? Core.Agent.ContextMode.Continuation
                : Core.Agent.ContextMode.Compact;
        return new Core.Agent.ContextManagementOptions(
            mode,
            Math.Clamp(ReminderPercent, 0, 99),
            VectorRetrieval
        );
    }
}

/// <summary>API 密钥条目。</summary>
public sealed class ApiKeyEntry
{
    /// <summary>存储 ConfigKey，如 "openai"、"custom"。</summary>
    public string Provider { get; set; } = "";

    /// <summary>自定义连接器名称，如 "我的智谱"（仅自定义时使用）。</summary>
    public string? Name { get; set; }

    /// <summary>API 密钥。</summary>
    public string? ApiKey { get; set; }

    /// <summary>基础 URL（仅自定义 provider 时使用）。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// 线路协议：openai-completion / openai-responses / anthropic-message。
    /// 决定该密钥用哪套请求-响应格式（自定义端点常见三种）。
    /// </summary>
    public string Protocol { get; set; } = "openai-completion";
}

/// <summary>MCP 配置。</summary>
public sealed class McpConfig
{
    /// <summary>MCP 服务器列表。</summary>
    public List<McpServerEntry> Servers { get; set; } = [];

    /// <summary>
    /// 从字典加载 MCP 配置。
    /// </summary>
    /// <param name="dict">配置字典。</param>
    /// <returns>MCP 配置对象。</returns>
    public static McpConfig FromDict(Dictionary<string, object?> dict)
    {
        var config = new McpConfig();

        if (
            dict.TryGetValue("servers", out var serversVal)
            && serversVal is List<object?> serversList
        )
        {
            config.Servers = serversList
                .Select(s =>
                {
                    if (s is Dictionary<string, object?> serverDict)
                    {
                        return new McpServerEntry
                        {
                            Name = serverDict.TryGetValue("name", out var n)
                                ? n?.ToString() ?? ""
                                : "",
                            Transport = serverDict.TryGetValue("transport", out var tr)
                                ? tr?.ToString() ?? "stdio"
                                : "stdio",
                            Url = serverDict.TryGetValue("url", out var u) ? u?.ToString() : null,
                            Command = serverDict.TryGetValue("command", out var c)
                                ? c?.ToString() ?? ""
                                : "",
                            Args =
                                serverDict.TryGetValue("args", out var a)
                                && a is List<object?> argsList
                                    ? argsList.Select(arg => arg?.ToString() ?? "").ToList()
                                    : [],
                            Enabled =
                                serverDict.TryGetValue("enabled", out var e)
                                && bool.TryParse(e?.ToString(), out var enabled)
                                && enabled,
                            Auth = serverDict.TryGetValue("auth", out var au)
                                ? au?.ToString() ?? "none"
                                : "none",
                            BearerToken = serverDict.TryGetValue("bearer-token", out var bt)
                                ? bt?.ToString()
                                : null,
                            AuthHeader = serverDict.TryGetValue("auth-header", out var ah)
                                ? ah?.ToString()
                                : null,
                            AuthValue = serverDict.TryGetValue("auth-value", out var av)
                                ? av?.ToString()
                                : null,
                        };
                    }
                    return new McpServerEntry();
                })
                .Where(s => !string.IsNullOrEmpty(s.Name))
                .ToList();
        }

        return config;
    }

    /// <summary>
    /// 转换为字典。
    /// </summary>
    /// <returns>配置字典。</returns>
    public Dictionary<string, object?> ToDict()
    {
        return new Dictionary<string, object?>
        {
            ["servers"] = Servers
                .Select(s => new Dictionary<string, object?>
                {
                    ["name"] = s.Name,
                    ["transport"] = s.Transport,
                    ["url"] = s.Url,
                    ["command"] = s.Command,
                    ["args"] = s.Args,
                    ["enabled"] = s.Enabled,
                    ["auth"] = s.Auth,
                    ["bearer-token"] = s.BearerToken,
                    ["auth-header"] = s.AuthHeader,
                    ["auth-value"] = s.AuthValue,
                })
                .ToList<object?>(),
        };
    }
}

/// <summary>MCP 服务器条目。</summary>
public sealed class McpServerEntry
{
    /// <summary>服务器名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>传输类型：stdio（本地进程）或 http（远程端点）。</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>远程 MCP 端点 URL（Transport=http 时使用）。</summary>
    public string? Url { get; set; }

    /// <summary>启动命令（Transport=stdio 时使用）。</summary>
    public string Command { get; set; } = "";

    /// <summary>命令参数列表。</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 鉴权方式：none（无鉴权）/ bearer（Authorization: Bearer &lt;BearerToken&gt;）/ header（自定义头）。
    /// </summary>
    public string Auth { get; set; } = "none";

    /// <summary>Bearer 令牌（Auth=bearer 时使用）。</summary>
    public string? BearerToken { get; set; }

    /// <summary>自定义鉴权头名（Auth=header 时使用，如 X-API-Key）。</summary>
    public string? AuthHeader { get; set; }

    /// <summary>自定义鉴权头的值（Auth=header 时使用）。</summary>
    public string? AuthValue { get; set; }
}

/// <summary>工具配置。</summary>
public sealed class ToolConfig
{
    /// <summary>工具条目列表。</summary>
    public List<ToolEntry> Tools { get; set; } = [];

    /// <summary>
    /// 从列表加载工具配置。
    /// </summary>
    /// <param name="list">工具配置列表。</param>
    /// <returns>工具配置对象。</returns>
    public static ToolConfig FromList(List<object?> list)
    {
        var config = new ToolConfig
        {
            Tools = list.Select(t =>
                {
                    if (t is Dictionary<string, object?> toolDict)
                    {
                        return new ToolEntry
                        {
                            Name = toolDict.TryGetValue("name", out var n)
                                ? n?.ToString() ?? ""
                                : "",
                            Enabled =
                                !toolDict.TryGetValue("enabled", out var e)
                                || !bool.TryParse(e?.ToString(), out var enabled)
                                || enabled,
                            RateLimit =
                                toolDict.TryGetValue("rate-limit", out var rl)
                                && int.TryParse(rl?.ToString(), out var rate)
                                    ? rate
                                    : null,
                        };
                    }
                    return new ToolEntry();
                })
                .Where(t => !string.IsNullOrEmpty(t.Name))
                .ToList(),
        };

        return config;
    }

    /// <summary>
    /// 转换为列表。
    /// </summary>
    /// <returns>工具配置列表。</returns>
    public List<object?> ToList()
    {
        return Tools
            .Select(t =>
            {
                var dict = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["enabled"] = t.Enabled,
                };
                if (t.RateLimit is not null)
                    dict["rate-limit"] = t.RateLimit;
                return dict;
            })
            .ToList<object?>();
    }
}

/// <summary>工具条目。</summary>
public sealed class ToolEntry
{
    /// <summary>工具名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>速率限制（可选）。</summary>
    public int? RateLimit { get; set; }
}

/// <summary>Embedding 模型配置。</summary>
public sealed class EmbeddingConfig
{
    /// <summary>
    /// Embedding 模型名称（兼容旧配置：仅存在 "model" 字段的 config.yaml）。
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// 嵌入提供方（显式 opt-in）："cloud" = 云端 /v1/embedding（需登录；默认 0 计费，维度须与
    /// 本地向量库一致）；空/其他 = 本地（LlmTornado，按 Model/ApiKeys 选择）。
    /// TUI/Desktop 设置面板不编辑此字段，保存时原样保留。
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>选中的提供商 ConfigKey（预设如 "openai"，自定义如 "custom:名称"）。</summary>
    public string Default { get; set; } = "openai";

    /// <summary>默认 embedding 模型名称。</summary>
    public string DefaultModel { get; set; } = "text-embedding-3-small";

    /// <summary>Embedding 专用的 API 密钥列表（独立于聊天连接器）。</summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];

    /// <summary>
    /// 回退到聊天连接器的 API（无独立 embedding 密钥时使用）。
    /// 仅用于 UI 判断是否展示"共享连接器密钥"提示，不参与序列化。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [YamlDotNet.Serialization.YamlIgnore]
    public bool FallbackToChat { get; set; } = true;

    public Dictionary<string, object?> ToDict()
    {
        var cloud = string.Equals(Provider, "cloud", StringComparison.OrdinalIgnoreCase);
        if (!cloud)
        {
            switch (ApiKeys.Count)
            {
                // 旧格式回退：只有 Model 且无 ApiKeys → 写回单字段，保持旧 config.yaml 可读
                case 0 when string.IsNullOrEmpty(Model):
                    return new Dictionary<string, object?> { ["model"] = "" };
                case 0
                    when !string.IsNullOrEmpty(Model)
                        && Default == "openai"
                        && DefaultModel == Model:
                    return new Dictionary<string, object?> { ["model"] = Model };
            }
        }

        var dict = new Dictionary<string, object?>
        {
            ["default"] = Default,
            ["default-model"] = DefaultModel,
        };
        if (cloud)
        {
            dict["provider"] = "cloud";
        }
        if (ApiKeys.Count > 0)
        {
            dict["api-keys"] = ApiKeys
                .Select(k =>
                {
                    var d = new Dictionary<string, object?> { ["provider"] = k.Provider };
                    if (k.Name is not null)
                        d["name"] = k.Name;
                    if (k.ApiKey is not null)
                        d["api-key"] = k.ApiKey;
                    if (k.BaseUrl is not null)
                        d["base-url"] = k.BaseUrl;
                    d["protocol"] = k.Protocol;
                    return d;
                })
                .ToList<object?>();
        }
        return dict;
    }

    public static EmbeddingConfig FromDict(Dictionary<string, object?> dict)
    {
        var config = new EmbeddingConfig();

        if (dict.TryGetValue("provider", out var providerValue))
        {
            config.Provider = providerValue?.ToString() ?? "";
        }

        // 旧格式：仅有 model 字段
        if (dict.TryGetValue("model", out var model) && model is string modelStr)
        {
            config.Model = modelStr;
            if (!string.IsNullOrEmpty(modelStr))
            {
                config.Default = "openai";
                config.DefaultModel = modelStr;
            }
            return config;
        }

        // 新格式：完整 provider-like 结构
        config.Default = dict.TryGetValue("default", out var def)
            ? def?.ToString() ?? "openai"
            : "openai";
        config.DefaultModel = dict.TryGetValue("default-model", out var dm)
            ? dm?.ToString() ?? "text-embedding-3-small"
            : "text-embedding-3-small";

        if (dict.TryGetValue("api-keys", out var keysVal) && keysVal is List<object?> keysList)
        {
            config.ApiKeys = keysList
                .Select(k =>
                {
                    if (k is Dictionary<string, object?> keyDict)
                    {
                        return new ApiKeyEntry
                        {
                            Provider = keyDict.TryGetValue("provider", out var p)
                                ? p?.ToString() ?? ""
                                : "",
                            Name = keyDict.TryGetValue("name", out var n) ? n?.ToString() : null,
                            ApiKey = keyDict.TryGetValue("api-key", out var ak)
                                ? ak?.ToString()
                                : null,
                            BaseUrl = keyDict.TryGetValue("base-url", out var bu)
                                ? bu?.ToString()
                                : null,
                            Protocol = keyDict.TryGetValue("protocol", out var pr)
                                ? pr?.ToString() ?? "openai-completion"
                                : "openai-completion",
                        };
                    }
                    return new ApiKeyEntry();
                })
                .Where(k => !string.IsNullOrEmpty(k.Provider))
                .ToList();
        }

        // 注意：Model 为空 = 未配置 embedding（纯 BM25/FTS）。不能静默替换为
        // DefaultModel，否则「清空模型 = 禁用 embedding」的语义失效。
        return config;
    }
}

/// <summary>
/// 本地判定模型（System One / d1-omni，llama.cpp <c>/v1/systemone</c>）配置。
/// 整块可选：<see cref="Enabled"/>=false 时零 HTTP，检索/记忆/关系判定路径全部短路。
/// record + init：`with` 表达式用于配置热更新（UI 保存时原子交换）。
/// </summary>
public sealed record SystemOneConfig
{
    /// <summary>总开关。</summary>
    public bool Enabled { get; init; }

    /// <summary>llama-server 地址（`/v1/systemone` 所在主机）。</summary>
    public string BaseUrl { get; init; } = "http://127.0.0.1:8080";

    /// <summary>
    /// 接口路径（基址之后；默认 `/v1/systemone`）。
    /// 本地 llama.cpp 与托管 Jev（TypeSafe）同形；OpenRouter 决策端点为 `/decisions`（基址 https://openrouter.ai/api/alpha）。
    /// </summary>
    public string Path { get; init; } = "/v1/systemone";

    /// <summary>
    /// Bearer 访问密钥：托管 Jev（TypeSafe / OpenRouter）必填；空 = 不发送 Authorization 头（本地端点）。
    /// </summary>
    public string ApiKey { get; init; } = "";

    /// <summary>请求体 model 字段值。</summary>
    public string Model { get; init; } = "d1-omni";

    /// <summary>单次请求超时（毫秒）。</summary>
    public int TimeoutMs { get; init; } = 20_000;

    /// <summary>单请求 state+questions 序列化字符预算（上限由 llama-server -b 决定）。</summary>
    public int MaxStateChars { get; init; } = 3000;

    /// <summary>检索重排子配置。</summary>
    public SystemOneRerankConfig Rerank { get; init; } = new();

    /// <summary>记忆冲突判定子配置。</summary>
    public SystemOneConflictConfig Conflicts { get; init; } = new();

    /// <summary>关系提案子配置。</summary>
    public SystemOneRelationConfig Relations { get; init; } = new();

    /// <summary>本体提案（scan_ontology）子配置。</summary>
    public SystemOneOntologyConfig Ontology { get; init; } = new();

    /// <summary>Agent 循环判定子配置（三处启发式复核；失败一律保持现有行为）。</summary>
    public SystemOneLoopConfig Loop { get; init; } = new();

    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["enabled"] = Enabled,
            ["base-url"] = BaseUrl,
            ["path"] = Path,
            ["api-key"] = ApiKey,
            ["model"] = Model,
            ["timeout-ms"] = TimeoutMs,
            ["max-state-chars"] = MaxStateChars,
            ["rerank"] = Rerank.ToDict(),
            ["conflicts"] = Conflicts.ToDict(),
            ["relations"] = Relations.ToDict(),
            ["ontology"] = Ontology.ToDict(),
            ["loop"] = Loop.ToDict(),
        };

    public static SystemOneConfig FromDict(Dictionary<string, object?> dict) =>
        new()
        {
            Enabled = Bool(dict, "enabled", false),
            BaseUrl = Str(dict, "base-url", "http://127.0.0.1:8080"),
            Path = Str(dict, "path", "/v1/systemone"),
            ApiKey = Str(dict, "api-key", ""),
            Model = Str(dict, "model", "d1-omni"),
            TimeoutMs = Int(dict, "timeout-ms", 20_000),
            MaxStateChars = Int(dict, "max-state-chars", 3000),
            Rerank = SystemOneRerankConfig.FromDict(Sub(dict, "rerank")),
            Conflicts = SystemOneConflictConfig.FromDict(Sub(dict, "conflicts")),
            Relations = SystemOneRelationConfig.FromDict(Sub(dict, "relations")),
            Ontology = SystemOneOntologyConfig.FromDict(Sub(dict, "ontology")),
            Loop = SystemOneLoopConfig.FromDict(Sub(dict, "loop")),
        };

    // ── 解析辅助（子配置共用） ──

    internal static Dictionary<string, object?> Sub(Dictionary<string, object?> dict, string key) =>
        dict.TryGetValue(key, out var v) && v is Dictionary<string, object?> d ? d : [];

    internal static bool Bool(Dictionary<string, object?> dict, string key, bool fallback)
    {
        if (!dict.TryGetValue(key, out var v))
            return fallback;
        if (v is bool b)
            return b;
        var s = v?.ToString();
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        return fallback;
    }

    internal static int Int(Dictionary<string, object?> dict, string key, int fallback)
    {
        if (!dict.TryGetValue(key, out var v) || v is null)
            return fallback;
        try
        {
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    internal static double Double(Dictionary<string, object?> dict, string key, double fallback)
    {
        if (!dict.TryGetValue(key, out var v) || v is null)
            return fallback;
        try
        {
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    internal static string Str(Dictionary<string, object?> dict, string key, string fallback) =>
        dict.TryGetValue(key, out var v) && v?.ToString() is { Length: > 0 } s ? s : fallback;
}

/// <summary>检索重排子配置（只重排不丢弃；失败/缺答案的条目保持原分）。</summary>
public sealed record SystemOneRerankConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>候选放大倍数（1 = 不放大）。</summary>
    public int Overfetch { get; init; } = 2;

    /// <summary>融合权重 w ∈ [0,1]：final = rrf * (1 + w * (2p - 1))。</summary>
    public double Weight { get; init; } = 0.5;

    /// <summary>每条候选送入判定的文本截断长度（字符）。</summary>
    public int PassageChars { get; init; } = 240;

    /// <summary>每次检索最多判定的候选数。</summary>
    public int MaxCandidates { get; init; } = 20;

    /// <summary>
    /// 判定口径：
    /// <c>relevant</c>（默认）= 该段落与检索意图相关的程度（部分相关也算）；
    /// <c>answers</c> = 该段落**本身**是否直接回答检索意图（仅提及实体、相邻线索不算）。
    /// 实测（真实端点，strict/loose 两种 gold）后者在 R@5/AE@5 上稳定更好 ~10pp，故设为默认；
    /// 联想类查询由 <c>JudgeRerank.CriteriaFor(profile)</c> 按档位改回 relevant。
    /// </summary>
    public string Criteria { get; init; } = "answers";

    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["enabled"] = Enabled,
            ["overfetch"] = Overfetch,
            ["weight"] = Weight,
            ["passage-chars"] = PassageChars,
            ["max-candidates"] = MaxCandidates,
            ["criteria"] = Criteria,
        };

    public static SystemOneRerankConfig FromDict(Dictionary<string, object?> dict)
    {
        var defaults = new SystemOneRerankConfig();
        return new SystemOneRerankConfig
        {
            Enabled = SystemOneConfig.Bool(dict, "enabled", defaults.Enabled),
            Overfetch = SystemOneConfig.Int(dict, "overfetch", defaults.Overfetch),
            Weight = SystemOneConfig.Double(dict, "weight", defaults.Weight),
            PassageChars = SystemOneConfig.Int(dict, "passage-chars", defaults.PassageChars),
            MaxCandidates = SystemOneConfig.Int(dict, "max-candidates", defaults.MaxCandidates),
            Criteria = SystemOneConfig.Str(dict, "criteria", defaults.Criteria),
        };
    }
}

/// <summary>记忆冲突判定子配置。</summary>
public sealed record SystemOneConflictConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>单次判定最多送入冲突对数量。</summary>
    public int MaxPairs { get; init; } = 12;

    /// <summary>判定为 unrelated 且概率 ≥ 此值时从结果中移除该冲突对。</summary>
    public double DropUnrelatedProbability { get; init; } = 0.7;

    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["enabled"] = Enabled,
            ["max-pairs"] = MaxPairs,
            ["drop-unrelated-probability"] = DropUnrelatedProbability,
        };

    public static SystemOneConflictConfig FromDict(Dictionary<string, object?> dict)
    {
        var defaults = new SystemOneConflictConfig();
        return new SystemOneConflictConfig
        {
            Enabled = SystemOneConfig.Bool(dict, "enabled", defaults.Enabled),
            MaxPairs = SystemOneConfig.Int(dict, "max-pairs", defaults.MaxPairs),
            DropUnrelatedProbability = SystemOneConfig.Double(
                dict,
                "drop-unrelated-probability",
                defaults.DropUnrelatedProbability
            ),
        };
    }
}

/// <summary>关系提案（scan_relationships）子配置。</summary>
public sealed record SystemOneRelationConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>低于此概率的判定降级为 review（待人工确认）。</summary>
    public double MinProbability { get; init; } = 0.7;

    /// <summary>
    /// 低于此置信度的判定降级为 review（jevgraph 证据闸门的第二个门；应答未给出置信度时不判）。
    /// 与 <see cref="MinProbability"/> 是两个口径：概率是「所选关系标签在选项分布里的概率」，
    /// 置信度是「模型对整条判断的把握」——低置信度即便标签概率高也只提议、不直接建议落库。
    /// </summary>
    public double MinConfidence { get; init; } = 0.8;

    /// <summary>单次扫描最多判定候选对数量。</summary>
    public int MaxCandidates { get; init; } = 24;

    /// <summary>每个提及的最近邻上限（同句封锁）。</summary>
    public int MaxNeighbors { get; init; } = 8;

    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["enabled"] = Enabled,
            ["min-probability"] = MinProbability,
            ["min-confidence"] = MinConfidence,
            ["max-candidates"] = MaxCandidates,
            ["max-neighbors"] = MaxNeighbors,
        };

    public static SystemOneRelationConfig FromDict(Dictionary<string, object?> dict)
    {
        var defaults = new SystemOneRelationConfig();
        return new SystemOneRelationConfig
        {
            Enabled = SystemOneConfig.Bool(dict, "enabled", defaults.Enabled),
            MinProbability = SystemOneConfig.Double(
                dict,
                "min-probability",
                defaults.MinProbability
            ),
            MinConfidence = SystemOneConfig.Double(dict, "min-confidence", defaults.MinConfidence),
            MaxCandidates = SystemOneConfig.Int(dict, "max-candidates", defaults.MaxCandidates),
            MaxNeighbors = SystemOneConfig.Int(dict, "max-neighbors", defaults.MaxNeighbors),
        };
    }
}

/// <summary>本体提案（scan_ontology）子配置；SystemOne 总开关关闭时零请求。</summary>
public sealed record SystemOneOntologyConfig
{
    public bool Enabled { get; init; } = true;
    public double MinProbability { get; init; } = 0.7;
    public double MinConfidence { get; init; } = 0.8;
    public int MaxCandidates { get; init; } = 24;
    public int MaxEvidenceChars { get; init; } = 300;

    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["enabled"] = Enabled,
            ["min-probability"] = MinProbability,
            ["min-confidence"] = MinConfidence,
            ["max-candidates"] = MaxCandidates,
            ["max-evidence-chars"] = MaxEvidenceChars,
        };

    public static SystemOneOntologyConfig FromDict(Dictionary<string, object?> dict)
    {
        var defaults = new SystemOneOntologyConfig();
        return new SystemOneOntologyConfig
        {
            Enabled = SystemOneConfig.Bool(dict, "enabled", defaults.Enabled),
            MinProbability = SystemOneConfig.Double(
                dict,
                "min-probability",
                defaults.MinProbability
            ),
            MinConfidence = SystemOneConfig.Double(dict, "min-confidence", defaults.MinConfidence),
            MaxCandidates = SystemOneConfig.Int(dict, "max-candidates", defaults.MaxCandidates),
            MaxEvidenceChars = SystemOneConfig.Int(
                dict,
                "max-evidence-chars",
                defaults.MaxEvidenceChars
            ),
        };
    }
}

/// <summary>Agent 循环判定子配置（停止/漂移/稳定性三处启发式复核；默认关闭）。</summary>
public sealed record SystemOneLoopConfig
{
    /// <summary>循环判定总开关（默认关闭：它会改变核心循环行为，需显式开启）。</summary>
    public bool Enabled { get; init; }

    /// <summary>判定输入文本截断（助手回复 / 最近输出）。</summary>
    public int TextChars { get; init; } = 1200;

    /// <summary>自然停止复核子配置。</summary>
    public SystemOneStopCheckConfig StopCheck { get; init; } = new();

    /// <summary>VSA 漂移提醒复核子配置。</summary>
    public SystemOneDriftCheckConfig DriftCheck { get; init; } = new();

    /// <summary>稳定性断路复核子配置。</summary>
    public SystemOneStabilityCheckConfig StabilityCheck { get; init; } = new();

    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["enabled"] = Enabled,
            ["text-chars"] = TextChars,
            ["stop-check"] = StopCheck.ToDict(),
            ["drift-check"] = DriftCheck.ToDict(),
            ["stability-check"] = StabilityCheck.ToDict(),
        };

    public static SystemOneLoopConfig FromDict(Dictionary<string, object?> dict)
    {
        var defaults = new SystemOneLoopConfig();
        return new SystemOneLoopConfig
        {
            Enabled = SystemOneConfig.Bool(dict, "enabled", defaults.Enabled),
            TextChars = SystemOneConfig.Int(dict, "text-chars", defaults.TextChars),
            StopCheck = SystemOneStopCheckConfig.FromDict(SystemOneConfig.Sub(dict, "stop-check")),
            DriftCheck = SystemOneDriftCheckConfig.FromDict(
                SystemOneConfig.Sub(dict, "drift-check")
            ),
            StabilityCheck = SystemOneStabilityCheckConfig.FromDict(
                SystemOneConfig.Sub(dict, "stability-check")
            ),
        };
    }
}

/// <summary>自然停止复核子配置（一轮无工具调用的收尾 → 复核是否真的完成）。</summary>
public sealed record SystemOneStopCheckConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>判定「未完成，应继续」的概率阈值。</summary>
    public double MinProbability { get; init; } = 0.8;

    /// <summary>每个 run 最多注入提醒继续的次数。</summary>
    public int MaxPerRun { get; init; } = 1;

    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["enabled"] = Enabled,
            ["min-probability"] = MinProbability,
            ["max-per-run"] = MaxPerRun,
        };

    public static SystemOneStopCheckConfig FromDict(Dictionary<string, object?> dict)
    {
        var defaults = new SystemOneStopCheckConfig();
        return new SystemOneStopCheckConfig
        {
            Enabled = SystemOneConfig.Bool(dict, "enabled", defaults.Enabled),
            MinProbability = SystemOneConfig.Double(
                dict,
                "min-probability",
                defaults.MinProbability
            ),
            MaxPerRun = SystemOneConfig.Int(dict, "max-per-run", defaults.MaxPerRun),
        };
    }
}

/// <summary>VSA 漂移提醒复核子配置（判定仍在轨道时压制误报提醒）。</summary>
public sealed record SystemOneDriftCheckConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>判定「仍在角色/任务范围内」的概率阈值。</summary>
    public double MinProbability { get; init; } = 0.6;

    public Dictionary<string, object?> ToDict() =>
        new() { ["enabled"] = Enabled, ["min-probability"] = MinProbability };

    public static SystemOneDriftCheckConfig FromDict(Dictionary<string, object?> dict)
    {
        var defaults = new SystemOneDriftCheckConfig();
        return new SystemOneDriftCheckConfig
        {
            Enabled = SystemOneConfig.Bool(dict, "enabled", defaults.Enabled),
            MinProbability = SystemOneConfig.Double(
                dict,
                "min-probability",
                defaults.MinProbability
            ),
        };
    }
}

/// <summary>稳定性断路复核子配置（判定仍在实质推进时推翻本次断路）。</summary>
public sealed record SystemOneStabilityCheckConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>判定「仍在实质推进」的概率阈值。</summary>
    public double MinProbability { get; init; } = 0.75;

    /// <summary>取样最近 N 次工具调用。</summary>
    public int MaxCalls { get; init; } = 6;

    public Dictionary<string, object?> ToDict() =>
        new()
        {
            ["enabled"] = Enabled,
            ["min-probability"] = MinProbability,
            ["max-calls"] = MaxCalls,
        };

    public static SystemOneStabilityCheckConfig FromDict(Dictionary<string, object?> dict)
    {
        var defaults = new SystemOneStabilityCheckConfig();
        return new SystemOneStabilityCheckConfig
        {
            Enabled = SystemOneConfig.Bool(dict, "enabled", defaults.Enabled),
            MinProbability = SystemOneConfig.Double(
                dict,
                "min-probability",
                defaults.MinProbability
            ),
            MaxCalls = SystemOneConfig.Int(dict, "max-calls", defaults.MaxCalls),
        };
    }
}
