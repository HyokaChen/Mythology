using Core.Data;
using Core.Plugins;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 上下文管理设置标签页：压缩（compact）/ 接续（continuation）模式切换 + 接续参数。
/// 保存即热生效——<see cref="ContextModeSwitcher"/> 重注册接续工具并重建工具搜索索引，无需重启。
/// </summary>
internal sealed class ContextTab(SettingsService cfg, ContextModeSwitcher? switcher)
{
    /// <summary>模式选项（顺序即下拉顺序；Key 必须是 ContextManagementConfig 认得的取值）。</summary>
    private static readonly (string Label, string Key)[] Modes =
    [
        ("压缩（摘要，保留最近若干条）", "compact"),
        ("接续（换页，不丢细节）", "continuation"),
    ];

    private readonly State<int> _modeState = new(IndexOfMode(cfg.ContextManagement.Mode));
    private readonly State<string?> _reminderText = new(
        cfg.ContextManagement.ReminderPercent > 0
            ? cfg.ContextManagement.ReminderPercent.ToString()
            : ""
    );
    private readonly State<bool> _vectorRetrieval = new(cfg.ContextManagement.VectorRetrieval);

    /// <summary>当前选择的模式 key（compact | continuation）。</summary>
    public string SelectedMode => Modes[Math.Clamp(_modeState.Value, 0, Modes.Length - 1)].Key;

    /// <summary>
    /// 构建上下文管理配置（供 SettingsDialog 保存/热切换用）。
    /// 提醒阈值非法时回落到原值，不把垃圾写进配置。
    /// </summary>
    public ContextManagementConfig BuildConfig() =>
        new()
        {
            Mode = SelectedMode,
            ReminderPercent = int.TryParse(_reminderText.Value?.Trim(), out var percent)
                ? percent
                : cfg.ContextManagement.ReminderPercent,
            VectorRetrieval = _vectorRetrieval.Value,
        };

    /// <summary>构建上下文管理标签页的 UI。</summary>
    public Visual Build()
    {
        var modeSelect = new Select<string>()
            .Items(Modes.Select(m => m.Label).ToArray())
            .SelectedIndex(_modeState)
            .Style(SelectStyle.NoBorder);
        modeSelect.HorizontalAlignment = Align.Stretch;

        var reminderBox = new TextBox()
            .Text(_reminderText)
            .Placeholder("0 或留空 = 按模型窗口推导")
            .HorizontalAlignment(Align.Stretch);

        var vectorSwitch = new Switch()
            .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
            .IsOn(_vectorRetrieval);

        var grid = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Fixed(12) },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            )
            .ColumnGap(2)
            .RowGap(1)
            .Cell(new Markup("[dim]模式[/]"), 0, 0)
            .Cell(modeSelect, 0, 1)
            .Cell(new Markup("[dim]换页提醒[/]"), 1, 0)
            .Cell(reminderBox, 1, 1)
            .Cell(new Markup("[dim]向量检索[/]"), 2, 0)
            .Cell(vectorSwitch, 2, 1)
            .HorizontalAlignment(Align.Stretch);

        var notes = new VStack(
            new Markup(
                "[dim]压缩[/]: 上下文接近上限时把早期消息摘要成压缩块（有损，可 /decompress 恢复）。[/]"
            ),
            new Markup(
                "[dim]接续[/]: 接近上限时自动开启新页，旧页原文留在历史里，模型按需检索（不丢细节）。[/]"
            ),
            new Markup(
                switcher is { IsPluginAvailable: false }
                    ? "[yellow]接续插件未加载：只能使用压缩模式。[/]"
                    : "[dim]切换立即生效（工具与检索索引就地重载，无需重启）。[/]"
            )
        ).Spacing(0);

        return new VStack(new HStack(grid, notes).Spacing(3)).Spacing(1);
    }

    private static int IndexOfMode(string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        for (var i = 0; i < Modes.Length; i++)
        {
            if (Modes[i].Key == normalized)
                return i;
        }
        return 0; // 拼错/缺省 → 压缩（与 ContextManagementConfig.ToOptions 的回落一致）
    }
}
