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
    public async Task MentionSuggestions_EmptyQuery_ListsEntities_AndOpensPopup()
    {
        using var h = new Harness();
        await SeedEntityRefsAsync(h);
        var vm = h.CreateVm();
        vm.CurrentNovelId = "n1";

        await vm.UpdateMentionSuggestionsAsync("");

        Assert.True(vm.IsSuggestionPopupVisible);
        Assert.Equal(0, vm.SelectedSuggestionIndex);
        Assert.Contains(vm.Suggestions, s => s.Name == "张三");
        Assert.Contains(vm.Suggestions, s => s.Name == "雨夜寄北");
    }

    [Fact]
    public async Task MentionSuggestions_NoNovel_HidesPopup()
    {
        using var h = new Harness();
        var vm = h.CreateVm();
        vm.CurrentNovelId = null;

        await vm.UpdateMentionSuggestionsAsync("");

        Assert.False(vm.IsSuggestionPopupVisible);
        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public async Task EntityRefSuggestions_TypeStage_ListsAllSevenTypes()
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

        await vm.UpdateEntityRefSuggestionsAsync(EntityRefParser.TryParse("#角色:李", 5));

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

    /// <summary>种子：1 个角色 + 1 章，供 @ / # 补全与内联测试。</summary>
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
        await store.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = "n1",
                Title = "雨夜寄北",
                Order = 1,
                Content = "雨落下来。",
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

    private static Desktop.Controls.VariantReviewDecision RefineDecision() =>
        new()
        {
            Action = Desktop.Controls.VariantReviewAction.Refine,
            VariantId = "v1",
            VariantLabel = "悲剧走向",
            VariantContent = "变体正文",
        };

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

        public ConfigStore ConfigStore { get; }

        public Harness()
        {
            var root = Path.Combine(_base, "novels");
            var storeDir = Path.Combine(_base, "store");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(storeDir);

            var registry = new ToolRegistry();
            Agent = new Agent(registry, null);
            _bridge = new DesktopAgentBridge(Agent);
            _novelStore = new FileNovelStore(root, storeDir);
            ConfigStore = new ConfigStore(storeDir);
            _engine = new SessionEngine(
                _bridge,
                new FileSessionStore(storeDir, root),
                _novelStore,
                new TodoStore(),
                new ContextTokenCalibrator(),
                ConfigStore,
                NullLogger.Instance
            );
        }

        public Agent Agent { get; }

        public INovelStore NovelStore => _novelStore;

        public ConversationViewModel CreateVm() => new(_bridge, _engine, _novelStore);

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
