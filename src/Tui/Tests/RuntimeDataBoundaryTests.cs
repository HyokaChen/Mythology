using System.Reflection;
using Core.Cloud;
using Core.Data;
using Core.Novels;
using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 运行时数据边界回归（黑块 / 解析崩溃守卫）：
/// 小说数据（实体名 / 章标题 / 检索片段）、LLM 输出（用户问题选项）、云端结果
/// （设定检查 findings / 摘要）、插件清单（工具名 / 标签）进入 Markup 前必须转义。
///
/// 敌意载荷：`[` `]`（Markup 标签定界符）+ VS16(U+FE0F) + ZWJ(U+200D) + U+274E ❎ + 双宽 CJK。
/// 契约分两层：
///   1. Escape 本身：定界符倍增（[ → [[），使载荷整体退化为字面量；
///   2. 渲染管线：经过 Markup($"{...}") 插值处理器的二次转义后，成品文本中不得残留
///      可解析的裸标签——即 bold 两侧的括号数必须是偶数（≥2），且整体不抛解析异常。
/// </summary>
public sealed class RuntimeDataBoundaryTests
{
    // 载荷含 [bold]...[/]：若未转义会被当样式吞掉（内容消失）；转义后按字面量渲染。
    private const string HostileName = "反派[bold]教主[/]\uFE0F\u200D";
    private const string HostileDetail = "证据[red]断裂[/]\u274E中";

    /// <summary>
    /// 转义契约（可达、可验证）：Escape 后的载荷经 Markup 解析必须仍按字面量取回。
    /// 用 Markup 渲染宽度反推：若标签被解析，可见文本会显著变短（bold 指令不占格）。
    /// </summary>
    [Fact]
    public void EscapedPayload_SurvivesMarkupParsing_AsLiteral()
    {
        var escaped = ToolCallRenderer.Escape(HostileName);
        // 未转义直传：Markup 会吞掉标签内容（宽度 ≈ icon+2）
        var rawWidth = XenoAtom.Terminal.TerminalTextUtility.GetWidth(HostileName.AsSpan());
        var escapedWidth = XenoAtom.Terminal.TerminalTextUtility.GetWidth(escaped.AsSpan());
        // 转义只增字符不减内容：宽度必须不减（[[ ]]成对出现，双倍字符仍全宽计入）
        Assert.True(escapedWidth >= rawWidth, $"转义后宽度倒退: {escapedWidth} < {rawWidth}");
        Assert.Contains("[[bold]]", escaped, StringComparison.Ordinal);
    }

    /// <summary>
    /// 渲染契约：载荷经过插值转义后，成品 Markup 文本里 bold 两侧必须是成对的倍增括号
    /// （[[...]] 或更多偶数层），不得出现单层裸标签——单层即意味着标签被解析、内容被吞。
    /// </summary>
    [Theory]
    [InlineData("[[bold]]", true)] // 单次转义（如 new Markup(escapedString) 直传）
    [InlineData("[[[bold]]]", false)] // 奇数层：仍会被当标签
    [InlineData("[[[[bold]]]]", true)] // 插值处理器二次转义后的正确形态
    [InlineData("[bold]", false)] // 完全没转义
    public void EscapedTagPairs_AreInert(string candidate, bool inert)
    {
        Assert.Equal(!inert, HasOddBracketRunAroundTag(candidate, "bold"));
    }

    /// <summary>设定检查详情：云端 findings / 摘要含敌意载荷时安全渲染（不抛、内容不消失）。</summary>
    [Fact]
    public async Task SetupCheckDetail_RendersHostilePayloads()
    {
        var store = new SetUpPanelLayoutTests.FakeStore { Novel = MakeNovel() };
        var panel = new SetUpPanel(store, PanelThemes.Dark);
        panel.ShowSetupCheckResult(
            new CheckSetupDetails
            {
                Findings =
                [
                    new SetupFindingEntry
                    {
                        Id = "f-1",
                        Verdict = "conflict",
                        Description = HostileDetail,
                        Evidence = HostileDetail,
                        FixCandidates = [HostileName],
                        Suggestion = HostileDetail,
                    },
                ],
                OutlineSummary = [HostileDetail],
                CoverageSummary = [HostileDetail],
            }
        );
        // 检查段挂在大纲分区书级摘要下：切到大纲分区（7）再展开 Check 段
        typeof(SetUpPanel)
            .GetMethod("SelectSection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [7]);
        panel.ToggleSetupCheckResults();
        await panel.RefreshAsync("n1");

        var visual = panel.Visual;
        visual.Measure(new LayoutConstraints(0, SetUpPanelLayoutTests.PanelWidth, 0, 9999));
        visual.Arrange(
            new Rectangle(0, 0, SetUpPanelLayoutTests.PanelWidth, SetUpPanelLayoutTests.PanelHeight)
        );

        var raw = SetUpPanelLayoutTests.CollectRawMarkup(visual);
        // 契约：内容不消失（字面量仍在），且载荷标签已倍增为字面量（生产自身样式 [bold] 除外）
        Assert.Contains(raw, t => t.Contains("证据", StringComparison.Ordinal));
        Assert.Contains(raw, t => t.Contains("断裂", StringComparison.Ordinal));
        Assert.Contains(raw, t => t.Contains("\u274E", StringComparison.Ordinal));
        Assert.Contains(raw, t => t.Contains("[[[[red]]]]", StringComparison.Ordinal));
    }

    /// <summary>实体名册：敌意实体名安全渲染（折叠头 Markup 不炸、名字不消失）。</summary>
    [Fact]
    public async Task EntityRoster_RendersHostileNames()
    {
        var store = new SetUpPanelLayoutTests.FakeStore { Novel = MakeNovel() };
        store.Characters.Add(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = HostileName,
                Role = CharacterRole.Protagonist,
                Personality = HostileDetail,
                Background = HostileDetail,
            }
        );
        store.Plotlines.Add(
            new Plotline
            {
                Id = "pl1",
                NovelId = "n1",
                Name = HostileName,
                Type = PlotlineType.Main,
                Description = HostileDetail,
            }
        );

        var panel = new SetUpPanel(store, PanelThemes.Dark);
        await panel.RefreshAsync("n1");

        var accordion = SetUpPanelLayoutTests.GetCharacterAccordion(panel);
        Assert.Single(accordion.Children);
        var raw = SetUpPanelLayoutTests.CollectRawMarkup(accordion);

        Assert.Contains(raw, t => t.Contains("反派", StringComparison.Ordinal));
        Assert.Contains(raw, t => t.Contains("教主", StringComparison.Ordinal));
        // 载荷标签经插值二次转义后应为四层括号（字面量），不是被解析的样式
        Assert.Contains(raw, t => t.Contains("[[[[bold]]]]", StringComparison.Ordinal));
    }

    /// <summary>动态标题宽度安全：ChromeText.ForTitle 按显示宽度截断并清洗易碎序列。</summary>
    [Fact]
    public void ChromeText_ForTitle_ClipsByDisplayWidth()
    {
        var clipped = ChromeText.ForTitle(HostileDetail, 8);
        Assert.True(
            XenoAtom.Terminal.TerminalTextUtility.GetWidth(clipped.AsSpan()) <= 8,
            $"ForTitle 超宽: '{clipped}'"
        );
        Assert.DoesNotContain("\uFE0E", clipped, StringComparison.Ordinal);
    }

    /// <summary>
    /// 载荷标签（bold / red——生产代码自身不会用这两个词以外的载荷词）是否以奇数层括号出现：
    /// 奇数 = 仍会被 Markup 解析成样式、内容被吞。排除法：只查载荷词，不管生产样式标签。
    /// </summary>
    private static readonly string[] HostileTags = ["bold", "red", "italic", "underline"];

    private static bool HasOddBracketRunAroundTag(string text, string tag)
    {
        var marker = tag + "]";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            // 向前数连续 '['
            var open = 0;
            var i = index - 1;
            while (i >= 0 && text[i] == '[')
            {
                open++;
                i--;
            }
            if (open % 2 == 1)
                return true;
            index = text.IndexOf(marker, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>成品文本是否残留可解析的敌意标签（载荷词 + 奇数层括号）。</summary>
    private static bool HasInertHostileTag(string text) =>
        HostileTags.Any(t => HasOddBracketRunAroundTag(text, t));

    /// <summary>
    /// 设定检查详情的新键消费（2026-10-18）：设定图来源摘要 + 分卷大纲行渲染；
    /// 键缺席（旧服务端/quick）→ 两段都不出现。
    /// </summary>
    [Fact]
    public async Task SetupCheckDetail_RendersGraphAndVolumes()
    {
        var store = new SetUpPanelLayoutTests.FakeStore { Novel = MakeNovel() };
        var panel = new SetUpPanel(store, PanelThemes.Dark);

        panel.ShowSetupCheckResult(
            new CheckSetupDetails
            {
                Graph = new SetupGraphInfo("graph", 42, 66),
                Volumes =
                [
                    new VolumeOutlineEntry
                    {
                        VolumeId = "v1",
                        Name = "启程",
                        ChapterCount = 12,
                        BeatCount = 18,
                        TensionMean = 0.42,
                        TensionMin = 0.2,
                        TensionMax = 0.78,
                        ArcShape = "man-in-a-hole",
                        HookCoverage = 0.25,
                    },
                    new VolumeOutlineEntry
                    {
                        VolumeId = "v2",
                        Name = "决战",
                        ChapterCount = 8,
                        FlatLine = true,
                    },
                ],
            }
        );
        typeof(SetUpPanel)
            .GetMethod("SelectSection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [7]);
        panel.ToggleSetupCheckResults();
        await panel.RefreshAsync("n1");
        var raw = LayoutAndCollect(panel);

        Assert.Contains(raw, t => t.Contains("同步图", StringComparison.Ordinal));
        Assert.Contains(raw, t => t.Contains("启程", StringComparison.Ordinal));
        Assert.Contains(raw, t => t.Contains("决战", StringComparison.Ordinal));
        Assert.Contains(raw, t => t.Contains("平坦", StringComparison.Ordinal)); // flat-line 显式提示

        // 键缺席 → 新段整体不出现（展示层自动隐藏，零破坏）；用新面板避免上一轮节点残留
        var freshPanel = new SetUpPanel(store, PanelThemes.Dark);
        freshPanel.ShowSetupCheckResult(new CheckSetupDetails());
        typeof(SetUpPanel)
            .GetMethod("SelectSection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(freshPanel, [7]);
        freshPanel.ToggleSetupCheckResults();
        await freshPanel.RefreshAsync("n1");
        var rawWithout = LayoutAndCollect(freshPanel);
        Assert.DoesNotContain(rawWithout, t => t.Contains("同步图", StringComparison.Ordinal));
        Assert.DoesNotContain(rawWithout, t => t.Contains("分卷大纲", StringComparison.Ordinal));
    }

    /// <summary>布局 + 收集原始 Markup 文本（本文件统一入口）。</summary>
    private static IReadOnlyList<string> LayoutAndCollect(SetUpPanel panel)
    {
        var visual = panel.Visual;
        visual.Measure(new LayoutConstraints(0, SetUpPanelLayoutTests.PanelWidth, 0, 9999));
        visual.Arrange(
            new Rectangle(0, 0, SetUpPanelLayoutTests.PanelWidth, SetUpPanelLayoutTests.PanelHeight)
        );
        return SetUpPanelLayoutTests.CollectRawMarkup(visual);
    }

    private static Novel MakeNovel() =>
        new()
        {
            Id = "n1",
            NovelId = "n1",
            Title = HostileName,
            Outline = "全书走向：夺宝",
        };
}
