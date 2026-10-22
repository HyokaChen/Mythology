using Avalonia.Controls;
using Avalonia.Input;
using Core.Data;

namespace Desktop.Controls;

/// <summary>
/// 新建章节快速窗口：标题 + 所属卷（预选调用行所在卷），回车即建；
/// 细纲 / 计划出场 / 正文等重字段仍走完整实体编辑窗（章行 ⋯ → 编辑细纲）。
/// </summary>
public partial class ChapterQuickCreateWindow : Window
{
    private readonly IReadOnlyList<Volume> _volumes;
    private readonly Action<string, string?> _onCreate;
    private bool _submitted;

    public ChapterQuickCreateWindow(
        IReadOnlyList<Volume> volumes,
        string? presetVolumeId,
        Action<string, string?> onCreate
    )
    {
        _volumes = volumes;
        _onCreate = onCreate;
        InitializeComponent();
        Title = "新建章节";

        var labels = new List<string> { "（未分卷）" };
        labels.AddRange(
            volumes.Select(v =>
                string.IsNullOrWhiteSpace(v.Name) ? $"第{v.Order}卷" : $"第{v.Order}卷 · {v.Name}"
            )
        );
        VolumeBox.ItemsSource = labels;
        VolumeBox.SelectedIndex = IndexOf(volumes, presetVolumeId);

        TitleBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };
        Opened += (_, _) => TitleBox.Focus();
    }

    /// <summary>提交（幂等）：关闭窗口并回调（标题, 目标卷 id｜null = 未分卷）。</summary>
    private void Submit()
    {
        if (_submitted)
            return;
        _submitted = true;
        var title = TitleBox.Text?.Trim() ?? "";
        var index = VolumeBox.SelectedIndex;
        var volumeId = index > 0 && index <= _volumes.Count ? _volumes[index - 1].Id : null;
        Close();
        _onCreate(title, volumeId);
    }

    /// <summary>所属卷下拉的预选下标（0 = 未分卷；未命中 → 0）。</summary>
    private static int IndexOf(IReadOnlyList<Volume> volumes, string? volumeId)
    {
        if (string.IsNullOrEmpty(volumeId))
            return 0;
        for (var i = 0; i < volumes.Count; i++)
            if (string.Equals(volumes[i].Id, volumeId, StringComparison.Ordinal))
                return i + 1;
        return 0;
    }

    private void CancelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close();

    private void CreateButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Submit();
}
