using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Core.Tools;
using LiveMarkdown.Avalonia;

namespace Desktop.Controls;

/// <summary>用户在变体审阅窗中选择的操作（采用/继续修改/忽略）。</summary>
public enum VariantReviewAction
{
    /// <summary>采用此变体（写入章节或加入对话）。</summary>
    Apply,

    /// <summary>基于此变体继续修改。</summary>
    Refine,

    /// <summary>忽略此变体组。</summary>
    Dismiss,
}

/// <summary>变体审阅决策（镜像 TUI VariantReviewDecision）。</summary>
public sealed record VariantReviewDecision
{
    /// <summary>用户选择的操作。</summary>
    public VariantReviewAction Action { get; init; }

    /// <summary>选中的变体 ID。</summary>
    public string? VariantId { get; init; }

    /// <summary>选中的变体标签。</summary>
    public string? VariantLabel { get; init; }

    /// <summary>选中的变体内容。</summary>
    public string? VariantContent { get; init; }
}

/// <summary>
/// 变体审阅窗口（对齐 TUI VariantReviewDialog）：Tab 页展示 explore 工具返回的多个变体，
/// 预览后选择采用（Apply）/ 继续修改（Refine）/ 忽略（Esc=Dismiss）。
/// </summary>
public partial class VariantReviewWindow : Window
{
    /// <summary>用户决策（关闭时未选择 = null，调用方视为 Dismiss）。</summary>
    public VariantReviewDecision? Decision { get; private set; }

    private readonly List<VariantResult> _variants;
    private readonly List<Button> _tabButtons = [];
    private int _selectedIndex;

    /// <summary>构造：传入 explore 变体组详情。</summary>
    public VariantReviewWindow(ExploreDetails explore)
    {
        _variants = explore.Variants;
        _selectedIndex = 0;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "变体审阅", 960, 640);
        CanResize = true;
        MinWidth = 720;
        MinHeight = 480;

        // 目标行文案（镜像 TUI：无目标=仅对话；无 ChapterId=新建；否则覆盖）
        var target = explore.ApplyTarget;
        TargetText.Text =
            target is null ? "目标：仅对话"
            : string.IsNullOrEmpty(target.ChapterId)
                ? $"目标：新建章节（第 {target.Order} 章「{target.Title}」）"
            : $"目标：覆盖第 {target.Order} 章「{target.Title}」";

        // Tab 条（Label 空 → 变体N；失败 → FAILED 后缀），code-built NavPill
        for (var i = 0; i < _variants.Count; i++)
        {
            var idx = i;
            var label = string.IsNullOrEmpty(_variants[i].Label)
                ? $"变体{i + 1}"
                : _variants[i].Label;
            if (_variants[i].IsFailed)
                label += " [FAILED]";
            var btn = new Button
            {
                Classes = { "NavPill" },
                Padding = new Avalonia.Thickness(16, 12),
                Content = new TextBlock { Text = label, FontSize = 13 },
            };
            btn.Click += (_, _) => SelectIndex(idx);
            _tabButtons.Add(btn);
            TabBar.Children.Add(btn);
        }

        ApplyButton.Content =
            target is null ? "添加到对话"
            : string.IsNullOrEmpty(target.ChapterId) ? "采用并创建章节"
            : "采用并覆盖章节";

        RefreshSelection();
        KeyDown += OnKeyDown;
    }

    private void RefreshSelection()
    {
        // Tab 选中态：Adaline Pill Tab 规范用 Sage Mist wash，不用透明度弱化。
        for (var i = 0; i < _tabButtons.Count; i++)
        {
            _tabButtons[i].Opacity = 1.0;
            _tabButtons[i].Classes.Set("Active", i == _selectedIndex);
        }

        var builder = new ObservableStringBuilder();
        builder.Append(_variants[_selectedIndex].Content);
        ContentMarkdown.MarkdownBuilder = builder;

        // 失败变体不可采用（镜像 TUI CanApply）
        ApplyButton.IsEnabled = !_variants[_selectedIndex].IsFailed;
    }

    private void SelectIndex(int index)
    {
        if (index == _selectedIndex)
            return;
        _selectedIndex = ((index % _variants.Count) + _variants.Count) % _variants.Count;
        RefreshSelection();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                if (_variants.Count > 1)
                    SelectIndex(_selectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Right:
                if (_variants.Count > 1)
                    SelectIndex(_selectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Escape:
                Decision = null; // 显式 Dismiss 语义
                Close();
                e.Handled = true;
                break;
        }
    }

    private void RefineButton_OnClick(object? sender, RoutedEventArgs e) =>
        Confirm(VariantReviewAction.Refine);

    private void ApplyButton_OnClick(object? sender, RoutedEventArgs e) =>
        Confirm(VariantReviewAction.Apply);

    /// <summary>构造当前选中变体的决策并关闭。</summary>
    private void Confirm(VariantReviewAction action)
    {
        if (action == VariantReviewAction.Apply && _variants[_selectedIndex].IsFailed)
            return; // 失败变体不可采用（按钮禁用的双保险）
        var v = _variants[_selectedIndex];
        Decision = new VariantReviewDecision
        {
            Action = action,
            VariantId = v.Id,
            VariantLabel = v.Label,
            VariantContent = v.Content,
        };
        Close();
    }
}
