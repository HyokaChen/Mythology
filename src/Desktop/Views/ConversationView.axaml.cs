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
        // 会话切换 / /new 后数据上下文换新 VM：整页滑入，避免无反馈的硬切。
        DataContextChanged += (_, _) =>
        {
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

        var trimmed = text.TrimStart();
        var isCommand =
            (trimmed.StartsWith('/') || trimmed.StartsWith(':')) && !text.Contains('\n');
        if (isCommand)
        {
            // 斜杠/冒号命令补全优先于 @ / # 补全
            vm.IsSuggestionPopupVisible = false;
            vm.UpdateCommandSuggestions(text);
            return;
        }

        // 关闭命令补全（非斜杠输入）
        vm.IsCommandPopupVisible = false;

        // # 实体引用优先（与 TUI TriggerCompletion 的判定顺序一致）
        var entityRef = EntityRefParser.TryParse(text, caret);
        if (entityRef.Stage != EntityRefParser.Stage.None)
        {
            _ = vm.UpdateEntityRefSuggestionsAsync(entityRef);
            return;
        }

        var mention = MentionParser.TryGetMention(text, caret);
        if (!mention.Found)
        {
            vm.IsSuggestionPopupVisible = false;
            return;
        }

        _ = vm.UpdateMentionSuggestionsAsync(mention.Query);
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
