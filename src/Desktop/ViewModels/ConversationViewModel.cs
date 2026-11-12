using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Core.Agent;
using Core.Data;
using Core.Interactions;
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
    private readonly ToolDisplayFormatter _toolDisplay;
    private readonly ToolRegistry? _registry;
    private readonly ILogger _log;
    private readonly string? _novelsRoot;
    private CancellationTokenSource? _cts;
    private StreamEventHandler? _handler;
    private ChatMessageModel? _agentMessage;
    private bool _editSubmitting;

    /// <summary>本轮已先行插入的用户消息文本（抑制 Loop 对同一条消息的回显）。</summary>
    private string? _pendingEchoText;
    private int _suggestionVersion;

    /// <summary>消息列表。</summary>
    public ObservableCollection<ChatMessageModel> Messages { get; } = [];

    /// <summary>输入补全建议（@ 文件路径 / # 实体引用共用同一弹窗）。</summary>
    public ObservableCollection<FileSystemItem> Suggestions { get; } = [];

    /// <summary>斜杠命令补全建议。</summary>
    public ObservableCollection<string> CommandSuggestions { get; } = [];

    /// <summary>选中命令时替换的绝对起点（含 /tools 参数级补全）。</summary>
    public int CommandReplaceStart { get; private set; }

    /// <summary>选中命令时替换的长度。</summary>
    public int CommandReplaceLength { get; private set; }

    /// <summary>选中命令后追加的文本（命令级 = 空格；/tools 插件级 = 冒号）。</summary>
    public string CommandSuffix { get; private set; } = " ";

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
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsBusy));
        }
    }

    private bool _isSubmitting;

    /// <summary>
    /// 提交已受理、引擎调用尚未返回（含上下文准备、压缩预检、回合落盘）。
    /// 与 <see cref="IsProcessing"/> 取并集驱动发送↔停止按钮：只要任一段在途，发送按钮就不得露脸，
    /// 否则 ReactiveCommand 执行期的 :disabled 灰态会在准备阶段闪一帧。
    /// </summary>
    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSubmitting, value);
            this.RaisePropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>发送按钮应让位的总状态：Agent 循环运行中，或提交尚未走完。</summary>
    public bool IsBusy => IsProcessing || _isSubmitting;

    private bool _isCompacting;
    private ChatMessageModel? _compactStatusMessage;
    private bool _compactStartObserved;
    private string? _pendingCompactEnd;
    private DispatcherTimer? _compactWatch;

    /// <summary>上下文压缩是否进行中（系统状态行 + 状态栏 spinner 的单一事实源）。</summary>
    public bool IsCompacting
    {
        get => _isCompacting;
        private set => this.RaiseAndSetIfChanged(ref _isCompacting, value);
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

    /// <summary>/new：请求宿主创建新的对话 VM 并切换（保留旧对话历史）。</summary>
    public event Action? RequestNewConversation;

    /// <summary>/notes：请求宿主打开当前小说的作者随手记窗口。</summary>
    public event Action? RequestNotes;

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
    } = "输入指令，Enter 发送 · @ 引用文件 · # 引用任意设定 · Esc 中断";

    /// <summary>
    /// 可用斜杠命令列表（补全候选）。权威定义是 <see cref="TryExecuteCommand"/> 的 switch 分支——
    /// 本表只是「给用户看的子集 + 带参数形态的展开」（如 <c>/theme dark</c>、<c>/compact smart</c>），
    /// 由 <c>AvailableCommands_AreAllDispatched</c> 测试锁住「表里的都能派发」。
    /// 反向不要求（<c>/stats</c> 是 <c>/state</c> 的别名等，不必都进补全）。
    /// </summary>
    private static readonly string[] AvailableCommands =
    [
        "/help",
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
        "/var",
        "/notes",
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
        var startOffset = text.Length - trimmed.Length;
        CommandReplaceStart = startOffset;
        CommandReplaceLength = trimmed.Length;
        CommandSuffix = " ";
        var isCommand = trimmed.StartsWith('/') || trimmed.StartsWith(':');
        if (!isCommand || trimmed.Contains('\n'))
        {
            IsCommandPopupVisible = false;
            return;
        }

        // /tools 二级：插件 ID → 选中自动补冒号 → 工具名
        const string toolsPrefix = "/tools ";
        if (trimmed.StartsWith(toolsPrefix, StringComparison.Ordinal))
        {
            var after = trimmed[toolsPrefix.Length..];
            var colon = after.IndexOf(':');
            if (colon < 0)
            {
                // 已经选完插件（尾部空格）→ 等用户继续输入冒号/回车，不再弹同一份列表
                if (!after.Contains(' '))
                {
                    var pluginIds = _registry?.GetPluginIds() ?? [];
                    var matches = pluginIds
                        .Where(id => id.StartsWith(after, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var candidates = matches.Count > 0 ? matches : pluginIds.ToList();
                    if (candidates.Count > 0)
                    {
                        foreach (var id in candidates)
                            CommandSuggestions.Add(id);
                        CommandReplaceStart = startOffset + toolsPrefix.Length;
                        CommandReplaceLength = after.Length;
                        CommandSuffix = ":";
                        IsCommandPopupVisible = true;
                        _selectedCommandIndex = 0;
                        RaiseSelectedCommandChanged();
                        return;
                    }
                }
            }
            else
            {
                var pluginId = after[..colon];
                var toolPrefix = after[(colon + 1)..];
                if (!toolPrefix.Contains(' '))
                {
                    var actualPluginName = _registry?.GetPluginNameById(pluginId);
                    if (actualPluginName is not null)
                    {
                        var toolNames = _registry!
                            .GetToolsByPlugin(actualPluginName)
                            .Select(t => t.Name)
                            .OrderBy(n => n)
                            .ToList();
                        var matches = toolNames
                            .Where(n =>
                                n.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase)
                            )
                            .ToList();
                        var candidates = matches.Count > 0 ? matches : toolNames;
                        if (candidates.Count > 0)
                        {
                            foreach (var name in candidates)
                                CommandSuggestions.Add(name);
                            CommandReplaceStart = startOffset + toolsPrefix.Length + colon + 1;
                            CommandReplaceLength = toolPrefix.Length;
                            CommandSuffix = " ";
                            IsCommandPopupVisible = true;
                            _selectedCommandIndex = 0;
                            RaiseSelectedCommandChanged();
                            return;
                        }
                    }
                }
            }

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
    /// 对齐 TUI 命令语义：/help /state /history /tools /new /novel /settings /theme
    /// /compact /decompress /tree /mode /commands /resume /stats /variants 与 :cloud 子命令。
    /// </summary>
    public bool TryExecuteCommand(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/') && !trimmed.StartsWith(':'))
            return false;

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        // /new 会主动中止当前回合；压缩/模式切换必须等回合结束，避免与流式写回竞争。
        if (_bridge.IsProcessing && cmd is "/compact" or "/decompress" or "/context" or "/mode")
        {
            SystemMessageRequested?.Invoke("Agent 正在生成，先中断当前回合后再执行该命令");
            CommandExecuted?.Invoke();
            return true;
        }

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
            case "/new":
                if (parts.Length > 1)
                {
                    SystemMessageRequested?.Invoke($"用法: {cmd}（不接受参数）");
                    break;
                }
                RequestNewConversation?.Invoke();
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
            case "/var": // 兼容简写（与 TUI 对齐）
                RequestReopenVariants?.Invoke();
                break;
            case "/notes":
                RequestNotes?.Invoke();
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
    /// /read [序号|ID|标题片段]：打开章节内容窗口（与 TUI 的 /read 同源逻辑：
    /// Core.Novels.ChapterLocator 定位，Desktop 侧用 ChapterReaderWindow 呈现；默认可编辑）。
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
        // 压缩开始/结束的系统状态行由引擎事件驱动；这里只为「连开始事件都没有」的
        // 前置拒绝补一条提示，避免和进行中状态行重复。
        _compactStartObserved = false;
        var outcome = await _engine.CompactAsync(smart, keepRecent);
        ContextStatsChanged?.Invoke();
        if (!_compactStartObserved)
            AddSystemNotice(
                outcome.Kind switch
                {
                    CompactOutcomeKind.Rejected => outcome.Error ?? "压缩未执行",
                    CompactOutcomeKind.Failed => outcome.Error is { Length: > 0 } e
                        ? $"压缩失败：{e}"
                        : "压缩失败",
                    _ => "压缩未执行",
                }
            );
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
    public void BeginVariantRefinement(ExploreDetails explore, VariantReviewDecision decision) =>
        BeginVariantRefinement(
            new PendingRefinement(
                decision,
                explore.Prompt,
                explore.ExploreType,
                explore.ApplyTarget
            )
        );

    /// <summary>直接消费 Core 效果 plan 的 refinement 入口。</summary>
    public void BeginVariantRefinement(VariantEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        BeginVariantRefinement(
            new PendingRefinement(
                new VariantReviewDecision
                {
                    Action = VariantReviewAction.Refine,
                    VariantId = effect.VariantId,
                    VariantLabel = effect.VariantLabel,
                    VariantContent = effect.VariantContent,
                },
                effect.ExplorePrompt,
                effect.ExploreType,
                effect.ApplyTarget
            )
        );
    }

    private void BeginVariantRefinement(PendingRefinement pending)
    {
        _pendingRefinement = pending;
        var label = pending.Decision.VariantLabel ?? "variant";
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
        var targetText =
            p.ApplyTarget is { } t && !string.IsNullOrEmpty(t.Title)
                ? $"第{t.Order}章「{t.Title}」"
                : null;

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
        sb.AppendLine(
            VariantXml.Wrap(
                p.Decision.VariantContent ?? "",
                p.Decision.VariantId,
                label,
                p.ExploreType,
                targetText
            )
        );
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
        foreach (
            var msg in SessionMessageMapper.MapUi(await _engine.GetUiMessagesAsync(), _toolDisplay)
        )
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

    /// <summary>
    /// 根据 @ 查询刷新文件路径补全建议（小说项目根为基准目录；~/ 与 / 为绝对入口）。
    /// 目录候选尾带 / 可继续导航；空查询列出项目根顶层条目。
    /// </summary>
    public async Task UpdateMentionSuggestionsAsync(string query)
    {
        var version = ++_suggestionVersion;
        Suggestions.Clear();
        var novelsRoot = _novelsRoot ?? "";
        if (novelsRoot.Length == 0)
        {
            IsSuggestionPopupVisible = false;
            return;
        }

        try
        {
            // IO 在线程池执行（目录枚举可能碰到网络卷/慢盘，避免卡 UI 线程）
            var entries = await Task.Run(() =>
                Core.Shared.FilePathCompletion.GetEntries(query, novelsRoot)
            );

            // 过期结果丢弃（用户在等待期间继续输入）：弹窗状态归更新的那次调用管
            if (version != _suggestionVersion)
                return;
            Suggestions.Clear();
            foreach (var entry in entries)
            {
                Suggestions.Add(
                    new FileSystemItem
                    {
                        Name = entry.Name,
                        FullPath = entry.FullPath,
                        IconKind = Core.Shared.FilePathCompletion.IsDirectoryEntry(entry.Name)
                            ? LucideIconKind.Folder
                            : LucideIconKind.FileText,
                    }
                );
            }
            SelectedSuggestionIndex = 0;
            IsSuggestionPopupVisible = Suggestions.Count > 0;
        }
        catch
        {
            // IO 不可用时静默降级（无补全）
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
            EntityType.Novel => LucideIconKind.BookOpen,
            EntityType.Volume => LucideIconKind.Folder,
            EntityType.Chapter => LucideIconKind.FileText,
            EntityType.Character => LucideIconKind.Users,
            EntityType.Faction => LucideIconKind.Building,
            EntityType.Location => LucideIconKind.MapPin,
            EntityType.Plotline => LucideIconKind.GitBranch,
            EntityType.Scene => LucideIconKind.Theater,
            EntityType.Item => LucideIconKind.Package,
            EntityType.Relationship => LucideIconKind.Link2,
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
            case EntityType.Novel:
            {
                var novel = await _store.GetNovel(novelId);
                return novel is null ? [] : [(novel.Title, novel.Id)];
            }
            case EntityType.Volume:
                // 卷名可能是空串（只填了序号）→ 用「第N卷」作为引用名，与大纲行一致
                return
                [
                    .. (await _store.GetVolumes(novelId)).Select(v =>
                        (string.IsNullOrWhiteSpace(v.Name) ? $"第{v.Order}卷" : v.Name, v.Id)
                    ),
                ];
            case EntityType.Character:
                return [.. (await _store.GetCharacters(novelId)).Select(c => (c.Name, c.Id))];
            case EntityType.Faction:
                return [.. (await _store.GetFactions(novelId)).Select(f => (f.Name, f.Id))];
            case EntityType.Location:
                return [.. (await _store.GetLocations(novelId)).Select(l => (l.Name, l.Id))];
            case EntityType.Plotline:
                return [.. (await _store.GetPlotlines(novelId)).Select(p => (p.Name, p.Id))];
            case EntityType.Scene:
                return [.. (await _store.GetScenesByNovel(novelId)).Select(s => (s.Title, s.Id))];
            case EntityType.Item:
                return [.. (await _store.GetItems(novelId)).Select(i => (i.Name, i.Id))];
            case EntityType.Relationship:
            {
                var relationships = await _store.GetRelationshipsByNovel(novelId);
                var nameById = (await _store.GetCharacters(novelId)).ToDictionary(
                    c => c.Id,
                    c => c.Name
                );
                // 关系引用名刻意不含空格（面板展示用「 ↔ 」）：名册必须能被整体匹配
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
                // 章节引用名 = 标题（不带「序. 」前缀）；名称可含空格，由最长匹配处理
                return [.. (await _store.GetChapters(novelId)).Select(c => (c.Title, c.Id))];
            default:
                return [];
        }
    }

    /// <summary>
    /// 发送前把 `#类型:名称` 内联为 `类型[ID:xxx]:名称`。
    /// 名称可含空格（章节标题 / 卷名），按「最长且词边界结束」匹配；
    /// 未知类型/名称查不到 → 原样保留；任何异常 → 返回原文，绝不吞掉用户输入。
    /// </summary>
    public async Task<string> InlineEntityReferencesAsync(string text)
    {
        if (string.IsNullOrEmpty(CurrentNovelId) || !text.Contains('#'))
            return text;

        try
        {
            var matches = EntityRefParser.StartRegex().Matches(text);
            if (matches.Count == 0)
                return text;

            var refsByType = new Dictionary<EntityType, List<(string Name, string Id)>>();
            var sb = new System.Text.StringBuilder(text.Length + 32);
            var last = 0;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Index < last)
                    continue; // 已落在上一个引用名称内部
                var display = match.Groups[1].Value;
                var type = EntityRefParser.ToEntityType(display);
                if (type is null)
                    continue;

                var nameStart = match.Index + match.Length;
                if (nameStart >= text.Length)
                    continue;

                if (!refsByType.TryGetValue(type.Value, out var refs))
                {
                    refs = await LoadEntityRefsAsync(type.Value);
                    refsByType[type.Value] = refs;
                }

                var hit = EntityReferenceMatcher.MatchLongest(text, nameStart, refs);
                if (hit is null)
                    continue;

                sb.Append(text, last, match.Index - last)
                    .Append(display)
                    .Append("[ID:")
                    .Append(hit.Value.Id)
                    .Append("]:")
                    .Append(hit.Value.Name);
                last = nameStart + hit.Value.Name.Length;
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
        : this(null!, null!, null!) { }

    public ConversationViewModel(
        DesktopAgentBridge bridge,
        SessionEngine engine,
        INovelStore store,
        ILogger<ConversationViewModel>? log = null,
        ToolRegistry? tools = null,
        string? novelsRoot = null
    )
    {
        _bridge = bridge;
        _engine = engine;
        _store = store;
        _novelsRoot = novelsRoot;
        _toolDisplay = new ToolDisplayFormatter(tools);
        _registry = tools;
        _log =
            log
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationViewModel>.Instance;

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync);
        StopProcessingCommand = ReactiveCommand.Create(StopProcessing);
        NewConversationCommand = ReactiveCommand.CreateFromTask(NewConversationAsync);

        // 引擎通知 → 系统气泡 + 仪表重算（压缩进度、溢出重试、持久化故障）
        _engine.OnSystemNotice += text => Dispatcher.UIThread.Post(() => AddSystemNotice(text));
        _engine.OnCompactStart += () =>
        {
            // 先记在调用线程上：CompactAsync 可能在 UI Post 执行前就返回，
            // 只靠排版后的状态会误判成「没有开始事件」并补出重复提示。
            _compactStartObserved = true;
            Dispatcher.UIThread.Post(() =>
            {
                IsCompacting = true;
                CompactStateChanged?.Invoke(true);

                // 原地更新「压缩中…」行：spinner 只在压缩期间转，结束后同一行变结果，
                // 不再留下两条静态系统消息。
                _compactStatusMessage ??= new ChatMessageModel(MessageSender.System)
                {
                    IsSpinnerVisible = true,
                };
                if (_compactStatusMessage.Content.Length == 0)
                    _compactStatusMessage.AppendContent("上下文压缩中…");
                if (!Messages.Contains(_compactStatusMessage))
                {
                    Messages.Add(_compactStatusMessage);
                    HasMessages = true;
                }

                EnsureCompactWatch();

                // 跨线程事件到达顺序不保证：End 可能先排进 UI 队列。
                // Start 的 UI action 跑起来后立即收尾，避免留下永久旋转的「压缩中」行。
                if (_pendingCompactEnd is { } pending)
                    ApplyCompactEnd(pending);
            });
        };
        _engine.OnCompactEnd += message =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_compactStatusMessage is null)
                {
                    // Start 的 UI action 尚未执行：先挂起，由 Start 收尾。
                    if (_compactStartObserved)
                    {
                        _pendingCompactEnd = message;
                        return;
                    }

                    AddSystemNotice(message);
                    return;
                }

                ApplyCompactEnd(message);
            });
    }

    /// <summary>把压缩结束事件落到同一条系统状态行，并清掉压缩态。</summary>
    private void ApplyCompactEnd(string message)
    {
        _compactWatch?.Stop();
        _pendingCompactEnd = null;
        _compactStartObserved = false;
        IsCompacting = false;
        CompactStateChanged?.Invoke(false);

        var status = _compactStatusMessage;
        _compactStatusMessage = null;
        if (status is not null)
        {
            status.IsSpinnerVisible = false;
            status.ResetContent();
            status.AppendContent(message);
        }
        else
        {
            AddSystemNotice(message);
        }

        ContextStatsChanged?.Invoke();
    }

    /// <summary>
    /// 压缩态的兜底看门狗：事件丢失/乱序时，只要引擎已不在压缩，2 秒内收掉残留状态行。
    /// 正常结束仍走 OnCompactEnd，保留真实的完成文案。
    /// </summary>
    private void EnsureCompactWatch()
    {
        _compactWatch ??= new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            (_, _) => ReconcileCompactState()
        );
        if (!_compactWatch.IsEnabled)
            _compactWatch.Start();
    }

    /// <summary>UI 压缩态与引擎真实状态对账；引擎仍在压缩时不动。</summary>
    private void ReconcileCompactState()
    {
        if (_engine.IsCompacting)
            return;
        if (!IsCompacting && _compactStatusMessage is not { IsSpinnerVisible: true })
            return;

        ApplyCompactEnd("上下文压缩已结束");
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

    /// <summary>
    /// 实体变更提示：Agent 运行中直接插入当前 Agent 消息的步骤时间线，
    /// 让它出现在触发它的工具步骤之后，而不是等整条回复结束后统一堆到末尾。
    /// 空闲时仍落系统行；工具写盘的迟到事件直接忽略（工具步骤已经展示结果）。
    /// </summary>
    public void AddEntityChangeNotice(string text, bool fromToolCall)
    {
        if (IsProcessing && _agentMessage is { } agentMessage)
        {
            agentMessage.AgentSteps.Add(AgentStep.CreateNotice(text));
            return;
        }

        if (!fromToolCall)
            AddSystemNotice(text);
    }

    /// <summary>主窗口把处理状态转发到活跃对话（后台对话不被转发）。</summary>
    public void SetProcessing(bool processing)
    {
        ReconcileCompactState();
        IsProcessing = processing;
        if (processing || _agentMessage is null)
            return;

        // AgentEnd/Interrupted 正常收尾时 _agentMessage 已置空；这里处理的是错误路径：
        // Core 可能只发 AgentError 不发 AgentEnd，UI 必须自行收口，否则「重试」永远不出现。
        _pendingEchoText = null;
        FinalizeAgentMessage(null);
    }

    /// <summary>发送消息。</summary>
    private async Task SendMessageAsync()
    {
        var text = InputText?.Trim() ?? "";
        if (text.Length == 0)
            return;

        // 斜杠命令优先于 pending refinement：/new 等不能被当成修改要求发出去。
        if (TryExecuteCommand(text))
        {
            InputText = "";
            return;
        }

        // Refinement 流程：用户正在响应"继续修改"的变体
        if (_pendingRefinement is not null)
        {
            var p = _pendingRefinement;
            _pendingRefinement = null;
            InputText = "";
            PushHistory(text);

            IsSubmitting = true;
            try
            {
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
            }
            finally
            {
                IsSubmitting = false;
            }
            return;
        }

        // 普通消息：Agent 处理中不排队（发送按钮此时隐藏，Enter 路径由此守卫兜底）
        if (IsBusy)
            return;

        IsSubmitting = true;
        try
        {
            var sent = await InlineEntityReferencesAsync(text);
            PushHistory(text);
            InputText = "";
            await SendEnginePromptAsync(sent, displayText: text);
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    /// <summary>
    /// 发送核心：用户气泡先行 + Agent 占位消息 + 流式 handler + 引擎编排（压缩预检/溢出重试）。
    /// 普通消息、变体 refinement、结构化实体提取共用（提取路径变体时主窗口也调用）。
    /// <paramref name="displayText"/> 只影响用户气泡显示（# 引用内联后 LLM 收 ID、气泡留原文）。
    /// </summary>
    public async Task SendEnginePromptAsync(string text, string? displayText = null)
    {
        // 拒发守卫：变体提取等路径可能绕过输入框 Enter 守卫；此时 bridge.SendAsync
        // 必抛 InvalidOperationException 且占位气泡永久留在对话里，宁可不发。
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

        IsSubmitting = true;
        try
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            // ★ 用户气泡先行：同步插入，不等 Loop 回显（此前占位先上屏、用户气泡迟到）。
            // _pendingEchoText 供 HandleAgentEvent 吞掉 Loop 对同一消息的回显。
            var userMsg = new ChatMessageModel(MessageSender.User);
            userMsg.Content.Append(displayText ?? text);
            Messages.Add(userMsg);
            HasMessages = true;
            _pendingEchoText = text;

            // 创建空白 Agent 消息占位（流式内容落点）；首个正文/步骤到达前显示等待行。
            _agentMessage = new ChatMessageModel(MessageSender.Agent) { IsWaiting = true };
            var localAgentMessage = _agentMessage;
            _handler = new StreamEventHandler(
                localAgentMessage,
                onError: null,
                onExploreVariant: ed => ExploreVariantRaised?.Invoke(ed),
                toolDisplay: _toolDisplay
            );
            Messages.Add(localAgentMessage);
            HasMessages = true;
            ContextStatsChanged?.Invoke();

            // 引擎编排：小说上下文注入 → 压缩预检 → 发送 → 持久化（溢出自动 compact+retry）
            await _engine.RunPromptAsync(text, _cts.Token);
            ContextStatsChanged?.Invoke();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    /// <summary>进入用户消息内联编辑：同一时刻只允许一条消息在编辑。</summary>
    public void BeginEditUserMessage(ChatMessageModel message)
    {
        if (IsBusy || message.Sender != MessageSender.User)
            return;

        foreach (var item in Messages)
        {
            if (item.IsEditing && !ReferenceEquals(item, message))
                item.IsEditing = false;
        }

        message.EditDraft = message.Content.ToString();
        message.IsEditing = true;
    }

    /// <summary>退出内联编辑，不写回原消息。</summary>
    public void CancelUserEdit(ChatMessageModel message) => message.IsEditing = false;

    /// <summary>
    /// 提交用户消息编辑：原气泡及其后续内容从 UI 移除，按编辑后的文本从该回合重新生成。
    /// 旧分支保留在会话树里，只是不再是活跃路径。
    /// </summary>
    public async Task SubmitUserEditAsync(ChatMessageModel message)
    {
        if (
            IsBusy
            || _bridge.IsProcessing
            || _editSubmitting
            || message.Sender != MessageSender.User
        )
            return;

        _editSubmitting = true;
        try
        {
            var editedText = (message.EditDraft ?? "").Trim();
            if (editedText.Length == 0)
                return;

            var index = Messages.IndexOf(message);
            if (index < 0)
                return;
            var ordinal = CountVisibleUserOrdinal(index);
            if (ordinal < 0)
                return;

            var outbound = await InlineEntityReferencesAsync(editedText);
            if (!message.IsEditing)
                return; // 引用解析期间用户已取消，或另一条消息已进入编辑
            PushHistory(editedText);
            message.IsEditing = false;

            var removed = Messages.Skip(index).ToArray();
            TrimFrom(index);
            await StartRetryTurnAsync(ordinal, outbound, editedText, index, removed);
        }
        finally
        {
            _editSubmitting = false;
        }
    }

    /// <summary>
    /// 助手消息重试：保留它对应的用户消息，丢掉该回合之后的 UI 内容，重新生成最终正文。
    /// 只允许助手消息调用（用户气泡上的按钮是复制/编辑）。
    /// </summary>
    public async Task RetryMessageAsync(ChatMessageModel message)
    {
        if (IsBusy || _bridge.IsProcessing || message.Sender != MessageSender.Agent)
            return;

        foreach (var item in Messages)
        {
            if (item.IsEditing)
                item.IsEditing = false;
        }

        var index = Messages.IndexOf(message);
        if (index < 0)
            return;

        var userIndex = index;
        while (userIndex >= 0 && Messages[userIndex].Sender != MessageSender.User)
            userIndex--;
        if (userIndex < 0)
        {
            AddSystemNotice("找不到这条回复对应的用户消息，无法重试");
            return;
        }

        var ordinal = CountVisibleUserOrdinal(userIndex);
        if (ordinal < 0)
            return;

        var removed = Messages.Skip(userIndex + 1).ToArray();
        TrimFrom(userIndex + 1);
        await StartRetryTurnAsync(
            ordinal,
            editedOutbound: null,
            editedDisplay: null,
            restoreIndex: userIndex + 1,
            removed
        );
    }

    /// <summary>
    /// 重试/编辑共用的 UI 落点：创建（编辑时的）新用户气泡与 Agent 占位、挂流式 handler，
    /// 再让引擎回退分支并继续生成。引擎拒绝时恢复被移除的 UI 行并保留系统提示。
    /// </summary>
    private async Task StartRetryTurnAsync(
        int userOrdinal,
        string? editedOutbound,
        string? editedDisplay,
        int restoreIndex,
        ChatMessageModel[] removed
    )
    {
        if (_engine.IsPersistenceFaulted)
        {
            AddSystemNotice("会话保存故障尚未恢复，已拒绝重试以防止历史记录缺失");
            RestoreMessages(restoreIndex, removed);
            return;
        }
        if (_bridge.IsProcessing)
        {
            AddSystemNotice("Agent 正在处理中，请等待当前回合结束后再重试");
            RestoreMessages(restoreIndex, removed);
            return;
        }

        IsSubmitting = true;
        ChatMessageModel? localUser = null;
        ChatMessageModel? localAgent = null;
        try
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            // 编辑/重试都走 Continue：不会再有用户消息回显，不需要 echo 抑制。
            _pendingEchoText = null;

            if (editedDisplay is not null)
            {
                localUser = new ChatMessageModel(MessageSender.User);
                localUser.Content.Append(editedDisplay);
                Messages.Add(localUser);
                HasMessages = true;
            }

            localAgent = new ChatMessageModel(MessageSender.Agent) { IsWaiting = true };
            _agentMessage = localAgent;
            _handler = new StreamEventHandler(
                localAgent,
                onError: null,
                onExploreVariant: ed => ExploreVariantRaised?.Invoke(ed),
                toolDisplay: _toolDisplay
            );
            Messages.Add(localAgent);
            HasMessages = true;
            ContextStatsChanged?.Invoke();

            var started = await _engine.RetryFromUserMessageAsync(
                userOrdinal,
                editedOutbound,
                _cts.Token
            );
            if (!started)
            {
                if (localUser is not null)
                    Messages.Remove(localUser);
                if (ReferenceEquals(_agentMessage, localAgent))
                {
                    Messages.Remove(localAgent);
                    _agentMessage = null;
                    _handler = null;
                }
                RestoreMessages(restoreIndex, removed);
            }

            ContextStatsChanged?.Invoke();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    /// <summary>UI 可见用户消息序号（与引擎的 active user 过滤口径一致）。</summary>
    private int CountVisibleUserOrdinal(int messageIndex)
    {
        var ordinal = 0;
        for (var i = 0; i <= messageIndex && i < Messages.Count; i++)
        {
            if (Messages[i].Sender == MessageSender.User)
                ordinal++;
        }
        return ordinal - 1;
    }

    private void TrimFrom(int index)
    {
        for (var i = Messages.Count - 1; i >= index; i--)
            Messages.RemoveAt(i);
    }

    private void RestoreMessages(int index, IReadOnlyList<ChatMessageModel> removed)
    {
        for (var i = 0; i < removed.Count; i++)
            Messages.Insert(index + i, removed[i]);
        HasMessages = Messages.Count > 0;
    }

    /// <summary>中断当前处理。</summary>
    private void StopProcessing()
    {
        _cts?.Cancel();
        _bridge.Stop();
        FinalizeAgentMessage(null, interrupted: true);
    }

    /// <summary>新建对话（宿主在切换动画前可直接 await）。</summary>
    public async Task NewConversationAsync()
    {
        await _bridge.StopAsync();
        _engine.NewSession();
        Messages.Clear();
        Suggestions.Clear();
        _agentMessage = null;
        _handler = null;
        HasMessages = false;
        ContextStatsChanged?.Invoke();
    }

    /// <summary>加载历史会话：切换引擎上下文（LLM 侧）并重建 UI 消息。</summary>
    public async Task LoadConversationAsync(string conversationId)
    {
        await _engine.SwitchToAsync(conversationId, CurrentNovelId ?? "");
        Messages.Clear();
        foreach (
            var msg in SessionMessageMapper.MapUi(await _engine.GetUiMessagesAsync(), _toolDisplay)
        )
            Messages.Add(msg);
        _agentMessage = null;
        _handler = null;
        HasMessages = Messages.Count > 0;
        HistoryLoaded?.Invoke();
        ContextStatsChanged?.Invoke();
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
                _pendingEchoText = null;
                ConversationCompleted?.Invoke();
                break;

            case AgentInterrupted:
                FinalizeAgentMessage(null, interrupted: true);
                _pendingEchoText = null;
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

            default:
                _handler?.Handle(evt);
                break;
        }

        UpdateWaitingIndicator(evt);
    }

    /// <summary>
    /// 维护回合内等待行（spinner +「执行中」）：
    /// - 可见活动到达 → 立即收起：正文/思考 token、可见工具步骤、错误/熔断；
    /// - 阶段边界 → 立即亮起：思考结束、工具结束、LLM 流结束、下一轮 TurnStart。
    ///   否则「思考结束 → 工具/下一段正文」的间隙看起来像回合已经结束。
    /// 等待行的可见性仍以「没有正在处理的可见步骤」为准，并行工具/步骤不会被误判为空闲。
    /// 隐藏工具（上下文接续/历史检索）不产生步骤：开始时保持等待指示，结束后继续等待。
    /// </summary>
    private void UpdateWaitingIndicator(AgentEvent evt)
    {
        if (_agentMessage is null)
            return;

        var hasVisibleActivity = evt switch
        {
            // 纯空白 token 不是可见活动：模型常在正文前发 "\n\n"，
            // 若把它当成正文开始，思考结束后的等待行会提前消失，留下空白间隙。
            StreamChunk { ContentType: StreamContentType.Text } chunk => !string.IsNullOrWhiteSpace(
                chunk.Delta
            ),
            StreamChunk { ContentType: StreamContentType.Thinking } => true,
            ToolCallStreamStarted tcs => !Core.Tools.HiddenTools.IsHidden(tcs.ToolName),
            ToolExecutionStart tes => !Core.Tools.HiddenTools.IsHidden(tes.ToolName),
            AgentError or CircuitBreakerTriggered => true,
            _ => false,
        };
        if (hasVisibleActivity)
        {
            _agentMessage.IsWaiting = false;
            return;
        }

        if (
            evt
            is AgentStart
                or StreamChunk { ContentType: StreamContentType.ThinkingEnd }
                or ToolCallStreamStarted
                or ToolExecutionStart
                or ToolExecutionEnd
                or ToolExecutionUpdate
                or LlmStreamFinished
                or TurnStart
        )
        {
            // 只亮不灭：阶段边界永远不该把初始等待行/已有等待行关掉。
            // 关掉只由可见活动负责；这里只在没有处理中步骤时重新亮起。
            if (!_agentMessage.AgentSteps.Any(step => step.IsProcessing))
                _agentMessage.IsWaiting = true;
        }
    }

    /// <summary>结束当前 Agent 消息（可选追加文本/标记中断）。</summary>
    private void FinalizeAgentMessage(string? append, bool interrupted = false)
    {
        if (_agentMessage is null)
            return;
        _agentMessage.IsWaiting = false;
        _agentMessage.IsCompleted = true;
        if (interrupted)
            _agentMessage.IsInterrupted = true;
        if (!string.IsNullOrEmpty(append))
            _agentMessage.AppendContent(append);
        _handler?.FinalizeProcessingSteps(interrupted);
        _handler?.CommitSources();

        // 空占位（Agent 未产出任何内容）移除，避免留下空气泡
        if (_agentMessage.IsEmpty && _agentMessage.AgentSteps.Count == 0)
            Messages.Remove(_agentMessage);

        _agentMessage = null;
        _handler = null;
    }
}
