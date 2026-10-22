using System.Text;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 终端格宽探测回归锁（用户实机：Ghostty + 中文 fallback 字体，悬浮时黑块盖字、缺字）。
///
/// 契约：
/// 1) EAW=A 判定表由 Python unicodedata 机器生成（scripts/gen-ambiguous-table.py），升序不相交；
/// 2) EAW=A 判定与 Unicode 数据一致（含本仓大量使用的 ─ │ █ ● ★ × …；注意 ▸ ✕ ▾ ❯ 是 Neutral，
///    不属于 EAW=A，只能靠逐字形探测覆盖）；
/// 3) WideRuneResolver：emoji 永远 2 格（框架默认语义），其余按探测结果；
/// 4) 探测失败必须回退（不抛异常），行为与本修复前一致。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class TerminalWidthProbeTests
{
    [Fact]
    public void AmbiguousTable_IsSortedAndDisjoint()
    {
        var ranges =
            typeof(TerminalWidthProbe)
                .GetField(
                    "AmbiguousRanges",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
                )!
                .GetValue(null) as (int Start, int End)[];
        Assert.NotNull(ranges);
        Assert.True(ranges!.Length > 50, $"区间数异常：{ranges.Length}");

        var prevEnd = -1;
        foreach (var (start, end) in ranges)
        {
            Assert.True(start <= end, $"区间倒置：0x{start:X4}..0x{end:X4}");
            Assert.True(
                start > prevEnd,
                $"区间重叠或未排序：0x{start:X4}（上一区间止于 0x{prevEnd:X4}）"
            );
            prevEnd = end;
        }
    }

    [Theory]
    // EAW=A：箭头/制表线/方块/几何/标点/数据自带字形（关系名里的 ⇔ 等）
    [InlineData(0x2192, true)] // →
    [InlineData(0x2190, true)] // ←
    [InlineData(0x21D4, true)] // ⇔
    [InlineData(0x21D2, true)] // ⇒
    [InlineData(0x2500, true)] // ─
    [InlineData(0x2502, true)] // │
    [InlineData(0x2588, true)] // █
    [InlineData(0x258C, true)] // ▌
    [InlineData(0x25CF, true)] // ●
    [InlineData(0x2605, true)] // ★
    [InlineData(0x00D7, true)] // ×
    [InlineData(0x2026, true)] // …
    [InlineData(0x2022, true)] // •
    // Neutral / Wide / Narrow：不属 EAW=A
    [InlineData(0x25B8, false)] // ▸（Neutral，靠逐字形探测）
    [InlineData(0x2715, false)] // ✕（Neutral）
    [InlineData(0x276F, false)] // ❯（Neutral）
    [InlineData('A', false)]
    [InlineData(0x4E2D, false)] // 中（Wide，框架本就按 2 格）
    [InlineData(0x1F50D, false)] // 🔍（Wide emoji）
    public void IsAmbiguous_MatchesUnicodeData(int codePoint, bool expected) =>
        Assert.Equal(expected, TerminalWidthProbe.IsAmbiguous(new Rune(codePoint)));

    [Fact]
    public void Resolver_EmojiAlwaysWide_RestFollowsProbe()
    {
        var none = TerminalWidthProbe.BuildResolver(null);
        var classWide = TerminalWidthProbe.BuildResolver(
            new TerminalWidthProbe.ProbeResult(AmbiguousClassWide: true, new HashSet<int>())
        );
        var decorationsWide = TerminalWidthProbe.BuildResolver(
            new TerminalWidthProbe.ProbeResult(
                AmbiguousClassWide: false,
                new HashSet<int> { 0x25B8 } // ▸ 字体回退变宽
            )
        );

        static int Width(Rune rune, Func<Rune, bool> resolver) =>
            TerminalTextUtility.GetRuneWidth(rune, resolver, TerminalTextUtility.DefaultTabWidth);

        // 探测失败 ⇒ 框架默认：EAW=A 按 1 格、emoji 按 2 格
        Assert.Equal(1, Width(new Rune(0x2192), none));
        Assert.Equal(2, Width(new Rune(0x1F50D), none));

        // EAW=A 整类加宽（→ ⇔ ─ █ …），Neutral 装饰字形不受影响
        Assert.Equal(2, Width(new Rune(0x2192), classWide));
        Assert.Equal(2, Width(new Rune(0x21D4), classWide));
        Assert.Equal(2, Width(new Rune(0x2500), classWide));
        Assert.Equal(2, Width(new Rune(0x2588), classWide));
        Assert.Equal(1, Width(new Rune(0x25B8), classWide));

        // 字体回退类：只加宽实测为宽的字形
        Assert.Equal(2, Width(new Rune(0x25B8), decorationsWide));
        Assert.Equal(1, Width(new Rune(0x2192), decorationsWide));
        Assert.Equal(2, Width(new Rune(0x1F50D), decorationsWide));

        // ASCII / CJK 宽字形不受影响
        Assert.Equal(1, Width(new Rune('A'), classWide));
        Assert.Equal(2, Width(new Rune(0x4E2D), classWide));
    }

    [Fact]
    public void Inventory_CoversClosedSetGlyphs_AndProbeTargetsNarrowOnly()
    {
        var inventory = (int[])
            typeof(TerminalWidthProbe)
                .GetField(
                    "InventoryCodePoints",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
                )!
                .GetValue(null)!;
        Assert.True(inventory.Length >= 100, $"清单过小：{inventory.Length}");

        // 面板/对话框/状态栏的关键字形必须在清单里（字体回退时会变宽的那批）
        foreach (
            var cp in new[]
            {
                0x25B8,
                0x2715,
                0x25BE,
                0x276F,
                0x2500,
                0x2588,
                0x2192,
                0x21D4,
                0x00D7,
                0x2026,
            }
        )
            Assert.Contains(cp, inventory);

        // 清单里的宽字形（emoji/W/F）不参与实测加宽；窄字形必须覆盖封闭集
        var narrowInInventory = inventory.Count(cp =>
            Rune.IsValid(cp)
            && TerminalTextUtility.GetRuneWidth(
                new Rune(cp),
                null,
                TerminalTextUtility.DefaultTabWidth
            ) == 1
        );
        Assert.True(narrowInInventory >= 40, $"清单窄字形过少：{narrowInInventory}");
    }

    [Fact]
    public void NoteText_CollectsDataRunes_SkipsNonCandidates()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"myth-glyphs-{Guid.NewGuid():N}.txt");
        Environment.SetEnvironmentVariable("MYTHOLOGY_TUI_GLYPH_CACHE", tmp);
        try
        {
            // 单格数据字形（终端是否应加宽，留给下次启动的 DSR 实测）。
            TerminalWidthProbe.NoteText("陈折花 ⇴ 善我 ‘中文’"); // ⇴ U+21F4：不在封闭集里的数据字形
            // CJK/全角/emoji 已有稳定宽度；VS16/ZWJ 零宽，不能进入「加宽到两格」缓存。
            TerminalWidthProbe.NoteText("中文ＡＢＣ abc 🔍 →\uFE0F\u200D❎");

            var lines = File.ReadAllLines(tmp);
            Assert.Contains("21F4", lines); // ⇴（数据自带、不在封闭集）被收集
            Assert.Contains("2018", lines); // ‘：需要实测而非盲目加宽
            Assert.Contains("2019", lines); // ’：需要实测而非盲目加宽
            Assert.DoesNotContain("4E2D", lines); // 中：CJK 恒定宽
            Assert.DoesNotContain("FF21", lines); // Ａ：全角恒定宽
            Assert.DoesNotContain("4A", lines); // 'J'：ASCII
            Assert.DoesNotContain("21D4", lines); // ⇔：已在封闭集清单里，逐个实测覆盖
            Assert.DoesNotContain("1F50D", lines); // 🔍：emoji 恒定宽
            Assert.DoesNotContain("FE0F", lines); // VS16：零宽格式字符
            Assert.DoesNotContain("200D", lines); // ZWJ：零宽格式字符
            Assert.DoesNotContain("274E", lines); // ❎：框架已按双宽处理
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYTHOLOGY_TUI_GLYPH_CACHE", null);
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    [Theory]
    [InlineData(0x2018, 1, false)] // 弯引号实测单格时不得扩宽
    [InlineData(0x2018, 2, true)] // 框架默认单格、终端实测双格时才扩宽
    [InlineData(0xFE0F, 2, false)] // VS16 零宽
    [InlineData(0x200D, 2, false)] // ZWJ 零宽
    [InlineData(0x274E, 2, false)] // emoji 已由框架默认 resolver 扩宽
    public void ShouldWidenDataRune_RequiresAOneCellCandidateMeasuredAsTwo(
        int codePoint,
        int measuredWidth,
        bool expected
    ) =>
        Assert.Equal(
            expected,
            TerminalWidthProbe.ShouldWidenDataRune(new Rune(codePoint), measuredWidth)
        );

    /// <summary>
    /// 反向不一致（终端把汉字画成 1 格）必须能被探测结果表达出来 —— 这类无法用
    /// WideRuneResolver 修正（只能加宽），只能告警用户改字体；此锁保证字段与传播存在。
    /// </summary>
    [Fact]
    public void ProbeResult_CarriesCjkNarrowFlag()
    {
        var narrow = new TerminalWidthProbe.ProbeResult(
            false,
            new HashSet<int>(),
            CjkRenderedNarrow: true
        );
        var normal = new TerminalWidthProbe.ProbeResult(false, new HashSet<int>());

        Assert.True(narrow.CjkRenderedNarrow);
        Assert.False(normal.CjkRenderedNarrow); // 默认 false，不影响既有语义
    }

    [Fact]
    public void Probe_WithoutTerminalResponse_FallsBackWithoutThrowing()
    {
        var backend = new InMemoryTerminalBackend(new TerminalSize(80, 24), null);
        Terminal.Initialize(
            backend,
            new TerminalOptions { ImplicitStartInput = true },
            force: true
        );
        try
        {
            var result = TerminalWidthProbe.Probe(Terminal.Instance);
            Assert.True(
                result is null,
                $"虚拟终端不应答 DSR ⇒ 必须回退 null（不得瞎猜），实际 {result}"
            );
        }
        finally
        {
            Terminal.Close();
        }
    }
}
