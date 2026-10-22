using XenoAtom.Terminal;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 图形字符宽度安全回归（黑块清剿守卫，详见 docs/UI.md §14），两层防线：
/// 1) 字形宽度表（封闭集）：生产用到的每个字形都必须在此登记，宽字形计 2 格、窄字形计 1 格——
///    新增字形忘登记 → CI 红，杜绝"框架按 1 格记账、终端按 2 格渲染"的黑块；
/// 2) 生产源码扫描：src/Tui 下（测试除外）不得再出现易碎序列——零宽修饰符（VS15/VS16/ZWJ/ZWNJ）
///    与历史争议集（☁ ⚠ ⚔ ⛩ 🏙 🏛 🛡 🏷 ⚧ ⛓ ⬆ ⬇ 👨 ❎）；并禁止出现表外字形（封闭集守卫）。
///    注：❎（U+274E）为黑块源：emoji 宽块呈现与细十字预期/渲染账本不一致，关闭键统一 ✕（U+2715）。
/// </summary>
public class ChromeGlyphPolicyTests
{
    // ── 字形宽度表（封闭集，InUseGlyphsAreRegistered 守卫）──────────────
    // 宽字形（emoji，宽度必须为 2）
    private static readonly string[] WideGlyphs =
    [
        "🌀",
        "🗑",
        "🧾",
        "📤",
        "🌆",
        "🌐",
        "🌓",
        "🌲",
        "🌳",
        "🌾",
        "🎒",
        "🎓",
        "🎨",
        "🎬",
        "🎭",
        "🎯",
        "🏠",
        "🏢",
        "🏦",
        "🏯",
        "🏰",
        "🏴",
        "👍",
        "👎",
        "👤",
        "👥",
        "👪",
        "👫",
        "👶",
        "👹",
        "💍",
        "💕",
        "💡",
        "💢",
        "💤",
        "💥",
        "💨",
        "💬",
        "💭",
        "💰",
        "📂",
        "📁",
        "📄",
        "📊",
        "📋",
        "📌",
        "📍",
        "📕",
        "📖",
        "📗",
        "📚",
        "📜",
        "📝",
        "📦",
        "📿",
        "🔀",
        "🔄",
        "🔍",
        "🔒",
        "🔖",
        "🔗",
        "🔥",
        "🔧",
        "🔮",
        "🔴",
        "🔵",
        "🖼",
        "🙇",
        "🚧",
        "🚫",
        "🟠",
        "🟡",
        "🟢",
        "🤖",
        "🤝",
        "🧑",
        "🧠",
        "🧪",
        "🧹",
        "🩸",
        "🪢",
        "⏳",
        "⚡",
        "⚪",
        "⛔",
        "✅",
        "✋",
        "✨",
        "❌",
        "❓",
        "❗",
        "➕",
        "⬜",
        "⭐",
    ];

    // 窄字形（符号/制表/盲文，宽度必须为 1）
    private static readonly string[] NarrowGlyphs =
    [
        "§",
        "©",
        "·",
        "×",
        "ø",
        "—",
        "•",
        "…",
        "⇔", // EAW=A：框架按 1 格记账；终端若画 2 格由 TerminalWidthProbe 运行时纠正
        "★", // 同上
        "←",
        "↑",
        "→",
        "↓",
        "↔",
        "⇅",
        "≥",
        "⋯",
        "─",
        "│",
        "┌",
        "┐",
        "└",
        "┘",
        "├",
        "┤",
        "┬",
        "┴",
        "┼",
        "═",
        "▀",
        "▄",
        "█",
        "▎",
        "▲",
        "▶",
        "▸",
        "▼",
        "▾",
        "◀",
        "○",
        "●",
        "☐",
        "☑",
        "♀",
        "♂",
        "♟",
        "✓",
        "✕",
        "✏",
        "↩",
        "❯",
        "⠇",
        "⠋",
        "⠏",
        "⠙",
        "⠦",
        "⠧",
        "⠴",
        "⠸",
        "⠹",
        "⠼",
    ];

    [Theory]
    [MemberData(nameof(WideGlyphCases))]
    public void WideGlyph_CountsTwoCells(string glyph) =>
        Assert.Equal(2, TerminalTextUtility.GetWidth(glyph.AsSpan()));

    [Theory]
    [MemberData(nameof(NarrowGlyphCases))]
    public void NarrowGlyph_CountsOneCell(string glyph) =>
        Assert.Equal(1, TerminalTextUtility.GetWidth(glyph.AsSpan()));

    public static TheoryData<string> WideGlyphCases() => new(WideGlyphs);

    public static TheoryData<string> NarrowGlyphCases() => new(NarrowGlyphs);

    [Fact]
    public void ProductionSources_FreeOfFragileGlyphs()
    {
        var root = FindRepoRoot();
        var banned = new[]
        {
            "\uFE0E",
            "\uFE0F",
            "\u200C",
            "\u200D",
            "\u2601",
            "\u26A0",
            "\u2694",
            "\u26E9",
            "\uD83C\uDFD9",
            "\uD83C\uDFDB",
            "\uD83D\uDEE1",
            "\uD83C\uDFF7",
            "\u26A7",
            "\u26D3",
            "\u2B06",
            "\u2B07",
            "\uD83D\uDC68",
            "\u274E",
        };

        var violations = new List<string>();
        foreach (
            var file in Directory.EnumerateFiles(
                Path.Combine(root, "src", "Tui"),
                "*.cs",
                SearchOption.AllDirectories
            )
        )
        {
            var segments = file.Split(Path.DirectorySeparatorChar);
            if (segments.Contains("Tests") || segments.Contains("obj") || segments.Contains("bin"))
                continue;

            var lines = File.ReadAllLines(file);
            for (var n = 0; n < lines.Length; n++)
            {
                var trimmed = lines[n].TrimStart();
                if (
                    trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                )
                    continue;

                foreach (var b in banned)
                {
                    if (lines[n].Contains(b, StringComparison.Ordinal))
                        violations.Add(
                            $"{Path.GetRelativePath(root, file)}:{n + 1} 含 U+{char.ConvertToUtf32(b, 0):X4}"
                        );
                }
            }
        }

        Assert.True(violations.Count == 0, "生产源码含易碎字形:\n" + string.Join("\n", violations));
    }

    /// <summary>封闭集守卫：生产源码（src/Tui，测试/obj/bin 除外）出现表外非 ASCII 字形即失败。</summary>
    /// <remarks>
    /// 排除范围与 <see cref="ProductionSources_FreeOfFragileGlyphs"/> 一致：
    /// CJK 表意文字（0x4E00-0x9FFF）、CJK 部首扩展、假名、CJK 符号与标点（「」，。等）、
    /// 全角形式（（）｜等）——这些区块在东亚宽度下恒为宽格，无终端分歧，不属于本回归范围。
    /// </remarks>
    [Fact]
    public void ProductionSources_OnlyRegisterGlyphs()
    {
        var root = FindRepoRoot();
        var registered = new HashSet<string>(
            WideGlyphs.Concat(NarrowGlyphs),
            StringComparer.Ordinal
        );

        var violations = new List<string>();
        foreach (
            var file in Directory.EnumerateFiles(
                Path.Combine(root, "src", "Tui"),
                "*.cs",
                SearchOption.AllDirectories
            )
        )
        {
            var segments = file.Split(Path.DirectorySeparatorChar);
            if (segments.Contains("Tests") || segments.Contains("obj") || segments.Contains("bin"))
                continue;

            var lines = File.ReadAllLines(file);
            for (var n = 0; n < lines.Length; n++)
            {
                var trimmed = lines[n].TrimStart();
                if (
                    trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                )
                    continue;

                foreach (var rune in lines[n].EnumerateRunes())
                {
                    if (rune.Value < 0x80)
                        continue;
                    if (
                        (rune.Value >= 0x4E00 && rune.Value <= 0x9FFF)
                        || (rune.Value >= 0x3400 && rune.Value <= 0x4DBF)
                        || (rune.Value >= 0x3040 && rune.Value <= 0x30FF)
                        || (rune.Value >= 0x3000 && rune.Value <= 0x303F)
                        || (rune.Value >= 0xFF00 && rune.Value <= 0xFFEF)
                        || rune.Value == 0xFEFF
                    )
                        continue;

                    var glyph = rune.ToString();
                    if (!registered.Contains(glyph))
                        violations.Add(
                            $"{Path.GetRelativePath(root, file)}:{n + 1} 字形 U+{rune.Value:X4} '{glyph}' 未在宽度表登记"
                        );
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "生产源码含未登记字形（请先确认其终端宽度，再把字形与实测宽度补进宽度表）:\n"
                + string.Join("\n", violations.Distinct())
        );
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Tui")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录");
    }
}
