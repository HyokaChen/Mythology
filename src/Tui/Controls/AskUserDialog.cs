using Core.Shared;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// ask_user 工具的弹出对话框 — 渲染结构化问题，支持选择选项。
///
/// 设计参考 Codex/Claude Code/OpenCode：
/// - 单选问题用 radio 列表，最后一行是 "Type something." 自定义输入
/// - 多选问题用 checkbox 列表（空格/Enter 勾选，确认后一次提交全部选中项）
/// - 用户选择或输入后，答案追加到 ConversationLoop，继续 agent loop
/// </summary>
public static class AskUserDialog
{
    private const string CustomOption = "Type something.";

    /// <summary>
    /// 显示带问题的模态对话框。用户回答完所有问题后返回。
    /// </summary>
    /// <param name="store">用户问题存储。</param>
    /// <param name="questions">待回答问题列表。</param>
    public static async Task Show(UserQuestionStore store, IReadOnlyList<PendingQuestion> questions)
    {
        switch (questions.Count)
        {
            case 0:
                return;
            case 1:
                await ShowSingleDialogAsync(store, questions[0]);
                return;
            default:
                await ShowSequentialAsync(store, questions);
                break;
        }
    }

    /// <summary>
    /// 按顺序显示多个问题的对话框，每个问题独立弹窗，带进度指示。
    ///
    /// Dialog.Show() 是非阻塞的（仅把窗口加入渲染层立即返回），所以必须先等当前
    /// 问题被回答/取消后再显示下一个；否则多个弹窗会在同一帧同时叠加渲染，
    /// 每个弹窗各自的确认按钮因高度不同而错位叠显（点一次只关掉一个）。
    /// </summary>
    private static async Task ShowSequentialAsync(
        UserQuestionStore store,
        IReadOnlyList<PendingQuestion> questions
    )
    {
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (q.IsAnswered)
                continue;

            var remaining = questions.Count - i;
            await ShowSingleDialogAsync(store, q, remaining);
        }
    }

    /// <summary>
    /// 显示单个问题的对话框并等待用户回答/取消后返回。
    /// </summary>
    private static async Task ShowSingleDialogAsync(
        UserQuestionStore store,
        PendingQuestion question,
        int? remaining = null
    )
    {
        ShowSingleDialog(store, question, remaining);
        // 所有关闭路径（选中选项 / 自定义输入 / Esc / X 按钮）都会 Answer，
        // 因此这里必定完成；等待可确保对话框按顺序逐个弹出。
        await store.WaitForAnswerAsync(question.Id);
    }

    /// <summary>
    /// 显示单个问题的对话框。带可选进度指示。
    /// </summary>
    /// <param name="store">用户问题存储。</param>
    /// <param name="question">待回答问题。</param>
    /// <param name="remaining">剩余问题数（null 表示不显示进度）。</param>
    private static void ShowSingleDialog(
        UserQuestionStore store,
        PendingQuestion question,
        int? remaining = null
    )
    {
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

        var title = remaining.HasValue
            ? $"{question.Header} [#C7B299]({remaining} 题待答)[/]"
            : $"{question.Header}";

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

        var closeBtn = PanelThemes.CreateDialogCloseButton(() =>
        {
            store.Answer(question.Id, ["(已取消)"]);
            dialog.Close();
        });

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

                store.Answer(question.Id, answers);
                dialog.Close();
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

            store.Answer(question.Id, [selected]);
            dialog.Close();
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
                        store.Answer(question.Id, ["(已取消)"]);
                        dialog.Close();
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
                            store.Answer(question.Id, [selected]);
                            dialog.Close();
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

            store.Answer(question.Id, [customText]);
            dialog.Close();
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
