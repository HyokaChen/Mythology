using System.Text.Json;
using Core.Data;
using Core.Data.Migrations;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 迁移 005（全仓非 SQL 键 → kebab）的行为锚定：同步文档 / 本地状态文件 / JSON 列的键改名，
/// 值、未知键与数据 map 键不变，幂等。
/// </summary>
public sealed class LocalKebabKeysTests
{
    // ── 同步文档（novelsRoot）──────────────────────────────────────────────

    [Fact]
    public async Task UpAsync_RewritesNovelDocuments_AndIsIdempotent()
    {
        using var dir = new TempDirHandle();
        var novels = Path.Combine(dir.Path, "novels");
        var novel = Path.Combine(novels, "我的小说");
        Directory.CreateDirectory(Path.Combine(novel, "chapters"));
        Directory.CreateDirectory(Path.Combine(novel, "characters"));

        File.WriteAllText(
            Path.Combine(novel, "novel.yaml"),
            "id: n1\ntitle: 测试\nwriting_style: 简洁\ncompletion_status: ongoing\ncreated_at: 2026-01-01T00:00:00Z\ncustom_unknown_key: keep-me\n"
        );
        // Markdown：只改 front-matter，正文里的同形文本不动
        File.WriteAllText(
            Path.Combine(novel, "chapters", "ch1.md"),
            "---\nid: ch1\norder: 1\nvolume_id: vol1\nplotline_ids: pl1,pl2\ncharacter_ids: c1\nstatus: draft\n---\n正文：他写道 volume_id: 不应被改。\n"
        );
        File.WriteAllText(
            Path.Combine(novel, "characters", "c1.yaml"),
            "id: c1\nnovel_id: n1\nname: 林云\nfaction_id: f1\nfirst_appearance_chapter_id: ch_1\n"
        );

        var migration = new LocalKebabKeys();
        await migration.UpAsync(dir.Path, novels, CancellationToken.None);

        var novelYaml = File.ReadAllText(Path.Combine(novel, "novel.yaml"));
        Assert.Contains("writing-style: 简洁", novelYaml);
        Assert.Contains("completion-status: ongoing", novelYaml);
        Assert.Contains("custom_unknown_key: keep-me", novelYaml); // 未知键原样保留

        var chapter = File.ReadAllText(Path.Combine(novel, "chapters", "ch1.md"));
        Assert.Contains("volume-id: vol1", chapter);
        Assert.Contains("plotline-ids: pl1,pl2", chapter);
        Assert.Contains("正文：他写道 volume_id: 不应被改。", chapter); // 正文不受影响

        var character = File.ReadAllText(Path.Combine(novel, "characters", "c1.yaml"));
        Assert.Contains("novel-id: n1", character);
        Assert.Contains("first-appearance-chapter-id: ch_1", character);

        var before = File.ReadAllText(Path.Combine(novel, "novel.yaml"));
        await migration.UpAsync(dir.Path, novels, CancellationToken.None);
        Assert.Equal(before, File.ReadAllText(Path.Combine(novel, "novel.yaml"))); // 幂等
    }

    [Fact]
    public async Task UpAsync_SkipsDotAndConflictAndNodeModulesDirectories()
    {
        using var dir = new TempDirHandle();
        var novels = Path.Combine(dir.Path, "novels");
        var novel = Path.Combine(novels, "我的小说");
        Directory.CreateDirectory(Path.Combine(novel, ".mythology"));
        Directory.CreateDirectory(Path.Combine(novel, "node_modules"));
        Directory.CreateDirectory(Path.Combine(novel, "ch1.md.conflict-20260101"));

        const string snake = "id: x\nnovel_id: n1\n";
        var dot = Path.Combine(novel, ".mythology", "state.yaml");
        var dep = Path.Combine(novel, "node_modules", "dep.yaml");
        var conflict = Path.Combine(novel, "ch1.md.conflict-20260101", "old.yaml");
        File.WriteAllText(dot, snake);
        File.WriteAllText(dep, snake);
        File.WriteAllText(conflict, snake);

        await new LocalKebabKeys().UpAsync(dir.Path, novels, CancellationToken.None);

        Assert.Contains("novel_id: n1", File.ReadAllText(dot));
        Assert.Contains("novel_id: n1", File.ReadAllText(dep));
        Assert.Contains("novel_id: n1", File.ReadAllText(conflict));
    }

    [Fact]
    public async Task UpAsync_CoversRootsListedInWorkspacesJson()
    {
        using var dir = new TempDirHandle();
        var active = Path.Combine(dir.Path, "active");
        var other = Path.Combine(dir.Path, "other-root");
        Directory.CreateDirectory(Path.Combine(active, "n"));
        Directory.CreateDirectory(Path.Combine(other, "n"));
        File.WriteAllText(Path.Combine(active, "n", "novel.yaml"), "id: a\nnovel_id: n1\n");
        File.WriteAllText(Path.Combine(other, "n", "novel.yaml"), "id: b\nnovel_id: n2\n");
        // 必须用 JSON 序列化写（Windows 路径的反斜杠要转义）——手写插值会产出非法 JSON，
        // 让 walker 的 workspaces.json 解析失败、只迁移活动目录。
        File.WriteAllText(
            Path.Combine(dir.Path, "workspaces.json"),
            JsonSerializer.Serialize(
                new
                {
                    roots = new[] { new { path = other, added_at = "2026-01-01T00:00:00Z" } },
                    active_root = active,
                }
            )
        );

        await new LocalKebabKeys().UpAsync(dir.Path, active, CancellationToken.None);

        Assert.Contains("novel-id: n1", File.ReadAllText(Path.Combine(active, "n", "novel.yaml")));
        Assert.Contains("novel-id: n2", File.ReadAllText(Path.Combine(other, "n", "novel.yaml")));
        var workspaces = File.ReadAllText(Path.Combine(dir.Path, "workspaces.json"));
        // 键改写成 kebab，值（根路径）必须逐字保留 → 解析后断言（JSON 会把反斜杠转义，不能直接查原文）
        using var doc = JsonDocument.Parse(workspaces);
        Assert.True(doc.RootElement.TryGetProperty("active-root", out var activeRootEl));
        Assert.Equal(active, activeRootEl.GetString());
        var firstRoot = doc.RootElement.GetProperty("roots").EnumerateArray().First();
        Assert.True(firstRoot.TryGetProperty("added-at", out _));
        Assert.Equal(other, firstRoot.GetProperty("path").GetString());
    }

    // ── 本地状态文件 / 插件清单 ────────────────────────────────────────────

    [Fact]
    public async Task UpAsync_RewritesLocalStateFiles()
    {
        using var dir = new TempDirHandle();
        var mythology = Path.Combine(dir.Path, ".mythology");
        var syncDir = Path.Combine(mythology, "novel-sync");
        var todoDir = Path.Combine(mythology, "todos");
        var pluginDir = Path.Combine(mythology, "plugins", "demo");
        Directory.CreateDirectory(syncDir);
        Directory.CreateDirectory(todoDir);
        Directory.CreateDirectory(pluginDir);

        File.WriteAllText(
            Path.Combine(mythology, "config.yaml"),
            "default_model: gpt-x\ncontext_window_size: 200000\ntheme: dark\n"
        );
        File.WriteAllText(
            Path.Combine(mythology, "cloud-session.json"),
            """{"apiKey":"k","workspaceId":"w","plan":"free","savedAt":"2026-01-01T00:00:00Z"}"""
        );
        File.WriteAllText(Path.Combine(syncDir, "n1.json"), """{"version":1,"documents":{}}""");
        File.WriteAllText(Path.Combine(todoDir, "s1.json"), """[{"id":"t1","text":"x"}]""");
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            """{"id":"demo","name":"Demo","min_core_version":"0.0.2","permissions":[]}"""
        );

        await new LocalKebabKeys().UpAsync(mythology, dir.Path, CancellationToken.None);

        var config = File.ReadAllText(Path.Combine(mythology, "config.yaml"));
        Assert.Contains("default-model: gpt-x", config);
        Assert.Contains("context-window-size: 200000", config);
        Assert.Contains("theme: dark", config);

        var session = File.ReadAllText(Path.Combine(mythology, "cloud-session.json"));
        Assert.Contains("\"api-key\": \"k\"", session);
        Assert.Contains("\"workspace-id\": \"w\"", session);

        var plugin = File.ReadAllText(Path.Combine(pluginDir, "plugin.json"));
        Assert.Contains("\"min-core-version\": \"0.0.2\"", plugin);
        Assert.Contains("\"permissions\": []", plugin);
    }

    // ── JSON 列（检索库 / 记忆表）──────────────────────────────────────────

    [Fact]
    public void RewriteJson_RenamesNestedKeys_PreservesValuesAndDataMapKeys()
    {
        var json =
            """{"chapter_order":3,"participant_ids":["c1"],"documents":{"/a/b.yaml":{"revision":1}},"custom_key":"keep"}""";

        var rewritten = LocalKebabKeys.RewriteJson(json);

        Assert.NotNull(rewritten);
        using var document = JsonDocument.Parse(rewritten!);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("chapter-order").GetInt32());
        Assert.Equal(1, root.GetProperty("participant-ids").GetArrayLength());
        Assert.True(root.GetProperty("documents").TryGetProperty("/a/b.yaml", out _)); // 数据 map 键保留
        Assert.Equal("keep", root.GetProperty("custom_key").GetString());
    }

    [Fact]
    public void RewriteJson_NoKnownKeys_ReturnsNull() =>
        Assert.Null(LocalKebabKeys.RewriteJson("""{"chapter-order":3,"name":"x"}"""));

    [Fact]
    public void RewriteJson_MemoryJournalPayload_RenamesMemoryKeys()
    {
        var rewritten = LocalKebabKeys.RewriteJson(
            """{"memory_kind":2,"owner_id":"o1","dropped_content":"c","importance":0.8}"""
        );

        Assert.NotNull(rewritten);
        using var document = JsonDocument.Parse(rewritten!);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("memory-kind").GetInt32());
        Assert.Equal("o1", root.GetProperty("owner-id").GetString());
        Assert.Equal("c", root.GetProperty("dropped-content").GetString());
        Assert.Equal(0.8, root.GetProperty("importance").GetDouble());
    }

    [Fact]
    public void RewriteYamlKey_HandlesIndentAndListMarker()
    {
        Assert.Equal(("novel-id: n1", 1), LocalKebabKeys.RewriteYamlKey("novel_id: n1", DocMap()));
        var (line, count) = LocalKebabKeys.RewriteYamlKey("  - owner_id: c1", DocMap());
        Assert.Equal("  - owner-id: c1", line);
        Assert.Equal(1, count);
        Assert.Equal(("name: x", 0), LocalKebabKeys.RewriteYamlKey("name: x", DocMap()));
    }

    /// <summary>借道反射之外的公开入口：用一次真实迁移的映射验证行改写（简化：直接用已知键）。</summary>
    private static Dictionary<string, string> DocMap() =>
        new(StringComparer.Ordinal) { ["novel_id"] = "novel-id", ["owner_id"] = "owner-id" };

    // ── 会话文件（JSONL + MANIFEST，含枚举值）──────────────────────────────

    [Fact]
    public async Task UpAsync_RewritesSessionFiles_IncludingEnumValues_AndPreservesBom()
    {
        using var dir = new TempDirHandle();
        var mythology = Path.Combine(dir.Path, ".mythology");
        var sessionDir = Path.Combine(
            mythology,
            "sessions",
            "--novels-X--",
            "2026-01-01T00-00-00Z_abc"
        );
        var inputHistory = Path.Combine(mythology, "input-history");
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(inputHistory);

        // 行级：camel 键 + snake 枚举值（BOM 前缀保留）
        File.WriteAllText(
            Path.Combine(sessionDir, "000000.jsonl"),
            "\ufeff"
                + """{"type":"message","id":"m1","parentId":null,"timestamp":"t","role":"user","content":"hi","toolCalls":"[]","contextTokens":12}"""
                + "\n"
                + """{"type":"event","id":"e1","eventType":"model-change","errorCode":"context-overflow","order":2}"""
                + "\n"
                + """{"type":"entity-change","timestamp":"t","novelId":"n1","entityType":"novel","entityId":"n1","action":"create","source":"ai","toolName":null,"sessionId":"abc"}"""
                + "\n"
        );
        File.WriteAllText(
            Path.Combine(sessionDir, "MANIFEST.json"),
            """{"version":1,"sessionId":"abc","novelId":"X","title":"t","createdAt":"c","updatedAt":"u","segments":[{"seq":0,"file":"000000.jsonl","startOrder":0,"endOrder":2,"messageCount":1,"entryCount":3,"fileSize":100,"hasCompact":false,"createdAt":"c"}],"activeSegment":0,"compactCount":0,"totalEntryCount":3,"tokenEstimate":9}"""
        );
        File.WriteAllText(
            Path.Combine(inputHistory, "abc.jsonl"),
            """{"text":"hi","timestamp":"t"}""" + "\n"
        );

        await new LocalKebabKeys().UpAsync(mythology, dir.Path, CancellationToken.None);

        var jsonl = File.ReadAllText(Path.Combine(sessionDir, "000000.jsonl"));
        Assert.StartsWith("\ufeff", jsonl); // BOM 保留
        Assert.Contains("\"parent-id\"", jsonl);
        Assert.Contains("\"tool-calls\"", jsonl);
        Assert.Contains("\"context-tokens\"", jsonl);
        Assert.Contains("\"event-type\":\"model-change\"", jsonl);
        Assert.Contains("\"error-code\":\"context-overflow\"", jsonl);
        Assert.Contains("\"entity-change\"", jsonl); // type 值也改名
        Assert.Contains("\"novel-id\":\"n1\"", jsonl);
        Assert.DoesNotContain("\"parentId\"", jsonl);

        var manifest = File.ReadAllText(Path.Combine(sessionDir, "MANIFEST.json"));
        Assert.Contains("\"session-id\"", manifest);
        Assert.Contains("\"start-order\"", manifest);
        Assert.Contains("\"has-compact\"", manifest);
        Assert.Contains("\"compact-count\"", manifest);

        // 幂等
        var before = File.ReadAllText(Path.Combine(sessionDir, "000000.jsonl"));
        await new LocalKebabKeys().UpAsync(mythology, dir.Path, CancellationToken.None);
        Assert.Equal(before, File.ReadAllText(Path.Combine(sessionDir, "000000.jsonl")));
    }

    [Fact]
    public async Task UpAsync_SessionFiles_ParseWithCurrentJsonlOptions()
    {
        using var dir = new TempDirHandle();
        var mythology = Path.Combine(dir.Path, ".mythology");
        var sessionDir = Path.Combine(mythology, "sessions", "--novels-X--", "s1");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(
            Path.Combine(sessionDir, "000000.jsonl"),
            """{"type":"message","id":"m1","parentId":"p0","role":"user","content":"hi","contextTokens":7}"""
        );

        await new LocalKebabKeys().UpAsync(mythology, dir.Path, CancellationToken.None);

        var line = File.ReadAllText(Path.Combine(sessionDir, "000000.jsonl")).Trim();
        var dto = System.Text.Json.JsonSerializer.Deserialize<Core.Data.JsonlMessageEntry>(
            line,
            Core.Data.JsonlSerializer.Options
        );
        Assert.NotNull(dto);
        Assert.Equal("m1", dto!.Id);
        Assert.Equal("p0", dto.ParentId);
        Assert.Equal(7, dto.ContextTokens);
    }

    [Fact]
    public async Task UpAsync_RewritesRelationshipTypeValues_OnlyInRelationshipsFile()
    {
        using var dir = new TempDirHandle();
        var mythology = Path.Combine(dir.Path, ".mythology");
        var novels = Path.Combine(dir.Path, "novels");
        var novel = Path.Combine(novels, "书");
        Directory.CreateDirectory(novel);

        File.WriteAllText(
            Path.Combine(novel, "relationships.yaml"),
            "- id: r1\n  character-a-id: c1\n  character-b-id: c2\n  type: masterservant\n"
                + "- id: r2\n  character-a-id: c1\n  character-b-id: c3\n  type: fellow_disciple\n"
                + "- id: r3\n  character-a-id: c1\n  character-b-id: c4\n  type: love\n"
                + "- id: r4\n  character-a-id: c1\n  character-b-id: c5\n  type: parent-of\n"
        );
        // 其它文件的 type 属不同枚举（值均为单词）——必须保持原样
        File.WriteAllText(
            Path.Combine(novel, "plotlines.yaml"),
            "- id: pl1\n  name: 线A\n  type: main\n"
        );
        File.WriteAllText(
            Path.Combine(novel, "factions.yaml"),
            "- id: f1\n  name: 青云宗\n  type: sect\n"
        );

        await new LocalKebabKeys().UpAsync(mythology, novels, CancellationToken.None);

        var rels = File.ReadAllText(Path.Combine(novel, "relationships.yaml"));
        Assert.Contains("type: master-servant", rels);
        Assert.Contains("type: fellow-disciple", rels);
        Assert.Contains("type: lover", rels); // 工具描述写 lover，历史存的是 love
        Assert.Contains("type: parent-of", rels); // 已是 kebab → 幂等不动
        Assert.DoesNotContain("masterservant", rels);
        Assert.DoesNotContain("fellow_disciple", rels);
        Assert.Equal(
            "- id: pl1\n  name: 线A\n  type: main\n",
            File.ReadAllText(Path.Combine(novel, "plotlines.yaml"))
        );
        Assert.Contains("type: sect", File.ReadAllText(Path.Combine(novel, "factions.yaml")));
    }

    [Fact]
    public async Task KebabFileNames_RenamesLegacyDataFiles_AndSkipsIgnoredDirs()
    {
        using var dir = new TempDirHandle();
        var mythology = Path.Combine(dir.Path, ".mythology");
        var novels = Path.Combine(dir.Path, "novels");
        var novel = Path.Combine(novels, "书");
        var nested = Path.Combine(novel, "vol1");
        var ignored = Path.Combine(novel, "node_modules");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(ignored);
        File.WriteAllText(
            Path.Combine(novel, "world_rules.yaml"),
            "- id: wr1\n  type: travel_speed\n"
        );
        File.WriteAllText(
            Path.Combine(novel, "faction_relationships.yaml"),
            "- id: fr1\n  type: ally\n"
        );
        File.WriteAllText(Path.Combine(nested, "world_rules.yaml"), "- id: wr2\n");
        File.WriteAllText(Path.Combine(ignored, "world_rules.yaml"), "- id: wr3\n");

        await new KebabFileNames().UpAsync(mythology, novels, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(novel, "world-rules.yaml"))); // 内容不变
        Assert.Equal(
            "- id: wr1\n  type: travel_speed\n",
            File.ReadAllText(Path.Combine(novel, "world-rules.yaml"))
        );
        Assert.True(File.Exists(Path.Combine(novel, "faction-relationships.yaml")));
        Assert.True(File.Exists(Path.Combine(nested, "world-rules.yaml"))); // 嵌套目录同样处理
        Assert.False(File.Exists(Path.Combine(novel, "world_rules.yaml")));
        Assert.False(File.Exists(Path.Combine(novel, "faction_relationships.yaml")));
        Assert.True(File.Exists(Path.Combine(ignored, "world_rules.yaml"))); // 忽略目录不动

        // 幂等：再跑一次不报错且不改内容
        await new KebabFileNames().UpAsync(mythology, novels, CancellationToken.None);
        Assert.Equal(
            "- id: wr1\n  type: travel_speed\n",
            File.ReadAllText(Path.Combine(novel, "world-rules.yaml"))
        );

        // 新旧并存：不覆盖已有 kebab 文件
        File.WriteAllText(Path.Combine(novel, "world_rules.yaml"), "- id: dup\n");
        await new KebabFileNames().UpAsync(mythology, novels, CancellationToken.None);
        Assert.Equal(
            "- id: wr1\n  type: travel_speed\n",
            File.ReadAllText(Path.Combine(novel, "world-rules.yaml"))
        );
    }

    [Fact]
    public async Task KebabWorldRuleTypes_RewritesTypeValues_ScopedToWorldRulesFile()
    {
        // 迁移表的值必须落在规范键集内（否则迁移产出与云端 StpChecker 期望不一致）
        Assert.All(
            KebabWorldRuleTypes.LegacyValueMap.Values,
            v => Assert.Contains(WorldRuleTypes.All, t => t.Key == v)
        );
        Assert.Equal("travel-speed", WorldRuleTypes.Normalize("travel_speed"));
        Assert.Equal("travel-speed", WorldRuleTypes.Normalize("  Travel_Speed "));
        Assert.Equal("地点距离", WorldRuleTypes.LabelFor("location_distance"));
        Assert.Equal("自造规则", WorldRuleTypes.LabelFor("自造规则")); // 开放词汇原样返回
        // 单位提示是云端契约：StpChecker 要求 travel-speed 的单位含「里/日」，写错=整类检查静默失效
        Assert.Equal("里/日", WorldRuleTypes.All.Single(t => t.Key == "travel-speed").UnitHint);
        Assert.All(WorldRuleTypes.All, t => Assert.False(string.IsNullOrWhiteSpace(t.UnitHint)));

        using var dir = new TempDirHandle();
        var mythology = Path.Combine(dir.Path, ".mythology");
        var novels = Path.Combine(dir.Path, "novels");
        var novel = Path.Combine(novels, "书");
        Directory.CreateDirectory(novel);
        File.WriteAllText(
            Path.Combine(novel, "world-rules.yaml"),
            "- id: wr1\n  type: travel_speed\n  value: 300\n  unit: 里/日\n"
                + "- id: wr2\n  type: location_distance\n  value: 200\n  unit: 里\n"
                + "- id: wr3\n  type: travel-speed\n  value: 100\n  unit: 里/日\n"
                + "- id: wr4\n  type: 自造规则\n  value: 1\n  unit: \n"
        );
        // 其它文件的 type 属不同枚举 → 不动
        File.WriteAllText(Path.Combine(novel, "plotlines.yaml"), "- id: pl1\n  type: main\n");

        await new KebabWorldRuleTypes().UpAsync(mythology, novels, CancellationToken.None);

        var rules = File.ReadAllText(Path.Combine(novel, "world-rules.yaml"));
        Assert.Contains("type: travel-speed", rules);
        Assert.Contains("type: location-distance", rules);
        Assert.DoesNotContain("travel_speed", rules);
        Assert.DoesNotContain("location_distance", rules);
        Assert.Contains("type: 自造规则", rules); // 开放词汇保留
        Assert.Equal(
            "- id: pl1\n  type: main\n",
            File.ReadAllText(Path.Combine(novel, "plotlines.yaml"))
        );

        // 幂等：再跑一次内容不变
        var before = File.ReadAllText(Path.Combine(novel, "world-rules.yaml"));
        await new KebabWorldRuleTypes().UpAsync(mythology, novels, CancellationToken.None);
        Assert.Equal(before, File.ReadAllText(Path.Combine(novel, "world-rules.yaml")));
    }

    [Fact]
    public async Task UpAsync_RewritesEntityTypeValues_InDocsAndSessions()
    {
        using var dir = new TempDirHandle();
        var mythology = Path.Combine(dir.Path, ".mythology");
        var novels = Path.Combine(dir.Path, "novels");
        var novel = Path.Combine(novels, "书");
        var sessionDir = Path.Combine(mythology, "sessions", "--novels-书--", "s1");
        Directory.CreateDirectory(Path.Combine(novel, "appearances"));
        Directory.CreateDirectory(sessionDir);

        // 文档：历史两种写法（YamlHelper 的无分隔符 + snake）都要改成 kebab
        File.WriteAllText(
            Path.Combine(novel, "appearances", "ap1.yaml"),
            "id: ap1\nentity_type: timelineevent\nentity_id: te1\nchapter_id: ch1\nmode: present\n"
        );
        File.WriteAllText(
            Path.Combine(novel, "appearances", "ap2.yaml"),
            "id: ap2\nentity_type: state_change\ntarget-entity-type: world_rule\n"
        );
        File.WriteAllText(
            Path.Combine(novel, "appearances", "ap3.yaml"),
            "id: ap3\nentity_type: faction_relationship\n"
        );
        // 会话：entityType 值
        File.WriteAllText(
            Path.Combine(sessionDir, "000000.jsonl"),
            """{"type":"entity-change","timestamp":"t","novelId":"n1","entityType":"timeline_event","entityId":"te1","action":"create","source":"ai"}"""
        );

        await new LocalKebabKeys().UpAsync(mythology, novels, CancellationToken.None);

        var ap1 = File.ReadAllText(Path.Combine(novel, "appearances", "ap1.yaml"));
        Assert.Contains("entity-type: timeline-event", ap1);
        var ap2 = File.ReadAllText(Path.Combine(novel, "appearances", "ap2.yaml"));
        Assert.Contains("entity-type: state-change", ap2);
        Assert.Contains("target-entity-type: world-rule", ap2);
        var ap3 = File.ReadAllText(Path.Combine(novel, "appearances", "ap3.yaml"));
        Assert.Contains("entity-type: faction-relationship", ap3); // 历史 snake 值必须仍被迁移命中

        var session = File.ReadAllText(Path.Combine(sessionDir, "000000.jsonl"));
        Assert.Contains("\"entity-type\":\"timeline-event\"", session);
        Assert.DoesNotContain("timeline_event", session);
    }

    [Fact]
    public async Task KebabEntityFileNames_RenamesEntityFiles_UsingYamlId_AndIsIdempotent()
    {
        using var dir = new TempDirHandle();
        var mythology = Path.Combine(dir.Path, ".mythology");
        var novels = Path.Combine(dir.Path, "novels");
        var novel = Path.Combine(novels, "书");
        var characters = Path.Combine(novel, "characters");
        var ignored = Path.Combine(novel, "node_modules");
        Directory.CreateDirectory(characters);
        Directory.CreateDirectory(ignored);

        File.WriteAllText(
            Path.Combine(characters, "林云_c1.yaml"),
            "id: c1\nnovel-id: n1\nname: 林云\n"
        );
        // 名称本身含下划线：按 YAML id 定位后缀，不能靠字符串分割
        File.WriteAllText(
            Path.Combine(characters, "李_楚_a451e2ef92ec.yaml"),
            "id: a451e2ef92ec\nnovel-id: n1\nname: 李_楚\n"
        );
        File.WriteAllText(Path.Combine(ignored, "甲_c9.yaml"), "id: c9\nnovel-id: n1\nname: 甲\n");
        // 非 {Name}_{Id} 形态：无 id 的集合文件 / id 与文件名不对应 → 不动
        File.WriteAllText(Path.Combine(novel, "relationships.yaml"), "- id: r1\n");
        File.WriteAllText(Path.Combine(characters, "ch001.yaml"), "id: other\n");

        await new KebabEntityFileNames().UpAsync(mythology, novels, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(characters, "林云-c1.yaml")));
        Assert.True(File.Exists(Path.Combine(characters, "李_楚-a451e2ef92ec.yaml"))); // 名称里的下划线保留
        Assert.False(File.Exists(Path.Combine(characters, "林云_c1.yaml")));
        Assert.False(File.Exists(Path.Combine(characters, "李_楚_a451e2ef92ec.yaml")));
        Assert.True(File.Exists(Path.Combine(ignored, "甲_c9.yaml"))); // 忽略目录不动
        Assert.True(File.Exists(Path.Combine(novel, "relationships.yaml"))); // 集合文件不受影响
        Assert.True(File.Exists(Path.Combine(characters, "ch001.yaml"))); // id 不匹配文件名 → 不动

        // 内容不改写（只改名）
        Assert.Equal(
            "id: c1\nnovel-id: n1\nname: 林云\n",
            File.ReadAllText(Path.Combine(characters, "林云-c1.yaml"))
        );

        // 幂等：再跑一次不再产生改动
        await new KebabEntityFileNames().UpAsync(mythology, novels, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(characters, "林云-c1.yaml")));
        Assert.False(File.Exists(Path.Combine(characters, "林云_c1.yaml")));

        // 新旧并存：不覆盖已有 kebab 文件
        File.WriteAllText(Path.Combine(characters, "林云_c1.yaml"), "id: c1\nname: 重复\n");
        await new KebabEntityFileNames().UpAsync(mythology, novels, CancellationToken.None);
        Assert.Equal(
            "id: c1\nnovel-id: n1\nname: 林云\n",
            File.ReadAllText(Path.Combine(characters, "林云-c1.yaml"))
        );
        Assert.True(File.Exists(Path.Combine(characters, "林云_c1.yaml")));
    }

    [Fact]
    public async Task SaveCharacter_WritesKebabFileName_AndRenameKeepsIdReachable()
    {
        using var dir = new TempDirHandle();
        var store = new FileNovelStore(
            Path.Combine(dir.Path, "novels"),
            Path.Combine(dir.Path, "myth")
        );
        var character = new Character
        {
            Id = "c0001",
            NovelId = "n1",
            Name = "林云",
        };
        await store.SaveNovel(new Novel { Id = "n1", Title = "书" });
        await store.SaveCharacter(character);

        var file = Assert.Single(
            Directory.GetFiles(Path.Combine(dir.Path, "novels", "书", "characters"))
        );
        Assert.Equal("林云-c0001.yaml", Path.GetFileName(file));

        // 改名后仍能按 id 读回（查找走 YAML id，不解析文件名）
        await store.SaveCharacter(
            new Character
            {
                Id = "c0001",
                NovelId = "n1",
                Name = "林云改",
            }
        );
        var renamed = Assert.Single(
            Directory.GetFiles(Path.Combine(dir.Path, "novels", "书", "characters"))
        );
        Assert.Equal("林云改-c0001.yaml", Path.GetFileName(renamed));
        Assert.Equal("林云改", (await store.GetCharacter("c0001"))!.Name);
    }
}

/// <summary>临时目录句柄（测试结束递归删除）。</summary>
public sealed class TempDirHandle : IDisposable
{
    public TempDirHandle()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "myth-kebab-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响测试结论
        }
    }
}
