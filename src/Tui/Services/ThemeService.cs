using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;

namespace Tui.Services;

/// <summary>
/// 主题服务 — 管理当前主题（<see cref="ThemeRegistry"/> 中的定义）与切换。
/// 切换即时生效并触发 ThemeChanged（TuiApp.ApplyTheme 刷新各面板配色）；
/// 持久化由调用方在用户确认后调用 <see cref="PersistTheme"/> + SettingsService.SaveAllAsync。
/// </summary>
public sealed class ThemeService
{
    private readonly SettingsService _settings;
    private Visual? _rootVisual;

    /// <summary>当前主题。</summary>
    public ThemeDefinition Current { get; private set; }

    /// <summary>是否深色主题（由当前主题决定）。</summary>
    public bool IsDark => Current.IsDark;

    /// <summary>主题更改事件。</summary>
    public event Action? ThemeChanged;

    /// <summary>
    /// 初始化主题服务：启动时从持久化配置恢复主题，未配置或失效时回退 Desert。
    /// </summary>
    /// <param name="settings">设置服务（需已 LoadAll）。</param>
    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        Current = ThemeRegistry.Get(settings.ThemeName) ?? ThemeRegistry.Desert;
    }

    /// <summary>按 Id 切换主题（即时生效，不持久化）。</summary>
    public void SetTheme(string id)
    {
        if (ThemeRegistry.Get(id) is { } theme)
            SetTheme(theme);
    }

    /// <summary>切换主题（即时生效，不持久化）。</summary>
    private void SetTheme(ThemeDefinition theme)
    {
        if (ReferenceEquals(Current, theme))
            return;
        Current = theme;
        Apply();
        ThemeChanged?.Invoke();
    }

    /// <summary>循环到下一个主题（/theme toggle、命令面板）。</summary>
    public void NextTheme()
    {
        var all = ThemeRegistry.All;
        var idx = all.TakeWhile(t => !ReferenceEquals(t, Current)).Count();
        SetTheme(all[(idx + 1) % all.Count]);
    }

    /// <summary>持久化当前主题选择（写入配置内存，由调用方 SaveAllAsync 落盘）。</summary>
    public void PersistTheme() => _settings.ThemeName = Current.Id;

    /// <summary>设置根视觉元素，并立即应用当前主题（保证启动即生效）。</summary>
    /// <param name="root">根 Visual。</param>
    public void SetRootVisual(Visual root)
    {
        _rootVisual = root;
        Apply();
    }

    /// <summary>应用当前主题：更新全局当前配色、控件主题与控件样式包（注入根视觉，控件继承）。</summary>
    private void Apply()
    {
        if (_rootVisual is null)
            return;
        PanelThemes.Current = Current.Colors; // 构建时快照用（弹窗/动态控件）
        _rootVisual.Style(Current.UiTheme); // 控件主题
        // 控件样式注入根：局部无覆盖的控件渲染时 GetStyle 沿树继承，主题切换全量刷新
        var s = Current.ControlStyles;
        _rootVisual.SetStyle(s.Dialog);
        _rootVisual.SetStyle(s.TabControl);
        _rootVisual.SetStyle(s.Collapsible);
        _rootVisual.SetStyle(s.Button);
        _rootVisual.SetStyle(s.Switch);
        // 弹层家族（Popup / OptionList / ListBox / MenuList / Tooltip）：不注入时框架用默认主题，
        // 弹层表面会透基础底、边缘留中性冷灰——在暖色主题里就是「一块黑」（用户实机报障）。
        PanelThemes.InjectOverlayStyles(_rootVisual, Current.Colors);
        // 终端默认底色随主题走（未写格/BCE 擦除格取该色）
        if (Terminal.Instance is { } terminal)
            TuiApp.ApplyTerminalDefaultColors(terminal);
    }
}
