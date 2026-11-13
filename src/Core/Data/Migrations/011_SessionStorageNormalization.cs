using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Shared;

namespace Core.Data.Migrations;

/// <summary>
/// 011：会话存储收敛到「小说 id 键 + kebab-case 键名」。
///
/// 背景（两个历史遗留，叠加后旧会话不可见/不可恢复）：
/// <list type="bullet">
///   <item>会话目录早期按**小说标题**编码（<c>sessions/--novels-{标题}--/</c>）；
///         小说 id 后来变成 12 位 hex，新会话按 id 编码 → 同一本小说出现两个目录。</item>
///   <item>会话 MANIFEST / JSONL 仍是 camelCase 键（001/002 用遗留读取器写回，
///         005 的 kebab 迁移未覆盖 sessions）；当前 FileSessionStore 用 kebab 读取器，
///         导致 sessionId / novelId / parentId / eventType 等字段读空：
///         会话列表出现 <c>id=""</c> 幽灵行，GetSession(真实 id) 返回 null。</item>
/// </list>
///
/// 处置：
/// <list type="number">
///   <item>所有会话 MANIFEST / 分片 JSONL 的键与事件枚举统一为 kebab-case；</item>
///   <item>MANIFEST 的 novel-id 若是旧标题、且能唯一映射到当前小说 id → 改写为 id；</item>
///   <item>会话目录及其遗留 flat JSONL / audit 文件搬到 <c>--novels-{小说id}--</c> 下；</item>
///   <item>无法确定归属（小说已删除/标题重复）的目录原样保留，只做键规范化。</item>
/// </list>
///
/// 幂等：键已是 kebab、目录已按 id 归属时无操作；重复执行安全。
/// 验证：迁移后二次扫描全部 MANIFEST/JSONL，仍有遗留 camelCase 键或读不出的
/// MANIFEST 时抛异常，阻止 MigrationRunner 写 success（与 001/002 同模式）。
/// </summary>
public sealed class SessionStorageNormalization : IDataMigration
{
    public int Version => 11;
    public string Name => "011_SessionStorageNormalization";
    public string Description =>
        "会话目录统一为小说 id 键；MANIFEST/JSONL 键与事件枚举统一 kebab-case";

    public TextWriter? Log { get; set; }

    /// <summary>camelCase → kebab-case 键映射（MANIFEST 与 JSONL 全量覆盖）。</summary>
    private static readonly Dictionary<string, string> s_keyRenames = new(StringComparer.Ordinal)
    {
        ["sessionId"] = "session-id",
        ["novelId"] = "novel-id",
        ["parentId"] = "parent-id",
        ["targetId"] = "target-id",
        ["fromId"] = "from-id",
        ["firstKeptEntryId"] = "first-kept-entry-id",
        ["createdAt"] = "created-at",
        ["updatedAt"] = "updated-at",
        ["startOrder"] = "start-order",
        ["endOrder"] = "end-order",
        ["messageCount"] = "message-count",
        ["entryCount"] = "entry-count",
        ["fileSize"] = "file-size",
        ["compactEntryId"] = "compact-entry-id",
        ["hasCompact"] = "has-compact",
        ["hasWindow"] = "has-window",
        ["windowId"] = "window-id",
        ["prevWindowId"] = "prev-window-id",
        ["firstWindowId"] = "first-window-id",
        ["activeSegment"] = "active-segment",
        ["compactCount"] = "compact-count",
        ["windowCount"] = "window-count",
        ["totalEntryCount"] = "total-entry-count",
        ["tokenEstimate"] = "token-estimate",
        ["toolName"] = "tool-name",
        ["toolCallId"] = "tool-call-id",
        ["toolCalls"] = "tool-calls",
        ["reasoningContent"] = "reasoning-content",
        ["isError"] = "is-error",
        ["contextTokens"] = "context-tokens",
        ["eventType"] = "event-type",
        ["errorCode"] = "error-code",
        ["selectedVariantIndex"] = "selected-variant-index",
        ["selectedLabel"] = "selected-label",
        ["messagesBefore"] = "messages-before",
        ["estimatedTokensAfter"] = "estimated-tokens-after",
        ["willRetry"] = "will-retry",
        ["compressedMessageIds"] = "compressed-message-ids",
        ["blockId"] = "block-id",
    };

    /// <summary>事件类型枚举值（遗留 snake_case → 现 kebab-case）。</summary>
    private static readonly Dictionary<string, string> s_eventRenames = new(StringComparer.Ordinal)
    {
        ["model_change"] = "model-change",
        ["variant_selected"] = "variant-selected",
        ["variant_set_created"] = "variant-set-created",
    };

    public async Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var sessionsDir = Path.Combine(mythologyDir, "sessions");
        if (!Directory.Exists(sessionsDir))
        {
            Log?.WriteLine("  sessions 目录不存在，跳过迁移。");
            return;
        }

        var novels = NovelIdIndex.Build(mythologyDir, novelsRoot, Log);
        if (novels.Count == 0)
            Log?.WriteLine("  [warn] 未发现小说清单，只规范化会话键，不改目录归属。");

        var movedSessions = 0;
        var movedFiles = 0;
        var rewrittenManifests = 0;
        var rewrittenJsonl = 0;
        var unresolved = 0;

        foreach (var novelDir in Directory.GetDirectories(sessionsDir))
        {
            ct.ThrowIfCancellationRequested();
            var dirKey = ExtractNovelKey(novelDir);
            if (dirKey is null)
                continue; // 非 --novels-*-- 布局，不属于会话存储

            var movedFromDir = 0;

            // 会话分片目录：键规范化 → 归属解析 → 搬迁
            foreach (var sessionDir in Directory.GetDirectories(novelDir))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(sessionDir).StartsWith("audit_", StringComparison.Ordinal))
                    continue;

                foreach (var file in Directory.GetFiles(sessionDir, "*.jsonl"))
                {
                    ct.ThrowIfCancellationRequested();
                    if (await NormalizeJsonlFileAsync(file, ct))
                        rewrittenJsonl++;
                }

                var manifestPath = Path.Combine(sessionDir, "MANIFEST.json");
                if (!File.Exists(manifestPath))
                {
                    // 没有 MANIFEST 的目录无法确定归属；分片仍已规范化，原样保留
                    continue;
                }

                var manifest = await ReadJsonObjectAsync(manifestPath, ct);
                if (manifest is null)
                {
                    Log?.WriteLine($"  [warn] MANIFEST 解析失败，跳过：{manifestPath}");
                    continue;
                }

                var changed = NormalizeNode(manifest);
                var rawNovelId = GetString(manifest, "novel-id");
                var targetNovelId = novels.Resolve(rawNovelId, dirKey);

                if (targetNovelId is null)
                {
                    unresolved++;
                    if (changed)
                    {
                        await WriteJsonObjectAsync(manifestPath, manifest, ct);
                        rewrittenManifests++;
                    }
                    Log?.WriteLine($"  [warn] 无法确定小说归属，保留原目录：{sessionDir}");
                    continue;
                }

                if (!string.Equals(rawNovelId, targetNovelId, StringComparison.Ordinal))
                {
                    manifest["novel-id"] = targetNovelId;
                    changed = true;
                }

                if (changed)
                {
                    await WriteJsonObjectAsync(manifestPath, manifest, ct);
                    rewrittenManifests++;
                }

                var targetNovelDir = Path.Combine(sessionsDir, NovelDirName(targetNovelId));
                if (
                    !string.Equals(
                        Path.GetFileName(novelDir),
                        Path.GetFileName(targetNovelDir),
                        StringComparison.Ordinal
                    )
                )
                {
                    Directory.CreateDirectory(targetNovelDir);
                    var destination = Path.Combine(targetNovelDir, Path.GetFileName(sessionDir));
                    if (Directory.Exists(destination))
                    {
                        Log?.WriteLine(
                            $"  [warn] 目标已存在同名会话目录，保留源目录：{sessionDir} → {destination}"
                        );
                        continue;
                    }

                    Directory.Move(sessionDir, destination);
                    movedSessions++;
                    movedFromDir++;
                    Log?.WriteLine(
                        $"  会话搬迁：{Path.GetFileName(novelDir)} → {targetNovelId}/{Path.GetFileName(sessionDir)}"
                    );
                }
            }

            // 顶层遗留文件：flat JSONL 会话与 audit 日志
            foreach (var file in Directory.GetFiles(novelDir, "*.jsonl"))
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                var isAudit = fileName.StartsWith("audit_", StringComparison.Ordinal);

                if (!isAudit && await NormalizeJsonlFileAsync(file, ct))
                    rewrittenJsonl++;

                var targetNovelId = novels.Resolve(null, dirKey);
                if (targetNovelId is null)
                    continue;

                var targetNovelDir = Path.Combine(sessionsDir, NovelDirName(targetNovelId));
                if (
                    string.Equals(
                        Path.GetFileName(novelDir),
                        Path.GetFileName(targetNovelDir),
                        StringComparison.Ordinal
                    )
                )
                    continue;

                Directory.CreateDirectory(targetNovelDir);
                var destination = Path.Combine(targetNovelDir, fileName);
                if (File.Exists(destination))
                {
                    Log?.WriteLine(
                        $"  [warn] 目标已存在同名文件，保留源文件：{file} → {destination}"
                    );
                    continue;
                }

                File.Move(file, destination);
                movedFiles++;
                movedFromDir++;
            }

            // 搬空的遗留标题目录删除；仍有未解析内容则保留
            if (movedFromDir > 0 && IsEmptyDirectory(novelDir))
            {
                try
                {
                    Directory.Delete(novelDir);
                }
                catch (Exception ex)
                {
                    Log?.WriteLine($"  [warn] 空目录删除失败 {novelDir}: {ex.Message}");
                }
            }
        }

        Log?.WriteLine(
            $"  会话存储规范化：搬迁会话 {movedSessions} 个、文件 {movedFiles} 个；"
                + $" MANIFEST {rewrittenManifests} 个、JSONL {rewrittenJsonl} 个已改写；"
                + $" 归属不明 {unresolved} 个。"
        );

        await VerifyAsync(sessionsDir, Log, ct);
    }

    // ═══════════════════════════════════════════════════════════
    //  键 / 枚举规范化
    // ═══════════════════════════════════════════════════════════

    /// <summary>递归重命名 camelCase 键，并把事件枚举值改成 kebab-case。</summary>
    private static bool NormalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var changed = false;
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    var value = obj[key];
                    if (!s_keyRenames.TryGetValue(key, out var renamed))
                        continue;

                    obj.Remove(key);
                    // 目标键已存在（半迁移残留）时保留目标值，只丢弃重复的旧键
                    if (!obj.ContainsKey(renamed))
                        obj[renamed] = value;
                    changed = true;
                }

                foreach (var pair in obj.ToList())
                    changed |= NormalizeNode(pair.Value);

                changed |= NormalizeEventType(obj);
                return changed;
            }
            case JsonArray array:
            {
                var changed = false;
                foreach (var item in array)
                    changed |= NormalizeNode(item);
                return changed;
            }
            default:
                return false;
        }
    }

    /// <summary>事件类型值 snake_case → kebab-case（<c>model_change</c> → <c>model-change</c>）。</summary>
    private static bool NormalizeEventType(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("event-type", out var node) || node is not JsonValue value)
            return false;
        if (!value.TryGetValue<string>(out var raw))
            return false;

        var mapped = s_eventRenames.GetValueOrDefault(raw)
            ?? (raw.Contains('_') ? raw.Replace('_', '-') : raw);
        if (string.Equals(mapped, raw, StringComparison.Ordinal))
            return false;

        obj["event-type"] = mapped;
        return true;
    }

    // ═══════════════════════════════════════════════════════════
    //  文件改写
    // ═══════════════════════════════════════════════════════════

    private static async Task<bool> NormalizeJsonlFileAsync(string path, CancellationToken ct)
    {
        var original = await AtomicFile.ReadAllTextAsync(path, ct);
        if (string.IsNullOrEmpty(original))
            return false;

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var rawLines = original.Split('\n');
        var nodes = new List<JsonObject?>(rawLines.Length);
        var changed = new List<bool>(rawLines.Length);
        var anyChanged = false;

        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                nodes.Add(null);
                changed.Add(false);
                continue;
            }

            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                obj = null;
            }

            nodes.Add(obj);
            var normalized = obj is not null && NormalizeNode(obj);
            changed.Add(normalized);
            anyChanged |= normalized;
        }

        if (!anyChanged)
            return false;

        var output = new StringBuilder(original.Length + 256);
        for (var i = 0; i < rawLines.Length; i++)
        {
            if (i > 0)
                output.Append(newline);

            var raw = rawLines[i].TrimEnd('\r');
            output.Append(
                changed[i] && nodes[i] is { } node
                    ? node.ToJsonString(JsonlSerializer.Options)
                    : raw
            );
        }

        await AtomicFile.WriteAllTextAsync(path, output.ToString(), ct);
        return true;
    }

    private static async Task<JsonObject?> ReadJsonObjectAsync(string path, CancellationToken ct)
    {
        try
        {
            var text = await AtomicFile.ReadAllTextAsync(path, ct);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task WriteJsonObjectAsync(string path, JsonObject obj, CancellationToken ct) =>
        AtomicFile.WriteAllTextAsync(path, obj.ToJsonString(JsonlSerializer.Options), ct);

    private static string? GetString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node)
        && node is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;

    // ═══════════════════════════════════════════════════════════
    //  迁移后验证
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 二次扫描：仍有 camelCase 键、事件枚举未转换、或 MANIFEST 读不出 session-id
    /// → 抛异常阻止成功记账（否则修复后不会重跑）。
    /// </summary>
    private static async Task VerifyAsync(string sessionsDir, TextWriter? log, CancellationToken ct)
    {
        var legacyKeys = 0;
        var legacyEvents = 0;
        var brokenManifests = 0;

        foreach (
            var manifestPath in Directory.EnumerateFiles(
                sessionsDir,
                "MANIFEST.json",
                SearchOption.AllDirectories
            )
        )
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manifest = JsonSerializer.Deserialize<SessionManifest>(
                    await AtomicFile.ReadAllTextAsync(manifestPath, ct),
                    JsonlSerializer.Options
                );
                if (manifest is null || string.IsNullOrEmpty(manifest.SessionId))
                    brokenManifests++;
            }
            catch (JsonException)
            {
                brokenManifests++;
            }
        }

        foreach (
            var file in Directory.EnumerateFiles(
                sessionsDir,
                "*.jsonl",
                SearchOption.AllDirectories
            )
        )
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).StartsWith("audit_", StringComparison.Ordinal))
                continue;

            foreach (var raw in (await AtomicFile.ReadAllTextAsync(file, ct)).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;
                JsonObject? obj;
                try
                {
                    obj = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue; // 损坏行不属于本次迁移职责
                }

                if (obj is null)
                    continue;
                if (HasLegacyKey(obj))
                    legacyKeys++;
                if (HasLegacyEvent(obj))
                    legacyEvents++;
            }
        }

        if (legacyKeys > 0 || legacyEvents > 0 || brokenManifests > 0)
        {
            throw new InvalidOperationException(
                $"会话存储迁移未完全生效：camelCase 键 {legacyKeys} 行、"
                    + $"遗留事件枚举 {legacyEvents} 行、无法读取的 MANIFEST {brokenManifests} 个。"
            );
        }

        log?.WriteLine($"  验证通过：MANIFEST 与 JSONL 均为 kebab-case，事件枚举已转换。");
    }

    private static bool HasLegacyKey(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    if (s_keyRenames.ContainsKey(pair.Key))
                        return true;
                    if (HasLegacyKey(pair.Value))
                        return true;
                }

                return false;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (HasLegacyKey(item))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static bool HasLegacyEvent(JsonObject obj)
    {
        if (obj.TryGetPropertyValue("event-type", out var node) && node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var raw))
            {
                if (s_eventRenames.ContainsKey(raw) || raw.Contains('_'))
                    return true;
            }
        }

        foreach (var pair in obj)
        {
            if (pair.Value is JsonObject nested && HasLegacyEvent(nested))
                return true;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════
    //  小说 id ↔ 标题映射
    // ═══════════════════════════════════════════════════════════

    /// <summary>从所有已登记小说目录构建 id / 标题 / 目录名 → id 映射。</summary>
    private sealed class NovelIdIndex
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _byTitle = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _byDirName = new(StringComparer.Ordinal);

        public int Count => _ids.Count;

        public static NovelIdIndex Build(string mythologyDir, string novelsRoot, TextWriter? log)
        {
            var index = new NovelIdIndex();
            foreach (var root in MigrationWalkers.CollectRoots(mythologyDir, novelsRoot, log))
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var novelDir in Directory.GetDirectories(root))
                {
                    var yamlPath = Path.Combine(novelDir, "novel.yaml");
                    if (!File.Exists(yamlPath))
                        continue;

                    try
                    {
                        var dict = YamlHelper.Deserialize(AtomicFile.ReadAllText(yamlPath));
                        var id = StoreHelper.GetString(dict, "id");
                        var title = StoreHelper.GetString(dict, "title");
                        // 老小说没有 id 字段时，FileNovelStore 用目录名当 id
                        id = string.IsNullOrWhiteSpace(id) ? Path.GetFileName(novelDir) : id;
                        index.Add(
                            id,
                            title ?? Path.GetFileName(novelDir),
                            Path.GetFileName(novelDir)
                        );
                    }
                    catch (Exception ex)
                    {
                        log?.WriteLine($"  [warn] 读取 novel.yaml 失败 {yamlPath}: {ex.Message}");
                    }
                }
            }

            return index;
        }

        private void Add(string id, string title, string dirName)
        {
            _ids.Add(id);
            AddAmbiguous(_byTitle, StoreHelper.SanitizeFileName(title), id);
            AddAmbiguous(_byDirName, StoreHelper.SanitizeFileName(dirName), id);
        }

        private static void AddAmbiguous(Dictionary<string, string?> map, string key, string id)
        {
            if (key.Length == 0)
                return;
            // 标题重名 → 置 null（无法唯一确定归属，保留原目录交人工判断）
            if (map.TryGetValue(key, out var existing) && existing is not null && existing != id)
                map[key] = null;
            else
                map[key] = id;
        }

        /// <summary>解析会话归属：MANIFEST novel-id 优先，其次会话目录键。</summary>
        public string? Resolve(string? manifestNovelId, string sessionDirKey)
        {
            if (!string.IsNullOrWhiteSpace(manifestNovelId))
            {
                if (_ids.Contains(manifestNovelId))
                    return manifestNovelId;

                var byValue = Lookup(manifestNovelId);
                if (byValue is not null)
                    return byValue;
            }

            if (_ids.Contains(sessionDirKey))
                return sessionDirKey;

            return Lookup(sessionDirKey);
        }

        private string? Lookup(string raw)
        {
            var key = StoreHelper.SanitizeFileName(raw);
            return _byTitle.GetValueOrDefault(key) ?? _byDirName.GetValueOrDefault(key);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  目录辅助
    // ═══════════════════════════════════════════════════════════

    private static string NovelDirName(string novelId) =>
        string.Format(MythologyPaths.SessionDirPattern, novelId);

    /// <summary>解析 <c>--novels-X--</c> 目录名中的 X；非该布局返回 null。</summary>
    private static string? ExtractNovelKey(string novelDir)
    {
        var name = Path.GetFileName(novelDir);
        if (
            !name.StartsWith("--novels-", StringComparison.Ordinal)
            || !name.EndsWith("--", StringComparison.Ordinal)
        )
            return null;

        var key = name[9..^2];
        return key.Length == 0 ? null : key;
    }

    private static bool IsEmptyDirectory(string path)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (IOException)
        {
            return false;
        }
    }
}
