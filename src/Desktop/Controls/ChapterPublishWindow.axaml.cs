using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Core.Data;
using Core.Evolution;
using Desktop.ViewModels;

namespace Desktop.Controls;

/// <summary>
/// 章节发布预览窗口（对齐 TUI 发布确认对话框）：待生效变化 + 正文检测到的死亡（可取消勾选）
/// + 联动建议（自动项不可取消）+ 预检问题。
/// 取消（Esc / 取消按钮 / 关窗）→ 返回 null，调用方保持草稿。
/// </summary>
public partial class ChapterPublishWindow : Window
{
    private readonly ChapterPublishViewModel _vm;

    public ChapterPublishWindow(
        PublishPreview preview,
        Func<EntityType, string, string>? nameOf = null
    )
    {
        _vm = new ChapterPublishViewModel(preview, nameOf);
        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "发布章节", 720, 660);

        TitleText.Text = _vm.Title;
        SubtitleText.Text = _vm.Subtitle;

        // ── 待生效变化 ──
        if (_vm.HasPendingChanges)
        {
            foreach (var line in _vm.PendingChangeLines)
                PendingPanel.Children.Add(BodyText(line));
        }
        else
        {
            PendingPanel.Children.Add(BodyText(_vm.PendingEmptyHint));
        }

        // ── 检测到的死亡（默认全选）──
        if (_vm.HasDetectedDeaths)
        {
            foreach (var item in _vm.DetectedDeaths)
                DeathsPanel.Children.Add(BuildOptionRow(item));
        }
        else
        {
            DeathsSection.IsVisible = false;
        }

        // ── 联动建议 ──
        if (_vm.HasSuggestions)
        {
            foreach (var item in _vm.Suggestions)
                SuggestionsPanel.Children.Add(BuildOptionRow(item));
        }
        else
        {
            SuggestionsSection.IsVisible = false;
        }

        // ── 预检提示（阻断用红色正文；阻断项存在时禁用发布）──
        if (_vm.HasIssues)
        {
            foreach (var line in _vm.Issues)
            {
                var text = new TextBlock
                {
                    Text = line,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                };
                text.Classes.Add(line.StartsWith('❌') ? "Danger" : "Tag");
                IssuesPanel.Children.Add(text);
            }
        }
        else
        {
            IssuesSection.IsVisible = false;
        }

        PublishButton.IsEnabled = _vm.CanPublish;
        PublishButton.Click += (_, _) => Confirm();
        CancelButton.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>模态显示并返回确认结果（null = 取消）。</summary>
    public Task<PublishOptions?> ShowAsync(Window owner) => ShowDialog<PublishOptions?>(owner);

    private void Confirm()
    {
        if (!_vm.CanPublish)
            return; // 阻断项存在（按钮已禁用；双保险）
        Close(_vm.BuildOptions());
    }

    /// <summary>普通正文行（变化摘要）。</summary>
    private static TextBlock BodyText(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "Tag" },
        };

    /// <summary>勾选行：主标题 + 说明（自动项禁用勾选）。</summary>
    private static Control BuildOptionRow(PublishOptionItem item)
    {
        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(
            new TextBlock
            {
                Text = item.Label,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        if (!string.IsNullOrWhiteSpace(item.Detail))
            stack.Children.Add(
                new TextBlock
                {
                    Text = item.Detail,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "Tag" },
                }
            );

        var box = new CheckBox
        {
            IsChecked = item.IsSelected,
            IsEnabled = item.CanToggle,
            Content = stack,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        box.IsCheckedChanged += (_, _) => item.IsSelected = box.IsChecked == true;
        return box;
    }
}
