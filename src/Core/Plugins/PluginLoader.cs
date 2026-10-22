using System.Reflection;
using System.Runtime.Loader;
using Core.Infrastructure;
using Core.Tools;
using Microsoft.Extensions.Logging;

namespace Core.Plugins;

/// <summary>
/// 插件加载器 — 负责扫描、加载和管理外部插件 DLL。
/// 三级信任模型：Builtin（目录位置）、Verified（DLL 签名白名单）、External（最小权限）。
/// 权限数据源是 DLL 中的 PluginPermissionAttribute（受强名称签名保护）。
/// 线程安全：_loaded 使用锁保护，LoadedPlugins 缓存结果避免重复分配。
/// </summary>
public sealed class PluginLoader(ILogger? logger = null) : IDisposable
{
    /// <summary>插件卸载后触发的回调（用于重建搜索索引等）。</summary>
    public Action<string>? OnPluginUnloaded { get; set; }

    private readonly List<(
        PluginLoadContext Ctx,
        IToolPlugin Instance,
        PluginManifest Manifest,
        PluginTrustLevel Trust,
        string SourceDir
    )> _loaded = [];

    private readonly Lock _lock = new();
    private int _loadedVersion;
    private (int Version, IReadOnlyList<LoadedPlugin> List) _cachedPlugins;
    private bool _disposed;
    private ToolRegistry? _registry;

    /// <summary>获取所有已加载插件的只读列表。缓存结果，仅在 _loaded 变更时重新计算。</summary>
    public IReadOnlyList<LoadedPlugin> LoadedPlugins
    {
        get
        {
            lock (_lock)
            {
                if (_cachedPlugins.Version == _loadedVersion)
                    return _cachedPlugins.List;
                var list = _loaded
                    .Select(p => new LoadedPlugin(p.Instance, p.Manifest, p.Trust, p.SourceDir))
                    .ToList()
                    .AsReadOnly();
                _cachedPlugins = (_loadedVersion, list);
                return list;
            }
        }
    }

    /// <summary>
    /// 加载所有插件，将工具注册到 registry 并按插件分组。
    /// plugin.json 的 permissions/author 字段运行时不信任（权限从 DLL Attribute 读取，Trust 由目录+签名判定）。
    /// manifest.Id 用于去重和分组，但以 DLL 所在目录名作为权威源（不可篡改）。
    /// </summary>
    /// <param name="registry">工具注册表，用于注册插件提供的工具。</param>
    /// <param name="pluginDirs">插件目录列表及对应的默认信任级别。</param>
    /// <param name="trustedSp">受信任的服务提供器（用于 Builtin 插件）。</param>
    /// <param name="trustedPublicKeyToken">Mythology 官方签名 PublicKeyToken（用于 Verified 判定）。</param>
    public void LoadAll(
        ToolRegistry registry,
        IEnumerable<(string Directory, PluginTrustLevel Trust)> pluginDirs,
        IServiceProvider trustedSp,
        string? trustedPublicKeyToken = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _registry = registry;

        var builtinDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        var trustedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (trustedPublicKeyToken is not null)
            trustedTokens.Add(trustedPublicKeyToken);

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var newPlugins =
            new List<(
                PluginLoadContext Ctx,
                IToolPlugin Instance,
                PluginManifest Manifest,
                PluginTrustLevel Trust,
                string SourceDir
            )>();

        foreach (var (dir, _) in pluginDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var manifest = PluginManifest.LoadFromDirectory(subDir);
                if (manifest is null)
                    continue;

                // 权威 Id = 目录名（不可篡改）。plugin.json 的 id 可能被篡改，用目录名交叉验证。
                var authoritativeId = Path.GetFileName(subDir);
                if (
                    !string.Equals(manifest.Id, authoritativeId, StringComparison.OrdinalIgnoreCase)
                )
                {
                    CoreLogger.PluginManifestMismatch(
                        logger.Safe(),
                        authoritativeId,
                        "id=" + authoritativeId,
                        "id=" + manifest.Id
                    );
                    // 覆盖为目录名（权威源），防止篡改 JSON 导致去重混乱
                    manifest.Id = authoritativeId;
                }

                if (!seenIds.Add(authoritativeId))
                    continue;

                foreach (var dllPath in Directory.GetFiles(subDir, "*.dll"))
                {
                    PluginLoadContext? ctx = null;
                    try
                    {
                        // 0. 提前创建 PluginLoadContext，用于共享依赖检查和程序集加载
                        ctx = new PluginLoadContext(dllPath);

                        // 1. 跳过共享依赖：如果 DLL 的程序集名已在默认 ALC 中加载，
                        //    说明它是共享依赖（如 Core.dll），不是插件候选。
                        //    使用 PluginLoadContext.IsSharedAssembly 确保与 Load() 使用同一数据源。
                        //    同时检查版本兼容性：
                        //    - 主版本不一致 → Error 阻断（接口不兼容）
                        //    - 插件编译版本 > Host 版本 → Warning（运行时可能缺失 API）
                        //    - 插件编译版本 < Host 版本 → Debug（接口二进制兼容，通常安全）
                        var dllAssemblyNameObj = AssemblyName.GetAssemblyName(dllPath);
                        var dllAssemblyName = dllAssemblyNameObj.Name;
                        if (
                            dllAssemblyName is not null
                            && ctx.IsSharedAssembly(dllAssemblyName, out var hostVersion)
                        )
                        {
                            var pluginVersion = dllAssemblyNameObj.Version ?? new Version(0, 0);

                            if (hostVersion.Major != pluginVersion.Major)
                            {
                                // 主版本不一致 — 接口可能不兼容，阻断加载
                                CoreLogger.PluginSharedDependencyVersionIncompatible(
                                    logger.Safe(),
                                    authoritativeId,
                                    dllAssemblyName,
                                    hostVersion.ToString(),
                                    pluginVersion.ToString()
                                );
                            }
                            else if (pluginVersion > hostVersion)
                            {
                                // 插件编译版本比 Host 新 — 可能调用 Host 中不存在的 API
                                CoreLogger.PluginSharedDependencyVersionNewer(
                                    logger.Safe(),
                                    authoritativeId,
                                    dllAssemblyName,
                                    hostVersion.ToString(),
                                    pluginVersion.ToString()
                                );
                            }
                            else
                            {
                                // 插件编译版本 <= Host 版本 — 接口二进制兼容
                                CoreLogger.PluginSharedDependencyVersionOlder(
                                    logger.Safe(),
                                    authoritativeId,
                                    dllAssemblyName,
                                    hostVersion.ToString(),
                                    pluginVersion.ToString()
                                );
                            }

                            // 共享依赖跳过 — Dispose 空的 ALC（取消 Resolving 订阅）
                            ctx.Dispose();
                            continue;
                        }

                        // 2. Trust 判定：目录位置 + DLL 签名
                        var trust = ResolveTrust(subDir, dllPath, builtinDir, trustedTokens);
                        CoreLogger.PluginTrustResolved(logger.Safe(), authoritativeId, trust);

                        // 3. 加载程序集（复用步骤 1 已解析的 AssemblyName）
                        var assembly = ctx.LoadFromAssemblyName(dllAssemblyNameObj);

                        // 4. 从 DLL 反射读取权限声明（物化为数组，避免多次枚举延迟迭代器）
                        var declaredPermissions = assembly
                            .GetCustomAttributes<PluginPermissionAttribute>()
                            .Select(a => a.Permission)
                            .ToArray();

                        // 7. 查找 IToolPlugin 实现（严格校验：只允许一个）
                        var pluginTypes = assembly
                            .GetExportedTypes()
                            .Where(t => !t.IsAbstract && typeof(IToolPlugin).IsAssignableFrom(t))
                            .ToList();

                        switch (pluginTypes.Count)
                        {
                            case 0:
                                ctx.Dispose();
                                // 依赖 DLL，不是插件，静默跳过
                                continue;
                            case > 1:
                                CoreLogger.PluginLoadFailed(
                                    logger.Safe(),
                                    dllPath,
                                    "Multiple IToolPlugin implementations: "
                                        + string.Join(", ", pluginTypes.Select(t => t.FullName))
                                );
                                ctx.Dispose();
                                continue;
                        }

                        var plugin = (IToolPlugin)Activator.CreateInstance(pluginTypes[0])!;

                        // 5. 不一致性检测（警告，不阻断）
                        var jsonPerms = new HashSet<string>(
                            manifest.Permissions,
                            StringComparer.OrdinalIgnoreCase
                        );
                        var attrPerms = new HashSet<string>(
                            declaredPermissions,
                            StringComparer.OrdinalIgnoreCase
                        );
                        if (!jsonPerms.SetEquals(attrPerms))
                        {
                            CoreLogger.PluginManifestMismatch(
                                logger.Safe(),
                                manifest.Id,
                                string.Join(",", attrPerms.OrderBy(p => p)),
                                string.Join(",", jsonPerms.OrderBy(p => p))
                            );
                        }

                        // 6. 根据 Trust 级别裁剪权限
                        var effectivePermissions = trust switch
                        {
                            PluginTrustLevel.Builtin => declaredPermissions,
                            PluginTrustLevel.Verified => declaredPermissions,
                            PluginTrustLevel.External => declaredPermissions.Where(
                                PluginPermissions.ExternalAllowed.Contains
                            ),
                            _ => [],
                        };

                        // 8. 根据 Trust 级别构建 ServiceProvider
                        var sp =
                            trust == PluginTrustLevel.Builtin
                                ? trustedSp
                                : new RestrictedPluginServiceProvider(
                                    trustedSp,
                                    PluginCapabilities.ResolveAllowedServices(effectivePermissions)
                                );

                        // 9. 初始化插件
                        plugin.Initialize(sp);
                        newPlugins.Add((ctx, plugin, manifest, trust, subDir));

                        // 10. 注册工具（用 manifest.Name 分组）
                        foreach (var tool in plugin.GetTools())
                            registry.Register(tool, manifest.Name);
                    }
                    catch (Exception ex)
                    {
                        CoreLogger.PluginLoadFailed(logger.Safe(), dllPath, ex.Message);
                        ctx?.Dispose();
                    }
                }
            }
        }

        lock (_lock)
        {
            _loaded.AddRange(newPlugins);
            _loadedVersion++;
        }
    }

    /// <summary>
    /// 按插件 ID 卸载单个插件：先从 ToolRegistry 移除工具，再卸载 ALC。
    /// </summary>
    /// <param name="pluginId">要卸载的插件 ID（目录名/权威 ID）。</param>
    /// <param name="registry">工具注册表，用于移除该插件注册的工具。</param>
    /// <returns>是否成功卸载。</returns>
    public bool UnloadPlugin(string pluginId, ToolRegistry registry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PluginLoadContext? ctxToRemove = null;
        string? pluginName = null;

        lock (_lock)
        {
            var idx = _loaded.FindIndex(p =>
                string.Equals(p.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase)
            );
            if (idx < 0)
                return false;

            var entry = _loaded[idx];
            ctxToRemove = entry.Ctx;
            pluginName = entry.Manifest.Name;
            _loaded.RemoveAt(idx);
            _loadedVersion++;
        }

        // 先从 ToolRegistry 移除工具（释放 AgentTool 引用，使 ALC 可被 GC 回收）
        if (pluginName is not null)
            registry.UnregisterByPlugin(pluginName);

        // 再 Dispose ALC（取消 Default.Resolving 订阅 + Unload）
        try
        {
            ctxToRemove?.Dispose();
            CoreLogger.PluginUnloaded(logger.Safe(), pluginId);
        }
        catch (Exception ex)
        {
            CoreLogger.PluginUnloadFailed(logger.Safe(), pluginId, ex.Message);
        }

        OnPluginUnloaded?.Invoke(pluginId);

        return true;
    }

    /// <summary>
    /// 扫描插件目录，返回所有找到的插件清单及其信任级别。
    /// </summary>
    public static List<(
        PluginManifest Manifest,
        string SubDir,
        PluginTrustLevel Trust
    )> ScanPluginDirs(
        IEnumerable<(string Directory, PluginTrustLevel Trust)> pluginDirs,
        string? trustedPublicKeyToken = null
    )
    {
        var builtinDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        var trustedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (trustedPublicKeyToken is not null)
            trustedTokens.Add(trustedPublicKeyToken);

        var result = new List<(PluginManifest, string, PluginTrustLevel)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (dir, _) in pluginDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var manifest = PluginManifest.LoadFromDirectory(subDir);
                if (manifest is null)
                    continue;

                // 权威 Id = 目录名（不可篡改），覆盖 JSON 中可能被篡改的 id
                var authoritativeId = Path.GetFileName(subDir);
                if (
                    !string.Equals(manifest.Id, authoritativeId, StringComparison.OrdinalIgnoreCase)
                )
                    manifest.Id = authoritativeId;

                if (!seenIds.Add(authoritativeId))
                    continue;

                // 先用目录默认级别，DLL 签名可升级为 Verified
                var trust = PluginTrustLevel.External;
                var dlls = Directory.GetFiles(subDir, "*.dll");
                if (dlls.Length > 0)
                    trust = ResolveTrust(subDir, dlls[0], builtinDir, trustedTokens);

                result.Add((manifest, subDir, trust));
            }
        }

        return result;
    }

    /// <summary>
    /// 信任级别判定：Builtin（目录位置）→ Verified（DLL 签名白名单）→ External。
    /// </summary>
    private static PluginTrustLevel ResolveTrust(
        string subDir,
        string dllPath,
        string builtinDir,
        HashSet<string> trustedTokens
    )
    {
        // Builtin: 目录在主程序 plugins/ 下
        if (subDir.StartsWith(builtinDir, StringComparison.OrdinalIgnoreCase))
            return PluginTrustLevel.Builtin;

        // Verified: DLL PublicKeyToken 在白名单
        var token = ReadPublicKeyToken(dllPath);
        if (token is not null && trustedTokens.Contains(token))
            return PluginTrustLevel.Verified;

        // External: 其他一切
        return PluginTrustLevel.External;
    }

    /// <summary>
    /// 读取 DLL 的 PublicKeyToken，返回小写十六进制字符串。无签名返回 null。
    /// 使用 stackalloc + span 避免中间 byte[] 分配和 ToLowerInvariant 的字符串分配。
    /// PublicKeyToken 最多 8 字节 → 16 hex 字符，stackalloc 完全安全。
    /// </summary>
    private static string? ReadPublicKeyToken(string dllPath)
    {
        try
        {
            var name = AssemblyName.GetAssemblyName(dllPath);
            var token = name.GetPublicKeyToken();
            return token is null || token.Length == 0 ? null : HexEncodeLower(token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将字节 span 编码为小写十六进制字符串。
    /// 避免 Convert.ToHexString + ToLowerInvariant 的两次分配。
    /// </summary>
    private static string HexEncodeLower(ReadOnlySpan<byte> bytes)
    {
        // "0123456789abcdef" 查找表 — 单次 span 索引替代条件分支
        ReadOnlySpan<char> hexMap = "0123456789abcdef";

        Span<char> buffer = stackalloc char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            buffer[i * 2] = hexMap[b >> 4];
            buffer[i * 2 + 1] = hexMap[b & 0x0F];
        }

        return new string(buffer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<(
            PluginLoadContext Ctx,
            IToolPlugin,
            PluginManifest,
            PluginTrustLevel,
            string
        )> snapshot;
        lock (_lock)
        {
            snapshot = _loaded.ToList();
            _loaded.Clear();
            _loadedVersion++;
        }

        // 先从 ToolRegistry 移除所有插件工具（释放 AgentTool 引用，使 ALC 可被 GC 回收）
        if (_registry is not null)
        {
            foreach (var (_, _, manifest, _, _) in snapshot)
                _registry.UnregisterByPlugin(manifest.Name);
        }

        // 再 Dispose ALC（取消 Default.Resolving 订阅 + Unload）
        foreach (var (ctx, _, _, _, _) in snapshot)
        {
            try
            {
                ctx.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// 插件加载上下文 — 提供隔离的程序集加载环境。
    /// 优先从默认上下文加载共享程序集，避免重复加载。
    /// 使用 Dictionary 缓存共享程序集名→引用映射，替代线性扫描。
    /// 订阅 Default.Resolving 事件，确保 Assembly.Load() 能回退到插件 ALC 查找。
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly AssemblyDependencyResolver _resolver;
        private Dictionary<string, Assembly>? _sharedAssemblies;
        private Dictionary<string, Version>? _sharedAssemblyVersions;
        private readonly Func<AssemblyLoadContext, AssemblyName, Assembly?> _resolvingHandler;
        private bool _disposed;

        public PluginLoadContext(string pluginPath)
            : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
            _resolvingHandler = OnDefaultResolving;
            Default.Resolving += _resolvingHandler;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Default.Resolving -= _resolvingHandler;
            Unload();
        }

        /// <summary>
        /// Default ALC Resolving 回调 — 当 Assembly.Load() 在 Default ALC 中找不到程序集时，
        /// 回退到当前插件 ALC 的已加载程序集中查找。
        /// 只在已加载集合中查找，不触发新加载，避免递归。
        /// </summary>
        private Assembly? OnDefaultResolving(AssemblyLoadContext defaultAlc, AssemblyName name)
        {
            return name.Name is null
                ? null
                : Assemblies.FirstOrDefault(asm =>
                    string.Equals(asm.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)
                );
        }

        /// <summary>
        /// 检查程序集名是否为共享依赖，并返回 Host 中的版本。
        /// 使用与 Load() 相同的数据源（Default.Assemblies），确保一致性。
        /// </summary>
        internal bool IsSharedAssembly(string name, out Version hostVersion)
        {
            _sharedAssemblyVersions ??= Default
                .Assemblies.Where(a => a.GetName().Name is not null)
                .ToDictionary(
                    a => a.GetName().Name!,
                    a => a.GetName().Version ?? new Version(0, 0),
                    StringComparer.OrdinalIgnoreCase
                );

            return _sharedAssemblyVersions.TryGetValue(name, out hostVersion!);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // 延迟初始化共享程序集查找表
            _sharedAssemblies ??= Default
                .Assemblies.Where(a => a.GetName().Name is not null)
                .ToDictionary(a => a.GetName().Name!, StringComparer.OrdinalIgnoreCase);

            if (name.Name is not null && _sharedAssemblies.TryGetValue(name.Name, out var shared))
                return shared;

            var path = _resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
        }
    }
}

/// <summary>
/// 已加载插件信息 — 包含插件实例、清单、信任级别和来源目录。
/// Name/Version/Description 从 Manifest 读取。
/// </summary>
public sealed class LoadedPlugin(
    IToolPlugin plugin,
    PluginManifest manifest,
    PluginTrustLevel trust,
    string sourceDir
)
{
    public IToolPlugin Plugin { get; } = plugin;
    public PluginManifest Manifest { get; } = manifest;
    public PluginTrustLevel Trust { get; } = trust;
    public string SourceDir { get; } = sourceDir;

    /// <summary>插件名称 — 从 manifest 读取。</summary>
    public string Name => Manifest.Name;

    /// <summary>插件版本 — 从 manifest 读取。</summary>
    public string Version => Manifest.Version;

    /// <summary>插件描述 — 从 manifest 读取。</summary>
    public string Description => Manifest.Description;
}
