using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Core.Agent;
using Core.Shared;
using LlmTornado.Chat;
using LlmTornado.Code;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Data;

/// <summary>
/// 基于 JSONL 的会话存储实现。
/// 使用 append-only JSONL 格式，每行一个 JSON 对象，通过 type 字段区分条目类型。
/// </summary>
public sealed class FileSessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = JsonlSerializer.Options;

    private readonly NovelWorkspace _workspace;
    private readonly string _sessionsDir;
    private readonly string _inputHistoryDir;

    /// <summary>单目录构造（测试/兼容既有调用方）。</summary>
    public FileSessionStore(string? novelsRoot = null, string? mythologyDir = null)
        : this(
            NovelWorkspace.Single(
                novelsRoot ?? Path.Combine(Directory.GetCurrentDirectory(), "novels")
            ),
            mythologyDir
        ) { }

    /// <summary>工作区构造：小说标题缓存覆盖全部已登记目录；清单变化时失效重建。</summary>
    public FileSessionStore(NovelWorkspace workspace, string? mythologyDir = null)
    {
        _workspace = workspace;
        var mythDir =
            mythologyDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mythology"
            );
        _sessionsDir = Path.Combine(mythDir, "sessions");
        _inputHistoryDir = Path.Combine(mythDir, "input-history");

        Directory.CreateDirectory(_sessionsDir);
        Directory.CreateDirectory(_inputHistoryDir);

        workspace.Changed += InvalidateNovelTitles;
    }

    /// <summary>项目目录清单变化 → 丢弃 novelId↔标题 缓存（会话目录名按标题编码）。</summary>
    private void InvalidateNovelTitles()
    {
        _novelIdToTitle = null;
        _sanitizedTitleToNovelId = null;
    }

    /// <summary>
    /// 设为 true 时跳过 AtomicFile.EnsureUnchangedAsync 冲突校验。
    /// 人类用户通过 TUI 写入前设为 true，写入后恢复 false。
    /// </summary>
    public bool SkipConflictCheck { get; set; }

    /// <summary>
    /// ParseAllEntries 结果缓存。Key = JSONL 文件路径。
    /// 当文件 mtime 未变时复用解析结果，避免每次 GetContextMessages/GetActiveMessages/GetTreeStructure
    /// 都全量读取和解析 JSONL 文件。写入后由 InvalidateParseCache 失效。
    /// 容量限制：最多缓存 32 个文件，超出时清除最早写入的条目。
    /// </summary>
    private sealed record ParseCacheEntry(
        DateTime Mtime,
        List<ParsedEntry> Entries,
        string? LeafTargetId,
        Dictionary<string, List<string>> LabelMap,
        DateTime LastAccessed = default
    );

    private const int MaxParseCacheSize = 128;

    private const long MaxActiveSegmentBytes = 8L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, ParseCacheEntry> _parseCache = new();

    /// <summary>
    /// 会话 ID → (novelId, sessionDir) 索引，避免 GetSession 全目录扫描。
    /// sessionDir 是会话目录路径（分片格式）或旧格式的 JSONL 文件父目录。
    /// 启动后首次调用时懒构建，新会话写入时增量更新。
    /// </summary>
    private Dictionary<string, (string NovelId, string SessionDir)>? _sessionIndex;

    /// <summary>
    /// 会话 ID → 分片管理器。用于分片格式的会话存储和并发控制。
    /// </summary>
    private readonly ConcurrentDictionary<string, SessionSegmentManager> _segmentManagers = new();

    /// <summary>
    /// 当前进程内正在 compact 的会话 ID 集合（防止重入）。
    /// </summary>
    private readonly HashSet<string> _compactingSessions = [];

    /// <summary>
    /// novelId → title 缓存，避免 GetNovelTitle 每次遍历所有 novel 目录读取 yaml。
    /// 懒构建，首次调用 GetNovelTitle 或 GetNovelIdFromSessionDir 时填充。
    /// </summary>
    private Dictionary<string, string>? _novelIdToTitle;

    /// <summary>
    /// SanitizeFileName(title) → novelId 反向索引，供 GetNovelIdFromSessionDir O(1) 查找。
    /// 与 _novelIdToTitle 同步构建。
    /// </summary>
    private Dictionary<string, string>? _sanitizedTitleToNovelId;

    /// <summary>
    /// 确保 novelId→title 缓存已构建（懒加载）。
    /// </summary>
    private void EnsureNovelTitleCache()
    {
        if (_novelIdToTitle is not null)
            return;

        _novelIdToTitle = [];
        _sanitizedTitleToNovelId = [];

        foreach (var dir in _workspace.EnumerateNovelDirectories())
        {
            var yamlPath = Path.Combine(dir, "novel.yaml");
            if (!File.Exists(yamlPath))
                continue;
            try
            {
                var text = AtomicFile.ReadAllText(yamlPath);
                var dict = YamlHelper.Deserialize(text);
                var id = GetString(dict, "id");
                var title = GetString(dict, "title");
                if (id is not null && title is not null)
                {
                    _novelIdToTitle[id] = title;
                    _sanitizedTitleToNovelId[SanitizeFileName(title)] = id;
                }
            }
            catch
            {
                // 跳过
            }
        }
    }

    /// <summary>
    /// 生成带时间戳的会话目录名。格式：{yyyy-MM-ddTHH-mm-ss-fffZ}_{sessionId}
    /// 例如：2026-07-01T08-30-00-123Z_abc123def456
    /// 时间戳前缀使文件系统浏览时可按时间排序，清晰识别会话创建时间。
    /// </summary>
    private static string FormatSessionDirName(string sessionId)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        return $"{timestamp}_{sessionId}";
    }

    /// <summary>
    /// 确保会话索引已构建（懒加载）。
    /// </summary>
    private void EnsureSessionIndex()
    {
        if (_sessionIndex is not null)
            return;

        _sessionIndex = new Dictionary<string, (string, string)>();
        if (!Directory.Exists(_sessionsDir))
            return;

        foreach (var novelDir in Directory.GetDirectories(_sessionsDir))
        {
            var novelId = GetNovelIdFromSessionDir(novelDir);

            // 先扫描新格式：目录（会话分片），从 MANIFEST 读取 sessionId 作为索引键
            foreach (var sessionSubDir in Directory.GetDirectories(novelDir))
            {
                var dirName = Path.GetFileName(sessionSubDir);
                if (dirName.StartsWith("audit_"))
                    continue;
                var manifestPath = Path.Combine(sessionSubDir, "MANIFEST.json");
                if (!File.Exists(manifestPath))
                    continue;
                // 从 MANIFEST 读取 sessionId 作为索引键，目录名可以是任意格式（含时间戳）
                var manifest = ReadManifest(manifestPath);
                var sid = manifest?.SessionId;
                if (sid is not null && !_sessionIndex.ContainsKey(sid))
                    _sessionIndex[sid] = (novelId, sessionSubDir);
            }

            // 再扫描旧格式：*.jsonl 文件（兼容迁移前数据，首次访问时自动迁移）
            foreach (var jsonlPath in Directory.GetFiles(novelDir, "*.jsonl"))
            {
                var fileName = Path.GetFileNameWithoutExtension(jsonlPath);
                if (fileName.StartsWith("audit_"))
                    continue;
                // 新格式索引已存在时跳过（新格式优先）
                if (_sessionIndex.ContainsKey(fileName))
                    continue;
                // 注册旧格式索引，写入方法首次访问时会触发迁移
                _sessionIndex[fileName] = (novelId, jsonlPath);
            }
        }
    }

    /// <summary>
    /// 将新会话注册到索引（SaveSessionMessages 首次写入时调用）。
    /// sessionPath 是会话目录路径（新格式）或 JSONL 文件路径（旧格式兼容）。
    /// </summary>
    private void RegisterSessionIndex(string sessionId, string novelId, string sessionPath)
    {
        EnsureSessionIndex();
        _sessionIndex![sessionId] = (novelId, sessionPath);
    }

    public async Task<AgentSession?> GetSession(string id, CancellationToken ct = default)
    {
        EnsureSessionIndex();
        if (_sessionIndex!.TryGetValue(id, out var info))
        {
            // 新格式：从 MANIFEST 读取
            var manifestPath = Path.Combine(info.SessionDir, "MANIFEST.json");
            if (File.Exists(manifestPath))
            {
                var session = BuildSessionFromManifest(id, info.NovelId, info.SessionDir);
                if (session is not null)
                    return session;
            }

            // 旧格式兼容：从 JSONL 文件读取
            if (File.Exists(info.SessionDir) && Path.GetExtension(info.SessionDir) == ".jsonl")
                return await BuildSessionFromJsonlAsync(id, info.NovelId, info.SessionDir);
        }

        return null;
    }

    public async Task<List<AgentSession>> GetSessions(
        string novelId,
        CancellationToken ct = default
    )
    {
        var result = new List<AgentSession>();
        var baseDir = GetSessionDir(novelId);
        if (!Directory.Exists(baseDir))
            return result;

        // 新格式：扫描子目录（会话分片目录）
        foreach (var subDir in Directory.GetDirectories(baseDir))
        {
            var dirName = Path.GetFileName(subDir);
            if (dirName.StartsWith("audit_"))
                continue;
            var manifestPath = Path.Combine(subDir, "MANIFEST.json");
            if (!File.Exists(manifestPath))
                continue;
            var session = BuildSessionFromManifest(dirName, novelId, subDir);
            if (session is not null)
                result.Add(session);
        }

        // 旧格式兼容：扫描 *.jsonl 文件（迁移前数据）
        foreach (var jsonlPath in Directory.GetFiles(baseDir, "*.jsonl"))
        {
            var fileName = Path.GetFileNameWithoutExtension(jsonlPath);
            if (fileName.StartsWith("audit_"))
                continue;
            // 避免与已添加的目录格式重复
            if (result.Any(s => s.Id == fileName))
                continue;
            var session = await BuildSessionFromJsonlAsync(fileName, novelId, jsonlPath);
            if (session is not null)
                result.Add(session);
        }

        return result.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <summary>
    /// 删除会话（JSONL 分片目录或旧格式单文件）及其全部内存态：
    /// 会话索引、分片管理器缓存、解析缓存、compact 防重入标记。
    /// 解析缓存按路径键控且缓存优先（命中不触盘），不清会致"已删会话复活"。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>索引中存在该会话（已删除或本就不在磁盘）返回 true；无此会话返回 false。</returns>
    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        EnsureSessionIndex();
        if (!_sessionIndex!.TryGetValue(sessionId, out var rec))
            return false;

        // 活跃写入（compact 分片滚动）与删除互斥：管理器在场时先拿其会话锁
        var mgr = _segmentManagers.TryGetValue(sessionId, out var cached) ? cached : null;
        if (mgr is not null)
            await mgr.AcquireLockAsync(ct);
        try
        {
            var target = rec.SessionDir;
            // 新格式首次写入会把活跃分片文件路径（而非会话目录）注册进索引
            // （SaveSessionMessages → RegisterSessionIndex(mgr.ActivePath)）；
            // 目录里还有 MANIFEST 时它其实是分片文件 → 归一化到父目录，删整个会话目录。
            var parent = Path.GetDirectoryName(target);
            if (
                !Directory.Exists(target)
                && parent is not null
                && Path.GetExtension(target).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(parent, "MANIFEST.json"))
            )
            {
                target = parent;
            }

            if (Directory.Exists(target))
            {
                // 缓存清理要在磁盘删除前取路径清单
                foreach (var segPath in GetExistingSegmentPaths(target))
                    InvalidateParseCache(segPath);
                Directory.Delete(target, recursive: true);
            }
            else if (File.Exists(target))
            {
                // 旧格式单 .jsonl（父目录无 MANIFEST）
                InvalidateParseCache(target);
                File.Delete(target);
            }
        }
        finally
        {
            if (mgr is not null)
                mgr.ReleaseLock();
        }

        _sessionIndex.Remove(sessionId);
        _segmentManagers.TryRemove(sessionId, out _);
        _compactingSessions.Remove(sessionId);
        return true;
    }

    /// <summary>会话目录下现存的分片文件路径（按 seq 升序，供解析缓存失效）。</summary>
    private static IEnumerable<string> GetExistingSegmentPaths(string sessionDir)
    {
        try
        {
            return Directory
                .GetFiles(sessionDir, "*.jsonl")
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  消息写入
    // ═══════════════════════════════════════════════════════════

    public async Task<string?> SaveSessionMessages(
        string sessionId,
        string novelId,
        IReadOnlyList<LlmChatMessage> newMessages,
        string? lastParentEntryId = null,
        int startOrder = 0,
        CancellationToken ct = default
    )
    {
        if (newMessages.Count == 0)
            return lastParentEntryId;

        // 使用分片存储（新格式）
        var mgr = GetOrCreateSegmentManager(sessionId, novelId);
        await mgr.AcquireLockAsync();
        try
        {
            RotateActiveSegmentIfNeeded(mgr, startOrder);
            var path = mgr.ActivePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var previousEntryId = lastParentEntryId;
            var lastEntryId = previousEntryId;

            var lines = new List<string>(newMessages.Count);

            // 仅 tool-call batch 才建立索引；普通 user/text batch 不分配临时 Dictionary。
            Dictionary<string, string>? toolCallNameIndex = null;
            foreach (var m in newMessages)
            {
                if (m.Role != ChatMessageRoles.Assistant || m.ToolCalls is not { Count: > 0 })
                    continue;

                toolCallNameIndex ??= new Dictionary<string, string>();
                foreach (var tc in m.ToolCalls)
                {
                    if (tc.Id is not null && tc.FunctionCall?.Name is { } name)
                        toolCallNameIndex[tc.Id] = name;
                }
            }

            for (var i = 0; i < newMessages.Count; i++)
            {
                var m = newMessages[i];
                var text = m.Content ?? "";
                var reasoningText = "";

                if (m.Parts is not null && m.Parts.Count > 0)
                {
                    text = string.Join(
                        "",
                        m.Parts.Where(p => p.Type == ChatMessageTypes.Text).Select(p => p.Text)
                    );
                    reasoningText = string.Join(
                        "\n\n",
                        m.Parts.Where(p =>
                                p is { Type: ChatMessageTypes.Reasoning, Reasoning: not null }
                            )
                            .Select(p => p.Reasoning!.Content ?? "")
                    );
                }
                if (
                    string.IsNullOrEmpty(reasoningText) && !string.IsNullOrEmpty(m.ReasoningContent)
                )
                {
                    reasoningText = m.ReasoningContent;
                }

                var role = MessageRoleMapping.ToRoleString(m.Role ?? ChatMessageRoles.User);

                var toolNameForSave = m.Name;
                if (
                    role == "tool"
                    && string.IsNullOrEmpty(toolNameForSave)
                    && m.ToolCallId is not null
                    && toolCallNameIndex is not null
                    && toolCallNameIndex.TryGetValue(m.ToolCallId, out var foundName)
                )
                {
                    toolNameForSave = foundName;
                }

                var entryId = GenerateEntryId();
                var order = startOrder + i;

                string? toolCallsJson = null;
                if (m.ToolCalls is not null && m.ToolCalls.Count > 0)
                {
                    // Use strong-typed DTO instead of anonymous type — STJ caches serialization
                    // metadata for named types, yielding better performance than reflection per call
                    var toolCalls = m
                        .ToolCalls.Where(tc => tc.FunctionCall is not null)
                        .Select(tc => new ToolCallItemDto
                        {
                            Id = tc.Id,
                            Name = tc.FunctionCall!.Name,
                            Arguments = tc.FunctionCall.Arguments,
                        })
                        .ToList();
                    toolCallsJson = JsonSerializer.Serialize(toolCalls, JsonOpts);
                }

                var entry = new JsonlMessageEntry
                {
                    Id = entryId,
                    ParentId = previousEntryId,
                    Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Role = role,
                    Content = text,
                    ReasoningContent = string.IsNullOrEmpty(reasoningText) ? null : reasoningText,
                    ToolName = toolNameForSave,
                    ToolCallId = m.ToolCallId,
                    IsError = m.ToolInvocationSucceeded == false ? true : null,
                    Order = order,
                    ToolCalls = toolCallsJson,
                    ContextTokens = m.Tokens,
                };
                previousEntryId = entryId;
                lastEntryId = entryId;

                lines.Add(JsonSerializer.Serialize(entry, JsonOpts));
            }

            await AtomicFile.AppendLinesDurableAsync(
                path,
                lines,
                checkConflict: !SkipConflictCheck
            );
            InvalidateParseCache(path);
            RegisterSessionIndex(sessionId, novelId, path);

            // 更新 MANIFEST
            UpdateManifestSegment(mgr, lines.Count, lines.Count, lastEntryId);

            return lastEntryId;
        }
        finally
        {
            mgr.ReleaseLock();
        }
    }

    /// <summary>
    /// 写入 compact 条目并滚动分片。返回 (compact 条目 entryId, blockId)。
    /// </summary>
    public async Task<(string? EntryId, string BlockId)> CompactSession(
        string sessionId,
        string novelId,
        string summary,
        string firstKeptEntryId,
        int messagesBefore,
        string? parentEntryId = null,
        int order = 0,
        string? reason = null,
        bool willRetry = false,
        int estimatedTokensAfter = 0,
        List<string>? compressedMessageIds = null,
        CancellationToken ct = default
    )
    {
        var mgr = GetOrCreateSegmentManager(sessionId, novelId);

        // 获取会话级别写锁，防止与 SaveSessionMessages 并发
        var compactLockPath = Path.Combine(mgr.SessionDir, "compact.lock");
        using var writeLock = await AtomicFile.AcquireWriteLockAsync(compactLockPath);

        await mgr.AcquireLockAsync();
        try
        {
            // BlockId = 新分片 seq：分片全局递增且不重用，天然唯一稳定（B2）。
            var newSeq = mgr.ActiveSeq + 1;
            var blockId = $"b{newSeq}";
            var path = mgr.ActivePath;

            var compactEntryId = GenerateEntryId();
            var compactEntry = new JsonlCompactEntry
            {
                Id = compactEntryId,
                ParentId = parentEntryId,
                Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Summary = summary,
                FirstKeptEntryId = firstKeptEntryId,
                MessagesBefore = messagesBefore,
                Order = order,
                Reason = reason,
                WillRetry = willRetry ? true : null,
                EstimatedTokensAfter = estimatedTokensAfter > 0 ? estimatedTokensAfter : null,
                CompressedMessageIds = compressedMessageIds ?? [],
                BlockId = blockId,
            };

            // 从当前活跃分片读取保留消息行
            var retainedLines = await ReadRetainedLinesAsync(path, firstKeptEntryId);

            // 写新分片文件：compact 行 + 保留消息
            var newFilePath = Path.Combine(mgr.SessionDir, $"{newSeq:D6}.jsonl");
            var allLines = new List<string>(1 + retainedLines.Count)
            {
                JsonSerializer.Serialize(compactEntry, JsonOpts),
            };
            allLines.AddRange(retainedLines);
            await File.WriteAllLinesAsync(newFilePath, allLines);

            // 创建新分段的 MANIFEST 记录
            var newSegment = new ManifestSegmentRecord
            {
                Seq = newSeq,
                File = $"{newSeq:D6}.jsonl",
                StartOrder = order,
                EndOrder = order + retainedLines.Count,
                MessageCount = retainedLines.Count,
                EntryCount = 1 + retainedLines.Count,
                FileSize = new FileInfo(newFilePath).Length,
                CompactEntryId = compactEntryId,
                HasCompact = true,
                CreatedAt = DateTime.UtcNow,
            };

            // 更新 MANIFEST
            var segments = new List<ManifestSegmentRecord>(mgr.Manifest.Segments) { newSegment };
            var now = DateTime.UtcNow;
            mgr.ReplaceManifest(
                new SessionManifest
                {
                    Version = mgr.Manifest.Version,
                    SessionId = mgr.Manifest.SessionId,
                    NovelId = mgr.Manifest.NovelId,
                    Title = mgr.Manifest.Title,
                    CreatedAt = mgr.Manifest.CreatedAt,
                    UpdatedAt = now,
                    Segments = segments,
                    ActiveSegment = newSeq,
                    CompactCount = mgr.Manifest.CompactCount + 1,
                    WindowCount = mgr.Manifest.WindowCount,
                    TotalEntryCount = mgr.Manifest.TotalEntryCount,
                    TokenEstimate =
                        estimatedTokensAfter > 0
                            ? estimatedTokensAfter
                            : mgr.Manifest.TokenEstimate,
                }
            );

            WriteManifest(mgr.SessionDir, mgr.Manifest);
            InvalidateParseCache(path);
            InvalidateParseCache(newFilePath);

            return (compactEntryId, blockId);
        }
        finally
        {
            mgr.ReleaseLock();
        }
    }

    /// <summary>
    /// 开启新的一页上下文：滚动到新分片并在其首行写 window 条目。
    /// 不生成摘要、不复制任何消息（旧分片 append-only 保留全部历史）。
    /// </summary>
    /// <returns>(WindowId, EntryId, Order)。WindowId = $"w{newSeq}"；Order 为 window 行的 order。</returns>
    public async Task<(string WindowId, string EntryId, int Order)> StartNewContextWindowAsync(
        string sessionId,
        string novelId,
        string reason,
        int order,
        string? note = null,
        CancellationToken ct = default
    )
    {
        var mgr = GetOrCreateSegmentManager(sessionId, novelId);

        // 与会话写入共用同一把锁，防止与 SaveSessionMessages / CompactSession 并发
        var windowLockPath = Path.Combine(mgr.SessionDir, "compact.lock");
        using var writeLock = await AtomicFile.AcquireWriteLockAsync(windowLockPath);

        await mgr.AcquireLockAsync();
        try
        {
            var newSeq = mgr.ActiveSeq + 1;
            var windowId = $"w{newSeq}";
            var newFile = $"{newSeq:D6}.jsonl";
            var newPath = Path.Combine(mgr.SessionDir, newFile);

            var previousWindowId = await FindLatestWindowIdAsync(sessionId, ct) ?? "w0";
            var entryId = GenerateEntryId();
            var windowEntry = new JsonlWindowEntry
            {
                Id = entryId,
                ParentId = mgr.ActiveLastEntryId,
                Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                WindowId = windowId,
                PrevWindowId = previousWindowId,
                FirstWindowId = "w0",
                Reason = reason,
                Note = note,
                Order = order,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            await AtomicFile.AppendLinesDurableAsync(
                newPath,
                [JsonSerializer.Serialize(windowEntry, JsonOpts)],
                checkConflict: !SkipConflictCheck,
                ct: ct
            );
            InvalidateParseCache(newPath);

            var newSegment = new ManifestSegmentRecord
            {
                Seq = newSeq,
                File = newFile,
                StartOrder = order,
                EndOrder = order,
                MessageCount = 0,
                EntryCount = 1,
                FileSize = new FileInfo(newPath).Length,
                WindowId = windowId,
                HasWindow = true,
                CreatedAt = DateTime.UtcNow,
            };

            var segments = new List<ManifestSegmentRecord>(mgr.Manifest.Segments) { newSegment };
            mgr.ReplaceManifest(
                new SessionManifest
                {
                    Version = mgr.Manifest.Version,
                    SessionId = mgr.Manifest.SessionId,
                    NovelId = mgr.Manifest.NovelId,
                    Title = mgr.Manifest.Title,
                    CreatedAt = mgr.Manifest.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                    Segments = segments,
                    ActiveSegment = newSeq,
                    CompactCount = mgr.Manifest.CompactCount,
                    TotalEntryCount = mgr.Manifest.TotalEntryCount + 1,
                    TokenEstimate = 0,
                    WindowCount = mgr.Manifest.WindowCount + 1,
                }
            );

            WriteManifest(mgr.SessionDir, mgr.Manifest);

            // ★ 元数据链：window 行成为会话最后一条条目（占用一个 order），
            // 恢复会话时 GetSessionMetadataAsync 会从它推导 nextOrder 与 parentEntryId。
            mgr.ActiveLastEntryId = entryId;

            return (windowId, entryId, order);
        }
        finally
        {
            mgr.ReleaseLock();
        }
    }

    /// <summary>取会话最新的页 ID（无 window 条目返回 null）。</summary>
    private async Task<string?> FindLatestWindowIdAsync(string sessionId, CancellationToken ct)
    {
        var windows = await ListWindowsCoreAsync(sessionId, ct);
        return windows.Count > 1 ? windows[^1].WindowId : null;
    }

    /// <summary>
    /// 读取会话当前页边界；无 window 条目时返回首页 ("w0", null, "w0", 0)。
    /// </summary>
    public async Task<ContextWindowState> GetWindowBoundaryAsync(
        string sessionId,
        CancellationToken ct = default
    )
    {
        var windows = await ListWindowsCoreAsync(sessionId, ct);
        var current = windows[^1];
        return new ContextWindowState(
            current.WindowId,
            current.PrevWindowId,
            "w0",
            current.StartOrder,
            TaskAnchor: null,
            RollReason: current.Reason,
            HandoffNote: current.Note
        );
    }

    /// <summary>
    /// 列出会话所有页（含隐式首页 w0），按 order 升序。
    /// </summary>
    public async Task<List<SessionWindowInfo>> ListWindowsAsync(
        string sessionId,
        CancellationToken ct = default
    ) => await ListWindowsCoreAsync(sessionId, ct);

    /// <summary>
    /// 页枚举核心：跨分片合并解析 → 取 window 条目 → 拼出页区间与每页消息数。
    /// </summary>
    private async Task<List<SessionWindowInfo>> ListWindowsCoreAsync(
        string sessionId,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        List<ParsedEntry> entries;
        var mgr = GetSegmentManager(sessionId);
        if (mgr is not null && mgr.ActiveFileExists)
        {
            (entries, _) = await MergeEntriesAcrossSegments(mgr, sessionId);
        }
        else
        {
            var sessionPath = ResolveSessionDir(sessionId);
            if (sessionPath is null || !File.Exists(sessionPath))
                return [BuildFirstWindow(0)];
            (entries, _, _) = await ReadAndParseJsonlAsync(sessionPath, sessionId);
        }

        var windowEntries = entries
            .Where(e => e.Type == JsonlEntryType.Window)
            .OrderBy(e => e.Order)
            .ToList();

        var pages = new List<SessionWindowInfo>(windowEntries.Count + 1) { BuildFirstWindow(0) };
        foreach (var w in windowEntries)
        {
            if (string.IsNullOrEmpty(w.WindowId))
                continue;
            pages.Add(
                new SessionWindowInfo(
                    w.WindowId,
                    w.PrevWindowId ?? pages[^1].WindowId,
                    w.Order + 1,
                    0,
                    w.EntryId,
                    w.Timestamp == default ? null : w.Timestamp.ToString("O"),
                    w.WindowReason,
                    w.WindowNote
                )
            );
        }

        // 每页消息数：order 落在 [本页 StartOrder, 下一页 StartOrder) 的 message 条目
        var messageOrders = entries
            .Where(e => e.Type == JsonlEntryType.Message)
            .Select(e => e.Order)
            .ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            var lower = pages[i].StartOrder;
            var upper = i + 1 < pages.Count ? pages[i + 1].StartOrder : int.MaxValue;
            var count = messageOrders.Count(o => o >= lower && o < upper);
            pages[i] = pages[i] with { ItemCount = count };
        }

        return pages;
    }

    /// <summary>隐式首页 w0（未发生换页时唯一的一页）。</summary>
    private static SessionWindowInfo BuildFirstWindow(int itemCount) =>
        new("w0", null, 0, itemCount, null, null, null);

    // ═══════════════════════════════════════════════════════════
    //  history_* 检索（跨页查阅被封存的历史消息与工具输出）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 列出历史条目（可跨页）。windowId 为 null = 全部页。
    /// 注意：与 GetContextMessages 不同，history 查询<b>不做页截断、不做 compact 截断</b>——
    /// 它的语义就是「查阅被封存的历史」。
    /// </summary>
    public async Task<List<HistoryItemInfo>> ListHistoryItemsAsync(
        string sessionId,
        string? windowId,
        string? role,
        string? toolName,
        int limit,
        bool recentFirst,
        int maxCharsPerItem,
        CancellationToken ct = default
    )
    {
        var (entries, pages) = await LoadHistoryAsync(sessionId, ct);
        var items = entries
            .Where(e => e.Type == JsonlEntryType.Message)
            .Select(e => ToHistoryItem(e, pages, maxCharsPerItem))
            .Where(i => MatchesWindow(i, windowId) && MatchesRoleAndTool(i, role, toolName));

        return OrderAndTrim(items, limit, recentFirst);
    }

    /// <summary>大小写敏感字面子串检索（Codex 同款）：命中消息 Content 或工具调用参数。</summary>
    public async Task<List<HistoryItemInfo>> SearchHistoryContentsAsync(
        string sessionId,
        string query,
        string? windowId,
        string? role,
        string? toolName,
        int limit,
        bool recentFirst,
        CancellationToken ct = default
    )
    {
        var (entries, pages) = await LoadHistoryAsync(sessionId, ct);
        var items = entries
            .Where(e => e.Type == JsonlEntryType.Message)
            .Where(e => ContainsOrdinal(e, query))
            .Select(e => ToHistoryItem(e, pages, maxCharsPerItem: 0))
            .Where(i => MatchesWindow(i, windowId) && MatchesRoleAndTool(i, role, toolName));

        return OrderAndTrim(items, limit, recentFirst);
    }

    /// <summary>
    /// 按页 ID + 条目 ID 读取历史条目原文（返回未截断内容，供调用方按 offset/limit 切片）。
    /// </summary>
    public async Task<(HistoryItemInfo Item, string FullContent)?> ReadHistoryItemAsync(
        string sessionId,
        string windowId,
        string itemId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        var (entries, pages) = await LoadHistoryAsync(sessionId, ct);
        var entry = entries.FirstOrDefault(e =>
            e.Type == JsonlEntryType.Message
            && string.Equals(e.EntryId, itemId, StringComparison.Ordinal)
        );
        if (entry is null)
            return null;

        var item = ToHistoryItem(entry, pages, maxCharsPerItem: 0);
        if (!string.IsNullOrEmpty(windowId) && !MatchesWindow(item, windowId))
            return null;

        return (item, item.Content);
    }

    /// <summary>合并读取条目与页区间（history 查询共用；旧格式会话走单文件解析）。</summary>
    private async Task<(List<ParsedEntry> Entries, List<SessionWindowInfo> Pages)> LoadHistoryAsync(
        string sessionId,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        List<ParsedEntry> entries;
        var mgr = GetSegmentManager(sessionId);
        if (mgr is not null && mgr.ActiveFileExists)
        {
            (entries, _) = await MergeEntriesAcrossSegments(mgr, sessionId);
        }
        else
        {
            var sessionPath = ResolveSessionDir(sessionId);
            if (sessionPath is null || !File.Exists(sessionPath))
                return ([], [BuildFirstWindow(0)]);
            (entries, _, _) = await ReadAndParseJsonlAsync(sessionPath, sessionId);
        }

        var windowEntries = entries
            .Where(e => e.Type == JsonlEntryType.Window)
            .OrderBy(e => e.Order)
            .ToList();
        var pages = new List<SessionWindowInfo>(windowEntries.Count + 1) { BuildFirstWindow(0) };
        foreach (var w in windowEntries)
        {
            if (string.IsNullOrEmpty(w.WindowId))
                continue;
            pages.Add(
                new SessionWindowInfo(
                    w.WindowId,
                    w.PrevWindowId ?? pages[^1].WindowId,
                    w.Order + 1,
                    0,
                    w.EntryId,
                    w.Timestamp == default ? null : w.Timestamp.ToString("O"),
                    w.WindowReason,
                    w.WindowNote
                )
            );
        }

        return (entries, pages);
    }

    /// <summary>条目 → 历史视图（maxCharsPerItem &lt;= 0 表示不截断）。</summary>
    private static HistoryItemInfo ToHistoryItem(
        ParsedEntry entry,
        List<SessionWindowInfo> pages,
        int maxCharsPerItem
    )
    {
        var content = entry.Message?.Content ?? "";
        if (maxCharsPerItem > 0 && content.Length > maxCharsPerItem)
            content = content[..maxCharsPerItem] + "…（已截断，用 history_read_item 读取全文）";

        return new HistoryItemInfo(
            entry.EntryId ?? "",
            ResolveWindowId(pages, entry.Order),
            entry.Order,
            entry.Role ?? "unknown",
            entry.Message?.ToolName,
            entry.Timestamp == default ? null : entry.Timestamp.ToString("O"),
            content
        );
    }

    /// <summary>order 落在哪一页（页区间查表）。</summary>
    private static string ResolveWindowId(List<SessionWindowInfo> pages, int order)
    {
        var windowId = pages[0].WindowId;
        foreach (var page in pages)
        {
            if (page.StartOrder <= order)
                windowId = page.WindowId;
            else
                break;
        }
        return windowId;
    }

    private static bool MatchesWindow(HistoryItemInfo item, string? windowId) =>
        string.IsNullOrEmpty(windowId)
        || string.Equals(item.WindowId, windowId, StringComparison.Ordinal);

    private static bool MatchesRoleAndTool(HistoryItemInfo item, string? role, string? toolName) =>
        (
            string.IsNullOrEmpty(role)
            || string.Equals(item.Role, role, StringComparison.OrdinalIgnoreCase)
        )
        && (
            string.IsNullOrEmpty(toolName)
            || string.Equals(item.ToolName, toolName, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>大小写敏感字面子串匹配（消息正文 + 工具调用参数），Codex 同款语义。</summary>
    private static bool ContainsOrdinal(ParsedEntry entry, string query)
    {
        var content = entry.Message?.Content;
        if (content is not null && content.Contains(query, StringComparison.Ordinal))
            return true;
        var toolCalls = entry.Message?.ToolCallsJson;
        return toolCalls is not null && toolCalls.Contains(query, StringComparison.Ordinal);
    }

    private static List<HistoryItemInfo> OrderAndTrim(
        IEnumerable<HistoryItemInfo> items,
        int limit,
        bool recentFirst
    )
    {
        var max = limit <= 0 ? 20 : Math.Min(limit, 200);
        var ordered = recentFirst
            ? items.OrderByDescending(i => i.Order)
            : items.OrderBy(i => i.Order);
        return ordered.Take(max).ToList();
    }

    /// <summary>
    /// 从 JSONL 文件中读取 firstKeptEntryId 及其之后的所有行。
    /// firstKeptEntryId 是保留的第一条消息的 ID，该行本身也会被包含在结果中。
    /// </summary>
    private static async Task<List<string>> ReadRetainedLinesAsync(
        string path,
        string firstKeptEntryId
    )
    {
        var lines = await File.ReadAllLinesAsync(path);
        var result = new List<string>(lines.Length);
        var found = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (!found)
            {
                if (line.Contains($"\"id\":\"{firstKeptEntryId}\""))
                {
                    found = true;
                    result.Add(line); // 包含 firstKeptEntryId 所在行本身
                }
                continue;
            }
            result.Add(line);
        }
        return result;
    }

    public async Task SaveSessionEvent(
        string sessionId,
        string novelId,
        SessionEvent evt,
        CancellationToken ct = default
    )
    {
        var mgr = GetOrCreateSegmentManager(sessionId, novelId);
        await mgr.AcquireLockAsync();
        try
        {
            var path = mgr.ActivePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var entryId = evt.EntryId ?? GenerateEntryId();
            var entry = new JsonlEventEntry
            {
                Id = entryId,
                ParentId = evt.ParentEntryId,
                Timestamp = evt.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                EventType = evt.EventType,
                Order = evt.Order,
                ErrorCode = evt.ErrorCode,
                Content = evt.Content,
                From = evt.From,
                To = evt.To,
                SelectedVariantIndex =
                    evt.EventType == SessionEventType.VariantSelected
                        ? evt.SelectedVariantIndex
                        : null,
                SelectedLabel =
                    evt.EventType == SessionEventType.VariantSelected ? evt.SelectedLabel : null,
            };

            var json = JsonSerializer.Serialize(entry, JsonOpts);
            await AtomicFile.AppendLinesDurableAsync(
                path,
                [json],
                checkConflict: !SkipConflictCheck
            );
            InvalidateParseCache(path);

            UpdateManifestSegment(mgr, 1, 0, entryId);
        }
        finally
        {
            mgr.ReleaseLock();
        }
    }

    public async Task SaveSessionLeaf(
        string sessionId,
        string novelId,
        SessionLeaf leaf,
        CancellationToken ct = default
    )
    {
        var mgr = GetOrCreateSegmentManager(sessionId, novelId);
        await mgr.AcquireLockAsync();
        try
        {
            var path = mgr.ActivePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var entry = new JsonlLeafEntry { TargetId = leaf.TargetId };

            var json = JsonSerializer.Serialize(entry, JsonOpts);
            await AtomicFile.AppendLinesDurableAsync(
                path,
                [json],
                checkConflict: !SkipConflictCheck
            );
            InvalidateParseCache(path);

            UpdateManifestSegment(mgr, 1, 0, null);
        }
        finally
        {
            mgr.ReleaseLock();
        }
    }

    public async Task SaveBranchSummary(
        string sessionId,
        string novelId,
        BranchSummary summary,
        CancellationToken ct = default
    )
    {
        var mgr = GetOrCreateSegmentManager(sessionId, novelId);
        await mgr.AcquireLockAsync();
        try
        {
            var path = mgr.ActivePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var entryId = summary.EntryId ?? GenerateEntryId();
            var entry = new JsonlBranchSummaryEntry
            {
                Id = entryId,
                ParentId = summary.ParentEntryId,
                Timestamp = summary.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                FromId = summary.FromId,
                Summary = summary.Summary,
                Order = summary.Order,
            };

            var json = JsonSerializer.Serialize(entry, JsonOpts);
            await AtomicFile.AppendLinesDurableAsync(
                path,
                [json],
                checkConflict: !SkipConflictCheck
            );
            InvalidateParseCache(path);

            UpdateManifestSegment(mgr, 1, 0, entryId);
        }
        finally
        {
            mgr.ReleaseLock();
        }
    }

    public async Task SaveSessionLabel(
        string sessionId,
        string novelId,
        SessionLabel label,
        CancellationToken ct = default
    )
    {
        var mgr = GetOrCreateSegmentManager(sessionId, novelId);
        await mgr.AcquireLockAsync();
        try
        {
            var path = mgr.ActivePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var entryId = label.EntryId ?? GenerateEntryId();
            var entry = new JsonlLabelEntry
            {
                Id = entryId,
                ParentId = label.ParentEntryId,
                Timestamp = label.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                TargetId = label.TargetId,
                Label = label.Label,
                Order = label.Order,
            };

            var json = JsonSerializer.Serialize(entry, JsonOpts);
            await AtomicFile.AppendLinesDurableAsync(
                path,
                [json],
                checkConflict: !SkipConflictCheck
            );
            InvalidateParseCache(path);

            UpdateManifestSegment(mgr, 1, 0, entryId);
        }
        finally
        {
            mgr.ReleaseLock();
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  消息读取（统一入口）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 消息渲染模式，控制 compact/event 条目的输出格式。
    /// </summary>
    private enum MessageRenderMode
    {
        /// <summary>LLM 上下文：compact 注入为 UserMessage，event 不输出。</summary>
        Context,

        /// <summary>UI 渲染：compact 以 Unknown+EventType="compact" 返回，event 以 Unknown 返回。</summary>
        Active,
    }

    public Task<List<ChatMessage>> GetContextMessages(
        string sessionId,
        CancellationToken ct = default
    ) => GetMessagesInternal(sessionId, MessageRenderMode.Context);

    public Task<List<ChatMessage>> GetActiveMessages(
        string sessionId,
        CancellationToken ct = default
    ) => GetMessagesInternal(sessionId, MessageRenderMode.Active);

    /// <summary>
    /// 通过会话索引查找会话路径（目录或旧格式 JSONL 文件路径），避免全目录扫描。
    /// 返回目录路径（新格式）或文件路径（旧格式兼容）。
    /// </summary>
    private string? ResolveSessionDir(string sessionId)
    {
        EnsureSessionIndex();
        return _sessionIndex!.TryGetValue(sessionId, out var info) ? info.SessionDir : null;
    }

    /// <summary>
    /// 跨全部分片合并解析条目（B5 分支读取）。
    ///
    /// 背景：compact 滚动分片后，旧分片保留完整历史（append-only），但活跃路径
    /// 的消息可能在旧分片（分支切换后尤其如此）。GetMessagesInternal 原只读最新分片，
    /// 导致分支 A 的消息在 compact 后切回 A 时读不到。
    ///
    /// 策略：从新到旧遍历分片，按 EntryId 去重（新分片优先 = 保留行副本是权威），
    /// leaf 取最新分片的。返回按 Order 升序（BuildMessagesFromEntries 依赖 Order 递增）。
    /// </summary>
    private async Task<(
        List<ParsedEntry> Entries,
        string? LeafTargetId
    )> MergeEntriesAcrossSegments(SessionSegmentManager mgr, string sessionId)
    {
        var segmentPaths = mgr.AllSegmentPaths;

        // P5 快速路径：单分片（从未 compact 的常见场景）无需去重/排序——
        // 单文件 append-only 保证 EntryId 唯一且 Order 递增，直接返回。
        // 多分片场景才有去重（保留行副本优先）+ 排序开销。
        if (segmentPaths.Count == 1)
        {
            var onlyPath = segmentPaths[0];
            if (File.Exists(onlyPath))
            {
                var (entries, segLeaf, _) = await ReadAndParseJsonlAsync(onlyPath, sessionId);
                return (entries, segLeaf);
            }
            return ([], null);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<ParsedEntry>();
        string? leafTargetId = null;

        // 从新到旧：新分片的保留行副本优先，旧分片补漏（被压缩消息只在旧分片）
        foreach (var segPath in Enumerable.Reverse(segmentPaths))
        {
            if (!File.Exists(segPath))
                continue;
            var (entries, segLeaf, _) = await ReadAndParseJsonlAsync(segPath, sessionId);
            // ★ 只取最新分片的 segLeaf（从新到旧遍历，第一个非 null 即最新）；
            // 旧分片的 segLeaf 是历史尾部，若覆盖会把 leaf 指向过期消息，
            // 导致 activeIds 回溯链错位（B5 修复）。
            if (leafTargetId is null && segLeaf is not null)
                leafTargetId = segLeaf;

            foreach (var e in entries)
            {
                if (e.EntryId is null || seen.Add(e.EntryId))
                    merged.Add(e);
            }
        }

        // BuildMessagesFromEntries 依赖 Order 递增（JSONL append-only 约定）——合并后必须显式排序
        merged.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return (merged, leafTargetId);
    }

    /// <summary>
    /// 统一的消息读取逻辑，消除 GetContextMessages 与 GetActiveMessages 的重复代码。
    /// 两者唯一差异在于 compact 和 event 条目的渲染方式。
    /// 新格式：跨分片合并读取（B5：分支/compact 后旧分片内容可见）；旧格式兼容：读取 JSONL 文件。
    /// </summary>
    private async Task<List<ChatMessage>> GetMessagesInternal(
        string sessionId,
        MessageRenderMode mode
    )
    {
        // 优先使用新格式（分片存储）
        var mgr = GetSegmentManager(sessionId);
        if (mgr is not null && mgr.ActiveFileExists)
        {
            // B5：跨分片合并（EntryId 去重 + Order 排序），分支/compact 后旧分片内容仍可见
            var (entries, leafTargetId) = await MergeEntriesAcrossSegments(mgr, sessionId);
            return BuildMessagesFromEntries(entries, leafTargetId, mode, sessionId);
        }

        // 旧格式兼容
        var sessionPath = ResolveSessionDir(sessionId);
        if (sessionPath is null)
            return [];

        // sessionPath 可能是旧格式的 JSONL 文件路径
        if (File.Exists(sessionPath) && Path.GetExtension(sessionPath) == ".jsonl")
        {
            var (entries, leafTargetId, _) = await ReadAndParseJsonlAsync(sessionPath, sessionId);
            return BuildMessagesFromEntries(entries, leafTargetId, mode, sessionId);
        }

        return [];
    }

    /// <summary>
    /// 从解析后的条目构建消息列表。
    /// 提取为单独方法避免 GetMessagesInternal 中重复代码。
    /// </summary>
    private static List<ChatMessage> BuildMessagesFromEntries(
        List<ParsedEntry> entries,
        string? leafTargetId,
        MessageRenderMode mode,
        string sessionId
    )
    {
        if (entries.Count == 0)
            return [];

        var (activeIds, idMap) = ResolveActivePath(entries, leafTargetId);

        // 计算 leaf 目标条目的 order，用于崩溃场景判断（leaf 之后追加的消息应可见）
        var leafOrder =
            leafTargetId is not null && idMap.TryGetValue(leafTargetId, out var leafEntry)
                ? leafEntry.Order
                : int.MaxValue;

        // 推导 latestCompact：单次遍历找 order 最大的 compact 条目（B5）。
        // ★ 不按 activeIds 过滤：跨分片合并后，compact 条目是分片头部标记，其 parentId
        // 链不在 leaf 回溯路径上（回溯沿消息的 parentId 走），activeIds 过滤会误判 compact
        // 为非活跃 → compactFromOrder 解析失败 → 旧分片被压缩消息泄漏回可见列表。
        // 取 order 最大者 = 最近一次压缩，其 FirstKeptEntryId 截断点天然覆盖会话前缀；
        // 分支场景下废弃分支的 compact（order 更小）不会当选，不影响活跃分支。
        ParsedEntry? latestCompact = null;
        foreach (
            var e in entries
                .Where(e => e.Type == JsonlEntryType.Compact)
                .Where(e => latestCompact is null || e.Order > latestCompact.Order)
        )
        {
            latestCompact = e;
        }

        // 确定 compact 截断点
        var compactFromOrder = ResolveCompactFromOrder(latestCompact, idMap);

        // 页边界：最新 window 条目的 order（无 window 条目 = -1）。
        // ★ 仅模型口径（Context）生效：换页后旧页的一切内容（消息 / 分支摘要 / 旧 compact 摘要）
        // 都不进模型上下文，这正是 Codex「新窗不携带摘要」的空页语义。
        // Active 模式（UI / 会话管理）不做页截断，仍可看到全部历史。
        var windowFromOrder = -1;
        if (mode == MessageRenderMode.Context)
        {
            foreach (var e in entries)
            {
                if (e.Type == JsonlEntryType.Window && e.Order > windowFromOrder)
                    windowFromOrder = e.Order;
            }
        }

        // 过滤 + 渲染（JSONL append-only 保证 order 递增，无需额外排序）
        var messages = new List<ChatMessage>();

        foreach (var entry in entries)
        {
            // ★ 页截断：封页（order ≤ 最新 window 行）的内容一律不进模型上下文
            if (windowFromOrder >= 0 && entry.Order <= windowFromOrder)
                continue;

            switch (entry.Type)
            {
                // branch_summary
                case JsonlEntryType.BranchSummary:
                {
                    if (
                        activeIds is not null
                        && entry.EntryId is not null
                        && !activeIds.Contains(entry.EntryId)
                    )
                    {
                        if (
                            entry.ParentEntryId is null
                            || !activeIds.Contains(entry.ParentEntryId)
                            || entry.Order <= leafOrder
                        )
                            continue;
                    }
                    if (compactFromOrder >= 0 && entry.Order < compactFromOrder)
                        continue;

                    messages.Add(
                        new ChatMessage
                        {
                            Id = entry.Order,
                            SessionId = sessionId,
                            Role = MessageRole.User,
                            Content = $"[之前尝试的分支摘要] {entry.DisplayText}",
                            Order = entry.Order,
                            EntryId = entry.EntryId,
                            ParentEntryId = entry.ParentEntryId,
                            CreatedAt = entry.Timestamp,
                        }
                    );
                    continue;
                }
                // compact 条目：根据渲染模式差异化输出
                // 新格式分片存储中，compact 条目始终在活跃路径上（它是分片的头部标记）。
                // 旧格式兼容：如果 compact 不在活跃路径上，检查其 parentId 链。
                // B5：跨分片合并后所有 compact 条目都在 entries 中，但只渲染最新一个
                // （previousSummary 链已把历史摘要并入最新摘要，避免上下文 token 随压缩次数累积）。
                // 历史 block 仍可通过 decompress(ListCompactBlocksAsync) 枚举定位。
                case JsonlEntryType.Compact
                    when latestCompact is not null && entry.EntryId != latestCompact.EntryId:
                // ★ B5：compact 是全局摘要（覆盖会话前缀，分支间共享），不做 activeIds/
                // parentId 链检查——分支切换后挂载点（压缩时最后一条消息）不在新活跃路径，
                // 但摘要内容对当前分支仍有效（被压缩的早期消息已从可见列表移除，
                // 必须靠摘要承载上下文）。latestCompact（order 最大）即最近一次压缩。
                case JsonlEntryType.Compact when string.IsNullOrEmpty(entry.CompactSummary):
                    continue;
                case JsonlEntryType.Compact:
                {
                    if (mode == MessageRenderMode.Context)
                    {
                        // B2：摘要标签带 blockId（[上下文摘要 b1]），会话恢复后模型仍可 decompress；
                        // 迁移前旧数据无 blockId，降级为无标签摘要。
                        var label = entry.CompactBlockId is not null
                            ? $"[上下文摘要 {entry.CompactBlockId}]"
                            : "[上下文摘要]";
                        messages.Add(
                            new ChatMessage
                            {
                                Id = entry.Order,
                                SessionId = sessionId,
                                Role = MessageRole.User,
                                Content = $"{label}\n{entry.CompactSummary}",
                                Order = entry.Order,
                                EntryId = entry.EntryId,
                                ParentEntryId = entry.ParentEntryId,
                                CreatedAt = entry.Timestamp,
                            }
                        );
                    }
                    else
                    {
                        messages.Add(
                            new ChatMessage
                            {
                                Id = entry.Order,
                                SessionId = sessionId,
                                Role = MessageRole.Unknown,
                                Content = entry.CompactSummary,
                                Order = entry.Order,
                                EntryId = entry.EntryId,
                                ParentEntryId = entry.ParentEntryId,
                                CreatedAt = entry.Timestamp,
                                EventType = SessionEventType.Compact,
                            }
                        );
                    }
                    continue;
                }
                // label 条目不输出到 messages
                case JsonlEntryType.Label:
                // event 条目
                case JsonlEntryType.Event when mode == MessageRenderMode.Context:
                    continue;
                case JsonlEntryType.Event:
                {
                    if (entry.Message is not null)
                    {
                        if (
                            activeIds is not null
                            && entry.EntryId is not null
                            && !activeIds.Contains(entry.EntryId)
                        )
                        {
                            if (
                                entry.ParentEntryId is null
                                || !activeIds.Contains(entry.ParentEntryId)
                                || entry.Order <= leafOrder
                            )
                                continue;
                        }
                        if (compactFromOrder >= 0 && entry.Order < compactFromOrder)
                            continue;

                        messages.Add(entry.Message);
                    }
                    continue;
                }
            }

            // message 条目
            if (entry.Type != JsonlEntryType.Message || entry.Message is null)
                continue;

            if (
                activeIds is not null
                && entry.EntryId is not null
                && !activeIds.Contains(entry.EntryId)
            )
            {
                if (
                    entry.ParentEntryId is null
                    || !activeIds.Contains(entry.ParentEntryId)
                    || entry.Order <= leafOrder
                )
                    continue;
            }

            if (compactFromOrder >= 0 && entry.Order < compactFromOrder)
                continue;

            messages.Add(entry.Message);
        }

        BackfillToolNames(messages);
        return DeduplicateByOrder(messages);
    }

    // ═══════════════════════════════════════════════════════════
    //  轻量元数据
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Lightweight compact summary extraction — returns the latest compact summary text
    /// from ParsedEntry without building ChatMessage objects. Used by PrepareAndCompactAsync
    /// to avoid redundant full GetContextMessages/GetActiveMessages calls.
    /// 新格式：读取最新分片；旧格式兼容：读取 JSONL 文件。
    /// </summary>
    public async Task<string?> GetLatestCompactSummaryAsync(
        string sessionId,
        CancellationToken ct = default
    )
    {
        // 新格式：跨分片合并读取（B5：分支/compact 后最新 compact 可能在旧分片）
        var mgr = GetSegmentManager(sessionId);
        if (mgr is not null && mgr.ActiveFileExists)
        {
            var (entries, leafTargetId) = await MergeEntriesAcrossSegments(mgr, sessionId);
            return ExtractLatestCompactSummary(entries, leafTargetId);
        }

        // 旧格式兼容
        var path = ResolveSessionDir(sessionId);
        if (path is null || !File.Exists(path))
            return null;
        if (Path.GetExtension(path) != ".jsonl")
            return null;

        var (entriesOld, leafTargetIdOld, _) = await ReadAndParseJsonlAsync(path, sessionId);
        return ExtractLatestCompactSummary(entriesOld, leafTargetIdOld);
    }

    /// <summary>
    /// 按 blockId 跨分片读取被压缩消息的原文（B2 decompress 核心）。
    ///
    /// 分片滚动是 append-only：compact 时新分片 = [compact 行 + 保留消息]，
    /// 旧分片不删除，被压缩消息的原文行永远在旧分片中。
    /// 从 MANIFEST 全部分片从新到旧扫描，命中所有 entryId 即提前终止。
    /// </summary>
    /// <returns>按 entryIds 原始顺序的消息列表；blockId 不存在返回 null。</returns>
    public async Task<List<ChatMessage>?> ReadCompactedMessagesAsync(
        string sessionId,
        string blockId,
        CancellationToken ct = default
    )
    {
        var mgr = GetSegmentManager(sessionId);
        if (mgr is null)
            return null;

        // 1. 从新到旧遍历分片，找 blockId 匹配的 compact 条目（最新分片先查，命中率高）
        List<string>? targetIds = null;
        foreach (var segPath in Enumerable.Reverse(mgr.AllSegmentPaths))
        {
            if (!File.Exists(segPath))
                continue;
            var (entries, _, _) = await ReadAndParseJsonlAsync(segPath, sessionId);
            var compact = entries.FirstOrDefault(e =>
                e.Type == JsonlEntryType.Compact && e.CompactBlockId == blockId
            );
            if (compact is null)
                continue;
            targetIds = compact.CompactMessageIds;
            break;
        }

        // 迁移前旧数据：无 BlockId/CompressedMessageIds，无法定位原文
        if (targetIds is null || targetIds.Count == 0)
            return null;

        // 2. 跨全部分片收集这些 entryId 的原文（从新到旧，命中即停）
        var remaining = new HashSet<string>(targetIds, StringComparer.Ordinal);
        var found = new Dictionary<string, ChatMessage>(remaining.Count);
        foreach (var segPath in Enumerable.Reverse(mgr.AllSegmentPaths))
        {
            if (remaining.Count == 0)
                break;
            if (!File.Exists(segPath))
                continue;
            var (entries, _, _) = await ReadAndParseJsonlAsync(segPath, sessionId);
            foreach (var e in entries)
            {
                if (e.EntryId is null || !remaining.Contains(e.EntryId))
                    continue;
                if (e.Message is not null)
                    found[e.EntryId] = e.Message;
                remaining.Remove(e.EntryId);
            }
        }

        // 3. 按 entryIds 原始顺序返回（消息可能已从活跃路径移除，但原文在旧分片）
        var result = new List<ChatMessage>(targetIds.Count);
        foreach (var id in targetIds)
        {
            if (found.TryGetValue(id, out var msg))
                result.Add(msg);
        }
        return result;
    }

    /// <summary>列出会话所有已持久化的压缩块（blockId + 摘要 + 消息数），供 decompress 枚举。</summary>
    public async Task<
        List<(string BlockId, string Summary, int MessageCount)>
    > ListCompactBlocksAsync(string sessionId, CancellationToken ct = default)
    {
        var result = new List<(string BlockId, string Summary, int MessageCount)>();
        var mgr = GetSegmentManager(sessionId);
        if (mgr is null)
            return result;

        foreach (var segPath in Enumerable.Reverse(mgr.AllSegmentPaths))
        {
            if (!File.Exists(segPath))
                continue;
            var (entries, _, _) = await ReadAndParseJsonlAsync(segPath, sessionId);
            foreach (var e in entries)
            {
                if (e.Type != JsonlEntryType.Compact || e.CompactBlockId is null)
                    continue;
                if (result.Any(b => b.BlockId == e.CompactBlockId))
                    continue; // 幂等：同一 blockId 只列一次
                result.Add((e.CompactBlockId, e.CompactSummary ?? "", e.CompactMessageIds.Count));
            }
        }
        return result;
    }

    /// <summary>
    /// 从解析条目中提取最新的 compact 摘要文本。
    /// </summary>
    private static string? ExtractLatestCompactSummary(
        List<ParsedEntry> entries,
        string? leafTargetId
    )
    {
        if (entries.Count == 0)
            return null;

        var activeIds = ResolveActiveIds(entries, leafTargetId);

        ParsedEntry? latest = null;
        foreach (var e in entries)
        {
            if (e.Type != JsonlEntryType.Compact)
                continue;
            if (activeIds is not null && (e.EntryId is null || !activeIds.Contains(e.EntryId)))
                continue;
            if (latest is null || e.Order > latest.Order)
                latest = e;
        }
        return latest?.CompactSummary;
    }

    /// <summary>
    /// Returns the Content field of the most recent VariantSetCreated event.
    /// Used to restore variant dialogs after session reload.
    /// 新格式：读取最新分片；旧格式兼容：读取 JSONL 文件。
    /// </summary>
    public async Task<string?> GetLastVariantSetContentAsync(
        string sessionId,
        CancellationToken ct = default
    )
    {
        // 新格式
        var mgr = GetSegmentManager(sessionId);
        if (mgr is not null && mgr.ActiveFileExists)
        {
            var (entries, leafTargetId, _) = await ReadAndParseJsonlAsync(
                mgr.ActivePath,
                sessionId
            );
            return ExtractLastVariantSetContent(entries, leafTargetId);
        }

        // 旧格式
        var path = ResolveSessionDir(sessionId);
        if (path is null || !File.Exists(path) || Path.GetExtension(path) != ".jsonl")
            return null;

        var (entriesOld, leafTargetIdOld, _) = await ReadAndParseJsonlAsync(path, sessionId);
        return ExtractLastVariantSetContent(entriesOld, leafTargetIdOld);
    }

    private static string? ExtractLastVariantSetContent(
        List<ParsedEntry> entries,
        string? leafTargetId
    )
    {
        if (entries.Count == 0)
            return null;

        var activeIds = ResolveActiveIds(entries, leafTargetId);

        ParsedEntry? latest = null;
        foreach (var e in entries)
        {
            if (e.Type != JsonlEntryType.Event)
                continue;
            if (
                !string.IsNullOrEmpty(e.EventContent) && e.EventContent.Contains("\"variantSetId\"")
            )
            {
                if (activeIds is not null && (e.EntryId is null || !activeIds.Contains(e.EntryId)))
                    continue;
                if (latest is null || e.Order > latest.Order)
                    latest = e;
            }
        }
        return latest?.EventContent;
    }

    /// <summary>
    /// 轻量级会话元数据查询：仅返回 max order 和最后一条非 compact 条目的 entryId。
    /// 新格式：从 MANIFEST 直接读取（最快）；旧格式兼容：解析 JSONL 文件。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <returns>(maxOrder + 1, lastNonCompactEntryId)。无数据时返回 (0, null)。</returns>
    public async Task<(int NextOrder, string? LastEntryId)> GetSessionMetadataAsync(
        string sessionId,
        CancellationToken ct = default
    )
    {
        // 优先从 MANIFEST 读取（新格式）
        var mgr = GetSegmentManager(sessionId);
        if (mgr is not null)
        {
            var nextOrder = mgr.NextOrder;
            // 如果 ActiveLastEntryId 有缓存，直接返回
            if (mgr.ActiveLastEntryId is not null)
                return (nextOrder, mgr.ActiveLastEntryId);

            // 兜底：从活跃分片解析
            if (mgr.ActiveFileExists)
            {
                var (entries, leafTargetId, _) = await ReadAndParseJsonlAsync(
                    mgr.ActivePath,
                    sessionId
                );
                return ExtractMetadataFromEntries(entries, leafTargetId, nextOrder);
            }

            return (nextOrder, null);
        }

        // 旧格式兼容
        var path = ResolveSessionDir(sessionId);
        if (path is null || !File.Exists(path) || Path.GetExtension(path) != ".jsonl")
            return (0, null);

        var (entriesOld, leafTargetIdOld, _) = await ReadAndParseJsonlAsync(path, sessionId);
        return ExtractMetadataFromEntries(entriesOld, leafTargetIdOld, 0);
    }

    /// <summary>
    /// 从解析条目中提取元数据 (nextOrder, lastEntryId)。
    /// </summary>
    private static (int NextOrder, string? LastEntryId) ExtractMetadataFromEntries(
        List<ParsedEntry> entries,
        string? leafTargetId,
        int defaultNextOrder
    )
    {
        if (entries.Count == 0)
            return (defaultNextOrder, null);

        var activeIds = ResolveActiveIds(entries, leafTargetId);

        var maxOrder = -1;
        string? lastEntryId = null;

        foreach (var e in entries)
        {
            if (activeIds is not null && e.EntryId is not null && !activeIds.Contains(e.EntryId))
                continue;
            if (e.Type is JsonlEntryType.Compact or JsonlEntryType.Label)
                continue;
            if (e.Order > maxOrder)
            {
                maxOrder = e.Order;
                lastEntryId = e.EntryId;
            }
        }

        return (maxOrder >= 0 ? maxOrder + 1 : defaultNextOrder, lastEntryId);
    }

    // ═══════════════════════════════════════════════════════════
    //  树结构
    // ═══════════════════════════════════════════════════════════

    public async Task<SessionTreeStructure> GetTreeStructure(
        string sessionId,
        CancellationToken ct = default
    )
    {
        var result = new SessionTreeStructure();

        // 尝试新格式：合并所有分片
        var mgr = GetSegmentManager(sessionId);
        if (mgr is not null)
        {
            return await BuildTreeFromAllSegments(mgr, sessionId);
        }

        // 旧格式兼容
        var path = ResolveSessionDir(sessionId);
        if (path is null || !File.Exists(path) || Path.GetExtension(path) != ".jsonl")
            return result;

        var (entries, leafTargetId, labelMap) = await ReadAndParseJsonlAsync(path, sessionId);
        return BuildTreeFromEntries(entries, leafTargetId, labelMap, sessionId);
    }

    /// <summary>
    /// 从所有分片合并构建树结构。
    /// </summary>
    private async Task<SessionTreeStructure> BuildTreeFromAllSegments(
        SessionSegmentManager mgr,
        string sessionId
    )
    {
        var result = new SessionTreeStructure();

        var allEntries = new List<ParsedEntry>();
        string? leafTargetId = null;
        var labelMap = new Dictionary<string, List<string>>();

        foreach (var segPath in mgr.AllSegmentPaths)
        {
            if (!File.Exists(segPath))
                continue;
            var (entries, segLeaf, segLabels) = await ReadAndParseJsonlAsync(segPath, sessionId);

            // Window 只做页边界，不进树/UI（compact 同理）；两者都由各自机制单独呈现或完全隐藏。
            allEntries.AddRange(
                entries.Where(e => e.Type is not (JsonlEntryType.Compact or JsonlEntryType.Window))
            );

            // ★ 只取最新分片的 segLeaf（从新到旧遍历，第一个非 null 即最新）；
            // 旧分片的 segLeaf 是历史尾部，若覆盖会把 leaf 指向过期消息，
            // 导致 activeIds 回溯链错位（B5 修复）。
            if (leafTargetId is null && segLeaf is not null)
                leafTargetId = segLeaf;

            foreach (var (targetId, labels) in segLabels)
            {
                if (!labelMap.TryGetValue(targetId, out var existing))
                    labelMap[targetId] = [.. labels];
                else
                    existing.AddRange(labels);
            }
        }

        return allEntries.Count == 0
            ? result
            : BuildTreeFromEntries(allEntries, leafTargetId, labelMap, sessionId);
    }

    /// <summary>
    /// 从解析条目构建树结构。
    /// </summary>
    private static SessionTreeStructure BuildTreeFromEntries(
        List<ParsedEntry> entries,
        string? leafTargetId,
        Dictionary<string, List<string>> labelMap,
        string sessionId
    )
    {
        var result = new SessionTreeStructure();

        var activeIds = ResolveActiveIds(entries, leafTargetId);
        result.ActiveLeafId = leafTargetId;
        if (activeIds is not null)
            result.ActiveEntryIds = activeIds;

        // 构建父→子映射
        var childMap = new Dictionary<string, List<ParsedEntry>>();
        var rootEntries = new List<ParsedEntry>();
        foreach (var e in entries)
        {
            if (e.Type == JsonlEntryType.Compact)
                continue;

            if (e.ParentEntryId is null || e.ParentEntryId == sessionId)
            {
                rootEntries.Add(e);
            }
            else
            {
                if (!childMap.TryGetValue(e.ParentEntryId, out var children))
                {
                    children = [];
                    childMap[e.ParentEntryId] = children;
                }
                children.Add(e);
            }
        }

        // 递归构建节点
        var nodes = new List<SessionTreeNode>();
        var visited = new HashSet<string>();
        foreach (var root in rootEntries.OrderBy(e => e.Order))
        {
            BuildTreeNodes(root, childMap, labelMap, nodes, visited);
        }

        // 处理孤立节点
        foreach (var e in entries)
        {
            if (e.Type == JsonlEntryType.Compact)
                continue;
            if (e.EntryId is not null && !visited.Contains(e.EntryId))
            {
                nodes.Add(EntryToNode(e, childMap, labelMap));
            }
        }

        result.Nodes = nodes.OrderBy(n => n.Order).ToList();
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    //  输入历史
    // ═══════════════════════════════════════════════════════════

    public async Task SaveInputHistory(
        string sessionId,
        string text,
        CancellationToken ct = default
    )
    {
        var path = Path.Combine(_inputHistoryDir, $"{sessionId}.jsonl");
        var entry = new JsonlInputHistoryEntry
        {
            Text = text,
            Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        var json = JsonSerializer.Serialize(entry, JsonOpts);
        await AtomicFile.AppendLineAsync(path, json);
    }

    public async Task<List<string>> GetInputHistory(
        string sessionId,
        int limit = 100,
        CancellationToken ct = default
    )
    {
        var path = Path.Combine(_inputHistoryDir, $"{sessionId}.jsonl");
        if (!File.Exists(path))
            return [];

        var lines = await File.ReadAllLinesAsync(path);
        var result = new List<string>();
        // 从末尾读取，避免全量 Reverse
        for (var i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            try
            {
                var entry = JsonSerializer.Deserialize<JsonlInputHistoryEntry>(lines[i], JsonOpts);
                if (entry is not null)
                    result.Add(entry.Text);
            }
            catch
            {
                // 跳过无法解析的行
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    //  解析缓存
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 读取并解析 JSONL 文件，如果文件 mtime 未变则复用缓存结果。
    /// </summary>
    private async Task<(
        List<ParsedEntry> Entries,
        string? LeafTargetId,
        Dictionary<string, List<string>> LabelMap
    )> ReadAndParseJsonlAsync(string path, string sessionId)
    {
        var currentMtime = File.GetLastWriteTimeUtc(path);

        if (_parseCache.TryGetValue(path, out var cached) && cached.Mtime == currentMtime)
        {
            // Update last accessed time for approximate LRU
            _parseCache[path] = cached with
            {
                LastAccessed = DateTime.UtcNow,
            };
            return (cached.Entries, cached.LeafTargetId, cached.LabelMap);
        }

        var allContent = await File.ReadAllTextAsync(path);
        AtomicFile.TrackContent(path, allContent);

        var lines = allContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var (entries, leafTargetId, labelMap) = ParseAllEntries(lines, sessionId);

        // Approximate LRU eviction: remove oldest 1/4 entries by LastAccessed time.
        // Better than clearing the entire cache when session count > 32.
        if (_parseCache.Count >= MaxParseCacheSize)
        {
            var toRemove = _parseCache
                .OrderBy(kvp => kvp.Value.LastAccessed)
                .Take(MaxParseCacheSize / 4)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in toRemove)
                _parseCache.TryRemove(key, out _);
        }
        var now = DateTime.UtcNow;
        _parseCache[path] = new ParseCacheEntry(currentMtime, entries, leafTargetId, labelMap, now);
        return (entries, leafTargetId, labelMap);
    }

    /// <summary>
    /// 使指定 JSONL 文件的解析缓存失效。写入新条目后调用。
    /// </summary>
    private void InvalidateParseCache(string path)
    {
        _parseCache.TryRemove(path, out _);
    }

    // ═══════════════════════════════════════════════════════════
    //  公共解析：ParseAllEntries
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// JSONL 条目解析中间结构，用于路径回溯和树结构构建。
    /// </summary>
    private sealed record ParsedEntry
    {
        public string? EntryId { get; init; }
        public string? ParentEntryId { get; init; }
        public JsonlEntryType Type { get; init; }
        public int Order { get; init; }
        public ChatMessage? Message { get; init; }

        /// <summary>compact 条目：保留的起始 entryId。</summary>
        public string? CompactFirstKeptEntryId { get; init; }

        /// <summary>compact 条目：AI 生成的结构化摘要。</summary>
        public string? CompactSummary { get; init; }

        /// <summary>compact 条目：压缩前的消息数量。</summary>
        public int MessagesBefore { get; init; }

        /// <summary>compact 条目：触发原因。</summary>
        public string? CompactReason { get; init; }

        /// <summary>compact 条目：是否自动重试。</summary>
        public bool CompactWillRetry { get; init; }

        /// <summary>compact 条目：压缩后的估算 token 数。</summary>
        public int EstimatedTokensAfter { get; init; }

        /// <summary>compact 条目：压缩块 ID（"b1"...）。迁移前旧数据为 null。</summary>
        public string? CompactBlockId { get; init; }

        /// <summary>compact 条目：被压缩消息的 entryId 列表（decompress 恢复原文用）。</summary>
        public List<string> CompactMessageIds { get; init; } = [];

        /// <summary>window 条目：本页 ID（"w3"）。</summary>
        public string? WindowId { get; init; }

        /// <summary>window 条目：上一页 ID。</summary>
        public string? PrevWindowId { get; init; }

        /// <summary>window 条目：触发原因（model | overflow | threshold）。</summary>
        public string? WindowReason { get; init; }

        /// <summary>window 条目的交接说明（new_context(note:)。</summary>
        public string? WindowNote { get; init; }

        /// <summary>显示文本（供 TreeSelectorPanel 使用）。</summary>
        public string? DisplayText { get; init; }

        /// <summary>Event 条目的原始 Content 字段（用于 VariantSetCreated 等复杂载荷的恢复）。</summary>
        public string? EventContent { get; init; }

        /// <summary>消息角色（仅 message 类型有值）。</summary>
        public string? Role { get; init; }

        /// <summary>label 条目：标记的目标条目 id。</summary>
        public string? LabelTargetId { get; init; }

        /// <summary>label 条目：用户自定义标签文本。</summary>
        public string? LabelText { get; init; }

        /// <summary>时间戳。</summary>
        public DateTime Timestamp { get; init; }
    }

    /// <summary>
    /// 统一解析 JSONL 所有条目，返回 (entries, leafTargetId, labelMap)。
    /// 使用强类型 DTO 反序列化替代 JsonDocument.Parse + TryGetProperty 手动解析。
    /// </summary>
    private static (
        List<ParsedEntry> Entries,
        string? LeafTargetId,
        Dictionary<string, List<string>> LabelMap
    ) ParseAllEntries(string[] lines, string sessionId)
    {
        var entries = new List<ParsedEntry>();
        var labelMap = new Dictionary<string, List<string>>();
        string? leafTargetId = null;
        string? lastEntryId = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // 容错：尝试修复多个 JSON 对象拼接在同一行的情况（如 leaf 后缺少换行）
            foreach (var segment in SplitConcatenatedJson(line))
            {
                if (string.IsNullOrWhiteSpace(segment))
                    continue;

                try
                {
                    // 先探测 type，再分发到具体 DTO
                    var probe = JsonSerializer.Deserialize<JsonlTypeProbe>(segment, JsonOpts);
                    if (probe?.Type is null)
                        continue;

                    switch (probe.Type)
                    {
                        case JsonlEntryType.Leaf:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlLeafEntry>(segment, JsonOpts);
                            if (dto is not null)
                                leafTargetId = dto.TargetId;
                            break;
                        }
                        case JsonlEntryType.Window:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlWindowEntry>(
                                segment,
                                JsonOpts
                            );
                            if (dto is not null)
                            {
                                lastEntryId = dto.Id;
                                entries.Add(FromWindowDto(dto));
                            }
                            break;
                        }
                        case JsonlEntryType.Compact:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlCompactEntry>(
                                segment,
                                JsonOpts
                            );
                            if (dto is not null)
                            {
                                lastEntryId = dto.Id;
                                entries.Add(FromCompactDto(dto));
                            }
                            break;
                        }
                        case JsonlEntryType.BranchSummary:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlBranchSummaryEntry>(
                                segment,
                                JsonOpts
                            );
                            if (dto is not null)
                            {
                                lastEntryId = dto.Id;
                                entries.Add(FromBranchSummaryDto(dto));
                            }
                            break;
                        }
                        case JsonlEntryType.Event:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlEventEntry>(
                                segment,
                                JsonOpts
                            );
                            if (dto is not null)
                            {
                                lastEntryId = dto.Id;
                                entries.Add(FromEventDto(dto, sessionId));
                            }
                            break;
                        }
                        case JsonlEntryType.Message:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlMessageEntry>(
                                segment,
                                JsonOpts
                            );
                            if (dto is not null)
                            {
                                lastEntryId = dto.Id;
                                entries.Add(FromMessageDto(dto, sessionId));
                            }
                            break;
                        }
                        case JsonlEntryType.Label:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlLabelEntry>(
                                segment,
                                JsonOpts
                            );
                            if (dto is not null)
                            {
                                var entry = FromLabelDto(dto);
                                entries.Add(entry);
                                // 直接构建 labelMap，避免 GetTreeStructure 二次遍历
                                if (entry.LabelTargetId is not null && entry.LabelText is not null)
                                {
                                    if (!labelMap.TryGetValue(entry.LabelTargetId, out var list))
                                    {
                                        list = [];
                                        labelMap[entry.LabelTargetId] = list;
                                    }
                                    list.Add(entry.LabelText);
                                }
                            }
                            break;
                        }
                        // type=session / entity_change 等其他类型跳过
                    }
                }
                catch
                {
                    // 单行解析失败不影响其他行
                }
            }
        }

        // 始终以最后一条非 leaf 条目的 ID 作为 leafTargetId。
        // 活跃路径从最后消息的 parentId 链回溯，无需依赖 leaf 条目。
        // leaf 条目仅作为 TreeSelector 的分支参考，不参与活跃路径计算。
        if (lastEntryId is not null)
            leafTargetId = lastEntryId;

        return (entries, leafTargetId, labelMap);
    }

    private static ParsedEntry FromCompactDto(JsonlCompactEntry dto)
    {
        var timestamp = DateTime.TryParse(dto.Timestamp, out var cdt) ? cdt : DateTime.UtcNow;

        return new ParsedEntry
        {
            EntryId = dto.Id,
            ParentEntryId = dto.ParentId,
            Type = JsonlEntryType.Compact,
            Order = dto.Order,
            CompactFirstKeptEntryId = dto.FirstKeptEntryId,
            CompactSummary = dto.Summary,
            MessagesBefore = dto.MessagesBefore ?? 0,
            CompactReason = dto.Reason,
            CompactWillRetry = dto.WillRetry == true,
            EstimatedTokensAfter = dto.EstimatedTokensAfter ?? 0,
            CompactBlockId = dto.BlockId,
            CompactMessageIds = dto.CompressedMessageIds,
            Timestamp = timestamp,
        };
    }

    private static ParsedEntry FromWindowDto(JsonlWindowEntry dto)
    {
        var timestamp = DateTime.TryParse(dto.Timestamp, out var wdt) ? wdt : DateTime.UtcNow;

        return new ParsedEntry
        {
            EntryId = dto.Id,
            ParentEntryId = dto.ParentId,
            Type = JsonlEntryType.Window,
            Order = dto.Order,
            WindowId = dto.WindowId,
            PrevWindowId = dto.PrevWindowId,
            WindowReason = dto.Reason,
            WindowNote = dto.Note,
            Timestamp = timestamp,
        };
    }

    private static ParsedEntry FromBranchSummaryDto(JsonlBranchSummaryEntry dto)
    {
        var timestamp = DateTime.TryParse(dto.Timestamp, out var dt) ? dt : DateTime.UtcNow;

        return new ParsedEntry
        {
            EntryId = dto.Id,
            ParentEntryId = dto.ParentId,
            Type = JsonlEntryType.BranchSummary,
            Order = dto.Order,
            DisplayText = dto.Summary,
            Timestamp = timestamp,
        };
    }

    private static ParsedEntry FromEventDto(JsonlEventEntry dto, string sessionId)
    {
        var eventType = dto.EventType;
        var fromStr = dto.From ?? "?";
        var toStr = dto.To ?? "?";
        var variantIdx = dto.SelectedVariantIndex ?? 0;
        var variantLabel = dto.SelectedLabel ?? "";

        var evtContent = eventType switch
        {
            SessionEventType.Interrupt => "中断生成",
            SessionEventType.ModelChange => $"模型切换: {fromStr} → {toStr}",
            SessionEventType.VariantSelected => $"选择变体 #{variantIdx}: {variantLabel}",
            SessionEventType.VariantSetCreated => "生成变体组",
            SessionEventType.Error => $"❌ {dto.ErrorCode ?? ""} {dto.Content ?? ""}".Trim(),
            _ => eventType.ToString(),
        };

        var timestamp = DateTime.TryParse(dto.Timestamp, out var dt) ? dt : DateTime.UtcNow;

        return new ParsedEntry
        {
            EntryId = dto.Id,
            ParentEntryId = dto.ParentId,
            Type = JsonlEntryType.Event,
            Order = dto.Order,
            Message = new ChatMessage
            {
                Id = dto.Order,
                SessionId = sessionId,
                Role = MessageRole.Unknown,
                Content = evtContent,
                Order = dto.Order,
                EntryId = dto.Id,
                ParentEntryId = dto.ParentId,
                CreatedAt = timestamp,
                EventType = eventType,
            },
            DisplayText = eventType switch
            {
                SessionEventType.Interrupt => "⚡ 中断",
                SessionEventType.ModelChange => "🔄 模型切换",
                SessionEventType.VariantSelected => "✅ 变体选择",
                SessionEventType.VariantSetCreated => "📋 变体组",
                SessionEventType.Error => "❌ 错误",
                _ => $"📋 {eventType}",
            },
            Timestamp = timestamp,
            EventContent = dto.Content,
        };
    }

    private static ParsedEntry FromMessageDto(JsonlMessageEntry dto, string sessionId)
    {
        var msgRole = MessageRoleMapping.ToMessageRole(dto.Role);
        var timestamp = DateTime.TryParse(dto.Timestamp, out var dt) ? dt : DateTime.UtcNow;

        return new ParsedEntry
        {
            EntryId = dto.Id,
            ParentEntryId = dto.ParentId,
            Type = JsonlEntryType.Message,
            Order = dto.Order,
            Message = new ChatMessage
            {
                Id = dto.Order,
                SessionId = sessionId,
                Role = msgRole,
                Content = dto.Content,
                ReasoningContent = dto.ReasoningContent,
                ToolName = dto.ToolName,
                ToolCallId = dto.ToolCallId,
                ToolCallsJson = dto.ToolCalls,
                IsError = dto.IsError == true,
                Tokens = dto.ContextTokens,
                Order = dto.Order,
                EntryId = dto.Id,
                ParentEntryId = dto.ParentId,
                CreatedAt = timestamp,
            },
            DisplayText = TruncateForDisplay(dto.Role, dto.Content),
            Role = dto.Role,
            Timestamp = timestamp,
        };
    }

    private static ParsedEntry FromLabelDto(JsonlLabelEntry dto)
    {
        var timestamp = DateTime.TryParse(dto.Timestamp, out var dt) ? dt : DateTime.UtcNow;

        return new ParsedEntry
        {
            EntryId = dto.Id,
            ParentEntryId = dto.ParentId,
            Type = JsonlEntryType.Label,
            Order = dto.Order,
            LabelTargetId = dto.TargetId,
            LabelText = dto.Label,
            Timestamp = timestamp,
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  路径回溯 + compact 截断
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 从 leaf targetId 沿 parentId 回溯到根，返回活跃路径上的 entryId 集合。
    /// 无 leaf 条目时返回 null（表示所有条目都在活跃路径上）。
    /// 轻量版本：不构建 idMap，供不需要 idMap 的场景使用。
    /// </summary>
    private static HashSet<string>? ResolveActiveIds(
        List<ParsedEntry> entries,
        string? leafTargetId
    )
    {
        if (leafTargetId is null)
            return null;

        // Build a minimal id→parentId map for backtracking only
        var parentMap = new Dictionary<string, string?>(entries.Count);
        foreach (var e in entries)
        {
            if (e.EntryId is not null)
                parentMap[e.EntryId] = e.ParentEntryId;
        }

        var activeIds = new HashSet<string>();
        var currentId = leafTargetId;
        var safety = 0;
        while (currentId is not null && safety < 10000)
        {
            if (!activeIds.Add(currentId))
                break;
            if (!parentMap.TryGetValue(currentId, out var parentId))
                break;
            currentId = parentId;
            safety++;
        }

        return activeIds;
    }

    /// <summary>
    /// 从 leaf targetId 沿 parentId 回溯到根，返回 (activeIds, idMap)。
    /// idMap 供 ResolveCompactFromOrder 复用，避免重复构建索引。
    /// 无 leaf 条目时 activeIds 为 null（表示所有条目都在活跃路径上）。
    /// </summary>
    private static (
        HashSet<string>? ActiveIds,
        Dictionary<string, ParsedEntry> IdMap
    ) ResolveActivePath(List<ParsedEntry> entries, string? leafTargetId)
    {
        var idMap = new Dictionary<string, ParsedEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (e.EntryId is not null)
                idMap[e.EntryId] = e;
        }

        if (leafTargetId is null)
            return (null, idMap);

        var activeIds = new HashSet<string>();
        var currentId = leafTargetId;
        var safety = 0;
        while (currentId is not null && safety < 10000)
        {
            if (!activeIds.Add(currentId))
                break;
            if (!idMap.TryGetValue(currentId, out var entry))
                break;
            currentId = entry.ParentEntryId;
            safety++;
        }

        return (activeIds, idMap);
    }

    /// <summary>
    /// 解析 compact 截断点的 order 值。
    /// firstKeptEntryId → 查找对应 order；找不到则返回 -1（不过滤）。
    /// </summary>
    private static int ResolveCompactFromOrder(
        ParsedEntry? latestCompact,
        Dictionary<string, ParsedEntry> idMap
    )
    {
        if (
            latestCompact?.CompactFirstKeptEntryId != null
            && idMap.TryGetValue(latestCompact.CompactFirstKeptEntryId, out var keptEntry)
        )
        {
            return keptEntry.Order;
        }

        return -1;
    }

    /// <summary>
    /// 回填 tool 消息的 ToolName（从 assistant 消息的 ToolCallsJson 中查找匹配）。
    /// O(N) 实现：预构建 toolCallId → toolName 索引，避免 O(N²) 嵌套循环。
    /// </summary>
    private static void BackfillToolNames(List<ChatMessage> messages)
    {
        // 预构建索引：从所有 assistant 消息的 ToolCallsJson 中提取 toolCallId → toolName
        // 使用 ToolCallItemDto 强类型反序列化替代 JsonDocument.Parse 手动属性访问
        var toolCallIndex = new Dictionary<string, string>();
        foreach (var msg in messages)
        {
            if (msg.Role != MessageRole.Assistant || msg.ToolCallsJson is null)
                continue;
            var items = ToolCallItemDto.DeserializeList(msg.ToolCallsJson);
            if (items is null)
                continue;
            foreach (var tc in items)
            {
                if (tc.Id is not null && tc.Name is not null)
                    toolCallIndex[tc.Id] = tc.Name;
            }
        }

        // O(N) 回填
        foreach (var msg in messages)
        {
            if (msg.ToolName is null && msg is { Role: MessageRole.Tool, ToolCallId: not null })
            {
                if (toolCallIndex.TryGetValue(msg.ToolCallId, out var name))
                    msg.ToolName = name;
            }
        }
    }

    /// <summary>
    /// 按 order 去重：同一 order 取最后一条（解决旧 Bug 导致的重复）。
    /// 比 GroupBy(order).Select(g => g.Last()).OrderBy 更高效：
    /// 从后向前遍历取每个 order 的首次出现，然后反转。
    /// </summary>
    /// <remarks>
    /// 如果当前数据触发去重，说明有 order 重复（数据损坏或旧 Bug 遗留）。
    /// 在 Debug 构建中输出警告以供排查。
    /// </remarks>
    private static List<ChatMessage> DeduplicateByOrder(List<ChatMessage> messages)
    {
        if (messages.Count <= 1)
            return messages;

        var seen = new HashSet<int>();
        var result = new List<ChatMessage>(messages.Count);
        var duplicateCount = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (seen.Add(messages[i].Order))
            {
                result.Add(messages[i]);
            }
            else
            {
                duplicateCount++;
            }
        }

        if (duplicateCount > 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[FileSessionStore] DeduplicateByOrder: removed {duplicateCount} duplicate(s) "
                    + $"from session (order collision). This may indicate stale data from an old bug."
            );
        }

        result.Reverse();
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    //  树结构辅助
    // ═══════════════════════════════════════════════════════════

    private static void BuildTreeNodes(
        ParsedEntry entry,
        Dictionary<string, List<ParsedEntry>> childMap,
        Dictionary<string, List<string>> labelMap,
        List<SessionTreeNode> nodes,
        HashSet<string> visited
    )
    {
        if (entry.EntryId is null || !visited.Add(entry.EntryId))
            return;

        nodes.Add(EntryToNode(entry, childMap, labelMap));

        if (childMap.TryGetValue(entry.EntryId, out var children))
        {
            foreach (var child in children.OrderBy(c => c.Order))
            {
                BuildTreeNodes(child, childMap, labelMap, nodes, visited);
            }
        }
    }

    private static SessionTreeNode EntryToNode(
        ParsedEntry entry,
        Dictionary<string, List<ParsedEntry>> childMap,
        Dictionary<string, List<string>> labelMap
    )
    {
        // JSONL append-only guarantees children are already in order — no OrderBy needed
        var childIds = childMap.TryGetValue(entry.EntryId ?? "", out var children)
            ? children.Select(c => c.EntryId ?? "").ToList()
            : [];

        var labels = labelMap.TryGetValue(entry.EntryId ?? "", out var lbls) ? lbls : [];

        return new SessionTreeNode
        {
            EntryId = entry.EntryId ?? "",
            ParentEntryId = entry.ParentEntryId,
            ChildIds = childIds,
            Type = entry.Type,
            Role = entry.Role,
            DisplayText = entry.DisplayText ?? "",
            Order = entry.Order,
            Labels = labels,
            CreatedAt = entry.Timestamp,
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  会话分片管理
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 管理会话的分片生命周期。封装单个会话的 MANIFEST 读写、活跃分片路径解析和并发控制。
    /// </summary>
    private sealed class SessionSegmentManager(string sessionDir, SessionManifest manifest)
    {
        private SessionManifest _manifest = manifest;
        private readonly SemaphoreSlim _lock = new(1, 1);

        /// <summary>会话目录路径。</summary>
        public string SessionDir => sessionDir;

        /// <summary>当前 MANIFEST（快照，修改后需写回）。</summary>
        public SessionManifest Manifest => _manifest;

        /// <summary>当前活跃分片完整路径。</summary>
        public string ActivePath =>
            Path.Combine(sessionDir, _manifest.Segments[_manifest.ActiveSegment].File);

        /// <summary>当前活跃分片序号。</summary>
        public int ActiveSeq => _manifest.ActiveSegment;

        /// <summary>活跃文件是否存在。</summary>
        public bool ActiveFileExists => File.Exists(ActivePath);

        /// <summary>获取所有分片文件路径（按 seq 升序）。</summary>
        public List<string> AllSegmentPaths =>
            _manifest
                .Segments.OrderBy(s => s.Seq)
                .Select(s => Path.Combine(sessionDir, s.File))
                .ToList();

        /// <summary>下次 append 写入的起始 order（= 当前 activeSegment 的 endOrder + 1，或 0）。</summary>
        public int NextOrder =>
            _manifest.Segments.Count == 0
                ? 0
                : _manifest.Segments[_manifest.ActiveSegment].EndOrder + 1;

        /// <summary>当前活跃分片的最后一条 entryId（用于追加 parentId 链）。</summary>
        public string? ActiveLastEntryId { get; set; }

        /// <summary>获取指定序号的分片路径。</summary>
        public string GetSegmentPath(int seq) =>
            Path.Combine(sessionDir, _manifest.Segments[seq].File);

        /// <summary>替换整个 MANIFEST（原子操作：先更新内存再写磁盘）。</summary>
        public void ReplaceManifest(SessionManifest manifest) => _manifest = manifest;

        /// <summary>获取内部锁（用于并发写入保护）。</summary>
        public async Task AcquireLockAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
        }

        /// <summary>释放内部锁。</summary>
        public void ReleaseLock() => _lock.Release();
    }

    /// <summary>
    /// 获取或创建指定会话的 SegmentManager。
    /// 如果会话尚未初始化（首次写入），先创建目录和初始 MANIFEST。
    /// </summary>
    private SessionSegmentManager GetOrCreateSegmentManager(string sessionId, string novelId)
    {
        if (_segmentManagers.TryGetValue(sessionId, out var mgr))
            return mgr;

        // 尝试从索引获取会话路径
        var sessionPath = ResolveSessionDir(sessionId);
        if (sessionPath is not null)
        {
            // 新格式：目录中存在 MANIFEST.json
            if (Directory.Exists(sessionPath))
            {
                var manifestPath = Path.Combine(sessionPath, "MANIFEST.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = ReadManifest(manifestPath);
                    if (manifest is not null)
                    {
                        var manager = new SessionSegmentManager(sessionPath, manifest);
                        _segmentManagers[sessionId] = manager;
                        return manager;
                    }
                }
            }

            // 旧格式兼容：将旧 JSONL 文件同步迁移为新格式目录（带时间戳前缀）
            if (File.Exists(sessionPath) && Path.GetExtension(sessionPath) == ".jsonl")
            {
                var oldDirBase = Path.GetDirectoryName(sessionPath)!;
                var newDir = Path.Combine(oldDirBase, sessionId);
                if (!Directory.Exists(newDir))
                {
                    var finalDir = MigrateOldFormatSync(
                        sessionPath,
                        oldDirBase,
                        sessionId,
                        novelId
                    );
                    _sessionIndex![sessionId] = (novelId, finalDir);
                    newDir = finalDir;
                }

                // 现在读取 MANIFEST
                var manifestPath = Path.Combine(newDir, "MANIFEST.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = ReadManifest(manifestPath);
                    if (manifest is not null)
                    {
                        var manager = new SessionSegmentManager(newDir, manifest);
                        _segmentManagers[sessionId] = manager;
                        return manager;
                    }
                }
            }
        }

        // 首次写入：创建带时间戳的会话目录 + 初始 MANIFEST
        var baseDir = GetSessionDir(novelId);
        var dirName = FormatSessionDirName(sessionId);
        var newDir2 = Path.Combine(baseDir, dirName);
        Directory.CreateDirectory(newDir2);

        var now = DateTime.UtcNow;
        var initialManifest = new SessionManifest
        {
            Version = 1,
            SessionId = sessionId,
            NovelId = novelId,
            Title = "",
            CreatedAt = now,
            UpdatedAt = now,
            Segments =
            [
                new ManifestSegmentRecord
                {
                    Seq = 0,
                    File = "000000.jsonl",
                    StartOrder = 0,
                    EndOrder = -1,
                    MessageCount = 0,
                    EntryCount = 0,
                    FileSize = 0,
                    CompactEntryId = null,
                    HasCompact = false,
                    CreatedAt = DateTime.UtcNow,
                },
            ],
            ActiveSegment = 0,
            CompactCount = 0,
            TotalEntryCount = 0,
        };

        WriteManifest(newDir2, initialManifest);

        var newMgr = new SessionSegmentManager(newDir2, initialManifest);
        _segmentManagers[sessionId] = newMgr;
        return newMgr;
    }

    /// <summary>
    /// 同步迁移旧格式 JSONL 文件到新格式分片目录。
    /// 重命名 {sessionId}.jsonl → {sessionId}/000000.jsonl 并创建 MANIFEST.json。
    /// </summary>
    /// <summary>
    /// 同步迁移旧格式 JSONL 文件到新格式分片目录。
    /// 目录名格式：{timestamp}_{sessionId}/，如 2026-07-01T08-30-00-123Z_abc123def456/
    /// 返回最终使用的目录路径。
    /// </summary>
    private string MigrateOldFormatSync(
        string oldJsonlPath,
        string oldDirBase,
        string sessionId,
        string novelId
    )
    {
        // 使用旧文件的创建时间作为时间戳前缀
        var fileInfo = new FileInfo(oldJsonlPath);
        var timestamp = fileInfo.CreationTimeUtc.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        var dirName = $"{timestamp}_{sessionId}";
        var newDir = Path.Combine(oldDirBase, dirName);
        Directory.CreateDirectory(newDir);

        var targetPath = Path.Combine(newDir, "000000.jsonl");

        // 移动旧文件到新目录
        if (File.Exists(oldJsonlPath))
            File.Move(oldJsonlPath, targetPath);

        // 快速扫描提取元数据
        string? title = null;
        var createdAt = DateTime.MaxValue;
        var updatedAt = DateTime.MinValue;
        var messageCount = 0;
        var entryCount = 0;
        var maxOrder = -1;

        try
        {
            var lines = File.Exists(targetPath) ? File.ReadAllLines(targetPath) : [];
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                entryCount++;
                try
                {
                    var probe = JsonSerializer.Deserialize<JsonlTypeProbe>(line, JsonOpts);
                    if (probe?.Type == JsonlEntryType.Message)
                    {
                        messageCount++;
                        var msg = JsonSerializer.Deserialize<JsonlMessageEntry>(line, JsonOpts);
                        if (msg is not null)
                        {
                            if (msg.Order > maxOrder)
                                maxOrder = msg.Order;
                            if (title is null && msg.Role == "user")
                            {
                                var firstLine = msg.Content.Split('\n')[0].Trim();
                                title = firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
                            }
                            if (DateTime.TryParse(msg.Timestamp, out var dt))
                            {
                                if (dt < createdAt)
                                    createdAt = dt;
                                if (dt > updatedAt)
                                    updatedAt = dt;
                            }
                        }
                    }
                    // 提取时间戳（非 message 条目也记录时间范围）
                    if (probe?.Type is not null && probe.Type != JsonlEntryType.Message)
                    {
                        // 尝试解析时间戳
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("timestamp", out var tsEl))
                        {
                            var ts = tsEl.GetString();
                            if (ts is not null && DateTime.TryParse(ts, out var dt))
                            {
                                if (dt < createdAt)
                                    createdAt = dt;
                                if (dt > updatedAt)
                                    updatedAt = dt;
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        if (createdAt == DateTime.MaxValue)
            createdAt = new FileInfo(targetPath).CreationTimeUtc;
        if (updatedAt == DateTime.MinValue)
            updatedAt = new FileInfo(targetPath).LastWriteTimeUtc;

        var manifest = new SessionManifest
        {
            Version = 1,
            SessionId = sessionId,
            NovelId = novelId,
            Title = title ?? "",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Segments =
            [
                new ManifestSegmentRecord
                {
                    Seq = 0,
                    File = "000000.jsonl",
                    StartOrder = 0,
                    EndOrder = maxOrder >= 0 ? maxOrder : 0,
                    MessageCount = messageCount,
                    EntryCount = entryCount,
                    FileSize = new FileInfo(targetPath).Length,
                    CompactEntryId = null,
                    HasCompact = false,
                    CreatedAt = createdAt,
                },
            ],
            ActiveSegment = 0,
            CompactCount = 0,
            TotalEntryCount = entryCount,
        };

        WriteManifest(newDir, manifest);
        return newDir;
    }

    /// <summary>
    /// 获取指定会话的 SegmentManager，不存在时返回 null。
    /// </summary>
    private SessionSegmentManager? GetSegmentManager(string sessionId)
    {
        if (_segmentManagers.TryGetValue(sessionId, out var mgr))
            return mgr;

        // 尝试从索引恢复
        var sessionDir = ResolveSessionDir(sessionId);
        if (sessionDir is null || !Directory.Exists(sessionDir))
            return null;

        var manifestPath = Path.Combine(sessionDir, "MANIFEST.json");
        if (!File.Exists(manifestPath))
            return null;

        var manifest = ReadManifest(manifestPath);
        if (manifest is null)
            return null;

        var manager = new SessionSegmentManager(sessionDir, manifest);
        _segmentManagers[sessionId] = manager;
        return manager;
    }

    /// <summary>同步读取并反序列化 MANIFEST.json。</summary>
    private static SessionManifest? ReadManifest(string manifestPath)
    {
        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<SessionManifest>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>同步写入 MANIFEST.json。</summary>
    private static void WriteManifest(string sessionDir, SessionManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        Directory.CreateDirectory(sessionDir);
        var path = Path.Combine(sessionDir, "MANIFEST.json");
        AtomicFile.WriteAllText(path, json);
    }

    /// <summary>
    /// Rolls a full active segment before the next message batch is appended.
    /// Must be called while the session manager lock is held.
    /// </summary>
    private static void RotateActiveSegmentIfNeeded(SessionSegmentManager mgr, int nextOrder)
    {
        var activePath = mgr.ActivePath;
        if (!File.Exists(activePath) || new FileInfo(activePath).Length < MaxActiveSegmentBytes)
            return;

        var newSeq = mgr.ActiveSeq + 1;
        var newFile = $"{newSeq:D6}.jsonl";
        var newPath = Path.Combine(mgr.SessionDir, newFile);
        File.WriteAllText(newPath, string.Empty);

        var segments = new List<ManifestSegmentRecord>(mgr.Manifest.Segments)
        {
            new()
            {
                Seq = newSeq,
                File = newFile,
                StartOrder = nextOrder,
                EndOrder = nextOrder - 1,
                CreatedAt = DateTime.UtcNow,
            },
        };
        var now = DateTime.UtcNow;
        mgr.ReplaceManifest(
            new SessionManifest
            {
                Version = mgr.Manifest.Version,
                SessionId = mgr.Manifest.SessionId,
                NovelId = mgr.Manifest.NovelId,
                Title = mgr.Manifest.Title,
                CreatedAt = mgr.Manifest.CreatedAt,
                UpdatedAt = now,
                Segments = segments,
                ActiveSegment = newSeq,
                CompactCount = mgr.Manifest.CompactCount,
                WindowCount = mgr.Manifest.WindowCount,
                TotalEntryCount = mgr.Manifest.TotalEntryCount,
                TokenEstimate = mgr.Manifest.TokenEstimate,
            }
        );
        WriteManifest(mgr.SessionDir, mgr.Manifest);
    }

    /// <summary>
    /// 更新 MANIFEST 中活跃分片的 endOrder、messageCount 等增量字段。
    /// 必须在 SegmentManager 锁内调用。
    /// </summary>
    private static void UpdateManifestSegment(
        SessionSegmentManager mgr,
        int newEntryCount,
        int newMessageCount,
        string? lastEntryId
    )
    {
        var seg = mgr.Manifest.Segments[mgr.ActiveSeq];
        // ★ with 表达式：增量更新只改计数，其余字段（含 WindowId/HasWindow）原样保留——
        // 逐字段重建会在新增字段时静默丢数据。
        var segments = new List<ManifestSegmentRecord>(mgr.Manifest.Segments)
        {
            [mgr.ActiveSeq] = seg with
            {
                EndOrder = seg.StartOrder + seg.EntryCount + newEntryCount - 1,
                MessageCount = seg.MessageCount + newMessageCount,
                EntryCount = seg.EntryCount + newEntryCount,
            },
        };
        var now = DateTime.UtcNow;
        mgr.ReplaceManifest(
            new SessionManifest
            {
                Version = mgr.Manifest.Version,
                SessionId = mgr.Manifest.SessionId,
                NovelId = mgr.Manifest.NovelId,
                Title = mgr.Manifest.Title,
                CreatedAt = mgr.Manifest.CreatedAt,
                UpdatedAt = now,
                Segments = segments,
                ActiveSegment = mgr.Manifest.ActiveSegment,
                CompactCount = mgr.Manifest.CompactCount,
                WindowCount = mgr.Manifest.WindowCount,
                TotalEntryCount = mgr.Manifest.TotalEntryCount + newEntryCount,
                TokenEstimate = mgr.Manifest.TokenEstimate,
            }
        );
        mgr.ActiveLastEntryId = lastEntryId;
        WriteManifest(mgr.SessionDir, mgr.Manifest);
    }

    /// <summary>
    /// 自动迁移检查：如果旧格式 JSONL 文件存在，迁移为分片目录格式。
    /// 在 EnsureSessionIndex 中发现旧格式时调用。
    /// </summary>
    private async Task MigrateIfNeededAsync(string sessionId, string novelId, string jsonlPath)
    {
        var sessionDir = GetSessionDir(novelId);
        var newDir = Path.Combine(sessionDir, sessionId);

        if (File.Exists(jsonlPath) && !Directory.Exists(newDir))
        {
            Directory.CreateDirectory(newDir);

            // 重命名旧文件为 000000.jsonl
            var targetPath = Path.Combine(newDir, "000000.jsonl");
            File.Move(jsonlPath, targetPath);

            // 解析来提取元数据
            try
            {
                var (entries, _, _) = await ReadAndParseJsonlAsync(targetPath, sessionId);
                var title = ExtractTitleFromEntries(entries);
                var createdAt = DateTime.MaxValue;
                var updatedAt = DateTime.MinValue;
                foreach (var e in entries)
                {
                    if (e.Timestamp < createdAt)
                        createdAt = e.Timestamp;
                    if (e.Timestamp > updatedAt)
                        updatedAt = e.Timestamp;
                }
                if (createdAt == DateTime.MaxValue)
                    createdAt = new FileInfo(targetPath).CreationTimeUtc;
                if (updatedAt == DateTime.MinValue)
                    updatedAt = new FileInfo(targetPath).LastWriteTimeUtc;

                var messageCount = entries.Count(e => e.Type == JsonlEntryType.Message);
                var maxOrder = entries.Count > 0 ? entries.Max(e => e.Order) : 0;

                var manifest = new SessionManifest
                {
                    Version = 1,
                    SessionId = sessionId,
                    NovelId = novelId,
                    Title = title ?? "",
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt,
                    Segments =
                    [
                        new ManifestSegmentRecord
                        {
                            Seq = 0,
                            File = "000000.jsonl",
                            StartOrder = 0,
                            EndOrder = maxOrder,
                            MessageCount = messageCount,
                            EntryCount = entries.Count,
                            FileSize = new FileInfo(targetPath).Length,
                            CompactEntryId = null,
                            HasCompact = false,
                            CreatedAt = createdAt,
                        },
                    ],
                    ActiveSegment = 0,
                    CompactCount = 0,
                    TotalEntryCount = entries.Count,
                };

                WriteManifest(newDir, manifest);
            }
            catch
            {
                // 迁移失败时尝试回滚
                if (File.Exists(targetPath) && !Directory.Exists(jsonlPath))
                {
                    try
                    {
                        File.Move(targetPath, jsonlPath);
                    }
                    catch
                    { /* 忽略回滚失败 */
                    }
                }
                throw;
            }

            // 更新索引为目录路径
            _sessionIndex![sessionId] = (novelId, newDir);
        }
    }

    /// <summary>从解析条目中提取会话标题（第一条 user message）。</summary>
    private static string? ExtractTitleFromEntries(List<ParsedEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Type != JsonlEntryType.Message || entry.Role != "user")
                continue;
            var text = entry.Message?.Content ?? "";
            var firstLine = text.Split('\n')[0].Trim();
            return firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════
    //  会话元数据构建
    // ═══════════════════════════════════════════════════════════

    private async Task<AgentSession?> BuildSessionFromJsonlAsync(
        string sessionId,
        string novelId,
        string jsonlPath
    )
    {
        List<ParsedEntry> entries;
        try
        {
            var (parsedEntries, _, _) = await ReadAndParseJsonlAsync(jsonlPath, sessionId);
            entries = parsedEntries;
        }
        catch
        {
            return null;
        }

        if (entries.Count == 0)
            return null;

        string? title = null;
        var createdAt = DateTime.MaxValue;
        var updatedAt = DateTime.MinValue;

        foreach (var entry in entries)
        {
            if (entry.Timestamp < createdAt)
                createdAt = entry.Timestamp;
            if (entry.Timestamp > updatedAt)
                updatedAt = entry.Timestamp;

            if (title is not null)
                continue;

            if (entry.Type != JsonlEntryType.Message || entry.Role != "user")
                continue;

            // 取 content 的第一行作为 title
            var text = entry.Message?.Content ?? "";
            var firstLine = text.Split('\n')[0].Trim();
            title = firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
        }

        if (createdAt == DateTime.MaxValue)
        {
            var fi = new FileInfo(jsonlPath);
            createdAt = fi.CreationTimeUtc;
        }
        if (updatedAt == DateTime.MinValue)
        {
            var fi = new FileInfo(jsonlPath);
            updatedAt = fi.LastWriteTimeUtc;
        }

        return new AgentSession
        {
            Id = sessionId,
            NovelId = novelId,
            Title = title ?? "未命名会话",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };
    }

    /// <summary>
    /// 从 MANIFEST.json 快速构建 AgentSession 元数据。
    /// 相比 BuildSessionFromJsonlAsync 省去全量 JSONL 解析，性能提升显著。
    /// </summary>
    private static AgentSession? BuildSessionFromManifest(
        string sessionId,
        string novelId,
        string sessionDir
    )
    {
        var manifestPath = Path.Combine(sessionDir, "MANIFEST.json");
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<SessionManifest>(json, JsonOpts);
            if (manifest is null)
                return null;

            // 如果 title 为空，尝试从活跃分片的第一条 user message 提取
            var title = manifest.Title;
            if (string.IsNullOrEmpty(title))
            {
                title = ReadTitleFromActiveSegment(sessionDir, manifest);
            }

            return new AgentSession
            {
                Id = manifest.SessionId,
                NovelId = novelId,
                Title = title ?? "未命名会话",
                CreatedAt = manifest.CreatedAt,
                UpdatedAt = manifest.UpdatedAt,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从活跃分片的第一条 user message 提取标题（当 MANIFEST 中 title 为空时降级方案）。
    /// </summary>
    private static string? ReadTitleFromActiveSegment(string sessionDir, SessionManifest manifest)
    {
        if (manifest.Segments.Count == 0)
            return null;
        var activeFile = manifest.Segments[manifest.ActiveSegment].File;
        var activePath = Path.Combine(sessionDir, activeFile);
        if (!File.Exists(activePath))
            return null;

        try
        {
            var lines = File.ReadAllLines(activePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var probe = JsonSerializer.Deserialize<JsonlTypeProbe>(line, JsonOpts);
                    if (probe?.Type != JsonlEntryType.Message)
                        continue;
                    var msg = JsonSerializer.Deserialize<JsonlMessageEntry>(line, JsonOpts);
                    if (msg is null || msg.Role != "user")
                        continue;
                    var firstLine = msg.Content.Split('\n')[0].Trim();
                    return firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ═══════════════════════════════════════════════════════════
    //  路径/目录辅助
    // ═══════════════════════════════════════════════════════════

    private string GetSessionDir(string novelId)
    {
        var name = GetNovelTitle(novelId) ?? novelId;
        var encoded = SanitizeFileName(name);
        return Path.Combine(_sessionsDir, $"--novels-{encoded}--");
    }

    private string? GetNovelTitle(string novelId)
    {
        EnsureNovelTitleCache();
        return _novelIdToTitle!.GetValueOrDefault(novelId);
    }

    private string GetNovelIdFromSessionDir(string sessionDir)
    {
        var dirName = Path.GetFileName(sessionDir);

        if (
            dirName.StartsWith("--novels-", StringComparison.Ordinal)
            && dirName.EndsWith("--", StringComparison.Ordinal)
        )
        {
            var encodedTitle = dirName[9..^2];

            EnsureNovelTitleCache();
            if (_sanitizedTitleToNovelId!.TryGetValue(encodedTitle, out var novelId))
                return novelId;
        }

        return dirName;
    }

    // ═══════════════════════════════════════════════════════════
    //  工具方法
    // ═══════════════════════════════════════════════════════════

    private static string TruncateForDisplay(string role, string content)
    {
        var prefix = role switch
        {
            "user" => "👤 ",
            "assistant" => "🤖 ",
            "tool" => "🔧 ",
            "system" => "⚙️ ",
            _ => "❓ ",
        };
        return $"{prefix}{TruncateText(content, 55)}";
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var firstLine = text.Split('\n')[0].Trim();
        return firstLine.Length <= maxLength ? firstLine : firstLine[..maxLength] + "…";
    }

    private static string? GetString(Dictionary<string, object?> dict, string key) =>
        StoreHelper.GetString(dict, key);

    private static string SanitizeFileName(string name) => StoreHelper.SanitizeFileName(name);

    /// <summary>
    /// 生成 uuidv7 风格的 12 字符 hex 条目 ID（48 bit，降低碰撞风险）。
    /// 前 32 bit = 时间戳低 32 位，后 16 bit = 随机数。
    /// </summary>
    private static string GenerateEntryId()
    {
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var random = (ushort)Random.Shared.Next();
        return $"{timestamp:x8}{random:x4}";
    }

    /// <summary>
    /// 修复多个 JSON 对象拼接在同一行的问题（如 leaf 条目后缺少换行符）。
    /// 仅在 } 后紧跟 { 时拆分，避免破坏 JSON 字符串内部的 }。
    /// </summary>
    private static IEnumerable<string> SplitConcatenatedJson(string line)
    {
        while (true)
        {
            var idx = line.IndexOf("}{", StringComparison.Ordinal);
            if (idx < 0)
            {
                yield return line;
                yield break;
            }

            yield return line[..(idx + 1)];
            line = line[(idx + 1)..];
        }
    }
}
