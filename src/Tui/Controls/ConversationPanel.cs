using Core.Shared;
using Core.Tools;
using LlmTornado.Chat;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Figlet;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

using Data;

/// <summary>
/// 对话面板 — 显示用户和 AI 的消息，支持工具调用渲染。
/// </summary>
public sealed class ConversationPanel
{
    private readonly BackgroundPanel _panel; // 背景面板
    private PanelColors _colors; // 面板颜色配置
    private readonly AgentEventAdapter _adapter; // Agent 事件适配器
    private readonly ToolRegistry _registry; // 工具注册表

    private readonly StreamBubbleManager _streamBubbleManager; // 流式气泡管理器
    private readonly MessageRestorer _messageRestorer; // 消息恢复器

    private readonly Dictionary<string, int> _toolCallItemIndices = new(); // 工具调用项索引
    private readonly Dictionary<string, string> _toolCallNames = new(); // 工具调用名称
    private readonly Dictionary<string, string> _toolCallArgs = new(); // 工具调用参数
    private readonly Dictionary<string, int> _steerWaitingIndices = new(); // steerId → waiting 占位索引
    private readonly Queue<string> _steerWaitingIds = new(); // Waiting 占位 ID 的 FIFO 队列
    private int? _compactProgressIndex; // compact 进行中的占位索引
    private bool _isSteerPromoted; // 标记：下一个 OnUserMessage 来自 steer 转正，不调 Clear

    /// <summary>explore 工具变体选择事件。参数：toolCallId, details object。</summary>
    public event Action<string, object?>? OnExploreVariant;

    /// <summary>导航到实体事件。</summary>
    public event Action<string, string>? OnNavigateToEntity;

    /// <summary>Agent 状态变更事件（UI 三态，见 AgentUiState）。</summary>
    public event Action<TuiState>? AgentStateChanged;

    /// <summary>Agent 回合结束事件。</summary>
    public event Action? AgentTurnEnded;

    /// <summary>获取面板视觉元素。</summary>
    public Visual Visual => _panel;
    private DocumentFlow Flow { get; } // 文档流布局容器，承载所有气泡和工具调用项

    /// <summary>
    /// 初始化 ConversationPanel 实例。
    /// </summary>
    /// <param name="adapter">Agent 事件适配器。</param>
    /// <param name="registry">工具注册表。</param>
    /// <param name="colors">面板颜色配置。</param>
    public ConversationPanel(AgentEventAdapter adapter, ToolRegistry registry, PanelColors colors)
    {
        _adapter = adapter;
        _registry = registry;
        _colors = colors;

        Flow = new DocumentFlow { FollowTail = true, ItemSpacing = 0 };
        Flow.SetStyle(
            new ScrollViewerStyle
            {
                ScrollBarThickness = 1,
                TrackStyle = Style
                    .None.WithBackground(colors.ConversationBg)
                    .WithForeground(colors.ConversationBg),
                ThumbStyle = Style
                    .None.WithBackground(colors.ConversationBg)
                    .WithForeground(colors.ConversationBg),
            }
        );

        _panel = new BackgroundPanel
        {
            FillStyle = Style.None.WithBackground(colors.ConversationBg),
            Content = Flow,
        };

        Flow.PointerWheelRouted += OnFlowPointerWheel;

        _streamBubbleManager = new StreamBubbleManager(Flow, colors);
        _messageRestorer = new MessageRestorer(Flow, registry, colors, OnNavigateToEntity);

        AddHeader();
        WireEvents();
    }

    /// <summary>
    /// 添加对话面板头部：原 Figlet 金色标题保留，左侧配小 logo 点缀（MythMark 字符画）。
    /// Logo 透明处透出面板背景，与 Figlet 渐变均主题无关。
    /// </summary>
    private void AddHeader()
    {
        var logoFiglet = new TextFiglet(ProductInfo.Name)
        {
            Font = FigletPredefinedFont.Ogre,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = Align.Center,
        };
        var gradientBrush = Brush.LinearGradient(
            new GradientPoint(0f, 0f),
            new GradientPoint(1f, 1f),
            [
                new GradientStop(0f, Color.Rgb(0xFA, 0xBD, 0x2F)),
                new GradientStop(0.5f, Color.Rgb(0xFF, 0xF4, 0xD6)),
                new GradientStop(1f, Color.Rgb(0xD6, 0x8A, 0x48)),
            ]
        );
        logoFiglet.Style(
            TextFigletStyle.Default with
            {
                TextStyle = Style.None | TextStyle.Bold,
                ForegroundBrush = gradientBrush,
            }
        );

        Flow.Items.Add(
            new DocumentFlowItem
            {
                Content = new FlowDocument().Add(
                    new Padder
                    {
                        Content = new HStack(MythMark.Build(), logoFiglet).Spacing(2),
                        Padding = new Thickness(0, 1, 0, 1),
                        HorizontalAlignment = Align.Center,
                    }
                ),
                Alignment = DocumentFlowAlignment.Stretch,
                Padding = new Thickness(0),
            }
        );
    }

    /// <summary>
    /// 处理鼠标滚轮事件，实现自定义滚动。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">指针事件参数。</param>
    private void OnFlowPointerWheel(object? sender, PointerEventArgs e)
    {
        // Only intercept in Preview (Tunnel) phase — before ScrollViewer's Bubble handler runs.
        // ScrollViewer.OnPointerWheel scrolls only 1 row per tick; we scroll 3 instead.
        if (e.RoutingPhase != RoutingPhase.Preview)
            return;

        if (e.WheelDelta == 0)
            return;

        const int scrollStep = 4;
        var dy = e.WheelDelta > 0 ? -scrollStep : scrollStep;
        Flow.Scroll.ScrollBy(0, dy);
        e.Handled = true;
    }

    /// <summary>
    /// 连接 Agent 事件到 UI 更新。
    /// </summary>
    private void WireEvents()
    {
        _adapter.OnSteerPromoted += (steerId, text) =>
        {
            // Hook 信号 / CRUD inject → 无 waiting 需要删除
            if (string.IsNullOrEmpty(steerId))
                return;

            // 删除对应的 waiting 占位（索引匹配 + 偏移修正）
            RemoveWaitingItem(steerId);

            switch (_steerWaitingIds.Count)
            {
                // 从 FIFO 队列中出队（保持一致性）
                case > 0 when _steerWaitingIds.Peek() == steerId:
                    _steerWaitingIds.Dequeue();
                    break;
                case > 0:
                    // 防御性：FIFO 顺序不匹配，清空队列
                    _steerWaitingIds.Clear();
                    break;
            }

            // 标记：下一个 OnUserMessage 是 steer 转正，不调 Clear
            _isSteerPromoted = true;
        };

        _adapter.OnUserMessage += text =>
        {
            // ★ P0c: steer 转正时不调 Clear（waiting 阶段不破坏流式 assistant）
            if (!_isSteerPromoted)
            {
                _streamBubbleManager.Clear();
            }
            _isSteerPromoted = false;

            var userBlock = new TextBlock($"👤 {text}")
                .TextAlignment(TextAlignment.Left)
                .Wrap(true)
                .HorizontalAlignment(Align.Stretch)
                .Style(
                    new TextBlockStyle
                    {
                        Foreground = _colors.UserBubbleFg,
                        Background = _colors.UserBubbleBg,
                        FillBackground = true,
                    }
                );
            Flow.Items.Add(
                new DocumentFlowItem
                {
                    Content = new FlowDocument().Add(userBlock),
                    Alignment = DocumentFlowAlignment.Stretch,
                    BackgroundStyle = Style
                        .None.WithBackground(_colors.UserBubbleBg)
                        .WithForeground(_colors.UserBubbleFg),
                    Padding = new Thickness(3, 1, 3, 1),
                }
            );
        };

        _adapter.OnAssistantStart += () => _streamBubbleManager.Clear();

        _adapter.OnThinkingUpdate += accumulatedText =>
            _streamBubbleManager.UpdateThinking(accumulatedText);

        _adapter.OnThinkingEnd += () => _streamBubbleManager.FinalizeThinking();

        _adapter.OnAssistantDelta += delta => _streamBubbleManager.AppendAssistantDelta(delta);

        _adapter.OnAssistantEnd += _ => _streamBubbleManager.FinalizeAssistant();

        _adapter.OnToolStart += (id, name, args) =>
        {
            // 上下文接续机制对用户隐藏（new_context / history_* 不渲染）
            if (HiddenTools.IsHidden(name))
                return;

            _toolCallNames[id] = name;
            _toolCallArgs[id] = args;

            var (subtitle, paramDisplay) = ToolCallRenderer.ParseToolArgs(args);
            var item = ToolCallRenderer.BuildToolCallItem(
                name,
                subtitle,
                paramDisplay,
                isRunning: true,
                isError: false,
                _colors,
                argsJson: args
            );

            Flow.Items.Add(item);
            _toolCallItemIndices[id] = Flow.Items.Count - 1;
        };

        _adapter.OnToolEnd += (id, _, isError, result, details) =>
        {
            // 与 OnToolStart 对称：隐藏工具从未建卡，End 直接早退
            if (!_toolCallNames.ContainsKey(id))
            {
                _toolCallArgs.Remove(id);
                return;
            }

            var toolName = _toolCallNames.GetValueOrDefault(id, "tool");
            _toolCallNames.Remove(id);
            var argsStr = _toolCallArgs.GetValueOrDefault(id, "");
            _toolCallArgs.Remove(id);

            if (!_toolCallItemIndices.Remove(id, out var idx))
                return;
            if (idx < 0 || idx >= Flow.Items.Count)
                return;

            var tool = _registry.TryGet(toolName);
            var category = tool?.Category ?? ToolCategory.Generic;
            var entityType = tool?.EntityType;

            var (subtitle, paramDisplay) = ToolCallRenderer.ParseToolArgs(argsStr);

            Flow.Items[idx] = ToolCallRenderer.BuildToolCallResultItem(
                category,
                entityType,
                toolName,
                subtitle,
                paramDisplay,
                isError,
                // tool_search 卡片：隐藏工具名脱敏（模型侧结果文本不变，仅渲染层处理）
                toolName == "tool_search"
                    ? HiddenTools.Redact(result)
                    : result,
                _colors,
                OnNavigateToEntity,
                details,
                argsStr
            );

            // Explore/enhance tools: trigger variant selection event for any tool returning ExploreDetails
            if (!isError && details is ExploreDetails)
                OnExploreVariant?.Invoke(id, details);
        };

        _adapter.OnInterrupted += () =>
        {
            AgentStateChanged?.Invoke(TuiState.Idle);
            AddInterruptMessage();
        };

        _adapter.OnError += (rawMessage, httpStatusCode, isContextOverflow, recoverable) =>
        {
            AgentStateChanged?.Invoke(TuiState.Idle);
            AddErrorMessage(rawMessage, httpStatusCode, isContextOverflow, recoverable);
        };

        _adapter.OnAgentStart += () => AgentStateChanged?.Invoke(TuiState.Run);
        _adapter.OnAgentEnd += () =>
        {
            AgentStateChanged?.Invoke(TuiState.Idle);
            AgentTurnEnded?.Invoke();
        };

        _adapter.OnCompactStart += () =>
        {
            AgentStateChanged?.Invoke(TuiState.Compact);

            var compactBlock = new TextBlock("🔄 正在压缩上下文...")
                .TextAlignment(TextAlignment.Left)
                .HorizontalAlignment(Align.Stretch)
                .Style(
                    new TextBlockStyle
                    {
                        Foreground = _colors.Dim,
                        Background = _colors.ConversationBg,
                        FillBackground = true,
                    }
                );

            Flow.Items.Add(
                new DocumentFlowItem
                {
                    Content = new FlowDocument().Add(compactBlock),
                    Alignment = DocumentFlowAlignment.Stretch,
                    BackgroundStyle = Style
                        .None.WithBackground(_colors.ConversationBg)
                        .WithForeground(_colors.Dim),
                    Padding = new Thickness(3, 1, 3, 1),
                }
            );
            _compactProgressIndex = Flow.Items.Count - 1;
        };

        _adapter.OnCompactEnd += message =>
        {
            if (_compactProgressIndex is { } idx and >= 0 && idx < Flow.Items.Count)
            {
                var doneBlock = new TextBlock($"✅ {message}")
                    .TextAlignment(TextAlignment.Left)
                    .HorizontalAlignment(Align.Stretch)
                    .Style(
                        new TextBlockStyle
                        {
                            Foreground = _colors.Dim,
                            Background = _colors.ConversationBg,
                            FillBackground = true,
                        }
                    );

                Flow.Items[idx] = new DocumentFlowItem
                {
                    Content = new FlowDocument().Add(doneBlock),
                    Alignment = DocumentFlowAlignment.Stretch,
                    BackgroundStyle = Style
                        .None.WithBackground(_colors.ConversationBg)
                        .WithForeground(_colors.Dim),
                    Padding = new Thickness(3, 1, 3, 1),
                };
            }
            _compactProgressIndex = null;
            AgentStateChanged?.Invoke(TuiState.Idle);
        };

        _adapter.OnCircuitBreaker += (_, reason) =>
        {
            ToastService.Show(() =>
                new Toast
                {
                    Title = "断路器触发",
                    // Content 的 string → Visual 隐式转换生成 TextBlock（纯文本，不解析 markup）→ 不转义
                    Content = reason,
                    Severity = ToastSeverity.Error,
                    Duration = TimeSpan.FromSeconds(5),
                }
            );
        };
    }

    /// <summary>
    /// 应用主题颜色。
    /// </summary>
    /// <param name="colors">面板颜色配置。</param>
    public void ApplyTheme(PanelColors colors)
    {
        _colors = colors;
        _messageRestorer.SetColors(colors); // 重建气泡时用当前主题色
        _panel.FillStyle = Style.None.WithBackground(colors.ConversationBg);

        Flow.SetStyle(
            new ScrollViewerStyle
            {
                ScrollBarThickness = 1,
                TrackStyle = Style
                    .None.WithBackground(colors.ConversationBg)
                    .WithForeground(colors.ConversationBg),
                ThumbStyle = Style
                    .None.WithBackground(colors.ConversationBg)
                    .WithForeground(colors.ConversationBg),
            }
        );

        // ★ 强制面板重绘：FillStyle 是普通属性，set 不触发渲染；Refresh() 通过 detach/attach
        // 子内容触发 AttachChild → 渲染 + 面板进脏区，新 FillStyle 立即生效。
        _panel.Refresh();
    }

    /// <summary>
    /// 清空对话内容。
    /// </summary>
    public void Clear()
    {
        Flow.Items.Clear();
        _toolCallItemIndices.Clear();
        _toolCallNames.Clear();
        _streamBubbleManager.Clear();
        AddHeader();
    }

    /// <summary>
    /// 恢复消息到对话面板。
    /// </summary>
    /// <param name="messages">消息数组。</param>
    public void RestoreMessages(ChatMessage[] messages)
    {
        _messageRestorer.Restore(messages);
    }

    /// <summary>
    /// 添加中断消息。
    /// </summary>
    private void AddInterruptMessage()
    {
        _streamBubbleManager.Clear();

        var interruptBlock = new TextBlock("⛔ 中断输出")
            .TextAlignment(TextAlignment.Left)
            .HorizontalAlignment(Align.Stretch)
            .Style(
                new TextBlockStyle
                {
                    Foreground = _colors.InterruptBubbleFg,
                    Background = _colors.InterruptBubbleBg,
                    FillBackground = true,
                }
            );

        Flow.Items.Add(
            new DocumentFlowItem
            {
                Content = new FlowDocument().Add(interruptBlock),
                Alignment = DocumentFlowAlignment.Stretch,
                BackgroundStyle = Style
                    .None.WithBackground(_colors.InterruptBubbleBg)
                    .WithForeground(_colors.InterruptBubbleFg),
                Padding = new Thickness(3, 1, 3, 1),
            }
        );
    }

    /// <summary>
    /// 添加 LLM 错误消息气泡。直接展示 LLM API 原始错误消息，不翻译。
    /// </summary>
    /// <param name="rawMessage">LLM API 原始错误消息。</param>
    /// <param name="httpStatusCode">HTTP 状态码（null = 非 HTTP 错误）。</param>
    /// <param name="isContextOverflow">是否为上下文溢出错误。</param>
    /// <param name="recoverable">是否可自动恢复。</param>
    private void AddErrorMessage(
        string rawMessage,
        int? httpStatusCode,
        bool isContextOverflow,
        bool recoverable
    )
    {
        _streamBubbleManager.Clear();

        var icon = recoverable ? "❗" : "❌";
        var statusHint = httpStatusCode.HasValue ? $" (HTTP {httpStatusCode})" : "";
        var errorBlock = new TextBlock($"{icon}{statusHint} {rawMessage}")
            .TextAlignment(TextAlignment.Left)
            .Wrap(true)
            .HorizontalAlignment(Align.Stretch)
            .Style(
                new TextBlockStyle
                {
                    Foreground = _colors.ErrorBubbleFg,
                    Background = _colors.ErrorBubbleBg,
                    FillBackground = true,
                }
            );

        Flow.Items.Add(
            new DocumentFlowItem
            {
                Content = new FlowDocument().Add(errorBlock),
                Alignment = DocumentFlowAlignment.Stretch,
                BackgroundStyle = Style
                    .None.WithBackground(_colors.ErrorBubbleBg)
                    .WithForeground(_colors.ErrorBubbleFg),
                Padding = new Thickness(3, 1, 3, 1),
            }
        );
    }

    /// <summary>
    /// 添加系统消息。
    /// </summary>
    /// <param name="text">消息文本。</param>
    public void AddSystemMessage(string text)
    {
        var systemBlock = new TextBlock(text)
            .TextAlignment(TextAlignment.Left)
            .Wrap(true)
            .HorizontalAlignment(Align.Stretch)
            .Style(
                new TextBlockStyle
                {
                    Foreground = _colors.ThinkingBubbleFg,
                    Background = _colors.ThinkingBubbleBg,
                    FillBackground = true,
                }
            );

        Flow.Items.Add(
            new DocumentFlowItem
            {
                Content = new FlowDocument().Add(systemBlock),
                Alignment = DocumentFlowAlignment.Stretch,
                BackgroundStyle = Style
                    .None.WithBackground(_colors.ThinkingBubbleBg)
                    .WithForeground(_colors.ThinkingBubbleFg),
                Padding = new Thickness(3, 1, 3, 1),
            }
        );
    }

    /// <summary>
    /// 添加 Waiting 占位气泡 — 用户运行中输入时立即显示。
    /// 不调用 _streamBubbleManager.Clear()，不破坏正在流式的 assistant。
    /// </summary>
    /// <param name="text">消息文本。</param>
    /// <param name="steerId">Steer 标识符，用于后续转正时匹配删除。</param>
    public void AddWaitingUserMessage(string text, string steerId)
    {
        // ① 不调 _streamBubbleManager.Clear() — 关键约束 C1

        // ② 构建 waiting 气泡
        var waitingBlock = new TextBlock($"⏳ {text}")
            .TextAlignment(TextAlignment.Left)
            .Wrap(true)
            .HorizontalAlignment(Align.Stretch)
            .Style(
                new TextBlockStyle
                {
                    Foreground = _colors.ThinkingBubbleFg,
                    Background = _colors.ThinkingBubbleBg,
                    FillBackground = true,
                }
            );

        var item = new DocumentFlowItem
        {
            Content = new FlowDocument().Add(waitingBlock),
            Alignment = DocumentFlowAlignment.Stretch,
            BackgroundStyle = Style
                .None.WithBackground(_colors.ThinkingBubbleBg)
                .WithForeground(_colors.ThinkingBubbleFg),
            Padding = new Thickness(3, 1, 3, 1),
        };

        Flow.Items.Add(item);

        // ③ 追踪：存索引 + FIFO 队列
        _steerWaitingIndices[steerId] = Flow.Items.Count - 1;
        _steerWaitingIds.Enqueue(steerId);
    }

    /// <summary>
    /// 按 steerId 查找并删除 Flow.Items 中的 waiting 占位。
    /// 使用索引追踪，删除后修正所有 >idx 的索引（含 _toolCallItemIndices 和 _steerWaitingIndices）。
    /// </summary>
    /// <param name="steerId">Steer 标识符。</param>
    /// <returns>是否成功删除。</returns>
    private bool RemoveWaitingItem(string steerId)
    {
        if (!_steerWaitingIndices.Remove(steerId, out var idx))
            return false;

        if (idx < 0 || idx >= Flow.Items.Count)
            return false;

        Flow.Items.RemoveAt(idx);

        // 修正所有 >idx 的索引（RemoveAt 导致后续项前移 1 位）
        AdjustIndicesAfterRemoval(idx);
        return true;
    }

    /// <summary>
    /// 删除索引 removedIdx 处的项后，修正所有 >removedIdx 的索引。
    /// 同时修正 _toolCallItemIndices 和 _steerWaitingIndices。
    /// </summary>
    /// <param name="removedIdx">被删除项的索引。</param>
    private void AdjustIndicesAfterRemoval(int removedIdx)
    {
        // 修正 _toolCallItemIndices
        foreach (
            var key in _toolCallItemIndices.Keys.Where(key =>
                _toolCallItemIndices[key] > removedIdx
            )
        )
        {
            _toolCallItemIndices[key]--;
        }

        // 修正 _steerWaitingIndices
        foreach (
            var key in _steerWaitingIndices.Keys.Where(key =>
                _steerWaitingIndices[key] > removedIdx
            )
        )
        {
            _steerWaitingIndices[key]--;
        }
    }

    /// <summary>
    /// 清除所有 Waiting 占位气泡 — 中断时调用。
    /// 同时清空内部追踪队列。
    /// 逆序删除避免索引偏移问题。
    /// </summary>
    public void ClearAllWaiting()
    {
        if (_steerWaitingIndices.Count == 0)
            return;

        // 收集所有索引并按降序排列，从后往前删避免偏移
        var indices = _steerWaitingIndices
            .Values.Where(i => i >= 0 && i < Flow.Items.Count)
            .OrderByDescending(i => i)
            .ToList();

        foreach (var idx in indices)
        {
            Flow.Items.RemoveAt(idx);
        }

        // 修正 _toolCallItemIndices：所有 >最小被删索引 的索引减去删除数量
        if (indices.Count > 0)
        {
            var minRemoved = indices.Min();
            var removeCount = indices.Count;
            foreach (var key in _toolCallItemIndices.Keys)
            {
                if (_toolCallItemIndices[key] >= minRemoved + removeCount)
                    _toolCallItemIndices[key] -= removeCount;
                else if (_toolCallItemIndices[key] >= minRemoved)
                {
                    // 在被删区间内 — 不应发生（waiting item 不与 tool call 重叠）
                    // 防御性：保持不变
                }
            }
        }

        _steerWaitingIds.Clear();
        _steerWaitingIndices.Clear();
    }
}
