using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 主题选择对话框：列表 + 真彩色块预览。
/// ↑/↓ 选择即时预览切换（不持久化），Enter 应用并保存；Esc / 右上角关闭按钮 = 恢复打开前的主题。
/// </summary>
public static class ThemePickerDialog
{
    /// <summary>
    /// 弹出主题选择对话框。
    /// </summary>
    /// <param name="themeService">主题服务（预览切换与持久化入口）。</param>
    /// <param name="settingsService">设置服务（回车时落盘）。</param>
    public static void Show(ThemeService themeService, SettingsService settingsService)
    {
        var openId = themeService.Current.Id; // Esc 取消时恢复

        var list = new OptionList<ThemeDefinition>()
            .Items(ThemeRegistry.All)
            .ItemTemplate(
                new DataTemplate<ThemeDefinition>(
                    Display: (value, in _) =>
                    {
                        var t = value.GetValue();
                        var isCurrent = themeService.Current.Id == t.Id;
                        return new HStack(
                            Swatch(t.Tokens.Bg0),
                            Swatch(t.Tokens.Bg1),
                            Swatch(t.Tokens.Bg2),
                            Swatch(t.Tokens.Accent),
                            Swatch(t.Tokens.Fg0),
                            new Markup($"[primary]{t.Name}[/]"),
                            new Markup($"[dim]{t.Tokens.Description}[/]"),
                            new Markup(isCurrent ? "[green] ✓[/]" : "")
                        ).Spacing(1);
                    },
                    Editor: null
                )
            )
            .ActivateOnClick(true);

        var idx = ThemeRegistry.All.TakeWhile(t => t.Id != themeService.Current.Id).Count();
        list.SelectedIndex = idx;
        list.HorizontalAlignment = Align.Stretch;
        list.AutoFocus = true;

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内
            .Title("🎨 主题")
            .BottomLeftText(new Markup("[#C7B299]↑/↓ 即时预览 · Enter 应用并保存 · Esc 取消[/]"))
            .Width(56)
            .MinHeight(12)
            .Padding(new Thickness(2, 1, 2, 1));

        // 选中变化 → 即时切换（预览，不持久化）
        list.SelectionChangedRouted += (_, _) =>
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < ThemeRegistry.All.Count)
            {
                themeService.SetTheme(ThemeRegistry.All[list.SelectedIndex].Id);
            }
        };

        // Enter → 持久化（回车才存）+ 关闭
        list.ItemActivatedRouted += (_, _) =>
        {
            themeService.PersistTheme();
            _ = settingsService.SaveThemeAsync();
            dialog.Close();
        };

        // Esc / 右上角关闭按钮 → 恢复打开时的主题（不持久化）
        void Cancel()
        {
            themeService.SetTheme(openId);
            dialog.Close();
        }

        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    Cancel();
                    e.Handled = true;
                }
            }
        );

        var closeBtn = PanelThemes.CreateDialogCloseButton(Cancel);
        dialog.TopRightText(closeBtn);

        dialog.Content(PanelThemes.CreateDialogContent(list));
        dialog.Show();
        dialog.App?.Focus(list);
    }

    /// <summary>真彩色块（前景色渲染 "██"，色块预览用）。</summary>
    private static TextBlock Swatch(Color c) =>
        new TextBlock("██").Style(new TextBlockStyle { Foreground = c });
}
