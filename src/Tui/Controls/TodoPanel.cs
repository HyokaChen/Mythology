using System.Text;
using Core.Shared;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Threading;

namespace Tui.Controls;

/// <summary>
/// 任务面板 — 实时展示 todo_write 建立的任务清单（⬜ 待办 / 🔄 进行中 / ✅ 完成）。
/// 位于右栏 SetUpPanel 下方：上=内容世界（实体设定），下=执行过程（任务计划）。
/// 订阅 <see cref="TodoStore.Changed"/> 事件刷新；变更可能来自 Agent 后台线程，
/// 通过 Dispatcher 调度回 UI 线程后重建 Markup。
///
/// 折叠行为（Collapsible）：
/// - 无任务：自动收起，仅标题行「📋 暂无任务」
/// - 任务出现：若此前因无任务自动收起，则自动展开
/// - 有任务时用户手动折叠/展开：跨刷新保留（Refresh 不干预 IsExpanded）
/// </summary>
public sealed class TodoPanel : IDisposable
{
    private readonly TodoStore _store;
    private readonly Collapsible _collapsible;
    private readonly Visual _emptyState; // 空态：整个区域居中的 EmptyStatus 占位（Center 控件）
    private readonly State<string?> _headerText = new("📋 暂无任务");
    private readonly State<string?> _listText = new("");
    private readonly BackgroundPanel _panel;

    private bool _agentRunning;
    private bool _autoCollapsed = true; // 无任务自动收起标记：任务出现时自动展开一次

    /// <summary>根视觉元素。</summary>
    public Visual Visual => _panel;

    // ── 测试缝（internal，仅 Tui.Tests 可见） ──

    internal bool IsExpanded => _collapsible.IsExpanded;
    internal string HeaderText => _headerText.Value ?? "";
    internal string BodyText => _listText.Value ?? "";
    internal Visual? VisualContent => _panel.Content;

    /// <summary>
    /// 构造函数：订阅 TodoStore.Changed，立即渲染当前快照。
    /// </summary>
    public TodoPanel(TodoStore store, PanelColors colors)
    {
        _store = store;

        // header 仅放非 focusable Markup（规避 Collapsible Preview 捕获 + HasFocusWithin
        // 跟踪崩溃——该 bug 由 focusable Button 触发，SetUpPanel 有同源注释）
        var headerMarkup = new Markup(_headerText) { IsSelectable = false };
        _collapsible = new Collapsible(
            headerMarkup,
            new ScrollViewer
            {
                Content = new Markup(_listText)
                {
                    // 左侧缩进 2 列 = glyph(▸/▾ 1 列) + SpaceBetweenGlyphAndHeader(1)：
                    // 与 SetUpPanel 折叠项标题文字的左边距对齐，列表不贴面板左缘
                    Margin = new Thickness(2, 0, 0, 0),
                }.Wrap(true),
                // 内容区填满整个面板（去除 MaxHeight=12 封顶）：任务列表占满 35% 区域，
                // 列表超出时可滚动——消除「Collapsible 只占几行、面板下方大块空白」的布局怪象
                VerticalAlignment = Align.Stretch,
            }
        )
        {
            IsExpanded = false,
            VerticalAlignment = Align.Stretch, // 面板 35% 高度：Collapsible 撑满
            Margin = new Thickness(0, 1, 0, 0), // 顶部呼吸感：与上方分隔线/SetUpPanel 之间留 1 行
        }.Style(BuildCollapsibleStyle(colors));

        // 空态：整个区域水平+垂直居中的 EmptyStatus（Center 控件）
        _emptyState = new Center
        {
            Content = new Markup("[dim]📋 暂无任务[/]"),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        // 背景 = SidePanelBg（#5B3A29 deep brown）：与上方 SetUpPanel 面板完全同色——
        // 用户反馈「与 SetUpPanel 差异太大」：不再用低一档暖棕（#3E2C22 亮度断层明显），
        // 两面板统一 Desert 深棕；分层完全由 header 标题条（TabSelectedBg 亮一档）
        // + ThinSplitter 1px 分隔线完成。前景 = SidePanelFg（与 SetUpPanel 同）。
        _panel = new BackgroundPanel
        {
            FillStyle = Style
                .None.WithBackground(colors.SidePanelBg)
                .WithForeground(colors.SidePanelFg),
            Content = _collapsible,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        _store.Changed += OnStoreChanged;
        Refresh();
    }

    /// <summary>
    /// Collapsible 样式：标题条底 = TabSelectedBg（与 SetUpPanel 选中 tab 同色——
    /// 「当前工作区」语义），亮沙 bold 前景；hover/focus 前景 = warm copper
    /// （AccentBlue #D68A48，Desert 主强调色）；箭头字形沿用 ▸/▾。
    /// </summary>
    private static CollapsibleStyle BuildCollapsibleStyle(PanelColors colors) =>
        CollapsibleStyle.Default with
        {
            ExpandedGlyph = new Rune('▾'),
            CollapsedGlyph = new Rune('▸'),
            SpaceBetweenGlyphAndHeader = 1,
            ContentSpacing = 0,
            Header =
                Style.None.WithBackground(colors.TabSelectedBg).WithForeground(colors.TabSelectedFg)
                | TextStyle.Bold,
            HeaderHovered =
                Style.None.WithBackground(colors.TabHoveredBg).WithForeground(colors.AccentBlue)
                | TextStyle.Bold,
            HeaderFocused =
                Style.None.WithBackground(colors.TabHoveredBg).WithForeground(colors.AccentBlue)
                | TextStyle.Bold,
        };

    private void OnStoreChanged()
    {
        // Store 变更可能来自 Agent 工具执行线程，调度回 UI 线程刷新。
        try
        {
            Dispatcher.Current.Post(Refresh);
        }
        catch (InvalidOperationException)
        {
            // 未附着运行中的 TerminalApp（单元测试 / headless 场景）：直接刷新。
            Refresh();
        }
    }

    /// <summary>
    /// 运行态感知：Agent 运行时 in_progress 项高亮显示（bold yellow，与 SetUpPanel 高亮约定一致）。
    /// </summary>
    public void SetAgentRunning(bool running)
    {
        if (_agentRunning == running)
            return;
        _agentRunning = running;
        Refresh();
    }

    /// <summary>重建任务清单 Markup。</summary>
    public void Refresh()
    {
        // Deleted 过滤：status=deleted 而非 delete 的任务不渲染幽灵行（§10.8 F8）
        var items = _store.List().Where(i => i.Status != TodoStatus.Deleted).ToList();
        var idToSubject = items.ToDictionary(i => i.Id, i => i.Subject);

        // 空态：整块区域切换为居中的 EmptyStatus 占位（Center 控件，水平+垂直居中）
        if (items.Count == 0)
        {
            _autoCollapsed = true;
            _panel.Content = _emptyState;
            _listText.Value = "";
            return;
        }

        // 任务出现：若此前无任务（空态占位），恢复 Collapsible 并自动展开（用户手动折叠不干预）
        if (_autoCollapsed)
        {
            _autoCollapsed = false;
            _collapsible.IsExpanded = true;
        }
        _panel.Content = _collapsible;

        // 有任务：标题/列表左对齐——列表缩进 2 列，同 SetUpPanel 折叠项标题左边距
        _headerText.Value = FormatHeader(items, _agentRunning);

        var sb = new StringBuilder();
        // 已完成沉底（OrderBy bool：false 在前）
        foreach (var item in items.OrderBy(i => i.Status == TodoStatus.Completed))
            sb.AppendLine(FormatItem(item, idToSubject, _agentRunning));
        _listText.Value = sb.ToString();
    }

    /// <summary>
    /// 标题栏格式：📋 任务 x/y；Agent 运行时追加当前 in_progress 任务（🔄 #id subject）。
    /// </summary>
    internal static string FormatHeader(IReadOnlyList<TodoItem> items, bool agentRunning)
    {
        var completed = items.Count(i => i.Status == TodoStatus.Completed);
        var inProgress = agentRunning
            ? items.FirstOrDefault(i => i.Status == TodoStatus.InProgress)
            : null;
        var header = inProgress is not null
            ? $"📋 任务 {completed}/{items.Count} · 🔄 #{inProgress.Id} {ToolCallRenderer.Escape(inProgress.Subject)}"
            : $"📋 任务 {completed}/{items.Count}";
        return $"[bold]{header}[/]";
    }

    /// <summary>
    /// 单行任务格式：icon #id subject (activeForm) 🚧 依赖。
    /// 运行中 in_progress 高亮；已完成 dim 且沉底；依赖解析为任务文本。
    /// </summary>
    internal static string FormatItem(
        TodoItem item,
        IReadOnlyDictionary<int, string> idToSubject,
        bool agentRunning
    )
    {
        var icon = item.Status switch
        {
            TodoStatus.Pending => "⬜",
            TodoStatus.InProgress => "🔄",
            TodoStatus.Completed => "✅",
            _ => "❌",
        };

        var subject = item.Status switch
        {
            TodoStatus.Completed => $"[dim]{ToolCallRenderer.Escape(item.Subject)}[/]",
            TodoStatus.InProgress when agentRunning =>
                $"[bold yellow]{ToolCallRenderer.Escape(item.Subject)}[/]",
            TodoStatus.InProgress => $"[bold]{ToolCallRenderer.Escape(item.Subject)}[/]",
            _ => ToolCallRenderer.Escape(item.Subject),
        };

        var activeStr =
            item.Status == TodoStatus.InProgress && !string.IsNullOrEmpty(item.ActiveForm)
                ? $" [dim]({ToolCallRenderer.Escape(item.ActiveForm)})[/]"
                : "";

        return $"{icon} #{item.Id} {subject}{activeStr}{FormatBlockedBy(item, idToSubject)}";
    }

    /// <summary>
    /// 依赖链解析：blockedBy 裸 ID → 任务文本（🚧 #3 写大纲）；依赖已删除时降级为裸 ID。
    /// </summary>
    private static string FormatBlockedBy(
        TodoItem item,
        IReadOnlyDictionary<int, string> idToSubject
    )
    {
        if (item.BlockedBy.Length == 0)
            return "";
        var parts = item.BlockedBy.Select(b =>
            int.TryParse(b, out var bid) && idToSubject.TryGetValue(bid, out var subject)
                ? $"#{bid} {subject}"
                : $"#{b}"
        );
        return $" [dim]🚧 {string.Join(", ", parts)}[/]";
    }

    /// <summary>应用主题颜色。</summary>
    public void ApplyTheme(PanelColors colors)
    {
        // 与构造函数一致：背景 = SidePanelBg（与 SetUpPanel 同色统一）
        _panel.FillStyle = Style
            .None.WithBackground(colors.SidePanelBg)
            .WithForeground(colors.SidePanelFg);
        _collapsible.Style(BuildCollapsibleStyle(colors));
        Refresh();
    }

    /// <summary>取消事件订阅。</summary>
    public void Dispose() => _store.Changed -= OnStoreChanged;
}
