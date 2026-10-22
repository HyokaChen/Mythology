using System.Reflection;
using System.Text;
using System.Text.Json;
using Core.Agent;
using Core.Shared;
using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ConversationLoop 纯静态方法测试。
/// 通过反射测试 InjectNovelId、PatchReasoningContent、BuildAssistantMessage。
/// 这些方法不依赖 LlmTornado 运行时，可独立验证。
/// </summary>
public sealed class ConversationLoopStaticTests
{
    // ══════════════════════════════════════════════════════
    //  InjectNovelId — 通过反射访问私有静态方法
    // ══════════════════════════════════════════════════════

    private static JsonElement InjectNovelId(JsonElement args, string novelId)
    {
        var method = typeof(ConversationLoop).GetMethod(
            "InjectNovelId",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        return (JsonElement)method!.Invoke(null, [args, novelId])!;
    }

    [Fact]
    public void InjectNovelId_NonObject_ReturnsAsIs()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("\"hello\"");
        var result = InjectNovelId(json, "novel-1");
        Assert.Equal(JsonValueKind.String, result.ValueKind);
    }

    [Fact]
    public void InjectNovelId_NoNovelIdField_ReturnsAsIs()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{ \"other\": 42 }");
        var result = InjectNovelId(json, "novel-1");
        Assert.Equal(42, result.GetProperty("other").GetInt32());
    }

    [Fact]
    public void InjectNovelId_NovelIdNull_Injects()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{ \"novel-id\": null, \"other\": 1 }");
        var result = InjectNovelId(json, "novel-99");
        Assert.Equal("novel-99", result.GetProperty("novel-id").GetString());
        Assert.Equal(1, result.GetProperty("other").GetInt32());
    }

    [Fact]
    public void InjectNovelId_NovelIdEmptyString_Injects()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{ \"novel-id\": \"\" }");
        var result = InjectNovelId(json, "novel-1");
        Assert.Equal("novel-1", result.GetProperty("novel-id").GetString());
    }

    [Fact]
    public void InjectNovelId_NovelIdHasValue_DoesNotOverride()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{ \"novel-id\": \"existing\" }");
        var result = InjectNovelId(json, "novel-1");
        Assert.Equal("existing", result.GetProperty("novel-id").GetString());
    }

    [Fact]
    public void InjectNovelId_NumberNovelId_DoesNotOverride()
    {
        // novel_id 是数字类型，不被视为空，不注入
        var json = JsonSerializer.Deserialize<JsonElement>("{ \"novel-id\": 123 }");
        var result = InjectNovelId(json, "novel-1");
        Assert.Equal(123, result.GetProperty("novel-id").GetInt32());
    }

    // ══════════════════════════════════════════════════════
    //  PatchReasoningContent — 通过反射访问私有静态方法
    // ══════════════════════════════════════════════════════

    private static void PatchReasoningContent(List<ChatMessage> messages, string reasoningText)
    {
        var method = typeof(ConversationLoop).GetMethod(
            "PatchReasoningContent",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        method!.Invoke(null, [messages, reasoningText]);
    }

    [Fact]
    public void PatchReasoningContent_EmptyReasoning_NoChange()
    {
        var msg = new ChatMessage(ChatMessageRoles.Assistant, "reply");
        var messages = new List<ChatMessage> { msg };
        PatchReasoningContent(messages, "");
        Assert.Null(messages[0].ReasoningContent);
    }

    [Fact]
    public void PatchReasoningContent_NullReasoning_NoChange()
    {
        var msg = new ChatMessage(ChatMessageRoles.Assistant, "reply");
        var messages = new List<ChatMessage> { msg };
        PatchReasoningContent(messages, null!);
        Assert.Null(messages[0].ReasoningContent);
    }

    [Fact]
    public void PatchReasoningContent_AssistantWithoutReasoning_GetsPatched()
    {
        var msg = new ChatMessage(ChatMessageRoles.Assistant, "reply");
        var messages = new List<ChatMessage> { msg };
        PatchReasoningContent(messages, "thinking...");
        Assert.Equal("thinking...", messages[0].ReasoningContent);
    }

    [Fact]
    public void PatchReasoningContent_AssistantWithReasoning_NotOverwritten()
    {
        var msg = new ChatMessage(ChatMessageRoles.Assistant, "reply")
        {
            ReasoningContent = "existing",
        };
        var messages = new List<ChatMessage> { msg };
        PatchReasoningContent(messages, "new thinking");
        Assert.Equal("existing", messages[0].ReasoningContent);
    }

    [Fact]
    public void PatchReasoningContent_OnlyFirstAssistantPatched()
    {
        var msg1 = new ChatMessage(ChatMessageRoles.Assistant, "reply1");
        var msg2 = new ChatMessage(ChatMessageRoles.Assistant, "reply2");
        var messages = new List<ChatMessage> { msg1, msg2 };
        PatchReasoningContent(messages, "thinking");
        Assert.Equal("thinking", messages[0].ReasoningContent);
        Assert.Null(messages[1].ReasoningContent);
    }

    [Fact]
    public void PatchReasoningContent_UserMessagesNotPatched()
    {
        var userMsg = new ChatMessage(ChatMessageRoles.User, "question");
        var assistantMsg = new ChatMessage(ChatMessageRoles.Assistant, "answer");
        var messages = new List<ChatMessage> { userMsg, assistantMsg };
        PatchReasoningContent(messages, "thinking");
        Assert.Null(messages[0].ReasoningContent);
        Assert.Equal("thinking", messages[1].ReasoningContent);
    }

    [Fact]
    public void PatchReasoningContent_EmptyMessages_NoThrow()
    {
        var messages = new List<ChatMessage>();
        PatchReasoningContent(messages, "thinking");
        Assert.Empty(messages);
    }

    // ══════════════════════════════════════════════════════
    //  BuildAssistantMessage — 通过反射访问私有静态方法
    // ══════════════════════════════════════════════════════

    private static ChatMessage BuildAssistantMessage(
        string text,
        string reasoning,
        List<(string Id, string Name, string Arguments)> toolCalls
    )
    {
        var method = typeof(ConversationLoop).GetMethod(
            "BuildAssistantMessage",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        var fullText = new StringBuilder(text);
        var reasoningText = new StringBuilder(reasoning);
        return (ChatMessage)method!.Invoke(null, [fullText, reasoningText, toolCalls])!;
    }

    [Fact]
    public void BuildAssistantMessage_TextOnly_AssistantRole()
    {
        var msg = BuildAssistantMessage("hello", "", []);
        Assert.Equal(ChatMessageRoles.Assistant, msg.Role);
        // Text is in Parts, not Content directly
        Assert.NotNull(msg.Parts);
        Assert.Contains(msg.Parts, p => p is { Type: ChatMessageTypes.Text, Text: "hello" });
    }

    [Fact]
    public void BuildAssistantMessage_ReasoningOnly_HasReasoningPart()
    {
        var msg = BuildAssistantMessage("", "thinking", []);
        Assert.NotNull(msg.Parts);
        Assert.Contains(
            msg.Parts,
            p => p is { Type: ChatMessageTypes.Reasoning, Reasoning.Content: "thinking" }
        );
    }

    [Fact]
    public void BuildAssistantMessage_TextAndReasoning_BothPresent()
    {
        var msg = BuildAssistantMessage("answer", "thinking", []);
        Assert.NotNull(msg.Parts);
        Assert.Contains(msg.Parts, p => p is { Type: ChatMessageTypes.Text, Text: "answer" });
        Assert.Contains(
            msg.Parts,
            p => p is { Type: ChatMessageTypes.Reasoning, Reasoning.Content: "thinking" }
        );
    }

    [Fact]
    public void BuildAssistantMessage_WithToolCalls_HasToolCalls()
    {
        var toolCalls = new List<(string Id, string Name, string Arguments)>
        {
            ("call-1", "tool_a", "{}"),
        };
        var msg = BuildAssistantMessage("", "", toolCalls);
        Assert.NotNull(msg.ToolCalls);
        Assert.Single(msg.ToolCalls);
        Assert.Equal("call-1", msg.ToolCalls[0].Id);
        Assert.Equal("tool_a", msg.ToolCalls[0].FunctionCall?.Name);
    }

    [Fact]
    public void BuildAssistantMessage_AllThree_Combined()
    {
        var toolCalls = new List<(string Id, string Name, string Arguments)>
        {
            ("call-1", "tool_a", "{\"key\":\"val\"}"),
        };
        var msg = BuildAssistantMessage("answer", "thinking", toolCalls);
        Assert.NotNull(msg.Parts);
        Assert.Contains(msg.Parts, p => p is { Type: ChatMessageTypes.Text, Text: "answer" });
        Assert.Contains(
            msg.Parts,
            p => p is { Type: ChatMessageTypes.Reasoning, Reasoning.Content: "thinking" }
        );
        Assert.NotNull(msg.ToolCalls);
        Assert.Single(msg.ToolCalls);
    }

    [Fact]
    public void BuildAssistantMessage_EmptyAll_StillAssistant()
    {
        var msg = BuildAssistantMessage("", "", []);
        Assert.Equal(ChatMessageRoles.Assistant, msg.Role);
    }

    [Fact]
    public void BuildAssistantMessage_MultipleToolCalls_AllPresent()
    {
        var toolCalls = new List<(string Id, string Name, string Arguments)>
        {
            ("call-1", "tool_a", "{}"),
            ("call-2", "tool_b", "{}"),
            ("call-3", "tool_c", "{}"),
        };
        var msg = BuildAssistantMessage("", "", toolCalls);
        Assert.Equal(3, msg.ToolCalls?.Count);
    }

    // ══════════════════════════════════════════════════════
    //  ComputeTurnIncrement — 稳定性监控增量 token 计算
    //  ══════════════════════════════════════════════════════
    //  回归防护：全量 prompt 重复累计曾导致 TotalTokens 虚高（8 轮 201K > 200K 预算），
    //  实际上下文仅 26.5K。增量 = 上下文增长 + 本轮输出。

    [Fact]
    public void ComputeTurnIncrement_FirstCallNoSeed_ReturnsFullPromptPlusCompletion()
    {
        // 新会话首次调用：TotalTokens 从 0 建立，增量 = 完整上下文 + 输出
        var result = ConversationLoop.ComputeTurnIncrement(
            lastPromptTokens: null,
            promptTokens: 22_693,
            completionTokens: 137,
            seeded: false
        );
        Assert.Equal(22_830, result);
    }

    [Fact]
    public void ComputeTurnIncrement_FirstCallWithSeed_ReturnsCompletionOnly()
    {
        // 会话恢复（seed 已代表调用前上下文）：首次增量 = 本轮输出
        var result = ConversationLoop.ComputeTurnIncrement(
            lastPromptTokens: null,
            promptTokens: 22_693,
            completionTokens: 137,
            seeded: true
        );
        Assert.Equal(137, result);
    }

    [Fact]
    public void ComputeTurnIncrement_SubsequentCall_ReturnsPromptDeltaPlusCompletion()
    {
        // 后续调用：prompt 从 22,693 增长到 24,004，加本轮输出 98
        var result = ConversationLoop.ComputeTurnIncrement(
            lastPromptTokens: 22_693,
            promptTokens: 24_004,
            completionTokens: 98,
            seeded: true
        );
        Assert.Equal(1_409, result);
    }

    [Fact]
    public void ComputeTurnIncrement_CompactShrink_AllowsNegative()
    {
        // compact 后 prompt 缩小：增量为负，TotalTokens 自然回落（增长检测对负值安全）
        var result = ConversationLoop.ComputeTurnIncrement(
            lastPromptTokens: 26_533,
            promptTokens: 20_000,
            completionTokens: 1_200,
            seeded: true
        );
        Assert.Equal(-5_333, result);
    }

    [Fact]
    public void ComputeTurnIncrement_NoDoubleCounting_ReplayOfSessionStaysUnderBudget()
    {
        // 回归防护：复现真实会话的 8 次调用序列，验证累计不再虚高撞破 200K 预算
        // （bug 复现：全量累计 174,684 + 26.7K ≈ 201K ≥ 200K 触发 BudgetExhausted；
        //  增量口径累计应 ≈ 当前上下文 ~27K）
        var calls = new (int Prompt, int Completion)[]
        {
            (22_693, 137), // call1
            (24_004, 98), // call2
            (24_100, 101), // call3
            (25_281, 300), // call4
            (26_327, 200), // call5
            (26_117, 127), // call6
            (26_269, 264), // call7
            (26_600, 500), // call8（溢出轮）
        };

        long total = 0;
        int? lastPrompt = null;
        foreach (var (prompt, completion) in calls)
        {
            total += ConversationLoop.ComputeTurnIncrement(
                lastPrompt,
                prompt,
                completion,
                seeded: total > 0
            );
            lastPrompt = prompt;
        }

        // 增量口径：TotalTokens ≈ 首次全量 + 后续增量 ≈ 当前上下文 + 累计输出，远低于预算
        Assert.True(total < 40_000, $"增量口径累计应 ≈ 当前上下文，实际 {total}");
        Assert.True(total >= 26_000, $"增量口径累计应接近真实上下文 ~27K，实际 {total}");
    }

    // ══════════════════════════════════════════════════════
    //  PartitionByExecutionMode — 混合调度分组（写入串行 / 读取并行）
    // ══════════════════════════════════════════════════════

    [Fact]
    public void PartitionByExecutionMode_SeparatesSequentialAndParallel()
    {
        var registry = new ToolRegistry();
        registry.Register(new ModeTool("write_a", ToolExecutionMode.Sequential), "p");
        registry.Register(new ModeTool("write_b", ToolExecutionMode.Sequential), "p");
        registry.Register(new ModeTool("read_a", ToolExecutionMode.Parallel), "p");

        var calls = new List<FakeCall> { new("read_a"), new("write_a"), new("write_b") };

        var (sequential, parallel) = ConversationLoop.PartitionByExecutionMode(
            calls,
            c => registry.TryGet(c.Name)
        );

        Assert.Equal(["write_a", "write_b"], sequential.Select(c => c.Name));
        Assert.Equal(["read_a"], parallel.Select(c => c.Name));
    }

    [Fact]
    public void PartitionByExecutionMode_UnknownTool_FallsBackToParallel()
    {
        var registry = new ToolRegistry();
        registry.Register(new ModeTool("write_a", ToolExecutionMode.Sequential), "p");

        var calls = new List<FakeCall> { new("missing"), new("write_a") };

        var (sequential, parallel) = ConversationLoop.PartitionByExecutionMode(
            calls,
            c => registry.TryGet(c.Name)
        );

        Assert.Equal(["write_a"], sequential.Select(c => c.Name));
        Assert.Equal(["missing"], parallel.Select(c => c.Name));
    }

    [Fact]
    public void PartitionByExecutionMode_PreservesSequentialOrder()
    {
        var registry = new ToolRegistry();
        registry.Register(new ModeTool("s1", ToolExecutionMode.Sequential), "p");
        registry.Register(new ModeTool("s2", ToolExecutionMode.Sequential), "p");

        var calls = new List<FakeCall> { new("s2"), new("s1") };

        var (sequential, _) = ConversationLoop.PartitionByExecutionMode(
            calls,
            c => registry.TryGet(c.Name)
        );

        Assert.Equal(["s2", "s1"], sequential.Select(c => c.Name));
    }

    // ══════════════════════════════════════════════════════
    //  InjectRefTags — B3 ref 稳定（EntryId 锚定）
    // ══════════════════════════════════════════════════════

    [Fact]
    /// <summary>
    /// B3：相同 EntryId 集合两次注入（模拟压缩后保留消息重建）→ 相同 ref 编号。
    /// 验证跨压缩 ref 稳定（EntryId 持久化不变）。
    /// </summary>
    public void InjectRefTags_SameEntryIds_RefsStableAcrossInvocations()
    {
        var refByEntryId = new Dictionary<string, int>();
        var nextRef = 1;
        Func<ChatMessage, string?> provider = m => m.NativeObject as string;

        // 第一次注入：两条消息（模拟压缩前）
        var m1 = new ChatMessage(ChatMessageRoles.User, "消息一") { NativeObject = "eid1" };
        var m2 = new ChatMessage(ChatMessageRoles.User, "消息二") { NativeObject = "eid2" };
        var tags1 = ConversationLoop.InjectRefTags([m1, m2], provider, refByEntryId, ref nextRef);

        Assert.Equal(2, tags1.Count);
        Assert.StartsWith("[m0001]", m1.Content);
        Assert.StartsWith("[m0002]", m2.Content);

        // 还原 Content（模拟 StreamResponseRich 后的 finally 还原）
        foreach (var (msg, original) in tags1)
            msg.Content = original;

        // 第二次注入（模拟压缩后保留消息重建，EntryId 不变）→ ref 稳定
        var m1b = new ChatMessage(ChatMessageRoles.User, "消息一") { NativeObject = "eid1" };
        var m2b = new ChatMessage(ChatMessageRoles.User, "消息二") { NativeObject = "eid2" };
        var tags2 = ConversationLoop.InjectRefTags([m1b, m2b], provider, refByEntryId, ref nextRef);

        Assert.StartsWith("[m0001]", m1b.Content); // 同一 EntryId → 同一 ref
        Assert.StartsWith("[m0002]", m2b.Content);
        Assert.Equal(3, nextRef); // 未分配新号（仍是第一次注入后的下一个号）
    }

    [Fact]
    /// <summary>
    /// B3：新消息（新 EntryId）分配递增 ref，不回收旧号；无 EntryId 消息跳过。
    /// </summary>
    public void InjectRefTags_NewEntryId_AllocatesNextRef_SkipsNoEntryId()
    {
        var refByEntryId = new Dictionary<string, int>();
        var nextRef = 1;
        Func<ChatMessage, string?> provider = m => m.NativeObject as string;

        var old = new ChatMessage(ChatMessageRoles.User, "旧") { NativeObject = "eid_old" };
        ConversationLoop.InjectRefTags([old], provider, refByEntryId, ref nextRef);
        Assert.StartsWith("[m0001]", old.Content);
        old.Content = "旧"; // 还原

        // 新消息（有 EntryId）→ m0002；无 EntryId 消息 → 跳过
        var fresh = new ChatMessage(ChatMessageRoles.User, "新") { NativeObject = "eid_new" };
        var noId = new ChatMessage(ChatMessageRoles.User, "无ID");
        var injected = ConversationLoop.InjectRefTags(
            [fresh, noId],
            provider,
            refByEntryId,
            ref nextRef
        );

        Assert.Single(injected); // 只有 fresh 被注入
        Assert.StartsWith("[m0002]", fresh.Content);
        Assert.Equal("无ID", noId.Content); // 未注入
        Assert.Equal(3, nextRef);
    }

    [Fact]
    /// <summary>
    /// B3：摘要消息（[上下文摘要 前缀）跳过；已注入标签（[m 前缀）不重复注入。
    /// </summary>
    public void InjectRefTags_SkipsSummaryAndAlreadyTagged()
    {
        var refByEntryId = new Dictionary<string, int>();
        var nextRef = 1;
        Func<ChatMessage, string?> provider = m => m.NativeObject as string;

        var normal = new ChatMessage(ChatMessageRoles.User, "正常消息") { NativeObject = "eid1" };
        var summary = new ChatMessage(ChatMessageRoles.User, "[上下文摘要 b1]\n摘要内容")
        {
            NativeObject = null,
        };
        var alreadyTagged = new ChatMessage(ChatMessageRoles.User, "[m0042] 已有标签")
        {
            NativeObject = "eid2",
        };
        var assistant = new ChatMessage(ChatMessageRoles.Assistant, "助手回复");

        var injected = ConversationLoop.InjectRefTags(
            [normal, summary, alreadyTagged, assistant],
            provider,
            refByEntryId,
            ref nextRef
        );

        Assert.Single(injected); // 只有 normal
        Assert.StartsWith("[m0001]", normal.Content);
    }

    [Fact]
    /// <summary>
    /// B3：无 provider（默认）时按序编号，每轮从 0 重新分配（退化行为）。
    /// </summary>
    public void InjectRefTags_NoProvider_SequentialFromZero()
    {
        var refByEntryId = new Dictionary<string, int>();
        var nextRef = 0; // 无 provider 时 nextRef 无意义

        var a = new ChatMessage(ChatMessageRoles.User, "甲");
        var b = new ChatMessage(ChatMessageRoles.User, "乙");
        var injected = ConversationLoop.InjectRefTags([a, b], null, refByEntryId, ref nextRef);

        Assert.Equal(2, injected.Count);
        Assert.StartsWith("[m0000]", a.Content);
        Assert.StartsWith("[m0001]", b.Content);
    }

    // ── 辅助类：最小 AgentTool / 调用记录实现用于测试 ──

    private sealed record FakeCall(string Name);

    // ══════════════════════════════════════════════════════
    //  TruncateHeadTail — P1 头尾截断纯函数（maxChars 动态化）
    // ══════════════════════════════════════════════════════

    private static string TruncateHeadTail(string text, int maxChars)
    {
        var method = typeof(ConversationLoop).GetMethod(
            "TruncateHeadTail",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        return (string)method!.Invoke(null, [text, maxChars])!;
    }

    [Fact]
    public void TruncateToolResult_ShortText_Unchanged()
    {
        const string text = "成功：角色已更新";
        Assert.Equal(text, TruncateHeadTail(text, 10_000));
    }

    [Fact]
    public void TruncateToolResult_LongText_KeepsHeadTailWithMarker()
    {
        var text = new string('甲', 12_000);
        var result = TruncateHeadTail(text, 10_000);

        Assert.StartsWith("Total output lines: 1", result);
        Assert.Contains("…[省略", result);
        Assert.EndsWith(new string('甲', 4_000), result);
        Assert.True(result.Length < text.Length, "截断后必须短于原文");
        // 头 4000 字完整保留（maxChars=10_000 → headTail=4_000）
        Assert.Contains(new string('甲', 4_000) + "\n…[省略", result);
    }

    [Fact]
    public void TruncateToolResult_Deterministic()
    {
        var text = new string('乙', 12_000);
        Assert.Equal(TruncateHeadTail(text, 10_000), TruncateHeadTail(text, 10_000));
    }

    [Fact]
    public void TruncateHeadTail_LargerMaxChars_KeepsMoreContent()
    {
        // P1：maxChars 动态化——更大窗口保留更多内容（headTail = maxChars*2/5）
        var text = new string('丙', 50_000);
        var small = TruncateHeadTail(text, 10_000); // headTail = 4_000
        var large = TruncateHeadTail(text, 40_000); // headTail = 16_000
        Assert.Contains(new string('丙', 4_000) + "\n…[省略", small);
        Assert.Contains(new string('丙', 16_000) + "\n…[省略", large);
        Assert.True(large.Length > small.Length);
    }

    [Fact]
    public void TruncateHeadTail_ExactBoundary_NoTruncation()
    {
        // text.Length == maxChars 时不截断
        var text = new string('丁', 10_000);
        Assert.Equal(text, TruncateHeadTail(text, 10_000));
    }

    private sealed class ModeTool(string name, ToolExecutionMode mode) : AgentTool
    {
        public override string Name => name;
        public override string Label => name;
        public override string Description => "";
        public override JsonElement? Parameters => null;
        public override ToolExecutionMode ExecutionMode => mode;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        )
        {
            return Task.FromResult<AgentToolResult>(
                new AgentToolResult { Content = [new TextContent("ok")] }
            );
        }
    }
}

/// <summary>
/// IsTransientNetworkError 回归测试。
/// 背景：网络断连（远端强制关闭连接）抛 IOException/HttpIOException，
/// 若未识别为可恢复错误并 emit AgentError，UI spinner 永远不停止。
/// 此测试锁定"哪些异常算瞬态网络错误"的判定。
/// </summary>
public sealed class TransientNetworkErrorTests
{
    private static readonly MethodInfo IsTransientNetworkError = typeof(ConversationLoop).GetMethod(
        "IsTransientNetworkError",
        BindingFlags.NonPublic | BindingFlags.Static
    )!;

    private static bool Invoke(Exception ex) => (bool)IsTransientNetworkError.Invoke(null, [ex])!;

    [Fact]
    public void IOException_Direct_IsTransient()
    {
        // 会话日志根因：远程主机强迫关闭连接（HttpIOException : IOException）
        Assert.True(Invoke(new IOException("Unable to read data from the transport connection")));
    }

    [Fact]
    public void HttpIOException_Direct_IsTransient()
    {
        // HttpIOException : IOException（.NET 6+ 网络流异常）
        var io = new HttpIOException(
            HttpRequestError.Unknown,
            "Unable to read data from the transport connection",
            innerException: null
        );
        Assert.True(Invoke(io));
    }

    [Fact]
    public void HttpRequestException_NoStatusCode_WrappedIOException_IsTransient()
    {
        // .NET HttpClient 网络断连的典型形态：HttpRequestException(StatusCode=null)
        // 包装 IOException 作为 InnerException
        var ex = new HttpRequestException(
            "Unable to read data from the transport connection",
            new IOException("远程主机强迫关闭了一个现有的连接"),
            statusCode: null
        );
        Assert.True(Invoke(ex));
    }

    [Fact]
    public void JsonException_IsNotTransient()
    {
        Assert.False(Invoke(new JsonException("bad json")));
    }

    [Fact]
    public void HttpRequestException_With404_IsNotTransient()
    {
        // 有 HTTP 状态码的请求失败不属于"瞬态网络错误"（4xx 是业务拒绝）
        var ex = new HttpRequestException(
            "Not Found",
            null,
            statusCode: System.Net.HttpStatusCode.NotFound
        );
        Assert.False(Invoke(ex));
    }
}

/// <summary>
/// VSA 漂移检测采样窗口测试（SampleCurrentSegmentAssistantText）。
/// 背景：漂移检测曾全量取"最近 N 条助手消息"，把已被用户回答过的过期片段
/// （如澄清问题）也算进当前上下文，导致合法等待输入被误判为偏离并注入突兀的系统提醒。
/// 结构修复：窗口 = 最近一条用户消息之后的助手内容（当前任务段）。
/// 此测试锁定窗口切分、上限与顺序语义。
/// </summary>
public sealed class DriftSamplingWindowTests
{
    [Fact]
    public void SegmentSinceLastUser_ExcludesStalePreUserContent()
    {
        // 复刻真实案例：用户提问 → 助手澄清问题（被用户回答后成为过期片段）
        // → 用户重申"主要是第一章和第二章" → 助手连续工具调用（空 content）
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.User, "第一章第二章AI味太浓，审计！"),
            new(ChatMessageRoles.Assistant, "请问你指的是哪两章？1. ch001/002 2. 其他？"),
            new(ChatMessageRoles.User, "主要是第一章和第二章！！！"),
            new(ChatMessageRoles.Assistant, ""), // 工具调用：read（空内容）
            new(ChatMessageRoles.Tool, "ch001 73字"),
            new(ChatMessageRoles.Assistant, ""), // 工具调用：decompress（空内容）
            new(ChatMessageRoles.Tool, "当前没有可恢复的压缩块"),
        };

        var window = ConversationLoop.SampleCurrentSegmentAssistantText(messages, maxCount: 8);

        // 澄清问题已被用户回答 → 过期，不参与漂移度量；当前段只有空内容工具调用 → 窗口为空
        Assert.Empty(window);
    }

    [Fact]
    public void SegmentSinceLastUser_KeepsOnlyCurrentSegment_NewestLast()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.User, "第一轮指令"),
            new(ChatMessageRoles.Assistant, "第一轮汇报（过期片段）"),
            new(ChatMessageRoles.User, "第二轮指令"),
            new(ChatMessageRoles.Assistant, "正在执行第二轮"),
            new(ChatMessageRoles.Tool, "工具结果"),
            new(ChatMessageRoles.Assistant, "第二轮完成汇报"),
        };

        var window = ConversationLoop.SampleCurrentSegmentAssistantText(messages, maxCount: 8);

        Assert.Equal(["正在执行第二轮", "第二轮完成汇报"], window);
    }

    [Fact]
    public void MaxCount_CapsWindow_KeepsNewest()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.User, "指令"),
            new(ChatMessageRoles.Assistant, "a1"),
            new(ChatMessageRoles.Assistant, "a2"),
            new(ChatMessageRoles.Assistant, "a3"),
            new(ChatMessageRoles.Assistant, "a4"),
            new(ChatMessageRoles.Assistant, "a5"),
        };

        var window = ConversationLoop.SampleCurrentSegmentAssistantText(messages, maxCount: 3);

        Assert.Equal(["a3", "a4", "a5"], window);
    }

    [Fact]
    public void NoUserMessage_ReturnsAllAssistantContent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.Assistant, "开场白"),
            new(ChatMessageRoles.Tool, "中间工具"),
            new(ChatMessageRoles.Assistant, "继续"),
        };

        var window = ConversationLoop.SampleCurrentSegmentAssistantText(messages, maxCount: 8);

        Assert.Equal(["开场白", "继续"], window);
    }
}
