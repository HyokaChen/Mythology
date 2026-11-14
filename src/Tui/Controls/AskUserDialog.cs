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
/// 一次交互里的所有问题共用一个对话框；每题按 Header 分组，确认时整组提交。
/// </summary>
public static class AskUserDialog
{
    private const string CustomOption = "Type something.";
    private const string SelectPlaceholder = "请选择…";

    /// <summary>显示一组问题并等待关闭（回答或取消都已提交 Core）。</summary>
    public static async Task ShowAsync(
        InteractionHub hub,
        string interactionId,
        string leaseId,
        IReadOnlyList<QuestionSlotSnapshot> slots
    )
    {
        if (slots.Count == 0)
            return;

        var closed = new TaskCompletionSource();
        ShowDialog(hub, interactionId, leaseId, slots, () => closed.TrySetResult());
        // Dialog.Show() 非阻塞；等待关闭信号，确保 presenter 一次只弹一个。
        await closed.Task;
    }

    private static void ShowDialog(
        InteractionHub hub,
        string interactionId,
        string leaseId,
        IReadOnlyList<QuestionSlotSnapshot> slots,
        Action onClosed
    )
    {
        var editors = slots
            .Where(slot => slot.Outcome == QuestionOutcome.Unanswered)
            .Select((slot, index) => new QuestionEditor(slot, index, slots.Count))
            .ToList();
        if (editors.Count == 0)
            return;

        var body = new VStack().Spacing(1);
        foreach (var editor in editors)
        {
            body.Children.Add(editor.Section);
            body.Children.Add(new Markup(""));
        }

        var title =
            editors.Count == 1
                ? DisplayTitle(editors[0].Question, 1, 1)
                : $"询问 · {editors.Count} 个问题";

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title(new Markup(ToolCallRenderer.Escape(title)))
            .BottomLeftText(new Markup("[#C7B299]Tab 切换 · Enter 确认 · 空格勾选 · Esc 取消[/]"))
            .Width(56)
            .MinHeight(14)
            .Padding(new Thickness(2, 1, 2, 1));

        var closeBtn = PanelThemes.CreateDialogCloseButton(CancelAndClose);

        var confirmBtn = new Button(new Markup("确认")).Style(
            ThemeStyles.BuildButtonAccent(PanelThemes.Current)
        );
        confirmBtn.Click(SubmitOrFocusMissing);

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(confirmBtn);
        dialog.Content(PanelThemes.CreateScrollableDialogContent(body));

        dialog.KeyDown(
            (_, e) =>
            {
                switch (e.Key)
                {
                    case TerminalKey.Escape:
                        CancelAndClose();
                        e.Handled = true;
                        break;
                    case TerminalKey.Tab:
                        MoveFocus();
                        e.Handled = true;
                        break;
                    case TerminalKey.Enter
                        when dialog.App?.FocusedElement is not SelectionList<string>:
                        SubmitOrFocusMissing();
                        e.Handled = true;
                        break;
                }
            }
        );

        dialog.Show();
        FocusEditor(editors[0]);
        return;

        void SubmitOrFocusMissing()
        {
            var missing = editors.FirstOrDefault(editor => !editor.IsAnswered);
            if (missing is not null)
            {
                dialog.BottomLeftText(new Markup("[#C7B299]请先回答所有问题[/]"));
                if (missing.IsCustomSelected && !missing.InputPanel.IsVisible)
                    missing.ShowCustomInput();
                FocusEditor(missing);
                return;
            }

            var failed = false;
            foreach (var editor in editors)
            {
                var status = hub.SubmitQuestionAnswer(
                    interactionId,
                    editor.Question.Id,
                    editor.CollectAnswers(),
                    leaseId
                ).Status;
                if (status is not (SubmitStatus.Accepted or SubmitStatus.AlreadyResolved))
                    failed = true;
            }

            if (failed)
            {
                dialog.BottomLeftText(new Markup("[#C7B299]提交失败，请重试[/]"));
                return;
            }

            dialog.Close();
            onClosed();
        }

        void CancelAndClose()
        {
            foreach (var editor in editors)
                hub.CancelQuestion(interactionId, editor.Question.Id, leaseId, "user-cancelled");
            dialog.Close();
            onClosed();
        }

        void MoveFocus()
        {
            var targets = new List<Visual>();
            foreach (var editor in editors)
            {
                targets.Add(editor.List);
                if (editor.InputPanel.IsVisible)
                    targets.Add(editor.Input);
            }
            targets.Add(confirmBtn);

            var focused = dialog.App?.FocusedElement;
            var index = focused is null ? -1 : targets.IndexOf(focused);
            if (index < 0)
            {
                FocusEditor(editors[0]);
                return;
            }

            // 单选里选中「自定义输入」后，Tab 先进入输入框，而不是直接跳到下一题。
            if (
                focused is RadioButtonList<string> { SelectedIndex: var selectedIndex }
                && editors.FirstOrDefault(editor => editor.List == focused) is { } current
                && selectedIndex == current.CustomIndex
                && !current.InputPanel.IsVisible
            )
            {
                current.ShowCustomInput();
                FocusEditor(current);
                return;
            }

            var next = targets[(index + 1) % targets.Count];
            var nextEditor = editors.FirstOrDefault(editor =>
                ReferenceEquals(editor.List, next) || ReferenceEquals(editor.Input, next)
            );
            if (nextEditor is null)
                dialog.App?.Focus(next);
            else
                FocusEditor(nextEditor);
        }

        void FocusEditor(QuestionEditor editor)
        {
            dialog.App?.Focus(editor.FocusTarget);
            UiDispatch.PostIfAttached(() => RevealEditor(editor));
        }

        void RevealEditor(QuestionEditor editor)
        {
            var focused = dialog.App?.FocusedElement;
            if (
                focused is null
                || !editor
                    .Section.EnumerateVisualsDepthFirst()
                    .Any(visual => ReferenceEquals(visual, focused))
            )
            {
                // 焦点已移走：丢弃上一题的延迟滚动，避免快速 Tab 时来回跳。
                return;
            }

            var scroll = dialog
                .EnumerateVisualsDepthFirst()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
            if (scroll is null || scroll.ViewportHeight <= 0)
                return;

            var extentHeight = scroll.Content?.Bounds.Height ?? 0;
            if (extentHeight <= 0)
                return;

            var bounds = editor.Section.Bounds;
            if (bounds.Height <= 0)
                return;

            var viewTop = scroll.VerticalOffset;
            var viewBottom = viewTop + scroll.ViewportHeight;
            var top = Math.Max(0, bounds.Y);
            var bottom = top + bounds.Height;
            // 题目比视口还高时对齐题首，保证标题可见；否则整段滚入视口。
            var target =
                bounds.Height > scroll.ViewportHeight || top < viewTop ? top
                : bottom > viewBottom ? bottom - scroll.ViewportHeight
                : viewTop;
            scroll.VerticalOffset = Math.Clamp(
                target,
                0,
                Math.Max(0, extentHeight - scroll.ViewportHeight)
            );
        }
    }

    private static string DisplayTitle(PendingQuestion question, int ordinal, int total)
    {
        var header = string.IsNullOrWhiteSpace(question.Header) ? "问题" : question.Header.Trim();
        return total <= 1 ? header : $"{ordinal}. {header}";
    }

    /// <summary>单题的控件状态；一题一个 editor，避免多题共用选中/输入状态。</summary>
    private sealed class QuestionEditor
    {
        private readonly List<string> _displayOptions;
        private readonly MaskedInput _input;
        private readonly Padder _inputPanel;
        private readonly Visual _list;
        private readonly SelectionList<string>? _multiList;
        private readonly RadioButtonList<string>? _singleList;

        public QuestionEditor(QuestionSlotSnapshot slot, int ordinal, int total)
        {
            Slot = slot;
            Question = slot.Question;
            CustomIndex = Question.MultiSelect ? -1 : Question.Options.Length + 1;

            // 单选 = radio 列表（一次只能选一个），多选 = checkbox 列表（可勾选多项）。
            _displayOptions = Question.Options.ToList();
            if (CustomIndex >= 0)
            {
                _displayOptions.Insert(0, SelectPlaceholder);
                _displayOptions.Add(CustomOption);
            }

            if (Question.MultiSelect)
            {
                _multiList = BuildMultiSelectList(Question);
                _list = _multiList;
            }
            else
            {
                _singleList = BuildSingleSelectList(_displayOptions, CustomIndex);
                _list = _singleList;
            }
            _list.HorizontalAlignment = Align.Stretch;

            _input = new MaskedInput { Template = "____________________" };
            _inputPanel = new Padder
            {
                Content = _input,
                Padding = new Thickness(1, 0, 1, 0),
                IsVisible = false,
            };

            var section = new VStack(
                new Markup(
                    $"[bold]{ToolCallRenderer.Escape(DisplayTitle(Question, ordinal, total))}[/]"
                ),
                new Markup(ToolCallRenderer.Escape(Question.Question)).Wrap(true),
                new Padder { Content = _list, Padding = new Thickness(1, 1, 1, 0) },
                _inputPanel
            ).Spacing(1);
            Section = section;
        }

        public QuestionSlotSnapshot Slot { get; }
        public PendingQuestion Question { get; }
        public int CustomIndex { get; }
        public Visual List => _list;
        public MaskedInput Input => _input;
        public Padder InputPanel => _inputPanel;
        public bool IsCustomSelected =>
            !Question.MultiSelect && _singleList?.SelectedIndex == CustomIndex;
        public Visual FocusTarget => _inputPanel.IsVisible ? _input : _list;
        public VStack Section { get; }

        public bool IsAnswered
        {
            get
            {
                if (Question.MultiSelect)
                    return _multiList!.Checked.Any(checkedValue => checkedValue);
                if (_singleList!.SelectedIndex <= 0)
                    return false;
                if (_singleList.SelectedIndex != CustomIndex)
                    return true;
                return !string.IsNullOrWhiteSpace(_input.Value);
            }
        }

        public string[] CollectAnswers()
        {
            if (Question.MultiSelect)
            {
                var answers = new List<string>();
                for (var i = 0; i < Question.Options.Length; i++)
                {
                    if (i < _multiList!.Checked.Count && _multiList.Checked[i])
                        answers.Add(Question.Options[i]);
                }
                return answers.ToArray();
            }

            var index = _singleList!.SelectedIndex;
            if (index <= 0 || index >= _displayOptions.Count)
                return [];
            if (index != CustomIndex)
                return [_displayOptions[index]];

            var custom = _input.Value?.Trim();
            return string.IsNullOrEmpty(custom) ? [] : [custom];
        }

        public void ShowCustomInput() => _inputPanel.IsVisible = true;

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

        private static RadioButtonList<string> BuildSingleSelectList(
            IReadOnlyList<string> displayOptions,
            int customIndex
        )
        {
            var list = new RadioButtonList<string>(displayOptions, 0);
            list.ItemTemplate = new DataTemplate<string>(
                Display: (value, in context) =>
                    // 选项来自 LLM（用户问题的候选项）：按数据处理，先转义再渲染
                    context.Index == 0
                        ? new Markup("[dim italic]请选择…[/]")
                    : context.Index == customIndex ? new Markup("[dim italic]Type something.[/]")
                    : new Markup(ToolCallRenderer.Escape(value.GetValue())),
                Editor: null
            );
            return list;
        }
    }
}
