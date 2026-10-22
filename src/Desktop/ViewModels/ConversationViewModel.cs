using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Core.Agent;
using Core.Data;
using Core.Novels;
using Core.Shared;
using Core.Tools;
using Desktop.Controls;
using Desktop.Models;
using Desktop.Services;
using LlmTornado.Code;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using ChatMessageModel = Desktop.Models.ChatMessage;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Desktop.ViewModels;

/// <summary>
/// 对话视图模型：消息列表 + 输入 + 流式处理。每对话持有一个 <see cref="SessionEngine"/>
/// （JSONL 会话生命周期/压缩）。Agent 事件不再由本 VM 直接订阅桥接（桥接是单例，
/// transient VM 各自订阅会造成后台对话渲染活跃对话事件的串扰）——由 MainWindowViewModel
/// 统一订阅后转发到 <see cref="HandleAgentEvent"/>（仅活跃对话被转发）。
/// </summary>
public sealed class ConversationViewModel : ViewModelBase
{
    private readonly DesktopAgentBridge _bridge;
    private readonly SessionEngine _engine;
    private readonly INovelStore _store;
    private readonly UserQuestionStore _questionStore;
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;
    private StreamEventHandler? _handler;
    private ChatMessageModel? _agentMessage;

    /// <summary>本轮已先行插入的用户消息文本（抑制 Loop 对同一条消息的回显）。</summary>
    private string? _pendingEchoText;
    private int _suggestionVersion;

    /// <summary>消息列表。</summary>
    public ObservableCollection<ChatMessageModel> Messages { get; } = [];

    /// <summary>输入补全建议（@ 提及 / # 实体引用共用同一弹窗）。</summary>
    public ObservableCollection<FileSystemItem> Suggestions { get; } = [];

    /// <summary>斜杠命令补全建议。</summary>
    public ObservableCollection<string> CommandSuggestions { get; } = [];

    public string InputText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public bool IsCommandPopupVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private int _selectedCommandIndex;
    public int SelectedCommandIndex
    {
        get => _selectedCommandIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedCommandIndex, value);
    }

    public bool IsProcessing
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>历史导航状态（JSONL 持久化由引擎负责）。</summary>
    private int _historyIndex = -1;
    private string _historyDraft = "";

    /// <summary>记录已发送输入到历史（内存 + JSONL）。</summary>
    public void PushHistory(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
            return;
        _ = _engine.SaveInputHistoryAsync(t);
        _historyIndex = -1;
        _historyDraft = "";
    }

    /// <summary>向上导航历史（返回 null 表示无更多）。</summary>
    public string? NavigateHistoryUp()
    {
        var history = _engine.InputHistory;
        if (history.Count == 0)
            return null;
        if (_historyIndex < 0)
        {
            _historyDraft = InputText;
            _historyIndex = history.Count - 1;
            return history[_historyIndex];
        }
        if (_historyIndex == 0)
            return history[0];
        _historyIndex--;
        return history[_historyIndex];
    }

    /// <summary>向下导航历史（返回 null 表示回到草稿）。</summary>
    public string? NavigateHistoryDown()
    {
        var history = _engine.InputHistory;
        if (history.Count == 0 || _historyIndex < 0)
            return null;
        if (_historyIndex >= history.Count - 1)
        {
            _historyIndex = -1;
            return _historyDraft;
        }
        _historyIndex++;
        return history[_historyIndex];
    }

    /// <summary>记录当前输入为草稿（历史导航退出时恢复）。</summary>
    public void SetHistoryDraft(string draft) => _historyDraft = draft;

    /// <summary>命令执行完成事件（View 侧关闭弹窗、清空输入）。</summary>
    public event Action? CommandExecuted;

    /// <summary>当前会话 ID（引擎持有）。</summary>
    public string CurrentConversationId => _engine.SessionId;

    public string? CurrentNovelId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string CurrentNovelTitle
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "未选择小说";

    public bool IsSuggestionPopupVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public int SelectedSuggestionIndex
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool HasMessages
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string PlaceholderText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "输入指令，Enter 发送 · @ 引用角色/章节 · # 引用设定实体 · Esc 中断";

    /// <summary>
    /// 可用斜杠命令列表（补全候选）。权威定义是 <see cref="TryExecuteCommand"/> 的 switch 分支——
    /// 本表只是「给用户看的子集 + 带参数形态的展开」（如 <c>/theme dark</c>、<c>/compact smart</c>），
    /// 由 <c>AvailableCommands_AreAllDispatched</c> 测试锁住「表里的都能派发」。
    /// 反向不要求（<c>/stats</c> 是 <c>/state</c> 的别名等，不必都进补全）。
    /// </summary>
    private static readonly string[] AvailableCommands =
    [
        "/help",
        "/clear",
        "/state",
        "/history",
        "/tools",
        "/new",
        "/session",
        "/novel",
        "/settings",
        "/login",
        "/logout",
        "/compact",
        "/compact smart",
        "/decompress",
        "/tree",
        "/mode",
        "/context",
        "/read",
        "/chapter",
        "/search",
        "/commands",
        "/resume",
        "/stats",
        "/variants",
        ":cloud sync",
        ":cloud pull",
        ":cloud status",
        "/theme dark",
        "/theme light",
    ];

    /// <summary>输入为斜杠命令时刷新补全建议（View 的 TextChanged 调用）。</summary>
    public void UpdateCommandSuggestions(string text)
    {
        CommandSuggestions.Clear();
        var trimmed = text.TrimStart();
        var isCommand = trimmed.StartsWith('/') || trimmed.StartsWith(':');
        if (!isCommand || trimmed.Contains('\n'))
        {
            IsCommandPopupVisible = false;
            return;
        }

        var query = trimmed[1..].ToLowerInvariant();
        foreach (var cmd in AvailableCommands)
        {
            var cmdName = cmd[1..];
            if (string.IsNullOrEmpty(query) || cmdName.StartsWith(query))
                CommandSuggestions.Add(cmd);
        }

        IsCommandPopupVisible = CommandSuggestions.Count > 0;
        _selectedCommandIndex = 0;
        RaiseSelectedCommandChanged();
    }

    private void RaiseSelectedCommandChanged() =>
        this.RaisePropertyChanged(nameof(SelectedCommandIndex));

    /// <summary>
    /// 执行斜杠命令。返回 true 表示已消费（不再作为消息发送）。
    /// 对齐 TUI 命令语义：/help /clear /state /history /tools /new /novel /settings /theme
    /// /compact /decompress /tree /mode /commands /resume /stats /variants 与 :cloud 子命令。
    /// </summary>
    public bool TryExecuteCommand(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/') && !trimmed.StartsWith(':'))
            return false;

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        if (cmd == ":cloud")
        {
            RequestCloudColon?.Invoke(parts.Length > 1 ? parts[1].ToLowerInvariant() : "");
            CommandExecuted?.Invoke();
            return true;
        }

        switch (cmd)
        {
            case "/help":
                ShowAbout();
                break;
            case "/clear":
                ClearMessages();
                break;
            case "/new":
                NewConversationCommand.Execute().Subscribe();
                break;
            case "/session":
                RequestSessionPicker?.Invoke();
                break;
            case "/tools":
                ShowTools();
                break;
            case "/state":
            case "/stats": // /stats 别名 → 合并到 /state（对齐 TUI）
                _ = ShowStateAsync();
                break;
            case "/history":
                _ = ShowHistoryAsync();
                break;
            case "/novel":
                RequestNovelSwitch?.Invoke();
                break;
            case "/settings":
                RequestSettings?.Invoke();
                break;
            case "/login":
            case "/cloud":
                RequestCloudLogin?.Invoke();
                break;
            case "/logout":
                RequestCloudLogout?.Invoke();
                break;
            case "/theme":
                ToggleThemeRequested?.Invoke(parts.Length > 1 ? parts[1] : "toggle");
                break;
            case "/compact":
                _ = ExecuteCompactAsync(parts);
                break;
            case "/decompress":
                _ = ExecuteDecompressAsync(parts);
                break;
            case "/tree":
                RequestTree?.Invoke();
                break;
            case "/mode":
                RequestMode?.Invoke();
                break;
            case "/context":
                _ = ExecuteContextModeAsync(parts);
                break;
            case "/read":
                _ = ExecuteChapterReadAsync(parts);
                break;
            case "/chapter":
                _ = ExecuteChapterJumpAsync(parts);
                break;
            case "/search":
                _ = ExecuteChapterSearchAsync(parts);
                break;
            case "/commands":
                RequestCommandsDialog?.Invoke();
                break;
            case "/resume":
                RequestResume?.Invoke(parts.Length > 1 ? parts[1] : null);
                break;
            case "/variants":
                RequestReopenVariants?.Invoke();
                break;
            default:
                // 未知命令：提示
                SystemMessageRequested?.Invoke($"未知命令 `{cmd}`，输入 /commands 查看所有命令");
                break;
        }

        CommandExecuted?.Invoke();
        return true;
    }

    /// <summary>
    /// /context [compact|continuation]：上下文管理模式热切换（与 TUI 同名命令对齐）。
    /// 无参只回显当前模式；热切换由 Core 的 ContextModeSwitcher 完成（工具重注册 + 索引重建 + 落盘）。
    /// </summary>
    private async Task ExecuteContextModeAsync(string[] parts)
    {
        var arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        var switcher = Program.ContextModeSwitcher;
        var current = switcher?.CurrentMode ?? ContextMode.Compact;
        var currentLabel = current == ContextMode.Continuation ? "接续" : "压缩";

        if (arg is not ("compact" or "continuation"))
        {
            AddSystemNotice(
                $"当前上下文管理模式：{currentLabel}（/context compact 或 /context continuation 切换，立即生效）"
            );
            return;
        }

        if (switcher is null)
        {
            AddSystemNotice("上下文管理模式不可切换：Host 未接线。");
            return;
        }

        var (ok, message) = await switcher.SwitchModeAsync(arg);
        AddSystemNotice(message);
        ContextStatsChanged?.Invoke();
    }

    /// <summary>
    /// /read [序号|ID|标题片段]：打开只读章节阅读器（与 TUI 的 /read 同源逻辑：
    /// Core.Novels.ChapterLocator 定位，Desktop 侧用 ChapterReaderWindow 呈现）。
    /// </summary>
    private async Task ExecuteChapterReadAsync(string[] parts)
    {
        var chapter = await ResolveChapterAsync(parts);
        if (chapter is null)
            return;
        RequestChapterRead?.Invoke(chapter.Id);
    }

    /// <summary>/chapter [序号|ID|标题片段]：在设定面板大纲 Tab 定位（卷筛选 + 高亮）。</summary>
    private async Task ExecuteChapterJumpAsync(string[] parts)
    {
        var chapter = await ResolveChapterAsync(parts);
        if (chapter is null)
            return;
        RequestChapterJump?.Invoke(chapter.Id);
    }

    /// <summary>
    /// /search &lt;关键字&gt;：全书只读检索（离线、确定），结果进独立窗口，双击打开阅读器。
    /// </summary>
    private async Task ExecuteChapterSearchAsync(string[] parts)
    {
        if (string.IsNullOrEmpty(CurrentNovelId))
        {
            SystemMessageRequested?.Invoke("当前没有加载小说");
            return;
        }
        var query = parts.Length > 1 ? parts[1].Trim() : "";
        if (query.Length == 0)
        {
            SystemMessageRequested?.Invoke("用法: /search <关键字>");
            return;
        }

        var chapters = (await _store.GetChapters(CurrentNovelId)).OrderBy(c => c.Order).ToList();
        var hits = NovelTextSearch.Search(chapters, query);
        if (hits.Count == 0)
        {
            SystemMessageRequested?.Invoke(
                $"没有找到包含「{query}」的章节（全书 {chapters.Count} 章）"
            );
            return;
        }
        RequestChapterSearchResults?.Invoke(query, hits);
    }

    /// <summary>解析章节参数（序号 | ID | 标题片段）→ 章节；失败给出可行动提示。</summary>
    private async Task<Chapter?> ResolveChapterAsync(string[] parts)
    {
        if (string.IsNullOrEmpty(CurrentNovelId))
        {
            SystemMessageRequested?.Invoke("当前没有加载小说");
            return null;
        }

        var chapters = (await _store.GetChapters(CurrentNovelId)).OrderBy(c => c.Order).ToList();
        var (chapter, message) = ChapterLocator.Resolve(
            chapters,
            parts.Length > 1 ? parts[1] : null
        );
        if (chapter is null)
        {
            SystemMessageRequested?.Invoke(
                message is null ? ChapterLocator.UsageHint : $"/read · {message}"
            );
            return null;
        }
        return chapter;
    }

    /// <summary>/compact [smart] [N]：解析与 TUI SlashCommandParser.ParseCompact 一致。</summary>
    private async Task ExecuteCompactAsync(string[] parts)
    {
        var (smart, keepRecent) = ParseCompactArgs(parts);
        var outcome = await _engine.CompactAsync(smart, keepRecent);
        ContextStatsChanged?.Invoke();
        switch (outcome.Kind)
        {
            case CompactOutcomeKind.Done:
                AddSystemNotice($"已压缩上下文：{outcome.Before} → {outcome.After} 条 (LLM 可见)");
                break;
            case CompactOutcomeKind.Rejected:
                AddSystemNotice(outcome.Error ?? "压缩未执行");
                break;
            default:
                AddSystemNotice(outcome.Error is { Length: > 0 } e ? $"压缩失败：{e}" : "压缩失败");
                break;
        }
    }

    /// <summary>解析 /compact 参数（移植 SlashCommandParser.ParseCompact）：
    /// "/compact" → 普通压缩默认 keepRecent；"/compact N" → 普通压缩 keepRecent=N；
    /// "/compact smart [N]" → 智能压缩。</summary>
    private (bool Smart, int KeepRecent) ParseCompactArgs(string[] parts)
    {
        var keepRecent = _bridge.Agent.ModelProfile?.ScaledKeepRecent ?? 20;
        var smart = false;
        var idx = 1;
        if (parts.Length > idx && parts[idx].Equals("smart", StringComparison.OrdinalIgnoreCase))
        {
            smart = true;
            idx++;
        }
        if (parts.Length > idx && int.TryParse(parts[idx], out var parsed))
            keepRecent = parsed; // 非法值原样透传（压缩路径的 cutIndex 防御处理）
        return (smart, keepRecent);
    }

    private async Task ExecuteDecompressAsync(string[] parts)
    {
        var blockId = parts.Length >= 2 ? parts[1].Trim() : "";
        var md = await _engine.DecompressAsync(blockId);
        var owner = GetOwnerWindow();
        if (owner is not null)
            _ = new MarkdownDialog("恢复压缩内容", md).ShowDialog(owner);
        else
            AddSystemNotice(md);
    }

    /// <summary>请求切换小说（MainWindow 处理）。</summary>
    public event Action? RequestNovelSwitch;

    /// <summary>请求打开会话管理器（MainWindow 处理）。</summary>
    public event Action? RequestSessionPicker;

    /// <summary>请求打开设置（MainWindow 处理）。</summary>
    public event Action? RequestSettings;

    /// <summary>请求云端登录（MainWindow 打开 CloudLoginWindow）。</summary>
    public event Action? RequestCloudLogin;

    /// <summary>请求云端登出（MainWindow 调用 CloudAuthService.Logout）。</summary>
    public event Action? RequestCloudLogout;

    /// <summary>主题切换请求（MainWindow 处理）。</summary>
    public event Action<string>? ToggleThemeRequested;

    /// <summary>请求打开分支树选择器（/tree）。</summary>
    public event Action? RequestTree;

    /// <summary>请求切换增强模式（/mode）。</summary>
    public event Action? RequestMode;

    /// <summary>请求打开命令总表（/commands）。</summary>
    public event Action? RequestCommandsDialog;

    /// <summary>请求恢复小说会话（/resume [novel-id]）。</summary>
    public event Action<string?>? RequestResume;

    /// <summary>/read &lt;序号|ID|标题片段&gt;：只读章节阅读器（窗口）。</summary>
    public event Action<string>? RequestChapterRead;

    /// <summary>/chapter &lt;序号|ID|标题片段&gt;：在设定面板大纲 Tab 定位章节。</summary>
    public event Action<string>? RequestChapterJump;

    /// <summary>/search &lt;关键字&gt;：全书检索结果窗口（正文/细纲/标题）。</summary>
    public event Action<string, IReadOnlyList<ChapterSearchHit>>? RequestChapterSearchResults;

    /// <summary>请求执行 :cloud 子命令（sync|pull|status|login|logout）。</summary>
    public event Action<string>? RequestCloudColon;

    /// <summary>请求重新打开最近变体组（/variants）。</summary>
    public event Action? RequestReopenVariants;

    /// <summary>explore 工具产出变体组（MainWindow 弹审阅窗）。</summary>
    public event Action<ExploreDetails>? ExploreVariantRaised;

    /// <summary>上下文占用需要重算（发消息/压缩/切换后）。</summary>
    public event Action? ContextStatsChanged;

    /// <summary>压缩开始/结束（状态栏 spinner 压缩期同转,对齐 TUI）。</summary>
    public event Action<bool>? CompactStateChanged;

    /// <summary>系统消息（未知命令提示等）。</summary>
    public event Action<string>? SystemMessageRequested;

    /// <summary>对话完成事件（MainWindowViewModel 刷新会话列表）。</summary>
    public event Action? ConversationCompleted;

    /// <summary>历史加载完成事件（View 滚动到底部）。</summary>
    public event Action? HistoryLoaded;

    // ══════════════════════════════════════════════════════════
    //  变体 refinement 状态机（镜像 TuiApp.Variant.cs PendingRefinement）
    // ══════════════════════════════════════════════════════════

    /// <summary>变体「继续修改」待发上下文（用户提交下一条消息时消费）。</summary>
    internal sealed record PendingRefinement(
        VariantReviewDecision Decision,
        string? ExplorePrompt,
        string? ExploreType,
        VariantApplyTarget? ApplyTarget
    );

    private PendingRefinement? _pendingRefinement;

    /// <summary>是否有待发的变体修改要求（View 的 Enter 守卫据此在 Agent 处理中放行）。</summary>
    public bool HasPendingRefinement => _pendingRefinement is not null;

    /// <summary>
    /// 暂存 refine 上下文并预填输入框，等待用户输入修改要求（主窗口 Refine 路由调用）。
    /// </summary>
    public void BeginVariantRefinement(ExploreDetails explore, VariantReviewDecision decision)
    {
        _pendingRefinement = new PendingRefinement(
            decision,
            explore.Prompt,
            explore.ExploreType,
            explore.ApplyTarget
        );
        var label = decision.VariantLabel ?? "variant";
        InputText = $"修改「{label}」版本：";
        AddSystemNotice(
            $"📝 请输入对「{label}」的修改要求，按 Enter 发送（Agent 处理中会自动排入当前回合）"
        );
    }

    /// <summary>
    /// 构造 refinement prompt：剥离预填前缀后，按五段式拼装
    /// （原始探索指令 / 探索类型 / 应用目标 / 选中的变体内容 / 用户修改要求），
    /// 镜像 TuiApp.Input.cs BuildRefinementPrompt。
    /// </summary>
    internal static string BuildRefinementPrompt(PendingRefinement p, string text)
    {
        var label = p.Decision.VariantLabel ?? "variant";
        var prefix = $"修改「{label}」版本：";
        var modifiedText = text.StartsWith(prefix, StringComparison.Ordinal)
            ? text[prefix.Length..].Trim()
            : text;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"用户从 explore 工具的结果中选择了变体「{label}」（ID: {p.Decision.VariantId}），并要求基于此版本继续修改。"
        );
        sb.AppendLine();

        if (!string.IsNullOrEmpty(p.ExplorePrompt))
        {
            sb.AppendLine("===== 原始探索指令 =====\n");
            sb.AppendLine(p.ExplorePrompt);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(p.ExploreType))
        {
            sb.AppendLine("===== 探索类型 =====\n");
            sb.AppendLine(p.ExploreType);
            sb.AppendLine();
        }

        if (p.ApplyTarget is { } target && !string.IsNullOrEmpty(target.Title))
        {
            sb.AppendLine("===== 应用目标 =====\n");
            sb.AppendLine($"目标章节: {target.Title} (序号 {target.Order})");
            sb.AppendLine();
        }

        sb.AppendLine("===== 选中的变体内容 =====\n");
        sb.AppendLine(p.Decision.VariantContent ?? "");
        sb.AppendLine();
        sb.AppendLine("===== 用户修改要求 =====\n");
        sb.AppendLine(modifiedText);
        return sb.ToString();
    }

    /// <summary>
    /// 采用非章节变体：AppendMessage 写入上下文 + 增量持久化 + 系统气泡
    /// （镜像 TuiApp.Variant.cs:139-146，持久化失败早退）。
    /// </summary>
    public async Task AppendVariantToConversationAsync(string content, string label)
    {
        _bridge.Agent.AppendMessage(new LlmChatMessage(ChatMessageRoles.Assistant, content));
        if (!await _engine.TryPersistNewMessagesAsync())
            return;
        AddSystemNotice($"✅ 已采用变体「{label}」");
        ContextStatsChanged?.Invoke();
    }

    /// <summary>
    /// 切换分支并重建 UI 消息（镜像 LoadConversationAsync：含 _agentMessage/_handler 清理
    /// 与 HasMessages 重算，防止迟到的 AgentInterrupted 写入已移除的消息）。
    /// </summary>
    public async Task ReloadFromBranchAsync(string branchPointEntryId, bool generateSummary)
    {
        await _engine.SwitchBranchAsync(branchPointEntryId, generateSummary);
        Messages.Clear();
        foreach (var msg in SessionMessageMapper.MapUi(await _engine.GetUiMessagesAsync()))
            Messages.Add(msg);
        _agentMessage = null;
        _handler = null;
        HasMessages = Messages.Count > 0;
        HistoryLoaded?.Invoke();
        ContextStatsChanged?.Invoke();
    }

    /// <summary>显示关于/帮助窗口。</summary>
    private void ShowAbout()
    {
        var owner = GetOwnerWindow();
        if (owner is null)
            return;
        new AboutDialog().ShowDialog(owner);
    }

    /// <summary>清除当前对话（对齐 TUI /clear：清空 UI + 重置 Agent 上下文）。</summary>
    private void ClearMessages()
    {
        _bridge.Stop();
        _bridge.Agent.Reset();
        _engine.NewSession();
        Messages.Clear();
        Suggestions.Clear();
        _agentMessage = null;
        _handler = null;
        HasMessages = false;
        ContextStatsChanged?.Invoke();
    }

    /// <summary>显示可用的工具列表（对齐 TUI /tools + ToolsDialog）。</summary>
    private void ShowTools()
    {
        var registry = Program.Services?.GetService<Core.Tools.ToolRegistry>();
        if (registry is null)
            return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 可用工具");
        sb.AppendLine();
        sb.AppendLine($"共 **{registry.Count}** 个工具\n");
        foreach (var group in registry.GetAllByPlugin())
        {
            sb.AppendLine($"## {group.PluginName}");
            foreach (var t in group.Tools)
                sb.AppendLine($"- `{t.Name}` — {t.Description}");
            sb.AppendLine();
        }
        var owner = GetOwnerWindow();
        if (owner is null)
            return;
        var dialog = new MarkdownDialog("工具列表", sb.ToString());
        // 非 async 方法内 fire-and-forget（对话框模态显示，不阻塞发送流程）
        _ = dialog.ShowDialog(owner);
    }

    /// <summary>显示 Agent 状态（对齐 TUI /state）。</summary>
    private async Task ShowStateAsync()
    {
        try
        {
            var agent = _bridge.Agent;
            var s = agent.State;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Agent 状态");
            sb.AppendLine();
            sb.AppendLine("| 属性 | 值 |");
            sb.AppendLine("|------|-----|");
            sb.AppendLine($"| **Model** | {s.ModelName} |");
            sb.AppendLine($"| **ThinkingLevel** | {s.ThinkingLevel} |");
            sb.AppendLine($"| **Tools** | {s.Tools.Count} |");
            sb.AppendLine($"| **Messages** | {s.Messages.Count} |");
            sb.AppendLine($"| **IsRunning** | {IsProcessing} |");
            sb.AppendLine($"| **PendingToolCalls** | {s.PendingToolCalls.Count} |");
            sb.AppendLine($"| **Error** | {s.Error ?? "(无)"} |");
            sb.AppendLine();

            var stats = agent.UsageStats;
            if (stats is not null && stats.TotalCalls > 0)
            {
                sb.AppendLine("## 工具调用统计");
                sb.AppendLine();
                sb.AppendLine(stats.GetSummary());
                sb.AppendLine();
            }

            // 写作统计
            if (!string.IsNullOrEmpty(CurrentNovelId))
            {
                sb.AppendLine("## 写作统计");
                sb.AppendLine($"- 总字数: {await _store.GetTotalWordCount(CurrentNovelId)}");
                sb.AppendLine($"- 章节: {await _store.GetChapterCount(CurrentNovelId)}");
                sb.AppendLine($"- 角色: {await _store.GetCharacterCount(CurrentNovelId)}");
                sb.AppendLine();
            }

            if (agent.CompressionBlocks.Count > 0)
                sb.AppendLine(
                    $"- 历史压缩块: {agent.CompressionBlocks.Count} 个（/decompress 恢复）"
                );

            var owner = GetOwnerWindow();
            if (owner is null)
                return;
            await new MarkdownDialog("Agent 状态", sb.ToString()).ShowDialog(owner);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Agent 状态查询失败");
        }
    }

    /// <summary>显示对话历史（对齐 TUI /history）。</summary>
    private async Task ShowHistoryAsync()
    {
        try
        {
            var msgs = await _engine.GetUiMessagesAsync();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# 对话历史");
            sb.AppendLine();
            sb.AppendLine($"共 **{msgs.Count}** 条消息\n");
            foreach (var m in msgs)
            {
                // 上下文接续机制对用户隐藏：隐藏工具的调用与结果不列出
                if (Core.Tools.HiddenTools.IsHidden(m.ToolName))
                    continue;

                var role = m.Role switch
                {
                    MessageRole.User => "你",
                    MessageRole.Assistant => "Mythology",
                    MessageRole.Tool => $"工具({m.ToolName})",
                    _ => "系统",
                };
                sb.AppendLine($"### {role}");
                var content = m.Content;
                if (content.Length > 300)
                    content = content[..300] + "…";
                sb.AppendLine();
                sb.AppendLine(content);
                sb.AppendLine();
            }

            var owner = GetOwnerWindow();
            if (owner is null)
                return;
            await new MarkdownDialog("对话历史", sb.ToString()).ShowDialog(owner);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "对话历史查询失败");
        }
    }

    /// <summary>获取宿主主窗口（作为对话框所有者）。</summary>
    private static Window? GetOwnerWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: { } mw
            }
            ? mw
            : null;

    /// <summary>根据 @ 查询刷新补全建议（角色 + 章节）；空查询列出全部（各取上限 6 条）。</summary>
    public async Task UpdateMentionSuggestionsAsync(string query)
    {
        var version = ++_suggestionVersion;
        Suggestions.Clear();
        var novelId = CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            IsSuggestionPopupVisible = false;
            return;
        }
        try
        {
            var characters = await _store.GetCharacters(novelId);
            var items = characters
                .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(6)
                .Select(c => new FileSystemItem
                {
                    Name = c.Name,
                    FullPath = $"角色/{c.Name}",
                    IconKind = LucideIconKind.Users,
                })
                .ToList();

            var chapters = await _store.GetChapters(novelId);
            items.AddRange(
                chapters
                    .Where(ch => ch.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(6)
                    .Select(ch => new FileSystemItem
                    {
                        Name = ch.Title,
                        FullPath = $"章节/{ch.Title}",
                        IconKind = LucideIconKind.FileText,
                    })
            );

            // 过期结果丢弃（用户在等待期间继续输入）：弹窗状态归更新的那次调用管
            if (version != _suggestionVersion)
                return;
            Suggestions.Clear();
            foreach (var item in items)
                Suggestions.Add(item);
            SelectedSuggestionIndex = 0;
            IsSuggestionPopupVisible = Suggestions.Count > 0;
        }
        catch
        {
            // 数据层不可用时静默降级（无补全）
            if (version == _suggestionVersion)
                IsSuggestionPopupVisible = false;
        }
    }

    /// <summary>按 # 解析结果刷新补全建议：一级 = 类型显示名；二级 = 该类型实体名（前缀过滤）。</summary>
    public async Task UpdateEntityRefSuggestionsAsync(EntityRefParser.Result reference)
    {
        var version = ++_suggestionVersion;
        Suggestions.Clear();

        if (reference.Stage == EntityRefParser.Stage.Type)
        {
            foreach (
                var display in EntityRefParser.DisplayTypes.Where(t =>
                    t.Contains(reference.Query, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                Suggestions.Add(
                    new FileSystemItem
                    {
                        Name = display,
                        FullPath = "设定实体",
                        IconKind = IconForEntityType(EntityRefParser.ToEntityType(display)),
                    }
                );
            }
            SelectedSuggestionIndex = 0;
            IsSuggestionPopupVisible = Suggestions.Count > 0;
            return;
        }

        var type = EntityRefParser.ToEntityType(reference.TypeDisplay);
        if (type is null)
        {
            IsSuggestionPopupVisible = false;
            return;
        }

        try
        {
            var refs = await LoadEntityRefsAsync(type.Value);
            if (version != _suggestionVersion)
                return;
            foreach (
                var (name, _) in refs.Where(r =>
                    r.Name.Contains(reference.Query, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                Suggestions.Add(
                    new FileSystemItem
                    {
                        Name = name,
                        FullPath = reference.TypeDisplay,
                        IconKind = IconForEntityType(type),
                    }
                );
            }
            SelectedSuggestionIndex = 0;
            IsSuggestionPopupVisible = Suggestions.Count > 0;
        }
        catch
        {
            // 数据层不可用时静默降级（无补全）
            if (version == _suggestionVersion)
                IsSuggestionPopupVisible = false;
        }
    }

    /// <summary>实体类型 → 弹窗图标（与设定面板同款 Lucide 图标）。</summary>
    private static LucideIconKind IconForEntityType(EntityType? type) =>
        type switch
        {
            EntityType.Character => LucideIconKind.Users,
            EntityType.Faction => LucideIconKind.Building,
            EntityType.Location => LucideIconKind.MapPin,
            EntityType.Plotline => LucideIconKind.GitBranch,
            EntityType.Item => LucideIconKind.Package,
            EntityType.Relationship => LucideIconKind.Link2,
            EntityType.Chapter => LucideIconKind.FileText,
            _ => LucideIconKind.FileText,
        };

    /// <summary>
    /// # 实体引用名册：按类型取「引用显示名 → 实体 id」。
    /// 名册顺序沿用 store 返回（章节 = chNNN 文件序 = 章序）。补全与发送内联共用同一份名称口径。
    /// </summary>
    private async Task<List<(string Name, string Id)>> LoadEntityRefsAsync(EntityType type)
    {
        var novelId = CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return [];

        switch (type)
        {
            case EntityType.Character:
                return [.. (await _store.GetCharacters(novelId)).Select(c => (c.Name, c.Id))];
            case EntityType.Faction:
                return [.. (await _store.GetFactions(novelId)).Select(f => (f.Name, f.Id))];
            case EntityType.Location:
                return [.. (await _store.GetLocations(novelId)).Select(l => (l.Name, l.Id))];
            case EntityType.Plotline:
                return [.. (await _store.GetPlotlines(novelId)).Select(p => (p.Name, p.Id))];
            case EntityType.Item:
                return [.. (await _store.GetItems(novelId)).Select(i => (i.Name, i.Id))];
            case EntityType.Relationship:
            {
                var relationships = await _store.GetRelationshipsByNovel(novelId);
                var nameById = (await _store.GetCharacters(novelId)).ToDictionary(
                    c => c.Id,
                    c => c.Name
                );
                // 关系引用名刻意不含空格（面板展示用「 ↔ 」）：名册必须能被 InlineRegex 整体匹配
                return
                [
                    .. relationships.Select(r =>
                        (
                            $"{nameById.GetValueOrDefault(r.CharacterAId, "?")}↔{nameById.GetValueOrDefault(r.CharacterBId, "?")}",
                            r.Id
                        )
                    ),
                ];
            }
            case EntityType.Chapter:
                // 章节引用名 = 标题（不带「序. 」前缀）：名称含空格会截断内联正则
                return [.. (await _store.GetChapters(novelId)).Select(c => (c.Title, c.Id))];
            default:
                return [];
        }
    }

    /// <summary>
    /// 发送前把 `#类型:名称` 内联为 `类型[ID:xxx]:名称`（对齐 TUI TuiApp.Input.cs InlineEntityReferences）。
    /// 未知类型/名称查不到 → 原样保留；任何异常 → 返回原文，绝不吞掉用户输入。
    /// </summary>
    public async Task<string> InlineEntityReferencesAsync(string text)
    {
        if (string.IsNullOrEmpty(CurrentNovelId) || !text.Contains('#'))
            return text;

        try
        {
            var matches = EntityRefParser.InlineRegex().Matches(text);
            if (matches.Count == 0)
                return text;

            // key = "类型显示名\u0000名称"（值可为 null：已查过但未命中，避免重复读存储）
            var idByName = new Dictionary<string, string?>(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder(text.Length + 32);
            var last = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var display = match.Groups[1].Value;
                var name = match.Groups[2].Value;
                var type = EntityRefParser.ToEntityType(display);
                if (type is null)
                    continue;

                var key = display + "\u0000" + name;
                if (!idByName.TryGetValue(key, out var id))
                {
                    var refs = await LoadEntityRefsAsync(type.Value);
                    id = refs.Where(r => r.Name == name).Select(r => r.Id).FirstOrDefault();
                    idByName[key] = id;
                }
                if (id is null)
                    continue;

                sb.Append(text, last, match.Index - last)
                    .Append(display)
                    .Append("[ID:")
                    .Append(id)
                    .Append("]:")
                    .Append(name);
                last = match.Index + match.Length;
            }

            if (last == 0)
                return text;
            sb.Append(text, last, text.Length - last);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "# 实体引用内联失败，按原文发送");
            return text;
        }
    }

    /// <summary>发送命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SendMessageCommand { get; }

    /// <summary>停止命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> StopProcessingCommand { get; }

    /// <summary>新建对话命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewConversationCommand { get; }

    /// <summary>会话引擎（主窗口接分支树/变体重开时使用）。</summary>
    public SessionEngine Engine => _engine;

    /// <summary>激活本对话：把 ConversationLoop 每轮持久化回调指向本引擎。</summary>
    public void Activate() => _engine.Activate();

    /// <summary>无参构造函数（XAML 设计时支持）。</summary>
    public ConversationViewModel()
        : this(null!, null!, null!, null!) { }

    public ConversationViewModel(
        DesktopAgentBridge bridge,
        SessionEngine engine,
        INovelStore store,
        UserQuestionStore? questionStore = null,
        ILogger<ConversationViewModel>? log = null
    )
    {
        _bridge = bridge;
        _engine = engine;
        _store = store;
        _questionStore = questionStore ?? new UserQuestionStore();
        _log =
            log
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationViewModel>.Instance;

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync);
        StopProcessingCommand = ReactiveCommand.Create(StopProcessing);
        NewConversationCommand = ReactiveCommand.CreateFromTask(NewConversationAsync);

        // 引擎通知 → 系统气泡 + 仪表重算（压缩进度、溢出重试、持久化故障）
        _engine.OnSystemNotice += text => Dispatcher.UIThread.Post(() => AddSystemNotice(text));
        _engine.OnCompactStart += () =>
            Dispatcher.UIThread.Post(() =>
            {
                CompactStateChanged?.Invoke(true);
                AddSystemNotice("🗜 上下文压缩中…");
            });
        _engine.OnCompactEnd += _ =>
            Dispatcher.UIThread.Post(() =>
            {
                CompactStateChanged?.Invoke(false);
                ContextStatsChanged?.Invoke();
            });
    }

    /// <summary>从设定面板插入实体引用（如 `角色「林月如」`）。</summary>
    public void AppendInput(string text)
    {
        var current = InputText;
        var insert = current.Length > 0 && !current.EndsWith(' ') ? " " + text : text;
        InputText = current + insert + " ";
    }

    /// <summary>追加一条系统提示气泡（压缩进度/decompress 提示等）。</summary>
    public void AddSystemNotice(string text)
    {
        var msg = new ChatMessageModel(MessageSender.System);
        msg.Content.Append(text);
        Messages.Add(msg);
        HasMessages = true;
    }

    /// <summary>主窗口把处理状态转发到活跃对话（后台对话不被转发）。</summary>
    public void SetProcessing(bool processing) => IsProcessing = processing;

    /// <summary>发送消息。</summary>
    private async Task SendMessageAsync()
    {
        var text = InputText?.Trim() ?? "";
        if (text.Length == 0)
            return;

        // Refinement 优先于命令检查（对齐 TUI SubmitInput 顺序：pending 时输入必属修改要求）
        if (_pendingRefinement is not null)
        {
            var p = _pendingRefinement;
            _pendingRefinement = null;
            InputText = "";
            PushHistory(text);
            // # 实体引用先内联成 类型[ID:xxx]:名称 再拼五段式 prompt
            var outbound = await InlineEntityReferencesAsync(text);
            var refinementPrompt = BuildRefinementPrompt(p, outbound);

            if (IsProcessing)
            {
                // Agent 运行中：投递 Core SteeringQueue（turn 边界消费 →
                // SteerPromoted + MessageStart 由 HandleAgentEvent 渲染为 user 气泡），
                // 与 TUI 的引导排队语义一致（TuiApp.Input.cs SubmitInput）。
                var steerId = Guid.NewGuid().ToString("N")[..12];
                _bridge.Agent.Steer(
                    new LlmChatMessage(ChatMessageRoles.User, refinementPrompt),
                    steerId
                );
                AddSystemNotice("📝 修改要求已排入当前回合，将在本回合工具步结束后生效");
                return;
            }

            await SendEnginePromptAsync(refinementPrompt);
            return;
        }

        // 普通消息：Agent 处理中不排队（发送按钮此时隐藏，Enter 路径由此守卫兜底）
        if (IsProcessing)
            return;

        // 斜杠命令不进入对话循环（对齐 TUI HandleSlashCommand）
        if (TryExecuteCommand(text))
        {
            InputText = "";
            return;
        }

        var sent = await InlineEntityReferencesAsync(text);
        PushHistory(text);
        InputText = "";
        await SendEnginePromptAsync(sent, displayText: text);
    }

    /// <summary>
    /// 发送核心：用户气泡先行 + Loading 占位消息 + 流式 handler + 引擎编排（压缩预检/溢出重试）。
    /// 普通消息、变体 refinement、结构化实体提取共用（提取路径变体时主窗口也调用）。
    /// <paramref name="displayText"/> 只影响用户气泡显示（# 引用内联后 LLM 收 ID、气泡留原文）。
    /// </summary>
    public async Task SendEnginePromptAsync(string text, string? displayText = null)
    {
        // 拒发守卫：变体提取等路径可能绕过输入框 Enter 守卫；此时 bridge.SendAsync
        // 必抛 InvalidOperationException 且占位气泡永久卡 Loading，宁可不发。
        if (_bridge.IsProcessing)
        {
            AddSystemNotice("Agent 正在处理中，请等待当前回合结束后再发送");
            return;
        }
        if (_engine.IsPersistenceFaulted)
        {
            AddSystemNotice("会话保存故障尚未恢复，已拒绝发送以防止历史记录缺失");
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // ★ 用户气泡先行：同步插入，不等 Loop 回显（此前占位先上屏、用户气泡迟到）。
        // _pendingEchoText 供 HandleAgentEvent 吞掉 Loop 对同一消息的回显。
        var userMsg = new ChatMessageModel(MessageSender.User);
        userMsg.Content.Append(displayText ?? text);
        Messages.Add(userMsg);
        HasMessages = true;
        _pendingEchoText = text;

        // 创建空白 Agent 消息占位（Loading 指示）
        _agentMessage = new ChatMessageModel(MessageSender.Agent) { Loading = true };
        var localAgentMessage = _agentMessage;
        _handler = new StreamEventHandler(
            localAgentMessage,
            onError: null,
            onExploreVariant: ed => ExploreVariantRaised?.Invoke(ed)
        );
        Messages.Add(localAgentMessage);
        HasMessages = true;
        ContextStatsChanged?.Invoke();

        // 引擎编排：小说上下文注入 → 压缩预检 → 发送 → 持久化（溢出自动 compact+retry）
        await _engine.RunPromptAsync(text, _cts.Token);
        ContextStatsChanged?.Invoke();
    }

    /// <summary>中断当前处理。</summary>
    private void StopProcessing()
    {
        _cts?.Cancel();
        _bridge.Stop();
        FinalizeAgentMessage("\n\n> ⏹ 已中断");
    }

    /// <summary>新建对话。</summary>
    private Task NewConversationAsync()
    {
        _bridge.Stop();
        _engine.NewSession();
        Messages.Clear();
        Suggestions.Clear();
        _agentMessage = null;
        _handler = null;
        HasMessages = false;
        ContextStatsChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>加载历史会话：切换引擎上下文（LLM 侧）并重建 UI 消息。</summary>
    public async Task LoadConversationAsync(string conversationId)
    {
        await _engine.SwitchToAsync(conversationId, CurrentNovelId ?? "");
        Messages.Clear();
        foreach (var msg in SessionMessageMapper.MapUi(await _engine.GetUiMessagesAsync()))
            Messages.Add(msg);
        _agentMessage = null;
        _handler = null;
        HasMessages = Messages.Count > 0;
        HistoryLoaded?.Invoke();
        ContextStatsChanged?.Invoke();
    }

    /// <summary>等待 ask_user 工具创建问题并逐个弹出对话框（轮询上限 500ms，对齐 TUI）。</summary>
    public async Task ShowAskUserDialogAsync()
    {
        try
        {
            IReadOnlyList<PendingQuestion> pending = [];
            for (var i = 0; i < 50; i++) // 最多等 500ms
            {
                await Task.Delay(10);
                var all = _questionStore.GetPending();
                pending = all;
                if (pending.Count > 0)
                    break;
            }

            if (pending.Count == 0)
                return;

            var owner = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { } mw
                }
                ? mw
                : null;

            foreach (var q in pending.Where(q => !q.IsAnswered))
            {
                var win = new AskUserWindow(_questionStore, q);
                if (owner is not null)
                {
                    await win.ShowDialog(owner);
                }
                else
                {
                    // 无主窗口：非模态 + 关闭事件等待
                    var tcs = new TaskCompletionSource();
                    win.Closed += (_, _) => tcs.TrySetResult();
                    win.Show();
                    await tcs.Task;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AskUser 对话框弹出失败");
        }
    }

    /// <summary>处理 Agent 事件（UI 线程，由主窗口转发）。</summary>
    public void HandleAgentEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentStart:
                IsProcessing = true;
                break;

            case AgentEnd:
                FinalizeAgentMessage(null);
                ConversationCompleted?.Invoke();
                break;

            case AgentInterrupted:
                FinalizeAgentMessage("\n\n> ⏹ 已中断");
                break;

            // 用户消息（含注入的审计/系统消息）→ 创建用户气泡
            case MessageStart { Message.Role: LlmTornado.Code.ChatMessageRoles.User } ms:
                var text = ms.Message.Content ?? "";
                if (text.Length == 0)
                    break;
                // 本轮已先行插入的用户气泡：吞掉 Loop 回显（一次性内容匹配；
                // residual steer 回显先于本回合 user 消息且文本不同，不受影响）
                if (_pendingEchoText is not null && text == _pendingEchoText)
                {
                    _pendingEchoText = null;
                    break;
                }
                // 会话标题来源 = JSONL manifest（首条 user 消息提取），UI 不再持久化消息
                var userMsg = new ChatMessageModel(MessageSender.User);
                userMsg.Content.Append(text);
                Messages.Add(userMsg);
                HasMessages = true;
                break;

            // ask_user 工具：渲染工具卡片后弹出问题对话框（对齐 TUI AskUserDialog）
            case ToolExecutionStart { ToolName: "ask_user" }:
                _handler?.Handle(evt);
                _ = ShowAskUserDialogAsync();
                break;

            default:
                _handler?.Handle(evt);
                break;
        }
    }

    /// <summary>结束当前 Agent 消息（Loading=false，可选追加文本）。</summary>
    private void FinalizeAgentMessage(string? append)
    {
        if (_agentMessage is null)
            return;
        if (!string.IsNullOrEmpty(append))
            _agentMessage.Content.Append(append);
        _agentMessage.Loading = false;
        _handler?.CommitSources();

        // 空占位（Agent 未产出任何内容）移除，避免留下空气泡
        if (_agentMessage.IsEmpty && _agentMessage.AgentSteps.Count == 0)
            Messages.Remove(_agentMessage);

        _agentMessage = null;
        _handler = null;
    }
}
