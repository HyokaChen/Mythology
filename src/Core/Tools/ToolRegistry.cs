using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Core.Tools;

/// <summary>
/// 工具注册表，线程安全。
/// 按插件分组存储工具，支持分组查询和浏览。
/// 使用 ConcurrentDictionary 保证多线程安全。
/// </summary>
public sealed class ToolRegistry
{
    /// <summary>工具字典，按名称索引。Key 为工具名称，Value 为工具实例。</summary>
    private readonly ConcurrentDictionary<string, AgentTool> _tools = new(StringComparer.Ordinal);

    /// <summary>工具 → 插件名称映射。Key 为工具名称，Value 为所属插件名称。</summary>
    private readonly ConcurrentDictionary<string, string> _toolToPlugin = new(
        StringComparer.Ordinal
    );

    /// <summary>插件名称 → 工具名称列表。Key 为插件名称，Value 为该插件下的工具名称列表。</summary>
    private readonly ConcurrentDictionary<string, List<string>> _pluginTools = new(
        StringComparer.Ordinal
    );

    /// <summary>插件名称 → 规范化 ID 的映射。Key 为规范化 ID，Value 为原始插件名称。</summary>
    private readonly ConcurrentDictionary<string, string> _pluginIds = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>被禁用的插件名称集合。GetAll/TryGet 会过滤这些插件下的工具。</summary>
    private readonly ConcurrentDictionary<string, byte> _disabledPlugins = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>注册表版本号，每次 Register/Unregister 递增（Interlocked 保证并发安全）。</summary>
    private int _version;

    /// <summary>当前版本号，用于缓存失效检测。</summary>
    public int Version => Interlocked.CompareExchange(ref _version, 0, 0);

    /// <summary>
    /// 注册单个工具，必须指定所属插件。
    /// 如果工具已存在，会覆盖旧值。
    /// </summary>
    /// <param name="tool">要注册的工具。</param>
    /// <param name="pluginName">所属插件名称。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Register(AgentTool tool, string pluginName)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrEmpty(pluginName);

        _tools[tool.Name] = tool;
        _toolToPlugin[tool.Name] = pluginName;
        _pluginTools.AddOrUpdate(
            pluginName,
            [tool.Name],
            (_, existing) =>
            {
                if (!existing.Contains(tool.Name, StringComparer.Ordinal))
                {
                    var updated = new List<string>(existing) { tool.Name };
                    return updated;
                }
                return existing;
            }
        );

        // 注册规范化 ID
        var normalizedId = NormalizePluginId(pluginName);
        _pluginIds.TryAdd(normalizedId, pluginName);
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// 将插件名称规范化为 kebab-case ID。
    /// 例如 "My Plugin" → "my-plugin"，"My_Plugin" → "my-plugin"。
    /// </summary>
    /// <param name="pluginName">原始插件名称。</param>
    /// <returns>规范化的 kebab-case ID。</returns>
    public static string NormalizePluginId(string pluginName)
    {
        return pluginName.ToLowerInvariant().Replace(" ", "-").Replace("_", "-");
    }

    /// <summary>
    /// 批量注册工具，全部归属同一插件。
    /// </summary>
    /// <param name="tools">要注册的工具集合。</param>
    /// <param name="pluginName">所属插件名称。</param>
    public void RegisterRange(IEnumerable<AgentTool> tools, string pluginName)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrEmpty(pluginName);
        foreach (var tool in tools)
            Register(tool, pluginName);
    }

    /// <summary>
    /// 按名称获取工具。如果工具所属插件被禁用，返回 null。
    /// </summary>
    /// <param name="name">工具名称。</param>
    /// <returns>找到的工具实例，未找到或插件被禁用则返回 null。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AgentTool? TryGet(string name)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return null;
        if (
            _toolToPlugin.TryGetValue(name, out var pluginName)
            && _disabledPlugins.ContainsKey(pluginName)
        )
            return null;
        return tool;
    }

    /// <summary>
    /// 获取工具所属插件名称。
    /// </summary>
    /// <param name="toolName">工具名称。</param>
    /// <returns>所属插件名称，未找到则返回 null。</returns>
    public string? GetPluginName(string toolName)
    {
        return _toolToPlugin.GetValueOrDefault(toolName);
    }

    /// <summary>
    /// 获取所有已注册插件名称（去重、按字母顺序排序）。
    /// </summary>
    /// <returns>插件名称列表。</returns>
    public IReadOnlyList<string> GetPluginNames()
    {
        return _pluginTools.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 获取所有已注册插件的规范化 ID（去重、按字母顺序排序）。
    /// </summary>
    /// <returns>规范化 ID 列表。</returns>
    public IReadOnlyList<string> GetPluginIds()
    {
        return _pluginIds.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 通过规范化 ID 查找插件名称。
    /// </summary>
    /// <param name="pluginId">规范化 ID。</param>
    /// <returns>原始插件名称，未找到则返回 null。</returns>
    public string? GetPluginNameById(string pluginId)
    {
        return _pluginIds.GetValueOrDefault(pluginId);
    }

    /// <summary>
    /// 获取指定插件下的所有工具。
    /// </summary>
    /// <param name="pluginName">插件名称。</param>
    /// <returns>该插件下的工具列表，如果插件不存在则返回空数组。</returns>
    public IReadOnlyList<AgentTool> GetToolsByPlugin(string pluginName)
    {
        if (!_pluginTools.TryGetValue(pluginName, out var names))
            return [];

        var result = new List<AgentTool>(names.Count);
        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (_tools.TryGetValue(name, out var tool))
                result.Add(tool);
        }
        return result;
    }

    /// <summary>
    /// 获取所有已注册工具（扁平列表，按名称排序）。
    /// 过滤掉被禁用插件下的工具。
    /// </summary>
    /// <returns>所有可用工具的集合。</returns>
    public IReadOnlyCollection<AgentTool> GetAll()
    {
        if (_disabledPlugins.IsEmpty)
            return _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();

        return _tools
            .Values.Where(t =>
                !_toolToPlugin.TryGetValue(t.Name, out var pn) || !_disabledPlugins.ContainsKey(pn)
            )
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 设置被禁用的插件集合。替换之前的禁用列表。
    /// GetAll/TryGet 会自动过滤禁用插件下的工具。
    /// </summary>
    /// <param name="disabledPluginNames">被禁用的插件名称集合。</param>
    public void SetDisabledPlugins(IEnumerable<string> disabledPluginNames)
    {
        _disabledPlugins.Clear();
        foreach (var name in disabledPluginNames)
            _disabledPlugins.TryAdd(name, 0);
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// 获取当前被禁用的插件名称集合。
    /// </summary>
    public IReadOnlyCollection<string> GetDisabledPlugins() => _disabledPlugins.Keys.ToArray();

    /// <summary>
    /// 检查指定插件是否被禁用。
    /// </summary>
    public bool IsPluginDisabled(string pluginName) => _disabledPlugins.ContainsKey(pluginName);

    /// <summary>
    /// 按插件分组获取所有工具。
    /// </summary>
    /// <returns>插件名称和工具列表的元组列表。</returns>
    public IReadOnlyList<(string PluginName, IReadOnlyList<AgentTool> Tools)> GetAllByPlugin()
    {
        var plugins = GetPluginNames();
        var result = new List<(string, IReadOnlyList<AgentTool>)>(plugins.Count);
        foreach (var plugin in plugins)
        {
            var tools = GetToolsByPlugin(plugin);
            if (tools.Count > 0)
                result.Add((plugin, tools));
        }
        return result;
    }

    /// <summary>
    /// 按插件名称移除所有已注册工具。
    /// 返回被移除的工具数量。
    /// </summary>
    public int UnregisterByPlugin(string pluginName)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginName);

        if (!_pluginTools.TryRemove(pluginName, out var toolNames))
            return 0;

        foreach (var toolName in toolNames)
        {
            _tools.TryRemove(toolName, out _);
            _toolToPlugin.TryRemove(toolName, out _);
        }

        var normalizedId = NormalizePluginId(pluginName);
        _pluginIds.TryRemove(normalizedId, out _);
        Interlocked.Increment(ref _version);

        return toolNames.Count;
    }

    /// <summary>已注册工具的总数。</summary>
    public int Count => _tools.Count;

    /// <summary>已注册插件的数量。</summary>
    public int PluginCount => _pluginTools.Count;
}
