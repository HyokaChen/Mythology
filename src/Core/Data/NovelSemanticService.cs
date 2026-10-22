using System.Threading.Channels;
using Core.Shared;
using Tacos;

namespace Core.Data;

/// <summary>
/// 方案 2B 的读旁路服务：负责把磁盘事实灌入 <see cref="NovelSemanticDb"/>，
/// 并在每次查询前执行通道 1（mtime 探测）作为正确性地板。
///
/// Phase 1 本服务不接管 <see cref="FileNovelStore"/> 的任何现有路径；
/// Phase 2 由 AgentAuditLoop 改为消费本服务暴露的 L3 聚合值。
/// </summary>
public sealed class NovelSemanticService : IDisposable
{
    private readonly NovelWorkspace _workspace;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _novelDirs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _entityPaths = new(StringComparer.Ordinal);
    private readonly Channel<Action> _writerChannel = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
    );
    private readonly Task _writerTask;
    private bool _disposed;
    private bool _directoriesScanned;

    /// <summary>单目录构造（测试/兼容既有调用方）。</summary>
    public NovelSemanticService(string novelsRoot)
        : this(NovelWorkspace.Single(novelsRoot)) { }

    /// <summary>工作区构造：语义索引覆盖全部已登记项目目录；清单变化时强制重扫。</summary>
    public NovelSemanticService(NovelWorkspace workspace)
    {
        _workspace = workspace;
        _writerTask = Task.Run(ProcessWriterAsync);
        workspace.Changed += InvalidateDirectories;
    }

    /// <summary>项目目录清单变化 → 下次读取按新清单重扫。</summary>
    private void InvalidateDirectories()
    {
        lock (_gate)
            _directoriesScanned = false;
    }

    /// <summary>
    /// 单写者队列：把“文件已变更”投递给后台写者，避免在保存调用栈内直接 Set Input。
    /// 正确性仍由每次读前的 mtime 探测兜底，队列只负责低延迟失效。
    /// </summary>
    public void NotifyNovelChanged(string novelId)
    {
        if (string.IsNullOrEmpty(novelId))
            return;

        _writerChannel.Writer.TryWrite(() =>
        {
            lock (_gate)
            {
                ProbeNovelCore(novelId);
            }
        });
    }

    private async Task ProcessWriterAsync()
    {
        await foreach (var action in _writerChannel.Reader.ReadAllAsync())
        {
            try
            {
                action();
            }
            catch
            {
                // 写者队列中的单次刷新失败不影响后续事件。
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writerChannel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 退出时不需要因为后台刷新而阻塞。
        }
    }

    /// <summary>底层 Tacos 数据库，供埋点/观察者/统计使用。</summary>
    public NovelSemanticDb Db { get; } = new();

    /// <summary>订阅 Tacos 事件，用于验证 early cutoff。</summary>
    public IDisposable SubscribeEvents(ITacosObserver observer) => Db.SubscribeEvents(observer);

    // ──────────────────────────────────────────────────────
    //  Public query API
    // ──────────────────────────────────────────────────────

    public int DraftChapterCount(string novelId)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            return Db.DraftChapterCount(novelId);
        }
    }

    public int OrphanCharacterCount(string novelId)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            return Db.OrphanCharacterCount(novelId);
        }
    }

    public int OverdueForeshadowCount(string novelId)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            return Db.OverdueForeshadowCount(novelId);
        }
    }

    public int TotalWordCount(string novelId)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            return Db.TotalWordCount(novelId);
        }
    }

    public string[] ChapterIds(string novelId)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            return Db.ChapterIds(novelId);
        }
    }

    public string[] CharacterIds(string novelId)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            return Db.CharacterIds(novelId);
        }
    }

    public string[] PlotlineIds(string novelId)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            return Db.PlotlineIds(novelId);
        }
    }

    public string[] ConnectedCharacterIds(string novelId)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            return Db.ConnectedCharacterIds(novelId);
        }
    }

    // ── Phase 3 门面接入 API ──

    /// <summary>取得某实体 id 对应的原始文件文本（O(1) 经 id → InputId 映射）。</summary>
    public bool TryGetEntityText(
        string entityId,
        out string text,
        out string novelId,
        out string kind
    )
    {
        lock (_gate)
        {
            ProbeAllCore();
            if (
                Db.TryGetEntityFile(
                    entityId,
                    out var source,
                    out var foundNovelId,
                    out var foundKind
                )
            )
            {
                text = Db.GetSourceFileIdText(source);
                novelId = foundNovelId;
                kind = foundKind;
                return true;
            }

            text = "";
            novelId = "";
            kind = "";
            return false;
        }
    }

    /// <summary>在指定小说内按 id 取实体文件路径（供 Save 判断 Create/Update 与改名删旧文件）。</summary>
    public bool TryGetEntityPath(string novelId, string entityId, out string fullPath)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            if (_entityPaths.TryGetValue(entityId, out var path))
            {
                fullPath = path;
                return true;
            }

            fullPath = "";
            return false;
        }
    }

    /// <summary>在指定小说内按 id 取实体文本（避免全局 ProbeAll）。</summary>
    public bool TryGetEntityTextInNovel(
        string novelId,
        string entityId,
        out string text,
        out string kind
    )
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            if (
                Db.TryGetEntityFile(
                    entityId,
                    out var source,
                    out var foundNovelId,
                    out var foundKind
                )
                && foundNovelId == novelId
            )
            {
                text = Db.GetSourceFileIdText(source);
                kind = foundKind;
                return true;
            }

            text = "";
            kind = "";
            return false;
        }
    }

    /// <summary>取得某小说某 kind 目录的当前文件名单（来自 DirListing Input）。</summary>
    public string[] GetFileNames(string novelId, string kind)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            var dirId = Db.GetDirListingId(novelId, kind);
            return dirId is null ? [] : Db.GetDirListingIdSortedFileNames(dirId.Value);
        }
    }

    /// <summary>取得某小说某 kind 目录中指定文件的原始文本。</summary>
    public bool TryGetSourceText(string novelId, string kind, string fileName, out string text)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            var sourceId = Db.GetSourceFileId(novelId, fileName);
            if (sourceId is not null)
            {
                text = Db.GetSourceFileIdText(sourceId.Value);
                return true;
            }

            text = "";
            return false;
        }
    }

    /// <summary>取得某小说的 relationships.yaml 原始文本。</summary>
    public bool TryGetRelationshipText(string novelId, out string text)
    {
        lock (_gate)
        {
            ProbeNovelCore(novelId);
            var relId = Db.GetRelationshipFileId(novelId);
            if (relId is not null)
            {
                text = Db.GetSourceFileIdText(relId.Value);
                return true;
            }

            text = "";
            return false;
        }
    }

    /// <summary>启动/切换小说时做一次全量对账并预热冷路径。</summary>
    public void ProbeAll()
    {
        lock (_gate)
        {
            ProbeAllCore();
        }
    }

    private void ProbeAllCore()
    {
        EnsureDirectoriesScannedLocked();
        foreach (var novelId in _novelDirs.Keys.ToArray())
            ProbeNovelCore(novelId);
    }

    /// <summary>切换小说时对旧小说做缓存回收（本服务先提供清空实现，后续可细化到 TrimCaches）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _directoriesScanned = false;
            _novelDirs.Clear();
            _entityPaths.Clear();
            Db.ClearNovelState();
        }
    }

    /// <summary>切换小说时清空旧缓存并预热新小说（方案 2B 的回收时机）。</summary>
    public void SwitchNovel(string novelId)
    {
        lock (_gate)
        {
            Db.ClearCaches();
            ProbeNovelCore(novelId);
        }
    }

    /// <summary>清理超过指定 revision 数未使用的查询缓存条目。</summary>
    public int TrimCaches(int maxIdleRevisions)
    {
        lock (_gate)
        {
            return Db.TrimCaches(maxIdleRevisions);
        }
    }

    /// <summary>获取 Tacos 缓存统计（用于长跑内存监控）。</summary>
    public TacosCacheStatistics GetCacheStatistics() => Db.GetCacheStatistics();

    // ──────────────────────────────────────────────────────
    //  Implementation
    // ──────────────────────────────────────────────────────

    private void EnsureDirectoriesScannedLocked(bool force = false)
    {
        if (_directoriesScanned && !force)
            return;

        _novelDirs.Clear();
        {
            foreach (var dir in _workspace.EnumerateNovelDirectories())
            {
                var yamlPath = Path.Combine(dir, "novel.yaml");
                if (!File.Exists(yamlPath))
                    continue;

                try
                {
                    var text = File.ReadAllText(yamlPath);
                    var dict = YamlHelper.Deserialize(text);
                    var id = StoreHelper.GetString(dict, "id");
                    // ActiveRoot 先入 → 同 id 冲突以写入目录为准
                    if (id is not null)
                        _novelDirs.TryAdd(id, dir);
                }
                catch
                {
                    // 跳过损坏的 novel.yaml
                }
            }
        }

        _directoriesScanned = true;
    }

    private void ProbeNovelCore(string novelId)
    {
        EnsureDirectoriesScannedLocked();

        if (!_novelDirs.TryGetValue(novelId, out var dir))
        {
            // 运行期可能出现新建小说目录；第一次查询未知 novelId 时强制重新扫描。
            EnsureDirectoriesScannedLocked(force: true);
            if (!_novelDirs.TryGetValue(novelId, out dir))
                return;
        }

        Db.RegisterNovel(novelId);

        // 通道 1：枚举 + stat，比对 (名单, mtime, size)；有差异才更新 Input。
        ProbeDirListingCore(novelId, dir, "chapters", "chapters", "*.md");
        ProbeDirListingCore(novelId, dir, "characters", "characters", "*.yaml");
        ProbeDirListingCore(novelId, dir, "plotlines", "plotlines", "*.yaml");
        ProbeDirListingCore(novelId, dir, "factions", "factions", "*.yaml");
        ProbeDirListingCore(novelId, dir, "locations", "locations", "*.yaml");
        ProbeDirListingCore(novelId, dir, "items", "items", "*.yaml");
        ProbeDirListingCore(novelId, dir, "scenes", "scenes", "*.yaml");
        ProbeRelationshipFileCore(novelId, dir);
    }

    private void ProbeDirListingCore(
        string novelId,
        string novelDir,
        string kind,
        string subDir,
        string pattern
    )
    {
        var fullDir = Path.Combine(novelDir, subDir);
        var files = Array.Empty<string>();
        if (Directory.Exists(fullDir))
        {
            files = Directory
                .GetFiles(fullDir, pattern)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        var existingDir = Db.GetDirListingId(novelId, kind);
        InputId<DirListing> dirListingId;
        if (existingDir is null)
        {
            dirListingId = Db.CreateDirListingId(novelId, kind, files);
            Db.RegisterDirListing(novelId, kind, dirListingId);
        }
        else
        {
            dirListingId = existingDir.Value;
            if (
                !StringArrayComparer.Instance.Equals(
                    Db.GetDirListingIdSortedFileNames(dirListingId),
                    files
                )
            )
                Db.SetDirListingIdSortedFileNames(dirListingId, files);
        }

        foreach (var fileName in files)
        {
            var fullPath = Path.Combine(fullDir, fileName);
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                    continue;

                var mtime = info.LastWriteTimeUtc.Ticks;
                var size = info.Length;
                InputId<SourceFile> sourceId;
                var maybeExisting = Db.GetSourceFileId(novelId, fileName);
                if (maybeExisting is null)
                {
                    var text = File.ReadAllText(fullPath);
                    sourceId = Db.CreateSourceFileId(fullPath, mtime, size, text);
                    Db.RegisterSourceFile(novelId, fileName, fullPath, sourceId);
                }
                else
                {
                    sourceId = maybeExisting.Value;
                    var currentMtime = Db.GetSourceFileIdMtime(sourceId);
                    var currentSize = Db.GetSourceFileIdSize(sourceId);
                    if (currentMtime != mtime || currentSize != size)
                    {
                        var text = File.ReadAllText(fullPath);
                        Db.SetSourceFileIdPath(sourceId, fullPath);
                        Db.SetSourceFileIdMtime(sourceId, mtime);
                        Db.SetSourceFileIdSize(sourceId, size);
                        Db.SetSourceFileIdText(sourceId, text);
                    }
                }

                RegisterEntityIdFromText(novelId, kind, fileName, fullPath, sourceId);
            }
            catch
            {
                // 单个文件读取失败不阻断整个探测；下次 mtime 变化会再次尝试。
            }
        }
    }

    private void RegisterEntityIdFromText(
        string novelId,
        string kind,
        string fileName,
        string fullPath,
        InputId<SourceFile> sourceId
    )
    {
        try
        {
            var text = Db.GetSourceFileIdText(sourceId);
            var id = kind switch
            {
                "chapters" => MarkdownHelper.ParseMarkdown(text).Item1.GetValueOrDefault("id"),
                _ => StoreHelper.GetString(YamlHelper.Deserialize(text), "id"),
            };
            if (!string.IsNullOrEmpty(id))
            {
                Db.RegisterEntityFile(id!, sourceId, novelId, kind);
                _entityPaths[id!] = fullPath;
            }
        }
        catch
        {
            // 单文件解析失败不影响目录探测；后续查询会再次尝试。
        }
    }

    private void ProbeRelationshipFileCore(string novelId, string novelDir)
    {
        var relPath = Path.Combine(novelDir, "relationships.yaml");
        if (!File.Exists(relPath))
            return;

        try
        {
            var info = new FileInfo(relPath);
            var mtime = info.LastWriteTimeUtc.Ticks;
            var size = info.Length;
            const string fileName = "relationships.yaml";
            var maybeExisting = Db.GetSourceFileId(novelId, fileName);
            if (maybeExisting is null)
            {
                var text = File.ReadAllText(relPath);
                var newId = Db.CreateSourceFileId(relPath, mtime, size, text);
                Db.RegisterSourceFile(novelId, fileName, relPath, newId);
                Db.RegisterRelationshipFile(novelId, newId);
            }
            else
            {
                var existing = maybeExisting.Value;
                var currentMtime = Db.GetSourceFileIdMtime(existing);
                var currentSize = Db.GetSourceFileIdSize(existing);
                if (currentMtime != mtime || currentSize != size)
                {
                    var text = File.ReadAllText(relPath);
                    Db.SetSourceFileIdPath(existing, relPath);
                    Db.SetSourceFileIdMtime(existing, mtime);
                    Db.SetSourceFileIdSize(existing, size);
                    Db.SetSourceFileIdText(existing, text);
                }
                Db.RegisterRelationshipFile(novelId, existing);
            }
        }
        catch
        {
            // 关系文件损坏时保持上次可用状态，下次探测再试。
        }
    }
}
