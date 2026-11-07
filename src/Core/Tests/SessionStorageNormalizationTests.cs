using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Data;
using Core.Data.Migrations;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 011 会话存储规范化：
/// 旧目录按小说标题编码、MANIFEST/JSONL 仍是 camelCase（001 用遗留读取器写回），
/// 当前读取器按 kebab-case + 小说 id 找会话 → 旧会话读不出来。
/// 迁移后必须：目录收敛到 id 键、键/事件枚举 kebab 化、FileSessionStore 能列出并恢复旧会话。
/// </summary>
public sealed class SessionStorageNormalizationTests : IAsyncDisposable
{
    private const string NovelId = "abc123def456";
    private const string NovelTitle = "书";
    private readonly string _root;
    private readonly string _mythologyDir;
    private readonly string _novelsRoot;
    private readonly string _sessionsDir;

    public SessionStorageNormalizationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"session-011-{Guid.NewGuid():N}");
        _mythologyDir = Path.Combine(_root, ".mythology");
        _novelsRoot = Path.Combine(_root, "novels");
        _sessionsDir = Path.Combine(_mythologyDir, "sessions");
        Directory.CreateDirectory(Path.Combine(_novelsRoot, NovelTitle));
        Directory.CreateDirectory(_sessionsDir);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响断言
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LegacyTitleDir_IsRekeyedToNovelId_AndSessionsBecomeReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            Path.Combine(_novelsRoot, NovelTitle, "novel.yaml"),
            $"id: {NovelId}\ntitle: {NovelTitle}\n",
            ct
        );

        var sessionDir = Path.Combine(
            _sessionsDir,
            $"--novels-{NovelTitle}--",
            "2026-01-01T00-00-00-000Z_sess1"
        );
        Directory.CreateDirectory(sessionDir);
        await WriteLegacySessionAsync(sessionDir, ct);

        // 顶层遗留：flat 会话文件 + 已按 id 命名的 audit
        await File.WriteAllTextAsync(
            Path.Combine(_sessionsDir, $"--novels-{NovelTitle}--", "flatSess.jsonl"),
            """{"type":"message","id":"f1","parentId":"f0","timestamp":"2026-01-01T00:00:00Z","role":"user","content":"旧会话","order":0}"""
                + "\n",
            ct
        );
        await File.WriteAllTextAsync(
            Path.Combine(_sessionsDir, $"--novels-{NovelTitle}--", $"audit_{NovelId}.jsonl"),
            """{"novelId":"abc123def456"}""" + "\n",
            ct
        );

        var migration = new SessionStorageNormalization { Log = new StringWriter() };
        await migration.UpAsync(_mythologyDir, _novelsRoot, ct);

        var idDir = Path.Combine(_sessionsDir, $"--novels-{NovelId}--");
        var movedSession = Path.Combine(idDir, "2026-01-01T00-00-00-000Z_sess1");
        Assert.True(Directory.Exists(movedSession), "会话目录应搬到小说 id 键目录");
        Assert.False(
            Directory.Exists(Path.Combine(_sessionsDir, $"--novels-{NovelTitle}--")),
            "搬空的标题目录应删除"
        );
        Assert.True(File.Exists(Path.Combine(idDir, "flatSess.jsonl")));
        Assert.True(File.Exists(Path.Combine(idDir, $"audit_{NovelId}.jsonl")));

        // MANIFEST 键与 novel-id 值
        var manifestText = await File.ReadAllTextAsync(
            Path.Combine(movedSession, "MANIFEST.json"),
            ct
        );
        var manifest = JsonSerializer.Deserialize<SessionManifest>(
            manifestText,
            JsonlSerializer.Options
        )!;
        Assert.Equal("sess1", manifest.SessionId);
        Assert.Equal(NovelId, manifest.NovelId);
        Assert.DoesNotContain("\"sessionId\"", manifestText);
        Assert.DoesNotContain("\"startOrder\"", manifestText);

        // JSONL 键与事件枚举
        var lines = (
            await File.ReadAllTextAsync(Path.Combine(movedSession, "000000.jsonl"), ct)
        ).Split('\n');
        var toolLine = JsonNode.Parse(lines[1])!.AsObject();
        Assert.True(toolLine.ContainsKey("parent-id"));
        Assert.True(toolLine.ContainsKey("tool-name"));
        Assert.True(toolLine.ContainsKey("tool-call-id"));
        Assert.True(toolLine.ContainsKey("context-tokens"));
        Assert.False(toolLine.ContainsKey("toolName"));
        var eventLine = JsonNode.Parse(lines[2])!.AsObject();
        Assert.Equal("model-change", (string?)eventLine["event-type"]);
        Assert.True(eventLine.ContainsKey("error-code"));

        // FileSessionStore：列表与按 id 恢复都必须能找到旧会话
        var store = new FileSessionStore(_novelsRoot, _mythologyDir);
        var sessions = await store.GetSessions(NovelId, ct);
        Assert.Equal(2, sessions.Count);
        var listed = Assert.Single(sessions, s => s.Id == "sess1");
        Assert.Equal(NovelId, listed.NovelId);
        Assert.NotNull(await store.GetSession("sess1", ct));
        Assert.NotNull(await store.GetSession("flatSess", ct));

        // 幂等：重复执行不产生重复目录 / 不改回 camelCase
        await migration.UpAsync(_mythologyDir, _novelsRoot, ct);
        Assert.Single(Directory.GetDirectories(idDir));
        Assert.Contains(
            "\"session-id\"",
            await File.ReadAllTextAsync(Path.Combine(movedSession, "MANIFEST.json"), ct)
        );
    }

    [Fact]
    public async Task UnresolvableNovelDir_IsKept_ButKeysAreNormalized()
    {
        var ct = TestContext.Current.CancellationToken;
        var novelDir = Path.Combine(_sessionsDir, "--novels-无名--");
        var sessionDir = Path.Combine(novelDir, "2026-01-01T00-00-00-000Z_s2");
        Directory.CreateDirectory(sessionDir);

        var manifest = new SessionManifest
        {
            SessionId = "s2",
            NovelId = "无名",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await File.WriteAllTextAsync(
            Path.Combine(sessionDir, "MANIFEST.json"),
            JsonSerializer.Serialize(manifest, JsonlSerializer.LegacyReadOptions),
            ct
        );
        await File.WriteAllTextAsync(
            Path.Combine(sessionDir, "000000.jsonl"),
            """{"type":"message","id":"m1","parentId":"m0","timestamp":"2026-01-01T00:00:00Z","role":"user","content":"hi","order":0}"""
                + "\n",
            ct
        );

        var migration = new SessionStorageNormalization { Log = new StringWriter() };
        await migration.UpAsync(_mythologyDir, _novelsRoot, ct);

        Assert.True(Directory.Exists(sessionDir), "归属不明的目录必须保留");
        var manifestText = await File.ReadAllTextAsync(
            Path.Combine(sessionDir, "MANIFEST.json"),
            ct
        );
        Assert.Contains("\"session-id\"", manifestText);
        Assert.DoesNotContain("\"sessionId\"", manifestText);
        var line = JsonNode
            .Parse(
                (await File.ReadAllTextAsync(Path.Combine(sessionDir, "000000.jsonl"), ct)).Trim()
            )!
            .AsObject();
        Assert.True(line.ContainsKey("parent-id"));
    }

    private static async Task WriteLegacySessionAsync(string sessionDir, CancellationToken ct)
    {
        var manifest = new SessionManifest
        {
            SessionId = "sess1",
            NovelId = NovelTitle,
            Title = "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Segments =
            [
                new ManifestSegmentRecord
                {
                    Seq = 0,
                    File = "000000.jsonl",
                    StartOrder = 0,
                    EndOrder = 2,
                    MessageCount = 2,
                    EntryCount = 3,
                    CreatedAt = DateTime.UtcNow,
                },
            ],
        };
        await File.WriteAllTextAsync(
            Path.Combine(sessionDir, "MANIFEST.json"),
            JsonSerializer.Serialize(manifest, JsonlSerializer.LegacyReadOptions),
            ct
        );

        var lines = new[]
        {
            """{"type":"message","id":"e1","parentId":"e0","timestamp":"2026-01-01T00:00:00Z","role":"user","content":"你好","order":0}""",
            """{"type":"message","id":"e2","parentId":"e1","timestamp":"2026-01-01T00:00:00Z","role":"tool","content":"ok","toolName":"probe","toolCallId":"c1","toolCalls":"[]","isError":false,"order":1,"contextTokens":123}""",
            """{"type":"event","id":"e3","parentId":"e2","timestamp":"2026-01-01T00:00:00Z","eventType":"model_change","order":2,"from":"a","to":"b","errorCode":"x"}""",
        };
        await File.WriteAllTextAsync(
            Path.Combine(sessionDir, "000000.jsonl"),
            string.Join("\n", lines) + "\n",
            ct
        );
    }
}
