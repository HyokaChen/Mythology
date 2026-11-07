using Core.Interactions;
using Core.Shared;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// ask_user 交互对话框 — 只渲染 Core 快照并把结果提交回 <see cref="InteractionHub"/>。
/// 每个问题一个对话框；多问队列由 TuiApp presenter 负责，避免同帧叠窗。
/// </summary>
public static class AskUserDialog
{
    private const string CustomOption = "Type something.";

    /// <summary>显示单个问题并等待关闭（回答或取消都已提交 Core）。</summary>
    public static async Task ShowAsync(
        InteractionHub hub,
        string interactionId,
        string leaseId,
        QuestionSlotSnapshot slot
    )
    {
        var closed = new TaskCompletionSource();
        ShowSingleDialog(hub, interactionId, leaseId, slot, () => closed.TrySetResult());
        // Dialog.Show() 非阻塞；等待关闭信号，确保 presenter 一次只弹一个。
        await closed.Task;
    }

    private static void ShowSingleDialog(
        InteractionHub hub,
        string interactionId,
        string leaseId,
        QuestionSlotSnapshot slot,
        Action onClosed
    )
    {
        var question = slot.Question;
        // 构建选项列表：原选项 + "Type something." （单选时）
        var hasCustomInput = !question.MultiSelect;
        var displayOptions = question.Options.ToList();
        if (hasCustomInput)
            displayOptions.Add(CustomOption);
        var customIndex = hasCustomInput ? question.Options.Length : -1;

        // 单选 = radio 列表（一次只能选一个），多选 = checkbox 列表（可勾选多项）。
        SelectionList<string>? multiList = null;
        RadioButtonList<string>? singleList = null;
        Visual list;
        if (question.MultiSelect)
        {
            multiList = BuildMultiSelectList(question);
            list = multiList;
        }
        else
        {
            singleList = BuildSingleSelectList(displayOptions, customIndex);
            list = singleList;
        }
        list.HorizontalAlignment = Align.Stretch;
        list.AutoFocus = true;

        // 自定义输入框（单选时初始隐藏）
        var inputField = new MaskedInput { Template = "____________________" };
        var inputPanel = new Padder
        {
            Content = inputField,
            Padding = new Thickness(1, 0, 1, 0),
            IsVisible = false,
        };

        var title = question.Header;

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title(new Markup(title))
            .BottomLeftText(
                new Markup(
                    question.MultiSelect
                        ? "[#C7B299]↑↓ 选择 · 空格/Enter 勾选 · Tab 确认 · Esc 取消[/]"
                        : "[#C7B299]↑↓ 选择 · Enter 确认 · Tab 输入 · Esc 取消[/]"
                )
            )
            .Width(52)
            .MinHeight(12)
            .Padding(new Thickness(2, 1, 2, 1));

        var closeBtn = PanelThemes.CreateDialogCloseButton(CancelAndClose);

        var confirmBtn = new Button(new Markup("确认")).Style(
            ThemeStyles.BuildButtonAccent(PanelThemes.Current)
        );
        confirmBtn.Click(() =>
        {
            if (question.MultiSelect)
            {
                var answers = CollectMultiAnswers(question, multiList!);
                if (answers.Length == 0)
                    return;

                SubmitAndClose(answers);
                return;
            }

            if (inputPanel.IsVisible)
            {
                SubmitCustomAnswer();
                return;
            }

            var selected = GetSelectedSingleOption(singleList, displayOptions);
            if (selected is null)
                return;

            if (selected == CustomOption)
            {
                ShowCustomInput();
                return;
            }

            SubmitAndClose([selected]);
        });

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(confirmBtn);
        dialog.Content(
            PanelThemes.CreateDialogContent(
                new VStack(
                    new Markup(ToolCallRenderer.Escape(question.Question)),
                    new Padder { Content = list, Padding = new Thickness(1, 1, 1, 0) },
                    inputPanel
                ).Spacing(1)
            )
        );

        dialog.KeyDown(
            (_, e) =>
            {
                switch (e.Key)
                {
                    case TerminalKey.Escape:
                        CancelAndClose();
                        e.Handled = true;
                        break;
                    case TerminalKey.Tab when question.MultiSelect:
                        dialog.App?.Focus(confirmBtn);
                        e.Handled = true;
                        break;
                    case TerminalKey.Enter when !question.MultiSelect && inputPanel.IsVisible:
                        SubmitCustomAnswer();
                        e.Handled = true;
                        break;
                    case TerminalKey.Enter when !question.MultiSelect:
                    {
                        var selected = GetSelectedSingleOption(singleList, displayOptions);
                        if (selected is null)
                            break;

                        if (selected == CustomOption)
                        {
                            ShowCustomInput();
                        }
                        else
                        {
                            SubmitAndClose([selected]);
                        }

                        e.Handled = true;
                        break;
                    }
                    case TerminalKey.Tab
                        when !question.MultiSelect
                            && !inputPanel.IsVisible
                            && GetSelectedSingleOption(singleList, displayOptions) == CustomOption:
                        ShowCustomInput();
                        e.Handled = true;
                        break;
                }
            }
        );

        dialog.Show();
        dialog.App?.Focus(list);
        return;

        void ShowCustomInput()
        {
            if (inputPanel.IsVisible)
                return;

            inputPanel.IsVisible = true;
            dialog.App?.Focus(inputField);
        }

        void SubmitCustomAnswer()
        {
            var customText = inputField.Value?.Trim();
            if (string.IsNullOrEmpty(customText))
                return;

            SubmitAndClose([customText]);
        }

        void SubmitAndClose(string[] answers)
        {
            hub.SubmitQuestionAnswer(interactionId, question.Id, answers, leaseId);
            dialog.Close();
            onClosed();
        }

        void CancelAndClose()
        {
            hub.CancelQuestion(interactionId, question.Id, leaseId, "user-cancelled");
            dialog.Close();
            onClosed();
        }
    }

    /// <summary>构建多选列表：每行一个 checkbox，空格/Enter 切换勾选状态。</summary>
    private static SelectionList<string> BuildMultiSelectList(PendingQuestion question)
    {
        var list = new SelectionList<string>();
        foreach (var option in question.Options)
            list.AddItem(option);

        list.ItemTemplate = new DataTemplate<string>(
            Display: (value, in _) =>
                // 选项来自 LLM（用户问题的候选项）：按数据处理，先转义再渲染
                new Markup(ToolCallRenderer.Escape(value.GetValue())),
            Editor: null
        );
        return list;
    }

    /// <summary>构建单选列表：每行一个 radio，最后一行用于自定义输入。</summary>
    private static RadioButtonList<string> BuildSingleSelectList(
        IReadOnlyList<string> displayOptions,
        int customIndex
    )
    {
        var list = new RadioButtonList<string>(displayOptions, 0);
        list.ItemTemplate = new DataTemplate<string>(
            Display: (value, in context) =>
                context.Index == customIndex
                    ? new Markup("[dim italic]Type something.[/]")
                    : new Markup(ToolCallRenderer.Escape(value.GetValue())),
            Editor: null
        );
        return list;
    }

    /// <summary>按选项原始顺序收集多选答案。</summary>
    private static string[] CollectMultiAnswers(
        PendingQuestion question,
        SelectionList<string> list
    )
    {
        var answers = new List<string>();
        for (var i = 0; i < question.Options.Length; i++)
        {
            if (i < list.Checked.Count && list.Checked[i])
                answers.Add(question.Options[i]);
        }
        return answers.ToArray();
    }

    /// <summary>读取单选列表当前选中的原始选项；索引无效时返回 null。</summary>
    private static string? GetSelectedSingleOption(
        RadioButtonList<string>? list,
        IReadOnlyList<string> displayOptions
    )
    {
        if (list is null)
            return null;

        var index = list.SelectedIndex;
        return index >= 0 && index < displayOptions.Count ? displayOptions[index] : null;
    }
}
