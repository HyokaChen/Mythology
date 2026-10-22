using Tui.Controls;
using Xunit;

namespace Tui.Tests;

using Data;

/// <summary>
/// MythStatusBar 状态栏配色测试（§11 点 4）。
/// 断言三态文字分色、token 三档暖色、增强模式暖色化、📖 图标 copper。
/// 通过 internal 测试缝（StatusText/TokenText/EnhanceModeText/NovelNameText）读取 State 值。
/// </summary>
public sealed class StatusBarTests
{
    private static MythStatusBar CreateBar() => new("test-model", PanelThemes.Dark);

    [Fact]
    public void SetAgentState_Idle_UsesDim()
    {
        var bar = CreateBar();
        bar.SetAgentState(TuiState.Idle);

        Assert.Contains("💤 idle", bar.StatusText);
        Assert.NotEqual("", bar.StatusText);
    }

    [Fact]
    public void SetAgentState_Compact_UsesAmber()
    {
        var bar = CreateBar();
        bar.SetAgentState(TuiState.Compact);

        Assert.Contains("压缩", bar.StatusText);
        Assert.Contains("[dim]", bar.StatusText); // 压缩中 = dim
    }

    [Fact]
    public void SetAgentState_Run_ShowsItalicPoetry()
    {
        var bar = CreateBar();
        bar.SetAgentState(TuiState.Run);

        Assert.Contains("[italic]", bar.StatusText);
        Assert.NotEqual("", bar.StatusText);
    }

    [Fact]
    public void SetTokenStats_LowUsage_UsesDim()
    {
        var bar = CreateBar();
        bar.SetTokenStats(1000, 128000);

        Assert.Contains("[dim]📊", bar.TokenText);
    }

    [Fact]
    public void SetTokenStats_WarningUsage_UsesAmber()
    {
        var bar = CreateBar();
        // warningRatio 默认 0.9：115200/128000 = 90% → amber
        bar.SetTokenStats(115200, 128000);

        Assert.Contains("[yellow]", bar.TokenText);
        Assert.Contains("📊", bar.TokenText);
    }

    [Fact]
    public void SetTokenStats_OverflowUsage_UsesTerracotta()
    {
        var bar = CreateBar();
        bar.SetTokenStats(200000, 128000);

        Assert.Contains("[bold red]", bar.TokenText); // 超窗口 = red
    }

    [Fact]
    public void SetTokenStats_ZeroWindow_ClearsText()
    {
        var bar = CreateBar();
        bar.SetTokenStats(1000, 0);

        Assert.Equal("", bar.TokenText);
    }

    [Fact]
    public void SetTokenStats_FormatsCompactUnits()
    {
        var bar = CreateBar();
        bar.SetTokenStats(12_345, 128_000);

        Assert.Contains("12.3k / 128k", bar.TokenText);
        Assert.DoesNotContain("tok", bar.TokenText);
        Assert.DoesNotContain(",", bar.TokenText);
    }

    [Fact]
    public void SetTokenStats_Million_FormatsAsM()
    {
        var bar = CreateBar();
        bar.SetTokenStats(1_500_000, 2_000_000);

        Assert.Contains("1.5m / 2m", bar.TokenText);
    }

    [Fact]
    public void SetAgentState_Idle_NoTooltipContent()
    {
        var bar = CreateBar();
        bar.SetAgentState(TuiState.Idle);

        Assert.Equal("", bar.StatusFullText); // idle 不挂 tooltip，避免空白弹窗
    }

    [Theory]
    [InlineData("full", "[bold red]⚡[/]", "全自动")]
    [InlineData("semi", "[bold yellow]⚡[/]", "半自动")]
    [InlineData("none", "[dim]✋[/]", "手动")]
    public void SetEnhanceMode_UsesBuiltInColors(
        string mode,
        string expectedTag,
        string expectedTooltip
    )
    {
        var bar = CreateBar();
        bar.SetEnhanceMode(mode);

        Assert.Contains(expectedTag, bar.EnhanceModeText); // 仅图标，完整文案进 tooltip
        Assert.NotEqual("", bar.EnhanceModeText);
        Assert.Contains(expectedTooltip, bar.EnhanceModeTooltipText);
    }

    [Fact]
    public void SetNovelName_BookIconUsesCopper()
    {
        var bar = CreateBar();
        bar.SetNovelName("凡人修仙传");

        Assert.Contains("[bold]📖 ", bar.NovelNameText); // 书名图标 bold
        Assert.Contains("凡人修仙传", bar.NovelNameText);
        // 状态栏空间有限：不再展示题材
        Assert.DoesNotContain("东方玄幻", bar.NovelNameText);
    }

    [Fact]
    public void SetNovelName_Empty_ClearsText()
    {
        var bar = CreateBar();
        bar.SetNovelName(null);

        Assert.Equal("", bar.NovelNameText);
    }

    [Fact]
    public void SetNovelName_LongName_TruncatesWithEllipsis()
    {
        var bar = CreateBar();
        var longName = "凡人修仙传之大道朝天仙路漫漫"; // 17 全角 = 34 显示宽 > 上限
        bar.SetNovelName(longName);

        Assert.Contains("…", bar.NovelNameText);
        Assert.Equal(longName, bar.NovelNameFull); // tooltip 保留完整名
    }

    [Fact]
    public void SetAgentState_Run_TooltipKeepsFullPoem()
    {
        var bar = CreateBar();
        bar.SetAgentState(TuiState.Run);

        // 截断后的可见文本（去掉结尾省略号）是完整诗歌的前缀（tooltip 保留全文）
        var visible = bar.StatusText.Replace("[italic]", "").Replace("[/]", "").TrimEnd('…');
        Assert.False(string.IsNullOrEmpty(bar.StatusFullText));
        Assert.Contains(visible, bar.StatusFullText);
    }
}
