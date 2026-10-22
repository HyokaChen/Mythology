using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Shared;
using Microsoft.Data.Sqlite;

namespace Core.Data.Migrations;

/// <summary>
/// 迁移 005：把仓库内**所有非 SQL 的 JSON/YAML 键**统一为 kebab-case（一次到位）。
///
/// 覆盖面（一次迁移涵盖，无分步历史——本项目从未迁移过真实数据库，不需要中间版本）：
/// <list type="bullet">
///   <item><b>同步文档</b>：novelsRoot 下 `novel.yaml` / 实体 `*.yaml` / 章节 `*.md` 的 front-matter</item>
///   <item><b>本地状态文件</b>：`config.yaml`、`workspaces.json`、`cloud-session.json`、
///         `novel-sync/*.json`、`todos/*.json`、`plugins/**/plugin.json`</item>
///   <item><b>库内 JSON 列</b>：`retrieval_documents` / `retrieval_chunks` / `agent_memories` / `agent_journal`</item>
/// </list>
///
/// 注意：SQL **列名**保持 snake（SQL 标识符不允许 `-`）；本迁移只改「文件里的键」与「列内 JSON 的键」。
///
/// 幂等：只重命名映射表内的键；值、未知键、数据 map 的键（如 `documents` 的路径键）原样保留；
/// 无改动不写盘；单个文件/单行解析失败跳过并记日志（不阻断启动）。
/// </summary>
public sealed class LocalKebabKeys : IDataMigration
{
    public int Version => 5;
    public string Name => "005_LocalKebabKeys";
    public string Description =>
        "全仓非 SQL 键统一 kebab-case（同步文档 / 本地状态文件 / 插件清单 / 检索与记忆库 JSON 列）";

    public TextWriter? Log { get; set; }

    // ── 同步文档（YAML front-matter / 实体 yaml）────────────────────────────
    private static readonly Dictionary<string, string> s_docKeys = new(StringComparer.Ordinal)
    {
        ["novel_id"] = "novel-id",
        ["volume_id"] = "volume-id",
        ["faction_id"] = "faction-id",
        ["planned_reveal_chapter_id"] = "planned-reveal-chapter-id",
        ["resolved_at_chapter_id"] = "resolved-at-chapter-id",
        ["headquarters_id"] = "headquarters-id",
        ["faction_a_id"] = "faction-a-id",
        ["faction_b_id"] = "faction-b-id",
        ["entity_type"] = "entity-type",
        ["entity_id"] = "entity-id",
        ["chapter_id"] = "chapter-id",
        ["scene_ids"] = "scene-ids",
        ["evidence_span"] = "evidence-span",
        ["target_entity_type"] = "target-entity-type",
        ["target_id"] = "target-id",
        ["from_summary"] = "from-summary",
        ["to_summary"] = "to-summary",
        ["evidence_quote"] = "evidence-quote",
        ["parent_id"] = "parent-id",
        ["adjacent_locations"] = "adjacent-locations",
        ["distance_li"] = "distance-li",
        ["owner_id"] = "owner-id",
        ["ownership_periods"] = "ownership-periods",
        ["since_chapter_id"] = "since-chapter-id",
        ["until_chapter_id"] = "until-chapter-id",
        ["character_a_id"] = "character-a-id",
        ["character_b_id"] = "character-b-id",
        ["location_id"] = "location-id",
        ["time_description"] = "time-description",
        ["participant_ids"] = "participant-ids",
        ["plotline_ids"] = "plotline-ids",
        ["item_ids"] = "item-ids",
        ["emotional_arc"] = "emotional-arc",
        ["scene_type"] = "scene-type",
        ["character_ids"] = "character-ids",
        ["writing_style"] = "writing-style",
        ["completion_status"] = "completion-status",
        ["created_at"] = "created-at",
        ["updated_at"] = "updated-at",
        ["death_chapter_id"] = "death-chapter-id",
        ["first_appearance_chapter_id"] = "first-appearance-chapter-id",
        ["source_id"] = "source-id",
        ["target_entity_id"] = "target-entity-id",
    };

    /// <summary>config.yaml 的行首键。</summary>
    private static readonly Dictionary<string, string> s_yamlKeys = new(StringComparer.Ordinal)
    {
        ["api_key"] = "api-key",
        ["api_keys"] = "api-keys",
        ["auth_header"] = "auth-header",
        ["auth_value"] = "auth-value",
        ["base_url"] = "base-url",
        ["bearer_token"] = "bearer-token",
        ["context_management"] = "context-management",
        ["context_window_size"] = "context-window-size",
        ["default_model"] = "default-model",
        ["rate_limit"] = "rate-limit",
        ["reminder_percent"] = "reminder-percent",
        ["thinking_level"] = "thinking-level",
        ["vector_retrieval"] = "vector-retrieval",
    };

    /// <summary>JSON 文件的键（本地状态 + 插件清单 + 检索/记忆元数据 + journal），仅列已知键。</summary>
    private static readonly Dictionary<string, string> s_jsonKeys = new(StringComparer.Ordinal)
    {
        // 本地状态文件
        ["added_at"] = "added-at",
        ["last_used_at"] = "last-used-at",
        ["active_root"] = "active-root",
        ["apiKey"] = "api-key",
        ["publicId"] = "public-id",
        ["workspaceId"] = "workspace-id",
        ["savedAt"] = "saved-at",
        ["expiresAt"] = "expires-at",
        ["displayName"] = "display-name",
        // 插件清单（历史生成器写出 snake）
        ["min_core_version"] = "min-core-version",
        // 检索元数据 / 记忆 journal
        ["chapter_character_ids"] = "chapter-character-ids",
        ["chapter_plotline_ids"] = "chapter-plotline-ids",
        ["chapter_summary"] = "chapter-summary",
        ["chapter_title"] = "chapter-title",
        ["entity_status"] = "entity-status",
        ["faction_name"] = "faction-name",
        ["item_id"] = "item-id",
        ["location_name"] = "location-name",
        ["tool_name"] = "tool-name",
        ["window_id"] = "window-id",
        ["memory_kind"] = "memory-kind",
        ["dropped_content"] = "dropped-content",
        ["session_id"] = "session-id",
        ["chapter_order"] = "chapter-order",
        ["expires_at"] = "expires-at",
        // 会话 JSONL / MANIFEST.json（历史 camelCase）
        ["parentId"] = "parent-id",
        ["toolName"] = "tool-name",
        ["toolCallId"] = "tool-call-id",
        ["toolCalls"] = "tool-calls",
        ["isError"] = "is-error",
        ["reasoningContent"] = "reasoning-content",
        ["contextTokens"] = "context-tokens",
        ["errorCode"] = "error-code",
        ["eventType"] = "event-type",
        ["sessionId"] = "session-id",
        ["novelId"] = "novel-id",
        ["entityType"] = "entity-type",
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
        ["activeSegment"] = "active-segment",
        ["compactCount"] = "compact-count",
        ["windowCount"] = "window-count",
        ["totalEntryCount"] = "total-entry-count",
        ["tokenEstimate"] = "token-estimate",
        ["firstKeptEntryId"] = "first-kept-entry-id",
        ["messagesBefore"] = "messages-before",
        ["willRetry"] = "will-retry",
        ["estimatedTokensAfter"] = "estimated-tokens-after",
        ["compressedMessageIds"] = "compressed-message-ids",
        ["blockId"] = "block-id",
        ["prevWindowId"] = "prev-window-id",
        ["firstWindowId"] = "first-window-id",
        ["selectedVariantIndex"] = "selected-variant-index",
        ["selectedLabel"] = "selected-label",
        ["targetId"] = "target-id",
        ["fromId"] = "from-id",
    };

    /// <summary>会话文件中的「枚举值/代码值」映射（仅这些键的字符串值参与改名）。</summary>
    private static readonly Dictionary<string, string> s_sessionValueKeys = new(
        StringComparer.Ordinal
    )
    {
        ["type"] = "type",
        ["eventType"] = "event-type",
        ["event-type"] = "event-type",
        ["errorCode"] = "error-code",
        ["error-code"] = "error-code",
        ["entityType"] = "entity-type",
        ["entity-type"] = "entity-type",
    };

    /// <summary>同步文档中承载「实体类型值」的字段（YAML 值改写用）。</summary>
    private static readonly string[] s_docEntityTypeFields = ["entity-type", "target-entity-type"];

    /// <summary>仅在 <c>relationships.yaml</c> 中改写的字段（其余文件的 type 属不同枚举，值均为单词，无需改写）。</summary>
    private static readonly string[] s_relationshipTypeFields = ["type"];

    /// <summary>关系类型值：历史无分隔符 / snake → kebab（与 <c>RelationshipTypeExtensions</c> 对齐；love→lover 修工具描述与解析不一致）。</summary>
    private static readonly Dictionary<string, string> s_relationshipValues = new(
        StringComparer.Ordinal
    )
    {
        ["masterservant"] = "master-servant",
        ["master_servant"] = "master-servant",
        ["fellowdisciple"] = "fellow-disciple",
        ["fellow_disciple"] = "fellow-disciple",
        ["love"] = "lover",
    };

    private static readonly Dictionary<string, string> s_sessionValues = new(StringComparer.Ordinal)
    {
        // 实体类型值（历史两种写法：YamlHelper.EnumToYaml 的 "timelineevent" 与 ToKebabCaseString 的 "timeline_event"）
        ["timelineevent"] = "timeline-event",
        ["timeline_event"] = "timeline-event",
        ["worldrule"] = "world-rule",
        ["world_rule"] = "world-rule",
        ["statechange"] = "state-change",
        ["state_change"] = "state-change",
        ["factionrelationship"] = "faction-relationship",
        ["faction_relationship"] = "faction-relationship", // 历史 snake 值（勿改为 kebab，否则迁移失效）
        ["entity_change"] = "entity-change",
        ["branch_summary"] = "branch-summary",
        ["model_change"] = "model-change",
        ["variant_selected"] = "variant-selected",
        ["variant_set_created"] = "variant-set-created",
        ["context_overflow"] = "context-overflow",
    };

    /// <summary>JSON 改写用并集：同步文档键 ∪ JSON 键（同一套命名规则，避免两处漏配）。</summary>
    private static readonly Dictionary<string, string> s_allJsonKeys = s_docKeys
        .Concat(s_jsonKeys)
        .GroupBy(p => p.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

    /// <summary>（表名, 主键列, JSON 列）。</summary>
    private static readonly (string Table, string KeyColumn, string JsonColumn)[] s_sqliteTargets =
    [
        ("retrieval_documents", "document_id", "metadata_json"),
        ("retrieval_chunks", "chunk_id", "metadata_json"),
        ("agent_memories", "id", "metadata_json"),
        ("agent_journal", "seq", "payload_json"),
    ];

    public Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var changed = 0;
        changed += MigrateNovelDocuments(mythologyDir, novelsRoot, ct);
        changed += MigrateYamlFile(Path.Combine(mythologyDir, "config.yaml"), ct);
        changed += MigrateJsonFile(Path.Combine(mythologyDir, "workspaces.json"), ct);
        changed += MigrateJsonFile(Path.Combine(mythologyDir, "cloud-session.json"), ct);
        changed += MigrateJsonDir(Path.Combine(mythologyDir, "novel-sync"), "*.json", ct);
        changed += MigrateJsonDir(Path.Combine(mythologyDir, "todos"), "*.json", ct);
        changed += MigratePluginManifests(Path.Combine(mythologyDir, "plugins"), ct);
        changed += MigrateSessionFiles(mythologyDir, ct);
        changed += MigrateSqliteJsonColumns(Path.Combine(mythologyDir, "vectors.db"), ct);
        Log?.WriteLine($"  键统一完成：共改写 {changed} 个文件/行批次。");
        return Task.CompletedTask;
    }

    // ── 同步文档 ────────────────────────────────────────────────────────────

    private int MigrateNovelDocuments(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var roots = MigrationWalkers.CollectRoots(mythologyDir, novelsRoot, Log);
        var scanned = 0;
        var rewritten = 0;
        var keys = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in MigrationWalkers.EnumerateDocs(root))
            {
                ct.ThrowIfCancellationRequested();
                scanned++;
                string text;
                try
                {
                    text = AtomicFile.ReadAllText(file);
                }
                catch (Exception ex)
                {
                    Log?.WriteLine($"  [warn] 读取失败 {file}: {ex.Message}");
                    continue;
                }

                var isMarkdown = file.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                var isRelationshipFile = Path.GetFileName(file)
                    .Equals("relationships.yaml", StringComparison.OrdinalIgnoreCase);
                var (next, count) = RewriteDocument(text, isMarkdown, isRelationshipFile);
                if (count == 0)
                    continue;
                AtomicFile.WriteAllText(file, next);
                rewritten++;
                keys += count;
            }
        }
        Log?.WriteLine($"  同步文档：扫描 {scanned} 个文件，改写 {rewritten} 个（键 {keys} 处）。");
        return rewritten;
    }

    /// <summary>逐行改写（Markdown 仅改首个 front-matter 块）；返回新文本与改写键数。</summary>
    internal static (string Text, int Count) RewriteDocument(
        string text,
        bool frontMatterOnly,
        bool isRelationshipFile = false
    )
    {
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var limit = lines.Length;
        var start = 0;
        if (frontMatterOnly)
        {
            if (lines.Length == 0 || lines[0].TrimEnd() != "---")
                return (text, 0);
            start = 1;
            limit = lines.Length;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() == "---")
                {
                    limit = i;
                    break;
                }
            }
        }

        var count = 0;
        for (var i = start; i < limit; i++)
        {
            var (next, n) = RewriteYamlKey(lines[i], s_docKeys);
            var (valued, m) = RewriteYamlValue(next, s_docEntityTypeFields, s_sessionValues);
            var (typed, k) = isRelationshipFile
                ? RewriteYamlValue(valued, s_relationshipTypeFields, s_relationshipValues)
                : (valued, 0);
            if (n > 0 || m > 0 || k > 0)
            {
                lines[i] = typed;
                count += n + m + k;
            }
        }

        return count == 0 ? (text, 0) : (string.Join('\n', lines), count);
    }

    /// <summary>YAML 行首键（缩进 + 可选列表标记）改名。</summary>
    internal static (string Line, int Count) RewriteYamlKey(
        string line,
        Dictionary<string, string> map
    )
    {
        var indent = line.Length - line.TrimStart().Length;
        var body = line[indent..];
        var prefix = "";
        if (body.StartsWith("- ", StringComparison.Ordinal))
        {
            prefix = "- ";
            body = body[2..];
        }
        foreach (var (old, kebab) in map)
        {
            if (body.StartsWith(old + ":", StringComparison.Ordinal))
                return (line[..indent] + prefix + kebab + body[old.Length..], 1);
        }
        return (line, 0);
    }

    /// <summary>改写指定字段的取值（仅限已知映射值；键须已是 kebab）。</summary>
    internal static (string Line, int Count) RewriteYamlValue(
        string line,
        string[] fields,
        Dictionary<string, string> map
    )
    {
        var indent = line.Length - line.TrimStart().Length;
        var body = line[indent..];
        var prefix = "";
        if (body.StartsWith("- ", StringComparison.Ordinal))
        {
            prefix = "- ";
            body = body[2..];
        }
        foreach (var field in fields)
        {
            if (!body.StartsWith(field + ":", StringComparison.Ordinal))
                continue;
            var raw = body[(field.Length + 1)..];
            var trimmed = raw.Trim();
            if (map.TryGetValue(trimmed, out var kebab))
                return (line[..indent] + prefix + field + ":" + raw.Replace(trimmed, kebab), 1);
            return (line, 0);
        }
        return (line, 0);
    }

    // ── 本地状态文件 / 插件清单 ─────────────────────────────────────────────

    private int MigrateYamlFile(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        try
        {
            var text = AtomicFile.ReadAllText(path);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var changed = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var (next, n) = RewriteYamlKey(lines[i], s_yamlKeys);
                if (n > 0)
                {
                    lines[i] = next;
                    changed += n;
                }
            }
            if (changed == 0)
                return 0;
            AtomicFile.WriteAllText(path, string.Join('\n', lines));
            Log?.WriteLine($"  {Path.GetFileName(path)}：改写 {changed} 个键。");
            return 1;
        }
        catch (Exception ex)
        {
            Log?.WriteLine($"  [warn] {Path.GetFileName(path)} 处理失败，跳过：{ex.Message}");
            return 0;
        }
    }

    private int MigrateJsonDir(string dir, string pattern, CancellationToken ct)
    {
        if (!Directory.Exists(dir))
            return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, pattern))
        {
            ct.ThrowIfCancellationRequested();
            count += MigrateJsonFile(file, ct);
        }
        return count;
    }

    private int MigratePluginManifests(string pluginsDir, CancellationToken ct)
    {
        if (!Directory.Exists(pluginsDir))
            return 0;
        var count = 0;
        foreach (
            var file in Directory.EnumerateFiles(
                pluginsDir,
                "plugin.json",
                SearchOption.AllDirectories
            )
        )
        {
            ct.ThrowIfCancellationRequested();
            count += MigrateJsonFile(file, ct);
        }
        return count;
    }

    private int MigrateJsonFile(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        try
        {
            var text = AtomicFile.ReadAllText(path);
            var rewritten = RewriteJson(text);
            if (rewritten is null)
                return 0;
            AtomicFile.WriteAllText(path, rewritten);
            Log?.WriteLine($"  {Path.GetFileName(path)}：键已改名。");
            return 1;
        }
        catch (Exception ex)
        {
            Log?.WriteLine($"  [warn] {Path.GetFileName(path)} 处理失败，跳过：{ex.Message}");
            return 0;
        }
    }

    /// <summary>重命名 JSON 中任意深度的已知键；无改动返回 null。</summary>
    internal static string? RewriteJson(string json, bool writeIndented = true)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
            return null;
        return Rename(node)
            ? node.ToJsonString(new JsonSerializerOptions { WriteIndented = writeIndented })
            : null;
    }

    private static bool Rename(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (s_allJsonKeys.TryGetValue(key, out var kebab))
                    {
                        var value = obj[key];
                        obj.Remove(key);
                        obj[kebab] = value;
                        changed = true;
                    }
                }
                // 会话枚举/代码值（仅限已知键，避免误改业务数据）
                foreach (var (key, _) in s_sessionValueKeys)
                {
                    if (
                        obj.TryGetPropertyValue(key, out var valueNode)
                        && valueNode is JsonValue value
                        && value.TryGetValue<string>(out var text)
                        && s_sessionValues.TryGetValue(text, out var kebabValue)
                    )
                    {
                        obj[key] = kebabValue;
                        changed = true;
                    }
                }
                foreach (var child in obj.Select(p => p.Value).ToList())
                {
                    if (child is not null && Rename(child))
                        changed = true;
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null && Rename(item))
                        changed = true;
                }
                break;
        }
        return changed;
    }

    // ── 会话文件（JSONL 行级 + MANIFEST.json）───────────────────────────────

    private int MigrateSessionFiles(string mythologyDir, CancellationToken ct)
    {
        var sessionsDir = Path.Combine(mythologyDir, "sessions");
        var inputHistoryDir = Path.Combine(mythologyDir, "input-history");
        var changed = 0;
        foreach (var dir in new[] { sessionsDir, inputHistoryDir })
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (
                var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories)
            )
            {
                ct.ThrowIfCancellationRequested();
                changed += MigrateJsonLines(file);
            }
        }
        if (Directory.Exists(sessionsDir))
        {
            foreach (
                var file in Directory.EnumerateFiles(
                    sessionsDir,
                    "MANIFEST.json",
                    SearchOption.AllDirectories
                )
            )
            {
                ct.ThrowIfCancellationRequested();
                changed += MigrateJsonFile(file, ct);
            }
        }
        Log?.WriteLine($"  会话文件：改写 {changed} 个文件。");
        return changed;
    }

    /// <summary>逐行改写 JSONL（保留 BOM 与行尾换行风格）；无改动不写盘。</summary>
    private int MigrateJsonLines(string path)
    {
        try
        {
            var text = AtomicFile.ReadAllText(path);
            var bom = text.StartsWith('\ufeff') ? "\ufeff" : "";
            var body = bom.Length > 0 ? text[1..] : text;
            var lines = body.Replace("\r\n", "\n").Split('\n');
            var changedLines = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                try
                {
                    var rewritten = RewriteJson(lines[i], writeIndented: false);
                    if (rewritten is null)
                        continue;
                    lines[i] = rewritten.Replace("\r\n", "\n").TrimEnd('\n');
                    changedLines++;
                }
                catch (System.Text.Json.JsonException)
                {
                    // 单行损坏：跳过该行（不阻断其余会话）
                }
            }
            if (changedLines == 0)
                return 0;
            AtomicFile.WriteAllText(path, bom + string.Join('\n', lines));
            Log?.WriteLine($"  {Path.GetFileName(path)}：改写 {changedLines} 行。");
            return 1;
        }
        catch (Exception ex)
        {
            Log?.WriteLine($"  [warn] {Path.GetFileName(path)} 处理失败，跳过：{ex.Message}");
            return 0;
        }
    }

    // ── 库内 JSON 列 ────────────────────────────────────────────────────────

    private int MigrateSqliteJsonColumns(string dbPath, CancellationToken ct)
    {
        if (!File.Exists(dbPath))
        {
            Log?.WriteLine("  vectors.db 不存在，跳过库内 JSON 列。");
            return 0;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        var total = 0;
        foreach (var (table, keyColumn, jsonColumn) in s_sqliteTargets)
        {
            ct.ThrowIfCancellationRequested();
            if (!TableHasColumn(connection, table, jsonColumn))
                continue;

            var rows = new List<(string Id, string Json)>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText =
                    $"SELECT {keyColumn}, {jsonColumn} FROM {table} WHERE {jsonColumn} IS NOT NULL AND {jsonColumn} LIKE '%_%'";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "";
                    var json = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (id.Length > 0 && json.Length > 0)
                        rows.Add((id, json));
                }
            }

            var changed = 0;
            foreach (var (id, json) in rows)
            {
                ct.ThrowIfCancellationRequested();
                string? rewritten;
                try
                {
                    rewritten = RewriteJson(json, writeIndented: false);
                }
                catch (JsonException ex)
                {
                    Log?.WriteLine($"  [warn] {table}:{id} JSON 解析失败，跳过（{ex.Message}）");
                    continue;
                }
                if (rewritten is null)
                    continue;

                using var update = connection.CreateCommand();
                update.CommandText =
                    $"UPDATE {table} SET {jsonColumn} = $json WHERE {keyColumn} = $id";
                update.Parameters.AddWithValue("$json", rewritten);
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
                changed++;
            }

            total += changed;
            Log?.WriteLine($"  {table}：扫描 {rows.Count} 行，改写 {changed} 行。");
        }
        return total;
    }

    private static bool TableHasColumn(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", table);
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0)
            return false;

        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table})";
        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
