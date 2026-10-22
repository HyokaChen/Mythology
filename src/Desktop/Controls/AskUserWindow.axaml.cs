using Avalonia.Controls;
using Avalonia.Input;
using Core.Shared;

namespace Desktop.Controls;

/// <summary>
/// ask_user 工具弹出窗口 — 渲染结构化问题，支持单选（+ 自定义输入）或多选。
/// 移植自 TUI AskUserDialog：用户选择后写入 UserQuestionStore，解除 Agent 工具阻塞。
/// 布局对齐 DESIGN.md（Adaline）：Linen 底 · Card 选项区 · Primary/Ghost 按钮 · 6px 节奏。
/// </summary>
public partial class AskUserWindow : Window
{
    private readonly UserQuestionStore _store;
    private readonly PendingQuestion _question;
    private readonly List<RadioButton> _radioOptions = [];
    private readonly List<CheckBox> _checkOptions = [];
    private RadioButton? _customRadio;

    public AskUserWindow(UserQuestionStore store, PendingQuestion question)
    {
        _store = store;
        _question = question;

        InitializeComponent();

        Title = question.Header;
        QuestionText.Text = _question.Question;

        BuildOptions();

        CancelButton.Click += (_, _) => CancelAnswer();
        ConfirmButton.Click += (_, _) => ConfirmAnswer();

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
                        Content = opt,
                        FontSize = 13,
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
                        Content = opt,
                        FontSize = 13,
                        GroupName = "ask",
                        Foreground = DialogTheme.ForestInk,
                        FontFamily = DialogTheme.FontSans,
                    }
                );
                _radioOptions.Add((RadioButton)OptionsPanel.Children[^1]);
            }

            _customRadio = new RadioButton
            {
                Content = "自定义输入",
                FontSize = 13,
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
                    .Select(c => c.Content?.ToString() ?? "")
                    .Where(s => s.Length > 0)
            );
            var custom = CustomInput.Text?.Trim();
            if (!string.IsNullOrEmpty(custom))
                answers.Add(custom);
        }
        else
        {
            var selected = _radioOptions.FirstOrDefault(r => r.IsChecked == true);
            if (selected is not null)
                answers.Add(selected.Content?.ToString() ?? "");

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

        _store.Answer(_question.Id, answers.ToArray());
        Close();
    }

    /// <summary>取消：写入「(已取消)」以解除阻塞（与 TUI 语义一致）。</summary>
    private void CancelAnswer()
    {
        _store.Answer(_question.Id, ["(已取消)"]);
        Close();
    }
}
