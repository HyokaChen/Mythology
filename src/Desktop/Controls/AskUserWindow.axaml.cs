using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Core.Interactions;
using Core.Shared;

namespace Desktop.Controls;

/// <summary>
/// ask_user 交互窗口 — 只渲染 Core 快照并把用户结果提交回 <see cref="InteractionHub"/>。
/// 窗口不写 store、不决定状态；关闭/X 也作为取消提交给 Core。
/// </summary>
public partial class AskUserWindow : Window
{
    private readonly InteractionHub _hub;
    private readonly string _interactionId;
    private readonly string _leaseId;
    private readonly QuestionSlotSnapshot _slot;
    private readonly PendingQuestion _question;
    private readonly List<RadioButton> _radioOptions = [];
    private readonly List<CheckBox> _checkOptions = [];
    private RadioButton? _customRadio;
    private bool _submitted;
    private bool _closedFromCore;

    public AskUserWindow(
        InteractionHub hub,
        string interactionId,
        string leaseId,
        QuestionSlotSnapshot slot
    )
    {
        _hub = hub;
        _interactionId = interactionId;
        _leaseId = leaseId;
        _slot = slot;
        _question = slot.Question;

        InitializeComponent();

        Title = _question.Header;
        QuestionText.Text = _question.Question;

        BuildOptions();

        CancelButton.Click += (_, _) => CancelAnswer();
        ConfirmButton.Click += (_, _) => ConfirmAnswer();
        Closed += (_, _) =>
        {
            if (!_submitted && !_closedFromCore)
            {
                _submitted = true;
                _hub.CancelQuestion(_interactionId, _question.Id, _leaseId, "window-closed");
            }
        };

        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CancelAnswer();
                    e.Handled = true;
                    break;
                case Key.Enter when CustomPanel.IsVisible:
                    ConfirmAnswer();
                    e.Handled = true;
                    break;
            }
        };
    }

    /// <summary>动态构建选项列表（单选 RadioButton 或多选 CheckBox）。</summary>
    private void BuildOptions()
    {
        OptionsPanel.Children.Clear();

        if (_question.MultiSelect)
        {
            foreach (var opt in _question.Options)
            {
                OptionsPanel.Children.Add(
                    new CheckBox
                    {
                        Content = BuildOptionText(opt),
                        Tag = opt,
                        FontSize = 14,
                        Foreground = DialogTheme.ForestInk,
                        FontFamily = DialogTheme.FontSans,
                    }
                );
                _checkOptions.Add((CheckBox)OptionsPanel.Children[^1]);
            }
        }
        else
        {
            foreach (var opt in _question.Options)
            {
                OptionsPanel.Children.Add(
                    new RadioButton
                    {
                        Content = BuildOptionText(opt),
                        Tag = opt,
                        FontSize = 14,
                        GroupName = "ask",
                        Foreground = DialogTheme.ForestInk,
                        FontFamily = DialogTheme.FontSans,
                    }
                );
                _radioOptions.Add((RadioButton)OptionsPanel.Children[^1]);
            }

            _customRadio = new RadioButton
            {
                Content = BuildOptionText("自定义输入"),
                FontSize = 14,
                GroupName = "ask",
                Foreground = DialogTheme.ForestInk,
                FontFamily = DialogTheme.FontSans,
            };
            _customRadio.IsCheckedChanged += (_, _) =>
            {
                CustomPanel.IsVisible = _customRadio.IsChecked == true;
                if (CustomPanel.IsVisible)
                    CustomInput.Focus();
            };
            OptionsPanel.Children.Add(_customRadio);
        }
    }

    /// <summary>收集答案并写入 store（解除 Agent 阻塞），关闭窗口。</summary>
    private void ConfirmAnswer()
    {
        var answers = new List<string>();

        if (_question.MultiSelect)
        {
            answers.AddRange(
                _checkOptions
                    .Where(c => c.IsChecked == true)
                    .Select(c => c.Tag as string ?? "")
                    .Where(s => s.Length > 0)
            );
            var custom = CustomInput.Text?.Trim();
            if (!string.IsNullOrEmpty(custom))
                answers.Add(custom);
        }
        else
        {
            var selected = _radioOptions.FirstOrDefault(r => r.IsChecked == true);
            if (selected?.Tag is string selectedText && selectedText.Length > 0)
                answers.Add(selectedText);

            if (_customRadio?.IsChecked == true)
            {
                var custom = CustomInput.Text?.Trim();
                if (string.IsNullOrEmpty(custom))
                {
                    CustomInput.Focus();
                    return;
                }
                answers.Add(custom);
            }
        }

        if (answers.Count == 0)
            return; // 未做任何选择：保持窗口打开

        _submitted = true;
        _hub.SubmitQuestionAnswer(_interactionId, _question.Id, answers.ToArray(), _leaseId);
        Close();
    }

    /// <summary>选项文本：占满选项宽度并换行，避免长选项在窄弹窗里被截断。</summary>
    private static TextBlock BuildOptionText(string text) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = DialogTheme.ForestInk,
            FontFamily = DialogTheme.FontSans,
        };

    /// <summary>取消当前题：Core 记录 Cancelled 并完成 waiter，继续下一题（若有）。</summary>
    private void CancelAnswer()
    {
        _submitted = true;
        _hub.CancelQuestion(_interactionId, _question.Id, _leaseId, "user-cancelled");
        Close();
    }

    /// <summary>Core 已解析/取消该交互：关闭窗口，且不要再提交 cancel。</summary>
    public void CloseFromCore()
    {
        _closedFromCore = true;
        Close();
    }
}
