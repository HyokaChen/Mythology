using System.Text.Json;
using Core.Data;
using Core.Shared;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>
/// FileSessionStore 会话 JSONL 集成测试。
/// 使用临时目录，每个测试创建独立的 FileSessionStore 实例，
/// 测试结束后清理临时文件并调用 AtomicFile.ForgetPath 避免跨测试冲突追踪。
/// 使用 [Collection] 禁止并行执行，避免 AtomicFile 静态锁冲突。
/// </summary>
[Collection("FileNovelStore")]
public sealed class FileNovelStoreSessionTests : IAsyncLifetime
{
    private readonly string _tempRoot;
    private readonly string _tempSessionsDir;
    private readonly string _tempInputHistoryDir;
    private FileSessionStore _store = null!;
    private const string TestNovelId = "testnovel";
    private const string TestSessionId = "sess00000001";

    /// <summary>
    /// 从 JSONL 行解析 type 字段为 <see cref="JsonlEntryType"/> 枚举，
    /// 替代裸字符串比较，保证类型安全。
    /// </summary>
    private static JsonlEntryType? ParseEntryType(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var t))
                return JsonSerializer.Deserialize<JsonlEntryType>(
                    t.GetRawText(),
                    JsonlSerializer.Options
                );
        }
        catch { }
        return null;
    }

    public FileNovelStoreSessionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mythology-test-{Guid.NewGuid():N}");
        _tempSessionsDir = Path.Combine(_tempRoot, "sessions");
        _tempInputHistoryDir = Path.Combine(_tempRoot, "input-history");
    }

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempSessionsDir);
        Directory.CreateDirectory(_tempInputHistoryDir);
        _store = CreateStore();
        return default;
    }

    public ValueTask DisposeAsync()
    {
        // 清理 AtomicFile 的静态追踪状态，避免跨测试冲突
        AtomicFile.ForgetPath(_tempRoot);
        if (Directory.Exists(_tempSessionsDir))
        {
            foreach (
                var file in Directory.GetFiles(_tempSessionsDir, "*", SearchOption.AllDirectories)
            )
                AtomicFile.ForgetPath(file);
        }

        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
        return default;
    }

    private FileSessionStore CreateStore()
    {
        var store = new FileSessionStore(
            novelsRoot: Path.Combine(_tempRoot, "novels"),
            mythologyDir: _tempRoot
        )
        {
            SkipConflictCheck = true,
        };
        return store;
    }

    /// <summary>获取测试会话的会话目录路径（动态查找，因为目录名包含时间戳前缀）。</summary>
    private string GetSessionDir()
    {
        var novelDir = Path.Combine(_tempSessionsDir, $"--novels-{TestNovelId}--");
        if (!Directory.Exists(novelDir))
            return Path.Combine(novelDir, TestSessionId); // fallback

        // 扫描子目录，查找 MANIFEST 中 sessionId 匹配的目录
        foreach (var subDir in Directory.GetDirectories(novelDir))
        {
            var manifestPath = Path.Combine(subDir, "MANIFEST.json");
            if (!File.Exists(manifestPath))
                continue;
            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<SessionManifest>(
                    json,
                    JsonlSerializer.Options
                );
                if (manifest?.SessionId == TestSessionId)
                    return subDir;
            }
            catch { }
        }

        // Fallback：返回 novelDir（测试可能还未创建会话）
        return Path.Combine(novelDir, TestSessionId);
    }

    /// <summary>获取活跃分片文件路径（新格式）。</summary>
    private string GetSegmentPath(int seq = 0) => Path.Combine(GetSessionDir(), $"{seq:D6}.jsonl");

    /// <summary>获取 MANIFEST 路径。</summary>
    private string GetManifestPath() => Path.Combine(GetSessionDir(), "MANIFEST.json");

    /// <summary>
    /// 在所有分片文件中查找指定 order 的条目 ID。
    /// 新格式分片存储中，历史消息可能在旧分片中，需要跨分片查找。
    /// </summary>
    private async Task<string?> FindEntryIdByOrderInSegmentsAsync(int order)
    {
        var sessionDir = GetSessionDir();
        if (!Directory.Exists(sessionDir))
            return null;

        var files = Directory.GetFiles(sessionDir, "*.jsonl").OrderBy(f => f).ToList();
        foreach (var file in files)
        {
            var lines = await File.ReadAllLinesAsync(file);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("order", out var o) && o.GetInt32() == order)
                        return doc.RootElement.GetProperty("id").GetString();
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>直接读取活跃分片 JSONL 文件所有行（用于验证持久化内容）。</summary>
    private async Task<string[]> ReadJsonlLinesAsync()
    {
        // 尝试新格式：活跃分片
        var sessionDir = GetSessionDir();
        if (Directory.Exists(sessionDir))
        {
            var manifestPath = GetManifestPath();
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(manifestPath);
                    var manifest = JsonSerializer.Deserialize<SessionManifest>(
                        json,
                        JsonlSerializer.Options
                    );
                    if (manifest is not null && manifest.Segments.Count > 0)
                    {
                        var activeFile = manifest.Segments[manifest.ActiveSegment].File;
                        var activePath = Path.Combine(sessionDir, activeFile);
                        if (File.Exists(activePath))
                            return await File.ReadAllLinesAsync(activePath);
                    }
                }
                catch { }
            }
            // Fallback: 扫描所有 .jsonl 文件，取最大序号
            var files = Directory.GetFiles(sessionDir, "*.jsonl").OrderBy(f => f).ToList();
            if (files.Count > 0 && File.Exists(files.Last()))
                return await File.ReadAllLinesAsync(files.Last());
        }
        return [];
    }

    /// <summary>创建简单的 user/assistant 消息对。</summary>
    private static List<LlmChatMessage> CreateSimpleConversation(
        string userText = "你好",
        string assistantText = "你好！有什么可以帮你的？"
    )
    {
        return
        [
            new LlmChatMessage(ChatMessageRoles.User, userText),
            new LlmChatMessage(ChatMessageRoles.Assistant, assistantText),
        ];
    }

    /// <summary>创建测试用 novel.yaml，使 GetNovelIdFromSessionDir 能正确反推 novelId。</summary>
    private async Task CreateTestNovelAsync(
        string novelId = TestNovelId,
        string title = TestNovelId
    )
    {
        var novelsDir = Path.Combine(_tempRoot, "novels");
        var novelDir = Path.Combine(novelsDir, title);
        Directory.CreateDirectory(novelDir);
        var yaml = $"id: {novelId}\ntitle: {title}\n";
        await File.WriteAllTextAsync(Path.Combine(novelDir, "novel.yaml"), yaml);
    }

    // ═══════════════════════════════════════════════════════════
    //  GetSession (从文件结构推导元数据)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>GetSession 应从 JSONL 文件推导会话元数据（标题取自首条 user 消息）。</summary>
    public async Task GetSession_DerivesMetadata_FromJsonl()
    {
        // 创建 novel.yaml 以支持 novelId 反向映射
        await CreateTestNovelAsync();

        // 写入消息到 JSONL
        var messages = CreateSimpleConversation("我的测试会话标题", "你好！");
        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        var result = await _store.GetSession(TestSessionId);

        Assert.NotNull(result);
        Assert.Equal(TestSessionId, result.Id);
        Assert.Equal(TestNovelId, result.NovelId);
        Assert.Equal("我的测试会话标题", result.Title);
    }

    [Fact]
    /// <summary>标题应跳过系统注入消息，取第一条作者输入。</summary>
    public async Task GetSession_SkipsInjectedMessages_ForTitle()
    {
        await CreateTestNovelAsync();
        var messages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "[本体 harness] 上次会话以来的本体变化\n- 小说已创建"),
            new(ChatMessageRoles.User, "[云端诊断] status=pass"),
            new(ChatMessageRoles.Assistant, "收到"),
            new(ChatMessageRoles.User, "帮我看看林月如的设定"),
            new(ChatMessageRoles.Assistant, "她是女主。"),
        };
        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        var session = await _store.GetSession(TestSessionId);

        Assert.NotNull(session);
        Assert.Equal("帮我看看林月如的设定", session.Title);
        Assert.DoesNotContain("[本体 harness]", session.Title);
    }

    [Fact]
    /// <summary>全部是系统注入消息时，标题回退占位符，不暴露注入内容。</summary>
    public async Task GetSession_AllInjectedMessages_FallsBackToUnnamed()
    {
        await CreateTestNovelAsync();
        var messages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "[本体 harness] 上次会话以来的本体变化"),
            new(ChatMessageRoles.User, "[风格观察] 重复句式偏多"),
            new(ChatMessageRoles.Assistant, "收到"),
        };
        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        var session = await _store.GetSession(TestSessionId);

        Assert.NotNull(session);
        Assert.Equal("未命名会话", session.Title);
    }

    [Fact]
    /// <summary>GetSession 对不存在的会话应返回 null。</summary>
    public async Task GetSession_ReturnsNull_ForNonExistentSession()
    {
        var result = await _store.GetSession("nonexistent");
        Assert.Null(result);
    }

    // ═══════════════════════════════════════════════════════════
    //  SaveSessionMessages + GetActiveMessages
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>SaveSessionMessages 应追加消息到 JSONL 并返回最后一条的 entryId。</summary>
    public async Task SaveSessionMessages_AppendsMessages_ReturnsLastEntryId()
    {
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        Assert.NotNull(lastEntryId);
        Assert.Equal(12, lastEntryId.Length); // GenerateEntryId 生成 12 字符 hex

        var lines = await ReadJsonlLinesAsync();
        // 2 条 message 行
        var messageLines = lines
            .Where(l =>
            {
                if (string.IsNullOrWhiteSpace(l))
                    return false;
                using var d = JsonDocument.Parse(l);
                return ParseEntryType(l) == JsonlEntryType.Message;
            })
            .ToList();
        Assert.Equal(2, messageLines.Count);
    }

    [Fact]
    /// <summary>SaveSessionMessages 空消息列表应返回 lastParentEntryId。</summary>
    public async Task SaveSessionMessages_EmptyList_ReturnsLastParentEntryId()
    {
        var result = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            [],
            lastParentEntryId: "abc12345"
        );
        Assert.Equal("abc12345", result);
    }

    [Fact]
    /// <summary>GetActiveMessages 应返回已保存的消息，按 order 排序。</summary>
    public async Task GetActiveMessages_ReturnsSavedMessages_InOrder()
    {
        var messages = CreateSimpleConversation("用户消息", "助手回复");
        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        // 追加 leaf 以确保路径完整
        var lines = await ReadJsonlLinesAsync();
        var lastMsgEntryId = lines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            })
            .Last();

        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastMsgEntryId! }
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        Assert.Equal(2, active.Count);
        Assert.Equal(MessageRole.User, active[0].Role);
        Assert.Equal("用户消息", active[0].Content);
        Assert.Equal(MessageRole.Assistant, active[1].Role);
        Assert.Equal("助手回复", active[1].Content);
    }

    [Fact]
    /// <summary>
    /// ContextTokens 锚点持久化往返：assistant 消息的 Tokens 应写入 JSONL 的 context-tokens 字段，
    /// 恢复读取时回到 ChatMessage.Tokens（方案 B 的锚点载体）。
    /// 旧数据缺失该字段时反序列化为 null（向后兼容）。
    /// </summary>
    public async Task ContextTokens_AnchorRoundTrip_ThroughJsonl()
    {
        var messages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "用户消息"),
            new(ChatMessageRoles.Assistant, "助手回复")
            {
                Tokens = 1234, // 模拟 ConversationLoop 写入的锚点
            },
        };

        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        // 1) JSONL 行中应存在 context-tokens: 1234
        var lines = await ReadJsonlLinesAsync();
        var assistantLine = lines.Single(l =>
        {
            using var d = JsonDocument.Parse(l);
            return d.RootElement.TryGetProperty("role", out var r) && r.GetString() == "assistant";
        });
        using (var doc = JsonDocument.Parse(assistantLine))
        {
            var el = doc.RootElement;
            Assert.True(
                el.TryGetProperty("context-tokens", out var ct),
                "JSONL 应包含 context-tokens 字段"
            );
            Assert.Equal(1234, ct.GetInt32());
        }

        // 2) user 消息行不应有 context-tokens（WhenWritingNull 不写出 null）
        var userLine = lines.Single(l =>
        {
            using var d = JsonDocument.Parse(l);
            return d.RootElement.TryGetProperty("role", out var r) && r.GetString() == "user";
        });
        using (var doc = JsonDocument.Parse(userLine))
        {
            Assert.False(
                doc.RootElement.TryGetProperty("context-tokens", out _),
                "user 消息不应写入 context-tokens"
            );
        }

        // 3) 恢复读取：assistant 消息应带回首 Tokens 锚点
        var restored = await _store.GetActiveMessages(TestSessionId);
        var assistant = restored.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(1234, assistant.Tokens);
        var user = restored.First(m => m.Role == MessageRole.User);
        Assert.Null(user.Tokens);
    }

    [Fact]
    /// <summary>旧数据兼容：无 context-tokens 字段的 JSONL 恢复后 Tokens 为 null，不抛异常。</summary>
    public async Task ContextTokens_Missing_DefaultsToNull()
    {
        var messages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "用户消息"),
            new(ChatMessageRoles.Assistant, "助手回复"), // 无 Tokens
        };

        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        var restored = await _store.GetActiveMessages(TestSessionId);
        Assert.All(restored, m => Assert.Null(m.Tokens));
    }

    [Fact]
    /// <summary>SaveSessionMessages 增量追加应正确链接 parent-id。</summary>
    public async Task SaveSessionMessages_IncrementalAppend_LinksParentId()
    {
        // 第一批消息
        var batch1 = CreateSimpleConversation("消息1", "回复1");
        var lastId1 = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch1,
            startOrder: 0
        );

        // 第二批消息，使用 lastId1 作为 lastParentEntryId
        var batch2 = CreateSimpleConversation("消息2", "回复2");
        var lastId2 = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch2,
            lastParentEntryId: lastId1,
            startOrder: 2
        );

        Assert.NotNull(lastId2);
        Assert.NotEqual(lastId1, lastId2);

        // 验证 JSONL 中第二批消息的 parent-id 指向 lastId1
        var lines = await ReadJsonlLinesAsync();
        var thirdMsgLine = lines
            .Where(l =>
            {
                if (string.IsNullOrWhiteSpace(l))
                    return false;
                using var d = JsonDocument.Parse(l);
                return ParseEntryType(l) == JsonlEntryType.Message
                    && d.RootElement.TryGetProperty("order", out var o)
                    && o.GetInt32() == 2;
            })
            .First();

        using var doc = JsonDocument.Parse(thirdMsgLine);
        Assert.Equal(lastId1, doc.RootElement.GetProperty("parent-id").GetString());
    }

    [Fact]
    /// <summary>SaveSessionMessages 应正确持久化 reasoning-content。</summary>
    public async Task SaveSessionMessages_PersistsReasoningContent()
    {
        var msg = new LlmChatMessage(ChatMessageRoles.Assistant, "最终回答")
        {
            ReasoningContent = "这是思考过程",
        };
        var messages = new List<LlmChatMessage> { msg };

        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        var lines = await ReadJsonlLinesAsync();
        var msgLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Message;
        });

        using var doc = JsonDocument.Parse(msgLine);
        Assert.Equal("这是思考过程", doc.RootElement.GetProperty("reasoning-content").GetString());
    }

    [Fact]
    /// <summary>SaveSessionMessages 应正确持久化 tool-calls JSON。</summary>
    public async Task SaveSessionMessages_PersistsToolCalls()
    {
        var assistantMsg = new LlmChatMessage(ChatMessageRoles.Assistant, "")
        {
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_123",
                    FunctionCall = new FunctionCall
                    {
                        Name = "get_weather",
                        Arguments = "{\"city\":\"北京\"}",
                    },
                },
            ],
        };

        var toolMsg = new LlmChatMessage(ChatMessageRoles.Tool, "晴天，25°C")
        {
            ToolCallId = "call_123",
            Name = "get_weather",
        };

        var messages = new List<LlmChatMessage> { assistantMsg, toolMsg };
        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        var lines = await ReadJsonlLinesAsync();
        // 验证 assistant 消息有 tool-calls
        var assistantLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Message
                && d.RootElement.TryGetProperty("role", out var r)
                && r.GetString() == "assistant";
        });

        using var doc = JsonDocument.Parse(assistantLine);
        Assert.True(doc.RootElement.TryGetProperty("tool-calls", out var tc));
        // tool-calls 被序列化为 JSON 字符串（嵌套 JSON），而非直接数组
        Assert.Equal(JsonValueKind.String, tc.ValueKind);
        var toolCallsText = tc.GetString();
        Assert.Contains("get_weather", toolCallsText);
        Assert.Contains("call_123", toolCallsText);
    }

    // ═══════════════════════════════════════════════════════════
    [Fact]
    public async Task SaveSessionMessages_ActiveSegmentAtLimit_RollsOverAndPreservesHistory()
    {
        var first = new LlmChatMessage(ChatMessageRoles.User, new string('x', 8 * 1024 * 1024));
        var firstEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            [first],
            startOrder: 0
        );

        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            [new LlmChatMessage(ChatMessageRoles.Assistant, "after rollover")],
            lastParentEntryId: firstEntryId,
            startOrder: 1
        );

        var manifestJson = await File.ReadAllTextAsync(
            GetManifestPath(),
            TestContext.Current.CancellationToken
        );
        var manifest = JsonSerializer.Deserialize<SessionManifest>(
            manifestJson,
            JsonlSerializer.Options
        );
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Segments.Count);
        Assert.Equal(1, manifest.ActiveSegment);
        Assert.True(File.Exists(GetSegmentPath(0)));
        Assert.True(File.Exists(GetSegmentPath(1)));

        var context = await _store.GetContextMessages(TestSessionId);
        Assert.Equal(2, context.Count);
        Assert.Equal("after rollover", context[1].Content);
    }

    //  CompactSession
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>CompactSession 应写入 compact + leaf 两行，并返回 compactEntryId。</summary>
    public async Task CompactSession_WritesCompactAndLeaf_ReturnsEntryId()
    {
        // 先写入一些消息
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        var (compactEntryId, _) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "对话摘要内容",
            firstKeptEntryId: lastEntryId!,
            messagesBefore: 2,
            parentEntryId: null,
            order: 2,
            reason: "overflow",
            willRetry: true,
            estimatedTokensAfter: 500
        );

        Assert.NotNull(compactEntryId);
        Assert.Equal(12, compactEntryId.Length);

        var lines = await ReadJsonlLinesAsync();
        // 验证 compact 行
        var compactLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Compact;
        });

        using var compactDoc = JsonDocument.Parse(compactLine);
        Assert.Equal(compactEntryId, compactDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal("对话摘要内容", compactDoc.RootElement.GetProperty("summary").GetString());
        Assert.Equal(
            lastEntryId,
            compactDoc.RootElement.GetProperty("first-kept-entry-id").GetString()
        );
        Assert.Equal(2, compactDoc.RootElement.GetProperty("messages-before").GetInt32());
        Assert.Equal("overflow", compactDoc.RootElement.GetProperty("reason").GetString());
        Assert.True(compactDoc.RootElement.GetProperty("will-retry").GetBoolean());
        Assert.Equal(500, compactDoc.RootElement.GetProperty("estimated-tokens-after").GetInt32());

        // 新格式分片存储：compact 后活跃分片是 000001.jsonl，包含 compact 条目（无独立 leaf 条目）
        // 验证活跃分片文件已创建
        var segment1Path = GetSegmentPath(1);
        Assert.True(File.Exists(segment1Path), "compact 后的新分片文件应存在");
    }

    [Fact]
    /// <summary>GetActiveMessages 在 compact 后应返回结构化 compact 条目（EventType="compact"），而非 UserMessage。</summary>
    public async Task GetActiveMessages_AfterCompact_ReturnsStructuredCompactEntry()
    {
        // 写入消息
        var messages = CreateSimpleConversation("用户A", "助手A");
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        // compact
        var (compactEntryId, _) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "这是摘要",
            firstKeptEntryId: lastEntryId!,
            messagesBefore: 2,
            parentEntryId: null,
            order: 2
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        // 应包含 compact 条目（EventType="compact"）+ first-kept-entry-id 之后的消息
        var compactEntry = active.FirstOrDefault(m => m.EventType == SessionEventType.Compact);
        Assert.NotNull(compactEntry);
        Assert.Equal(MessageRole.Unknown, compactEntry.Role);
        Assert.Equal("这是摘要", compactEntry.Content);
        Assert.Equal(compactEntryId, compactEntry.EntryId);
    }

    [Fact]
    /// <summary>GetContextMessages 在 compact 后应注入 compact 为 UserMessage（LLM 路径）。</summary>
    public async Task GetContextMessages_AfterCompact_InjectsCompactAsUserMessage()
    {
        var messages = CreateSimpleConversation("用户A", "助手A");
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "这是摘要",
            firstKeptEntryId: lastEntryId!,
            messagesBefore: 2,
            parentEntryId: null,
            order: 2
        );

        var context = await _store.GetContextMessages(TestSessionId);

        // compact 应被注入为 UserMessage，内容以 "[上下文摘要" 开头（新格式带 blockId："[上下文摘要 b1]"）
        var compactMsg = context.FirstOrDefault(m =>
            m.Role == MessageRole.User && m.Content.StartsWith("[上下文摘要")
        );
        Assert.NotNull(compactMsg);
        Assert.Contains("这是摘要", compactMsg.Content);
    }

    [Fact]
    /// <summary>Compact 截断应过滤 first-kept-entry-id 之前的消息。</summary>
    public async Task Compact_TruncatesMessagesBeforeFirstKeptEntryId()
    {
        // 写入 4 条消息
        var allMessages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "消息0"),
            new(ChatMessageRoles.Assistant, "回复0"),
            new(ChatMessageRoles.User, "消息1"),
            new(ChatMessageRoles.Assistant, "回复1"),
        };
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            allMessages,
            startOrder: 0
        );

        // 找到第 3 条消息（order=2）的 entryId 作为 first-kept-entry-id
        var lines = await ReadJsonlLinesAsync();
        var entryOrder2 = lines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("order", out var o) && o.GetInt32() == 2
                    ? d.RootElement.GetProperty("id").GetString()
                    : null;
            })
            .FirstOrDefault(id => id is not null);

        // compact，保留 order=2 及之后的消息
        // parentEntryId 指向 lastEntryId，使 compact 在活跃路径上
        await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "前两条消息的摘要",
            firstKeptEntryId: entryOrder2!,
            messagesBefore: 2,
            parentEntryId: lastEntryId,
            order: 4
        );

        var context = await _store.GetContextMessages(TestSessionId);

        // compact 注入的 UserMessage + order>=2 的消息
        Assert.True(context.Count >= 2, "应至少包含 compact 注入 + 2 条保留消息");
        // 不应包含 order<2 的消息
        Assert.DoesNotContain(context, m => m is { Content: "消息0", EventType: null });
        Assert.DoesNotContain(context, m => m is { Content: "回复0", EventType: null });
    }

    // ═══════════════════════════════════════════════════════════
    //  多次 compact（R2: 所有活跃路径 compact 都应注入）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>多次 compact 后，GetContextMessages 应注入所有活跃路径上的 compact 摘要。</summary>
    public async Task MultipleCompacts_AllActiveCompactsInjectedInContext()
    {
        // 第一轮对话
        var batch1 = CreateSimpleConversation("第一轮用户", "第一轮助手");
        var lastId1 = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch1,
            startOrder: 0
        );

        // 第一次 compact（保留 batch1 最后一条消息）
        var (compactId1, _) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "第一轮摘要",
            firstKeptEntryId: lastId1!,
            messagesBefore: 2,
            parentEntryId: null,
            order: 2
        );

        // 第二轮对话（parent-id 指向 compact1）
        var batch2 = CreateSimpleConversation("第二轮用户", "第二轮助手");
        var lastId2 = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch2,
            lastParentEntryId: compactId1,
            startOrder: 3
        );

        // 第三轮对话（parent-id 指向 lastId2）
        var batch3 = CreateSimpleConversation("第三轮用户", "第三轮助手");
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch3,
            lastParentEntryId: lastId2,
            startOrder: 5
        );

        // 第二次 compact（first-kept-entry-id 指向 batch2 的第一条消息，使 compact1 仍在截断点之后）
        // 这样 compact1(order=2) 和 compact2 都 >= compactFromOrder
        // 新格式：在活跃分片中查找 order=3 的消息（batch2 的第一条）
        var batch2FirstEntryId = await FindEntryIdByOrderInSegmentsAsync(3);

        await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "第二轮摘要",
            firstKeptEntryId: batch2FirstEntryId!,
            messagesBefore: 2,
            parentEntryId: compactId1,
            order: 7
        );

        var context = await _store.GetContextMessages(TestSessionId);

        // 新格式分片存储：只有最新分片的 compact 是可读的。
        // 分片中紧凑地只保留最新一个 compact 条目，老的 compact 通过 parent-id 链桥接。
        var compactMessages = context
            .Where(m => m.Role == MessageRole.User && m.Content.StartsWith("[上下文摘要"))
            .ToList();
        Assert.Single(compactMessages);
        Assert.Contains(compactMessages, m => m.Content.Contains("第二轮摘要"));
    }

    [Fact]
    /// <summary>多次 compact 后，GetActiveMessages 应返回所有活跃路径 compact 条目（EventType="compact"）。</summary>
    public async Task MultipleCompacts_AllActiveCompactsInActiveMessages()
    {
        var batch1 = CreateSimpleConversation("第一轮用户", "第一轮助手");
        var lastId1 = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch1,
            startOrder: 0
        );

        var (compactId1, _) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "第一轮摘要",
            firstKeptEntryId: lastId1!,
            messagesBefore: 2,
            parentEntryId: lastId1,
            order: 2
        );

        var batch2 = CreateSimpleConversation("第二轮用户", "第二轮助手");
        var lastId2 = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch2,
            lastParentEntryId: compactId1,
            startOrder: 3
        );

        // 第三轮对话
        var batch3 = CreateSimpleConversation("第三轮用户", "第三轮助手");
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch3,
            lastParentEntryId: lastId2,
            startOrder: 5
        );

        // 第二次 compact，first-kept-entry-id 指向 batch1 最后保留的消息（order=1）
        // 新格式：first-kept-entry-id 必须在当前活跃分片中，使用 compact 1 保留的 order=1 消息
        var firstKeptEntryId2 = await FindEntryIdByOrderInSegmentsAsync(1);

        await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "第二轮摘要",
            firstKeptEntryId: firstKeptEntryId2!,
            messagesBefore: 2,
            parentEntryId: compactId1,
            order: 7
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        // 新格式分片存储：只有最新分片中的 compact 条目在活跃路径上。
        var compactEntries = active.Where(m => m.EventType == SessionEventType.Compact).ToList();
        Assert.Single(compactEntries);
        Assert.Contains(compactEntries, m => m.Content.Contains("第二轮摘要"));
    }

    // ═══════════════════════════════════════════════════════════
    //  SaveSessionEvent + EventType (R3)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>SaveSessionEvent 应持久化 interrupt 事件。</summary>
    public async Task SaveSessionEvent_PersistsInterruptEvent()
    {
        await _store.SaveSessionEvent(
            TestSessionId,
            TestNovelId,
            new SessionEvent { EventType = SessionEventType.Interrupt, Order = 5 }
        );

        var lines = await ReadJsonlLinesAsync();
        var eventLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Event;
        });

        using var doc = JsonDocument.Parse(eventLine);
        Assert.Equal("interrupt", doc.RootElement.GetProperty("event-type").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("order").GetInt32());
    }

    [Fact]
    /// <summary>SaveSessionEvent 应持久化 model-change 事件（含 from/to）。</summary>
    public async Task SaveSessionEvent_PersistsModelChangeEvent()
    {
        await _store.SaveSessionEvent(
            TestSessionId,
            TestNovelId,
            new SessionEvent
            {
                EventType = SessionEventType.ModelChange,
                From = "gpt-4o",
                To = "claude-sonnet",
                Order = 3,
            }
        );

        var lines = await ReadJsonlLinesAsync();
        var eventLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Event;
        });

        using var doc = JsonDocument.Parse(eventLine);
        Assert.Equal("model-change", doc.RootElement.GetProperty("event-type").GetString());
        Assert.Equal("gpt-4o", doc.RootElement.GetProperty("from").GetString());
        Assert.Equal("claude-sonnet", doc.RootElement.GetProperty("to").GetString());
    }

    [Fact]
    /// <summary>SaveSessionEvent 应持久化 error 事件（含 error-code 和 content）。</summary>
    public async Task SaveSessionEvent_PersistsErrorEvent()
    {
        await _store.SaveSessionEvent(
            TestSessionId,
            TestNovelId,
            new SessionEvent
            {
                EventType = SessionEventType.Error,
                ErrorCode = "429",
                Content = "Rate limit exceeded",
                Order = 7,
            }
        );

        var lines = await ReadJsonlLinesAsync();
        var eventLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Event;
        });

        using var doc = JsonDocument.Parse(eventLine);
        Assert.Equal("error", doc.RootElement.GetProperty("event-type").GetString());
        Assert.Equal("429", doc.RootElement.GetProperty("error-code").GetString());
        Assert.Equal("Rate limit exceeded", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    /// <summary>SaveSessionEvent 应持久化 variant-selected 事件。</summary>
    public async Task SaveSessionEvent_PersistsVariantSelectedEvent()
    {
        await _store.SaveSessionEvent(
            TestSessionId,
            TestNovelId,
            new SessionEvent
            {
                EventType = SessionEventType.VariantSelected,
                SelectedVariantIndex = 2,
                SelectedLabel = "创意版本",
                Order = 10,
            }
        );

        var lines = await ReadJsonlLinesAsync();
        var eventLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Event;
        });

        using var doc = JsonDocument.Parse(eventLine);
        Assert.Equal("variant-selected", doc.RootElement.GetProperty("event-type").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("selected-variant-index").GetInt32());
        Assert.Equal("创意版本", doc.RootElement.GetProperty("selected-label").GetString());
    }

    [Fact]
    /// <summary>GetActiveMessages 应为 event 条目设置 EventType 字段（R3 修复验证）。</summary>
    public async Task GetActiveMessages_EventEntries_HaveEventTypeField()
    {
        // 先写入消息以建立路径
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        // 写入 interrupt 事件，指定 EntryId 以便 leaf 可以指向它
        const string eventEntryId = "evt00001";
        await _store.SaveSessionEvent(
            TestSessionId,
            TestNovelId,
            new SessionEvent
            {
                EntryId = eventEntryId,
                EventType = SessionEventType.Interrupt,
                ParentEntryId = lastEntryId,
                Order = 2,
            }
        );

        // leaf 指向事件条目，使事件在活跃路径上
        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = eventEntryId }
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        var interruptEntry = active.FirstOrDefault(m => m.EventType == SessionEventType.Interrupt);
        Assert.NotNull(interruptEntry);
        Assert.Equal(MessageRole.Unknown, interruptEntry.Role);
        Assert.Equal("中断生成", interruptEntry.Content);
    }

    [Fact]
    /// <summary>GetActiveMessages 应为 model-change 事件设置 EventType 和正确的 Content。</summary>
    public async Task GetActiveMessages_ModelChangeEvent_HasCorrectContent()
    {
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        const string eventEntryId = "evt00002";
        await _store.SaveSessionEvent(
            TestSessionId,
            TestNovelId,
            new SessionEvent
            {
                EntryId = eventEntryId,
                EventType = SessionEventType.ModelChange,
                ParentEntryId = lastEntryId,
                From = "gpt-4o",
                To = "claude-sonnet",
                Order = 2,
            }
        );

        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = eventEntryId }
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        var modelChange = active.FirstOrDefault(m => m.EventType == SessionEventType.ModelChange);
        Assert.NotNull(modelChange);
        Assert.Contains("gpt-4o", modelChange.Content);
        Assert.Contains("claude-sonnet", modelChange.Content);
    }

    // ═══════════════════════════════════════════════════════════
    //  SaveSessionLeaf + 分支切换
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>SaveSessionLeaf 应持久化 leaf 条目。</summary>
    public async Task SaveSessionLeaf_PersistsLeafEntry()
    {
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastEntryId! }
        );

        var lines = await ReadJsonlLinesAsync();
        var leafLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Leaf;
        });

        using var doc = JsonDocument.Parse(leafLine);
        Assert.Equal(lastEntryId, doc.RootElement.GetProperty("target-id").GetString());
    }

    [Fact]
    /// <summary>切换 leaf 应改变活跃路径，GetActiveMessages 只返回新路径上的消息。</summary>
    public async Task SwitchLeaf_ChangesActivePath()
    {
        // 写入主分支消息
        var mainMessages = CreateSimpleConversation("主分支用户", "主分支助手");
        await _store.SaveSessionMessages(TestSessionId, TestNovelId, mainMessages, startOrder: 0);

        // 写入分支消息（parent-id 指向第一条 user 消息，模拟从第一条消息分叉）
        var lines = await ReadJsonlLinesAsync();
        var firstMsgEntryId = lines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("order", out var o) && o.GetInt32() == 0
                    ? d.RootElement.GetProperty("id").GetString()
                    : null;
            })
            .FirstOrDefault(id => id is not null);

        var branchMessages = CreateSimpleConversation("分支用户", "分支助手");
        var branchLastId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            branchMessages,
            lastParentEntryId: firstMsgEntryId,
            startOrder: 2
        );

        // leaf 指向分支末端
        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = branchLastId! }
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        // 应包含第一条消息 + 分支消息，不包含主分支的 assistant 回复
        Assert.Contains(active, m => m.Content == "主分支用户"); // order=0 的 user 消息
        Assert.Contains(active, m => m.Content == "分支用户");
        Assert.Contains(active, m => m.Content == "分支助手");
        Assert.DoesNotContain(active, m => m.Content == "主分支助手");
    }

    [Fact]
    /// <summary>
    /// B5：compact 滚动分片后，切换 leaf 到旧分片的消息，跨分片合并仍可见。
    /// 验证核心价值：分支 A 的消息在旧分片，compact 后切回 A 仍能读取完整历史。
    /// </summary>
    public async Task Compact_ThenSwitchLeaf_OldSegmentMessagesVisible()
    {
        // 分片 0：4 条消息
        var all = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "A用户"),
            new(ChatMessageRoles.Assistant, "A助手"),
            new(ChatMessageRoles.User, "B用户"),
            new(ChatMessageRoles.Assistant, "B助手"),
        };
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            all,
            startOrder: 0
        );

        // leaf 指向最后一条（B助手）
        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastEntryId! }
        );

        // compact：保留 order=2（B用户）之后的，压缩 A用户/A助手
        var bUserEntryId = await FindEntryIdByOrderInSegmentsAsync(2);
        var (_, blockId) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "压缩了 A 轮次",
            firstKeptEntryId: bUserEntryId!,
            messagesBefore: 4,
            parentEntryId: lastEntryId,
            order: 4
        );
        Assert.Equal("b1", blockId);

        // compact 后：GetActiveMessages 应只剩保留消息 + compact（A 被压不可见）
        var afterCompact = await _store.GetActiveMessages(TestSessionId);
        Assert.DoesNotContain(afterCompact, m => m.Content == "A用户");
        Assert.DoesNotContain(afterCompact, m => m.Content == "A助手");
        Assert.Contains(afterCompact, m => m.Content == "B用户");
        Assert.Contains(afterCompact, m => m.Content == "B助手");

        // ★ 切换到 A 分支：追加一条新消息（parent-id 指向 A助手，模拟切回后继续对话）
        // 活跃路径由最后一条消息的 parent-id 链回溯决定（leaf 条目仅 TreeSelector 参考），
        // 所以切回分支必须通过"追加消息挂到 A 链上"实现。
        var aAssistantEntryId = await FindEntryIdByOrderInSegmentsAsync(1);
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            [new LlmChatMessage(ChatMessageRoles.User, "A分支新消息")],
            lastParentEntryId: aAssistantEntryId,
            startOrder: 5
        );

        // B5 核心价值：compact 后追加的分支消息跨分片可见——其 parent-id 链
        // 指向旧分片（分片 0）的 A助手，合并后回溯链完整。
        var afterSwitch = await _store.GetActiveMessages(TestSessionId);
        Assert.Contains(afterSwitch, m => m.Content == "A分支新消息");
        // 压缩语义：A 早期消息（order < compactFromOrder）仍被截断，仅摘要可见
        Assert.DoesNotContain(afterSwitch, m => m is { Content: "A用户", EventType: null });
        Assert.DoesNotContain(afterSwitch, m => m.Content == "B助手"); // 分支切换：B 不在新活跃路径
        Assert.Contains(afterSwitch, m => m.EventType == SessionEventType.Compact); // 摘要保留
    }

    [Fact]
    /// <summary>
    /// B5：跨分片合并后 compact 截断仍生效（被压缩消息不泄漏回可见列表）。
    /// 与 Compact_TruncatesMessagesBeforeFirstKeptEntryId 互补：验证跨分片场景。
    /// </summary>
    public async Task Compact_AcrossSegments_CompactedMessagesStillHidden()
    {
        // 两批消息：分片 0 全量，compact 后分片 1 只保留后两条
        var batch1 = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "旧1"),
            new(ChatMessageRoles.Assistant, "旧2"),
        };
        var lastId1 = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch1,
            startOrder: 0
        );
        var batch2 = CreateSimpleConversation("新1", "新2");
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            batch2,
            lastParentEntryId: lastId1,
            startOrder: 2
        );

        // compact 保留 order=2（新1）之后的
        var new1EntryId = await FindEntryIdByOrderInSegmentsAsync(2);
        await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "压缩旧轮次",
            firstKeptEntryId: new1EntryId!,
            messagesBefore: 4,
            parentEntryId: lastId1,
            order: 4
        );

        // 跨分片合并读取：旧分片有 旧1/旧2，但 compactFromOrder 截断应隐藏它们
        var context = await _store.GetContextMessages(TestSessionId);
        Assert.DoesNotContain(context, m => m is { Content: "旧1", EventType: null });
        Assert.DoesNotContain(context, m => m is { Content: "旧2", EventType: null });
        Assert.Contains(context, m => m.Content == "新1");
        Assert.Contains(context, m => m.Content == "新2");
        Assert.Contains(context, m => m.Content.StartsWith("[上下文摘要"));
    }

    // ═══════════════════════════════════════════════════════════
    //  SaveBranchSummary
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>SaveBranchSummary 应持久化 from-id 字段（R7 验证）。</summary>
    public async Task SaveBranchSummary_PersistsFromId()
    {
        await _store.SaveBranchSummary(
            TestSessionId,
            TestNovelId,
            new BranchSummary
            {
                EntryId = "bs001",
                ParentEntryId = "parent001",
                FromId = "from001",
                Summary = "被放弃分支的摘要",
                Order = 5,
            }
        );

        var lines = await ReadJsonlLinesAsync();
        var summaryLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.BranchSummary;
        });

        using var doc = JsonDocument.Parse(summaryLine);
        Assert.Equal("bs001", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("from001", doc.RootElement.GetProperty("from-id").GetString());
        Assert.Equal("被放弃分支的摘要", doc.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    /// <summary>GetContextMessages 应将 branch-summary 注入为 UserMessage。</summary>
    public async Task GetContextMessages_InjectsBranchSummaryAsUserMessage()
    {
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        const string bsEntryId = "bs001";
        await _store.SaveBranchSummary(
            TestSessionId,
            TestNovelId,
            new BranchSummary
            {
                EntryId = bsEntryId,
                ParentEntryId = lastEntryId,
                Summary = "之前尝试的方案",
                Order = 2,
            }
        );

        // leaf 指向 branch-summary，使其在活跃路径上
        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = bsEntryId }
        );

        var context = await _store.GetContextMessages(TestSessionId);

        var summaryMsg = context.FirstOrDefault(m =>
            m.Role == MessageRole.User && m.Content.Contains("[之前尝试的分支摘要]")
        );
        Assert.NotNull(summaryMsg);
        Assert.Contains("之前尝试的方案", summaryMsg.Content);
    }

    // ═══════════════════════════════════════════════════════════
    //  SaveSessionLabel (R8: label 参与路径回溯)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>SaveSessionLabel 应持久化 label 条目。</summary>
    public async Task SaveSessionLabel_PersistsLabelEntry()
    {
        await _store.SaveSessionLabel(
            TestSessionId,
            TestNovelId,
            new SessionLabel
            {
                EntryId = "lbl001",
                ParentEntryId = "parent001",
                TargetId = "target001",
                Label = "重要节点",
                Order = 3,
            }
        );

        var lines = await ReadJsonlLinesAsync();
        var labelLine = lines.First(l =>
        {
            if (string.IsNullOrWhiteSpace(l))
                return false;
            using var d = JsonDocument.Parse(l);
            return ParseEntryType(l) == JsonlEntryType.Label;
        });

        using var doc = JsonDocument.Parse(labelLine);
        Assert.Equal("lbl001", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("target001", doc.RootElement.GetProperty("target-id").GetString());
        Assert.Equal("重要节点", doc.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    /// <summary>label 条目应参与路径回溯但不输出到 messages（R8 验证）。</summary>
    public async Task LabelEntries_ParticipatesInPathResolution_NotInMessages()
    {
        // 写入消息
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        // 在消息之间插入 label
        var lines = await ReadJsonlLinesAsync();
        var firstMsgEntryId = lines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("order", out var o) && o.GetInt32() == 0
                    ? d.RootElement.GetProperty("id").GetString()
                    : null;
            })
            .FirstOrDefault(id => id is not null);

        await _store.SaveSessionLabel(
            TestSessionId,
            TestNovelId,
            new SessionLabel
            {
                EntryId = "lbl001",
                ParentEntryId = firstMsgEntryId,
                TargetId = firstMsgEntryId,
                Label = "起点标记",
                Order = 1,
            }
        );

        // leaf 指向最后一条消息
        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastEntryId! }
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        // label 不应出现在 messages 中
        Assert.DoesNotContain(active, m => m.Content == "起点标记");
        // 但路径回溯应正常工作（两条消息都应出现）
        Assert.Equal(2, active.Count);
    }

    // ═══════════════════════════════════════════════════════════
    //  GetTreeStructure
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>GetTreeStructure 应返回正确的树结构和活跃路径。</summary>
    public async Task GetTreeStructure_ReturnsCorrectTree()
    {
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastEntryId! }
        );

        var tree = await _store.GetTreeStructure(TestSessionId);

        Assert.NotNull(tree);
        Assert.Equal(2, tree.Nodes.Count);
        Assert.NotNull(tree.ActiveLeafId);
        Assert.Equal(lastEntryId, tree.ActiveLeafId);
    }

    [Fact]
    /// <summary>GetTreeStructure 应正确识别分支点。</summary>
    public async Task GetTreeStructure_IdentifiesForkNodes()
    {
        // 主分支
        var mainMessages = CreateSimpleConversation("主用户", "主助手");
        await _store.SaveSessionMessages(TestSessionId, TestNovelId, mainMessages, startOrder: 0);

        // 获取第一条消息的 entryId 作为分叉点
        var lines = await ReadJsonlLinesAsync();
        var firstEntryId = lines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("order", out var o) && o.GetInt32() == 0
                    ? d.RootElement.GetProperty("id").GetString()
                    : null;
            })
            .FirstOrDefault(id => id is not null);

        // 分支
        var branchMessages = CreateSimpleConversation("分支用户", "分支助手");
        var branchLastId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            branchMessages,
            lastParentEntryId: firstEntryId,
            startOrder: 2
        );

        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = branchLastId! }
        );

        var tree = await _store.GetTreeStructure(TestSessionId);

        Assert.True(tree.HasBranches, "应检测到分支");
        var forkNode = tree.Nodes.FirstOrDefault(n => n.IsFork);
        Assert.NotNull(forkNode);
        Assert.Equal(firstEntryId, forkNode.EntryId);
        Assert.Equal(2, forkNode.ChildIds.Count);
    }

    [Fact]
    /// <summary>GetTreeStructure 应包含 label 信息。</summary>
    public async Task GetTreeStructure_IncludesLabels()
    {
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        var lines = await ReadJsonlLinesAsync();
        var firstEntryId = lines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("order", out var o) && o.GetInt32() == 0
                    ? d.RootElement.GetProperty("id").GetString()
                    : null;
            })
            .FirstOrDefault(id => id is not null);

        await _store.SaveSessionLabel(
            TestSessionId,
            TestNovelId,
            new SessionLabel
            {
                EntryId = "lbl001",
                ParentEntryId = firstEntryId,
                TargetId = firstEntryId,
                Label = "起点",
                Order = 1,
            }
        );

        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastEntryId! }
        );

        var tree = await _store.GetTreeStructure(TestSessionId);

        var labeledNode = tree.Nodes.FirstOrDefault(n => n.Labels.Contains("起点"));
        Assert.NotNull(labeledNode);
    }

    // ═══════════════════════════════════════════════════════════
    //  InputHistory
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>SaveInputHistory + GetInputHistory 应正确持久化和读取。</summary>
    public async Task InputHistory_SaveAndGet_RoundTrip()
    {
        await _store.SaveInputHistory(TestSessionId, "第一条输入");
        await _store.SaveInputHistory(TestSessionId, "第二条输入");
        await _store.SaveInputHistory(TestSessionId, "第三条输入");

        var history = await _store.GetInputHistory(TestSessionId, limit: 10);

        // GetInputHistory 返回最近优先，倒序
        Assert.True(history.Count >= 3);
        Assert.Contains("第三条输入", history);
    }

    [Fact]
    /// <summary>GetInputHistory limit 参数应限制返回数量。</summary>
    public async Task GetInputHistory_RespectsLimit()
    {
        for (var i = 0; i < 10; i++)
            await _store.SaveInputHistory(TestSessionId, $"输入{i}");

        var history = await _store.GetInputHistory(TestSessionId, limit: 3);
        Assert.True(history.Count <= 3);
    }

    // ═══════════════════════════════════════════════════════════
    //  GetSessions
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>GetSessions 应返回指定 novelId 下的所有会话。</summary>
    public async Task GetSessions_ReturnsSessionsForNovel()
    {
        await CreateTestNovelAsync();

        // 创建两个会话的 JSONL 文件
        var msgs1 = CreateSimpleConversation("会话1标题", "回复1");
        var msgs2 = CreateSimpleConversation("会话2标题", "回复2");
        await _store.SaveSessionMessages("sess001", TestNovelId, msgs1, startOrder: 0);
        await _store.SaveSessionMessages("sess002", TestNovelId, msgs2, startOrder: 0);

        var sessions = await _store.GetSessions(TestNovelId);

        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(TestNovelId, s.NovelId));
    }

    [Fact]
    /// <summary>会话目录必须按小说 id 编码：标题改名后历史仍可见，新数据不能写进标题目录。</summary>
    public async Task GetSessions_UsesNovelIdDirectory_WhenTitleDiffers()
    {
        const string novelId = "novel-id-123";
        const string title = "剑门";
        await CreateTestNovelAsync(novelId, title);

        await _store.SaveSessionMessages(
            "sess-title-mismatch",
            novelId,
            CreateSimpleConversation("标题与会话目录无关", "好"),
            startOrder: 0
        );

        var idDir = Path.Combine(_tempSessionsDir, $"--novels-{novelId}--");
        var titleDir = Path.Combine(_tempSessionsDir, $"--novels-{title}--");
        Assert.True(Directory.Exists(idDir));
        Assert.False(Directory.Exists(titleDir));

        var sessions = await _store.GetSessions(novelId);
        var session = Assert.Single(sessions);
        Assert.Equal(novelId, session.NovelId);
        Assert.Equal("sess-title-mismatch", session.Id);
    }

    [Fact]
    /// <summary>升级兼容：旧版本写在标题目录下的会话仍能被列出与读取，且不与 id 目录重复。</summary>
    public async Task GetSessions_ReadsLegacyTitleDirectory()
    {
        const string novelId = "novel-id-legacy";
        const string title = "旧标题";
        const string sessionId = "sess-legacy";
        await CreateTestNovelAsync(novelId, title);

        await _store.SaveSessionMessages(
            sessionId,
            novelId,
            CreateSimpleConversation("旧会话", "仍在"),
            startOrder: 0
        );

        var idDir = Path.Combine(_tempSessionsDir, $"--novels-{novelId}--");
        var legacyDir = Path.Combine(_tempSessionsDir, $"--novels-{title}--");
        CopyDirectory(
            Directory.GetDirectories(idDir).Single(),
            Path.Combine(legacyDir, Path.GetFileName(Directory.GetDirectories(idDir).Single()))
        );

        var sessions = await _store.GetSessions(novelId);
        Assert.Single(sessions);
        Assert.Equal(sessionId, sessions[0].Id);

        // 索引也要从 MANIFEST 读出正确的小说 id（目录名是标题时不能误判）。
        var reopened = CreateStore();
        var session = await reopened.GetSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal(novelId, session.NovelId);
    }

    // ═══════════════════════════════════════════════════════════
    //  toolName 回填
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>GetActiveMessages 应从 assistant 的 toolCallsJson 回填 tool 消息的 toolName。</summary>
    public async Task GetActiveMessages_BackfillsToolName()
    {
        var assistantMsg = new LlmChatMessage(ChatMessageRoles.Assistant, "")
        {
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "tc_001",
                    FunctionCall = new FunctionCall
                    {
                        Name = "search_web",
                        Arguments = "{\"q\":\"test\"}",
                    },
                },
            ],
        };

        var toolMsg = new LlmChatMessage(ChatMessageRoles.Tool, "搜索结果")
        {
            ToolCallId = "tc_001",
        };
        // 不设置 Name，让回填逻辑处理

        var messages = new List<LlmChatMessage> { assistantMsg, toolMsg };
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastEntryId! }
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        var toolEntry = active.FirstOrDefault(m => m.Role == MessageRole.Tool);
        Assert.NotNull(toolEntry);
        Assert.Equal("search_web", toolEntry.ToolName);
    }

    // ═══════════════════════════════════════════════════════════
    //  Dedup by order
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>GetActiveMessages 应按 order 去重（取最后一条）。</summary>
    public async Task GetActiveMessages_DeduplicatesByOrder()
    {
        // 写入消息
        var messages = CreateSimpleConversation("原始用户", "原始助手");
        await _store.SaveSessionMessages(TestSessionId, TestNovelId, messages, startOrder: 0);

        // 读取第一条消息的 entryId 和 parent-id，用于构造覆盖条目
        var lines = await ReadJsonlLinesAsync();
        var firstMsg = lines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("order", out var o) && o.GetInt32() == 0
                    ? (
                        id: d.RootElement.GetProperty("id").GetString(),
                        parentId: d.RootElement.TryGetProperty("parent-id", out var p)
                            ? p.GetString()
                            : null
                    )
                    : (null, null);
            })
            .First(x => x.id is not null);

        // 手动追加一条同 order 的消息（模拟覆盖场景），parent-id 与原始消息相同
        var overrideLine = new Dictionary<string, object?>
        {
            ["type"] = "message", // JSONL wire format, kept as string for raw dict construction
            ["id"] = "override1",
            ["parent-id"] = firstMsg.parentId,
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["role"] = "user",
            ["content"] = "覆盖用户",
            ["order"] = 0,
        };
        var json = JsonSerializer.Serialize(overrideLine);
        var activePath = GetSegmentPath(0);
        await AtomicFile.AppendTextLockedAsync(
            activePath,
            json + "\n",
            checkConflict: false,
            TestContext.Current.CancellationToken
        );

        // leaf 指向 override1，使覆盖消息在活跃路径上
        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = "override1" }
        );

        var active = await _store.GetActiveMessages(TestSessionId);

        // order=0 应只有一条，且是最后写入的"覆盖用户"
        var order0Entries = active.Where(m => m.Order == 0).ToList();
        Assert.Single(order0Entries);
        Assert.Equal("覆盖用户", order0Entries[0].Content);
    }

    // ═══════════════════════════════════════════════════════════
    //  空会话 / 不存在的会话
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>GetActiveMessages 对不存在的会话应返回空列表。</summary>
    public async Task GetActiveMessages_NonexistentSession_ReturnsEmpty()
    {
        var result = await _store.GetActiveMessages("nonexistent");
        Assert.Empty(result);
    }

    [Fact]
    /// <summary>GetContextMessages 对不存在的会话应返回空列表。</summary>
    public async Task GetContextMessages_NonexistentSession_ReturnsEmpty()
    {
        var result = await _store.GetContextMessages("nonexistent");
        Assert.Empty(result);
    }

    [Fact]
    /// <summary>GetTreeStructure 对不存在的会话应返回空结构。</summary>
    public async Task GetTreeStructure_NonexistentSession_ReturnsEmpty()
    {
        var result = await _store.GetTreeStructure("nonexistent");
        Assert.Empty(result.Nodes);
        Assert.Empty(result.ActiveEntryIds);
    }

    // ═══════════════════════════════════════════════════════════
    //  B2 decompress 闭环测试：压缩 → 跨分片恢复原文
    // ═══════════════════════════════════════════════════════════

    [Fact]
    /// <summary>
    /// B2 闭环：写入 4 条消息 → 压缩前 2 条 → ReadCompactedMessagesAsync 恢复原文，
    /// 且恢复内容与压缩前一致（含被压缩消息的完整 Content）。
    /// </summary>
    public async Task Compact_ThenDecompress_RecoversOriginalMessages()
    {
        var messages = CreateSimpleConversation(); // 2 条消息 (user + assistant)
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );

        // 再加 2 条，共 4 条（parent-id 链接续，保证活跃路径完整）
        var more = new[]
        {
            new LlmChatMessage(ChatMessageRoles.User, "第三条用户消息"),
            new LlmChatMessage(ChatMessageRoles.Assistant, "第三条助手回复"),
        };
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            more,
            lastParentEntryId: lastEntryId,
            startOrder: 2
        );

        // 写 leaf 确保活跃路径完整
        var allLines = await ReadJsonlLinesAsync();
        var lastId = allLines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            })
            .Last();
        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastId! }
        );

        // 压缩前 2 条（first-kept-entry-id 指向第 3 条），只压缩前两条
        var active = await _store.GetActiveMessages(TestSessionId);
        Assert.Equal(4, active.Count);
        var firstKeptEntryId = active[2].EntryId!;
        var compressedIds = active.Take(2).Select(m => m.EntryId!).ToList();

        var (_, blockId) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "压缩了前两条消息",
            firstKeptEntryId,
            messagesBefore: 4,
            compressedMessageIds: compressedIds
        );

        // 验证 blockId 生成
        Assert.Equal("b1", blockId);

        // ListCompactBlocksAsync 能看到该块
        var blocks = await _store.ListCompactBlocksAsync(TestSessionId);
        Assert.Single(blocks);
        Assert.Equal("b1", blocks[0].BlockId);
        Assert.Equal(2, blocks[0].MessageCount);

        // ReadCompactedMessagesAsync 恢复原文
        var recovered = await _store.ReadCompactedMessagesAsync(TestSessionId, "b1");
        Assert.NotNull(recovered);
        Assert.Equal(2, recovered!.Count);
        Assert.Equal(MessageRole.User, recovered[0].Role);
        Assert.Equal(messages[0].Content, recovered[0].Content);
        Assert.Equal(messages[1].Content, recovered[1].Content);

        // 不存在的 blockId 返回 null
        Assert.Null(await _store.ReadCompactedMessagesAsync(TestSessionId, "b99"));
    }

    [Fact]
    /// <summary>
    /// B2 嵌套压缩：压缩两次后，第一个 block 仍可从旧分片恢复（分片 append-only）。
    /// </summary>
    public async Task Compact_Twice_FirstBlockStillRecoverable()
    {
        var messages = CreateSimpleConversation();
        var lastEntryId = await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            startOrder: 0
        );
        var more = new[]
        {
            new LlmChatMessage(ChatMessageRoles.User, "第三条用户消息"),
            new LlmChatMessage(ChatMessageRoles.Assistant, "第三条助手回复"),
        };
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            more,
            lastParentEntryId: lastEntryId,
            startOrder: 2
        );
        var allLines = await ReadJsonlLinesAsync();
        var lastId = allLines
            .Select(l =>
            {
                using var d = JsonDocument.Parse(l);
                return d.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            })
            .Last();
        await _store.SaveSessionLeaf(
            TestSessionId,
            TestNovelId,
            new SessionLeaf { TargetId = lastId! }
        );

        var active = await _store.GetActiveMessages(TestSessionId);
        // 第一次压缩：压前 2 条
        var firstKept = active[2].EntryId!;
        var ids1 = active.Take(2).Select(m => m.EntryId!).ToList();
        var (_, blockId1) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "第一次摘要",
            firstKept,
            messagesBefore: 4,
            compressedMessageIds: ids1
        );
        Assert.Equal("b1", blockId1);

        // 第二次压缩：把摘要 + 保留的 2 条再压（保留第 4 条）
        var active2 = await _store.GetActiveMessages(TestSessionId);
        // Active 模式：compact 条目渲染为 Unknown，不参与压缩 ID 收集（A3 修复）
        var ids2 = active2
            .Where(m => m.EventType != SessionEventType.Compact)
            .Take(2)
            .Select(m => m.EntryId!)
            .ToList();
        var (_, blockId2) = await _store.CompactSession(
            TestSessionId,
            TestNovelId,
            summary: "第二次摘要",
            active2[^1].EntryId!,
            messagesBefore: active2.Count,
            compressedMessageIds: ids2
        );
        Assert.Equal("b2", blockId2);

        // b1 仍可从旧分片恢复（分片 append-only）
        var rec1 = await _store.ReadCompactedMessagesAsync(TestSessionId, "b1");
        Assert.NotNull(rec1);
        Assert.Equal(2, rec1!.Count);
        Assert.Equal(messages[0].Content, rec1[0].Content);
        Assert.Equal(messages[1].Content, rec1[1].Content);

        // b2 可恢复（第 3 条 + 摘要消息本身？A3 过滤后 ids2 只含第 3 条真实消息）
        var rec2 = await _store.ReadCompactedMessagesAsync(TestSessionId, "b2");
        Assert.NotNull(rec2);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}

/// <summary>
/// 标记测试集合，禁止并行执行。
/// AtomicFile 使用静态 SemaphoreSlim 和内容追踪，
/// 并行测试会导致文件锁冲突。
/// </summary>
[CollectionDefinition("FileNovelStore", DisableParallelization = true)]
public sealed class FileNovelStoreSessionCollection;
