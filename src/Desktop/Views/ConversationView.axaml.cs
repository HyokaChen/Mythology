using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Desktop.Controls;
using Desktop.ViewModels;
using ReactiveUI.Primitives;

namespace Desktop.Views;

public partial class ConversationView : UserControl
{
    public ConversationView()
    {
        InitializeComponent();
        // Avalonia 12：拖放事件通过附加事件路由注册
        AddHandler(DragDrop.DragOverEvent, InputBox_OnDragOver);
        AddHandler(DragDrop.DropEvent, InputBox_OnDrop);

        // 补全弹窗：点击条目直接插入（对齐 TUI 鼠标点击语义；纯键盘导航仍可用）
        SuggestionList.PointerReleased += SuggestionList_OnPointerReleased;
        CommandList.PointerReleased += CommandList_OnPointerReleased;
        // 键盘导航时让选中项保持可见（长列表不丢失视野）
        SuggestionList.SelectionChanged += (_, _) => ScrollSelectionIntoView(SuggestionList);
        CommandList.SelectionChanged += (_, _) => ScrollSelectionIntoView(CommandList);
    }

    private ConversationViewModel? Vm => DataContext as ConversationViewModel;

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

    private void InputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
            return;

        // 斜杠命令补全弹窗打开时：方向键 / 回车 / Tab / Esc 由弹窗消费
        if (vm.IsCommandPopupVisible)
        {
            switch (e.Key)
            {
                case Key.Down:
                    vm.SelectedCommandIndex = Math.Min(
                        vm.SelectedCommandIndex + 1,
                        vm.CommandSuggestions.Count - 1
                    );
                    e.Handled = true;
                    return;
                case Key.Up:
                    vm.SelectedCommandIndex = Math.Max(vm.SelectedCommandIndex - 1, 0);
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.Tab:
                    InsertSelectedCommand(vm);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    vm.IsCommandPopupVisible = false;
                    e.Handled = true;
                    return;
            }
        }

        // 输入补全弹窗打开时：方向键 / 回车 / Tab / Esc 由弹窗消费
        if (vm.IsSuggestionPopupVisible)
        {
            switch (e.Key)
            {
                case Key.Down:
                    vm.SelectedSuggestionIndex = Math.Min(
                        vm.SelectedSuggestionIndex + 1,
                        vm.Suggestions.Count - 1
                    );
                    e.Handled = true;
                    return;
                case Key.Up:
                    vm.SelectedSuggestionIndex = Math.Max(vm.SelectedSuggestionIndex - 1, 0);
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.Tab:
                    InsertSelectedSuggestion(vm);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    vm.IsSuggestionPopupVisible = false;
                    e.Handled = true;
                    return;
            }
        }

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
            case Key.Enter
                when e.KeyModifiers == KeyModifiers.None
                    && (!vm.IsProcessing || vm.HasPendingRefinement):
                // 命令弹窗打开时 Enter 选择命令（已在上方处理），此处仅处理普通输入；
                // 变体修改要求待发时即便 Agent 处理中也放行（投递 SteeringQueue 排队）
                vm.SendMessageCommand.Execute().Subscribe();
                e.Handled = true;
                break;
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

        var isSlash = text.TrimStart().StartsWith('/') && !text.Contains('\n');
        if (isSlash)
        {
            // 斜杠命令补全优先于 @ / # 补全
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

    /// <summary>插入选中命令到输入框（替换 /query）。</summary>
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
        // 替换当前 /前缀（直到空格或行尾）
        var trimmed = text.TrimStart();
        var spaceIdx = trimmed.IndexOf(' ');
        var prefix = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
        var startOffset = text.Length - trimmed.Length;
        InputBox.Text = text[..startOffset] + command;
        InputBox.CaretIndex = InputBox.Text.Length;
        vm.IsCommandPopupVisible = false;
        InputBox.Focus();
    }

    /// <summary>插入选中建议到输入框（# 一级 → "#类型:"；# 二级 → "#类型:名称 "；@ → "@名称 "）。</summary>
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
        InputBox.Text = MentionParser.InsertSuggestion(text, caret, mention.Start, suggestion.Name);
        InputBox.CaretIndex = mention.Start + suggestion.Name.Length + 2;
        vm.IsSuggestionPopupVisible = false;
        InputBox.Focus();
    }

    // ── 拖放：把文件/目录路径插入输入框（@ 路径语义） ──

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
        var insert = string.Join(
            "\n",
            paths.Select(path => Directory.Exists(path) ? $"[目录]{path}" : $"[文件]{path}")
        );
        var caret = InputBox.CaretIndex;
        InputBox.Text = text.Insert(caret, insert + "\n");
        InputBox.CaretIndex = caret + insert.Length + 1;
        InputBox.Focus();
    }
}
