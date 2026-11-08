using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Desktop.Controls;
using Desktop.Models;
using Desktop.ViewModels;
using ReactiveUI.Primitives;

namespace Desktop.Views;

public partial class ConversationView : UserControl
{
    public ConversationView()
    {
        InitializeComponent();
        MessageList.ItemsSource = _visibleMessages;
        MessageList.ScrolledToTop += MessageList_OnScrolledToTop;
        // Enter 必须在 TextBox 处理 AcceptsReturn 之前拦截（隧道），
        // 否则 TextBox 会先插入换行、把 KeyDown 标记为已处理。
        AddHandler(KeyDownEvent, InputBox_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // Avalonia 12：拖放事件通过附加事件路由注册
        AddHandler(DragDrop.DragOverEvent, InputBox_OnDragOver);
        AddHandler(DragDrop.DropEvent, InputBox_OnDrop);

        // 补全弹窗：点击条目直接插入（对齐 TUI 鼠标点击语义；纯键盘导航仍可用）
        SuggestionList.PointerReleased += SuggestionList_OnPointerReleased;
        CommandList.PointerReleased += CommandList_OnPointerReleased;
        // 键盘导航时让选中项保持可见（长列表不丢失视野）
        SuggestionList.SelectionChanged += (_, _) => ScrollSelectionIntoView(SuggestionList);
        CommandList.SelectionChanged += (_, _) => ScrollSelectionIntoView(CommandList);
        // 弹窗打开时列表可能尚未生成容器，SelectedIndex 绑定会停在 -1。
        // 打开后显式选中首项，保证 / 与 # 弹窗默认高亮第一行。
        SuggestionPopup.Opened += (_, _) => SelectFirstSuggestion();
        CommandPopup.Opened += (_, _) => SelectFirstCommand();
        // 会话切换 / /new 后数据上下文换新 VM：先重建窗口，再整页滑入。
        DataContextChanged += (_, _) =>
        {
            AttachMessageSource();
            if (!IsLoaded || DataContext is not ConversationViewModel)
                return;
            Dispatcher.UIThread.Post(
                () =>
                    _ = Desktop.Styles.Motion.PlayPageEnter(this, Desktop.Styles.Motion.PageShift),
                DispatcherPriority.Loaded
            );
        };
    }

    private ConversationViewModel? Vm => DataContext as ConversationViewModel;

    // ── 消息窗口：尾部约 3 屏 + 滚到顶部增量加载 ─────────────────────────

    /// <summary>
    /// 不使用 VirtualizingStackPanel：stock VSP 的 extent 只按当前 realized 窗口估算，
    /// 对话行高度差异极大时（10k 字用户气泡、长表格/工具结果），ScrollToEnd 触发的 realize
    /// 会让估算在 30k↔67k px 间来回，offset 被 anchor/coerce 反复拉回，滚动条无限上下跳。
    /// 这里改回精确测量的 StackPanel + 像素预算窗口，窗口内 Extent 是真实高度。
    /// </summary>
    private const int MaxWindowMessages = 40;

    private const int MinWindowMessages = 4;
    private const int MaxPageMessages = 20;
    private const double WindowViewports = 3.0;
    private const double PageViewports = 1.5;
    private const double FallbackViewport = 600;

    private readonly ObservableCollection<ChatMessage> _visibleMessages = [];
    private ConversationViewModel? _messagesVm;
    private int _windowStart;
    private bool _windowExpanded;
    private bool _bulkRestore;
    private bool _refreshScheduled;
    private bool _loadingEarlier;

    /// <summary>DataContext 换 VM：重挂 Messages.CollectionChanged，窗口先落到最新一页。</summary>
    private void AttachMessageSource()
    {
        var vm = DataContext as ConversationViewModel;
        if (ReferenceEquals(vm, _messagesVm))
            return;

        if (_messagesVm is not null)
            _messagesVm.Messages.CollectionChanged -= OnMessagesChanged;

        _messagesVm = vm;
        _bulkRestore = false;
        _loadingEarlier = false;
        if (_messagesVm is not null)
            _messagesVm.Messages.CollectionChanged += OnMessagesChanged;

        RebuildWindowToTail();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_messagesVm is null)
            return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                // Clear + 批量 Add 的会话恢复：等整批落完再一次性取尾部窗口。
                if (_bulkRestore)
                    return;
                AppendWindowMessages(e);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                RemoveWindowMessages(e);
                break;
            case NotifyCollectionChangedAction.Reset:
                _bulkRestore = true;
                ScheduleWindowRefresh();
                break;
            default:
                ScheduleWindowRefresh();
                break;
        }
    }

    private void AppendWindowMessages(NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null || _messagesVm is null)
            return;

        var windowEnd = _windowStart + _visibleMessages.Count;
        if (e.NewStartingIndex < windowEnd)
        {
            // 插到已渲染窗口之前/中间：整体重建，保证窗口始终是源集合的连续后缀。
            ScheduleWindowRefresh();
            return;
        }

        foreach (var item in e.NewItems)
        {
            if (item is ChatMessage message)
                _visibleMessages.Add(message);
        }

        // 用户回到底部后，下次追加恢复固定窗口，避免向上翻过的历史永久驻留。
        if (_windowExpanded && MessageList.IsFollowing)
            _windowExpanded = false;

        if (_windowExpanded || !MessageList.IsFollowing)
            return;

        TrimWindowToBudget();
    }

    private void RemoveWindowMessages(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is null)
            return;

        for (var i = 0; i < e.OldItems.Count; i++)
        {
            if (e.OldItems[i] is not ChatMessage message)
                continue;

            var visibleIndex = _visibleMessages.IndexOf(message);
            if (visibleIndex >= 0)
                _visibleMessages.RemoveAt(visibleIndex);
            else if (e.OldStartingIndex >= 0 && e.OldStartingIndex + i < _windowStart)
                _windowStart--;
        }
    }

    /// <summary>合并批量刷新：Reset / 非尾部变更时只重建一次窗口。</summary>
    private void ScheduleWindowRefresh()
    {
        if (_refreshScheduled)
            return;

        _refreshScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _refreshScheduled = false;
                _bulkRestore = false;
                RebuildWindowToTail();
            },
            DispatcherPriority.Background
        );
    }

    /// <summary>窗口重置为最新一页（会话恢复 / /new / 分支切换）。</summary>
    private void RebuildWindowToTail()
    {
        MessageList.ResetFollow();
        _windowExpanded = false;

        if (_messagesVm is null)
        {
            _windowStart = 0;
            _visibleMessages.Clear();
            return;
        }

        var total = _messagesVm.Messages.Count;
        var budget = ViewportHeight() * WindowViewports;
        var start = total;
        var height = 0d;
        var count = 0;
        while (
            start > 0 && count < MaxWindowMessages && (height < budget || count < MinWindowMessages)
        )
        {
            start--;
            height += EstimateMessageHeight(_messagesVm.Messages[start]);
            count++;
        }

        _windowStart = start;
        _visibleMessages.Clear();
        for (var i = _windowStart; i < total; i++)
            _visibleMessages.Add(_messagesVm.Messages[i]);
    }

    /// <summary>跟随尾部时维持像素预算：从头部丢弃多余行（底部视口不动）。</summary>
    private void TrimWindowToBudget()
    {
        var budget = ViewportHeight() * WindowViewports + ViewportHeight();
        var height = _visibleMessages.Sum(EstimateMessageHeight);
        while (
            _visibleMessages.Count > MinWindowMessages
            && (height > budget || _visibleMessages.Count > MaxWindowMessages)
        )
        {
            height -= EstimateMessageHeight(_visibleMessages[0]);
            _visibleMessages.RemoveAt(0);
            _windowStart++;
        }
    }

    /// <summary>用户滚到顶部：向前补一页；视口锚点按新增内容高度下移。</summary>
    private void MessageList_OnScrolledToTop()
    {
        if (_loadingEarlier || _messagesVm is null || _windowStart <= 0)
            return;

        var pageBudget = ViewportHeight() * PageViewports;
        var take = 0;
        var addedHeight = 0d;
        while (_windowStart - take > 0 && take < MaxPageMessages && addedHeight < pageBudget)
        {
            take++;
            addedHeight += EstimateMessageHeight(_messagesVm.Messages[_windowStart - take]);
        }

        if (take == 0)
            return;

        var scroll = MessageScroll;
        var beforeOffset = scroll.Offset.Y;
        var beforeExtent = scroll.Extent.Height;

        _loadingEarlier = true;
        _windowExpanded = true;
        _windowStart -= take;
        for (var i = 0; i < take; i++)
            _visibleMessages.Insert(i, _messagesVm.Messages[_windowStart + i]);

        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    var added = scroll.Extent.Height - beforeExtent;
                    if (added > 0.5)
                        scroll.Offset = scroll.Offset.WithY(beforeOffset + added);
                }
                finally
                {
                    _loadingEarlier = false;
                }
            },
            DispatcherPriority.Background
        );
    }

    /// <summary>
    /// 粗略高度预算（只用于决定窗口边界，不参与布局）：
    /// 按 16px/24 行高、约 70 字符/行估算正文，再给步骤/卡片留固定开销。
    /// </summary>
    private static double EstimateMessageHeight(ChatMessage message)
    {
        var chars = message.Content.ToString().Length;
        var height = 80 + Math.Max(0, chars) / 70.0 * 24;
        foreach (var step in message.AgentSteps)
        {
            height += 44;
            var thinking = step.ThinkingBuilder.ToString().Length;
            var result = step.FullResult?.Length ?? 0;
            var details = step.IsDetailsExpanded ? thinking + result : 0;
            height += details / 70.0 * 24;
        }
        return Math.Max(64, Math.Min(height, 20_000));
    }

    private double ViewportHeight()
    {
        var height = MessageScroll.Viewport.Height;
        if (height > 1)
            return height;
        height = MessageScroll.Bounds.Height;
        return height > 1 ? height : FallbackViewport;
    }

    /// <summary>展开/收起步骤详情（思考全文、完整参数与结果）。</summary>
    private void StepDetails_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AgentStep step })
            step.IsDetailsExpanded = !step.IsDetailsExpanded;
        e.Handled = true;
    }

    /// <summary>补全弹窗条目被点击：插入该项（仅当点击落在条目上,滚动条等不触发）。</summary>
    private void SuggestionList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Visual source)
            return;
        if (source.FindAncestorOfType<ListBoxItem>(true) is null)
            return;
        InsertSelectedSuggestion(vm);
    }

    /// <summary>命令弹窗条目被点击：插入该命令（仅当点击落在条目上）。</summary>
    private void CommandList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Visual source)
            return;
        if (source.FindAncestorOfType<ListBoxItem>(true) is null)
            return;
        InsertSelectedCommand(vm);
    }

    /// <summary>键盘导航时滚动到选中项（长列表保持可见）。</summary>
    private static void ScrollSelectionIntoView(ListBox list)
    {
        var index = list.SelectedIndex;
        if (index >= 0)
            list.ScrollIntoView(index);
    }

    /// <summary>弹窗打开后默认高亮首项（列表容器此时才完成生成）。</summary>
    private void SelectFirstSuggestion()
    {
        var vm = Vm;
        if (vm is null || vm.Suggestions.Count == 0)
            return;
        if (vm.SelectedSuggestionIndex != 0)
            vm.SelectedSuggestionIndex = 0;
        SuggestionList.SelectedIndex = 0;
        SuggestionList.ScrollIntoView(0);
    }

    /// <summary>命令弹窗打开后默认高亮首个命令。</summary>
    private void SelectFirstCommand()
    {
        var vm = Vm;
        if (vm is null || vm.CommandSuggestions.Count == 0)
            return;
        if (vm.SelectedCommandIndex != 0)
            vm.SelectedCommandIndex = 0;
        CommandList.SelectedIndex = 0;
        CommandList.ScrollIntoView(0);
    }

    /// <summary>空状态快捷操作：预填输入框（发送由用户确认）。</summary>
    private void QuickAction_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action } && Vm is { } vm)
        {
            var prompt = action switch
            {
                "写一章" => "请根据当前设定写一章新内容，注意承接上一章情节，保持文风一致。",
                "建角色" => "请创建一个新角色，包含名字、性别、性格、外貌、背景与动机。",
                "埋伏笔" => "请为当前主线埋一个可回收的伏笔，并记录到情节线中。",
                "查设定" => "请检索并整理当前小说的主要设定（角色、地点、情节线）。",
                "续写" => "请续写当前章节，保持文风与设定一致，自然推进情节。",
                _ => action,
            };
            vm.AppendInput(prompt); // 仅预填，发送由用户按 Enter 确认
            InputBox.Focus();
        }
    }

    // ── 输入区按键：Enter 发送 / Shift+Enter 换行 / Esc 中断 / @ 弹窗导航 ──

    /// <summary>
    /// 隧道阶段拦截 Enter：TextBox.AcceptsReturn=true 时，类处理器会先插入换行，
    /// 冒泡的 KeyDown 再也收不到。这里先于 TextBox 处理，保证 Enter 发送、Shift+Enter 换行。
    /// </summary>
    private void InputBox_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
            return;

        // 中文输入法组合中（拼音未上屏）方向键属于候选词导航、Enter 属于确认，不能抢
        if (
            InputBox
                .GetVisualDescendants()
                .OfType<TextPresenter>()
                .Any(p => !string.IsNullOrEmpty(p.PreeditText))
        )
            return;

        // 弹窗导航必须在 TextBox 自己的 KeyDown 之前消费；否则多行输入会把
        // Up/Down 当成光标移动，弹窗永远收不到。
        if (TryHandlePopupKey(vm, e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || !InputBox.IsFocused)
            return;

        // 仅无修饰键发送；Shift/Ctrl/Alt+Enter 留给 TextBox 插入换行
        if (e.KeyModifiers != KeyModifiers.None)
            return;

        // 处理中且没有待发变体：不发送，也不让 TextBox 插入空换行
        if (vm.IsProcessing && !vm.HasPendingRefinement)
        {
            e.Handled = true;
            return;
        }

        // 发送后回到最新内容：用户可能刚在历史里翻看，回车发送应看到自己刚发的消息，
        // 并让本轮流式输出继续跟随到底部。
        MessageList.ResetFollow();
        vm.SendMessageCommand.Execute().Subscribe();
        e.Handled = true;
    }

    /// <summary>
    /// 弹窗打开时消费 Enter / Up / Down / Tab / Esc。
    /// 返回 true 表示按键已归弹窗所有。
    /// </summary>
    private bool TryHandlePopupKey(ConversationViewModel vm, KeyEventArgs e)
    {
        if (vm.IsCommandPopupVisible)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (vm.CommandSuggestions.Count > 0)
                        vm.SelectedCommandIndex = Math.Min(
                            vm.SelectedCommandIndex + 1,
                            vm.CommandSuggestions.Count - 1
                        );
                    return true;
                case Key.Up:
                    if (vm.CommandSuggestions.Count > 0)
                        vm.SelectedCommandIndex = Math.Max(vm.SelectedCommandIndex - 1, 0);
                    return true;
                case Key.Tab:
                case Key.Enter:
                    InsertSelectedCommand(vm);
                    return true;
                case Key.Escape:
                    vm.IsCommandPopupVisible = false;
                    return true;
            }
        }

        if (vm.IsSuggestionPopupVisible)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (vm.Suggestions.Count > 0)
                        vm.SelectedSuggestionIndex = Math.Min(
                            vm.SelectedSuggestionIndex + 1,
                            vm.Suggestions.Count - 1
                        );
                    return true;
                case Key.Up:
                    if (vm.Suggestions.Count > 0)
                        vm.SelectedSuggestionIndex = Math.Max(vm.SelectedSuggestionIndex - 1, 0);
                    return true;
                case Key.Tab:
                case Key.Enter:
                    InsertSelectedSuggestion(vm);
                    return true;
                case Key.Escape:
                    vm.IsSuggestionPopupVisible = false;
                    return true;
            }
        }

        return false;
    }

    private void InputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
            return;

        // 弹窗内的导航键已在隧道处理器中消费；这里只处理无弹窗时的历史/中断。

        // 输入历史导航：无弹窗 + 无多行 + 仅方向键（对齐 TUI 上下键历史）
        if (e.Key is Key.Up or Key.Down && InputBox.Text?.Contains('\n') != true)
        {
            if (InputBox.CaretIndex == 0 || e.Key == Key.Up)
            {
                var text = e.Key == Key.Up ? vm.NavigateHistoryUp() : vm.NavigateHistoryDown();
                if (text is not null)
                {
                    vm.SetHistoryDraft(InputBox.Text ?? "");
                    InputBox.Text = text;
                    InputBox.CaretIndex = text.Length;
                }
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.IsProcessing)
                    vm.StopProcessingCommand.Execute().Subscribe();
                e.Handled = true;
                break;
        }
    }

    private void InputBox_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        RefreshSuggestions();

    /// <summary>按当前文本与光标重算补全弹窗（TextChanged 与程序化插入后共用）。</summary>
    private void RefreshSuggestions()
    {
        var vm = Vm;
        if (vm is null)
            return;

        var text = InputBox.Text ?? "";
        var caret = InputBox.CaretIndex;

        // 当前光标 token 优先：# / @ / slash。斜杠命令不能吞掉正在输入的 @path；
        // 例如 "/new @src/Desktop/" 应展示文件补全，而不是静默执行 /new 丢弃参数。
        var entityRef = EntityRefParser.TryParse(text, caret);
        if (entityRef.Stage != EntityRefParser.Stage.None)
        {
            vm.IsCommandPopupVisible = false;
            _ = vm.UpdateEntityRefSuggestionsAsync(entityRef);
            return;
        }

        var mention = MentionParser.TryGetMention(text, caret);
        if (mention.Found)
        {
            vm.IsCommandPopupVisible = false;
            _ = vm.UpdateMentionSuggestionsAsync(mention.Query);
            return;
        }

        var trimmed = text.TrimStart();
        var isCommand =
            (trimmed.StartsWith('/') || trimmed.StartsWith(':')) && !text.Contains('\n');
        if (isCommand)
        {
            vm.IsSuggestionPopupVisible = false;
            vm.UpdateCommandSuggestions(text);
            return;
        }

        // 非命令、非 @/#：关闭两个补全弹窗。
        vm.IsCommandPopupVisible = false;
        vm.IsSuggestionPopupVisible = false;
    }

    /// <summary>插入选中命令到输入框（按 VM 给出的替换区间；命令级补空格、/tools 插件级补冒号）。</summary>
    private void InsertSelectedCommand(ConversationViewModel vm)
    {
        var text = InputBox.Text ?? "";
        if (vm.CommandSuggestions.Count == 0)
        {
            vm.IsCommandPopupVisible = false;
            return;
        }

        var index = Math.Clamp(vm.SelectedCommandIndex, 0, vm.CommandSuggestions.Count - 1);
        var command = vm.CommandSuggestions[index];
        var start = Math.Clamp(vm.CommandReplaceStart, 0, text.Length);
        var length = Math.Clamp(vm.CommandReplaceLength, 0, text.Length - start);
        var insert = command + vm.CommandSuffix;
        InputBox.Text = text[..start] + insert + text[(start + length)..];
        InputBox.CaretIndex = start + insert.Length;
        vm.IsCommandPopupVisible = false;
        InputBox.Focus();
        // 级联补全：/tools → 插件、/tools 插件: → 工具；其余命令选中后自然关闭。
        RefreshSuggestions();
    }

    /// <summary>插入选中建议到输入框（# 一级 → "#类型:"；# 二级 → "#类型:名称 "；@ → "@路径"，目录尾带 / 继续导航）。</summary>
    private void InsertSelectedSuggestion(ConversationViewModel vm)
    {
        var text = InputBox.Text ?? "";
        var caret = InputBox.CaretIndex;
        var index = Math.Clamp(
            vm.SelectedSuggestionIndex,
            0,
            Math.Max(vm.Suggestions.Count - 1, 0)
        );

        var entityRef = EntityRefParser.TryParse(text, caret);
        if (entityRef.Stage != EntityRefParser.Stage.None)
        {
            if (vm.Suggestions.Count == 0)
            {
                vm.IsSuggestionPopupVisible = false;
                return;
            }

            var picked = vm.Suggestions[index].Name;
            var applied =
                entityRef.Stage == EntityRefParser.Stage.Type
                    ? EntityRefParser.InsertType(text, caret, entityRef, picked) // 一级 → 随后进入二级
                    : EntityRefParser.InsertName(text, caret, entityRef, picked); // 二级 → 随后关闭
            InputBox.Text = applied.Text;
            InputBox.CaretIndex = applied.Caret;
            InputBox.Focus();
            RefreshSuggestions();
            return;
        }

        var mention = MentionParser.TryGetMention(text, caret);
        if (!mention.Found || vm.Suggestions.Count == 0)
        {
            vm.IsSuggestionPopupVisible = false;
            return;
        }

        var suggestion = vm.Suggestions[index];
        var (newText, newCaret) = MentionParser.InsertSuggestion(
            text,
            caret,
            mention.Start,
            suggestion.Name
        );
        InputBox.Text = newText;
        InputBox.CaretIndex = newCaret;
        // 目录候选尾带 / → 继续展开下一级；文件候选补空格关闭弹窗
        vm.IsSuggestionPopupVisible = false;
        InputBox.Focus();
        if (suggestion.Name.EndsWith('/'))
            RefreshSuggestions(); // 目录 → 下一级导航
    }

    // ── 拖放：把文件/目录路径插入输入框（@ 路径语义，与补全插入格式一致） ──

    private void InputBox_OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void InputBox_OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e
            .DataTransfer.GetItems(DataFormat.File)
            .Select(item => item.TryGetRaw(DataFormat.File))
            .OfType<Avalonia.Platform.Storage.IStorageItem>()
            .Select(item => item.Path.LocalPath)
            .ToArray();
        if (paths.Length == 0)
            return;

        var text = InputBox.Text ?? "";
        var insert = string.Join("\n", paths.Select(p => "@" + p));
        var caret = InputBox.CaretIndex;
        InputBox.Text = text.Insert(caret, insert + "\n");
        InputBox.CaretIndex = caret + insert.Length + 1;
        InputBox.Focus();
    }
}
