using System.Text.Json;
using Core.Agent;
using Core.Data;
using Core.Inference;
using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;
using ChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>
/// 循环判定（停止/漂移/稳定性复核）契约：
/// 命中阈值 → true；禁用/未启用/服务不可达/缺答案/低于阈值 → false（调用方保持既有行为）；
/// 判定请求的状态形状（键名与截断）与循环侧的注入/计数/每 run 上限。
/// </summary>
public sealed class LoopJudgmentTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static ConversationLoop CreateLoop(ISystemOneClient? systemOne) =>
        new(
            api: new LlmTornado.TornadoApi([
                new ProviderAuthentication(LLmProviders.OpenAi, "test-key"),
            ]),
            modelName: "gpt-test",
            systemPrompt: "system",
            thinkingLevel: "medium",
            toolRegistry: new ToolRegistry(),
            toolExecution: ToolExecutionMode.Parallel,
            systemOne: systemOne
        );

    private static SystemOneAnswer Noul(double p) => new() { Type = "noul", Noul = p };

    private static SystemOneAnswer ChoiceAnswer(string choice, double p) =>
        new()
        {
            Type = "choice",
            Choice = choice,
            Probabilities = new Dictionary<string, double>(StringComparer.Ordinal) { [choice] = p },
        };

    private static JsonElement Serialize(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    // ═══════════════════════════════════════════════════════════
    //  停止复核
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ShouldContinue_IncompleteAboveThreshold_ReturnsTrue_AndCarriesReply()
    {
        var stub = new StubJudge(_ => ChoiceAnswer("incomplete", 0.9));

        var hit = await ConversationLoop.ShouldContinueAsync(
            stub,
            "接下来我会把第三章改写完。",
            Ct
        );

        Assert.True(hit);
        Assert.Equal(1, stub.CallCount);
        var state = Serialize(stub.LastState);
        Assert.Equal("接下来我会把第三章改写完。", state.GetProperty("reply").GetString());
        var question = Assert.IsType<ChoiceQuestion>(stub.LastQuestions!["verdict"]);
        Assert.Contains("incomplete", question.Criteria.Keys);
    }

    [Fact]
    public async Task ShouldContinue_BelowThreshold_OrAwaitingUser_ReturnsFalse()
    {
        var low = new StubJudge(_ => ChoiceAnswer("incomplete", 0.7)); // < 0.8
        Assert.False(await ConversationLoop.ShouldContinueAsync(low, "接下来我会继续。", Ct));

        var awaiting = new StubJudge(_ => ChoiceAnswer("awaiting-user", 0.95));
        Assert.False(
            await ConversationLoop.ShouldContinueAsync(awaiting, "已按你的要求改完，要继续吗？", Ct)
        );

        var blank = new StubJudge(_ => ChoiceAnswer("incomplete", 0.99));
        Assert.False(await ConversationLoop.ShouldContinueAsync(blank, "   ", Ct));
        Assert.Equal(0, blank.CallCount); // 空输入不发请求
    }

    [Fact]
    public async Task ShouldContinue_ServiceUnavailable_ReturnsFalse()
    {
        var stub = new StubJudge(_ => ChoiceAnswer("incomplete", 0.99), fail: true);

        Assert.False(await ConversationLoop.ShouldContinueAsync(stub, "接下来我会继续。", Ct));
    }

    [Fact]
    public async Task ShouldContinue_Disabled_MakesNoRequest()
    {
        var stub = new StubJudge(_ => ChoiceAnswer("incomplete", 0.99), enabled: false);

        Assert.False(await ConversationLoop.ShouldContinueAsync(stub, "接下来我会继续。", Ct));
        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task ShouldContinue_StopCheckDisabled_MakesNoRequest()
    {
        var stub = new StubJudge(
            _ => ChoiceAnswer("incomplete", 0.99),
            loop: new SystemOneLoopConfig
            {
                Enabled = true,
                StopCheck = new SystemOneStopCheckConfig { Enabled = false },
            }
        );

        Assert.False(await ConversationLoop.ShouldContinueAsync(stub, "接下来我会继续。", Ct));
        Assert.Equal(0, stub.CallCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  漂移复核
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Drift_AboveThreshold_ReturnsTrue_AndTruncatesState()
    {
        var stub = new StubJudge(_ => Noul(0.7));
        var longRecent = new string('甲', ConversationLoop.JudgeRoleChars * 4);

        Assert.True(
            await ConversationLoop.DriftLooksOnTaskAsync(stub, "角色提示词", longRecent, Ct)
        );

        var state = Serialize(stub.LastState);
        Assert.Equal("角色提示词", state.GetProperty("role").GetString());
        // 默认 loop.text-chars = 1200：超长输入必须被截断（避免击穿 state 预算）
        Assert.Equal(1200, state.GetProperty("recent").GetString()!.Length);
    }

    [Fact]
    public async Task Drift_BelowThreshold_ReturnsFalse()
    {
        var stub = new StubJudge(_ => Noul(0.5)); // < 0.6

        Assert.False(
            await ConversationLoop.DriftLooksOnTaskAsync(stub, "角色提示词", "最近输出", Ct)
        );
    }

    // ═══════════════════════════════════════════════════════════
    //  稳定性复核
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Progress_AboveThreshold_ExposesProbability_AndDigestShape()
    {
        var stub = new StubJudge(_ => Noul(0.9));
        var calls = new List<string> { "write_chapter 第3章", "read_chapter 第3章" };

        var probability = await ConversationLoop.ProgressHitAsync(stub, "把第三章改完", calls, Ct);

        Assert.Equal(0.9, probability!.Value, 3); // 非空 = 判定「仍在实质推进」（阈值判定在方法内）

        var state = Serialize(stub.LastState);
        Assert.Equal("把第三章改完", state.GetProperty("task").GetString());
        Assert.Equal(2, state.GetProperty("calls").GetArrayLength());
        Assert.Equal("write_chapter 第3章", state.GetProperty("calls")[0].GetString()); // 最近在前
    }

    [Fact]
    public async Task Progress_BelowThreshold_OrEmptyCalls_ReturnsNull()
    {
        var stub = new StubJudge(_ => Noul(0.7)); // < 0.75
        Assert.Null(await ConversationLoop.ProgressHitAsync(stub, "任务", ["write_chapter"], Ct));

        var noCalls = new StubJudge(_ => Noul(0.99));
        Assert.Null(await ConversationLoop.ProgressHitAsync(noCalls, "任务", [], Ct));
        Assert.Equal(0, noCalls.CallCount); // 空历史不发请求
    }

    [Fact]
    public void BuildToolDigest_TruncatesArguments_AndKeepsRecentFirst()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(8);
        history.Add(new ToolDispatcher.ToolCallRecord("first", "a"));
        history.Add(
            new ToolDispatcher.ToolCallRecord(
                "second",
                new string('乙', ConversationLoop.JudgeCallArgsChars * 2)
            )
        );

        var recent = history.GetRecent(6);
        var lines = ConversationLoop.BuildToolDigest(recent, 6);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("second ", lines[0]); // 最近在前
        Assert.Equal("second ".Length + ConversationLoop.JudgeCallArgsChars, lines[0].Length);
        Assert.Equal("first a", lines[1]);
    }

    [Fact]
    public void LastAssistantText_TakesLastNonEmptyAssistantMessage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.User, "任务"),
            new(ChatMessageRoles.Assistant, "第一次回复"),
            new(ChatMessageRoles.Tool, ""),
            new(ChatMessageRoles.User, "继续"),
            new(ChatMessageRoles.Assistant, "第二次回复"),
        };

        Assert.Equal("第二次回复", ConversationLoop.LastAssistantText(messages));
        Assert.Null(
            ConversationLoop.LastAssistantText([new ChatMessage(ChatMessageRoles.User, "只有用户")])
        );
    }

    // ═══════════════════════════════════════════════════════════
    //  循环侧接线（注入 / 计数 / 每 run 上限 / 失败不改行为）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task JudgeContinue_InjectsNoticeOncePerRun()
    {
        var stub = new StubJudge(_ => ChoiceAnswer("incomplete", 0.9));
        var loop = CreateLoop(stub);
        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "把第三章改完")]);
        var before = loop.PersistedMessages.Count;

        var hit = await loop.JudgeContinueAsync("接下来我会把第三章改写完。", [], Ct);

        Assert.True(hit);
        Assert.Equal(before + 1, loop.PersistedMessages.Count);
        // 注入走仓库既有 parts 范式（与 VSA/稳定性提醒同形状）：文本在 Parts[0]，Content 为空
        var injected = loop.PersistedMessages[^1];
        Assert.Equal(ChatMessageRoles.Assistant, injected.Role);
        Assert.Equal(ChatMessageTypes.Text, injected.Parts?[0].Type);
        Assert.Contains("系统提醒", injected.Parts?[0].Text ?? "");

        // MaxPerRun 默认 1：第二次不再触发（也不发请求）
        Assert.False(await loop.JudgeContinueAsync("接下来我会把第三章改写完。", [], Ct));
        Assert.Equal(1, stub.CallCount);
        Assert.Equal(before + 1, loop.PersistedMessages.Count);
    }

    [Fact]
    public async Task JudgeContinue_ServiceUnavailable_DoesNotInject()
    {
        var stub = new StubJudge(_ => ChoiceAnswer("incomplete", 0.99), fail: true);
        var loop = CreateLoop(stub);
        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "任务")]);
        var before = loop.PersistedMessages.Count;

        Assert.False(await loop.JudgeContinueAsync("接下来我会继续。", [], Ct));
        Assert.Equal(before, loop.PersistedMessages.Count);
    }

    [Fact]
    public async Task Judges_WithoutClient_AreNoOps()
    {
        var loop = CreateLoop(null);
        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "任务")]);
        var before = loop.PersistedMessages.Count;
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(4);
        history.Add(new ToolDispatcher.ToolCallRecord("write_chapter", "第3章"));

        Assert.False(await loop.JudgeContinueAsync("接下来我会继续。", [], Ct));
        Assert.False(await loop.JudgeDriftVetoAsync(0.01, "最近输出", Ct));
        Assert.False(await loop.JudgeStabilityOverrideAsync(history, Ct));
        Assert.Equal(before, loop.PersistedMessages.Count);
    }

    [Fact]
    public async Task JudgeDriftVeto_AboveThreshold_Suppresses_BelowKeepsBehavior()
    {
        var suppressing = new StubJudge(_ => Noul(0.7));
        var loop = CreateLoop(suppressing);
        Assert.True(await loop.JudgeDriftVetoAsync(0.0123, "最近输出", Ct));

        var keeps = new StubJudge(_ => Noul(0.55)); // < 0.6
        var loop2 = CreateLoop(keeps);
        Assert.False(await loop2.JudgeDriftVetoAsync(0.0123, "最近输出", Ct));
    }

    [Fact]
    public async Task JudgeStabilityOverride_HitsOnce_AndRequiresToolCalls()
    {
        var stub = new StubJudge(_ => Noul(0.9));
        var loop = CreateLoop(stub);
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(8);
        history.Add(new ToolDispatcher.ToolCallRecord("write_chapter", "第3章"));
        history.Add(new ToolDispatcher.ToolCallRecord("read_chapter", "第3章"));

        Assert.True(await loop.JudgeStabilityOverrideAsync(history, Ct));
        // 每 run 至多一次（第二次不再发请求）
        Assert.False(await loop.JudgeStabilityOverrideAsync(history, Ct));
        Assert.Equal(1, stub.CallCount);

        // 空历史：不判定
        var loop2 = CreateLoop(new StubJudge(_ => Noul(0.9)));
        Assert.False(
            await loop2.JudgeStabilityOverrideAsync(
                new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(4),
                Ct
            )
        );
    }

    [Fact]
    public async Task Judges_LoopDisabled_MakeNoRequests()
    {
        var stub = new StubJudge(
            _ => Noul(0.99),
            loop: new SystemOneLoopConfig { Enabled = false }
        );
        var loop = CreateLoop(stub);
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(4);
        history.Add(new ToolDispatcher.ToolCallRecord("write_chapter", "第3章"));

        Assert.False(await loop.JudgeContinueAsync("接下来我会继续。", [], Ct));
        Assert.False(await loop.JudgeDriftVetoAsync(0.01, "最近输出", Ct));
        Assert.False(await loop.JudgeStabilityOverrideAsync(history, Ct));
        Assert.Equal(0, stub.CallCount);
    }

    /// <summary>
    /// 判定桩：按题号给答案（noul/choice）；fail=true 模拟服务不可用（返回 null）；
    /// 记录调用次数、最后一次 state/questions 供形状断言。
    /// </summary>
    private sealed class StubJudge : ISystemOneClient
    {
        private readonly Func<string, SystemOneAnswer?> _answer;
        private readonly bool _fail;

        public StubJudge(
            Func<string, SystemOneAnswer?> answer,
            bool enabled = true,
            SystemOneLoopConfig? loop = null,
            bool fail = false
        )
        {
            _answer = answer;
            _fail = fail;
            Options = new SystemOneConfig
            {
                Enabled = enabled,
                Loop = loop ?? new SystemOneLoopConfig { Enabled = true },
            };
        }

        public int CallCount { get; private set; }

        public object? LastState { get; private set; }

        public IReadOnlyDictionary<string, SystemOneQuestion>? LastQuestions { get; private set; }

        public bool Enabled => Options.Enabled;

        public SystemOneConfig Options { get; private set; }

        public Task<SystemOneResponse?> AskAsync(
            object state,
            IReadOnlyDictionary<string, SystemOneQuestion> questions,
            CancellationToken ct = default
        )
        {
            CallCount++;
            LastState = state;
            LastQuestions = questions;
            if (_fail)
                return Task.FromResult<SystemOneResponse?>(null);

            var answers = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
            foreach (var key in questions.Keys)
            {
                if (_answer(key) is { } value)
                    answers[key] = value;
            }
            return Task.FromResult<SystemOneResponse?>(
                new SystemOneResponse { Model = "stub", Answers = answers }
            );
        }

        public void UpdateConfig(SystemOneConfig options) => Options = options;
    }
}
