using System.Xml.Linq;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 自动更新 UI 结构锁（XAML 静态断言，不依赖 Avalonia 运行时）：
/// 遮罩 IsHitTestVisible=False（吞整窗点击的回归锁）、弹窗挂载 MainWindow、设置页「关于」Tab 入口。
/// </summary>
public sealed class UpdatePromptDialogStructureTests
{
    private static XElement Load(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. segments]);
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(Path.Combine(segments));
    }

    [Fact]
    public void PromptMask_IsHitTestInvisible_AndRootPanelHasFallback()
    {
        var root = Load("src", "Desktop", "Controls", "UpdatePromptDialog.axaml");

        // 遮罩必须显式 IsHitTestVisible=False —— 否则会吞掉整窗点击（参考实现记录过的缺陷）
        var mask = root.Descendants()
            .SingleOrDefault(e =>
                e.Name.LocalName == "Border" && e.Attribute("IsHitTestVisible")?.Value == "False"
            );
        Assert.NotNull(mask);

        // 根面板 IsVisible 必须带 FallbackValue=False（DataContext 为 null 时不得整窗盖遮罩）
        var panel = root.Descendants().Single(e => e.Name.LocalName == "Panel");
        var isVisible = panel.Attribute("IsVisible")?.Value ?? string.Empty;
        Assert.Contains("IsPromptVisible", isVisible, StringComparison.Ordinal);
        Assert.Contains("FallbackValue=False", isVisible, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_MountsUpdatePromptOverlay()
    {
        var root = Load("src", "Desktop", "Views", "MainWindow.axaml");

        var dialog = root.Descendants().Single(e => e.Name.LocalName == "UpdatePromptDialog");
        Assert.Equal("0", dialog.Attribute("Grid.Row")?.Value);
        Assert.Equal("4", dialog.Attribute("Grid.RowSpan")?.Value);
        Assert.Contains(
            "UpdatePrompt",
            dialog.Attribute("DataContext")?.Value ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void SettingsView_HasAboutTabWithUpdateEntry()
    {
        var root = Load("src", "Desktop", "Views", "SettingsView.axaml");

        var aboutTab = root.Descendants()
            .Single(e => e.Name.LocalName == "TabItem" && e.Attribute("Header")?.Value == "关于");
        var button = aboutTab.Descendants().Single(e => e.Name.LocalName == "Button");
        Assert.Contains(
            "PrimaryActionCommand",
            button.Attribute("Command")?.Value ?? string.Empty,
            StringComparison.Ordinal
        );
    }
}
