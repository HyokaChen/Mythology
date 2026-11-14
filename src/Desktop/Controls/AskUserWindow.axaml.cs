using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Core.Interactions;
using Core.Shared;
using Desktop.Styles;

namespace Desktop.Controls;

/// <summary>
/// ask_user 交互窗口 — 只渲染 Core 快照并把用户结果提交回 <see cref="InteractionHub"/>。
/// 一次交互里的所有问题共用一个窗口，每题按 Header 分组，确认时整组提交。
/// 窗口不写 store、不决定状态；关闭/X 也作为取消提交给 Core。
/// </summary>
public partial class AskUserWindow : Window
{
    private const string CustomOptionLabel = "自定义输入";
    private readonly InteractionHub _hub;
    private readonly string _interactionId;
    private readonly string _leaseId;
    private readonly IReadOnlyList<QuestionEditor> _editors;
    private bool _submitted;
    private bool _closedFromCore;

    public AskUserWindow(
        InteractionHub hub,
        string interactionId,
        string leaseId,
        IReadOnlyList<QuestionSlotSnapshot> slots
    )
    {
        _hub = hub;
        _interactionId = interactionId;
        _leaseId = leaseId;

        InitializeComponent();

        _editors = slots
            .Where(s => s.Outcome == QuestionOutcome.Unanswered)
            .Select(BuildEditor)
            .ToList();
        foreach (var editor in _editors)
            QuestionsPanel.Children.Add(editor.Root);

        Title =
            _editors.Count == 1
                ? DisplayHeader(_editors[0].Question)
                : $"需要你的回答 · {_editors.Count} 个问题";

        CancelButton.Click += (_, _) => CancelAnswer();
        ConfirmButton.Click += (_, _) => ConfirmAnswer();
        Closed += (_, _) =>
        {
            if (_submitted || _closedFromCore)
                return;

            _submitted = true;
            CancelRemaining();
        };
        Opened += (_, _) => _editors.FirstOrDefault()?.Focus();
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CancelAnswer();
                    e.Handled = true;
                    break;
                case Key.Enter when _editors.Any(editor => editor.CustomPanel.IsVisible):
                    ConfirmAnswer();
                    e.Handled = true;
                    break;
            }
        };
    }

    /// <summary>为单个问题构建标题、题干、选项与可选的自定义输入区。</summary>
    private static QuestionEditor BuildEditor(QuestionSlotSnapshot slot)
    {
        var question = slot.Question;
        var optionsPanel = new StackPanel { Spacing = 8 };
        var customInput = new TextBox
        {
            Classes = { "Adaline" },
            PlaceholderText = "输入自定义答案…",
        };
        var customPanel = new StackPanel
        {
            Spacing = 6,
            IsVisible = false,
            Margin = new Thickness(0, 10, 0, 0),
        };
        Motion.SetEnter(customPanel, true);
        Motion.SetEnterRise(customPanel, 4);
        customPanel.Children.Add(customInput);
        customPanel.Children.Add(
            new TextBlock
            {
                Text = "Enter 确认 · Esc 取消",
                FontSize = 10,
                Foreground = DialogTheme.SageMistBrush,
                FontFamily = DialogTheme.FontSans,
                LetterSpacing = 0.2,
                Margin = new Thickness(2, 2, 0, 0),
            }
        );

        var body = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(question.Header))
        {
            body.Children.Add(
                new TextBlock
                {
                    Text = question.Header,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = DialogTheme.DeepTealBrush,
                    FontFamily = DialogTheme.FontSans,
                    TextWrapping = TextWrapping.Wrap,
                }
            );
        }
        body.Children.Add(
            new TextBlock
            {
                Text = question.Question,
                FontSize = 16,
                FontWeight = FontWeight.Medium,
                Foreground = DialogTheme.ForestInk,
                FontFamily = DialogTheme.FontSans,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        body.Children.Add(optionsPanel);
        body.Children.Add(customPanel);

        var root = new Border
        {
            Padding = new Thickness(20),
            MaxHeight = 360,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
            },
        };
        root.Classes.Add("Card");

        var editor = new QuestionEditor(slot, root, optionsPanel, customPanel, customInput);
        editor.BuildOptions();
        return editor;
    }

    /// <summary>收集整组答案；任何一题未答都不提交，并把焦点带回第一题缺失项。</summary>
    private void ConfirmAnswer()
    {
        var unanswered = _editors.FirstOrDefault(editor => !editor.IsAnswered);
        if (unanswered is not null)
        {
            ValidationText.Text = "请先回答所有问题";
            ValidationText.IsVisible = true;
            unanswered.Focus();
            return;
        }

        _submitted = true;
        var failed = false;
        foreach (var editor in _editors)
        {
            var status = _hub.SubmitQuestionAnswer(
                _interactionId,
                editor.Question.Id,
                editor.CollectAnswers(),
                _leaseId
            ).Status;
            if (status is not (SubmitStatus.Accepted or SubmitStatus.AlreadyResolved))
                failed = true;
        }

        if (failed)
        {
            _submitted = false;
            ValidationText.Text = "提交失败，请重试";
            ValidationText.IsVisible = true;
            return;
        }

        Close();
    }

    /// <summary>取消整组：逐题取消，Core 会在最后一题结束时完成交互并唤醒 Agent。</summary>
    private void CancelAnswer()
    {
        _submitted = true;
        CancelRemaining();
        Close();
    }

    private void CancelRemaining()
    {
        foreach (var editor in _editors)
            _hub.CancelQuestion(_interactionId, editor.Question.Id, _leaseId, "user-cancelled");
    }

    /// <summary>Core 已解析/取消该交互：关闭窗口，且不要再提交 cancel。</summary>
    public void CloseFromCore()
    {
        _closedFromCore = true;
        Close();
    }

    private static string DisplayHeader(PendingQuestion question) =>
        string.IsNullOrWhiteSpace(question.Header) ? "询问" : question.Header.Trim();

    /// <summary>单题的控件状态；一题一个 editor，避免多题共用 GroupName / 输入框。</summary>
    private sealed class QuestionEditor(
        QuestionSlotSnapshot slot,
        Border root,
        StackPanel optionsPanel,
        StackPanel customPanel,
        TextBox customInput
    )
    {
        private readonly List<RadioButton> _radioOptions = [];
        private readonly List<CheckBox> _checkOptions = [];

        public QuestionSlotSnapshot Slot { get; } = slot;
        public PendingQuestion Question => Slot.Question;
        public Border Root { get; } = root;
        public StackPanel OptionsPanel { get; } = optionsPanel;
        public StackPanel CustomPanel { get; } = customPanel;
        public TextBox CustomInput { get; } = customInput;
        public RadioButton? CustomRadio { get; private set; }

        public bool IsAnswered
        {
            get
            {
                if (Question.MultiSelect)
                    return _checkOptions.Any(check => check.IsChecked == true);
                if (_radioOptions.Any(radio => radio.IsChecked == true))
                    return true;
                return CustomRadio?.IsChecked == true
                    && !string.IsNullOrWhiteSpace(CustomInput.Text);
            }
        }

        public void BuildOptions()
        {
            if (Question.MultiSelect)
            {
                // 多选：每行一个 checkbox，按选项原始顺序收集。
                foreach (var option in Question.Options)
                {
                    var check = new CheckBox { Content = BuildOptionText(option), Tag = option };
                    _checkOptions.Add(check);
                    OptionsPanel.Children.Add(check);
                }
                return;
            }

            // 单选：同一题内的 radio 共用唯一 GroupName；不同题不能互相取消。
            var groupName = "ask-" + Question.Id;
            foreach (var option in Question.Options)
            {
                var radio = new RadioButton
                {
                    Content = BuildOptionText(option),
                    Tag = option,
                    GroupName = groupName,
                };
                _radioOptions.Add(radio);
                OptionsPanel.Children.Add(radio);
            }

            CustomRadio = new RadioButton
            {
                Content = BuildOptionText(CustomOptionLabel),
                GroupName = groupName,
            };
            CustomRadio.IsCheckedChanged += (_, _) =>
            {
                CustomPanel.IsVisible = CustomRadio.IsChecked == true;
                if (CustomPanel.IsVisible)
                    CustomInput.Focus();
            };
            OptionsPanel.Children.Add(CustomRadio);
        }

        public string[] CollectAnswers()
        {
            if (Question.MultiSelect)
            {
                return _checkOptions
                    .Where(check => check.IsChecked == true)
                    .Select(check => check.Tag as string ?? "")
                    .Where(answer => answer.Length > 0)
                    .ToArray();
            }

            var selected = _radioOptions.FirstOrDefault(radio => radio.IsChecked == true);
            if (selected?.Tag is string option && option.Length > 0)
                return [option];

            var custom = CustomInput.Text?.Trim();
            return CustomRadio?.IsChecked == true && !string.IsNullOrEmpty(custom) ? [custom] : [];
        }

        public void Focus()
        {
            if (CustomPanel.IsVisible)
            {
                CustomInput.Focus();
                return;
            }

            Control? target = Question.MultiSelect
                ? _checkOptions.FirstOrDefault()
                : _radioOptions.FirstOrDefault(radio => radio.IsChecked == true)
                    ?? _radioOptions.FirstOrDefault();
            target?.Focus();
        }

        private static TextBlock BuildOptionText(string text) =>
            new()
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Foreground = DialogTheme.ForestInk,
                FontFamily = DialogTheme.FontSans,
            };
    }
}
