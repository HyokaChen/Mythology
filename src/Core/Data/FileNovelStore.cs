using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Core.Evolution;
using Core.Novels;
using Core.Shared;

namespace Core.Data;

/// <summary>方案 2B 的门面读取模式。</summary>
public enum NovelStoreMode
{
    /// <summary>完全走原有直读实现。</summary>
    Direct,

    /// <summary>读方法走 Tacos 同步内核。</summary>
    Incremental,

    /// <summary>双跑直读与 Tacos，并记录差异（灰度验证）。</summary>
    Shadow,
}

/// <summary>
/// 基于 YAML/MD 文件的数据存储实现。
/// 创作资产存储在项目目录，配置存储在 config.yaml。
/// 会话存储已迁移到 FileSessionStore。
/// </summary>
public sealed class FileNovelStore : INovelStore
{
    private readonly NovelWorkspace _workspace;
    private readonly string _sessionsDir;
    private NovelSemanticService? _semantic;

    /// <summary>方案 2B 的门面读取模式；默认 direct 保持既有行为。</summary>
    public NovelStoreMode StoreMode { get; set; } = NovelStoreMode.Direct;

    /// <summary>Shadow 模式检测到直读与 Tacos 结果不一致时触发。</summary>
    public event Action<string, string?, string?>? ShadowDifference;

    /// <summary>
    /// novelId → 目录路径 索引，避免 FindNovelDirById 每次遍历所有目录读 novel.yaml。
    /// 启动后首次调用时懒构建，SaveNovel/DeleteNovel 时增量更新。
    /// </summary>
    private ConcurrentDictionary<string, string>? _novelDirIndex;

    /// <summary>
    /// 实体变更事件。所有 Save*/Delete* 方法在成功写入文件后触发。
    /// </summary>
    public event Func<EntityChangeEvent, Task>? OnEntityChanged;

    /// <summary>
    /// 结构索引缓存：novelId → (目录戳, 快照)。戳由 novel.yaml / volumes/*.yaml / chapters/*.md 的
    /// （文件名, 长度, 修改时间）聚合而成——任何增删改（含改序换文件名）都会失配并重建。
    /// </summary>
    private readonly ConcurrentDictionary<
        string,
        (string Stamp, NovelStructure Value)
    > _structureCache = new(StringComparer.Ordinal);

    // ═══════════════════════════════════════════════════════════
    //  Session — moved to FileSessionStore (injected separately)
    // ═══════════════════════════════════════════════════════════

    /// <summary>单目录构造（测试与「只认一个目录」的调用方）：内部包成单根工作区，不落盘。</summary>
    public FileNovelStore(
        string? novelsRoot = null,
        string? mythologyDir = null,
        NovelSemanticService? semanticService = null
    )
        : this(
            NovelWorkspace.Single(
                novelsRoot ?? Path.Combine(Directory.GetCurrentDirectory(), "novels")
            ),
            mythologyDir,
            semanticService
        ) { }

    /// <summary>
    /// 工作区构造：读取范围 = 全部已登记项目目录（统一列表），写入范围 = ActiveRoot。
    /// 清单变化（UI 里添加/切换目录）时自动失效目录索引，无需重启。
    /// </summary>
    public FileNovelStore(
        NovelWorkspace workspace,
        string? mythologyDir = null,
        NovelSemanticService? semanticService = null
    )
    {
        _workspace = workspace;
        var mythDir =
            mythologyDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mythology"
            );
        _sessionsDir = Path.Combine(mythDir, "sessions");
        _semantic = semanticService;

        Directory.CreateDirectory(_workspace.ActiveRoot);
        _workspace.Changed += InvalidateNovelDirIndex;
    }

    /// <summary>当前工作区（项目目录清单）。UI 用它展示「这本书在哪个目录」。</summary>
    public NovelWorkspace Workspace => _workspace;

    /// <summary>某本书的磁盘目录（跨全部已登记目录查找）；未找到返回 null。</summary>
    public string? FindNovelDirectory(string novelId) => FindNovelDirById(novelId);

    /// <summary>清单变化 → 丢弃 novelId→目录 索引，下次读取按新清单重建。</summary>
    private void InvalidateNovelDirIndex() => _novelDirIndex = null;

    /// <summary>
    /// 方案 2B 灰度切换入口：附加 Tacos 旁路并设置读取模式。
    /// </summary>
    public void UseSemanticStore(NovelSemanticService semantic, NovelStoreMode mode)
    {
        _semantic = semantic;
        StoreMode = mode;
    }

    private void ReportShadowDifference<T>(string method, T? direct, T? incremental)
    {
        var directJson = direct is null
            ? null
            : JsonSerializer.Serialize(direct, JsonHelper.JsonOptions);
        var incrementalJson = incremental is null
            ? null
            : JsonSerializer.Serialize(incremental, JsonHelper.JsonOptions);
        if (directJson != incrementalJson)
            ShadowDifference?.Invoke(method, directJson, incrementalJson);
    }

    /// <summary>
    /// 确保 novelId→目录 索引已构建（懒加载）。
    /// </summary>
    private void EnsureNovelDirIndex()
    {
        if (_novelDirIndex is not null)
            return;

        var index = new ConcurrentDictionary<string, string>();
        foreach (var dir in _workspace.EnumerateNovelDirectories())
        {
            var yamlPath = Path.Combine(dir, "novel.yaml");
            if (!File.Exists(yamlPath))
                continue;

            try
            {
                var text = File.ReadAllText(yamlPath);
                var dict = YamlHelper.Deserialize(text);
                var id = GetString(dict, "id");
                if (id is not null)
                    index.TryAdd(id, dir); // ActiveRoot 先入 → 同 id 冲突以写入目录为准
            }
            catch
            {
                // 跳过无法解析的 novel.yaml
            }
        }
        _novelDirIndex = index;
    }

    /// <summary>
    /// 将新小说目录注册到索引（SaveNovel 创建新小说时调用）。
    /// </summary>
    private void RegisterNovelDir(string novelId, string dir)
    {
        EnsureNovelDirIndex();
        _novelDirIndex![novelId] = dir;
    }

    /// <summary>
    /// 从索引中移除小说目录（DeleteNovel 时调用）。
    /// </summary>
    private void RemoveNovelDir(string novelId)
    {
        _novelDirIndex?.TryRemove(novelId, out _);
    }

    // ═══════════════════════════════════════════════════════════
    //  Novel
    // ═══════════════════════════════════════════════════════════

    public async Task<Novel?> GetNovel(string id, CancellationToken ct = default)
    {
        var dir = FindNovelDirById(id);
        if (dir is null)
            return null;

        var yamlPath = Path.Combine(dir, "novel.yaml");
        if (!File.Exists(yamlPath))
            return null;

        var text = await File.ReadAllTextAsync(yamlPath, ct);
        var dict = YamlHelper.Deserialize(text);

        // 并行加载所有子实体，替代顺序 await
        var chaptersTask = GetChapters(id, ct);
        var charactersTask = GetCharacters(id, ct);
        var plotlinesTask = GetPlotlines(id, ct);
        var factionsTask = GetFactions(id, ct);
        var locationsTask = GetLocations(id, ct);
        var itemsTask = GetItems(id, ct);
        var timelineEventsTask = GetTimelineEvents(id, ct);
        var relationshipsTask = GetRelationshipsByNovel(id, ct);
        var scenesTask = GetScenesByNovel(id, ct);

        await Task.WhenAll(
            chaptersTask,
            charactersTask,
            plotlinesTask,
            factionsTask,
            locationsTask,
            itemsTask,
            timelineEventsTask,
            relationshipsTask,
            scenesTask
        );

        var novel = new Novel
        {
            Id = GetString(dict, "id") ?? id,
            NovelId = id,
            Title = GetString(dict, "title") ?? "",
            Genre = GetString(dict, "genre") ?? "",
            Author = GetString(dict, "author") ?? "",
            TargetWords = GetInt(dict, "target-words"),
            Description = GetString(dict, "description") ?? "",
            WritingStyle = GetString(dict, "writing-style") ?? "",
            TargetAudience = GetString(dict, "target-audience") ?? "",
            EnhancementProfile = GetString(dict, "enhancement-profile") ?? "commercial",
            AutoDiagnose = GetBool(dict, "auto-diagnose", true),
            AutoEnhance = GetString(dict, "auto-enhance") ?? "semi",
            CreatedAt = GetDateTime(dict, "created-at"),
            UpdatedAt = GetDateTime(dict, "updated-at"),
            CompletionStatus = GetString(dict, "completion-status") ?? "ongoing",
            Outline = GetString(dict, "outline") ?? "",
            Chapters = await chaptersTask,
            Characters = await charactersTask,
            Plotlines = await plotlinesTask,
            Factions = await factionsTask,
            Locations = await locationsTask,
            Items = await itemsTask,
            TimelineEvents = await timelineEventsTask,
            Relationships = await relationshipsTask,
            Scenes = await scenesTask,
        };

        return novel;
    }

    /// <summary>
    /// 获取小说摘要（不含子实体导航属性），用于不需要加载全部子实体的场景。
    /// </summary>
    public async Task<Novel?> GetNovelSummary(string id, CancellationToken ct = default)
    {
        var dir = FindNovelDirById(id);
        if (dir is null)
            return null;

        var yamlPath = Path.Combine(dir, "novel.yaml");
        if (!File.Exists(yamlPath))
            return null;

        var text = await File.ReadAllTextAsync(yamlPath, ct);
        var dict = YamlHelper.Deserialize(text);

        return new Novel
        {
            Id = GetString(dict, "id") ?? id,
            NovelId = id,
            Title = GetString(dict, "title") ?? "",
            Genre = GetString(dict, "genre") ?? "",
            Author = GetString(dict, "author") ?? "",
            TargetWords = GetInt(dict, "target-words"),
            Description = GetString(dict, "description") ?? "",
            WritingStyle = GetString(dict, "writing-style") ?? "",
            TargetAudience = GetString(dict, "target-audience") ?? "",
            EnhancementProfile = GetString(dict, "enhancement-profile") ?? "commercial",
            AutoDiagnose = GetBool(dict, "auto-diagnose", true),
            AutoEnhance = GetString(dict, "auto-enhance") ?? "semi",
            CompletionStatus = GetString(dict, "completion-status") ?? "ongoing",
            Outline = GetString(dict, "outline") ?? "",
            CreatedAt = GetDateTime(dict, "created-at"),
            UpdatedAt = GetDateTime(dict, "updated-at"),
        };
    }

    /// <summary>全部已登记项目目录下的小说并集（统一项目列表）；同 id 只保留写入目录里的那份。</summary>
    public async Task<List<Novel>> GetAllNovels(CancellationToken ct = default)
    {
        var result = new List<Novel>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in _workspace.EnumerateNovelDirectories())
        {
            var yamlPath = Path.Combine(dir, "novel.yaml");
            if (!File.Exists(yamlPath))
                continue;

            var text = await File.ReadAllTextAsync(yamlPath, ct);
            var dict = YamlHelper.Deserialize(text);
            var id = GetString(dict, "id");
            if (id is null || !seenIds.Add(id))
                continue;

            result.Add(
                new Novel
                {
                    Id = id,
                    NovelId = id,
                    Title = GetString(dict, "title") ?? "",
                    Genre = GetString(dict, "genre") ?? "",
                    Author = GetString(dict, "author") ?? "",
                    TargetWords = GetInt(dict, "target-words"),
                    Description = GetString(dict, "description") ?? "",
                    WritingStyle = GetString(dict, "writing-style") ?? "",
                    TargetAudience = GetString(dict, "target-audience") ?? "",
                    EnhancementProfile = GetString(dict, "enhancement-profile") ?? "commercial",
                    AutoDiagnose = GetBool(dict, "auto-diagnose", true),
                    AutoEnhance = GetString(dict, "auto-enhance") ?? "semi",
                    CompletionStatus = GetString(dict, "completion-status") ?? "ongoing",
                    Outline = GetString(dict, "outline") ?? "",
                    CreatedAt = GetDateTime(dict, "created-at"),
                    UpdatedAt = GetDateTime(dict, "updated-at"),
                }
            );
        }

        return result.OrderByDescending(n => n.UpdatedAt).ToList();
    }

    public async Task SaveNovel(Novel novel, CancellationToken ct = default)
    {
        // For new novels, use title as directory name (human-friendly)
        // For existing novels, find their current directory
        var existingDir = FindNovelDirById(novel.Id);
        string dir;
        if (existingDir is not null)
        {
            dir = existingDir;
        }
        else
        {
            dir = Path.Combine(_workspace.ActiveRoot, SanitizeFileName(novel.Title));
            // If directory already exists with a different novel, append ID suffix to avoid collision
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "novel.yaml")))
            {
                dir = Path.Combine(
                    _workspace.ActiveRoot,
                    $"{SanitizeFileName(novel.Title)}_{novel.Id}"
                );
            }
        }
        Directory.CreateDirectory(dir);

        var isNew = !File.Exists(Path.Combine(dir, "novel.yaml"));

        Dictionary<string, object?>? oldDict = null;
        var nPath = Path.Combine(dir, "novel.yaml");
        if (!isNew)
        {
            try
            {
                oldDict = YamlHelper.Deserialize(await File.ReadAllTextAsync(nPath, ct));
            }
            catch
            {
                oldDict = null;
            }
        }

        var dict = new Dictionary<string, object?>
        {
            ["id"] = novel.Id,
            ["title"] = novel.Title,
            ["genre"] = novel.Genre,
            ["author"] = novel.Author,
            ["target-words"] = novel.TargetWords.ToString(CultureInfo.InvariantCulture),
            ["description"] = novel.Description,
            ["outline"] = novel.Outline,
            ["writing-style"] = novel.WritingStyle,
            ["target-audience"] = novel.TargetAudience,
            ["enhancement-profile"] = novel.EnhancementProfile,
            ["auto-diagnose"] = novel.AutoDiagnose,
            ["auto-enhance"] = novel.AutoEnhance,
            ["completion-status"] = novel.CompletionStatus,
            ["created-at"] = (isNew ? DateTime.UtcNow : novel.CreatedAt).ToString(
                "O",
                CultureInfo.InvariantCulture
            ),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        var yaml = YamlHelper.Serialize(dict);
        if (!SkipConflictCheck)
            await AtomicFile.EnsureUnchangedAsync(nPath, ct);
        await AtomicFile.WriteAllTextAsync(nPath, yaml, ct);

        var action = isNew ? AuditAction.Create : AuditAction.Update;
        await FireEntityChanged(
            new EntityChangeEvent(EntityType.Novel, novel.Id, action, novel.Id, novel.Title)
        );

        RegisterNovelDir(novel.Id, dir);
        await TryRecordOntologyDiffAsync(
            novel.Id,
            EntityType.Novel,
            novel.Id,
            novel.Title,
            oldDict,
            dict,
            isNew
        );
    }

    public async Task DeleteNovel(string id, CancellationToken ct = default)
    {
        var dir = FindNovelDirById(id);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            RemoveNovelDir(id);
            await FireEntityChanged(
                new EntityChangeEvent(EntityType.Novel, id, AuditAction.Delete, id, id)
            );
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Chapter
    // ═══════════════════════════════════════════════════════════

    public async Task<Chapter?> GetChapter(string id, CancellationToken ct = default)
    {
        if (CanUseSemantic)
        {
            var semantic = GetChapterViaSemantic(id);
            if (StoreMode == NovelStoreMode.Shadow)
            {
                var direct = await GetChapterDirect(id);
                ReportShadowDifference(nameof(GetChapter), direct, semantic);
            }
            return semantic;
        }

        return await GetChapterDirect(id);
    }

    private async Task<Chapter?> GetChapterDirect(string id)
    {
        EnsureNovelDirIndex();
        foreach (var novelDir in _novelDirIndex!.Values)
        {
            var chapter = await FindChapterInDir(novelDir, id);
            if (chapter is not null)
                return chapter;
        }
        return null;
    }

    /// <summary>
    /// novelId-scoped chapter lookup: directly targets the novel's chapters directory,
    /// avoiding O(N×M) global scan across all novels.
    /// </summary>
    public async Task<Chapter?> GetChapter(
        string novelId,
        string id,
        CancellationToken ct = default
    )
    {
        if (CanUseSemantic)
        {
            var semantic = GetChapterViaSemantic(id);
            if (semantic is not null && semantic.NovelId != novelId)
                semantic = null;
            if (StoreMode == NovelStoreMode.Shadow)
            {
                var direct = await GetChapterDirect(novelId, id);
                ReportShadowDifference(nameof(GetChapter), direct, semantic);
            }
            return semantic;
        }

        return await GetChapterDirect(novelId, id);
    }

    private async Task<Chapter?> GetChapterDirect(string novelId, string id)
    {
        var dir = FindNovelDirById(novelId);
        if (dir is null)
            return null;

        // 文件名即序号（ch{Order:D3}.md）：数字寻址走单文件快路径，避免全目录扫描。
        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var order))
        {
            var byOrder = await GetChapterByOrder(novelId, order);
            if (byOrder is not null && (byOrder.Id == id || byOrder.Order == order))
                return byOrder;
        }

        return await FindChapterInDir(dir, id);
    }

    public async Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default)
    {
        if (CanUseSemantic)
        {
            var semantic = GetChaptersViaSemantic(novelId);
            if (StoreMode == NovelStoreMode.Shadow)
            {
                var direct = await GetChaptersDirect(novelId);
                ReportShadowDifference(nameof(GetChapters), direct, semantic);
            }
            return semantic;
        }

        return await GetChaptersDirect(novelId);
    }

    private async Task<List<Chapter>> GetChaptersDirect(string novelId)
    {
        var dir = FindNovelDirById(novelId);
        var chaptersDir = dir is not null ? Path.Combine(dir, "chapters") : null;
        if (chaptersDir is null || !Directory.Exists(chaptersDir))
            return [];

        var result = new List<Chapter>();
        foreach (var mdPath in Directory.GetFiles(chaptersDir, "*.md").OrderBy(p => p))
        {
            var text = await File.ReadAllTextAsync(mdPath);
            var (meta, body) = MarkdownHelper.ParseMarkdown(text);
            var id = meta.GetValueOrDefault("id");
            if (id is null)
                continue;

            result.Add(
                new Chapter
                {
                    Id = id,
                    NovelId = novelId,
                    Title = meta.GetValueOrDefault("title") ?? "",
                    Order = int.TryParse(meta.GetValueOrDefault("order"), out var o) ? o : 0,
                    Content = body,
                    Status = YamlHelper.YamlToEnum(meta, "status", ChapterStatus.Draft),
                    VolumeId = meta.GetValueOrDefault("volume-id") is { Length: > 0 } vid
                        ? vid
                        : null,
                    Summary = meta.GetValueOrDefault("summary") ?? "",
                    PlotlineIds = ParseIdList(meta.GetValueOrDefault("plotline-ids")),
                    CharacterIds = ParseIdList(meta.GetValueOrDefault("character-ids")),
                    Notes = meta.GetValueOrDefault("notes") ?? "",
                    CreatedAt = ParseDateTime(meta.GetValueOrDefault("created-at")),
                    UpdatedAt = ParseDateTime(meta.GetValueOrDefault("updated-at")),
                }
            );
        }

        return result.OrderBy(c => c.Order).ToList();
    }

    /// <summary>逗号分隔 ID 列表解析（frontmatter 无列表类型，空值 → 空列表）。</summary>
    private static List<string> ParseIdList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            :
            [
                .. value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                ),
            ];

    // ═══════════════════════════════════════════════════════════
    //  Structure index（大纲树：总纲 + 卷纲 + 章节细纲元数据，不含正文）
    // ═══════════════════════════════════════════════════════════

    public async Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default)
    {
        var dir = FindNovelDirById(novelId);
        if (dir is null)
            return null;

        var stamp = ComputeStructureStamp(dir);
        if (
            _structureCache.TryGetValue(novelId, out var cached)
            && string.Equals(cached.Stamp, stamp, StringComparison.Ordinal)
        )
            return cached.Value;

        var novel = await GetNovelSummary(novelId, ct);
        if (novel is null)
            return null;

        var volumes = await GetVolumes(novelId, ct);
        var volumeIds = volumes.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var chapters = await ReadChapterHeadersAsync(novelId, Path.Combine(dir, "chapters"), ct);

        var unassigned = chapters
            .Where(c => string.IsNullOrEmpty(c.VolumeId) || !volumeIds.Contains(c.VolumeId))
            .ToList();

        var structure = new NovelStructure(novel, volumes, chapters, unassigned);
        _structureCache[novelId] = (stamp, structure);
        return structure;
    }

    public async Task<Chapter?> GetChapterByOrder(
        string novelId,
        int order,
        CancellationToken ct = default
    )
    {
        var dir = FindNovelDirById(novelId);
        if (dir is null)
            return null;

        // 章节文件名由 Order 决定（ch{Order:D3}.md）：单文件读，不扫目录。
        var path = Path.Combine(dir, "chapters", $"ch{order:D3}.md");
        if (!File.Exists(path))
            return null;

        var text = await File.ReadAllTextAsync(path, ct);
        return ReadChapterFromText(novelId, text, null);
    }

    /// <summary>
    /// 章节骨架（frontmatter + 正文字符数），**不保留正文**：面板/树形展示只吃骨架，
    /// 正文由 <see cref="GetChapter"/> / <see cref="GetChapterByOrder"/> 按需单章读取。
    /// </summary>
    private static async Task<List<Chapter>> ReadChapterHeadersAsync(
        string novelId,
        string chaptersDir,
        CancellationToken ct
    )
    {
        var result = new List<Chapter>();
        if (!Directory.Exists(chaptersDir))
            return result;

        foreach (var mdPath in Directory.GetFiles(chaptersDir, "*.md"))
        {
            ct.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(mdPath, ct);
            var (meta, body) = MarkdownHelper.ParseMarkdown(text);
            var id = meta.GetValueOrDefault("id");
            if (id is null)
                continue;

            result.Add(
                new Chapter
                {
                    Id = id,
                    NovelId = novelId,
                    Title = meta.GetValueOrDefault("title") ?? "",
                    Order = int.TryParse(meta.GetValueOrDefault("order"), out var o) ? o : 0,
                    Status = YamlHelper.YamlToEnum(meta, "status", ChapterStatus.Draft),
                    VolumeId = meta.GetValueOrDefault("volume-id") is { Length: > 0 } vid
                        ? vid
                        : null,
                    Summary = meta.GetValueOrDefault("summary") ?? "",
                    PlotlineIds = ParseIdList(meta.GetValueOrDefault("plotline-ids")),
                    CharacterIds = ParseIdList(meta.GetValueOrDefault("character-ids")),
                    Notes = meta.GetValueOrDefault("notes") ?? "",
                    CreatedAt = ParseDateTime(meta.GetValueOrDefault("created-at")),
                    UpdatedAt = ParseDateTime(meta.GetValueOrDefault("updated-at")),
                    CharCount = body.Length,
                }
            );
        }

        return result.OrderBy(c => c.Order).ToList();
    }

    /// <summary>
    /// 目录戳：novel.yaml + volumes/*.yaml + chapters/*.md 的（相对名, 长度, mtime）FNV-1a。
    /// 含文件名——改序等于换文件名，只看长度/时间会漏判。
    /// </summary>
    private static string ComputeStructureStamp(string novelDir)
    {
        if (!File.Exists(Path.Combine(novelDir, "novel.yaml")))
            return "missing";

        var paths = new List<string> { Path.Combine(novelDir, "novel.yaml") };

        var volumesDir = Path.Combine(novelDir, "volumes");
        if (Directory.Exists(volumesDir))
            paths.AddRange(Directory.GetFiles(volumesDir, "*.yaml"));

        var chaptersDir = Path.Combine(novelDir, "chapters");
        if (Directory.Exists(chaptersDir))
            paths.AddRange(Directory.GetFiles(chaptersDir, "*.md"));

        paths.Sort(StringComparer.Ordinal);

        var hash = 2166136261u;
        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            var info = new FileInfo(path);
            foreach (var ch in fileName)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            hash ^= (uint)info.Length;
            hash *= 16777619u;
            hash ^= (uint)info.LastWriteTimeUtc.Ticks;
            hash *= 16777619u;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{paths.Count}:{hash:X8}");
    }

    public async Task SaveChapter(Chapter chapter, CancellationToken ct = default)
    {
        var dir = GetNovelDir(chapter.NovelId);
        var chaptersDir = Path.Combine(dir, "chapters");
        Directory.CreateDirectory(chaptersDir);

        // 章节文件名由 Order 决定（ch{Order:D3}.md）→ 改序等于换文件：
        // 写完新文件后删除本章旧序文件，否则会留下一份旧序文件 → 同一章出现两次、并可能覆盖他章正文。
        var fileName = $"ch{chapter.Order:D3}.md";
        var filePath = Path.Combine(chaptersDir, fileName);
        var isNew = !File.Exists(filePath);

        // 热路径（内容更新/同序保存）：目标文件里就是本章 → 无需扫描目录。
        var stalePath =
            isNew || await ReadChapterIdAsync(filePath) != chapter.Id
                ? await FindChapterFilePathById(chaptersDir, chapter.Id, filePath)
                : null;

        // 旧 frontmatter 必须在写盘之前读取（同一路径会被覆盖）；正文不进入日志。
        var oldMetaPath =
            File.Exists(filePath) && await ReadChapterIdAsync(filePath) == chapter.Id ? filePath
            : stalePath is not null && File.Exists(stalePath) ? stalePath
            : null;
        Dictionary<string, object?>? oldMeta = null;
        if (oldMetaPath is not null)
        {
            try
            {
                var (parsed, _) = MarkdownHelper.ParseMarkdown(
                    await File.ReadAllTextAsync(oldMetaPath, ct)
                );
                oldMeta = parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            }
            catch
            {
                oldMeta = null;
            }
        }

        // 章节状态是发布事务的结果，不允许旁路改：只有 ChapterLifecycleService 能改。
        if (!isNew && oldMeta is not null && !OntologyWriteContext.IsChapterLifecycleSuppressed)
        {
            var oldStatus = oldMeta.GetValueOrDefault("status")?.ToString() ?? "";
            var newStatus = YamlHelper.EnumToYaml(chapter.Status);
            if (
                oldStatus.Length > 0
                && !string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new InvalidOperationException(
                    "章节状态变更必须通过 ChapterLifecycleService（发布/撤回）。"
                );
            }
        }

        var meta = new Dictionary<string, string>
        {
            ["id"] = chapter.Id,
            ["order"] = chapter.Order.ToString(CultureInfo.InvariantCulture),
            ["title"] = chapter.Title,
            ["status"] = YamlHelper.EnumToYaml(chapter.Status),
            ["volume-id"] = chapter.VolumeId ?? "",
            ["summary"] = chapter.Summary,
            ["plotline-ids"] = string.Join(",", chapter.PlotlineIds),
            ["character-ids"] = string.Join(",", chapter.CharacterIds),
            ["notes"] = chapter.Notes,
            ["created-at"] = (isNew ? DateTime.UtcNow : chapter.CreatedAt).ToString(
                "O",
                CultureInfo.InvariantCulture
            ),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        var md = MarkdownHelper.WriteMarkdown(meta, chapter.Content);
        if (!SkipConflictCheck)
            await AtomicFile.EnsureUnchangedAsync(filePath, ct);
        await AtomicFile.WriteAllTextAsync(filePath, md, ct);

        // 旧序文件（改序前的位置）在新文件落盘后删除；不触发删除事件、不级联演化分片（章仍存在）。
        if (stalePath is not null && File.Exists(stalePath))
            File.Delete(stalePath);

        var action = isNew ? AuditAction.Create : AuditAction.Update;
        await FireEntityChanged(
            new EntityChangeEvent(
                EntityType.Chapter,
                chapter.Id,
                action,
                chapter.NovelId,
                chapter.Title
            )
        );
        await TryRecordOntologyDiffAsync(
            chapter.NovelId,
            EntityType.Chapter,
            chapter.Id,
            chapter.Title,
            oldMeta,
            meta.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            isNew,
            chapter.Id
        );
    }

    /// <summary>
    /// 全章重排：把全书章节按 (分组, 组内原序) 重排为连续序号，哨兵号留给新章。
    /// </summary>
    /// <remarks>
    /// 两阶段搬移：章节文件名是 <c>ch{Order:D3}.md</c> 且序号全局唯一，置换式改写会互相覆盖
    /// （A→2 会写穿仍住在 ch002.md 的 B）。故先把每章连同 appearances / evolution 分片落到一段
    /// 确定空闲的临时序号区，再从临时位搬到最终位；分片路径直接按目标序号拼出，不走结构缓存
    /// （搬移途中缓存仍是旧序号，经缓存解析会把分片写到错误位置）。
    /// 分片文件内无 chapter id、不可反查，故只能「读出 → 新位写入 → 删旧文件」，不能用 File.Move。
    /// </remarks>
    /// <param name="assignments"><see cref="ChapterOrdering.PlanFullReorder"/> 的结果。</param>
    public async Task ReorderAllChapters(
        string novelId,
        IReadOnlyList<ChapterOrderAssignment> assignments,
        CancellationToken ct = default
    )
    {
        if (assignments.Count == 0)
            return;

        var chapters = await GetChapters(novelId, ct);
        var byId = chapters.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var dir = GetNovelDir(novelId);

        // 临时区从「规划最大序号之上」开始，逐个跳过仍被占用的位，保证临时位一定是空闲位。
        var occupied = chapters.Select(c => c.Order).ToHashSet();
        var staged = new List<StagedChapter>();
        foreach (var assignment in assignments)
        {
            ct.ThrowIfCancellationRequested();
            if (
                assignment.ChapterId == ChapterOrdering.InsertSlotId
                || !byId.TryGetValue(assignment.ChapterId, out var chapter)
            )
                continue;

            var tempOrder = assignments.Max(a => a.Order) + 1;
            while (!occupied.Add(tempOrder))
                tempOrder++;

            staged.Add(
                new StagedChapter(
                    chapter,
                    tempOrder,
                    assignment.Order,
                    await GetAppearancesByChapter(novelId, chapter.Id, ct),
                    await GetEntityStateChangesByChapter(novelId, chapter.Id, ct)
                )
            );
            // 原分片位已读入内存 → 立即腾空，避免搬移结束后在旧位留孤儿（孤儿仍是 ch*.yaml 形状）。
            DeleteChapterFiles(dir, chapter.Order, md: false);
        }

        // 阶段二：临时位 → 最终位。每搬完一章立即清掉临时位，腾空后不会压到未搬走的内容。
        foreach (var stagedChapter in staged)
        {
            ct.ThrowIfCancellationRequested();
            await WriteChapterAtAsync(dir, stagedChapter.Chapter, stagedChapter.TempOrder, ct);
            WriteShardsAt(
                dir,
                stagedChapter.Appearances,
                stagedChapter.Changes,
                stagedChapter.TempOrder
            );

            await WriteChapterAtAsync(dir, stagedChapter.Chapter, stagedChapter.TargetOrder, ct);
            WriteShardsAt(
                dir,
                stagedChapter.Appearances,
                stagedChapter.Changes,
                stagedChapter.TargetOrder
            );

            DeleteChapterFiles(dir, stagedChapter.TempOrder);
        }

        // 逐章投递：订阅者（如语义索引、单写者队列）按章 id 刷新；不伪造 novel 级事件。
        foreach (var stagedChapter in staged)
        {
            await FireEntityChanged(
                new EntityChangeEvent(
                    EntityType.Chapter,
                    stagedChapter.Chapter.Id,
                    AuditAction.Update,
                    novelId,
                    stagedChapter.Chapter.Title
                )
            );
        }
    }

    /// <summary>重排过程中暂存的一章：临时位、最终位与其两类分片内容。</summary>
    private readonly record struct StagedChapter(
        Chapter Chapter,
        int TempOrder,
        int TargetOrder,
        List<AppearanceRecord> Appearances,
        List<EntityStateChange> Changes
    );

    /// <summary>
    /// 按给定序号写章文件。显式传 <paramref name="order"/>（不用 <c>chapter.Order</c>）以便一次搬移写两位；
    /// created-at 原样保留——重排不是新建。
    /// </summary>
    private async Task WriteChapterAtAsync(
        string novelDir,
        Chapter chapter,
        int order,
        CancellationToken ct
    )
    {
        var chaptersDir = Path.Combine(novelDir, "chapters");
        Directory.CreateDirectory(chaptersDir);
        var filePath = Path.Combine(chaptersDir, $"ch{order:D3}.md");
        // 目标位已被别的章占用时（置换），本次内容必须压在上面 → 不查 stale，避免删错章。
        // 临时位必然空闲，且本章原文件另有其位，删除由调用方统一按 TempOrder 处理。
        var meta = new Dictionary<string, string>
        {
            ["id"] = chapter.Id,
            ["order"] = order.ToString(CultureInfo.InvariantCulture),
            ["title"] = chapter.Title,
            ["status"] = YamlHelper.EnumToYaml(chapter.Status),
            ["volume-id"] = chapter.VolumeId ?? "",
            ["summary"] = chapter.Summary,
            ["plotline-ids"] = string.Join(",", chapter.PlotlineIds),
            ["character-ids"] = string.Join(",", chapter.CharacterIds),
            ["notes"] = chapter.Notes,
            ["created-at"] = chapter.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = chapter.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        await AtomicFile.WriteAllTextAsync(
            filePath,
            MarkdownHelper.WriteMarkdown(meta, chapter.Content),
            ct
        );
    }

    /// <summary>按给定序号写 appearances / evolution 分片（空则跳过，不留空文件）。</summary>
    private static void WriteShardsAt(
        string novelDir,
        List<AppearanceRecord> appearances,
        List<EntityStateChange> changes,
        int order
    )
    {
        if (appearances.Count > 0)
        {
            var path = Path.Combine(novelDir, "appearances", $"ch{order:D3}.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                YamlHelper.SerializeList(appearances.Select(AppearanceToDict).ToList())
            );
        }

        if (changes.Count > 0)
        {
            var path = Path.Combine(novelDir, "evolution", $"ch{order:D3}.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                YamlHelper.SerializeList(changes.Select(StateChangeToDict).ToList())
            );
        }
    }

    /// <summary>删除某序号下的章文件；<paramref name="md"/> 控制是否连同正文一起删（仅清分片时传 false）。</summary>
    private static void DeleteChapterFiles(string novelDir, int order, bool md = true)
    {
        var candidates = new List<string>
        {
            Path.Combine(novelDir, "appearances", $"ch{order:D3}.yaml"),
            Path.Combine(novelDir, "evolution", $"ch{order:D3}.yaml"),
        };
        if (md)
            candidates.Add(Path.Combine(novelDir, "chapters", $"ch{order:D3}.md"));

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// 按章节 id 定位其现有文件（排除 <paramref name="excludePath"/>）。
    /// 用于改序时清理旧文件：文件按 Order 命名，id 才是章节身份。
    /// </summary>
    private static async Task<string?> FindChapterFilePathById(
        string chaptersDir,
        string id,
        string excludePath
    )
    {
        foreach (var mdPath in Directory.GetFiles(chaptersDir, "*.md"))
        {
            if (string.Equals(mdPath, excludePath, StringComparison.Ordinal))
                continue;
            if (await ReadChapterIdAsync(mdPath) == id)
                return mdPath;
        }
        return null;
    }

    /// <summary>读取单个章节文件的 frontmatter id（热路径轻量判定用）。</summary>
    private static async Task<string?> ReadChapterIdAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        var (meta, _) = MarkdownHelper.ParseMarkdown(text);
        return meta.GetValueOrDefault("id");
    }

    public async Task DeleteChapter(string id, CancellationToken ct = default)
    {
        EnsureNovelDirIndex();
        foreach (var novelDir in _novelDirIndex!.Values)
        {
            var deleted = await TryDeleteChapterInDir(novelDir, id);
            if (deleted)
                return;
        }
    }

    /// <summary>
    /// novelId-scoped chapter deletion.
    /// </summary>
    public async Task DeleteChapter(string novelId, string id, CancellationToken ct = default)
    {
        var dir = FindNovelDirById(novelId);
        if (dir is not null)
            await TryDeleteChapterInDir(dir, id);
    }

    public Task<int> GetChapterCount(string novelId, CancellationToken ct = default)
    {
        try
        {
            if (_semantic is not null && StoreMode == NovelStoreMode.Incremental)
                return Task.FromResult(_semantic.GetFileNames(novelId, "chapters").Length);

            var dir = FindNovelDirById(novelId);
            var chaptersDir = dir is not null ? Path.Combine(dir, "chapters") : null;
            if (chaptersDir is null || !Directory.Exists(chaptersDir))
                return Task.FromResult(0);

            return Task.FromResult(Directory.GetFiles(chaptersDir, "*.md").Length);
        }
        catch (Exception exception)
        {
            return Task.FromException<int>(exception);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Character
    // ═══════════════════════════════════════════════════════════

    public async Task<Character?> GetCharacter(string id, CancellationToken ct = default)
    {
        return await GetEntityAsync<Character>(
            id,
            "characters",
            dict => new Character
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = GetString(dict, "novel-id") ?? "",
                Name = GetString(dict, "name") ?? "",
                Gender = GetString(dict, "gender") ?? "",
                Age = GetString(dict, "age") ?? "",
                Role = YamlHelper.YamlToEnum(dict, "role", CharacterRole.Supporting),
                Personality = GetString(dict, "personality") ?? "",
                Appearance = GetString(dict, "appearance") ?? "",
                Background = GetString(dict, "background") ?? "",
                Motivation = GetString(dict, "motivation") ?? "",
                Tags = GetStringList(dict, "tags"),
                Abilities = GetString(dict, "abilities") ?? "",
                Weakness = GetString(dict, "weakness") ?? "",
                Habits = GetString(dict, "habits") ?? "",
                Notes = GetString(dict, "notes") ?? "",
                Status = YamlHelper.YamlToEnum(dict, "status", CharacterStatus.Active),
                FactionId = GetString(dict, "faction-id"),
                CurrentLocationId = GetString(dict, "current-location-id"),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<Character?> GetCharacter(
        string novelId,
        string id,
        CancellationToken ct = default
    )
    {
        return await GetEntityAsync<Character>(
            novelId,
            id,
            "characters",
            dict => new Character
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Gender = GetString(dict, "gender") ?? "",
                Age = GetString(dict, "age") ?? "",
                Role = YamlHelper.YamlToEnum(dict, "role", CharacterRole.Supporting),
                Personality = GetString(dict, "personality") ?? "",
                Appearance = GetString(dict, "appearance") ?? "",
                Background = GetString(dict, "background") ?? "",
                Motivation = GetString(dict, "motivation") ?? "",
                Tags = GetStringList(dict, "tags"),
                Abilities = GetString(dict, "abilities") ?? "",
                Weakness = GetString(dict, "weakness") ?? "",
                Habits = GetString(dict, "habits") ?? "",
                Notes = GetString(dict, "notes") ?? "",
                Status = YamlHelper.YamlToEnum(dict, "status", CharacterStatus.Active),
                FactionId = GetString(dict, "faction-id"),
                CurrentLocationId = GetString(dict, "current-location-id"),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<List<Character>> GetCharacters(string novelId, CancellationToken ct = default)
    {
        return await GetEntitiesAsync(
            novelId,
            "characters",
            dict => new Character
            {
                Id = GetString(dict, "id") ?? "",
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Gender = GetString(dict, "gender") ?? "",
                Age = GetString(dict, "age") ?? "",
                Role = YamlHelper.YamlToEnum(dict, "role", CharacterRole.Supporting),
                Personality = GetString(dict, "personality") ?? "",
                Appearance = GetString(dict, "appearance") ?? "",
                Background = GetString(dict, "background") ?? "",
                Motivation = GetString(dict, "motivation") ?? "",
                Tags = GetStringList(dict, "tags"),
                Abilities = GetString(dict, "abilities") ?? "",
                Weakness = GetString(dict, "weakness") ?? "",
                Habits = GetString(dict, "habits") ?? "",
                Notes = GetString(dict, "notes") ?? "",
                Status = YamlHelper.YamlToEnum(dict, "status", CharacterStatus.Active),
                FactionId = GetString(dict, "faction-id"),
                CurrentLocationId = GetString(dict, "current-location-id"),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task SaveCharacter(Character character, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = character.Id,
            ["novel-id"] = character.NovelId,
            ["name"] = character.Name,
            ["gender"] = character.Gender,
            ["age"] = character.Age,
            ["role"] = YamlHelper.EnumToYaml(character.Role),
            ["personality"] = character.Personality,
            ["appearance"] = character.Appearance,
            ["background"] = character.Background,
            ["motivation"] = character.Motivation,
            ["tags"] = character.Tags,
            ["abilities"] = character.Abilities,
            ["weakness"] = character.Weakness,
            ["habits"] = character.Habits,
            ["notes"] = character.Notes,
            ["status"] = YamlHelper.EnumToYaml(character.Status),
            ["faction-id"] = character.FactionId,
            ["current-location-id"] = character.CurrentLocationId,
            ["metrics"] = MetricsToDict(character.Metrics),
            ["created-at"] = (
                character.CreatedAt == default ? DateTime.UtcNow : character.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await SaveEntityAsync(
            character.NovelId,
            "characters",
            $"{character.Name}-{character.Id}.yaml",
            character.Id,
            dict,
            EntityType.Character,
            character.Name
        );
    }

    public async Task DeleteCharacter(string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(id, "characters", EntityType.Character);
    }

    public async Task DeleteCharacter(string novelId, string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(novelId, id, "characters", EntityType.Character);
    }

    public Task<int> GetCharacterCount(string novelId, CancellationToken ct = default)
    {
        try
        {
            if (_semantic is not null && StoreMode == NovelStoreMode.Incremental)
                return Task.FromResult(_semantic.GetFileNames(novelId, "characters").Length);

            var dir = FindNovelDirById(novelId);
            var entityDir = dir is not null ? Path.Combine(dir, "characters") : null;
            if (entityDir is null || !Directory.Exists(entityDir))
                return Task.FromResult(0);

            return Task.FromResult(Directory.GetFiles(entityDir, "*.yaml").Length);
        }
        catch (Exception exception)
        {
            return Task.FromException<int>(exception);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Plotline
    // ═══════════════════════════════════════════════════════════

    public async Task<Plotline?> GetPlotline(string id, CancellationToken ct = default)
    {
        return await GetEntityAsync<Plotline>(
            id,
            "plotlines",
            dict => new Plotline
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = GetString(dict, "novel-id") ?? "",
                Name = GetString(dict, "name") ?? "",
                Description = GetString(dict, "description") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", PlotlineType.Main),
                Tension = YamlHelper.YamlToEnum(dict, "tension", PlotlineTension.Medium),
                PlannedRevealChapterId = GetString(dict, "planned-reveal-chapter-id"),
                ResolvedAtChapterId = GetString(dict, "resolved-at-chapter-id"),
                Status = YamlHelper.YamlToEnum(dict, "status", PlotlineStatus.Active),
                Turnpoints = GetString(dict, "turnpoints") ?? "",
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<Plotline?> GetPlotline(
        string novelId,
        string id,
        CancellationToken ct = default
    )
    {
        return await GetEntityAsync<Plotline>(
            novelId,
            id,
            "plotlines",
            dict => new Plotline
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Description = GetString(dict, "description") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", PlotlineType.Main),
                Tension = YamlHelper.YamlToEnum(dict, "tension", PlotlineTension.Medium),
                PlannedRevealChapterId = GetString(dict, "planned-reveal-chapter-id"),
                ResolvedAtChapterId = GetString(dict, "resolved-at-chapter-id"),
                Status = YamlHelper.YamlToEnum(dict, "status", PlotlineStatus.Active),
                Turnpoints = GetString(dict, "turnpoints") ?? "",
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default)
    {
        return await GetEntitiesAsync(
            novelId,
            "plotlines",
            dict => new Plotline
            {
                Id = GetString(dict, "id") ?? "",
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Description = GetString(dict, "description") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", PlotlineType.Main),
                Tension = YamlHelper.YamlToEnum(dict, "tension", PlotlineTension.Medium),
                PlannedRevealChapterId = GetString(dict, "planned-reveal-chapter-id"),
                ResolvedAtChapterId = GetString(dict, "resolved-at-chapter-id"),
                Status = YamlHelper.YamlToEnum(dict, "status", PlotlineStatus.Active),
                Turnpoints = GetString(dict, "turnpoints") ?? "",
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<List<Plotline>> GetOpenPlotlines(
        string novelId,
        CancellationToken ct = default
    )
    {
        var plotlines = await GetPlotlines(novelId, ct);
        return plotlines.Where(p => p.ResolvedAtChapterId == null).ToList();
    }

    public async Task SavePlotline(Plotline plotline, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = plotline.Id,
            ["novel-id"] = plotline.NovelId,
            ["name"] = plotline.Name,
            ["description"] = plotline.Description,
            ["type"] = YamlHelper.EnumToYaml(plotline.Type),
            ["tension"] = YamlHelper.EnumToYaml(plotline.Tension),
            ["planned-reveal-chapter-id"] = plotline.PlannedRevealChapterId,
            ["resolved-at-chapter-id"] = plotline.ResolvedAtChapterId,
            ["status"] = YamlHelper.EnumToYaml(plotline.Status),
            ["turnpoints"] = plotline.Turnpoints,
            ["created-at"] = (
                plotline.CreatedAt == default ? DateTime.UtcNow : plotline.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await SaveEntityAsync(
            plotline.NovelId,
            "plotlines",
            $"{plotline.Name}-{plotline.Id}.yaml",
            plotline.Id,
            dict,
            EntityType.Plotline,
            plotline.Name
        );
    }

    public async Task DeletePlotline(string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(id, "plotlines", EntityType.Plotline);
    }

    public async Task DeletePlotline(string novelId, string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(novelId, id, "plotlines", EntityType.Plotline);
    }

    // ═══════════════════════════════════════════════════════════
    //  Faction
    // ═══════════════════════════════════════════════════════════

    public async Task<Faction?> GetFaction(string id, CancellationToken ct = default)
    {
        return await GetEntityAsync<Faction>(
            id,
            "factions",
            dict => new Faction
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = GetString(dict, "novel-id") ?? "",
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", FactionType.Sect),
                Description = GetString(dict, "description") ?? "",
                Leader = GetString(dict, "leader") ?? "",
                HeadquartersId = GetString(dict, "headquarters-id"),
                Goal = GetString(dict, "goal") ?? "",
                Members = GetStringList(dict, "members"),
                Status = YamlHelper.YamlToEnum(dict, "status", FactionStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<Faction?> GetFaction(
        string novelId,
        string id,
        CancellationToken ct = default
    )
    {
        return await GetEntityAsync<Faction>(
            novelId,
            id,
            "factions",
            dict => new Faction
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", FactionType.Sect),
                Description = GetString(dict, "description") ?? "",
                Leader = GetString(dict, "leader") ?? "",
                HeadquartersId = GetString(dict, "headquarters-id"),
                Goal = GetString(dict, "goal") ?? "",
                Members = GetStringList(dict, "members"),
                Status = YamlHelper.YamlToEnum(dict, "status", FactionStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default)
    {
        return await GetEntitiesAsync(
            novelId,
            "factions",
            dict => new Faction
            {
                Id = GetString(dict, "id") ?? "",
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", FactionType.Sect),
                Description = GetString(dict, "description") ?? "",
                Leader = GetString(dict, "leader") ?? "",
                HeadquartersId = GetString(dict, "headquarters-id"),
                Goal = GetString(dict, "goal") ?? "",
                Members = GetStringList(dict, "members"),
                Status = YamlHelper.YamlToEnum(dict, "status", FactionStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task SaveFaction(Faction faction, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = faction.Id,
            ["novel-id"] = faction.NovelId,
            ["name"] = faction.Name,
            ["type"] = YamlHelper.EnumToYaml(faction.Type),
            ["description"] = faction.Description,
            ["leader"] = faction.Leader,
            ["headquarters-id"] = faction.HeadquartersId,
            ["goal"] = faction.Goal,
            ["members"] = faction.Members,
            ["status"] = YamlHelper.EnumToYaml(faction.Status),
            ["metrics"] = MetricsToDict(faction.Metrics),
            ["created-at"] = (
                faction.CreatedAt == default ? DateTime.UtcNow : faction.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await SaveEntityAsync(
            faction.NovelId,
            "factions",
            $"{faction.Name}-{faction.Id}.yaml",
            faction.Id,
            dict,
            EntityType.Faction,
            faction.Name
        );
    }

    public async Task DeleteFaction(string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(id, "factions", EntityType.Faction);
    }

    public async Task DeleteFaction(string novelId, string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(novelId, id, "factions", EntityType.Faction);
    }

    public async Task SaveFactionRelationship(
        FactionRelationship rel,
        CancellationToken ct = default
    )
    {
        var dir = GetNovelDir(rel.NovelId);
        var relsPath = Path.Combine(dir, "faction-relationships.yaml");

        var rels = new List<Dictionary<string, object?>>();
        if (File.Exists(relsPath))
        {
            var text = await File.ReadAllTextAsync(relsPath, ct);
            rels = YamlHelper.DeserializeList(text);
        }

        // Update or add
        var existingIdx = rels.FindIndex(r =>
            GetString(r, "faction-a-id") == rel.FactionAId
            && GetString(r, "faction-b-id") == rel.FactionBId
        );
        var oldDict = existingIdx >= 0 ? rels[existingIdx] : null;

        var relDict = new Dictionary<string, object?>
        {
            ["faction-a-id"] = rel.FactionAId,
            ["faction-b-id"] = rel.FactionBId,
            ["type"] = YamlHelper.EnumToYaml(rel.Type),
            ["description"] = rel.Description,
        };

        if (existingIdx >= 0)
            rels[existingIdx] = relDict;
        else
            rels.Add(relDict);

        var yaml = YamlHelper.SerializeList(rels);
        if (!SkipConflictCheck)
            await AtomicFile.EnsureUnchangedAsync(relsPath, ct);
        await AtomicFile.WriteAllTextAsync(relsPath, yaml, ct);

        // C3 补齐：该写路径原本不触发 EntityChanged，这里显式投递到单写者队列。
        _semantic?.NotifyNovelChanged(rel.NovelId);
        await TryRecordOntologyDiffAsync(
            rel.NovelId,
            EntityType.FactionRelationship,
            $"{rel.FactionAId}↔{rel.FactionBId}",
            $"{rel.FactionAId}↔{rel.FactionBId}",
            oldDict,
            relDict,
            existingIdx < 0
        );
    }

    public async Task<List<FactionRelationship>> GetFactionRelationships(
        string novelId,
        CancellationToken ct = default
    )
    {
        var dir = GetNovelDir(novelId);
        var relsPath = Path.Combine(dir, "faction-relationships.yaml");
        if (!File.Exists(relsPath))
            return [];

        var text = await File.ReadAllTextAsync(relsPath, ct);
        var rels = YamlHelper.DeserializeList(text);

        return rels.Select(r => new FactionRelationship
            {
                Id = $"{GetString(r, "faction-a-id")}↔{GetString(r, "faction-b-id")}",
                NovelId = novelId,
                FactionAId = GetString(r, "faction-a-id") ?? "",
                FactionBId = GetString(r, "faction-b-id") ?? "",
                Type = YamlHelper.YamlToEnum(r, "type", FactionRelationType.Neutral),
                Description = GetString(r, "description") ?? "",
            })
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════
    //  WorldRules（CHANGE_10 5.3：作者声明约束，L2 精确算法前置）
    // ═══════════════════════════════════════════════════════════

    public async Task<List<WorldRule>> GetWorldRules(string novelId, CancellationToken ct = default)
    {
        var dir = GetNovelDir(novelId);
        var path = Path.Combine(dir, "world-rules.yaml");
        if (!File.Exists(path))
            return [];

        var text = await File.ReadAllTextAsync(path, ct);
        var rules = YamlHelper.DeserializeList(text);

        return rules
            .Select(r => new WorldRule
            {
                Id = GetString(r, "id") ?? "",
                NovelId = novelId,
                EntryKind = YamlHelper.YamlToEnum(r, "entry-kind", WorldRuleEntryKind.Rule),
                Category = YamlHelper.YamlToEnum(r, "category", WorldRuleCategory.Other),
                Content = GetString(r, "content") ?? "",
                Type = GetString(r, "type") ?? "",
                Value = double.TryParse(GetString(r, "value"), out var v) ? v : 0,
                Unit = GetString(r, "unit") ?? "",
                Description = GetString(r, "description") ?? "",
                RuleKind = YamlHelper.YamlToEnum(r, "rule-kind", RuleKind.Numeric),
                SubjectType = EntityTypeExtensions.ParseEntityType(GetString(r, "subject-type")),
                SubjectId = GetString(r, "subject-id"),
                Metric = GetString(r, "metric"),
                Operator = GetString(r, "operator"),
                Threshold = double.TryParse(
                    GetString(r, "threshold"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var threshold
                )
                    ? threshold
                    : null,
                Action = GetString(r, "action"),
                RequiredState = GetString(r, "required-state"),
                ForbiddenAction = GetString(r, "forbidden-action"),
                Effect = ReadNestedPlannedDelta(r, "effect"),
                Severity = YamlHelper.YamlToEnum(r, "severity", RuleSeverity.Warning),
                EffectiveFromChapterId = GetString(r, "effective-from-chapter-id"),
                EffectiveUntilChapterId = GetString(r, "effective-until-chapter-id"),
                RequiredRelationType = GetString(r, "required-relation-type"),
                RequiredRelationTargetId = GetString(r, "required-relation-target-id"),
            })
            .Where(r =>
                !string.IsNullOrWhiteSpace(r.Id)
                && (
                    r.EntryKind == WorldRuleEntryKind.Setting
                        ? !string.IsNullOrWhiteSpace(r.Description)
                            || !string.IsNullOrWhiteSpace(r.Content)
                        : !string.IsNullOrWhiteSpace(r.Type)
                            || r.RuleKind != RuleKind.Numeric
                            || r.Effect is not null
                )
            )
            .ToList();
    }

    public async Task SaveWorldRule(string novelId, WorldRule rule, CancellationToken ct = default)
    {
        var dir = GetNovelDir(novelId);
        var path = Path.Combine(dir, "world-rules.yaml");

        var rules = new List<Dictionary<string, object?>>();
        if (File.Exists(path))
        {
            var text = await File.ReadAllTextAsync(path, ct);
            rules = YamlHelper.DeserializeList(text);
        }

        var ruleDict = new Dictionary<string, object?>
        {
            ["id"] = rule.Id,
            ["entry-kind"] = YamlHelper.EnumToYaml(rule.EntryKind),
            ["category"] = YamlHelper.EnumToYaml(rule.Category),
            ["content"] = string.IsNullOrWhiteSpace(rule.Content) ? null : rule.Content,
            ["type"] = rule.Type,
            ["value"] = rule.Value,
            ["unit"] = rule.Unit,
            ["description"] = rule.Description,
            ["rule-kind"] = rule.RuleKind.ToString().ToLowerInvariant(),
            ["subject-type"] = rule.SubjectType?.ToKebabCaseString(),
            ["subject-id"] = rule.SubjectId,
            ["metric"] = rule.Metric,
            ["operator"] = rule.Operator,
            ["threshold"] = rule.Threshold,
            ["action"] = rule.Action,
            ["required-state"] = rule.RequiredState,
            ["forbidden-action"] = rule.ForbiddenAction,
            ["effect"] = rule.Effect is null ? null : PlannedDeltaToDict(rule.Effect),
            ["severity"] = YamlHelper.EnumToYaml(rule.Severity),
            ["effective-from-chapter-id"] = rule.EffectiveFromChapterId,
            ["effective-until-chapter-id"] = rule.EffectiveUntilChapterId,
            ["required-relation-type"] = rule.RequiredRelationType,
            ["required-relation-target-id"] = rule.RequiredRelationTargetId,
        };

        var existingIdx = rules.FindIndex(r => GetString(r, "id") == rule.Id);
        var oldDict = existingIdx >= 0 ? rules[existingIdx] : null;
        if (existingIdx >= 0)
            rules[existingIdx] = ruleDict;
        else
            rules.Add(ruleDict);

        var yaml = YamlHelper.SerializeList(rules);
        if (!SkipConflictCheck)
            await AtomicFile.EnsureUnchangedAsync(path, ct);
        await AtomicFile.WriteAllTextAsync(path, yaml, ct);
        _semantic?.NotifyNovelChanged(novelId);

        var ruleName =
            !string.IsNullOrWhiteSpace(rule.Description) ? rule.Description
            : !string.IsNullOrWhiteSpace(rule.Type) ? rule.Type
            : rule.Content.Length > 30 ? rule.Content[..30]
            : rule.Content;
        await TryRecordOntologyDiffAsync(
            novelId,
            EntityType.WorldRule,
            rule.Id,
            ruleName,
            oldDict,
            ruleDict,
            existingIdx < 0
        );
    }

    public async Task DeleteWorldRule(string novelId, string ruleId, CancellationToken ct = default)
    {
        var dir = GetNovelDir(novelId);
        var path = Path.Combine(dir, "world-rules.yaml");
        if (!File.Exists(path))
            return;

        var text = await File.ReadAllTextAsync(path, ct);
        var rules = YamlHelper.DeserializeList(text);
        var removedDict = rules.FirstOrDefault(r => GetString(r, "id") == ruleId);
        if (removedDict is null)
            return;
        rules.Remove(removedDict);

        var yaml = YamlHelper.SerializeList(rules);
        if (!SkipConflictCheck)
            await AtomicFile.EnsureUnchangedAsync(path, ct);
        await AtomicFile.WriteAllTextAsync(path, yaml, ct);
        _semantic?.NotifyNovelChanged(novelId);

        var description = GetString(removedDict, "description");
        var ruleType = GetString(removedDict, "type");
        var content = GetString(removedDict, "content") ?? "";
        var ruleName =
            !string.IsNullOrWhiteSpace(description) ? description
            : !string.IsNullOrWhiteSpace(ruleType) ? ruleType
            : content.Length > 30 ? content[..30]
            : content;
        await TryRecordOntologyDeleteAsync(novelId, EntityType.WorldRule, ruleId, ruleName);
    }

    // ═══════════════════════════════════════════════════════════
    //  Entity Evolution（ENTITY-EVOLUTION §3.2/§3.3：按章分片，整章替换，章级联删除）
    //  appearances/ch{Order:D3}.yaml = 该章全部 AppearanceRecord
    //  evolution/ch{Order:D3}.yaml   = 该章全部 EntityStateChange
    // ═══════════════════════════════════════════════════════════

    /// <summary>章节 Id → Order 解析（ch{Order:D3} 文件名的基础）。找不到返回 null。</summary>
    private async Task<int?> ResolveChapterOrderAsync(
        string novelId,
        string chapterId,
        CancellationToken ct
    )
    {
        // 结构索引优先：只读 frontmatter，把「按章演化读」的定位从 O(全量正文) 降到 O(目录戳 + frontmatter)。
        var structure = await GetStructure(novelId, ct);
        var indexed = structure?.Chapters.FirstOrDefault(c => c.Id == chapterId)?.Order;
        if (indexed is not null)
            return indexed;

        var chapters = await GetChapters(novelId, ct);
        return chapters.FirstOrDefault(c => c.Id == chapterId)?.Order;
    }

    private async Task<string?> EvolutionShardPathAsync(
        string novelId,
        string chapterId,
        string subDir,
        CancellationToken ct
    )
    {
        var order = await ResolveChapterOrderAsync(novelId, chapterId, ct);
        if (order is null)
            return null;
        var dir = GetNovelDir(novelId);
        return Path.Combine(dir, subDir, $"ch{order.Value:D3}.yaml");
    }

    private static Dictionary<string, object?> AppearanceToDict(AppearanceRecord r) =>
        new()
        {
            ["id"] = r.Id,
            ["entity-type"] = r.EntityType.ToKebabCaseString(),
            ["entity-id"] = r.EntityId,
            ["chapter-id"] = r.ChapterId,
            ["mode"] = YamlHelper.EnumToYaml(r.Mode),
            ["scene-ids"] = r.SceneIds is { Count: > 0 } ? r.SceneIds : null,
            ["evidence-span"] = r.EvidenceSpan,
            ["source"] = YamlHelper.EnumToYaml(r.Source),
            ["created-at"] = (r.CreatedAt == default ? DateTime.UtcNow : r.CreatedAt).ToString(
                "O",
                CultureInfo.InvariantCulture
            ),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

    private static Dictionary<string, object?> StateChangeToDict(EntityStateChange c) =>
        new()
        {
            ["id"] = c.Id,
            ["target-entity-type"] = c.TargetEntityType.ToKebabCaseString(),
            ["target-id"] = c.TargetId,
            ["chapter-id"] = c.ChapterId,
            ["aspect"] = YamlHelper.EnumToYaml(c.Aspect),
            ["from-summary"] = c.FromSummary,
            ["to-summary"] = c.ToSummary,
            ["evidence-quote"] = c.EvidenceQuote,
            ["summary"] = c.Summary,
            ["source"] = YamlHelper.EnumToYaml(c.Source),
            ["effects"] = c.Effects is { Count: > 0 }
                ? c.Effects.Select(StateEffectToDict).ToList()
                : null,
            ["sequence"] = c.Sequence,
            ["cause"] = c.Cause,
            ["visibility"] = YamlHelper.EnumToYaml(c.Visibility),
            ["witness-ids"] = c.WitnessIds is { Count: > 0 } ? c.WitnessIds : null,
            ["location-id"] = c.LocationId,
            ["scene-id"] = c.SceneId,
            ["time-description"] = c.TimeDescription,
            ["plotline-ids"] = c.PlotlineIds is { Count: > 0 } ? c.PlotlineIds : null,
            ["lifecycle"] = YamlHelper.EnumToYaml(c.Lifecycle),
            ["committed-at"] = c.CommittedAt?.ToString("O", CultureInfo.InvariantCulture),
            ["change-set-id"] = c.ChangeSetId,
            ["is-cascade"] = c.IsCascade,
            ["source-change-id"] = c.SourceChangeId,
            ["session-id"] = c.SessionId,
            ["created-at"] = (c.CreatedAt == default ? DateTime.UtcNow : c.CreatedAt).ToString(
                "O",
                CultureInfo.InvariantCulture
            ),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

    private static Dictionary<string, object?> StateEffectToDict(StateEffect effect) =>
        new()
        {
            ["target-type"] = effect.TargetType.ToKebabCaseString(),
            ["target-id"] = effect.TargetId,
            ["field"] = effect.Field,
            ["operation"] = YamlHelper.EnumToYaml(effect.Operation),
            ["value"] = effect.Value,
            ["from"] = effect.From,
            ["cascade"] = effect.Cascade,
            ["source-change-id"] = effect.SourceChangeId,
        };

    private static List<StateEffect> StateEffectsFromDict(Dictionary<string, object?> dict)
    {
        if (!dict.TryGetValue("effects", out var raw) || raw is not List<object?> items)
            return [];

        var result = new List<StateEffect>();
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> map)
                continue;
            result.Add(
                new StateEffect
                {
                    TargetType =
                        EntityTypeExtensions.ParseEntityType(GetString(map, "target-type"))
                        ?? EntityType.Character,
                    TargetId = GetString(map, "target-id") ?? "",
                    Field = GetString(map, "field") ?? "",
                    Operation = YamlHelper.YamlToEnum(map, "operation", StateEffectOperation.Set),
                    Value = GetString(map, "value"),
                    From = GetString(map, "from"),
                    Cascade = GetBool(map, "cascade", false),
                    SourceChangeId = GetString(map, "source-change-id"),
                }
            );
        }
        return result;
    }

    private static AppearanceRecord AppearanceFromDict(
        Dictionary<string, object?> dict,
        string novelId
    ) =>
        new()
        {
            Id = GetString(dict, "id") ?? "",
            NovelId = novelId,
            EntityType = YamlHelper.YamlToEnum(dict, "entity-type", Core.Data.EntityType.Character),
            EntityId = GetString(dict, "entity-id") ?? "",
            ChapterId = GetString(dict, "chapter-id") ?? "",
            Mode = YamlHelper.YamlToEnum(dict, "mode", AppearanceMode.Present),
            SceneIds = GetStringList(dict, "scene-ids"),
            EvidenceSpan = GetString(dict, "evidence-span"),
            Source = YamlHelper.YamlToEnum(dict, "source", AuditSource.System),
            CreatedAt = GetDateTime(dict, "created-at"),
            UpdatedAt = GetDateTime(dict, "updated-at"),
        };

    private static EntityStateChange StateChangeFromDict(
        Dictionary<string, object?> dict,
        string novelId
    ) =>
        new()
        {
            Id = GetString(dict, "id") ?? "",
            NovelId = novelId,
            TargetEntityType = YamlHelper.YamlToEnum(
                dict,
                "target-entity-type",
                Core.Data.EntityType.Character
            ),
            TargetId = GetString(dict, "target-id") ?? "",
            ChapterId = GetString(dict, "chapter-id") ?? "",
            Aspect = YamlHelper.YamlToEnum(dict, "aspect", EvolutionAspect.Other),
            FromSummary = GetString(dict, "from-summary"),
            ToSummary = GetString(dict, "to-summary"),
            EvidenceQuote = GetString(dict, "evidence-quote"),
            Summary = GetString(dict, "summary") ?? "",
            Source = YamlHelper.YamlToEnum(dict, "source", AuditSource.System),
            Effects = StateEffectsFromDict(dict),
            Sequence = GetInt(dict, "sequence"),
            Cause = GetString(dict, "cause"),
            Visibility = YamlHelper.YamlToEnum(dict, "visibility", ChangeVisibility.Public),
            WitnessIds = GetStringList(dict, "witness-ids"),
            LocationId = GetString(dict, "location-id"),
            SceneId = GetString(dict, "scene-id"),
            TimeDescription = GetString(dict, "time-description"),
            PlotlineIds = GetStringList(dict, "plotline-ids"),
            Lifecycle = YamlHelper.YamlToEnum(dict, "lifecycle", ChangeLifeCycleFallback(dict)),
            CommittedAt = DateTime.TryParse(
                GetString(dict, "committed-at"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var committedAt
            )
                ? committedAt
                : null,
            ChangeSetId = GetString(dict, "change-set-id"),
            IsCascade = GetBool(dict, "is-cascade", false),
            SourceChangeId = GetString(dict, "source-change-id"),
            SessionId = GetString(dict, "session-id"),
            CreatedAt = GetDateTime(dict, "created-at"),
            UpdatedAt = GetDateTime(dict, "updated-at"),
        };

    /// <summary>
    /// 旧分片（本字段上线前写入）没有 lifecycle：视为已生效——历史上草稿章的变化也直接生效。
    /// </summary>
    private static ChangeLifecycle ChangeLifeCycleFallback(Dictionary<string, object?> dict) =>
        ChangeLifecycle.Committed;

    public async Task<List<AppearanceRecord>> GetAppearances(
        string novelId,
        CancellationToken ct = default
    )
    {
        var result = new List<AppearanceRecord>();
        var dir = FindNovelDirById(novelId);
        var entityDir = dir is not null ? Path.Combine(dir, "appearances") : null;
        if (entityDir is null || !Directory.Exists(entityDir))
            return result;

        foreach (var yamlPath in Directory.GetFiles(entityDir, "*.yaml").OrderBy(p => p))
        {
            var text = await File.ReadAllTextAsync(yamlPath, ct);
            foreach (var dict in YamlHelper.DeserializeList(text))
                result.Add(AppearanceFromDict(dict, novelId));
        }
        return result;
    }

    public async Task<List<AppearanceRecord>> GetAppearancesByChapter(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var path = await EvolutionShardPathAsync(novelId, chapterId, "appearances", ct);
        if (path is null || !File.Exists(path))
            return [];

        var text = await File.ReadAllTextAsync(path, ct);
        return YamlHelper
            .DeserializeList(text)
            .Select(dict => AppearanceFromDict(dict, novelId))
            .ToList();
    }

    public async Task<List<EntityStateChange>> GetEntityStateChangesByChapter(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var path = await EvolutionShardPathAsync(novelId, chapterId, "evolution", ct);
        if (path is null || !File.Exists(path))
            return [];

        var text = await File.ReadAllTextAsync(path, ct);
        var changes = YamlHelper
            .DeserializeList(text)
            .Select(dict => StateChangeFromDict(dict, novelId))
            .ToList();
        NormalizeSequences(changes);
        return changes;
    }

    public async Task SaveChapterAppearances(
        string novelId,
        string chapterId,
        IReadOnlyList<AppearanceRecord> records,
        CancellationToken ct = default
    )
    {
        var path = await EvolutionShardPathAsync(novelId, chapterId, "appearances", ct);
        if (path is null)
            return; // 章不存在（已删或从未写）→ 静默不写，宁可漏不误报

        var dictList = records.Select(AppearanceToDict).ToList();
        var yaml = YamlHelper.SerializeList(dictList);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!SkipConflictCheck && File.Exists(path))
            await AtomicFile.EnsureUnchangedAsync(path, ct);
        await AtomicFile.WriteAllTextAsync(path, yaml, ct);
        _semantic?.NotifyNovelChanged(novelId);
    }

    public async Task DeleteChapterAppearances(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var dir = FindNovelDirById(novelId);
        var entityDir = dir is not null ? Path.Combine(dir, "appearances") : null;
        if (entityDir is null || !Directory.Exists(entityDir))
            return;

        // 章删除级联：按 ChapterId 匹配分片（章 Id 已从 chapters/ 移除，Order 解析不可用，
        // 扫描分片内记录的 chapter-id 字段，整文件只含该章记录 → 命中即删文件）。
        foreach (var yamlPath in Directory.GetFiles(entityDir, "*.yaml"))
        {
            var text = await File.ReadAllTextAsync(yamlPath, ct);
            var dicts = YamlHelper.DeserializeList(text);
            if (dicts.Any(d => GetString(d, "chapter-id") == chapterId))
            {
                File.Delete(yamlPath);
                break;
            }
        }
        _semantic?.NotifyNovelChanged(novelId);
    }

    public async Task<List<EntityStateChange>> GetEntityStateChanges(
        string novelId,
        CancellationToken ct = default
    )
    {
        var result = new List<EntityStateChange>();
        var dir = FindNovelDirById(novelId);
        var entityDir = dir is not null ? Path.Combine(dir, "evolution") : null;
        if (entityDir is not null && Directory.Exists(entityDir))
        {
            // manual.yaml 是「设定变更」分片（ChapterId=""），单独读取，不当章节分片。
            foreach (
                var yamlPath in Directory
                    .GetFiles(entityDir, "*.yaml")
                    .OrderBy(p => p)
                    .Where(p =>
                        !string.Equals(Path.GetFileName(p), "manual.yaml", StringComparison.Ordinal)
                    )
            )
            {
                var text = await File.ReadAllTextAsync(yamlPath, ct);
                foreach (var dict in YamlHelper.DeserializeList(text))
                    result.Add(StateChangeFromDict(dict, novelId));
            }
        }

        NormalizeSequences(result);
        result.AddRange(await GetManualStateChanges(novelId, ct));
        return result;
    }

    /// <summary>旧分片缺 sequence：按文件顺序补 1..N（同章内稳定）。</summary>
    private static void NormalizeSequences(List<EntityStateChange> changes)
    {
        if (changes.All(c => c.Sequence > 0))
            return;
        foreach (var group in changes.GroupBy(c => c.ChapterId, StringComparer.Ordinal))
        {
            var index = 0;
            foreach (var change in group.Where(c => c.Sequence <= 0))
                change.Sequence = ++index;
        }
    }

    public Task<List<EntityStateChange>> GetEntityStateChangesByEntity(
        string novelId,
        EntityType entityType,
        string entityId,
        CancellationToken ct = default
    )
    {
        return GetEntityStateChangesByEntityCore(novelId, entityType, entityId, ct);
    }

    private async Task<List<EntityStateChange>> GetEntityStateChangesByEntityCore(
        string novelId,
        EntityType entityType,
        string entityId,
        CancellationToken ct
    )
    {
        var all = await GetEntityStateChanges(novelId, ct);
        return all.Where(c =>
                c.TargetEntityType == entityType
                && string.Equals(c.TargetId, entityId, StringComparison.Ordinal)
            )
            .ToList();
    }

    public async Task SaveEntityStateChanges(
        string novelId,
        string chapterId,
        IReadOnlyList<EntityStateChange> changes,
        CancellationToken ct = default
    )
    {
        var path = await EvolutionShardPathAsync(novelId, chapterId, "evolution", ct);
        if (path is null)
            return; // 同 SaveChapterAppearances：章不存在静默不写

        // 旧分片必须在写盘前读取（整章替换语义）。
        IReadOnlyList<EntityStateChange> oldChanges = [];
        try
        {
            oldChanges = await GetEntityStateChangesByChapter(novelId, chapterId, ct);
        }
        catch
        {
            oldChanges = [];
        }

        var dictList = changes.Select(StateChangeToDict).ToList();
        var yaml = YamlHelper.SerializeList(dictList);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!SkipConflictCheck && File.Exists(path))
            await AtomicFile.EnsureUnchangedAsync(path, ct);
        await AtomicFile.WriteAllTextAsync(path, yaml, ct);
        _semantic?.NotifyNovelChanged(novelId);

        if (OntologyWriteContext.IsJournalSuppressed)
        {
            return;
        }
        try
        {
            var actions = OntologyJournal.DiffStateChanges(novelId, chapterId, oldChanges, changes);
            if (actions.Count > 0)
            {
                await AppendOntologyActions(novelId, actions);
            }
        }
        catch
        {
            // 日志是旁路：永不阻塞状态记录写入。
        }
    }

    public async Task DeleteEntityStateChanges(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var dir = FindNovelDirById(novelId);
        var entityDir = dir is not null ? Path.Combine(dir, "evolution") : null;
        if (entityDir is null || !Directory.Exists(entityDir))
            return;

        foreach (var yamlPath in Directory.GetFiles(entityDir, "*.yaml"))
        {
            var text = await File.ReadAllTextAsync(yamlPath, ct);
            var dicts = YamlHelper.DeserializeList(text);
            if (dicts.Any(d => GetString(d, "chapter-id") == chapterId))
            {
                File.Delete(yamlPath);
                break;
            }
        }
        _semantic?.NotifyNovelChanged(novelId);
    }

    // ═══════════════════════════════════════════════════════════
    //  Location
    // ═══════════════════════════════════════════════════════════

    public async Task<Location?> GetLocation(string id, CancellationToken ct = default)
    {
        return await GetEntityAsync<Location>(
            id,
            "locations",
            dict => new Location
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = GetString(dict, "novel-id") ?? "",
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", LocationType.City),
                Atmosphere = GetString(dict, "atmosphere") ?? "",
                Description = GetString(dict, "description") ?? "",
                ParentId = GetString(dict, "parent-id"),
                AdjacentLocations = GetLocationAdjacency(dict, "adjacent-locations"),
                Status = YamlHelper.YamlToEnum(dict, "status", LocationStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<Location?> GetLocation(
        string novelId,
        string id,
        CancellationToken ct = default
    )
    {
        return await GetEntityAsync<Location>(
            novelId,
            id,
            "locations",
            dict => new Location
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", LocationType.City),
                Atmosphere = GetString(dict, "atmosphere") ?? "",
                Description = GetString(dict, "description") ?? "",
                ParentId = GetString(dict, "parent-id"),
                AdjacentLocations = GetLocationAdjacency(dict, "adjacent-locations"),
                Status = YamlHelper.YamlToEnum(dict, "status", LocationStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default)
    {
        return await GetEntitiesAsync(
            novelId,
            "locations",
            dict => new Location
            {
                Id = GetString(dict, "id") ?? "",
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", LocationType.City),
                Atmosphere = GetString(dict, "atmosphere") ?? "",
                Description = GetString(dict, "description") ?? "",
                ParentId = GetString(dict, "parent-id"),
                AdjacentLocations = GetLocationAdjacency(dict, "adjacent-locations"),
                Status = YamlHelper.YamlToEnum(dict, "status", LocationStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task SaveLocation(Location location, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = location.Id,
            ["novel-id"] = location.NovelId,
            ["name"] = location.Name,
            ["type"] = YamlHelper.EnumToYaml(location.Type),
            ["atmosphere"] = location.Atmosphere,
            ["description"] = location.Description,
            ["parent-id"] = location.ParentId,
            ["adjacent-locations"] = location.AdjacentLocations is { Count: > 0 }
                ? location
                    .AdjacentLocations.Select(a => new Dictionary<string, object?>
                    {
                        ["target-id"] = a.TargetId,
                        ["distance-li"] = a.DistanceLi.ToString(CultureInfo.InvariantCulture),
                    })
                    .ToList()
                : null,
            ["status"] = YamlHelper.EnumToYaml(location.Status),
            ["metrics"] = MetricsToDict(location.Metrics),
            ["created-at"] = (
                location.CreatedAt == default ? DateTime.UtcNow : location.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await SaveEntityAsync(
            location.NovelId,
            "locations",
            $"{location.Name}-{location.Id}.yaml",
            location.Id,
            dict,
            EntityType.Location,
            location.Name
        );
    }

    public async Task DeleteLocation(string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(id, "locations", EntityType.Location);
    }

    public async Task DeleteLocation(string novelId, string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(novelId, id, "locations", EntityType.Location);
    }

    // ═══════════════════════════════════════════════════════════
    //  Item
    // ═══════════════════════════════════════════════════════════

    public async Task<Item?> GetItem(string id, CancellationToken ct = default)
    {
        return await GetEntityAsync<Item>(
            id,
            "items",
            dict => new Item
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = GetString(dict, "novel-id") ?? "",
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", ItemType.Artifact),
                Description = GetString(dict, "description") ?? "",
                OwnerId = GetString(dict, "owner-id"),
                LocationId = GetString(dict, "location-id"),
                OwnershipPeriods = GetOwnershipPeriods(dict, "ownership-periods"),
                Status = YamlHelper.YamlToEnum(dict, "status", ItemStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default)
    {
        return await GetEntityAsync<Item>(
            novelId,
            id,
            "items",
            dict => new Item
            {
                Id = GetString(dict, "id") ?? id,
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", ItemType.Artifact),
                Description = GetString(dict, "description") ?? "",
                OwnerId = GetString(dict, "owner-id"),
                LocationId = GetString(dict, "location-id"),
                OwnershipPeriods = GetOwnershipPeriods(dict, "ownership-periods"),
                Status = YamlHelper.YamlToEnum(dict, "status", ItemStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task<List<Item>> GetItems(string novelId, CancellationToken ct = default)
    {
        return await GetEntitiesAsync(
            novelId,
            "items",
            dict => new Item
            {
                Id = GetString(dict, "id") ?? "",
                NovelId = novelId,
                Name = GetString(dict, "name") ?? "",
                Type = YamlHelper.YamlToEnum(dict, "type", ItemType.Artifact),
                Description = GetString(dict, "description") ?? "",
                OwnerId = GetString(dict, "owner-id"),
                LocationId = GetString(dict, "location-id"),
                OwnershipPeriods = GetOwnershipPeriods(dict, "ownership-periods"),
                Status = YamlHelper.YamlToEnum(dict, "status", ItemStatus.Active),
                Metrics = ReadMetrics(dict),
                CreatedAt = GetDateTime(dict, "created-at"),
                UpdatedAt = GetDateTime(dict, "updated-at"),
            }
        );
    }

    public async Task SaveItem(Item item, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = item.Id,
            ["novel-id"] = item.NovelId,
            ["name"] = item.Name,
            ["type"] = YamlHelper.EnumToYaml(item.Type),
            ["description"] = item.Description,
            ["owner-id"] = item.OwnerId,
            ["location-id"] = item.LocationId,
            ["status"] = YamlHelper.EnumToYaml(item.Status),
            ["ownership-periods"] = item
                .OwnershipPeriods.Select(o => new Dictionary<string, object?>
                {
                    ["owner-id"] = o.OwnerId,
                    ["since-chapter-id"] = o.SinceChapterId,
                    ["until-chapter-id"] = o.UntilChapterId,
                })
                .ToList(),
            ["metrics"] = MetricsToDict(item.Metrics),
            ["created-at"] = (
                item.CreatedAt == default ? DateTime.UtcNow : item.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await SaveEntityAsync(
            item.NovelId,
            "items",
            $"{item.Name}-{item.Id}.yaml",
            item.Id,
            dict,
            EntityType.Item,
            item.Name
        );
    }

    public async Task DeleteItem(string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(id, "items", EntityType.Item);
    }

    public async Task DeleteItem(string novelId, string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(novelId, id, "items", EntityType.Item);
    }

    // ═══════════════════════════════════════════════════════════
    //  TimelineEvent
    // ═══════════════════════════════════════════════════════════

    public async Task<List<TimelineEvent>> GetTimelineEvents(
        string novelId,
        CancellationToken ct = default
    )
    {
        var dir = GetNovelDir(novelId);
        var path = Path.Combine(dir, "timeline.yaml");
        if (!File.Exists(path))
            return [];

        var text = await File.ReadAllTextAsync(path, ct);
        var events = YamlHelper.DeserializeList(text);

        return events
            .Select(
                (e, idx) =>
                    new TimelineEvent
                    {
                        Id = GetString(e, "id") ?? $"te{idx}",
                        NovelId = novelId,
                        Title = GetString(e, "title") ?? "",
                        Description = GetString(e, "description") ?? "",
                        ChapterId = GetString(e, "chapter-id"),
                        CreatedAt = GetDateTime(e, "created-at"),
                        UpdatedAt = GetDateTime(e, "updated-at"),
                    }
            )
            .OrderBy(t => t.ChapterId)
            .ToList();
    }

    public async Task SaveTimelineEvent(TimelineEvent evt, CancellationToken ct = default)
    {
        var dir = GetNovelDir(evt.NovelId);
        var path = Path.Combine(dir, "timeline.yaml");

        var events = new List<Dictionary<string, object?>>();
        if (File.Exists(path))
        {
            var text = await File.ReadAllTextAsync(path, ct);
            events = YamlHelper.DeserializeList(text);
        }

        var existingIdx = events.FindIndex(e => GetString(e, "id") == evt.Id);
        var oldDict = existingIdx >= 0 ? events[existingIdx] : null;
        var dict = new Dictionary<string, object?>
        {
            ["id"] = evt.Id,
            ["title"] = evt.Title,
            ["description"] = evt.Description,
            ["chapter-id"] = evt.ChapterId,
            ["created-at"] = (evt.CreatedAt == default ? DateTime.UtcNow : evt.CreatedAt).ToString(
                "O",
                CultureInfo.InvariantCulture
            ),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        if (existingIdx >= 0)
            events[existingIdx] = dict;
        else
            events.Add(dict);

        var yaml = YamlHelper.SerializeList(events);
        if (!SkipConflictCheck)
            await AtomicFile.EnsureUnchangedAsync(path, ct);
        await AtomicFile.WriteAllTextAsync(path, yaml, ct);

        // C3 补齐：该写路径原本不触发 EntityChanged，这里显式投递到单写者队列。
        _semantic?.NotifyNovelChanged(evt.NovelId);
        await TryRecordOntologyDiffAsync(
            evt.NovelId,
            EntityType.TimelineEvent,
            evt.Id,
            evt.Title,
            oldDict,
            dict,
            existingIdx < 0,
            evt.ChapterId
        );
    }

    // ═══════════════════════════════════════════════════════════
    //  AuthorNote（书级随手记：notes/{Title}-{Id}.yaml）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 读取全部笔记（含已废弃）。走直读而非 <c>GetEntitiesAsync</c>：Incremental/Shadow 模式经
    /// Tacos 的 GetFileNames 时没有 "notes" 这个 kind，会恒返回空（静默丢数据）。
    /// </summary>
    public Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default) =>
        GetEntitiesDirectAsync(novelId, "notes", dict => ReadNoteFromDict(dict, novelId));

    public async Task SaveNote(AuthorNote note, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = note.Id,
            ["novel-id"] = note.NovelId,
            ["title"] = note.Title,
            ["body"] = note.Body,
            ["tags"] = note.Tags.Count > 0 ? note.Tags : null,
            ["status"] = YamlHelper.EnumToYaml(note.Status),
            ["captured-chapter-order"] = note.CapturedChapterOrder,
            ["session-id"] = note.SessionId,
            ["source"] = YamlHelper.EnumToYaml(note.Source),
            ["created-at"] = (
                note.CreatedAt == default ? DateTime.UtcNow : note.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await SaveEntityAsync(
            note.NovelId,
            "notes",
            $"{note.Title}-{note.Id}.yaml",
            note.Id,
            dict,
            EntityType.Note,
            note.Title
        );
    }

    private static AuthorNote ReadNoteFromDict(Dictionary<string, object?> dict, string novelId) =>
        new()
        {
            Id = GetString(dict, "id") ?? "",
            NovelId = novelId,
            Title = GetString(dict, "title") ?? "",
            Body = GetString(dict, "body") ?? "",
            Tags = GetStringList(dict, "tags"),
            Status = YamlHelper.YamlToEnum(dict, "status", NoteStatus.Active),
            CapturedChapterOrder = dict.ContainsKey("captured-chapter-order")
                ? GetInt(dict, "captured-chapter-order")
                : null,
            SessionId = GetString(dict, "session-id") ?? "",
            Source = YamlHelper.YamlToEnum(dict, "source", AuditSource.User),
            CreatedAt = GetDateTime(dict, "created-at"),
            UpdatedAt = GetDateTime(dict, "updated-at"),
        };

    // ═══════════════════════════════════════════════════════════
    //  Relationship
    // ═══════════════════════════════════════════════════════════

    public async Task<Relationship?> GetRelationship(string id, CancellationToken ct = default)
    {
        EnsureNovelDirIndex();
        foreach (var novelDir in _novelDirIndex!.Values)
        {
            var rel = await FindRelationshipInDir(novelDir, id);
            if (rel is not null)
                return rel;
        }
        return null;
    }

    /// <summary>
    /// novelId-scoped relationship lookup: directly targets the novel's relationships.yaml,
    /// avoiding O(N) global scan across all novels.
    /// </summary>
    public async Task<Relationship?> GetRelationship(
        string novelId,
        string id,
        CancellationToken ct = default
    )
    {
        var dir = FindNovelDirById(novelId);
        if (dir is null)
            return null;
        return await FindRelationshipInDir(dir, id, novelId);
    }

    public async Task<List<Relationship>> GetRelationshipsByCharacter(
        string characterId,
        CancellationToken ct = default
    )
    {
        var result = new List<Relationship>();
        EnsureNovelDirIndex();
        foreach (var novelDir in _novelDirIndex!.Values)
        {
            var path = Path.Combine(novelDir, "relationships.yaml");
            if (!File.Exists(path))
                continue;

            var text = await File.ReadAllTextAsync(path, ct);
            var rels = YamlHelper.DeserializeList(text);
            var novelId = GetNovelIdFromDir(novelDir);

            foreach (var r in rels)
            {
                var aId = GetString(r, "character-a-id");
                var bId = GetString(r, "character-b-id");
                if (aId == characterId || bId == characterId)
                {
                    result.Add(
                        new Relationship
                        {
                            Id = GetString(r, "id") ?? $"{aId}↔{bId}",
                            NovelId = novelId,
                            CharacterAId = aId ?? "",
                            CharacterBId = bId ?? "",
                            Type =
                                RelationshipTypeExtensions.ParseRelationshipType(
                                    GetString(r, "type")
                                ) ?? RelationshipType.Friend,
                            Strength = YamlHelper.YamlToEnum(
                                r,
                                "strength",
                                RelationshipStrength.Medium
                            ),
                            Description = GetString(r, "description") ?? "",
                            SinceChapterId = GetString(r, "since-chapter-id"),
                            UntilChapterId = GetString(r, "until-chapter-id"),
                            CreatedAt = GetDateTime(r, "created-at"),
                            UpdatedAt = GetDateTime(r, "updated-at"),
                        }
                    );
                }
            }
        }
        return result;
    }

    public async Task<List<Relationship>> GetRelationshipsByNovel(
        string novelId,
        CancellationToken ct = default
    )
    {
        if (CanUseSemantic)
        {
            var semantic = GetRelationshipsViaSemantic(novelId);
            if (StoreMode == NovelStoreMode.Shadow)
            {
                var direct = await GetRelationshipsByNovelDirect(novelId);
                ReportShadowDifference(nameof(GetRelationshipsByNovel), direct, semantic);
            }
            return semantic;
        }

        return await GetRelationshipsByNovelDirect(novelId);
    }

    private async Task<List<Relationship>> GetRelationshipsByNovelDirect(string novelId)
    {
        var dir = GetNovelDir(novelId);
        var path = Path.Combine(dir, "relationships.yaml");
        if (!File.Exists(path))
            return [];

        var text = await File.ReadAllTextAsync(path);
        var rels = YamlHelper.DeserializeList(text);

        return rels.Select(r => new Relationship
            {
                Id =
                    GetString(r, "id")
                    ?? $"{GetString(r, "character-a-id")}↔{GetString(r, "character-b-id")}",
                NovelId = novelId,
                CharacterAId = GetString(r, "character-a-id") ?? "",
                CharacterBId = GetString(r, "character-b-id") ?? "",
                Type =
                    RelationshipTypeExtensions.ParseRelationshipType(GetString(r, "type"))
                    ?? RelationshipType.Friend,
                Strength = YamlHelper.YamlToEnum(r, "strength", RelationshipStrength.Medium),
                Description = GetString(r, "description") ?? "",
                SinceChapterId = GetString(r, "since-chapter-id"),
                UntilChapterId = GetString(r, "until-chapter-id"),
                CreatedAt = GetDateTime(r, "created-at"),
                UpdatedAt = GetDateTime(r, "updated-at"),
            })
            .ToList();
    }

    public async Task SaveRelationship(Relationship relationship, CancellationToken ct = default)
    {
        var dir = GetNovelDir(relationship.NovelId);
        var path = Path.Combine(dir, "relationships.yaml");

        var rels = new List<Dictionary<string, object?>>();
        if (File.Exists(path))
        {
            var text = await File.ReadAllTextAsync(path, ct);
            rels = YamlHelper.DeserializeList(text);
        }

        var existingIdx = rels.FindIndex(r => GetString(r, "id") == relationship.Id);
        var oldDict = existingIdx >= 0 ? rels[existingIdx] : null;
        var dict = new Dictionary<string, object?>
        {
            ["id"] = relationship.Id,
            ["character-a-id"] = relationship.CharacterAId,
            ["character-b-id"] = relationship.CharacterBId,
            ["type"] = relationship.Type.ToKebabCaseString(),
            ["strength"] = YamlHelper.EnumToYaml(relationship.Strength),
            ["description"] = relationship.Description,
            ["since-chapter-id"] = relationship.SinceChapterId,
            ["until-chapter-id"] = relationship.UntilChapterId,
            ["created-at"] = (
                relationship.CreatedAt == default ? DateTime.UtcNow : relationship.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        if (existingIdx >= 0)
            rels[existingIdx] = dict;
        else
            rels.Add(dict);

        var yaml = YamlHelper.SerializeList(rels);
        if (!SkipConflictCheck)
            await AtomicFile.EnsureUnchangedAsync(path, ct);
        await AtomicFile.WriteAllTextAsync(path, yaml, ct);

        // Lightweight name lookup — read individual YAML files instead of loading all characters
        var nameA =
            await GetCharacterNameById(relationship.NovelId, relationship.CharacterAId, ct)
            ?? relationship.CharacterAId;
        var nameB =
            await GetCharacterNameById(relationship.NovelId, relationship.CharacterBId, ct)
            ?? relationship.CharacterBId;

        var action = existingIdx >= 0 ? AuditAction.Update : AuditAction.Create;
        await FireEntityChanged(
            new EntityChangeEvent(
                EntityType.Relationship,
                relationship.Id,
                action,
                relationship.NovelId,
                $"{nameA}↔{nameB}"
            )
        );
        await TryRecordOntologyDiffAsync(
            relationship.NovelId,
            EntityType.Relationship,
            relationship.Id,
            $"{nameA}↔{nameB}",
            oldDict,
            dict,
            existingIdx < 0
        );
    }

    public async Task DeleteRelationship(string id, CancellationToken ct = default)
    {
        EnsureNovelDirIndex();
        foreach (var novelDir in _novelDirIndex!.Values)
        {
            var deleted = await TryDeleteRelationshipInDir(novelDir, id);
            if (deleted)
                return;
        }
    }

    /// <summary>
    /// novelId-scoped relationship deletion.
    /// </summary>
    public async Task DeleteRelationship(string novelId, string id, CancellationToken ct = default)
    {
        var dir = FindNovelDirById(novelId);
        if (dir is not null)
            await TryDeleteRelationshipInDir(dir, id, novelId);
    }

    // ═══════════════════════════════════════════════════════════
    //  SceneDefinition
    // ═══════════════════════════════════════════════════════════

    private static SceneDefinition ReadSceneFromDict(
        Dictionary<string, object?> dict,
        string novelId
    )
    {
        return new SceneDefinition
        {
            Id = GetString(dict, "id") ?? "",
            NovelId = novelId,
            Title = GetString(dict, "title") ?? "",
            Order = int.TryParse(GetString(dict, "order"), out var o) ? o : 0,
            ChapterId = GetString(dict, "chapter-id"),
            LocationId = GetString(dict, "location-id"),
            TimeDescription = GetString(dict, "time-description") ?? "",
            ParticipantIds = GetStringList(dict, "participant-ids"),
            PlotlineIds = GetStringList(dict, "plotline-ids"),
            ItemIds = GetStringList(dict, "item-ids"),
            Conflict = GetString(dict, "conflict") ?? "",
            Goal = GetString(dict, "goal") ?? "",
            EmotionalArc = GetString(dict, "emotional-arc") ?? "",
            SceneType = YamlHelper.YamlToEnum(dict, "scene-type", SceneType.Dialogue),
            Notes = GetString(dict, "notes") ?? "",
            PlannedDeltas = ReadPlannedDeltas(dict),
        };
    }

    /// <summary>读取 scene YAML 的 planned-deltas（旧文件无该字段 → 空列表）。</summary>
    private static List<PlannedDelta> ReadPlannedDeltas(Dictionary<string, object?> dict)
    {
        if (!dict.TryGetValue("planned-deltas", out var raw) || raw is not List<object?> items)
            return [];

        var result = new List<PlannedDelta>();
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> map)
                continue;

            var targetType =
                EntityTypeExtensions.ParseEntityType(GetString(map, "target-entity-type"))
                ?? EntityType.Character;
            EvolutionAspect? aspect = null;
            if (
                Enum.TryParse<EvolutionAspect>(
                    GetString(map, "aspect"),
                    ignoreCase: true,
                    out var parsedAspect
                )
            )
            {
                aspect = parsedAspect;
            }

            result.Add(
                new PlannedDelta
                {
                    Kind = YamlHelper.YamlToEnum(map, "kind", PlannedDeltaKind.StateChange),
                    TargetEntityType = targetType,
                    TargetId = GetString(map, "target-id") ?? "",
                    Aspect = aspect,
                    FromSummary = GetString(map, "from"),
                    ToSummary = GetString(map, "to"),
                    Summary = GetString(map, "summary") ?? "",
                    RuleId = GetString(map, "rule-id"),
                    ActionType = GetString(map, "action-type"),
                }
            );
        }
        return result;
    }

    /// <summary>scene YAML 的 planned-deltas 序列化。</summary>
    private static List<Dictionary<string, object?>> PlannedDeltasToDicts(
        IReadOnlyList<PlannedDelta> deltas
    ) => deltas.Select(PlannedDeltaToDict).ToList();

    private static Dictionary<string, object?> PlannedDeltaToDict(PlannedDelta delta) =>
        new()
        {
            ["kind"] = delta.Kind.ToString().ToLowerInvariant(),
            ["target-entity-type"] = delta.TargetEntityType.ToKebabCaseString(),
            ["target-id"] = delta.TargetId,
            ["aspect"] = delta.Aspect?.ToString().ToLowerInvariant(),
            ["from"] = delta.FromSummary,
            ["to"] = delta.ToSummary,
            ["summary"] = delta.Summary,
            ["rule-id"] = delta.RuleId,
            ["action-type"] = delta.ActionType,
        };

    private static PlannedDelta? ReadNestedPlannedDelta(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var raw) || raw is not Dictionary<string, object?> nested)
            return null;
        var deltas = ReadPlannedDeltas(
            new Dictionary<string, object?> { ["planned-deltas"] = new List<object?> { nested } }
        );
        return deltas.Count > 0 ? deltas[0] : null;
    }

    public async Task<SceneDefinition?> GetScene(string id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            id,
            "scenes",
            dict => ReadSceneFromDict(dict, GetString(dict, "novel-id") ?? "")
        );
    }

    public async Task<SceneDefinition?> GetScene(
        string novelId,
        string id,
        CancellationToken ct = default
    )
    {
        return await GetEntityAsync(
            novelId,
            id,
            "scenes",
            dict => ReadSceneFromDict(dict, novelId)
        );
    }

    public async Task<List<SceneDefinition>> GetScenesByNovel(
        string novelId,
        CancellationToken ct = default
    )
    {
        return await GetEntitiesAsync(novelId, "scenes", dict => ReadSceneFromDict(dict, novelId));
    }

    public async Task<List<SceneDefinition>> GetScenesByChapter(
        string chapterId,
        CancellationToken ct = default
    )
    {
        var result = new List<SceneDefinition>();
        EnsureNovelDirIndex();
        foreach (var novelDir in _novelDirIndex!.Values)
        {
            var scenesDir = Path.Combine(novelDir, "scenes");
            if (!Directory.Exists(scenesDir))
                continue;

            var novelId = GetNovelIdFromDir(novelDir);
            foreach (var yamlPath in Directory.GetFiles(scenesDir, "*.yaml"))
            {
                var text = await File.ReadAllTextAsync(yamlPath, ct);
                var dict = YamlHelper.Deserialize(text);
                if (GetString(dict, "chapter-id") == chapterId)
                    result.Add(ReadSceneFromDict(dict, novelId));
            }
        }
        return result.OrderBy(s => s.Order).ToList();
    }

    public async Task SaveScene(SceneDefinition scene, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = scene.Id,
            ["novel-id"] = scene.NovelId,
            ["title"] = scene.Title,
            ["order"] = scene.Order,
            ["chapter-id"] = scene.ChapterId,
            ["location-id"] = scene.LocationId,
            ["time-description"] = scene.TimeDescription,
            ["participant-ids"] = scene.ParticipantIds,
            ["plotline-ids"] = scene.PlotlineIds,
            ["item-ids"] = scene.ItemIds,
            ["conflict"] = scene.Conflict,
            ["goal"] = scene.Goal,
            ["emotional-arc"] = scene.EmotionalArc,
            ["scene-type"] = YamlHelper.EnumToYaml(scene.SceneType),
            ["notes"] = scene.Notes,
            ["planned-deltas"] =
                scene.PlannedDeltas.Count > 0 ? PlannedDeltasToDicts(scene.PlannedDeltas) : null,
            ["created-at"] = (
                scene.CreatedAt == default ? DateTime.UtcNow : scene.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await SaveEntityAsync(
            scene.NovelId,
            "scenes",
            $"{scene.Title}-{scene.Id}.yaml",
            scene.Id,
            dict,
            EntityType.Scene,
            scene.Title
        );
    }

    public async Task DeleteScene(string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(id, "scenes", EntityType.Scene);
    }

    public async Task DeleteScene(string novelId, string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(novelId, id, "scenes", EntityType.Scene);
    }

    public Task<int> GetSceneCount(string novelId, CancellationToken ct = default)
    {
        try
        {
            if (_semantic is not null && StoreMode == NovelStoreMode.Incremental)
                return Task.FromResult(_semantic.GetFileNames(novelId, "scenes").Length);

            var dir = FindNovelDirById(novelId);
            var scenesDir = dir is not null ? Path.Combine(dir, "scenes") : null;
            if (scenesDir is null || !Directory.Exists(scenesDir))
                return Task.FromResult(0);

            return Task.FromResult(Directory.GetFiles(scenesDir, "*.yaml").Length);
        }
        catch (Exception exception)
        {
            return Task.FromException<int>(exception);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Volume（大纲二层：卷纲）
    // ═══════════════════════════════════════════════════════════

    private static Volume ReadVolumeFromDict(Dictionary<string, object?> dict, string novelId)
    {
        return new Volume
        {
            Id = GetString(dict, "id") ?? "",
            NovelId = novelId,
            Name = GetString(dict, "name") ?? "",
            Order = int.TryParse(GetString(dict, "order"), out var o) ? o : 0,
            Summary = GetString(dict, "summary") ?? "",
            Description = GetString(dict, "description") ?? "",
            Goal = GetString(dict, "goal") ?? "",
            PlotlineIds = GetStringList(dict, "plotline-ids"),
            CharacterIds = GetStringList(dict, "character-ids"),
            Notes = GetString(dict, "notes") ?? "",
        };
    }

    public async Task<Volume?> GetVolume(string id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            id,
            "volumes",
            dict => ReadVolumeFromDict(dict, GetString(dict, "novel-id") ?? "")
        );
    }

    public async Task<Volume?> GetVolume(string novelId, string id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            novelId,
            id,
            "volumes",
            dict => ReadVolumeFromDict(dict, novelId)
        );
    }

    public async Task<List<Volume>> GetVolumes(string novelId, CancellationToken ct = default)
    {
        var volumes = await GetEntitiesAsync(
            novelId,
            "volumes",
            dict => ReadVolumeFromDict(dict, novelId)
        );
        return volumes.OrderBy(v => v.Order).ToList();
    }

    public async Task SaveVolume(Volume volume, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = volume.Id,
            ["novel-id"] = volume.NovelId,
            ["name"] = volume.Name,
            ["order"] = volume.Order,
            ["summary"] = volume.Summary,
            ["description"] = volume.Description,
            ["goal"] = volume.Goal,
            ["plotline-ids"] = volume.PlotlineIds,
            ["character-ids"] = volume.CharacterIds,
            ["notes"] = volume.Notes,
            ["created-at"] = (
                volume.CreatedAt == default ? DateTime.UtcNow : volume.CreatedAt
            ).ToString("O", CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        await SaveEntityAsync(
            volume.NovelId,
            "volumes",
            $"{volume.Name}-{volume.Id}.yaml",
            volume.Id,
            dict,
            EntityType.Volume,
            volume.Name
        );
    }

    public async Task DeleteVolume(string id, CancellationToken ct = default)
    {
        await DeleteEntityAsync(id, "volumes", EntityType.Volume);
    }

    /// <summary>删除分卷并级联清空引用该卷的章节 VolumeId（避免悬空引用）。</summary>
    public async Task DeleteVolume(string novelId, string id, CancellationToken ct = default)
    {
        var exists = (await GetVolumes(novelId, ct)).Any(v => v.Id == id);
        if (!exists)
            return;

        await DeleteEntityAsync(novelId, id, "volumes", EntityType.Volume);

        foreach (var chapter in await GetChapters(novelId, ct))
        {
            if (chapter.VolumeId != id)
                continue;
            chapter.VolumeId = null;
            await SaveChapter(chapter, ct);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  AuditLog
    // ═══════════════════════════════════════════════════════════

    public async Task<List<EntityAuditLog>> GetEntityAuditLog(
        string entityId,
        CancellationToken ct = default
    )
    {
        var result = new List<EntityAuditLog>();
        if (!Directory.Exists(_sessionsDir))
            return result;

        // Only scan audit_*.jsonl files — skip session JSONL to avoid O(N×M) full scan
        foreach (
            var auditPath in Directory.EnumerateFiles(
                _sessionsDir,
                "audit_*.jsonl",
                SearchOption.AllDirectories
            )
        )
        {
            var lines = await File.ReadAllLinesAsync(auditPath, ct);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<JsonlAuditEntry>(
                        line,
                        JsonHelper.JsonOptions
                    );
                    if (entry is null || entry.EntityId != entityId)
                        continue;

                    result.Add(
                        new EntityAuditLog
                        {
                            Id = result.Count + 1,
                            NovelId = entry.NovelId,
                            EntityType = EntityTypeExtensions.ParseEntityType(entry.EntityType),
                            EntityId = entityId,
                            Action = Enum.TryParse<AuditAction>(
                                entry.Action,
                                ignoreCase: true,
                                out var a
                            )
                                ? a
                                : AuditAction.Create,
                            FieldName = entry.FieldName,
                            OldValue = entry.OldValue,
                            NewValue = entry.NewValue,
                            Source = Enum.TryParse<AuditSource>(
                                entry.Source,
                                ignoreCase: true,
                                out var s
                            )
                                ? s
                                : AuditSource.Ai,
                            ToolName = entry.ToolName,
                            SessionId = entry.SessionId,
                            CreatedAt = DateTime.TryParse(entry.Timestamp, out var dt)
                                ? dt
                                : DateTime.UtcNow,
                        }
                    );
                }
                catch
                {
                    // Skip malformed lines
                }
            }
        }
        return result.OrderByDescending(l => l.CreatedAt).ToList();
    }

    public async Task WriteAuditLogs(
        string novelId,
        EntityType entityType,
        string entityId,
        AuditAction action,
        Dictionary<string, string>? fieldChanges,
        AuditSource source = AuditSource.Ai,
        string? toolName = null,
        string? sessionId = null,
        CancellationToken ct = default
    )
    {
        var sessionDir = GetSessionDir(novelId);
        Directory.CreateDirectory(sessionDir);

        var path = Path.Combine(sessionDir, $"audit_{SanitizeFileName(novelId)}.jsonl");
        var timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var entityTypeStr = entityType.ToKebabCaseString();
        var actionStr = YamlHelper.EnumToYaml(action);
        var sourceStr = YamlHelper.EnumToYaml(source);

        if (fieldChanges is not null)
        {
            foreach (var (field, change) in fieldChanges)
            {
                var parts = change.Split(" → ", 2);
                var entry = new JsonlAuditEntry
                {
                    Timestamp = timestamp,
                    NovelId = novelId,
                    EntityType = entityTypeStr,
                    EntityId = entityId,
                    Action = actionStr,
                    FieldName = field,
                    OldValue = parts.Length > 0 ? parts[0] : null,
                    NewValue = parts.Length > 1 ? parts[1] : null,
                    Source = sourceStr,
                    ToolName = toolName,
                    SessionId = sessionId,
                };
                var json = JsonSerializer.Serialize(entry, JsonHelper.JsonOptions);
                await AtomicFile.AppendLineAsync(path, json, ct: ct);
            }
        }
        else
        {
            var entry = new JsonlAuditEntry
            {
                Timestamp = timestamp,
                NovelId = novelId,
                EntityType = entityTypeStr,
                EntityId = entityId,
                Action = actionStr,
                Source = sourceStr,
                ToolName = toolName,
                SessionId = sessionId,
            };
            var json = JsonSerializer.Serialize(entry, JsonHelper.JsonOptions);
            await AtomicFile.AppendLineAsync(path, json, ct: ct);
        }
    }

    private string GetSessionDir(string novelId)
    {
        var name = GetNovelTitle(novelId) ?? novelId;
        var encoded = SanitizeFileName(name);
        return Path.Combine(_sessionsDir, $"--novels-{encoded}--");
    }

    // ═══════════════════════════════════════════════════════════
    //  Ontology journal（本体动作日志：追加式 ontology/actions.jsonl）
    // ═══════════════════════════════════════════════════════════

    /// <summary>本体动作日志文件路径。</summary>
    private string GetOntologyJournalPath(string novelId) =>
        Path.Combine(GetNovelDir(novelId), "ontology", "actions.jsonl");

    public async Task AppendOntologyActions(
        string novelId,
        IReadOnlyList<OntologyAction> actions,
        CancellationToken ct = default
    )
    {
        if (actions.Count == 0)
        {
            return;
        }

        var path = GetOntologyJournalPath(novelId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var action in actions)
        {
            var json = JsonSerializer.Serialize(
                JsonlOntologyAction.FromModel(action),
                JsonHelper.JsonOptions
            );
            await AtomicFile.AppendLineAsync(path, json, ct: ct);
        }
    }

    public async Task<List<OntologyAction>> GetOntologyActions(
        string novelId,
        CancellationToken ct = default
    )
    {
        var result = new List<OntologyAction>();
        var path = GetOntologyJournalPath(novelId);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                var dto = JsonSerializer.Deserialize<JsonlOntologyAction>(
                    line,
                    JsonHelper.JsonOptions
                );
                if (dto?.ToModel() is { } action)
                {
                    result.Add(action);
                }
            }
            catch
            {
                // 跳过损坏行，日志读取永不因单行失败整体失败
            }
        }
        return result;
    }

    public Task<long> GetOntologyJournalStamp(string novelId, CancellationToken ct = default)
    {
        var path = GetOntologyJournalPath(novelId);
        return Task.FromResult(File.Exists(path) ? new FileInfo(path).Length : 0L);
    }

    /// <summary>本体日志游标文件路径（字节偏移，跨会话摘要用）。</summary>
    private string GetOntologyCursorPath(string novelId) =>
        Path.Combine(GetNovelDir(novelId), "ontology", "cursor.yaml");

    public async Task<long> GetOntologyCursorAsync(string novelId, CancellationToken ct = default)
    {
        var path = GetOntologyCursorPath(novelId);
        if (!File.Exists(path))
            return 0L;
        try
        {
            var dict = YamlHelper.Deserialize(await File.ReadAllTextAsync(path, ct));
            return long.TryParse(GetString(dict, "offset"), out var offset) && offset > 0
                ? offset
                : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    public async Task SetOntologyCursorAsync(
        string novelId,
        long offset,
        CancellationToken ct = default
    )
    {
        var path = GetOntologyCursorPath(novelId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dict = new Dictionary<string, object?>
        {
            ["offset"] = Math.Max(0, offset).ToString(CultureInfo.InvariantCulture),
            ["updated-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        await AtomicFile.WriteAllTextAsync(path, YamlHelper.Serialize(dict), ct);
    }

    /// <summary>
    /// 从字节偏移读取本体动作日志：跳到 offset，偏移不在行首时丢弃残行。
    /// 偏移超出文件长度（文件被截断/重建）→ 按 0 处理。
    /// </summary>
    public async Task<List<OntologyAction>> GetOntologyActionsAfterOffset(
        string novelId,
        long offset,
        CancellationToken ct = default
    )
    {
        var result = new List<OntologyAction>();
        var path = GetOntologyJournalPath(novelId);
        if (!File.Exists(path))
            return result;

        var length = new FileInfo(path).Length;
        if (offset < 0 || offset > length)
            offset = 0;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true
        );
        // 偏移是否落在行首：看前一个字节是不是换行（0 偏移必然是行首）。
        var atLineStart = offset == 0;
        if (offset > 0)
        {
            stream.Seek(offset - 1, SeekOrigin.Begin);
            var previous = stream.ReadByte();
            atLineStart = previous is '\n';
        }
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!atLineStart)
            {
                // 偏移落在行中间：这一行是残行，丢弃。
                atLineStart = true;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var dto = JsonSerializer.Deserialize<JsonlOntologyAction>(
                    line,
                    JsonHelper.JsonOptions
                );
                if (dto?.ToModel() is { } action)
                    result.Add(action);
            }
            catch
            {
                // 损坏行跳过
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    //  设定变更（无章节归属的人工状态变化：evolution/manual.yaml）
    // ═══════════════════════════════════════════════════════════

    private string GetManualEvolutionPath(string novelId) =>
        Path.Combine(GetNovelDir(novelId), "evolution", "manual.yaml");

    public async Task<List<EntityStateChange>> GetManualStateChanges(
        string novelId,
        CancellationToken ct = default
    )
    {
        var path = GetManualEvolutionPath(novelId);
        if (!File.Exists(path))
            return [];
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            return YamlHelper
                .DeserializeList(text)
                .Select(dict => StateChangeFromDict(dict, novelId))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveManualStateChange(
        string novelId,
        EntityStateChange change,
        CancellationToken ct = default
    )
    {
        var path = GetManualEvolutionPath(novelId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var records = new List<EntityStateChange>();
        if (File.Exists(path))
        {
            try
            {
                var text = await File.ReadAllTextAsync(path, ct);
                records = YamlHelper
                    .DeserializeList(text)
                    .Select(dict => StateChangeFromDict(dict, novelId))
                    .ToList();
            }
            catch
            {
                records = [];
            }
        }

        // 按 Id 去重追加（撤销会写入新的反向记录，不覆盖旧记录）。
        records.RemoveAll(r => string.Equals(r.Id, change.Id, StringComparison.Ordinal));
        records.Add(change);

        var yaml = YamlHelper.SerializeList(records.Select(StateChangeToDict).ToList());
        if (!SkipConflictCheck && File.Exists(path))
            await AtomicFile.EnsureUnchangedAsync(path, ct);
        await AtomicFile.WriteAllTextAsync(path, yaml, ct);
        _semantic?.NotifyNovelChanged(novelId);
    }

    /// <summary>
    /// 人工直接改状态字段时补一条「设定变更」（ChapterId=""）。
    /// 只对白名单字段、用户来源、非变更集写入生效；失败永不阻塞实体保存本身。
    /// </summary>
    private async Task RecordManualStateChangeAsync(
        string novelId,
        EntityType entityType,
        string entityId,
        string entityName,
        Dictionary<string, object?>? oldValues,
        Dictionary<string, object?> newValues,
        bool isNew
    )
    {
        if (isNew || oldValues is null)
            return;
        var scope = OntologyWriteContext.Current;
        if (
            scope.Source != AuditSource.User
            || !string.IsNullOrEmpty(scope.ChangeSetId)
            || OntologyWriteContext.IsManualStateChangeSuppressed
        )
            return;

        try
        {
            var effects = ManualStateChangeBuilder.Build(entityType, oldValues, newValues);
            if (effects.Count == 0)
                return;
            foreach (var effect in effects)
                effect.TargetId = entityId;

            var now = DateTime.UtcNow;
            var field = effects[0].Field;
            var oldText = JoinEffectTexts(effects.Select(e => e.From));
            var newText = JoinEffectTexts(effects.Select(e => e.Value));
            var change = new EntityStateChange
            {
                Id = $"manual_{entityId}_{now:yyyyMMddHHmmssfff}",
                NovelId = novelId,
                TargetEntityType = entityType,
                TargetId = entityId,
                ChapterId = "",
                Aspect = EvolutionAspectFor(field),
                FromSummary = oldText,
                ToSummary = newText,
                Summary =
                    $"{EntityFieldRegistry.Label(entityType)}「{entityName}」{field}: {oldText} → {newText}",
                Source = AuditSource.User,
                Effects = effects,
                Sequence = 0,
                Visibility = ChangeVisibility.Public,
                Lifecycle = ChangeLifecycle.Committed,
                CommittedAt = now,
                SessionId = scope.SessionId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await SaveManualStateChange(novelId, change);
        }
        catch
        {
            // 设定变更是旁路：永不阻塞实体保存。
        }
    }

    private static string JoinEffectTexts(IEnumerable<string?> values)
    {
        var items = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return items.Count == 0 ? "" : string.Join("、", items);
    }

    private static EvolutionAspect EvolutionAspectFor(string field) =>
        field switch
        {
            "status" => EvolutionAspect.Status,
            "faction-id" or "members" or "leader" => EvolutionAspect.Relationship,
            "owner-id" => EvolutionAspect.Possession,
            "current-location-id" or "location-id" or "parent-id" => EvolutionAspect.Location,
            _ => EvolutionAspect.Other,
        };

    // ═══════════════════════════════════════════════════════════
    //  变更集（发布/补记事务，changesets/{chapterId}/{setId}.json）
    // ═══════════════════════════════════════════════════════════

    private string GetChangesetDir(string novelId, string chapterId) =>
        Path.Combine(GetNovelDir(novelId), "changesets", chapterId);

    public Task SaveChapterChangeSet(
        string novelId,
        ChapterChangeSet set,
        CancellationToken ct = default
    )
    {
        var dir = GetChangesetDir(novelId, set.ChapterId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{set.Id}.json");
        var json = JsonSerializer.Serialize(set, JsonHelper.JsonOptions);
        return AtomicFile.WriteAllTextAsync(path, json, ct);
    }

    public Task<List<ChapterChangeSet>> GetChapterChangeSets(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var dir = GetChangesetDir(novelId, chapterId);
        if (!Directory.Exists(dir))
            return Task.FromResult(new List<ChapterChangeSet>());

        var result = new List<ChapterChangeSet>();
        foreach (
            var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal)
        )
        {
            try
            {
                var set = JsonSerializer.Deserialize<ChapterChangeSet>(
                    File.ReadAllText(path),
                    JsonHelper.JsonOptions
                );
                if (set is not null)
                    result.Add(set);
            }
            catch
            {
                // 损坏文件跳过
            }
        }
        return Task.FromResult(result);
    }

    /// <summary>写入后记录本体 diff（旁路：任何异常都不阻塞实体写入）。</summary>
    private async Task TryRecordOntologyDiffAsync(
        string novelId,
        EntityType entityType,
        string entityId,
        string entityName,
        IReadOnlyDictionary<string, object?>? oldValues,
        IReadOnlyDictionary<string, object?> newValues,
        bool isNew,
        string? chapterId = null
    )
    {
        if (OntologyWriteContext.IsJournalSuppressed)
        {
            return;
        }
        try
        {
            var actions = OntologyJournal.Diff(
                novelId,
                entityType,
                entityId,
                entityName,
                oldValues,
                newValues,
                isNew,
                chapterId
            );
            if (actions.Count > 0)
            {
                await AppendOntologyActions(novelId, actions);
            }
        }
        catch
        {
            // 日志是旁路：永不阻塞实体写入。
        }
    }

    /// <summary>删除后记录本体动作（旁路）。</summary>
    private async Task TryRecordOntologyDeleteAsync(
        string novelId,
        EntityType entityType,
        string entityId,
        string entityName
    )
    {
        if (OntologyWriteContext.IsJournalSuppressed)
        {
            return;
        }
        try
        {
            await AppendOntologyActions(
                novelId,
                [OntologyJournal.Deleted(novelId, entityType, entityId, entityName)]
            );
        }
        catch
        {
            // 日志是旁路：永不阻塞实体写入。
        }
    }

    /// <summary>
    /// 读取小说标题（直接读 novel.yaml，不再维护手写 title 缓存；Phase 5 清理）。
    /// </summary>
    private string? GetNovelTitle(string novelId)
    {
        var dir = FindNovelDirById(novelId);
        if (dir is null)
            return null;

        var yamlPath = Path.Combine(dir, "novel.yaml");
        if (!File.Exists(yamlPath))
            return null;

        try
        {
            var text = AtomicFile.ReadAllText(yamlPath);
            var dict = YamlHelper.Deserialize(text);
            return GetString(dict, "title");
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Stats
    // ═══════════════════════════════════════════════════════════

    public Task<int> GetTotalWordCount(string novelId, CancellationToken ct = default)
    {
        try
        {
            var dir = FindNovelDirById(novelId);
            var chaptersDir = dir is not null ? Path.Combine(dir, "chapters") : null;
            if (chaptersDir is null || !Directory.Exists(chaptersDir))
                return Task.FromResult(0);

            // Use file sizes as a fast approximation for word count.
            // Chinese text: roughly 1 character ≈ 1 word, UTF-8 bytes / 3 ≈ character count.
            // This avoids loading full chapter content into memory.
            var totalBytes = 0L;
            foreach (var mdPath in Directory.GetFiles(chaptersDir, "*.md"))
            {
                try
                {
                    var fi = new FileInfo(mdPath);
                    totalBytes += fi.Length;
                }
                catch
                {
                    // Skip inaccessible files
                }
            }

            // Rough approximation: subtract ~200 bytes for YAML front matter per file,
            // then divide by average bytes per Chinese character (~3 for UTF-8)
            var fileCount = Directory.GetFiles(chaptersDir, "*.md").Length;
            var estimatedChars = (int)((totalBytes - fileCount * 200L) / 3);
            return Task.FromResult(Math.Max(0, estimatedChars));
        }
        catch (Exception exception)
        {
            return Task.FromException<int>(exception);
        }
    }

    /// <summary>
    /// Lightweight character name lookup by id — reads only the single YAML file
    /// instead of loading all characters. Uses entity file index for O(1) path resolution.
    /// </summary>
    public async Task<string?> GetCharacterNameById(
        string novelId,
        string characterId,
        CancellationToken ct = default
    )
    {
        if (CanUseSemantic)
        {
            if (
                _semantic!.TryGetEntityTextInNovel(
                    novelId,
                    characterId,
                    out var semanticText,
                    out var kind
                )
                && kind == "characters"
            )
            {
                var semanticDict = YamlHelper.Deserialize(semanticText);
                return GetString(semanticDict, "name");
            }
            return null;
        }

        // Direct 模式不再维护手写实体索引，直接走 novelId 范围扫描。
        var character = await GetCharacter(novelId, characterId, ct);
        return character?.Name;
    }

    // ═══════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════

    private string GetNovelDir(string novelId)
    {
        var existing = FindNovelDirById(novelId);
        return existing
            ??
            // Fallback: if not found by ID, the directory may have been created
            // by SaveNovel using the title as directory name. Scan again.
            // If still not found, create with the novelId (should not happen normally).
            Path.Combine(_workspace.ActiveRoot, SanitizeFileName(novelId));
    }

    private string? FindNovelDirById(string id)
    {
        EnsureNovelDirIndex();
        if (_novelDirIndex!.TryGetValue(id, out var dir) && Directory.Exists(dir))
            return dir;

        // Fallback: 索引可能过期（外部新增目录/清单变化），重新扫描一次
        foreach (var d in _workspace.EnumerateNovelDirectories())
        {
            var yamlPath = Path.Combine(d, "novel.yaml");
            if (!File.Exists(yamlPath))
                continue;

            try
            {
                var text = File.ReadAllText(yamlPath);
                var dict = YamlHelper.Deserialize(text);
                var foundId = GetString(dict, "id");
                if (foundId is not null)
                    _novelDirIndex[foundId] = d;
                if (foundId == id)
                    return d;
            }
            catch
            {
                // 跳过无法解析的 novel.yaml
            }
        }
        return null;
    }

    private string GetNovelIdFromDir(string dir)
    {
        // 优先从索引反向查找
        EnsureNovelDirIndex();
        foreach (var (id, d) in _novelDirIndex!)
        {
            if (d == dir)
                return id;
        }

        // Fallback: 读取 novel.yaml
        var yamlPath = Path.Combine(dir, "novel.yaml");
        if (File.Exists(yamlPath))
        {
            var text = File.ReadAllText(yamlPath);
            var dict = YamlHelper.Deserialize(text);
            var id = GetString(dict, "id");
            if (id is not null)
            {
                _novelDirIndex[id] = dir;
                return id;
            }
        }
        return Path.GetFileName(dir);
    }

    // ── 冲突检测控制（由 TUI 在 Human 写入前设为 true）──

    /// <summary>
    /// 设为 true 时，所有 Save* 方法跳过 AtomicFile.EnsureUnchangedAsync 冲突校验。
    /// 人类用户通过 TUI 写入前应设为 true，写入后恢复 false。
    /// volatile: 可被并发读写，需要跨线程可见性保证。
    /// </summary>
    public volatile bool SkipConflictCheck;

    /// <summary>
    /// 进入手动写入模式：跳过冲突检查。返回 IDisposable，dispose 时自动恢复冲突检查。
    /// 用法: <c>using (store.BeginManualWrite()) { await store.SaveChapter(ch); }</c>
    /// 这比手动 true/false 切换更安全——即使异常发生也会恢复。
    /// </summary>
    public IDisposable BeginManualWrite() => new ManualWriteScope(this);

    /// <summary>
    /// 手动写入作用域——dispose 时恢复 SkipConflictCheck = false。
    /// </summary>
    private sealed class ManualWriteScope(FileNovelStore store) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                store.SkipConflictCheck = false;
        }
    }

    // ── 方案 2B · Phase 3：Tacos 旁路读取 ──

    private bool CanUseSemantic => _semantic is not null && StoreMode != NovelStoreMode.Direct;

    private T? GetEntityViaSemantic<T>(
        string id,
        string kind,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        if (_semantic is null)
            return null;
        if (!_semantic.TryGetEntityText(id, out var text, out var novelId, out var foundKind))
            return null;
        if (foundKind != kind)
            return null;

        var dict = YamlHelper.Deserialize(text);
        var entity = factory(dict);
        entity.Id = GetString(dict, "id") ?? id;
        entity.NovelId = novelId;
        return entity;
    }

    private T? GetEntityViaSemantic<T>(
        string novelId,
        string id,
        string kind,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        if (_semantic is null)
            return null;
        if (!_semantic.TryGetEntityTextInNovel(novelId, id, out var text, out var foundKind))
            return null;
        if (foundKind != kind)
            return null;

        var dict = YamlHelper.Deserialize(text);
        var entity = factory(dict);
        entity.Id = GetString(dict, "id") ?? id;
        entity.NovelId = novelId;
        return entity;
    }

    private List<T> GetEntitiesViaSemantic<T>(
        string novelId,
        string kind,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        var result = new List<T>();
        if (_semantic is null)
            return result;

        foreach (var fileName in _semantic.GetFileNames(novelId, kind))
        {
            if (!_semantic.TryGetSourceText(novelId, kind, fileName, out var text))
                continue;
            result.Add(factory(YamlHelper.Deserialize(text)));
        }
        return result;
    }

    private Chapter? GetChapterViaSemantic(string id)
    {
        if (_semantic is null)
            return null;
        if (!_semantic.TryGetEntityText(id, out var text, out var novelId, out var foundKind))
            return null;
        return foundKind != "chapters" ? null : ReadChapterFromText(novelId, text, id);
    }

    private Chapter ReadChapterFromText(string novelId, string text, string? fallbackId)
    {
        var (meta, body) = MarkdownHelper.ParseMarkdown(text);
        var id = meta.GetValueOrDefault("id") ?? fallbackId ?? "";
        return new Chapter
        {
            Id = id,
            NovelId = novelId,
            Title = meta.GetValueOrDefault("title") ?? "",
            Order = int.TryParse(meta.GetValueOrDefault("order"), out var o) ? o : 0,
            Content = body,
            Status = YamlHelper.YamlToEnum(meta, "status", ChapterStatus.Draft),
            VolumeId = meta.GetValueOrDefault("volume-id") is { Length: > 0 } vid ? vid : null,
            Summary = meta.GetValueOrDefault("summary") ?? "",
            PlotlineIds = ParseIdList(meta.GetValueOrDefault("plotline-ids")),
            CharacterIds = ParseIdList(meta.GetValueOrDefault("character-ids")),
            Notes = meta.GetValueOrDefault("notes") ?? "",
            CreatedAt = ParseDateTime(meta.GetValueOrDefault("created-at")),
            UpdatedAt = ParseDateTime(meta.GetValueOrDefault("updated-at")),
        };
    }

    private List<Chapter> GetChaptersViaSemantic(string novelId)
    {
        var result = new List<Chapter>();
        if (_semantic is null)
            return result;

        foreach (var fileName in _semantic.GetFileNames(novelId, "chapters"))
        {
            if (_semantic.TryGetSourceText(novelId, "chapters", fileName, out var text))
                result.Add(ReadChapterFromText(novelId, text, null));
        }
        return result.OrderBy(c => c.Order).ToList();
    }

    private List<Relationship> GetRelationshipsViaSemantic(string novelId)
    {
        var result = new List<Relationship>();
        if (_semantic is null || !_semantic.TryGetRelationshipText(novelId, out var text))
            return result;

        var rels = YamlHelper.DeserializeList(text);
        result.AddRange(
            rels.Select(r => new Relationship
            {
                Id =
                    GetString(r, "id")
                    ?? $"{GetString(r, "character-a-id")}↔{GetString(r, "character-b-id")}",
                NovelId = novelId,
                CharacterAId = GetString(r, "character-a-id") ?? "",
                CharacterBId = GetString(r, "character-b-id") ?? "",
                Type =
                    RelationshipTypeExtensions.ParseRelationshipType(GetString(r, "type"))
                    ?? RelationshipType.Friend,
                Strength = YamlHelper.YamlToEnum(r, "strength", RelationshipStrength.Medium),
                Description = GetString(r, "description") ?? "",
                SinceChapterId = GetString(r, "since-chapter-id"),
                UntilChapterId = GetString(r, "until-chapter-id"),
                CreatedAt = GetDateTime(r, "created-at"),
                UpdatedAt = GetDateTime(r, "updated-at"),
            })
        );
        return result;
    }

    private async Task<T?> GetEntityAsync<T>(
        string id,
        string subDir,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        if (CanUseSemantic)
        {
            var semantic = GetEntityViaSemantic(id, subDir, factory);
            if (StoreMode == NovelStoreMode.Shadow)
            {
                var direct = await GetEntityDirectAsync(id, subDir, factory);
                ReportShadowDifference($"GetEntity<{typeof(T).Name}>", direct, semantic);
            }
            return semantic;
        }

        return await GetEntityDirectAsync(id, subDir, factory);
    }

    private async Task<T?> GetEntityDirectAsync<T>(
        string id,
        string subDir,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        EnsureNovelDirIndex();
        foreach (var novelDir in _novelDirIndex!.Values)
        {
            var entityDir = Path.Combine(novelDir, subDir);
            if (!Directory.Exists(entityDir))
                continue;

            foreach (var yamlPath in Directory.GetFiles(entityDir, "*.yaml"))
            {
                var text = await File.ReadAllTextAsync(yamlPath);
                var dict = YamlHelper.Deserialize(text);
                if (GetString(dict, "id") == id)
                {
                    var entity = factory(dict);
                    entity.NovelId = GetNovelIdFromDir(novelDir);
                    return entity;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// novelId-scoped entity lookup: directly targets the novel directory,
    /// avoiding O(N×M) global scan across all novels.
    /// </summary>
    private async Task<T?> GetEntityAsync<T>(
        string novelId,
        string id,
        string subDir,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        if (CanUseSemantic)
        {
            var semantic = GetEntityViaSemantic(novelId, id, subDir, factory);
            if (StoreMode == NovelStoreMode.Shadow)
            {
                var direct = await GetEntityDirectAsync(novelId, id, subDir, factory);
                ReportShadowDifference($"GetEntity<{typeof(T).Name}>", direct, semantic);
            }
            return semantic;
        }

        return await GetEntityDirectAsync(novelId, id, subDir, factory);
    }

    private async Task<T?> GetEntityDirectAsync<T>(
        string novelId,
        string id,
        string subDir,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        var dir = FindNovelDirById(novelId);
        if (dir is null)
            return null;

        var entityDir = Path.Combine(dir, subDir);
        if (!Directory.Exists(entityDir))
            return null;

        foreach (var yamlPath in Directory.GetFiles(entityDir, "*.yaml"))
        {
            var text = await File.ReadAllTextAsync(yamlPath);
            var dict = YamlHelper.Deserialize(text);
            if (GetString(dict, "id") == id)
            {
                var entity = factory(dict);
                entity.NovelId = novelId;
                return entity;
            }
        }
        return null;
    }

    private async Task<List<T>> GetEntitiesAsync<T>(
        string novelId,
        string subDir,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        if (CanUseSemantic)
        {
            var semantic = GetEntitiesViaSemantic(novelId, subDir, factory);
            if (StoreMode == NovelStoreMode.Shadow)
            {
                var direct = await GetEntitiesDirectAsync(novelId, subDir, factory);
                ReportShadowDifference($"GetEntities<{typeof(T).Name}>", direct, semantic);
            }
            return semantic;
        }

        return await GetEntitiesDirectAsync(novelId, subDir, factory);
    }

    private async Task<List<T>> GetEntitiesDirectAsync<T>(
        string novelId,
        string subDir,
        Func<Dictionary<string, object?>, T> factory
    )
        where T : Entity
    {
        var result = new List<T>();
        var dir = GetNovelDir(novelId);
        var entityDir = Path.Combine(dir, subDir);
        if (!Directory.Exists(entityDir))
            return result;

        foreach (var yamlPath in Directory.GetFiles(entityDir, "*.yaml").OrderBy(p => p))
        {
            var text = await File.ReadAllTextAsync(yamlPath);
            var dict = YamlHelper.Deserialize(text);
            result.Add(factory(dict));
        }
        return result;
    }

    private async Task SaveEntityAsync(
        string novelId,
        string subDir,
        string fileName,
        string entityId,
        Dictionary<string, object?> dict,
        EntityType entityType,
        string entityName
    )
    {
        var dir = GetNovelDir(novelId);
        var entityDir = Path.Combine(dir, subDir);
        Directory.CreateDirectory(entityDir);

        // 判断 Create/Update：优先查实体索引（entityId 已存在 → Update），
        // 而非仅靠 File.Exists（当实体 Name 变更导致文件名变化时，旧文件仍存在但新路径不存在，
        // File.Exists 会误判为 Create）。
        // 方案 2B 下优先使用 Tacos 的 id→文件路径映射，避免再构建手写实体索引。
        bool existsInIndex;
        string? oldPath;
        // notes 不是 Tacos 探针的 kind（ProbeNovelCore 不探 notes）：走语义索引会恒判 Create、
        // 且拿不到旧文件名 → 改标题时旧文件不删（同一 id 两份）。笔记固定走目录扫描。
        if (CanUseSemantic && !string.Equals(subDir, "notes", StringComparison.Ordinal))
        {
            existsInIndex = _semantic!.TryGetEntityPath(novelId, entityId, out var semanticOldPath);
            oldPath = semanticOldPath;
        }
        else
        {
            // Direct 模式不再维护手写实体索引；扫描目录确认 Create/Update 与旧路径。
            existsInIndex = false;
            oldPath = null;
            if (Directory.Exists(entityDir))
            {
                foreach (var candidate in Directory.GetFiles(entityDir, "*.yaml"))
                {
                    try
                    {
                        var candidateDict = YamlHelper.Deserialize(
                            await File.ReadAllTextAsync(candidate)
                        );
                        if (GetString(candidateDict, "id") == entityId)
                        {
                            existsInIndex = true;
                            oldPath = candidate;
                            break;
                        }
                    }
                    catch
                    {
                        // 跳过损坏文件
                    }
                }
            }
        }
        var isNew = !existsInIndex;

        // 旧值必须在写盘/删旧文件之前读取：更新同一路径时文件马上会被覆盖。
        Dictionary<string, object?>? oldDict = null;
        if (existsInIndex && oldPath is not null && File.Exists(oldPath))
        {
            try
            {
                oldDict = YamlHelper.Deserialize(await File.ReadAllTextAsync(oldPath));
            }
            catch
            {
                oldDict = null;
            }
        }

        // 如果实体已存在但文件名变了（如角色改名），删除旧文件
        if (existsInIndex && oldPath is not null)
        {
            var safeFileName = SanitizeFileName(fileName);
            var newPath = Path.Combine(entityDir, safeFileName);
            if (oldPath != newPath && File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
        }

        var safeName = SanitizeFileName(fileName);
        var path = Path.Combine(entityDir, safeName);
        var yaml = YamlHelper.Serialize(dict);
        if (!SkipConflictCheck && File.Exists(path))
            await AtomicFile.EnsureUnchangedAsync(path);
        await AtomicFile.WriteAllTextAsync(path, yaml);

        var action = isNew ? AuditAction.Create : AuditAction.Update;
        await FireEntityChanged(
            new EntityChangeEvent(entityType, entityId, action, novelId, entityName)
        );
        await TryRecordOntologyDiffAsync(
            novelId,
            entityType,
            entityId,
            entityName,
            oldDict,
            dict,
            isNew,
            string.Equals(subDir, "scenes", StringComparison.Ordinal)
                ? GetString(dict, "chapter-id")
                : null
        );
        await RecordManualStateChangeAsync(
            novelId,
            entityType,
            entityId,
            entityName,
            oldDict,
            dict,
            isNew
        );
    }

    private async Task DeleteEntityAsync(string id, string subDir, EntityType entityType)
    {
        EnsureNovelDirIndex();
        foreach (var novelDir in _novelDirIndex!.Values)
        {
            var entityDir = Path.Combine(novelDir, subDir);
            if (!Directory.Exists(entityDir))
                continue;

            var novelId = GetNovelIdFromDir(novelDir);
            foreach (var yamlPath in Directory.GetFiles(entityDir, "*.yaml"))
            {
                var text = await File.ReadAllTextAsync(yamlPath);
                var dict = YamlHelper.Deserialize(text);
                if (GetString(dict, "id") == id)
                {
                    await PublishedReferenceGuard.EnsureCanDeleteAsync(
                        this,
                        novelId,
                        entityType,
                        id
                    );
                    var name = GetString(dict, "name") ?? id;
                    File.Delete(yamlPath);
                    await FireEntityChanged(
                        new EntityChangeEvent(entityType, id, AuditAction.Delete, novelId, name)
                    );
                    await TryRecordOntologyDeleteAsync(novelId, entityType, id, name);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// novelId-scoped entity deletion: directly targets the novel directory,
    /// avoiding O(N×M) global scan.
    /// </summary>
    private async Task DeleteEntityAsync(
        string novelId,
        string id,
        string subDir,
        EntityType entityType
    )
    {
        var dir = FindNovelDirById(novelId);
        if (dir is null)
            return;

        var entityDir = Path.Combine(dir, subDir);
        if (!Directory.Exists(entityDir))
            return;

        foreach (var yamlPath in Directory.GetFiles(entityDir, "*.yaml"))
        {
            var text = await File.ReadAllTextAsync(yamlPath);
            var dict = YamlHelper.Deserialize(text);
            if (GetString(dict, "id") == id)
            {
                await PublishedReferenceGuard.EnsureCanDeleteAsync(this, novelId, entityType, id);
                var name = GetString(dict, "name") ?? id;
                File.Delete(yamlPath);
                await FireEntityChanged(
                    new EntityChangeEvent(entityType, id, AuditAction.Delete, novelId, name)
                );
                await TryRecordOntologyDeleteAsync(novelId, entityType, id, name);
                return;
            }
        }
    }

    private async Task FireEntityChanged(EntityChangeEvent evt)
    {
        // 方案 2B · Phase 4：写盘成功后只向单写者队列投递刷新意图，不在保存栈内 Set Input。
        _semantic?.NotifyNovelChanged(evt.NovelId);

        // 结构索引失效（目录戳本也会失配；显式清除省掉一次目录枚举）。
        if (evt.NovelId is { Length: > 0 } novelId)
            _structureCache.TryRemove(novelId, out _);

        if (OnEntityChanged is not null)
            await OnEntityChanged(evt);
    }

    private static string? GetString(Dictionary<string, object?> dict, string key) =>
        StoreHelper.GetString(dict, key);

    private static int GetInt(Dictionary<string, object?> dict, string key) =>
        StoreHelper.GetInt(dict, key);

    private static bool GetBool(Dictionary<string, object?> dict, string key, bool fallback) =>
        bool.TryParse(GetString(dict, key), out var value) ? value : fallback;

    /// <summary>
    /// 从字典中读取字符串列表，兼容 YAML 序列格式和旧版 JSON 数组字符串。
    /// 用于 Tags、Members、ParticipantIds、PlotlineIds 等字段。
    /// </summary>
    private static Dictionary<string, double> ReadMetrics(Dictionary<string, object?> dict)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!dict.TryGetValue("metrics", out var raw) || raw is not Dictionary<string, object?> map)
        {
            return result;
        }
        foreach (var (key, value) in map)
        {
            if (
                value is string text
                && double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed
                )
            )
            {
                result[key] = parsed;
            }
        }
        return result;
    }

    private static Dictionary<string, object?>? MetricsToDict(Dictionary<string, double> metrics) =>
        metrics.Count == 0 ? null : metrics.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    private static List<string> GetStringList(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val is null)
            return [];

        switch (val)
        {
            // 新格式：YAML 序列 → List<object?>
            case List<object?> list:
                return list.Select(v => v?.ToString() ?? "").Where(s => s.Length > 0).ToList();
            // 旧格式：JSON 数组字符串（如 '["warrior","brave"]'）
            case string s when s.StartsWith('['):
                try
                {
                    return JsonSerializer.Deserialize<List<string>>(s) ?? [];
                }
                catch
                {
                    return [];
                }
            default:
                return [];
        }
    }

    /// <summary>读取道具所有权时段链（CHANGE_10 v4；缺键/空 → 空链，单值 OwnerId 兜底在 builder）。</summary>
    private static List<ItemOwnershipSpan> GetOwnershipPeriods(
        Dictionary<string, object?> dict,
        string key
    )
    {
        if (!dict.TryGetValue(key, out var val) || val is not List<object?> list)
            return [];
        var result = new List<ItemOwnershipSpan>();
        foreach (var entry in list)
        {
            if (entry is not Dictionary<string, object?> d)
                continue;
            var owner = GetString(d, "owner-id");
            if (string.IsNullOrWhiteSpace(owner))
                continue;
            result.Add(
                new ItemOwnershipSpan
                {
                    OwnerId = owner,
                    SinceChapterId = GetString(d, "since-chapter-id"),
                    UntilChapterId = GetString(d, "until-chapter-id"),
                }
            );
        }
        return result;
    }

    /// <summary>读取地点邻接表（CHANGE_10 v4；距离按 WorldRule 先例存字符串再 double.TryParse）。</summary>
    private static List<LocationAdjacency>? GetLocationAdjacency(
        Dictionary<string, object?> dict,
        string key
    )
    {
        if (!dict.TryGetValue(key, out var val) || val is not List<object?> list)
            return null;
        var result = new List<LocationAdjacency>();
        foreach (var entry in list)
        {
            if (entry is not Dictionary<string, object?> d)
                continue;
            var target = GetString(d, "target-id");
            if (string.IsNullOrWhiteSpace(target))
                continue;
            var distance = double.TryParse(GetString(d, "distance-li"), out var v) ? v : 0;
            result.Add(new LocationAdjacency { TargetId = target, DistanceLi = distance });
        }
        return result.Count > 0 ? result : null;
    }

    private static DateTime GetDateTime(Dictionary<string, object?> dict, string key) =>
        StoreHelper.GetDateTime(dict, key);

    private static DateTime ParseDateTime(string? str) => StoreHelper.ParseDateTime(str);

    private static string SanitizeFileName(string name) => StoreHelper.SanitizeFileName(name);

    /// <summary>
    /// Search for a chapter by id within a specific novel directory.
    /// Extracted from GetChapter to enable reuse by both global and novelId-scoped lookups.
    /// </summary>
    private async Task<Chapter?> FindChapterInDir(string novelDir, string id)
    {
        var chaptersDir = Path.Combine(novelDir, "chapters");
        if (!Directory.Exists(chaptersDir))
            return null;

        var isOrder = int.TryParse(id, out var orderId);
        foreach (var mdPath in Directory.GetFiles(chaptersDir, "*.md"))
        {
            var text = await File.ReadAllTextAsync(mdPath);
            var (meta, body) = MarkdownHelper.ParseMarkdown(text);
            if (
                meta.GetValueOrDefault("id") == id
                || (isOrder && meta.GetValueOrDefault("order") == id)
            )
            {
                var novelId = GetNovelIdFromDir(novelDir);
                var actualId = meta.GetValueOrDefault("id") ?? id;
                return new Chapter
                {
                    Id = actualId,
                    NovelId = novelId,
                    Title = meta.GetValueOrDefault("title") ?? "",
                    Order = int.TryParse(meta.GetValueOrDefault("order"), out var o) ? o : 0,
                    Content = body,
                    Status = YamlHelper.YamlToEnum(meta, "status", ChapterStatus.Draft),
                    VolumeId = meta.GetValueOrDefault("volume-id") is { Length: > 0 } vid
                        ? vid
                        : null,
                    Summary = meta.GetValueOrDefault("summary") ?? "",
                    PlotlineIds = ParseIdList(meta.GetValueOrDefault("plotline-ids")),
                    CharacterIds = ParseIdList(meta.GetValueOrDefault("character-ids")),
                    Notes = meta.GetValueOrDefault("notes") ?? "",
                    CreatedAt = ParseDateTime(meta.GetValueOrDefault("created-at")),
                    UpdatedAt = ParseDateTime(meta.GetValueOrDefault("updated-at")),
                };
            }
        }
        return null;
    }

    /// <summary>
    /// Try to delete a chapter by id within a specific novel directory.
    /// Returns true if the chapter was found and deleted.
    /// </summary>
    private async Task<bool> TryDeleteChapterInDir(string novelDir, string id)
    {
        var chaptersDir = Path.Combine(novelDir, "chapters");
        if (!Directory.Exists(chaptersDir))
            return false;

        foreach (var mdPath in Directory.GetFiles(chaptersDir, "*.md"))
        {
            var text = await File.ReadAllTextAsync(mdPath);
            var (meta, _) = MarkdownHelper.ParseMarkdown(text);
            if (meta.GetValueOrDefault("id") == id)
            {
                var novelId = GetNovelIdFromDir(novelDir);
                var title = meta.GetValueOrDefault("title") ?? id;
                File.Delete(mdPath);
                // ENTITY-EVOLUTION §3.2 章级联：删章 = 删该章两个演化分片（O(1)，每分片只含该章记录）
                await DeleteChapterAppearances(novelId, id);
                await DeleteEntityStateChanges(novelId, id);
                // 变更集随章删除（发布/撤回记录不再有意义）
                var changesetDir = Path.Combine(novelDir, "changesets", id);
                if (Directory.Exists(changesetDir))
                    Directory.Delete(changesetDir, recursive: true);
                await FireEntityChanged(
                    new EntityChangeEvent(EntityType.Chapter, id, AuditAction.Delete, novelId, id)
                );
                await TryRecordOntologyDeleteAsync(novelId, EntityType.Chapter, id, title);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Search for a relationship by id within a specific novel directory.
    /// </summary>
    private async Task<Relationship?> FindRelationshipInDir(
        string novelDir,
        string id,
        string? knownNovelId = null
    )
    {
        var path = Path.Combine(novelDir, "relationships.yaml");
        if (!File.Exists(path))
            return null;

        var text = await File.ReadAllTextAsync(path);
        var rels = YamlHelper.DeserializeList(text);

        foreach (var r in rels)
        {
            if (GetString(r, "id") == id)
            {
                var novelId = knownNovelId ?? GetNovelIdFromDir(novelDir);
                return new Relationship
                {
                    Id = id,
                    NovelId = novelId,
                    CharacterAId = GetString(r, "character-a-id") ?? "",
                    CharacterBId = GetString(r, "character-b-id") ?? "",
                    Type =
                        RelationshipTypeExtensions.ParseRelationshipType(GetString(r, "type"))
                        ?? RelationshipType.Friend,
                    Strength = YamlHelper.YamlToEnum(r, "strength", RelationshipStrength.Medium),
                    Description = GetString(r, "description") ?? "",
                    SinceChapterId = GetString(r, "since-chapter-id"),
                    UntilChapterId = GetString(r, "until-chapter-id"),
                    CreatedAt = GetDateTime(r, "created-at"),
                    UpdatedAt = GetDateTime(r, "updated-at"),
                };
            }
        }
        return null;
    }

    /// <summary>
    /// Try to delete a relationship by id within a specific novel directory.
    /// Returns true if the relationship was found and deleted.
    /// </summary>
    private async Task<bool> TryDeleteRelationshipInDir(
        string novelDir,
        string id,
        string? knownNovelId = null
    )
    {
        var path = Path.Combine(novelDir, "relationships.yaml");
        if (!File.Exists(path))
            return false;

        var text = await File.ReadAllTextAsync(path);
        var rels = YamlHelper.DeserializeList(text);
        var novelId = knownNovelId ?? GetNovelIdFromDir(novelDir);

        var idx = rels.FindIndex(r => GetString(r, "id") == id);
        if (idx >= 0)
        {
            var old = rels[idx];
            var name = $"{GetString(old, "character-a-id")}↔{GetString(old, "character-b-id")}";
            rels.RemoveAt(idx);
            var yaml = YamlHelper.SerializeList(rels);
            if (!SkipConflictCheck)
                await AtomicFile.EnsureUnchangedAsync(path);
            await AtomicFile.WriteAllTextAsync(path, yaml);
            await FireEntityChanged(
                new EntityChangeEvent(EntityType.Relationship, id, AuditAction.Delete, novelId, id)
            );
            await TryRecordOntologyDeleteAsync(novelId, EntityType.Relationship, id, name);
            return true;
        }
        return false;
    }
}
