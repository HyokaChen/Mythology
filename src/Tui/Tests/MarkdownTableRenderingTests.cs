using System.Reflection;
using Tui.Controls;
using Xunit;

namespace Tui.Tests;

public class MarkdownTableRenderingTests
{
    // ── WrapCell (tokenization + wrapping) ────────────────────

    [Fact]
    public void WrapCell_CjkCharacters_EachIsBreakPoint()
    {
        var lines = WrapCell("你好", 4);
        Assert.Single(lines);
        Assert.Equal("你好", lines[0]);

        lines = WrapCell("你好世界", 4);
        Assert.Equal(2, lines.Count);
        Assert.Equal("你好", lines[0]);
        Assert.Equal("世界", lines[1]);
    }

    [Fact]
    public void WrapCell_AsciiWord_StaysIntact()
    {
        var lines = WrapCell("hello world here", 10);
        Assert.Equal(2, lines.Count);
        Assert.Equal("hello", lines[0]);
        Assert.Equal("world here", lines[1]);
    }

    [Fact]
    public void WrapCell_BoldSpan_StaysIntact()
    {
        var lines = WrapCell("**bold text** more", 15);
        Assert.Single(lines);
        Assert.Equal("**bold text** more", lines[0]);

        lines = WrapCell("**bold text** more", 12);
        Assert.Equal(2, lines.Count);
        Assert.Equal("**bold text**", lines[0]);
        Assert.Equal("more", lines[1]);
    }

    [Fact]
    public void WrapCell_EmojiStars()
    {
        var lines = WrapCell("⭐⭐⭐ text", 10);
        Assert.Equal(2, lines.Count);
        Assert.Equal("⭐⭐⭐", lines[0]);
        Assert.Equal("text", lines[1]);
    }

    [Fact]
    public void WrapCell_EmptyReturnsSingleEmpty()
    {
        var lines = WrapCell("", 10);
        Assert.Single(lines);
        Assert.Equal("", lines[0]);
    }

    [Fact]
    public void WrapCell_CjkClosingPunctMovesToPreviousLine()
    {
        // At wide enough target, ， fits on previous line
        var lines = WrapCell("你好，世界", 6);
        Assert.All(
            lines,
            l => Assert.False(l.StartsWith('\uff0c'), "， should fit on line 1 at width 6")
        );

        // At very narrow target, ， may stay at line start rather than overflow
        lines = WrapCell("你好，世界", 4);
        Assert.True(lines.Count >= 2, "Should wrap to multiple lines");
    }

    [Fact]
    public void WrapCell_CjkOpeningPunctMovesToNextLine()
    {
        // At wide enough target, （ stays with following text
        var lines = WrapCell("你好（世界", 6);
        Assert.All(
            lines,
            l => Assert.False(l.EndsWith('\uff08'), "（ should not end a line at width 6")
        );

        // At narrow target, （ may end a line rather than overflow
        lines = WrapCell("你好（世界", 4);
        Assert.True(lines.Count >= 2, "Should wrap to multiple lines");
    }

    // ── ComputeTargetWidths ────────────────────────────────────

    [Fact]
    public void ComputeTargetWidths_FitsNaturally_NoChange()
    {
        int[] natural = [10, 20, 30];
        var needFallback = ComputeTargetWidths(natural, 3, 100, out var targets);
        Assert.False(needFallback);
        Assert.Equal(natural, targets);
    }

    [Fact]
    public void ComputeTargetWidths_NeedsShrinking()
    {
        int[] natural = [10, 20, 30];
        var needFallback = ComputeTargetWidths(natural, 3, 55, out var targets);
        Assert.False(needFallback);
        for (var i = 0; i < natural.Length; i++)
            Assert.True(targets[i] <= natural[i]);
        Assert.All(targets, t => Assert.True(t >= 4));
        var total = targets.Sum() + 3 * 3 + 1;
        Assert.True(total <= 55);
    }

    [Fact]
    public void ComputeTargetWidths_TooNarrow_ListFallback()
    {
        int[] natural = [10, 20, 30];
        var needFallback = ComputeTargetWidths(natural, 3, 25, out _);
        Assert.True(needFallback);
    }

    // ── Full Table Rendering ──────────────────────────────────

    [Fact]
    public void Convert_FitsNaturally_SingleLineGrid()
    {
        var md = "| A | B |\n|---|---|\n| 1 | 2 |\n";
        var result = MarkdownConverter.Convert(md, 120);
        Assert.Contains('┌', result);
        Assert.Contains('├', result);
        Assert.Contains('└', result);
    }

    [Fact]
    public void Convert_Overflows_MultiLineGrid()
    {
        var md =
            "| Col | Data |\n|-----|------|\n| Key | VeryLongDataThatMustWrapToMultipleLinesInNarrowColumn |\n";
        var result = MarkdownConverter.Convert(md, 45);
        Assert.Contains('┌', result);
        var lines = result.Split('\n');
        var contentLines = lines
            .Where(l => l.Contains('│') && !l.Contains('┌') && !l.Contains('├') && !l.Contains('└'))
            .ToList();
        Assert.True(
            contentLines.Count >= 3,
            $"Expected >= 3 content lines (multi-line wrap), got {contentLines.Count}.\n{result}"
        );
    }

    [Fact]
    public void Convert_ExtremeNarrow_ListFallback()
    {
        var md =
            "| A | VeryWideColumn | AnotherWideOne |\n|---|---|---|\n| 1 | lots of text | more text |\n";
        var result = MarkdownConverter.Convert(md, 30);
        Assert.DoesNotContain('┌', result);
        // List fallback: first column becomes heading, others become "**header**: value"
        Assert.Contains("VeryWideColumn", result);
        Assert.Contains("AnotherWideOne", result);
    }

    [Fact]
    public void Convert_WidthZero_NoWidthConstraint()
    {
        var md = "| A | B |\n|---|---|\n| 1 | 2 |\n";
        var result = MarkdownConverter.Convert(md, 0);
        Assert.Contains('┌', result);
    }

    [Fact]
    public void Convert_ContainsRealStars()
    {
        var md = "| Rating | Description |\n|--------|-------------|\n| ⭐⭐⭐ | Excellent |\n";
        var result = MarkdownConverter.Convert(md, 60);
        Assert.Contains('┌', result);
        Assert.Contains("⭐⭐⭐", result);
        Assert.Contains("Excellent", result);
    }

    [Fact]
    public void Convert_ContainsBoldInCells()
    {
        var md = "| Option | Detail |\n|--------|--------|\n| **Bold** | Normal |\n";
        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains('┌', result);
        Assert.Contains("Bold", result);
        Assert.Contains("Normal", result);
    }

    // ── Overflow regression tests ───────────────────────────

    [Fact]
    public void WrapCell_NeverExceedsTargetWidth()
    {
        // Long CJK text that used to overflow after FixCjkPunctuation
        var text =
            "折花帮忙打扫教室，无意中瞥见星罗桌肚里塞满了各种印着他名字的物品（橡皮、草稿纸）。星罗脸色瞬间煞白。";
        foreach (var tw in new[] { 20, 16, 12, 10, 8 })
        {
            var lines = WrapCell(text, tw);
            foreach (var line in lines)
            {
                var vlen = VisibleLength(line);
                Assert.True(
                    vlen <= tw,
                    $"WrapCell overflow at targetWidth={tw}: visible={vlen}, line=\"{line}\""
                );
            }
        }
    }

    [Fact]
    public void WrapCell_CjkOpeningPunctDoesNotOverflow()
    {
        // Opening punct （ pulled to previous line must not exceed targetWidth
        var text = "你好（世界测试）更多文字";
        foreach (var tw in new[] { 8, 6, 4 })
        {
            var lines = WrapCell(text, tw);
            foreach (var line in lines)
            {
                var vlen = VisibleLength(line);
                Assert.True(
                    vlen <= tw,
                    $"Opening punct overflow at targetWidth={tw}: visible={vlen}, line=\"{line}\""
                );
            }
        }
    }

    [Fact]
    public void WrapCell_CjkClosingPunctDoesNotOverflow()
    {
        // Closing punct ，。 moved to previous line must not exceed targetWidth
        var text = "测试文字，更多内容。还有更多。";
        foreach (var tw in new[] { 8, 6, 4 })
        {
            var lines = WrapCell(text, tw);
            foreach (var line in lines)
            {
                var vlen = VisibleLength(line);
                Assert.True(
                    vlen <= tw,
                    $"Closing punct overflow at targetWidth={tw}: visible={vlen}, line=\"{line}\""
                );
            }
        }
    }

    [Fact]
    public void Convert_FiveColumnCjkTable_NoBorderOverflow()
    {
        // The user's actual table: 5 columns with long CJK content in the last column
        var md =
            "| 场景名称 | 角色 | 地点 | 修复目标 | 核心情节 |\n"
            + "|------|------|------|------|------|\n"
            + "| 《值日生的秘密》 | 星罗 & 折花 | 新：学校教室 | 补齐星罗的病娇线 | 折花帮忙打扫教室，无意中瞥见星罗桌肚里塞满了各种印着他名字的物品（橡皮、草稿纸）。星罗脸色瞬间煞白。 |\n"
            + "| 《学姐的课后辅导》 | 杏珠 & 折花 | 新：社团活动室 | 补齐杏珠的高光线 | 杏珠把折花骗进社团室，以\"补课\"为由近距离指导折花。杏珠身上特有的香气让折花极度不安，但心跳加速。 |\n"
            + "| 《心声泄露事故》 | 美海 & 折花 | 学校走廊 | 发挥美海的功能 | 折花在心里疯狂吐槽刚才路过的胖大叔，美海在旁边突然冷冷地说：\"他在想你长得像那个大叔。\"全场死寂。 |\n"
            + "| 《后座的颠簸》 | 天音 & 折花 | 校门口街道 | 修复天音的互动 | 纯粹的日常生活。天音非要载折花回家，折花走神身体前倾，天音不得不大力揽住他的腰，还骂他是\"笨蛋\"。 |";

        foreach (var width in new[] { 80, 60, 50, 45 })
        {
            var result = MarkdownConverter.Convert(md, width);
            var lines = result.Split('\n');
            foreach (var line in lines)
            {
                // Strip XenoAtom markup tags [tag] and [/] to get visible text
                var plain = StripMarkupTags(line).TrimEnd();
                var vlen = VisibleLength(plain);
                Assert.True(
                    vlen <= width,
                    $"Border overflow at contentWidth={width}: visible={vlen}, plain=\"{plain}\""
                );
            }
        }
    }

    [Fact]
    public void Convert_WideTable_NarrowWidth_NoRowExceedsContentWidth()
    {
        // 6-column table with moderate content
        var md =
            "| A | B | C | D | E | F |\n"
            + "|---|---|---|---|---|---|\n"
            + "| alpha | beta | gamma | delta | epsilon | This is a long sentence that needs wrapping in narrow columns |\n";

        var result = MarkdownConverter.Convert(md, 55);
        var lines = result.Split('\n');
        foreach (var line in lines)
        {
            var plain = StripMarkupTags(line).TrimEnd();
            var vlen = VisibleLength(plain);
            Assert.True(
                vlen <= 55,
                $"Row overflow at contentWidth=55: visible={vlen}, plain=\"{plain}\""
            );
        }
    }

    [Fact]
    public void Convert_ListFallback_WongsWrapToContentWidth()
    {
        // When table falls back to list mode, long values should wrap
        var md =
            "| Name | Detail |\n"
            + "|------|--------|\n"
            + "| Test | This is a very long detail value that should be wrapped when the content width is narrow |\n";

        var result = MarkdownConverter.Convert(md, 40);
        var lines = result.Split('\n');
        foreach (var line in lines)
        {
            var plain = StripMarkupTags(line).TrimEnd();
            var vlen = VisibleLength(plain);
            Assert.True(
                vlen <= 40,
                $"List fallback overflow at contentWidth=40: visible={vlen}, plain=\"{plain}\""
            );
        }
    }

    // ── Emoji width tests ────────────────────────────────────

    [Fact]
    public void VisibleLength_SupplementaryPlaneEmoji_CountsAs2()
    {
        // 🏫 U+1F3EB — supplementary plane emoji (surrogate pair)
        Assert.Equal(2, VisibleLength("🏫"));
        // 🧶 U+1F9B6
        Assert.Equal(2, VisibleLength("🧶"));
        // 🥺 U+1F97A
        Assert.Equal(2, VisibleLength("🥺"));
        // 🙉 U+1F649
        Assert.Equal(2, VisibleLength("🙉"));
    }

    [Fact]
    public void VisibleLength_EmojiWithVariationSelector_CountsAs2()
    {
        // 🛣️ = U+1F6E3 + U+FE0F — supplementary emoji + VS16
        Assert.Equal(2, VisibleLength("🛣️"));
        // 🏟️ = U+1F3DF + U+FE0F
        Assert.Equal(2, VisibleLength("🏟️"));
    }

    [Fact]
    public void VisibleLength_ZwjEmojiSequence_MatchesWcwidth()
    {
        // 🕵️‍♀️ = U+1F575 + U+FE0F + U+200D + U+2640 + U+FE0F
        // wcwidth: 2(🕵) + 0(FE0F) + 0(200D) + 1(♀) + 0(FE0F) = 3
        // We must match wcwidth so PadCell padding aligns with terminal rendering
        Assert.Equal(3, VisibleLength("🕵️\u200D♀️"));
    }

    [Fact]
    public void VisibleLength_FemaleSign_IsWidth1()
    {
        // ♀ U+2640 — wcwidth returns 1 (text presentation default)
        Assert.Equal(1, VisibleLength("♀"));
    }

    [Fact]
    public void VisibleLength_EmojiPlusCjk_CorrectTotal()
    {
        // 🏫学校教室 = emoji(2) + 4×CJK(8) = 10
        Assert.Equal(10, VisibleLength("🏫学校教室"));
    }

    [Fact]
    public void VisibleLength_CheckMarkEmoji_CountsAs2()
    {
        // ✅ U+2705 — BMP emoji
        Assert.Equal(2, VisibleLength("✅"));
        // ❌ U+274C — BMP emoji
        Assert.Equal(2, VisibleLength("❌"));
    }

    [Fact]
    public void PadCell_EmojiPlusCjk_PadsToTargetWidth()
    {
        // "🏫学校教室" has visible width 10; pad to 14 should add 4 spaces
        var padded = PadCell("🏫学校教室", 14, 0); // 0 = Left
        Assert.Equal(14, VisibleLength(padded));
    }

    [Fact]
    public void Convert_EmojiTable_NoBorderOverflow()
    {
        // User's actual location table with emoji prefixes
        var md =
            "| 地点名称 | ID | 状态 |\n"
            + "|------|------|------|\n"
            + "| 🏫 学校教室 | 989c94f9ee51 | ✅ 已创建 |\n"
            + "| 🧶 社团活动室 | 752eba068f48 | ✅ 已创建 |\n"
            + "| 🛣️ 校门口街道 | 3471a29a3a3b | ✅ 已创建 |";

        foreach (var width in new[] { 80, 60, 50 })
        {
            var result = MarkdownConverter.Convert(md, width);
            var lines = result.Split('\n');
            foreach (var line in lines)
            {
                var plain = StripMarkupTags(line).TrimEnd();
                var vlen = VisibleLength(plain);
                Assert.True(
                    vlen <= width,
                    $"Emoji table overflow at contentWidth={width}: visible={vlen}, plain=\"{plain}\""
                );
            }
        }
    }

    [Fact]
    public void Convert_EmojiSceneTable_NoBorderOverflow()
    {
        // User's actual scene table with emoji prefixes and ZWJ emoji
        var md =
            "| 场景名 | 角色 | 状态 |\n"
            + "|------|------|------|\n"
            + "| 🥺 值日生的秘密 | 星罗 & 折花 | ✅ 已创建（发现病态收藏癖） |\n"
            + "| 🕵️‍♀️ 学姐的课后辅导 | 杏珠 & 折花 | ✅ 已创建（展现掌控力） |\n"
            + "| 🙉 心声泄露事故 | 美海 & 折花 | ✅ 已更新（修正了细节） |\n"
            + "| ⚡ 后座的颠簸 | 天音 & 折花 | ❌ 待创建 |\n"
            + "| 🏟️ 空荡的体育馆 | 美海 & 折花 | ❌ 待创建（之前的旧数据） |";

        foreach (var width in new[] { 80, 60, 50 })
        {
            var result = MarkdownConverter.Convert(md, width);
            var lines = result.Split('\n');
            foreach (var line in lines)
            {
                var plain = StripMarkupTags(line).TrimEnd();
                var vlen = VisibleLength(plain);
                Assert.True(
                    vlen <= width,
                    $"Emoji scene table overflow at contentWidth={width}: visible={vlen}, plain=\"{plain}\""
                );
            }
        }
    }

    // ── Reflection helpers ────────────────────────────────────

    private static List<string> WrapCell(string text, int targetWidth)
    {
        var m = typeof(MarkdownConverter).GetMethod(
            "WrapCell",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        return (List<string>)m.Invoke(null, [text, targetWidth])!;
    }

    private static bool ComputeTargetWidths(
        int[] naturalWidths,
        int colCount,
        int contentWidth,
        out int[] targetWidths
    )
    {
        var m = typeof(MarkdownConverter).GetMethod(
            "ComputeTargetWidths",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        var args = new object[] { naturalWidths, colCount, contentWidth, null! };
        var result = (bool)m.Invoke(null, args)!;
        targetWidths = (int[])args[3];
        return result;
    }

    private static int VisibleLength(string text)
    {
        var m = typeof(MarkdownConverter).GetMethod(
            "VisibleLength",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        return (int)m.Invoke(null, [text])!;
    }

    private static string PadCell(string text, int targetDisplayWidth, int alignInt)
    {
        var m = typeof(MarkdownConverter).GetMethod(
            "PadCell",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        // ColumnAlign enum: 0 = Left, 1 = Center, 2 = Right
        var alignType = typeof(MarkdownConverter).GetNestedType(
            "ColumnAlign",
            BindingFlags.NonPublic
        )!;
        var align = Enum.ToObject(alignType, alignInt);
        return (string)m.Invoke(null, new object[] { text, targetDisplayWidth, align })!;
    }

    /// <summary>
    /// Strips XenoAtom markup tags [tag] and [/] from a line, leaving only visible text.
    /// Used to measure the actual display width of Convert() output.
    /// </summary>
    private static string StripMarkupTags(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        var sb = new System.Text.StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '[' && i + 1 < line.Length)
            {
                // Check for closing tag [/]
                if (line[i..].StartsWith("[/]"))
                {
                    i += 3;
                    continue;
                }
                // Check for opening tag [tag] — skip until ]
                var closeBracket = line.IndexOf(']', i + 1);
                if (closeBracket >= 0)
                {
                    i = closeBracket + 1;
                    continue;
                }
            }
            // Escaped bracket \[
            if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] is '[' or ']')
            {
                sb.Append(line[i + 1]);
                i += 2;
                continue;
            }
            sb.Append(line[i]);
            i++;
        }
        return sb.ToString();
    }
}
