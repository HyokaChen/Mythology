using System.Reflection;
using System.Text.Json;
using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Desktop.Controls;
using Desktop.Models;
using Desktop.Services;
using Desktop.ViewModels;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI.Primitives;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// ConversationViewModel 命令/排队的可观察契约（含最小依赖装配：
/// 单例 Agent + DesktopAgentBridge + SessionEngine(临时目录)，不触网、不触碰真实 ~/.mythology）。
/// </summary>
public sealed class ConversationVmContractTests
{
    /// <summary>
    /// 变体 refinement 排队：Agent 处理中提交修改要求时，必须投递 Core SteeringQueue
    /// （turn 边界消费 → SteerPromoted/MessageStart 渲染 user 气泡），而不是被 IsProcessing
    /// 提前 return 静默丢弃，也不是并发 RunPromptAsync（单例 Agent 下不安全）。
    /// </summary>
    [Fact]
    public async Task Refinement_WhileProcessing_IsQueued_NotDropped()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        vm.IsProcessing = true; // 模拟 Agent 正在写作
        vm.BeginVariantRefinement(Explore(), RefineDecision());
        vm.InputText = "结局再克制一点";

        vm.SendMessageCommand.Execute().Subscribe();
        await Task.Delay(200);

        var (message, steerId) = Assert.Single(h.Agent.SteeringQueue.DequeueAllWithIds());
        Assert.Equal(ChatMessageRoles.User, message.Role);
        Assert.False(string.IsNullOrEmpty(steerId));
        // 五段式 prompt（预填前缀已剥离）
        Assert.Contains("===== 用户修改要求 =====", message.Content);
        Assert.Contains("结局再克制一点", message.Content);
        Assert.DoesNotContain("修改「悲剧走向」版本：", message.Content);
        // 暂存已消费，不会二次投递
        Assert.False(vm.HasPendingRefinement);
    }

    [Fact]
    public void EntityChangeNotice_WhileProcessing_InterleavesWithToolSteps()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        var agent = AttachAgentMessage(vm, processing: true);
        vm.Messages.Add(agent);

        var firstTool = ToolStep("情节线管理 A");
        agent.AgentSteps.Add(firstTool);
        vm.AddEntityChangeNotice("📝 修改情节线「卷一」", fromToolCall: true);

        var secondTool = ToolStep("情节线管理 B");
        agent.AgentSteps.Add(secondTool);
        vm.AddEntityChangeNotice("📝 修改情节线「卷二」", fromToolCall: true);

        Assert.Collection(
            agent.AgentSteps,
            step => Assert.Same(firstTool, step),
            step => AssertNotice(step, "📝 修改情节线「卷一」"),
            step => Assert.Same(secondTool, step),
            step => AssertNotice(step, "📝 修改情节线「卷二」")
        );
        Assert.Single(vm.Messages);
        Assert.Same(agent, vm.Messages[0]);
    }

    [Fact]
    public void EntityChangeNotice_ManualDuringProcessing_AlsoInterleaves()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        var agent = AttachAgentMessage(vm, processing: true);

        vm.AddEntityChangeNotice("📝 手动修改角色「林月如」", fromToolCall: false);

        AssertNotice(Assert.Single(agent.AgentSteps), "📝 手动修改角色「林月如」");
        Assert.Empty(vm.Messages);
    }

    /// <summary>
    /// 压缩事件必须驱动同一行系统状态：开始 → spinner + 「压缩中…」，结束 → spinner 停、
    /// 文本改为结果。否则用户只能看到两条静态系统消息，不知道压缩还在跑。
    /// </summary>
    [Fact]
    public void CompactEvents_DriveLiveSystemRowAndSpinner()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        var states = new List<bool>();
        vm.CompactStateChanged += states.Add;

        var start = (Action)
            typeof(SessionEngine)
                .GetField("OnCompactStart", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(h.Engine)!;
        var end =
            (Action<string>)
                typeof(SessionEngine)
                    .GetField("OnCompactEnd", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(h.Engine)!;

        HeadlessUiHost.Instance.Run(() =>
        {
            start();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsCompacting);
            var message = Assert.Single(vm.Messages);
            Assert.Equal(MessageSender.System, message.Sender);
            Assert.Equal("上下文压缩中…", message.Content.ToString());
            Assert.True(message.IsSpinnerVisible);

            end("上下文压缩完成：202 → 20 条");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.False(vm.IsCompacting);
            Assert.Equal("上下文压缩完成：202 → 20 条", message.Content.ToString());
            Assert.False(message.IsSpinnerVisible);
        });

        Assert.Equal([true, false], states);
    }

    /// <summary>
    /// 跨线程事件顺序不保证：End 的 UI action 可能先于 Start 执行。
    /// 此时 Start 必须立即消费挂起的 End，不能留下永久旋转的「上下文压缩中…」。
    /// </summary>
    [Fact]
    public void CompactEnd_BeforeStart_StillClearsCompactingRow()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        var states = new List<bool>();
        vm.CompactStateChanged += states.Add;

        var start = (Action)
            typeof(SessionEngine)
                .GetField("OnCompactStart", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(h.Engine)!;
        var end =
            (Action<string>)
                typeof(SessionEngine)
                    .GetField("OnCompactEnd", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(h.Engine)!;

        HeadlessUiHost.Instance.Run(() =>
        {
            end("上下文压缩完成：202 → 20 条");
            start();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.False(vm.IsCompacting);
            var message = Assert.Single(vm.Messages);
            Assert.Equal("上下文压缩完成：202 → 20 条", message.Content.ToString());
            Assert.False(message.IsSpinnerVisible);
        });

        Assert.Equal([true, false], states);
    }

    /// <summary>
    /// 压缩 End 事件完全丢失时，Agent 开始/结束处理是兜底对账点：
    /// 引擎已不在压缩，就不能让「上下文压缩中…」继续占着系统行。
    /// </summary>
    [Fact]
    public void ProcessingChange_ClearsStaleCompactingRow()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        var start = (Action)
            typeof(SessionEngine)
                .GetField("OnCompactStart", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(h.Engine)!;

        HeadlessUiHost.Instance.Run(() =>
        {
            start();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsCompacting);

            vm.SetProcessing(true);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.False(vm.IsCompacting);
            var message = Assert.Single(vm.Messages);
            Assert.Equal("上下文压缩已结束", message.Content.ToString());
            Assert.False(message.IsSpinnerVisible);
        });
    }

    [Fact]
    public void EntityChangeNotice_WhileIdle_IsSystemRow()
    {
        using var h = new Harness();
        var vm = h.CreateVm();

        vm.AddEntityChangeNotice("📝 手动修改角色「林月如」", fromToolCall: false);

        var message = Assert.Single(vm.Messages);
        Assert.Equal(MessageSender.System, message.Sender);
        Assert.Equal("📝 手动修改角色「林月如」", message.Content.ToString());
        Assert.True(vm.HasMessages);
    }

    [Fact]
    public void EntityChangeNotice_LateToolEvent_IsDropped()
    {
        using var h = new Harness();
        var vm = h.CreateVm();

        // 工具步骤已经结束并展示过结果；迟到的写盘事件不再补到底部。
        vm.AddEntityChangeNotice("📝 修改情节线「卷二」", fromToolCall: true);

        Assert.Empty(vm.Messages);
    }

    /// <summary>
    /// /resume 路由契约（MainWindowViewModel 据此恢复指定小说最新会话）：
    /// 带参 → RequestResume(novelId)；缺参 → RequestResume(null)（由主窗口提示用法）。
    /// </summary>
    [Fact]
    public void Resume_Command_RaisesRoutingEvent_WithParsedNovelId()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        string? observed = "unset";
        var raised = 0;
        vm.RequestResume += id =>
        {
            observed = id;
            raised++;
        };

        vm.TryExecuteCommand("/resume nv-42");
        Assert.Equal(1, raised);
        Assert.Equal("nv-42", observed);

        vm.TryExecuteCommand("/resume");
        Assert.Equal(2, raised);
        Assert.Null(observed);
    }

    /// <summary>/new 路由契约：由宿主创建新 VM 并切换，不在当前 VM 内原地清空消息。</summary>
    [Fact]
    public void New_Command_RaisesHostNewConversationRequest()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        var raised = 0;
        vm.RequestNewConversation += () => raised++;

        Assert.True(vm.TryExecuteCommand("/new"));
        Assert.Equal(1, raised);
        Assert.Equal(0, vm.Messages.Count);
    }

    [Fact]
    public void Clear_Command_IsUnknown_AfterRemoval()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        var raised = 0;
        string? notice = null;
        vm.RequestNewConversation += () => raised++;
        vm.SystemMessageRequested += text => notice = text;

        Assert.True(vm.TryExecuteCommand("/clear"));
        Assert.Equal(0, raised);
        Assert.Contains("未知命令", notice);
    }

    [Fact]
    public void New_Command_RejectsArguments()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        var raised = 0;
        string? notice = null;
        vm.RequestNewConversation += () => raised++;
        vm.SystemMessageRequested += text => notice = text;

        Assert.True(vm.TryExecuteCommand("/new extra"));
        Assert.Equal(0, raised);
        Assert.Contains("不接受参数", notice);
    }

    /// <summary>
    /// 用户气泡先行契约：SendEnginePromptAsync 必须先插 user 气泡再插 Agent 占位（修复前占位先上屏），
    /// 且 Loop 对同一条消息的 MessageStart 回显被吞（一次性内容匹配），不同文本照常渲染。
    /// TornadoApi=null → Agent.Prompt 在 GetOrCreateConversationLoop 抛异常，事件由测试主动喂。
    /// </summary>
    [Fact]
    public async Task SendEnginePromptAsync_UserBubbleFirst_EchoSuppressed()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        vm.Engine.NewSession(); // 有 _sessionId，错误事件可落盘临时目录

        await vm.SendEnginePromptAsync("第一章大纲");

        // ★ 用户气泡先行：先 user 后 agent 占位（修复前 agent 占位先插入）
        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal(MessageSender.User, vm.Messages[0].Sender);
        Assert.Equal("第一章大纲", vm.Messages[0].Content.ToString());
        Assert.Equal(MessageSender.Agent, vm.Messages[1].Sender);

        // Loop 同文本回显被吞
        vm.HandleAgentEvent(
            new MessageStart(new LlmChatMessage(ChatMessageRoles.User, "第一章大纲"))
        );
        Assert.Equal(2, vm.Messages.Count);

        // 不同文本（steer/注入）正常渲染
        vm.HandleAgentEvent(
            new MessageStart(new LlmChatMessage(ChatMessageRoles.User, "注入提示"))
        );
        Assert.Equal(3, vm.Messages.Count);

        // 中断收尾：占位标记 IsInterrupted 后保留（中断提示独立成行，不再写进 Content）
        vm.HandleAgentEvent(new AgentInterrupted());
        Assert.Equal(3, vm.Messages.Count);

        // flag 已被 99 行回显消费：此后同文本属于真正的再次输入（如 steer），正常渲染
        vm.HandleAgentEvent(
            new MessageStart(new LlmChatMessage(ChatMessageRoles.User, "第一章大纲"))
        );
        Assert.Equal(4, vm.Messages.Count);

        // 第二次发送同文本：新 user+agent 占位插入（4→6）+ flag 重新覆写
        await vm.SendEnginePromptAsync("第一章大纲");
        Assert.Equal(6, vm.Messages.Count);
        vm.HandleAgentEvent(
            new MessageStart(new LlmChatMessage(ChatMessageRoles.User, "第一章大纲"))
        );
        Assert.Equal(6, vm.Messages.Count); // 新 flag 吞掉本轮回显
    }

    /// <summary>
    /// 对话内等待行：发送后立即可见（spinner + 执行中）；首个思考/正文 token 到达即收起，
    /// 回合结束（中断）后不再残留。诗词由左下角状态栏展示，不再进入等待行。
    /// </summary>
    [Fact]
    public async Task WaitingRow_ClosesOnFirstActivity()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        vm.Engine.NewSession();

        await vm.SendEnginePromptAsync("写一段雨中送别");

        Assert.Equal(2, vm.Messages.Count);
        var agent = vm.Messages[1];
        Assert.True(agent.IsWaiting);

        vm.SetProcessing(true);

        vm.HandleAgentEvent(new StreamChunk("先分析", "", StreamContentType.Thinking));
        Assert.False(agent.IsWaiting);
        var step = Assert.Single(agent.AgentSteps);
        Assert.True(step.HasThinking);
        Assert.Equal("先分析", step.ThinkingText);

        vm.HandleAgentEvent(new AgentInterrupted());
        Assert.False(agent.IsWaiting);
        Assert.True(agent.IsInterrupted);
        Assert.DoesNotContain("已中断", agent.Content.ToString());
        Assert.Equal(2, vm.Messages.Count);
    }

    /// <summary>
    /// 阶段边界必须重新亮起等待行：思考结束 / 工具结束 / 下一轮开始，
    /// 下一个可见活动（思考或正文 token、下一张工具卡）到达时立即收起。
    /// 修复前只有「回合首个活动」会收起等待行，思考结束后界面看起来像已经结束。
    /// </summary>
    [Fact]
    public async Task WaitingRow_ReappearsBetweenPhases_AndClosesOnNextActivity()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        vm.Engine.NewSession();

        await vm.SendEnginePromptAsync("写一段雨中送别");
        var agent = vm.Messages[^1];
        Assert.True(agent.IsWaiting);

        vm.SetProcessing(true);

        // 思考流：步骤自己有 spinner，等待行收起
        vm.HandleAgentEvent(new StreamChunk("先分析", "", StreamContentType.Thinking));
        Assert.False(agent.IsWaiting);
        var thinking = Assert.Single(agent.AgentSteps);
        Assert.True(thinking.IsProcessing);

        // 思考结束：等待行立即出现在已完成步骤的下方
        vm.HandleAgentEvent(new StreamChunk("", "", StreamContentType.ThinkingEnd));
        Assert.False(thinking.IsProcessing);
        Assert.True(agent.IsWaiting);
        var rows = ConversationRow.FromMessage(agent);
        Assert.Equal(ConversationRowKind.Step, rows[^2].Kind);
        Assert.Equal(ConversationRowKind.Waiting, rows[^1].Kind);

        // 工具开始：工具卡接管执行中状态
        vm.HandleAgentEvent(new ToolExecutionStart("t1", "search", "{}"));
        Assert.False(agent.IsWaiting);
        var tool = agent.AgentSteps[^1];
        Assert.True(tool.IsProcessing);

        // 工具结束：下一轮 LLM 还没出 token，重新亮起等待行
        vm.HandleAgentEvent(new ToolExecutionEnd("t1", false, "ok"));
        Assert.False(tool.IsProcessing);
        Assert.True(agent.IsWaiting);

        // 下一段正文出现：等待行收起
        vm.HandleAgentEvent(new StreamChunk("正文", "", StreamContentType.Text));
        Assert.False(agent.IsWaiting);
    }

    /// <summary>
    /// 工具参数流式阶段：思考结束后空白正文不能收起等待行；首个
    /// <see cref="ToolCallStreamStarted"/> 立即建 pending 卡并接管执行中状态，
    /// <see cref="ToolExecutionStart"/> 必须复用同一张卡，工具结束/流结束后
    /// 等待行重新亮起且不残留 processing 步骤。
    /// </summary>
    [Fact]
    public async Task WaitingRow_ShowsDuringToolArgumentStream_AndPendingCardIsReused()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        vm.Engine.NewSession();

        await vm.SendEnginePromptAsync("写一段雨中送别");
        var agent = vm.Messages[^1];
        vm.SetProcessing(true);

        vm.HandleAgentEvent(new StreamChunk("先分析", "", StreamContentType.Thinking));
        vm.HandleAgentEvent(new StreamChunk("", "", StreamContentType.ThinkingEnd));

        // 模型在工具参数前可能流空白正文；等待行必须保持可见。
        vm.HandleAgentEvent(new StreamChunk("\n\n", "", StreamContentType.Text));
        Assert.True(agent.IsWaiting);

        // 首个工具参数 delta：立即建 pending 卡，等待行收起。
        vm.HandleAgentEvent(new ToolCallStreamStarted("call_1", "edit_entities", 0));
        Assert.False(agent.IsWaiting);
        var tool = Assert.Single(agent.AgentSteps.Where(s => s.Kind == StepKind.ToolCall));
        Assert.True(tool.IsProcessing);
        Assert.Equal("正在生成参数…", tool.ArgumentsSummary);

        // 参数生成完，执行开始：原地复用，不新增第二张卡。
        var stepCount = agent.AgentSteps.Count;
        vm.HandleAgentEvent(
            new ToolExecutionStart("call_1", "edit_entities", """{"id":"林月如"}""")
            {
                ToolCallKey = "call_1",
            }
        );
        Assert.Equal(stepCount, agent.AgentSteps.Count);
        Assert.Same(tool, agent.AgentSteps[^1]);
        Assert.True(tool.IsProcessing);

        // 工具结束：等待行重新亮起。
        vm.HandleAgentEvent(new ToolExecutionEnd("call_1", false, "ok") { ToolCallKey = "call_1" });
        Assert.False(tool.IsProcessing);
        Assert.True(agent.IsWaiting);

        // LLM 流结束：等待下一轮 ReAct，不能留下 processing 步骤。
        vm.HandleAgentEvent(
            new LlmStreamFinished(ChatMessageFinishReasons.EndTurn, true, false, true)
        );
        Assert.True(agent.IsWaiting);
        Assert.DoesNotContain(agent.AgentSteps, s => s.IsProcessing);
    }

    /// <summary>
    /// 模型切换生效契约：设置保存后 Agent 模型名必须更新且冻结的 ConversationLoop 失效
    /// （readonly _api/_modelName 不失效则新模型永不生效），_messages 保留（上下文无损）。
    /// 预热线：空 TornadoApi + 预取消 token，取消发生在任何 LLM 调用前，无网络。
    /// </summary>
    [Fact]
    public async Task SettingsSave_InvalidatesFrozenLoop_NewModelTakesEffect()
    {
        using var h = new Harness();
        // 预热：造出已冻结的 ConversationLoop（取消在 LLM 调用前发生，无网络）
        h.Agent.TornadoApi = new TornadoApi();
        using var warmupCts = new CancellationTokenSource();
        warmupCts.Cancel();
        try
        {
            await h.Agent.Prompt("warmup", warmupCts.Token);
        }
        catch (OperationCanceledException) { }
        Assert.NotNull(h.Agent.UsageStats); // UsageStats 透传自 _conversationLoop → loop 存在
        Assert.Single(h.Agent.State.Messages); // warmup user 消息已同步

        await h.ConfigStore.SaveAsync(
            new ConfigData
            {
                Providers = new ProviderConfig
                {
                    Default = "openai",
                    DefaultModel = "model-a",
                    ApiKeys = [new ApiKeyEntry { Provider = "openai", ApiKey = "sk-test" }],
                },
            }
        );

        var vm = new SettingsViewModel(h.ConfigStore, h.Agent, null).Refresh();
        Assert.Equal("model-a", vm.Model);
        vm.Model = "model-b";
        vm.SaveCommand.Execute().Subscribe();

        // SaveAsync 异步完成（轮询上限 5s，镜像 SettingsProviderRoundTripTests.WaitForAsync）
        for (var i = 0; i < 50 && h.Agent.State.ModelName != "model-b"; i++)
            await Task.Delay(100);

        Assert.Equal("model-b", h.Agent.State.ModelName); // ★ 修复前恒为 model-a（loop 冻结）
        Assert.Null(h.Agent.UsageStats); // loop 已失效 → 下次 Prompt 用新模型重建
        Assert.Single(h.Agent.State.Messages); // InvalidateConversationLoop 保留 _messages（上下文无损）
    }

    // ── /read、/chapter、/search：与 TUI 同源（Core.Novels），Desktop 只负责开窗口 ──

    [Fact]
    public async Task ReadCommand_ResolvesChapter_AndRaisesEvent()
    {
        using var h = new Harness();
        await SeedChaptersAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        string? readId = null;
        string? notice = null;
        vm.RequestChapterRead += id => readId = id;
        vm.SystemMessageRequested += text => notice = text;

        Assert.True(vm.TryExecuteCommand("/read 2"));
        for (var i = 0; i < 50 && readId is null && notice is null; i++)
            await Task.Delay(20);

        Assert.Equal("c2", readId);
        Assert.Null(notice);
    }

    [Fact]
    public async Task ReadCommand_UnknownChapter_ExplainsInsteadOfOpening()
    {
        using var h = new Harness();
        await SeedChaptersAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        string? readId = null;
        string? notice = null;
        vm.RequestChapterRead += id => readId = id;
        vm.SystemMessageRequested += text => notice = text;

        Assert.True(vm.TryExecuteCommand("/read 99"));
        for (var i = 0; i < 50 && notice is null; i++)
            await Task.Delay(20);

        Assert.Null(readId);
        Assert.Contains("没有第 99 章", notice);
    }

    [Fact]
    public async Task ChapterCommand_ResolvesByTitle_AndRaisesJump()
    {
        using var h = new Harness();
        await SeedChaptersAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        string? jumped = null;
        vm.RequestChapterJump += id => jumped = id;

        Assert.True(vm.TryExecuteCommand("/chapter 进城"));
        for (var i = 0; i < 50 && jumped is null; i++)
            await Task.Delay(20);

        Assert.Equal("c2", jumped);
    }

    [Fact]
    public async Task SearchCommand_ReturnsHits_AndReportsEmptyResult()
    {
        using var h = new Harness();
        await SeedChaptersAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        IReadOnlyList<Core.Novels.ChapterSearchHit>? hits = null;
        string? query = null;
        string? notice = null;
        vm.RequestChapterSearchResults += (q, h2) =>
        {
            query = q;
            hits = h2;
        };
        vm.SystemMessageRequested += text => notice = text;

        Assert.True(vm.TryExecuteCommand("/search 密信"));
        for (var i = 0; i < 50 && hits is null; i++)
            await Task.Delay(20);

        Assert.Equal("密信", query);
        Assert.Equal("c1", Assert.Single(hits!).Chapter.Id);

        // 无命中：明确提示，不开空窗口
        hits = null;
        Assert.True(vm.TryExecuteCommand("/search 不存在的词"));
        for (var i = 0; i < 50 && notice is null; i++)
            await Task.Delay(20);

        Assert.Null(hits);
        Assert.Contains("没有找到包含", notice);
    }

    [Fact]
    public async Task MentionSuggestions_EmptyQuery_ListsProjectRootFiles_AndOpensPopup()
    {
        using var h = new Harness();
        var vm = h.CreateVm();

        // 项目根（A2）下放置一个文件：裸 @ → 列项目根顶层条目
        File.WriteAllText(Path.Combine(h.NovelsRoot, "序章.txt"), "内容");

        await vm.UpdateMentionSuggestionsAsync("");

        Assert.True(vm.IsSuggestionPopupVisible);
        Assert.Equal(0, vm.SelectedSuggestionIndex);
        Assert.Contains(vm.Suggestions, s => s.Name == "序章.txt");
    }

    [Fact]
    public async Task MentionSuggestions_SubdirectorySlash_NavigatesNextLevel()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        Directory.CreateDirectory(Path.Combine(h.NovelsRoot, "卷一 风起"));
        File.WriteAllText(Path.Combine(h.NovelsRoot, "卷一 风起", "细纲.md"), "内容");

        // B2 回归：含空格目录 + 尾 / → 列子目录内容
        await vm.UpdateMentionSuggestionsAsync("卷一 风起/");

        var suggestion = vm.Suggestions.SingleOrDefault(s => s.Name == "细纲.md");
        Assert.NotNull(suggestion);
        Assert.True(vm.IsSuggestionPopupVisible);
    }

    [Fact]
    public async Task MentionSuggestions_NoNovelsRoot_HidesPopup()
    {
        using var h = new Harness();
        // 未提供项目根（设计时 VM）→ 不弹窗（降级而非崩溃）
        var vm = new ConversationViewModel(h.Bridge, h.Engine, h.NovelStore);

        await vm.UpdateMentionSuggestionsAsync("");

        Assert.False(vm.IsSuggestionPopupVisible);
        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public async Task EntityRefSuggestions_TypeStage_ListsAllReferenceableTypes()
    {
        using var h = new Harness();
        var vm = h.CreateVm();

        await vm.UpdateEntityRefSuggestionsAsync(EntityRefParser.TryParse("#", 1));

        Assert.True(vm.IsSuggestionPopupVisible);
        Assert.Equal(EntityRefParser.DisplayTypes, vm.Suggestions.Select(s => s.Name).ToArray());

        await vm.UpdateEntityRefSuggestionsAsync(EntityRefParser.TryParse("#角", 2));

        Assert.Equal("角色", Assert.Single(vm.Suggestions).Name);
    }

    [Fact]
    public async Task EntityRefSuggestions_NameStage_FiltersByPrefix()
    {
        using var h = new Harness();
        await SeedEntityRefsAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        await vm.UpdateEntityRefSuggestionsAsync(EntityRefParser.TryParse("#角色:张", 5));

        var suggestion = Assert.Single(vm.Suggestions);
        Assert.Equal("张三", suggestion.Name);
        Assert.Equal("角色", suggestion.FullPath);
        Assert.True(vm.IsSuggestionPopupVisible);

        await vm.UpdateEntityRefSuggestionsAsync(EntityRefParser.TryParse("#角色:王", 5));

        Assert.Empty(vm.Suggestions);
        Assert.False(vm.IsSuggestionPopupVisible);
    }

    [Fact]
    public async Task InlineEntityReferences_ResolvesNameToId()
    {
        using var h = new Harness();
        await SeedEntityRefsAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        var sent = await vm.InlineEntityReferencesAsync("请让 #角色:张三 出场");

        Assert.Equal("请让 角色[ID:ch1]:张三 出场", sent);
    }

    [Fact]
    public async Task InlineEntityReferences_UnknownNameOrType_LeavesTextUnchanged()
    {
        using var h = new Harness();
        await SeedEntityRefsAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        Assert.Equal("#角色:不存在", await vm.InlineEntityReferencesAsync("#角色:不存在"));
        Assert.Equal("#xyz:1", await vm.InlineEntityReferencesAsync("#xyz:1"));
    }

    [Fact]
    public async Task EntityRefSuggestions_NameStage_SupportsVolumeChapterSceneAndNovel()
    {
        using var h = new Harness();
        await SeedEntityRefsAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        await vm.UpdateEntityRefSuggestionsAsync(
            EntityRefParser.TryParse("#分卷:卷", "#分卷:卷".Length)
        );
        Assert.Equal("卷一 风起", Assert.Single(vm.Suggestions).Name);

        await vm.UpdateEntityRefSuggestionsAsync(
            EntityRefParser.TryParse("#章节:雨夜", "#章节:雨夜".Length)
        );
        Assert.Equal("雨夜 寄北", Assert.Single(vm.Suggestions).Name);

        await vm.UpdateEntityRefSuggestionsAsync(
            EntityRefParser.TryParse("#场景:初遇", "#场景:初遇".Length)
        );
        Assert.Equal("初遇 在雨里", Assert.Single(vm.Suggestions).Name);

        await vm.UpdateEntityRefSuggestionsAsync(
            EntityRefParser.TryParse("#小说:测试", "#小说:测试".Length)
        );
        Assert.Equal("测试小说", Assert.Single(vm.Suggestions).Name);
    }

    [Fact]
    public async Task InlineEntityReferences_ResolvesSpacedNamesAcrossAllTabTypes()
    {
        using var h = new Harness();
        await SeedEntityRefsAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        var sent = await vm.InlineEntityReferencesAsync(
            "看 #场景:初遇 在雨里 与 #章节:雨夜 寄北 及 #分卷:卷一 风起 和 #小说:测试小说 结束"
        );

        Assert.Equal(
            "看 场景[ID:sc1]:初遇 在雨里 与 章节[ID:c1]:雨夜 寄北 及 分卷[ID:v1]:卷一 风起 和 小说[ID:n1]:测试小说 结束",
            sent
        );
    }

    [Fact]
    public async Task InlineEntityReferences_ShortNameDoesNotEatLongerWord()
    {
        using var h = new Harness();
        await SeedEntityRefsAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        // 角色「李」是「李四」的前缀：没有词边界，不能把「李四」解析成引用
        Assert.Equal("#角色:李四", await vm.InlineEntityReferencesAsync("#角色:李四"));
    }

    [Fact]
    public void CommandSuggestions_ToolsCascade_UsesArgumentRangeAndColon()
    {
        using var h = new Harness();
        h.Registry.Register(new StubTool("read_file", "读取文件"), "test-plugin");
        var vm = h.CreateVm();

        vm.UpdateCommandSuggestions("/to");
        Assert.Contains("/tools", vm.CommandSuggestions);
        Assert.Equal(
            (0, 3, " "),
            (vm.CommandReplaceStart, vm.CommandReplaceLength, vm.CommandSuffix)
        );

        vm.UpdateCommandSuggestions("/tools ");
        Assert.Contains("test-plugin", vm.CommandSuggestions);
        Assert.Equal(
            (7, 0, ":"),
            (vm.CommandReplaceStart, vm.CommandReplaceLength, vm.CommandSuffix)
        );

        vm.UpdateCommandSuggestions("/tools test-plugin:");
        Assert.Contains("read_file", vm.CommandSuggestions);
        Assert.Equal(
            (19, 0, " "),
            (vm.CommandReplaceStart, vm.CommandReplaceLength, vm.CommandSuffix)
        );
    }

    /// <summary>种子：角色 + 书 + 卷 + 章 + 场景（含空格名称），供 @ / # 补全与内联测试。</summary>
    private static async Task SeedEntityRefsAsync(Harness h)
    {
        var store = h.NovelStore;
        await store.SaveNovel(new Novel { Id = "n1", Title = "测试小说" });
        await store.SaveCharacter(
            new Character
            {
                Id = "ch1",
                NovelId = "n1",
                Name = "张三",
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "ch2",
                NovelId = "n1",
                Name = "李",
            }
        );
        await store.SaveVolume(
            new Volume
            {
                Id = "v1",
                NovelId = "n1",
                Name = "卷一 风起",
                Order = 1,
            }
        );
        await store.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = "n1",
                Title = "雨夜 寄北",
                Order = 1,
                Content = "雨落下来。",
            }
        );
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "sc1",
                NovelId = "n1",
                Title = "初遇 在雨里",
                Order = 1,
                ChapterId = "c1",
            }
        );
    }

    /// <summary>种子：3 章（含细纲/正文），供定位与检索测试。</summary>
    private static async Task SeedChaptersAsync(Harness h)
    {
        var store = h.NovelStore;
        await store.SaveNovel(new Novel { Id = "n1", Title = "测试小说" });
        await store.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = "n1",
                Title = "雨夜寄北",
                Order = 1,
                Summary = "发现密信",
                Content = "雨落下来，他把密信折好。",
            }
        );
        await store.SaveChapter(
            new Chapter
            {
                Id = "c2",
                NovelId = "n1",
                Title = "进城",
                Order = 2,
                Content = "城门在他面前打开。",
            }
        );
    }

    private static ExploreDetails Explore() =>
        new()
        {
            VariantSetId = "vs-1",
            ExploreType = "plot",
            Prompt = "生成两个剧情走向",
            Variants =
            [
                new VariantResult
                {
                    Id = "v1",
                    Label = "悲剧走向",
                    Content = "变体正文",
                },
            ],
        };

    private static Core.Tools.VariantReviewDecision RefineDecision() =>
        new()
        {
            Action = Core.Tools.VariantReviewAction.Refine,
            VariantId = "v1",
            VariantLabel = "悲剧走向",
            VariantContent = "变体正文",
        };

    /// <summary>测试用：把 Agent 占位消息挂到 VM 当前回合（生产代码由 SendMessageAsync 创建）。</summary>
    private static Desktop.Models.ChatMessage AttachAgentMessage(
        ConversationViewModel vm,
        bool processing
    )
    {
        var agent = new Desktop.Models.ChatMessage(MessageSender.Agent);
        var field = typeof(ConversationViewModel).GetField(
            "_agentMessage",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        field.SetValue(vm, agent);
        vm.IsProcessing = processing;
        return agent;
    }

    private static AgentStep ToolStep(string title) =>
        new()
        {
            Kind = StepKind.ToolCall,
            Title = title,
            Status = AgentStepStatus.Completed,
        };

    private static void AssertNotice(AgentStep step, string text)
    {
        Assert.Equal(StepKind.Notice, step.Kind);
        Assert.Equal(AgentStepStatus.Completed, step.Status);
        Assert.True(step.IsNoticeStep);
        Assert.False(step.IsStepHeader);
        Assert.False(step.IsContentStep);
        Assert.Equal(text, step.ContentText);
    }

    // ── 重试 / 编辑用户消息：分支回退语义 ──────────────────────────────

    [Fact]
    public async Task RetryFromUserMessage_KeepsTargetUser_AndDropsFollowingAssistant()
    {
        using var h = new Harness();
        await SeedConversationAsync(h);

        var started = await h.Engine.RetryFromUserMessageAsync(
            visibleUserMessageOrdinal: 0,
            editedText: null,
            CancellationToken.None
        );

        Assert.True(started);
        var active = await h.Engine.GetUiMessagesAsync();
        Assert.DoesNotContain(active, m => m.Role == MessageRole.Assistant);
        Assert.Single(active, m => m.Role == MessageRole.User && m.Content == "第一问");
        Assert.DoesNotContain(active, m => m.Content == "第二问");
    }

    [Fact]
    public async Task RetryFromUserMessage_LeavesOldBranchOnDisk()
    {
        using var h = new Harness();
        await SeedConversationAsync(h);

        await h.Engine.RetryFromUserMessageAsync(0, null, CancellationToken.None);

        var raw = string.Join(
            "\n",
            Directory
                .GetFiles(h.StoreDir, "*.jsonl", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
        );
        Assert.Contains("第一答", raw, StringComparison.Ordinal);
        var active = await h.Engine.GetUiMessagesAsync();
        Assert.DoesNotContain(active, m => m.Content == "第一答");
    }

    [Fact]
    public async Task RetryFromUserMessage_DoesNotRewriteExistingJsonl()
    {
        using var h = new Harness();
        await SeedConversationAsync(h);

        var before = Directory
            .GetFiles(h.StoreDir, "*.jsonl", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

        await h.Engine.RetryFromUserMessageAsync(0, null, CancellationToken.None);

        foreach (var (path, original) in before)
        {
            Assert.True(File.Exists(path), $"retry removed {Path.GetFileName(path)}");
            var current = File.ReadAllBytes(path);
            Assert.True(
                current.Length >= original.Length,
                $"retry truncated {Path.GetFileName(path)}: {original.Length} -> {current.Length}"
            );
            Assert.True(
                current.AsSpan(0, original.Length).SequenceEqual(original),
                $"retry rewrote the existing prefix of {Path.GetFileName(path)}"
            );
        }
    }

    [Fact]
    public async Task RetryFromUserMessage_ByEntryId_WorksWhenTargetLeftActiveAfterCompact()
    {
        using var h = new Harness();
        await SeedConversationAsync(h);

        var active = await h.Engine.GetUiMessagesAsync();
        var firstUser = active.First(m => m.Content == "第一问");
        var secondUser = active.First(m => m.Content == "第二问");
        var last = active[^1];
        var (nextOrder, _) = await h.Store.GetSessionMetadataAsync(h.Engine.SessionId);
        await h.Store.CompactSession(
            h.Engine.SessionId,
            h.Agent.CurrentNovelId ?? "",
            summary: "compact summary",
            firstKeptEntryId: secondUser.EntryId!,
            messagesBefore: active.Count,
            parentEntryId: last.EntryId,
            order: nextOrder
        );

        var compacted = await h.Engine.GetUiMessagesAsync();
        Assert.DoesNotContain(compacted, m => m.EntryId == firstUser.EntryId);

        var started = await h.Engine.RetryFromUserMessageAsync(
            0,
            "改写第一问",
            CancellationToken.None,
            targetEntryId: firstUser.EntryId
        );

        Assert.True(started);
        var after = await h.Engine.GetUiMessagesAsync();
        Assert.Contains(after, m => m.Content == "改写第一问");
    }

    [Fact]
    public async Task RetryFromUserMessage_KeepsEarlierTurns_WhenTargetingSecondUser()
    {
        using var h = new Harness();
        await SeedConversationAsync(h);

        await h.Engine.RetryFromUserMessageAsync(1, null, CancellationToken.None);

        var active = await h.Engine.GetUiMessagesAsync();
        Assert.Contains(active, m => m.Content == "第一答");
        Assert.Contains(active, m => m.Content == "第二问");
        Assert.DoesNotContain(active, m => m.Content == "第二答");
    }

    [Fact]
    public async Task RetryFromUserMessage_WithEditedText_ReplacesUserMessageBranch()
    {
        using var h = new Harness();
        await SeedConversationAsync(h);

        await h.Engine.RetryFromUserMessageAsync(0, "改写后的问题", CancellationToken.None);

        var active = await h.Engine.GetUiMessagesAsync();
        Assert.DoesNotContain(active, m => m.Content == "第一问");
        Assert.DoesNotContain(active, m => m.Role == MessageRole.Assistant);
        Assert.Single(active, m => m.Role == MessageRole.User && m.Content == "改写后的问题");
    }

    [Fact]
    public async Task RetryMessage_TrimsUiAfterTargetUser_AndCreatesAgentPlaceholder()
    {
        using var h = new Harness();
        await SeedConversationAsync(h);
        var vm = h.CreateVm();
        await vm.LoadConversationAsync(h.Engine.SessionId);

        var firstAssistant = vm.Messages.First(m => m.Sender == MessageSender.Agent);
        await vm.RetryMessageAsync(firstAssistant);

        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal(MessageSender.User, vm.Messages[0].Sender);
        Assert.Equal("第一问", vm.Messages[0].Content.ToString());
        Assert.Equal(MessageSender.Agent, vm.Messages[1].Sender);
    }

    [Fact]
    public async Task SubmitUserEdit_ReplacesBubble_AndAppendsEditedUser()
    {
        using var h = new Harness();
        await SeedConversationAsync(h);
        var vm = h.CreateVm();
        await vm.LoadConversationAsync(h.Engine.SessionId);

        var secondUser = vm.Messages.Where(m => m.Sender == MessageSender.User).ElementAt(1);
        vm.BeginEditUserMessage(secondUser);
        secondUser.EditDraft = "第二问-改";
        await vm.SubmitUserEditAsync(secondUser);

        Assert.Equal(4, vm.Messages.Count);
        Assert.Equal("第一问", vm.Messages[0].Content.ToString());
        Assert.Equal(MessageSender.Agent, vm.Messages[1].Sender);
        Assert.Equal("第二问-改", vm.Messages[2].Content.ToString());
        Assert.Equal(MessageSender.Agent, vm.Messages[3].Sender);
    }

    [Fact]
    public async Task StopProcessing_KeepsPartialAssistantMessage_ForCopyAndRetry()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        vm.Engine.NewSession();
        await vm.SendEnginePromptAsync("写一段雨中送别");

        var agent = vm.Messages[^1];
        vm.SetProcessing(true);
        vm.HandleAgentEvent(new StreamChunk("写到一半", "", StreamContentType.Text));
        vm.StopProcessingCommand.Execute().Subscribe();

        Assert.True(agent.IsInterrupted);
        Assert.True(agent.IsCompleted);
        Assert.True(agent.HasVisibleContent);

        var rows = ConversationRow.FromMessage(agent);
        Assert.Contains(rows, row => row.Kind == ConversationRowKind.Content);
        Assert.Contains(rows, row => row.Kind == ConversationRowKind.Interrupted);
        var actions = Assert.Single(rows, row => row.Kind == ConversationRowKind.Actions);
        Assert.True(actions.IsLastInMessage);
    }

    private static async Task SeedConversationAsync(Harness h)
    {
        h.Engine.NewSession();
        h.Agent.AppendMessage(new LlmChatMessage(ChatMessageRoles.User, "第一问"));
        h.Agent.AppendMessage(new LlmChatMessage(ChatMessageRoles.Assistant, "第一答"));
        h.Agent.AppendMessage(new LlmChatMessage(ChatMessageRoles.User, "第二问"));
        h.Agent.AppendMessage(new LlmChatMessage(ChatMessageRoles.Assistant, "第二答"));
        Assert.True(await h.Engine.TryPersistNewMessagesAsync());
    }

    private sealed class StubTool(string name, string label) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => "";
        public override JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        ) => Task.FromResult(new AgentToolResult());
    }

    /// <summary>最小装配（临时目录；Dispose 清理）。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _base = Path.Combine(
            Path.GetTempPath(),
            $"myth-desktop-vm-{Guid.NewGuid():N}"
        );
        private readonly FileNovelStore _novelStore;
        private readonly SessionEngine _engine;
        private readonly DesktopAgentBridge _bridge;
        private readonly FileSessionStore _store;

        public ConfigStore ConfigStore { get; }

        public Harness()
        {
            var root = Path.Combine(_base, "novels");
            NovelsRoot = root;
            var storeDir = Path.Combine(_base, "store");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(storeDir);

            Registry = new ToolRegistry();
            Agent = new Agent(Registry, null);
            _bridge = new DesktopAgentBridge(Agent);
            _novelStore = new FileNovelStore(root, storeDir);
            ConfigStore = new ConfigStore(storeDir);
            StoreDir = Path.Combine(root, "sessions");
            _store = new FileSessionStore(storeDir, root);
            _engine = new SessionEngine(
                _bridge,
                _store,
                _novelStore,
                new TodoStore(),
                new ContextTokenCalibrator(),
                ConfigStore,
                NullLogger.Instance
            );
        }

        public Agent Agent { get; }

        public ToolRegistry Registry { get; }

        public INovelStore NovelStore => _novelStore;
        public FileSessionStore Store => _store;
        public string StoreDir { get; }

        public string NovelsRoot { get; }

        public DesktopAgentBridge Bridge => _bridge;

        public SessionEngine Engine => _engine;

        public ConversationViewModel CreateVm() =>
            new(_bridge, _engine, _novelStore, tools: Registry, novelsRoot: NovelsRoot);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_base, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }
}
