using Core.Data;
using Tui.Controls;
using Tui.Services;
using XenoAtom.Terminal.UI.Styling;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// ThemeService 测试：当前主题管理、切换、循环、持久化、事件。
/// </summary>
public sealed class ThemeServiceTests
{
    private static ThemeService CreateService() => new(new SettingsService(new ConfigStore()));

    [Fact]
    public void ThemeService_DefaultIsDesert()
    {
        var service = CreateService();
        Assert.Equal("desert", service.Current.Id);
        Assert.True(service.IsDark);
    }

    [Fact]
    public void SetTheme_ById_Switches()
    {
        var service = CreateService();
        service.SetTheme("nord");
        Assert.Equal("nord", service.Current.Id);
        Assert.True(service.IsDark); // Nord 为深色
    }

    [Fact]
    public void SetTheme_CaseInsensitive()
    {
        var service = CreateService();
        service.SetTheme("TOKYO-NIGHT");
        Assert.Equal("tokyo-night", service.Current.Id);
    }

    [Fact]
    public void SetTheme_UnknownId_KeepsCurrent()
    {
        var service = CreateService();
        service.SetTheme("does-not-exist");
        Assert.Equal("desert", service.Current.Id);
    }

    [Fact]
    public void NextTheme_CyclesThroughRegistry()
    {
        var service = CreateService();
        Assert.Equal("desert", service.Current.Id);
        service.NextTheme();
        Assert.Equal("desert-light", service.Current.Id); // 注册表顺序第 2
        service.NextTheme();
        Assert.Equal("catppuccin", service.Current.Id); // 第 3
    }

    [Fact]
    public void NextTheme_WrapsAround()
    {
        var service = CreateService();
        for (var i = 0; i < ThemeRegistry.All.Count; i++)
            service.NextTheme();
        Assert.Equal("desert", service.Current.Id); // 循环回起点
    }

    [Fact]
    public void PersistTheme_WritesSettings()
    {
        var settings = new SettingsService(new ConfigStore());
        var service = new ThemeService(settings);
        service.SetTheme("dracula");
        service.PersistTheme();
        Assert.Equal("dracula", settings.ThemeName);
    }

    [Fact]
    public void ThemeChanged_FiresOnSetTheme()
    {
        var service = CreateService();
        var fired = false;
        service.ThemeChanged += () => fired = true;
        service.SetTheme("gruvbox");
        Assert.True(fired);
    }

    [Fact]
    public void ThemeChanged_NotFired_WhenSameTheme()
    {
        var service = CreateService();
        var fired = false;
        service.ThemeChanged += () => fired = true;
        service.SetTheme("desert"); // 已是默认
        Assert.False(fired);
    }

    [Fact]
    public void SetRootVisual_InjectsDefaultButtonStyle_NotVariantStyles()
    {
        var service = CreateService();
        var root = new BackgroundPanel();
        service.SetRootVisual(root);

        // ButtonAccent/Danger/Close 共用同一个 ButtonStyle.Key，注入根视觉会互相覆盖；
        // 根上只应保留通用 Button 样式，变体样式由各按钮构建时用 .Style(...) 局部覆盖。
        Assert.Equal(service.Current.ControlStyles.Button, root.GetStyle<ButtonStyle>());
    }

    [Fact]
    public void SetRootVisual_ThenSetTheme_AppliesTheme()
    {
        var service = CreateService();
        var root = new BackgroundPanel();
        service.SetRootVisual(root);
        service.SetTheme("catppuccin");

        // SetRootVisual 后 Apply 注入根视觉；切主题必须更新 Current 且控件样式包注入根（沿树继承）
        Assert.Equal("catppuccin", service.Current.Id);
        Assert.True(service.IsDark); // Catppuccin Mocha 为深色主题
        Assert.Equal(service.Current.ControlStyles.Button, root.GetStyle<ButtonStyle>());
    }
}
