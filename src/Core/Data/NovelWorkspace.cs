using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Shared;
using Microsoft.Extensions.Configuration;

namespace Core.Data;

/// <summary>工作区条目：一个本地小说项目目录（其下每个子目录 = 一本书）。</summary>
public sealed record WorkspaceRoot(string Path, DateTime AddedAt, DateTime LastUsedAt);

/// <summary>
/// 小说项目目录工作区 —— 统一管理「多个本地 novels 目录」。
///
/// 为什么需要：TUI 从仓库根启动，<c>cwd/novels</c> 天然正确；Desktop 的 cwd 不可控
/// （<c>dotnet run --project src/Desktop</c> → src/Desktop；Finder 双击 → <c>/</c>），
/// 只能靠候选链「猜」。猜错根的后果不是列表空着这么简单——cloud-novel-sync 会把整本书
/// 同步到错误目录，且与 TUI 拉取的位置分叉。本类型把「目录」从一次性字符串升级为
/// 可增删、可持久化、跨入口共享的清单：
/// <list type="bullet">
///   <item><description><see cref="Roots"/>：读取范围（项目列表 / 索引 / 同步发现 = 各根并集）</description></item>
///   <item><description><see cref="ActiveRoot"/>：写入范围（新建项目落盘处、云端 pull 落盘处）</description></item>
/// </list>
/// 清单持久化在 <c>~/.mythology/workspaces.json</c>，TUI 与 Desktop 读写同一份 → 两端列表一致；
/// 变更触发 <see cref="Changed"/>，消费方（FileNovelStore / FileSessionStore / NovelSemanticService）
/// 据此清目录索引，无需重启进程。
/// </summary>
public sealed class NovelWorkspace
{
    /// <summary>环境变量覆盖（与 --novels 同义，供 CI / 脚本使用）。</summary>
    public const string EnvVar = "MYTHOLOGY_NOVELS_PATH";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly Lock _gate = new();
    private readonly string? _persistPath; // null = 不落盘（Single / 测试）
    private readonly List<WorkspaceRoot> _roots = [];
    private string _activeRoot = string.Empty;

    /// <summary>清单变化（新增/移除/切换活动目录）→ 消费方清目录索引。</summary>
    public event Action? Changed;

    /// <summary>
    /// 加载（或新建）工作区清单。
    /// </summary>
    /// <param name="mythologyDir">数据目录，缺省 ~/.mythology。</param>
    /// <param name="seedRoots">启动时并入清单的目录（候选链发现的项目目录）。</param>
    /// <param name="activeRoot">活动（写入）目录；为空则沿用清单里最近使用者。</param>
    public NovelWorkspace(
        string? mythologyDir = null,
        IEnumerable<string>? seedRoots = null,
        string? activeRoot = null
    )
        : this(
            Path.Combine(
                mythologyDir
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        MythologyPaths.ConfigDirName
                    ),
                "workspaces.json"
            )
        )
    {
        foreach (var entry in ReadFile(_persistPath))
            _roots.Add(entry);
        foreach (var seed in seedRoots ?? [])
            UpsertSeed(seed);

        _activeRoot =
            PickActive(_persistPath, Normalize(activeRoot))
            ?? MostRecentlyUsed()?.Path
            ?? DefaultNovelsRoot();

        // 不变量：写入目录必须被登记，否则并集扫描看不到落在它里面的新书
        if (_roots.All(r => r.Path != _activeRoot))
        {
            Directory.CreateDirectory(_activeRoot);
            UpsertSeed(_activeRoot);
        }
    }

    private NovelWorkspace(string? persistPath) => _persistPath = persistPath;

    /// <summary>单目录工作区（不落盘）：测试与「显式指定一个目录」的退化形态。</summary>
    public static NovelWorkspace Single(string novelsRoot)
    {
        var workspace = new NovelWorkspace(persistPath: null)
        {
            _activeRoot = Path.GetFullPath(novelsRoot),
            HasSpecifiedRoot = true, // 调用方显式给定的目录
        };
        workspace._roots.Add(
            new WorkspaceRoot(workspace._activeRoot, DateTime.UtcNow, DateTime.UtcNow)
        );
        return workspace;
    }

    /// <summary>读取范围：全部已登记目录（按最近使用倒序）。</summary>
    public IReadOnlyList<string> Roots
    {
        get
        {
            lock (_gate)
                return [.. _roots.OrderByDescending(r => r.LastUsedAt).Select(r => r.Path)];
        }
    }

    /// <summary>写入范围：新建项目 / 云端拉取的落盘目录。</summary>
    public string ActiveRoot
    {
        get
        {
            lock (_gate)
                return _activeRoot;
        }
    }

    /// <summary>
    /// 根是否「被指定过」：--novels / 环境变量 / 配置 / 清单里的活动目录（用户在 UI 选过），
    /// 或猜出的目录里已有真实书籍（TUI 的 cwd 语义：cd 到仓库根就是指定）。
    /// 云端同步据此守护：猜出来的空目录（Desktop 打包后回退 ~/.mythology/novels）不许落云端数据。
    /// </summary>
    public bool HasSpecifiedRoot { get; private set; }

    /// <summary>登记目录（已存在则仅刷新清单）。返回是否新增。不创建目录、不删除任何文件。</summary>
    public bool Add(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return false;

        bool added;
        lock (_gate)
        {
            added = UpsertSeed(normalized);
            if (added)
                SaveLocked();
        }
        if (added)
            Changed?.Invoke();
        return added;
    }

    /// <summary>把目录设为活动（写入）目录；未登记则先登记。切换后新建项目/拉取落到该目录。</summary>
    public void SetActive(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return;

        lock (_gate)
        {
            UpsertSeed(normalized);
            _activeRoot = normalized;
            TouchLocked(normalized);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 从清单移除目录（只移出列表，磁盘文件一律不动）。活动目录被移除时自动切到最近使用的剩余目录；
    /// 清单清空时活动目录退回默认候选（不阻断启动）。
    /// </summary>
    public bool Remove(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return false;

        bool removed;
        lock (_gate)
        {
            removed = _roots.RemoveAll(r => r.Path == normalized) > 0;
            if (!removed)
                return false;
            if (_activeRoot == normalized)
                _activeRoot = MostRecentlyUsed()?.Path ?? normalized;
            SaveLocked();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>打开某本书时记录其所在目录为最近使用（不改活动目录 → 不打断当前写入目标）。</summary>
    public void Touch(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return;

        lock (_gate)
        {
            if (!UpsertSeed(normalized))
                return;
            TouchLocked(normalized);
            SaveLocked();
        }
    }

    /// <summary>
    /// 全部已登记目录下的小说目录（含 novel.yaml 的子目录）。ActiveRoot 排最前 →
    /// 同一本书在两个目录里都出现时以写入目录为准。读取范围 = 清单并集，即「统一的项目列表」。
    /// </summary>
    public IReadOnlyList<string> EnumerateNovelDirectories()
    {
        var seen = new HashSet<string>(PathComparer.Instance);
        var result = new List<string>();
        foreach (var root in OrderedRootsWithActiveFirst())
        {
            foreach (var dir in SafeEnumerateDirectories(root))
            {
                if (
                    File.Exists(Path.Combine(dir, MythologyPaths.NovelMetaFileName))
                    && seen.Add(dir)
                )
                    result.Add(dir);
            }
        }
        return result;
    }

    /// <summary>Roots 快照，ActiveRoot 提前（写入目录优先于「最近使用」排序）。</summary>
    private List<string> OrderedRootsWithActiveFirst()
    {
        lock (_gate)
            return [.. _roots.Select(r => r.Path).OrderByDescending(p => p == _activeRoot)];
    }

    /// <summary>某本书所在的项目目录（在全部 Roots 下找 novel.yaml 的 id）；找不到返回 null。</summary>
    public string? FindRootForNovel(string novelId)
    {
        if (string.IsNullOrEmpty(novelId))
            return null;
        foreach (var root in Roots)
        {
            foreach (var dir in SafeEnumerateDirectories(root))
            {
                var yaml = Path.Combine(dir, MythologyPaths.NovelMetaFileName);
                if (!File.Exists(yaml))
                    continue;
                try
                {
                    var dict = YamlHelper.Deserialize(File.ReadAllText(yaml));
                    if (
                        dict.TryGetValue("id", out var v)
                        && v is string id
                        && string.Equals(id, novelId, StringComparison.Ordinal)
                    )
                        return root;
                }
                catch
                {
                    // 损坏的 novel.yaml 不参与归属判定
                }
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════
    //  启动解析（TUI / Host / Desktop 共用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 解析启动根目录。优先级（高→低）：
    /// 1. <paramref name="explicitPath"/>（--novels，即使目录不存在也照办——用户说了算）
    /// 2. 环境变量 <c>MYTHOLOGY_NOVELS_PATH</c>
    /// 3. 配置 <c>Novels:Path</c>（appsettings.json）
    /// 4. 工作区清单的活动目录（上次在 UI 里指定的）
    /// 5. cwd/novels（TUI 语义：从仓库根启动即命中）
    /// 6. 从可执行文件上溯 6 级找仓库内 novels/（开发布局 bin/…/Desktop/src → 仓库根）
    /// 7. 可执行文件旁 novels/（发布布局）
    /// 8. ~/.mythology/novels
    /// 4~8 的「猜」遵循同一条规则：优先存在且有内容，空的遗留目录不得遮蔽真实数据。
    /// </summary>
    /// <summary>解析结果：<see cref="Path"/> 为落点；<see cref="Specified"/> 表示「用户指定过」
    /// （显式参数/环境变量/配置/清单活动目录），或猜出的目录里已有真实书籍（TUI 的 cwd 语义）。</summary>
    public sealed record ResolvedRoot(string Path, bool Specified);

    public static ResolvedRoot ResolveNovelsRoot(
        IConfiguration? config,
        string? explicitPath = null
    )
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return new(Path.GetFullPath(explicitPath), Specified: true);
        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return new(Path.GetFullPath(env), Specified: true);
        var configured = config?[ConfigurationKeys.NovelsPath];
        if (!string.IsNullOrWhiteSpace(configured))
            return new(Path.GetFullPath(configured), Specified: true);

        // 清单活动目录优先于 cwd：用户在 UI 里指定过，就不该被启动位置覆盖
        var saved = ReadActive(DefaultPersistPath());
        if (saved is not null && HasContent(saved))
            return new(saved, Specified: true);

        var picked = SelectRoot(DiscoverCandidateRoots()) ?? DefaultNovelsRoot();
        // 猜出来的目录：只有当里面已有真实书籍才视为「指定过」（空壳不许接云端数据）
        return new(picked, Specified: IsSpecifiedRoot(picked));
    }

    /// <summary>
    /// 候选选择规则：「存在且有内容」优先——空的遗留目录（如 src/novels、src/Desktop/novels 空壳）
    /// 不得遮蔽真实数据目录；全部无内容时退回第一个存在的目录；全都不存在返回 null。
    /// </summary>
    public static string? SelectRoot(IEnumerable<string> candidates)
    {
        var list = candidates as IReadOnlyList<string> ?? candidates.ToList();
        foreach (var candidate in list)
            if (HasContent(candidate))
                return candidate;
        foreach (var candidate in list)
            if (Directory.Exists(candidate))
                return candidate;
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>候选目录链（不含显式指定/环境变量/配置覆盖），用于给工作区清单播种。</summary>
    public static IReadOnlyList<string> DiscoverCandidateRoots()
    {
        var candidates = new List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), MythologyPaths.NovelsSubDir),
        };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, MythologyPaths.NovelsSubDir));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, MythologyPaths.NovelsSubDir));
        candidates.Add(DefaultNovelsRoot());
        return [.. candidates.Select(Normalize).Where(p => p is not null).Select(p => p!)];
    }

    /// <summary>
    /// 打开工作区：按 <see cref="ResolveNovelsRoot"/> 定活动目录，并把候选链上「存在且有内容」
    /// 的目录并入清单 → 两端（TUI/Desktop）看到同一份项目目录列表。
    /// </summary>
    public static NovelWorkspace Open(IConfiguration? config, string? explicitPath = null)
    {
        var resolved = ResolveNovelsRoot(config, explicitPath);
        var seeds = DiscoverCandidateRoots().Where(HasContent).Append(resolved.Path);
        return new NovelWorkspace(mythologyDir: null, seedRoots: seeds, activeRoot: resolved.Path)
        {
            HasSpecifiedRoot = resolved.Specified,
        };
    }

    /// <summary>
    /// 猜出来的目录是否算「已被指定」：里面已有一本可识别的书（子目录含 novel.yaml）。
    /// 空目录不算——空壳（打包回退 ~/.mythology/novels、src/Desktop/novels 遗留壳）不许接云端数据。
    /// </summary>
    public static bool IsSpecifiedRoot(string root) =>
        SafeEnumerateDirectories(root)
            .Any(dir => File.Exists(Path.Combine(dir, MythologyPaths.NovelMetaFileName)));

    // ═══════════════════════════════════════════════════════════
    //  内部
    // ═══════════════════════════════════════════════════════════

    private static string DefaultNovelsRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            MythologyPaths.ConfigDirName,
            MythologyPaths.NovelsSubDir
        );

    private static string? DefaultPersistPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            MythologyPaths.ConfigDirName,
            "workspaces.json"
        );

    /// <summary>规范化：绝对路径 + 去掉尾分隔符；非法/空返回 null。不要求目录已存在。</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(
                path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            );
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>活动目录取值：显式指定（须在清单中）→ 清单已存值（须存在）。</summary>
    private static string? PickActive(string? path, string? explicitActive)
    {
        if (explicitActive is not null)
            return explicitActive;
        var saved = ReadActive(path);
        return saved is not null && Directory.Exists(saved) ? saved : null;
    }

    private static bool HasContent(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>登记（不保存）：返回是否新增。仅接受真实存在的目录，避免清单积累死路径。</summary>
    private bool UpsertSeed(string normalizedPath)
    {
        if (!Directory.Exists(normalizedPath))
            return false;
        var existing = _roots.FindIndex(r => r.Path == normalizedPath);
        var now = DateTime.UtcNow;
        if (existing >= 0)
            return false;
        _roots.Add(new WorkspaceRoot(normalizedPath, now, now));
        return true;
    }

    private void TouchLocked(string normalizedPath)
    {
        var index = _roots.FindIndex(r => r.Path == normalizedPath);
        if (index < 0)
            return;
        var root = _roots[index];
        _roots[index] = root with { LastUsedAt = DateTime.UtcNow };
    }

    private WorkspaceRoot? MostRecentlyUsed() =>
        _roots.OrderByDescending(r => r.LastUsedAt).FirstOrDefault();

    private void SaveLocked()
    {
        if (_persistPath is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
            AtomicFile.WriteAllText(
                _persistPath,
                JsonSerializer.Serialize(new WorkspaceFile(_roots, _activeRoot), Json)
            );
        }
        catch (Exception)
        {
            // 清单写盘失败不影响本次会话（内存态仍然正确），下次启动退回候选链
        }
    }

    private sealed record WorkspaceFile(List<WorkspaceRoot> Roots, string? ActiveRoot);

    private static List<WorkspaceRoot> ReadFile(string? path)
    {
        if (path is null || !File.Exists(path))
            return [];
        try
        {
            var file = JsonSerializer.Deserialize<WorkspaceFile>(
                AtomicFile.ReadAllText(path),
                Json
            );
            return file?.Roots is null
                ? []
                :
                [
                    .. file
                        .Roots.Select(r => r with { Path = Normalize(r.Path) ?? "" })
                        .Where(r => r.Path.Length > 0 && Directory.Exists(r.Path)),
                ];
        }
        catch (Exception)
        {
            return []; // 损坏 → 按空清单处理（候选链兜底）
        }
    }

    private static string? ReadActive(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            var file = JsonSerializer.Deserialize<WorkspaceFile>(
                AtomicFile.ReadAllText(path),
                Json
            );
            return Normalize(file?.ActiveRoot);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>目录路径比较：Windows 大小写不敏感、Unix 敏感（跟随 OS 文件系统的真实语义）。</summary>
internal sealed class PathComparer : IEqualityComparer<string>
{
    public static readonly PathComparer Instance = new();

    private static readonly StringComparer Inner = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public bool Equals(string? x, string? y) => Inner.Equals(x, y);

    public int GetHashCode(string obj) => Inner.GetHashCode(obj);
}
