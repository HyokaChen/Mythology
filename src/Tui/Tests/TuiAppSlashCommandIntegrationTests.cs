using Core.Agent;
using Core.Data;
using LlmTornado.Code;
using Tui.Services;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui.Tests;

/// <summary>
/// TuiApp 斜杠命令集成测试（无终端环境）：构造真实 TuiApp（共享 TuiSlashTestHarness 装配），
/// 反射驱动私有 HandleSlashCommand，断言非 UI 绑定的可观察副作用（主题切换、会话清空、
/// 云端登出、增强模式持久化、/compact 真实压缩落盘）。
///
/// 弹框命令的端到端渲染覆盖见 TuiAppTerminalSlashCommandIntegrationTests（虚拟终端运行
/// 真实 TerminalApp）；本类聚焦可在无终端下断言的状态副作用，与终端套件互补。
/// </summary>
public sealed class TuiAppSlashCommandIntegrationTests : IAsyncDisposable
{
    public async ValueTask DisposeAsync() => await H.DisposeAsync();

    private void InvokeSlash(string input) => H.InvokeSlashCommand(input);

    private TuiSlashTestHarness H { get; } = new();

    // ── /theme：主题切换（同步可断言） ──

    [Fact]
    public void ThemeDark_SetsDesertThemeAndPersists()
    {
        InvokeSlash("/theme dark");

        Assert.Equal(ThemeRegistry.Desert.Id, H.ThemeService.Current.Id);
        Assert.Equal(ThemeRegistry.Desert.Id, H.SettingsService.ThemeName); // PersistTheme 已写内存
    }

    [Fact]
    public void ThemeLight_SetsDesertLightTheme()
    {
        InvokeSlash("/theme light");

        Assert.Equal(ThemeRegistry.DesertLight.Id, H.ThemeService.Current.Id);
    }

    [Fact]
    public void ThemeToggle_CyclesTheme()
    {
        var before = H.ThemeService.Current.Id;

        InvokeSlash("/theme toggle");

        Assert.NotEqual(before, H.ThemeService.Current.Id);
    }

    [Fact]
    public void ThemeUnknown_KeepsCurrentTheme()
    {
        var before = H.ThemeService.Current.Id;

        InvokeSlash("/theme bogus"); // 仅 Toast，主题不变

        Assert.Equal(before, H.ThemeService.Current.Id);
    }

    // ── /clear：清空 Agent 上下文 ──

    [Fact]
    public void Clear_ResetsAgentMessages()
    {
        H.Agent.AppendMessage(new LlmChatMessage(ChatMessageRoles.User, "你好"));
        H.Agent.AppendMessage(
            new LlmChatMessage(ChatMessageRoles.Assistant, "你好，我是 Mythology")
        );
        Assert.Equal(2, H.Agent.State.Messages.Count);

        InvokeSlash("/clear");

        Assert.Empty(H.Agent.State.Messages);
    }

    // ── /logout 与 :cloud logout：清除云端凭据（含死代码修复回归） ──

    [Fact]
    public void Logout_ClearsConfiguredCloudSession()
    {
        H.SeedConfiguredCloudSession();

        InvokeSlash("/logout");

        Assert.False(H.CloudConnection.IsConfigured);
        Assert.Null(H.CloudConnection.Current);
    }

    [Fact]
    public void CloudColonLogout_ClearsConfiguredCloudSession()
    {
        // 回归：修复前 case ":cloud logout" 是死代码（路由只比较第一个 token，
        // 带空格的 case 永远不匹配），:cloud logout 实际落入未知命令。
        H.SeedConfiguredCloudSession();

        InvokeSlash(":cloud logout");

        Assert.False(H.CloudConnection.IsConfigured);
        Assert.Null(H.CloudConnection.Current);
    }

    [Fact]
    public void CloudColonStatus_DoesNotThrow()
    {
        InvokeSlash(":cloud status"); // 仅 Toast，无对话框
    }

    [Fact]
    public void CloudColonUnknownSubcommand_DoesNotThrow()
    {
        InvokeSlash(":cloud bogus"); // 未知子命令 → 未知命令提示
    }

    // ── /mode：增强模式循环 + 持久化到小说 ──

    [Fact]
    public async Task Mode_CyclesEnhanceModeAndPersistsNovel()
    {
        var novel = new Novel
        {
            Id = TuiSlashTestHarness.NovelId,
            Title = "测试小说",
            Genre = "奇幻",
            AutoEnhance = "none",
        };
        H.Store.Novels[TuiSlashTestHarness.NovelId] = novel;
        H.Agent.CurrentNovelId = TuiSlashTestHarness.NovelId;

        InvokeSlash("/mode");

        // StatusBar 默认增强模式为 semi → 首次 /mode 循环到 full
        await TuiSlashTestHarness.WaitUntilAsync(
            () =>
                H.Store.Novels.TryGetValue(TuiSlashTestHarness.NovelId, out var saved)
                && saved.AutoEnhance == "full",
            "增强模式未持久化为 full（循环 semi → full）"
        );
    }

    // ── /compact：真实压缩落盘（种子会话 + Agent 消息） ──

    [Fact]
    public async Task Compact_Plain_CompactsSeededSession()
    {
        await H.SeedSessionStoreAsync();

        InvokeSlash("/compact 2");

        await AssertCompactedAsync();
    }

    [Fact]
    public async Task Compact_SmartWithoutScorer_FallsBackToPlain()
    {
        // MessageScorer 为 null 时 smart 必须退回普通压缩（同一落盘结果）
        Assert.Null(H.Agent.MessageScorer);
        await H.SeedSessionStoreAsync();

        InvokeSlash("/compact smart 2");

        await AssertCompactedAsync();
    }

    [Fact]
    public void Compact_WithoutNovel_DoesNotThrow()
    {
        // 无小说/无消息 → 仅 Toast「没有可压缩的会话」
        InvokeSlash("/compact");
    }

    private async Task AssertCompactedAsync()
    {
        // 压缩完成：Agent 内存消息 = 摘要 + 保留尾段（cutIndex = 6 - 2 = 4 → 3 条）
        await TuiSlashTestHarness.WaitUntilAsync(
            () =>
                H.Agent.State.Messages.Count == 3
                && H.Agent.State.Messages[0]
                    .Content?.StartsWith("[上下文摘要", StringComparison.Ordinal) == true,
            "Agent 消息未压缩为「摘要 + 保留尾段」"
        );

        // 压缩块落盘：ListCompactBlocksAsync（/decompress 枚举 API）可见该块。
        // 注意：不用 GetLatestCompactSummaryAsync 断言——它只沿活跃 tip 的 parent 链
        // 回溯，压缩后下一条消息写入前 compact 条目不在链上（生产路径由后续消息自愈）。
        var blocks = await H.SessionStore.ListCompactBlocksAsync(TuiSlashTestHarness.SessionId);
        Assert.Single(blocks);
        Assert.Equal("b1", blocks[0].BlockId);
        Assert.Equal(4, blocks[0].MessageCount);
        Assert.Contains("压缩概览", blocks[0].Summary);

        // 被压缩的消息原文进入 CompressionBlocks（decompress 恢复数据源）
        Assert.Single(H.Agent.CompressionBlocks);
        Assert.Equal(4, H.Agent.CompressionBlocks[0].MessageIds.Count);
    }

    // ── 无崩溃冒烟（Toast-only 路径） ──

    [Fact]
    public void ResumeWithoutArg_DoesNotThrow()
    {
        InvokeSlash("/resume"); // 提示用法
    }

    [Fact]
    public void ToolsUnknownPlugin_DoesNotThrow()
    {
        InvokeSlash("/tools nosuchplugin"); // Toast 未知插件
    }

    [Fact]
    public void UnknownCommand_DoesNotThrow()
    {
        InvokeSlash("/bogus"); // 模糊匹配提示
    }

    // ── /context：上下文管理模式热切换（真实切换器：工具族 + Agent 选项 + config.yaml） ──

    [Fact]
    public async Task ContextContinuation_RegistersToolsAppliesOptionsAndPersists()
    {
        Assert.Equal(ContextMode.Compact, PersistedMode());

        InvokeSlash("/context continuation");

        // 切换是异步的：落盘最先发生，工具注册/Agent 选项在后 —— 等最后的可观察效果落定再断言
        await TuiSlashTestHarness.WaitUntilAsync(
            () =>
                H.Registry.GetToolsByPlugin("Context Continuation").Count == 1
                && H.Agent.ContextManagement is { Mode: ContextMode.Continuation },
            "/context continuation 未生效"
        );

        // 接续工具族已注册（组名 = 插件名），Agent 拿到接续选项（换页分支/提示骨架的判据）
        Assert.Equal(
            [TuiSlashTestHarness.FakeContextModePlugin.ToolName],
            H.Registry.GetToolsByPlugin("Context Continuation").Select(t => t.Name)
        );
        Assert.Equal(ContextMode.Continuation, H.Agent.ContextManagement!.Mode);
        Assert.Equal(ContextMode.Continuation, PersistedMode()); // 落盘：重启后仍生效

        InvokeSlash("/context compact");

        await TuiSlashTestHarness.WaitUntilAsync(
            () =>
                H.Registry.GetToolsByPlugin("Context Continuation").Count == 0
                && H.Agent.ContextManagement is null,
            "/context compact 未生效"
        );

        Assert.Empty(H.Registry.GetToolsByPlugin("Context Continuation"));
        Assert.Null(H.Agent.ContextManagement);
        Assert.Equal(ContextMode.Compact, PersistedMode());
    }

    /// <summary>磁盘上的模式（config.yaml）：重启后仍生效的判据。</summary>
    private ContextMode PersistedMode() =>
        new ConfigStore(H.ConfigStoreDirectory).Load().ContextManagement.ToOptions().Mode;

    [Fact]
    public async Task ContextUnknownMode_KeepsCurrentMode()
    {
        InvokeSlash("/context continuation");
        await TuiSlashTestHarness.WaitUntilAsync(
            () => H.Agent.ContextManagement is { Mode: ContextMode.Continuation },
            "/context continuation 未生效"
        );

        InvokeSlash("/context compression"); // 非法参数：提示用法，不改状态

        Assert.Equal(ContextMode.Continuation, H.Agent.ContextManagement!.Mode);
        Assert.Single(H.Registry.GetToolsByPlugin("Context Continuation"));
    }
}
