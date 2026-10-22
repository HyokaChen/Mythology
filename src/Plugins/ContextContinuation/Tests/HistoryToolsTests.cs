using System.Text.Json;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Plugins.ContextContinuation;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Plugins.ContextContinuation.Tests;

/// <summary>
/// history_* 工具族：跨页检索（字面子串）、过滤、按 id 回读、未知页/空查询错误语义，
/// 以及换页前内容仍可被检索到（页截断不影响 history）。
/// </summary>
[Collection("FileNovelStore")]
public sealed class HistoryToolsTests : IAsyncLifetime
{
    private readonly string _tempRoot;
    private readonly string _tempSessionsDir;
    private FileSessionStore _store = null!;
    private AgentType _agent = null!;
    private const string TestNovelId = "historynovel";
    private const string TestSessionId = "histsess0001";

    public HistoryToolsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mythology-hist-{Guid.NewGuid():N}");
        _tempSessionsDir = Path.Combine(_tempRoot, "sessions");
    }

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempSessionsDir);
        _store = new FileSessionStore(
            novelsRoot: Path.Combine(_tempRoot, "novels"),
            mythologyDir: _tempRoot
        )
        {
            SkipConflictCheck = true,
        };
        _agent = new AgentType(new ToolRegistry())
        {
            SessionId = TestSessionId,
            CurrentNovelId = TestNovelId,
        };
        return default;
    }

    public ValueTask DisposeAsync()
    {
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
            // 清理失败不影响测试
        }
        return default;
    }

    private async Task WriteMessagesAsync(params (string Role, string Text)[] messages)
    {
        var list = messages
            .Select(m => new LlmTornado.Chat.ChatMessage(
                MessageRoleMapping.ToChatMessageRoles(
                    m.Role switch
                    {
                        "assistant" => MessageRole.Assistant,
                        "tool" => MessageRole.Tool,
                        _ => MessageRole.User,
                    }
                ),
                m.Text
            ))
            .ToList();
        var (nextOrder, lastEntryId) = await _store.GetSessionMetadataAsync(TestSessionId);
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            list,
            lastParentEntryId: lastEntryId,
            startOrder: nextOrder
        );
    }

    private static string TextOf(AgentToolResult result) =>
        Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;

    private static string TextOf(AgentToolResult<string> result) => TextOf((AgentToolResult)result);

    // ═══════════════════════════════════════════════════════════
    //  换页前内容仍可检索（页截断只影响模型上下文，不影响 history）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_FindsMessagesSealedInPreviousPage()
    {
        await WriteMessagesAsync(("user", "把这段旧代码里的魔法数字改掉"));
        await _store.StartNewContextWindowAsync(TestSessionId, TestNovelId, "model", order: 1);
        await WriteMessagesAsync(("user", "现在改渲染层"));

        // 模型上下文已被页截断（旧消息不可见）
        Assert.DoesNotContain(
            await _store.GetContextMessages(TestSessionId),
            m => m.Content!.Contains("魔法数字")
        );

        var tool = new HistorySearchContentsTool(_agent, _store);
        var result = await tool.ExecuteAsync(
            "call_1",
            new HistorySearchParams { Query = "魔法数字" },
            CancellationToken.None
        );

        var text = TextOf(result);
        Assert.False(result.IsError);
        Assert.Contains("魔法数字", text);
        Assert.Contains("[窗口 w0 | id", text);
        Assert.Contains("order 0 | user]", text);
    }

    // ═══════════════════════════════════════════════════════════
    //  过滤与边界
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_FiltersByRoleAndWindow_AndErrorsOnEmptyQuery()
    {
        await WriteMessagesAsync(("user", "目标文本 A"), ("assistant", "目标文本 B"));
        await _store.StartNewContextWindowAsync(TestSessionId, TestNovelId, "model", order: 2);
        await WriteMessagesAsync(("user", "目标文本 C"));

        var tool = new HistorySearchContentsTool(_agent, _store);

        var assistantOnly = await tool.ExecuteAsync(
            "call_1",
            new HistorySearchParams { Query = "目标文本", Role = "assistant" },
            CancellationToken.None
        );
        var assistantText = TextOf(assistantOnly);
        Assert.Contains("目标文本 B", assistantText);
        Assert.DoesNotContain("目标文本 C", assistantText);

        var windowOnly = await tool.ExecuteAsync(
            "call_2",
            new HistorySearchParams { Query = "目标文本", WindowId = "w1" },
            CancellationToken.None
        );
        var windowText = TextOf(windowOnly);
        Assert.Contains("目标文本 C", windowText);
        Assert.DoesNotContain("目标文本 A", windowText);

        var unknownWindow = await tool.ExecuteAsync(
            "call_3",
            new HistorySearchParams { Query = "目标文本", WindowId = "w9" },
            CancellationToken.None
        );
        Assert.True(unknownWindow.IsError);
        Assert.Contains("未找到页", TextOf(unknownWindow));

        var emptyQuery = await tool.ExecuteAsync(
            "call_4",
            new HistorySearchParams { Query = "   " },
            CancellationToken.None
        );
        Assert.True(emptyQuery.IsError);
        Assert.Contains("query 不能为空", TextOf(emptyQuery));
    }

    // ═══════════════════════════════════════════════════════════
    //  按 id 回读 + 区间切片
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadItem_ReturnsSlicedOriginalText()
    {
        var longText = "开头" + new string('x', 500) + "结尾";
        await WriteMessagesAsync(("user", longText));

        var search = new HistorySearchContentsTool(_agent, _store);
        var searchResult = TextOf(
            await search.ExecuteAsync(
                "call_1",
                new HistorySearchParams { Query = "开头" },
                CancellationToken.None
            )
        );

        var itemId = System
            .Text.RegularExpressions.Regex.Match(searchResult, @"id ([0-9a-f]{12})")
            .Groups[1]
            .Value;
        Assert.NotEqual("", itemId);

        var read = new HistoryReadItemTool(_agent, _store);
        var slice = TextOf(
            await read.ExecuteAsync(
                "call_2",
                new HistoryReadItemParams
                {
                    WindowId = "w0",
                    ItemId = itemId,
                    OffsetChars = 0,
                    LimitChars = 8,
                },
                CancellationToken.None
            )
        );

        Assert.Contains($"id {itemId}", slice);
        Assert.Contains("字符 0-8/", slice);
        Assert.Contains("开头", slice);
        Assert.DoesNotContain("结尾", slice);

        // 未知 id / offset 越界
        var missing = await read.ExecuteAsync(
            "call_3",
            new HistoryReadItemParams { WindowId = "w0", ItemId = "deadbeef0000" },
            CancellationToken.None
        );
        Assert.True(missing.IsError);

        var beyond = await read.ExecuteAsync(
            "call_4",
            new HistoryReadItemParams
            {
                WindowId = "w0",
                ItemId = itemId,
                OffsetChars = 99_999,
            },
            CancellationToken.None
        );
        Assert.True(beyond.IsError);
        Assert.Contains("超出条目长度", TextOf(beyond));
    }

    // ═══════════════════════════════════════════════════════════
    //  list_windows / list_items / 无会话
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ListWindows_And_ListItems_Behave()
    {
        await WriteMessagesAsync(("user", "第一条"));
        await _store.StartNewContextWindowAsync(TestSessionId, TestNovelId, "model", order: 1);
        await WriteMessagesAsync(("user", "第二条"));

        var windows = new HistoryListWindowsTool(_agent, _store);
        var windowsText = TextOf(
            await windows.ExecuteAsync(
                "call_1",
                new HistoryListWindowsParams(),
                CancellationToken.None
            )
        );
        Assert.Contains("共 2 页", windowsText);
        Assert.Contains("[窗口 w0 | 1 条", windowsText);
        Assert.Contains("(当前)", windowsText);

        var items = new HistoryListItemsTool(_agent, _store);
        var itemsText = TextOf(
            await items.ExecuteAsync(
                "call_2",
                new HistoryListItemsParams { WindowId = "w1" },
                CancellationToken.None
            )
        );
        Assert.Contains("第二条", itemsText);
        Assert.DoesNotContain("第一条", itemsText);

        // 无会话 → IsError（三个工具一致）
        var orphan = new AgentType(new ToolRegistry());
        foreach (
            var tool in new AgentTool[]
            {
                new HistoryListWindowsTool(orphan, _store),
                new HistoryListItemsTool(orphan, _store),
                new HistorySearchContentsTool(orphan, _store),
                new HistoryReadItemTool(orphan, _store),
            }
        )
        {
            var result = await tool.ExecuteAsync(
                "call_x",
                System.Text.Json.JsonDocument.Parse("{}").RootElement,
                CancellationToken.None
            );
            Assert.True(result.IsError);
            Assert.Contains("当前没有活动会话", TextOf(result));
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  隐藏名单 + 工具 schema
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void HiddenTools_CoverContinuationToolFamilies_AndNewContextHasNoteSchema()
    {
        var names = new AgentTool[]
        {
            new NewContextTool(_agent),
            new HistoryListWindowsTool(_agent, _store),
            new HistoryListItemsTool(_agent, _store),
            new HistoryReadItemTool(_agent, _store),
            new HistorySearchContentsTool(_agent, _store),
        }
            .Select(t => t.Name)
            .ToList();

        Assert.Equal(5, names.Count);
        Assert.All(names, n => Assert.True(HiddenTools.IsHidden(n), $"{n} 必须在隐藏名单里"));
        Assert.Equal(5, HiddenTools.Names.Count);

        // new_context 必须带手写 schema——泛型基类对无属性参数类返回 null，
        // 而 BuildChatRequest 只发送 Parameters.HasValue 的工具（否则工具对模型不可见）。
        var tool = new NewContextTool(_agent);
        Assert.NotNull(tool.Parameters);
        var schema = tool.Parameters!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("note", out _));
    }

    /// <summary>new_context 的可选 note：记录成交接说明（截断），无 note 时不置位。</summary>
    [Fact]
    public async Task NewContextTool_RecordsOptionalHandoffNote()
    {
        var tool = new NewContextTool(_agent);

        await tool.ExecuteAsync(
            "call-1",
            JsonDocument.Parse("""{"note":"三~五章已改完；下一步核对第六章时间线"}""").RootElement,
            CancellationToken.None
        );
        Assert.Equal("model", _agent.PendingNewContextReason);
        Assert.Equal("三~五章已改完；下一步核对第六章时间线", _agent.PendingNewContextNote);
        Assert.Equal("三~五章已改完；下一步核对第六章时间线", _agent.ConsumeNewContextNote());
        Assert.Null(_agent.ConsumeNewContextNote()); // 取走即清空

        _agent.ConsumeNewContextRequest();
        await tool.ExecuteAsync(
            "call-2",
            JsonDocument.Parse("{}").RootElement,
            CancellationToken.None
        );
        Assert.Null(_agent.PendingNewContextNote); // 不填 note = 只交接用户最后需求（锚点）

        // 超长 note 截断（它每轮都出现在新片片段里）
        _agent.ConsumeNewContextRequest();
        var longNote = new string('长', NewContextTool.NoteMaxChars + 50);
        var args3 = JsonDocument
            .Parse(JsonSerializer.Serialize(new { note = longNote }))
            .RootElement;
        await tool.ExecuteAsync("call-3", args3, CancellationToken.None);
        var stored = _agent.ConsumeNewContextNote()!;
        Assert.Equal(NewContextTool.NoteMaxChars + 1, stored.Length); // 截断 + 省略号
        Assert.EndsWith("…", stored, StringComparison.Ordinal);
    }
}
