using System.Collections.Concurrent;
using System.Text;

namespace Core.Tools;

/// <summary>
/// 工具注入器：控制每次 LLM 请求注入的工具子集。
///
/// 核心机制：
/// - 常驻工具（AlwaysLoaded）：始终注入（tool_search / ask_user / todo_write）。
/// - 已展开工具（Expanded）：通过 tool_search 命中后缓存于此，后续请求持续注入。
/// - 延迟加载工具（DeferLoading=true）：不预注入，仅通过 tool_search 发现。
///
/// 会话切换时清空 Expanded 缓存。
/// </summary>
public sealed class ToolInjector
{
    private readonly ToolRegistry _registry;
    private readonly ToolSearchEngine _searchEngine;

    // 常驻工具名称集合
    private readonly HashSet<string> _alwaysLoadedNames;

    // 会话内已展开工具：toolName → (工具实例, 最近使用时间戳)
    // 单字典原子写入，避免双字典非原子双写的 LRU 竞态（刚写入即被淘汰）
    private readonly ConcurrentDictionary<string, (AgentTool Tool, long Ticks)> _expandedTools =
        new(StringComparer.Ordinal);

    // 最大展开工具数（超过后 LRU 淘汰）
    private const int MaxExpandedTools = 20;

    // 常驻工具默认集合
    private static readonly string[] DefaultAlwaysLoaded =
    [
        "tool_search",
        "ask_user",
        "todo_write",
    ];

    // 延迟加载插件名单：这些插件下的工具不预注入，仅通过 tool_search 发现
    private readonly HashSet<string> _deferredPluginNames;

    // 动态注入模式：true = 按需注入（延迟插件不预注入）；false = 全量注入（灰度开关）
    private readonly bool _dynamicMode;

    // 非延迟工具缓存（仅在注册表版本变更时重建）
    private List<AgentTool>? _cachedRegularTools;
    private int _cachedRegistryVersion = -1;

    // BuildRequestTools 结果缓存：签名不变时直接返回缓存数组（避免每请求排序）
    private string? _cachedSignature;
    private IReadOnlyList<AgentTool>? _cachedResult;

    // 展开集版本计数器：Expand/Evict/Clear 时递增，ToolsSignature 据此 O(1) 计算
    // （替代每次 Keys.OrderBy + string.Join 的 O(n log n) 字符串拼接）
    private int _toolsVersion;

    // LRU 单调时钟：展开时递增，保证并发/快速连续展开的时间戳严格有序
    // （Environment.TickCount64 分辨率 ~15.6ms，快速循环中多个工具同 tick → 淘汰不确定）
    private long _lruClock;

    public ToolInjector(
        ToolRegistry registry,
        ToolSearchEngine searchEngine,
        string[]? alwaysLoadedNames = null,
        IReadOnlySet<string>? deferredPluginNames = null,
        bool dynamicMode = true
    )
    {
        _registry = registry;
        _searchEngine = searchEngine;
        _alwaysLoadedNames = new HashSet<string>(
            alwaysLoadedNames ?? DefaultAlwaysLoaded,
            StringComparer.Ordinal
        );
        _deferredPluginNames = new HashSet<string>(
            deferredPluginNames ?? new HashSet<string>(),
            StringComparer.Ordinal
        );
        _dynamicMode = dynamicMode;
        _cachedRegularTools = BuildRegularToolsList();
        _cachedRegistryVersion = _registry.Version;
    }

    /// <summary>构建非延迟工具列表（过滤 DeferLoading + 延迟插件）。</summary>
    private List<AgentTool> BuildRegularToolsList()
    {
        var result = new List<AgentTool>();
        foreach (var tool in _registry.GetAll())
        {
            if (tool.DeferLoading)
                continue;
            // dynamicMode=false（灰度全量）时不过滤延迟插件
            if (_dynamicMode && _deferredPluginNames.Count > 0)
            {
                var plugin = _registry.GetPluginName(tool.Name);
                if (plugin is not null && _deferredPluginNames.Contains(plugin))
                    continue;
            }
            result.Add(tool);
        }
        return result;
    }

    /// <summary>会话内已展开的工具（只读快照）。</summary>
    public IReadOnlyDictionary<string, AgentTool> ExpandedTools =>
        _expandedTools.ToDictionary(kv => kv.Key, kv => kv.Value.Tool, StringComparer.Ordinal);

    /// <summary>
    /// 注入面签名：registry 版本 + 展开集版本计数。
    /// 任一变化都意味着下一轮工具列表需要重建（ConversationLoop 据此热刷新）。
    /// O(1)：仅作相等比较，不要求人类可读；调试展开集可直接读 ExpandedTools。
    /// </summary>
    public string ToolsSignature => $"{_registry.Version}:{Volatile.Read(ref _toolsVersion)}";

    /// <summary>
    /// 构建每次请求的工具列表 = 常驻 + 已展开 + 非延迟加载工具。
    ///
    /// 非延迟加载工具（DeferLoading=false 且不在常驻集合中）也始终注入，
    /// 以保持向后兼容——只有显式标记 DeferLoading=true 的工具才被延迟。
    /// </summary>
    public IReadOnlyList<AgentTool> BuildRequestTools()
    {
        // 注入面签名缓存：签名不变（registry/展开集未变）直接返回缓存结果
        var signature = ToolsSignature;
        if (_cachedSignature == signature && _cachedResult is not null)
            return _cachedResult;

        // 缓存失效检测：注册表版本变更时重建非延迟工具列表
        if (_cachedRegistryVersion != _registry.Version)
        {
            _cachedRegularTools = BuildRegularToolsList();
            _cachedRegistryVersion = _registry.Version;
        }

        var result = new Dictionary<string, AgentTool>(StringComparer.Ordinal);

        // 1. 常驻工具
        foreach (var name in _alwaysLoadedNames)
        {
            if (_registry.TryGet(name) is { } tool)
                result[name] = tool;
        }

        // 2. 已展开工具（经 registry 验证仍存在——插件卸载后自动排除）
        foreach (var (name, _) in _expandedTools)
        {
            if (_registry.TryGet(name) is { } tool)
                result[name] = tool;
        }

        // 3. 非延迟工具（来自缓存）
        // dynamicMode：_cachedRegularTools 已按延迟插件过滤；
        // 全量模式（灰度开关 ToolSearchMode=all）：除显式 DeferLoading 外全部注入。
        // TryAdd 去重：常驻 / 已展开已注入的同名工具跳过。
        foreach (var tool in _cachedRegularTools!)
        {
            result.TryAdd(tool.Name, tool);
        }

        var built = result.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        _cachedSignature = signature;
        return _cachedResult = built;
    }

    /// <summary>
    /// 根据 query 检索并展开工具（由 ToolSearchTool.ExecuteAsync 调用）。
    /// 命中工具的完整 schema 加入 ExpandedTools，下一轮请求自动注入。
    /// </summary>
    /// <returns>命中的工具名列表。</returns>
    public async Task<IReadOnlyList<string>> ExpandAsync(
        string query,
        int topK = 5,
        CancellationToken ct = default
    )
    {
        // 索引失效时自动重建（防御性：PluginLoader 回调可能已触发 InvalidateIndex）
        if (!_searchEngine.IsIndexed)
            await _searchEngine.BuildIndexAsync(_registry, ct);

        var hits = await _searchEngine.SearchAsync(query, topK, ct);
        return ExpandHits(hits);
    }

    /// <summary>
    /// 展开已检索命中的工具（由 ToolSearchTool.ExecuteAsync 传入预检索结果，
    /// 避免同一 query 双重检索 + 两次 embedding 网络往返）。
    /// </summary>
    public IReadOnlyList<string> ExpandHits(List<ToolSearchHit> hits)
    {
        var expanded = false;
        foreach (var hit in hits)
        {
            if (_registry.TryGet(hit.ToolName) is { } tool)
            {
                _expandedTools[hit.ToolName] = (tool, Interlocked.Increment(ref _lruClock));
                expanded = true;
            }
        }

        // LRU 淘汰
        EvictIfNeeded();

        // 展开集有变化才递增版本（避免无谓重建）
        if (expanded)
            Interlocked.Increment(ref _toolsVersion);

        return hits.Select(h => h.ToolName).ToArray();
    }

    /// <summary>确保搜索索引已就绪（索引失效时惰性重建）。</summary>
    public async Task EnsureIndexedAsync(CancellationToken ct = default)
    {
        if (!_searchEngine.IsIndexed)
            await _searchEngine.BuildIndexAsync(_registry, ct);
    }

    /// <summary>手动展开指定工具（如模型直接调用已知工具名）。</summary>
    public void ExpandTool(string toolName)
    {
        if (_registry.TryGet(toolName) is { } tool)
        {
            _expandedTools[toolName] = (tool, Interlocked.Increment(ref _lruClock));
            Interlocked.Increment(ref _toolsVersion);
            EvictIfNeeded();
        }
    }

    /// <summary>清空已展开工具缓存（会话切换时调用）。</summary>
    public void ClearExpanded()
    {
        _expandedTools.Clear();
        _cachedSignature = null;
        _cachedResult = null;
        Interlocked.Increment(ref _toolsVersion);
    }

    /// <summary>使工具搜索索引失效（插件卸载后调用）。</summary>
    public void InvalidateIndex() => _searchEngine.InvalidateIndex();

    public bool IsToolAvailable(string toolName)
    {
        if (_alwaysLoadedNames.Contains(toolName))
            return _registry.TryGet(toolName) is not null;
        // 非延迟工具（DeferLoading=false 且不在延迟插件名单）已注入 → 可用
        if (_cachedRegularTools is { } regular && regular.Any(t => t.Name == toolName))
            return _registry.TryGet(toolName) is not null;
        return _expandedTools.ContainsKey(toolName) && _registry.TryGet(toolName) is not null;
    }

    private void EvictIfNeeded()
    {
        if (_expandedTools.Count <= MaxExpandedTools)
            return;

        // 按最近使用时间排序，淘汰最旧的。
        // 先 ToArray 快照再排序：直接对 ConcurrentDictionary 做 OrderBy，
        // 并发展开时枚举期间被修改会抛 CopyTo 越界异常（ConcurrentDictionary 的 LINQ 非安全快照）。
        var snapshot = _expandedTools.ToArray();
        var toEvict = snapshot
            .OrderBy(kv => kv.Value.Ticks)
            .Take(snapshot.Length - MaxExpandedTools)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var name in toEvict)
        {
            _expandedTools.TryRemove(name, out _);
        }
    }

    /// <summary>
    /// 生成可用工具库目录（动态，注入 system prompt）。
    /// 只列当前未注入的插件能力——已注入工具模型直接可见，无需目录；
    /// 目录 = 实际加载工具集的实时投影，插件装卸后自动增减（热刷新重建时重新生成）。
    /// 全量模式（dynamicMode=false）下无隐藏工具 → 返回空串。
    /// </summary>
    public string BuildToolDirectory()
    {
        // 当前注入集合（常驻 + 已展开 + 非延迟）
        var injected = BuildRequestTools();
        var injectedNames = new HashSet<string>(
            injected.Select(t => t.Name),
            StringComparer.Ordinal
        );

        // 未注入工具按插件分组
        var hiddenByPlugin = new Dictionary<string, List<AgentTool>>(StringComparer.Ordinal);
        foreach (var tool in _registry.GetAll())
        {
            if (injectedNames.Contains(tool.Name))
                continue;
            var plugin = _registry.GetPluginName(tool.Name) ?? "Core";
            if (!hiddenByPlugin.TryGetValue(plugin, out var list))
                hiddenByPlugin[plugin] = list = [];
            list.Add(tool);
        }

        if (hiddenByPlugin.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("## 可用工具库目录（按需检索）");
        sb.AppendLine();
        sb.AppendLine("当前列表仅注入核心工具。通过 tool_search 可发现以下未注入能力：");

        foreach (
            var (plugin, tools) in hiddenByPlugin.OrderBy(kv => kv.Key, StringComparer.Ordinal)
        )
        {
            var samples = tools.Take(3).ToList();
            var labels = string.Join(
                "、",
                samples.Select(t => t.Label).Where(l => !string.IsNullOrWhiteSpace(l))
            );
            var names = string.Join("、", samples.Select(t => t.Name));
            sb.AppendLine($"- {plugin}（{tools.Count} 工具）：{labels}（如 {names}）");
        }

        sb.AppendLine("需要这些能力时用 tool_search 检索具体工具名，不要假设工具不存在。");
        return sb.ToString();
    }
}
