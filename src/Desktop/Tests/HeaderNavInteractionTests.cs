using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 头部导航交互回归锁（小说/会话切换器 + 主题链路 + 禁用态）。
/// 锁的都是「编译期不报、界面静默退化」的缺陷面：
/// 当前项标记 / 预选、回车打开、Esc 分层、会话行本地时间、深色 SukiUI 表层键翻转、禁用 CTA 灰化、
/// 顶部切换器可切换性（chevron / 全名 tooltip / 键盘焦点 wash）。
/// </summary>
public sealed class HeaderNavInteractionTests
{
    private static string Find(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var parts = new List<string> { dir.FullName, "src", "Desktop" };
            parts.AddRange(segments);
            var candidate = Path.Combine(parts.ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未找到 src/Desktop/{string.Join('/', segments)}");
    }

    private static string Text(params string[] segments) => File.ReadAllText(Find(segments));

    [Fact]
    public void ProjectPicker_CurrentNovel_MarkedAndPreselected()
    {
        var xaml = Text("Controls", "ProjectPickerWindow.axaml");
        Assert.Contains("{Binding IsCurrent}", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"当前\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CountNote\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("←", xaml, StringComparison.Ordinal); // 返回按钮已改用 Lucide ChevronLeft

        var code = Text("Controls", "ProjectPickerWindow.axaml.cs");
        Assert.Contains("currentNovelId", code, StringComparison.Ordinal);
        Assert.Contains("IsCurrent: n.Id == _currentNovelId", code, StringComparison.Ordinal);
        Assert.Contains("ScrollIntoView", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectPicker_Keyboard_EnterOpens_EscapeIsLayered()
    {
        var code = Text("Controls", "ProjectPickerWindow.axaml.cs");
        Assert.Contains("Key.Enter", code, StringComparison.Ordinal);
        Assert.Contains("if (FormPanel.IsVisible)", code, StringComparison.Ordinal);
        Assert.Contains("BackToMain", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionManager_Row_Humanized_WithLocalTimeAndCurrentMark()
    {
        var xaml = Text("Controls", "SessionManagerWindow.axaml");
        Assert.Contains("c:SessionRow", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding IsCurrent}", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"当前\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("models:", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding UpdatedAt", xaml, StringComparison.Ordinal);

        var code = Text("Controls", "SessionManagerWindow.axaml.cs");
        Assert.Contains("ToLocalTime():MM-dd HH:mm", code, StringComparison.Ordinal);
        Assert.Contains("currentSessionId", code, StringComparison.Ordinal);
        Assert.Contains("record SessionRow(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Switchers_Footer_ShowMonoCount()
    {
        Assert.Contains(
            "x:Name=\"CountNote\"",
            Text("Controls", "SessionManagerWindow.axaml"),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "CountNote.Text",
            Text("Controls", "SessionManagerWindow.axaml.cs"),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "CountNote.Text",
            Text("Controls", "ProjectPickerWindow.axaml.cs"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ThemeService_Dark_FollowsSukiSurfaceKeys()
    {
        var code = Text("Services", "DesktopThemeService.cs");
        Assert.Contains("DarkSurfaceColors", code, StringComparison.Ordinal);
        Assert.Contains("DarkSurfaceBrushes", code, StringComparison.Ordinal);
        Assert.Contains("SukiCardBackground", code, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutPresenterBackground", code, StringComparison.Ordinal);
        Assert.Contains("_lightSurfaceSnapshot", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeButton_Tooltip_IsStateful()
    {
        Assert.Contains(
            "{Binding ThemeToggleTip}",
            Text("Views", "MainWindow.axaml"),
            StringComparison.Ordinal
        );
        var vm = Text("ViewModels", "MainWindowViewModel.cs");
        Assert.Contains("ThemeToggleTip", vm, StringComparison.Ordinal);
        Assert.Contains("nameof(ThemeToggleTip)", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledButtons_AreQuieted()
    {
        var styles = Text("Styles", "MythologyStyles.axaml");
        Assert.Contains("Button.Primary:disabled", styles, StringComparison.Ordinal);
        Assert.Contains("Button.Ghost:disabled", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_Switchers_HaveChevronAndFullTitleTips()
    {
        var xaml = Text("Views", "MainWindow.axaml");
        Assert.Contains("{Binding NovelPillTip}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding SessionPillTip}", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("Kind=\"ChevronDown\"").Length - 1);
        Assert.Contains("设置（Ctrl+P）", xaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"18,9,14,9\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_SwitcherTips_CarryNameAndShortcut()
    {
        var vm = Text("ViewModels", "MainWindowViewModel.cs");
        Assert.Contains("nameof(NovelPillTip)", vm, StringComparison.Ordinal);
        Assert.Contains("nameof(SessionPillTip)", vm, StringComparison.Ordinal);
        Assert.Contains("当前小说：", vm, StringComparison.Ordinal);
        Assert.Contains("当前会话：", vm, StringComparison.Ordinal);
        Assert.Contains("（Ctrl+O）", vm, StringComparison.Ordinal);
        Assert.Contains("（Ctrl+E）", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void NavPill_HasKeyboardFocusWash()
    {
        var styles = Text("Styles", "MythologyStyles.axaml");
        Assert.Contains("Button.NavPill:focus-visible", styles, StringComparison.Ordinal);
    }
}
