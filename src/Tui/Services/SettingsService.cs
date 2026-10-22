using Core.Data;
using Core.Plugins;

namespace Tui.Services;

/// <summary>
/// 管理持久化设置，存储在 ~/.mythology/config.yaml 中。
/// 替代原来的 SQLite AppSetting KV 表。
/// 直接操作 ConfigData 模型，通过 ConfigStore 读写 YAML。
/// </summary>
public sealed class SettingsService(ConfigStore configStore)
{
    /// <summary>内存中的配置数据副本，LoadAll 时从 YAML 加载。</summary>
    private ConfigData _data = new();

    /// <summary>提供商配置。</summary>
    public ProviderConfig Provider
    {
        get => _data.Providers;
        set => _data.Providers = value;
    }

    /// <summary>MCP 配置。</summary>
    public McpConfig Mcp
    {
        get => _data.Mcp;
        set => _data.Mcp = value;
    }

    /// <summary>工具配置。</summary>
    public ToolConfig Tool
    {
        get => _data.Tools;
        set => _data.Tools = value;
    }

    /// <summary>Embedding 模型配置。</summary>
    public EmbeddingConfig Embedding
    {
        get => _data.Embedding;
        set => _data.Embedding = value;
    }

    /// <summary>本地判定模型（System One / d1-omni）配置（可选，默认关闭）。</summary>
    public SystemOneConfig SystemOne
    {
        get => _data.SystemOne;
        set => _data.SystemOne = value;
    }

    /// <summary>上下文管理配置（compact | continuation）。</summary>
    public ContextManagementConfig ContextManagement
    {
        get => _data.ContextManagement;
        set => _data.ContextManagement = value;
    }

    /// <summary>当前主题标识（ThemeRegistry.Id）。</summary>
    public string ThemeName
    {
        get => _data.ThemeName;
        set => _data.ThemeName = value;
    }

    /// <summary>扫描到的插件列表（从 plugin.json 加载）。</summary>
    public List<PluginManifest> DiscoveredPlugins { get; private set; } = [];

    /// <summary>
    /// 加载所有配置。
    /// </summary>
    public void LoadAll()
    {
        // 从 YAML 文件加载配置到内存
        _data = configStore.Load();

        // Use GetConfigPath to check if config file truly exists
        // without leaking file system dependency into SettingsService
        // 仅当 Default 为纯 "custom"（无名称，未初始化）且无 API Key 时覆盖默认值
        // 已配置的自定义 provider 使用 "custom:名称" 复合键，不会进入此分支
        if (
            string.IsNullOrEmpty(_data.Providers.Default)
            || _data.Providers is { Default: "custom", ApiKeys.Count: 0 }
        )
        {
            // 未配置时写入默认提供商（openai + gpt-4o-mini）
            _data.Providers = new ProviderConfig
            {
                Default = "openai",
                DefaultModel = "gpt-4o-mini",
                ThinkingLevel = "medium",
                ApiKeys = [],
            };
        }

        if (_data.Mcp.Servers.Count == 0)
        {
            // MCP 未配置时写入示例服务器
            _data.Mcp = new McpConfig
            {
                Servers =
                [
                    new McpServerEntry
                    {
                        Name = "example-server",
                        Command = "npx",
                        Args = ["-y", "@example/mcp-server"],
                        Enabled = true,
                    },
                ],
            };
        }

        ScanPlugins();
    }

    /// <summary>
    /// 重新扫描插件目录，刷新 DiscoveredPlugins 列表。
    /// </summary>
    public void ReloadPlugins() => ScanPlugins();

    /// <summary>
    /// 扫描所有插件目录，加载 plugin.json 信息。
    /// </summary>
    private void ScanPlugins()
    {
        var pluginDirs = GetPluginDirs();
        var scanned = PluginLoader.ScanPluginDirs(pluginDirs);
        DiscoveredPlugins = scanned.Select(s => s.Manifest).ToList();
    }

    /// <summary>
    /// 获取插件是否启用（从 ToolConfig 查找，默认启用）。
    /// </summary>
    /// <param name="pluginId">插件 ID。</param>
    /// <returns>是否启用。</returns>
    public bool IsPluginEnabled(string pluginId)
    {
        var entry = _data.Tools.Tools.FirstOrDefault(t =>
            t.Name.Equals(pluginId, StringComparison.OrdinalIgnoreCase)
        );
        return entry?.Enabled ?? true;
    }

    /// <summary>
    /// 保存提供商配置。
    /// </summary>
    public async Task SaveProviderAsync() => await SaveAllAsync();

    /// <summary>
    /// 保存 MCP 配置。
    /// </summary>
    public async Task SaveMcpAsync() => await SaveAllAsync();

    /// <summary>
    /// 保存工具配置。
    /// </summary>
    public async Task SaveToolAsync() => await SaveAllAsync();

    /// <summary>
    /// 保存上下文管理配置（不触发运行期热切换；热切换走 ContextModeSwitcher）。
    /// </summary>
    public async Task SaveContextManagementAsync() => await SaveAllAsync();

    /// <summary>
    /// 异步保存所有配置到 ~/.mythology/config.yaml。
    /// </summary>
    /// <summary>保存主题配置到磁盘。</summary>
    public Task SaveThemeAsync() => SaveAllAsync();

    private async Task SaveAllAsync()
    {
        await configStore.SaveAsync(_data);
    }

    /// <summary>
    /// 获取插件目录列表。
    /// </summary>
    /// <returns>插件目录和信任级别列表。</returns>
    private static List<(string Directory, PluginTrustLevel Trust)> GetPluginDirs()
    {
        var dirs = new List<(string, PluginTrustLevel)>();
        // 用户主目录
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // 环境变量指定的插件目录
        var envDir = Environment.GetEnvironmentVariable("NOVEL_PLUGINS_DIR");
        if (envDir is not null)
            dirs.Add((envDir, PluginTrustLevel.External));
        // 工作目录下的 .mythology/plugins
        dirs.Add(
            (
                Path.Combine(Directory.GetCurrentDirectory(), ".mythology", "plugins"),
                PluginTrustLevel.External
            )
        );
        // 用户主目录下的 ~/.mythology/plugins
        dirs.Add((Path.Combine(homeDir, ".mythology", "plugins"), PluginTrustLevel.External));
        return dirs;
    }
}
