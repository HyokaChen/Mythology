using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Desktop.Models;

namespace Desktop.Controls;

/// <summary>
/// 实体筛选对话框（对齐 TUI EntityFilterDialog）：1~2 个维度（下拉）+ 清除 / 应用。
/// 维度与选项由 <c>SetUpPanelViewModel.BuildEntityFilterFields</c> 按类型与当前数据构建；
/// 关键字仍只有搜索框一个输入口（一个条件一个输入口）。
/// </summary>
public partial class EntityFilterDialog : Window
{
    private readonly Action<EntityListFilter> _onApply;
    private readonly IReadOnlyList<(string Label, string? Value)> _primaryOptions;
    private readonly ComboBox _primaryBox;
    private readonly IReadOnlyList<(string Label, string? Value)>? _secondaryOptions;
    private readonly ComboBox? _secondaryBox;

    public EntityFilterDialog(
        string sectionName,
        EntityFilterField primary,
        EntityFilterField? secondary,
        EntityListFilter current,
        Action<EntityListFilter> onApply
    )
    {
        _onApply = onApply;
        _primaryOptions = primary.Options;

        InitializeComponent();
        Title = $"筛选{sectionName}";

        _primaryBox = AddRow(primary.Label, primary.Options, current.Primary);
        if (secondary is not null)
        {
            _secondaryOptions = secondary.Options;
            _secondaryBox = AddRow(secondary.Label, secondary.Options, current.Secondary);
        }

        ClearButton.Click += (_, _) =>
        {
            Close();
            onApply(EntityListFilter.None);
        };
        ApplyButton.Click += (_, _) =>
        {
            Close();
            onApply(
                new EntityListFilter(
                    ValueAt(_primaryOptions, _primaryBox.SelectedIndex),
                    _secondaryBox is null
                        ? null
                        : ValueAt(_secondaryOptions!, _secondaryBox.SelectedIndex)
                )
            );
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    /// <summary>一行维度：Eyebrow 标签 + Adaline 下拉（预选当前值）。</summary>
    private ComboBox AddRow(
        string label,
        IReadOnlyList<(string Label, string? Value)> options,
        string? currentValue
    )
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = options.Select(o => o.Label).ToArray(),
            SelectedIndex = IndexOf(options, currentValue),
        };
        // 代码构建控件同样显式挂 Adaline 模板（与 EntityEditWindow.BuildSelect 同款）
        if (
            Application.Current!.TryFindResource("AdalineComboBox", out var theme)
            && theme is Avalonia.Styling.ControlTheme controlTheme
        )
            combo.Theme = controlTheme;

        var caption = new TextBlock { Text = label };
        caption.Classes.Add("Eyebrow");

        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(caption);
        row.Children.Add(combo);
        FieldRows.Children.Add(row);
        return combo;
    }

    /// <summary>当前值 → 选项索引（null = 首项「全部」；值不在选项里同样回落首项）。</summary>
    private static int IndexOf(IReadOnlyList<(string Label, string? Value)> options, string? value)
    {
        if (value is null)
            return 0;
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Value, value, StringComparison.Ordinal))
                return i;
        }
        return 0;
    }

    private static string? ValueAt(
        IReadOnlyList<(string Label, string? Value)> options,
        int index
    ) => options[Math.Clamp(index, 0, options.Count - 1)].Value;
}
