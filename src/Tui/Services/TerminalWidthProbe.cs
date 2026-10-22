using System.Text;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;

namespace Tui.Services;

/// <summary>
/// 终端字形格宽探测 —— 解决「框架记账的格宽 ≠ 终端实际占位」导致的整行错位 / 行尾露底（黑带）/ 字形被顶掉（缺字）。
///
/// 两类来源：
/// 1) <b>Unicode East_Asian_Width = Ambiguous</b>（EAW=A）整类：`→ ⇔ ─ │ █ ▀ ● ★ × … •` 等
///    （本仓大量使用，含分隔线 ─ 与方块 █）。框架默认只把 emoji 按 2 格，其余按 1 格；
///    部分终端/字体（CJK locale 的 fallback、legacy 宽度模型）把 A 类画成 2 格。
/// 2) <b>字体回退导致的个别字形</b>：如 `▸ ✕ ▾ ❯`（Unicode 判为 Neutral）在中文 fallback 字体下
///    也可能占 2 格 —— 这不是 EAW 问题，只能逐字形实测。实体数据自带的字形（关系名/规则文本里的 ⇔）
///    靠 EAW 整类覆盖。
///
/// 做法：UI 接管屏幕**之前**，对一组代表字形逐个「查光标列 → 打印字形 → 再查光标列」实测格宽；
/// 结果交给 <see cref="BuildResolver"/> 生成 WideRuneResolver。探测失败（终端不应答）→ null，
/// 调用方退回框架默认（emoji 加宽），行为与本修复前完全一致。
/// </summary>
public static partial class TerminalWidthProbe
{
    /// <summary>EAW=A 类的代表字形（覆盖制表线、方块、箭头、几何图形、标点）。</summary>
    private static readonly char[] AmbiguousProbes = ['→', '─', '⇔', '█', '★', '●', '×', '…'];

    /// <summary>Unicode 判为 Neutral、但在中文 fallback 字体下可能变宽的本仓装饰字形。</summary>
    private static readonly char[] DecorationProbes = ['▸', '✕', '▾', '❯'];

    /// <summary>
    /// 东亚宽字形（CJK）代表：框架按 2 格记账。若终端/字体把它们画成 1 格，
    /// 每个汉字后面就会留下 1 格底色空档（实机表现：汉字之间有空档、文字后面一块
    /// 跟随主题变色的黑块/白块）。这类**反向**不一致无法用 WideRuneResolver 修正
    /// （该旋钮只能加宽），只能探测出来并警告用户改字体/终端设置。
    /// </summary>
    private static readonly char[] CjkProbes = ['中', 'あ'];

    /// <summary>探测结果：EAW=A 整类是否加宽 + 需要单独加宽的码点（字体回退类）。</summary>
    public sealed record ProbeResult(
        bool AmbiguousClassWide,
        IReadOnlySet<int> ExtraWideCodePoints,
        bool CjkRenderedNarrow = false
    )
    {
        /// <summary>该字形是否按 2 格渲染。</summary>
        public bool IsWide(Rune rune) =>
            ExtraWideCodePoints.Contains(rune.Value) || (AmbiguousClassWide && IsAmbiguous(rune));
    }

    /// <summary>码点是否属于 Unicode East_Asian_Width = Ambiguous（表见 .Ambiguous.g.cs，机器生成）。</summary>
    public static bool IsAmbiguous(Rune rune)
    {
        var cp = rune.Value;
        var lo = 0;
        var hi = AmbiguousRanges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var (start, end) = AmbiguousRanges[mid];
            if (cp < start)
                hi = mid - 1;
            else if (cp > end)
                lo = mid + 1;
            else
                return true;
        }
        return false;
    }

    /// <summary>
    /// 构造 <see cref="TerminalRunOptions.WideRuneResolver"/>：emoji 类字形始终加宽（框架默认语义），
    /// 其余按探测结果。<paramref name="probe"/> 为 null（探测失败）时等同框架默认。
    /// </summary>
    public static Func<Rune, bool> BuildResolver(ProbeResult? probe) =>
        rune => TerminalTextUtility.IsLikelyEmojiScalar(rune) || (probe?.IsWide(rune) ?? false);

    /// <summary>
    /// 实测终端格宽。返回 null = 探测失败（终端不应答 DSR / 结果自相矛盾），调用方回退默认。
    /// 本方法会临时打印并擦除探测字形，必须在 UI 接管屏幕之前调用。
    /// </summary>
    public static ProbeResult? Probe(TerminalInstance terminal)
    {
        try
        {
            var ambiguousWidths = Measure(terminal, AmbiguousProbes);
            var decorationWidths = Measure(terminal, DecorationProbes);

            // 东亚宽字形（CJK）实测：若被画成 1 格，则整体排版必然错位（无法修正，只能告警）
            var cjkWidths = Measure(terminal, CjkProbes);
            var cjkNarrow = cjkWidths is not null && cjkWidths.Any(w => w.Width == 1);

            // 逐字形清单独立于代表字形：代表字形探测失败时清单仍要实测
            // （字体回退只影响个别字形，靠清单抓）。
            var inventoryWide = MeasureInventory(terminal);
            if (ambiguousWidths is null && decorationWidths is null && inventoryWide.Count == 0)
                return null; // 全都没测到：调用方按框架默认回退

            // EAW=A：全部一致才算「整类加宽」；不一致（个别字形被 fallback 拉宽）只加宽实测为宽的那些
            var ambiguous = ambiguousWidths ?? [];
            var decorations = decorationWidths ?? [];
            var wideAmbiguous = ambiguous
                .Where(w => w.Width == 2)
                .Select(w => w.CodePoint)
                .ToArray();
            var narrowAmbiguous = ambiguous
                .Where(w => w.Width == 1)
                .Select(w => w.CodePoint)
                .ToArray();
            var classWide = wideAmbiguous.Length > 0 && narrowAmbiguous.Length == 0;

            var extra = decorations.Where(w => w.Width == 2).Select(w => w.CodePoint).ToHashSet();
            if (!classWide)
                foreach (var cp in wideAmbiguous)
                    extra.Add(cp);

            // 清单逐字形实测：字体回退会让我方按 1 格记账的字形在终端占 2 格（整行错位、
            // 行尾露底、把后面的宽字形挤掉 = 一个汉字宽的黑块）。
            // 只实测框架按 1 格的字形：加宽是 WideRuneResolver 唯一能修的方向。
            extra.UnionWith(inventoryWide);

            // Data glyphs from the previous session are candidates, not known-wide glyphs.
            // Only add them after a real cursor-position measurement says they occupy 2 cells.
            if (TryReadPendingDataRunes(out var pendingDataRunes))
            {
                var dataWide = MeasureDataRunes(
                    terminal,
                    pendingDataRunes,
                    out var unmeasuredDataRunes
                );
                extra.UnionWith(dataWide);
                SavePendingDataRunes(unmeasuredDataRunes);
            }

            return new ProbeResult(classWide, extra, cjkNarrow);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 数据自带字形的落盘位置：会话运行期收集默认单格的未知字形，
    /// 下次启动在 UI 接管屏幕前用 DSR 实测；只有实测双格的字形才并入 resolver。
    /// </summary>
    /// <summary>数据字形缓存文件（环境变量可覆盖，测试用）。</summary>
    private static string PendingDataRunesPath =>
        Environment.GetEnvironmentVariable("MYTHOLOGY_TUI_GLYPH_CACHE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mythology",
            "tui-pending-glyphs.txt"
        );

    /// <summary>已收集的数据字形（会话运行期累积；启动补测时读出）。</summary>
    private static readonly HashSet<int> DataRunes = [];

    private static bool _dataRunesLoaded;

    /// <summary>
    /// 收集渲染文本里的非 ASCII 字形（跳过 CJK/全角等东亚宽度恒定的区块），
    /// 供下次启动时实测其格宽。UI 线程调用，幂等且廉价。
    /// </summary>
    public static void NoteText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        EnsureDataRunesLoaded();
        var added = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            // EAW=A 数据字形需要单独测量；非单格字形已不是 WideRuneResolver 能修的范围。
            if (cp < 0x80 || IsEastAsianStable(cp) || !IsDataRuneProbeCandidate(rune))
                continue;
            if (DataRunes.Add(cp))
                added = true;
        }

        // 落盘条件：有新字形，或文件已被上次启动取走（否则下次启动拿不到已发现的字形，
        // 实测结果随之丢失，黑块会复发）。
        if (!added && File.Exists(PendingDataRunesPath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingDataRunesPath)!);
            File.WriteAllLines(PendingDataRunesPath, DataRunes.Select(cp => cp.ToString("X")));
        }
        catch (Exception)
        {
            // 落盘失败不影响运行
        }
    }

    /// <summary>读取待测数据字形；调用方只有在测量完成后才更新缓存。</summary>
    private static bool TryReadPendingDataRunes(out int[] codePoints)
    {
        codePoints = [];
        try
        {
            if (!File.Exists(PendingDataRunesPath))
                return true;
            codePoints = File.ReadAllLines(PendingDataRunesPath)
                .Select(line =>
                    int.TryParse(
                        line,
                        System.Globalization.NumberStyles.HexNumber,
                        null,
                        out var cp
                    )
                        ? cp
                        : -1
                )
                .Where(cp => cp > 0 && Rune.IsValid(cp))
                .Distinct()
                .ToArray();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>只保留本次无法测量的候选；空集合则清除已处理缓存。</summary>
    private static void SavePendingDataRunes(IReadOnlyCollection<int> codePoints)
    {
        try
        {
            if (codePoints.Count == 0)
            {
                if (File.Exists(PendingDataRunesPath))
                    File.Delete(PendingDataRunesPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PendingDataRunesPath)!);
            File.WriteAllLines(PendingDataRunesPath, codePoints.Select(cp => cp.ToString("X")));
        }
        catch (Exception)
        {
            // 缓存写入失败不影响当前探测结果。
        }
    }

    /// <summary>该 rune 是否原本占一格、因此需要终端实测才能决定是否扩宽。</summary>
    internal static bool IsDataRuneProbeCandidate(Rune rune) =>
        !TerminalTextUtility.IsLikelyEmojiScalar(rune)
        && TerminalTextUtility.GetRuneWidth(rune, null, TerminalTextUtility.DefaultTabWidth) == 1;

    /// <summary>数据 rune 只有「框架默认一格、终端实测两格」才加入扩宽集合。</summary>
    internal static bool ShouldWidenDataRune(Rune rune, int measuredWidth) =>
        IsDataRuneProbeCandidate(rune) && measuredWidth == 2;

    /// <summary>
    /// 逐个实测未分类的数据 rune。每个 glyph 从行首测量，避免长缓存跨行后列差失真；
    /// DSR 失败时保留尚未测量的候选，交给下次启动重试。
    /// </summary>
    private static HashSet<int> MeasureDataRunes(
        TerminalInstance terminal,
        IReadOnlyCollection<int> codePoints,
        out List<int> unmeasured
    )
    {
        var wide = new HashSet<int>();
        unmeasured = [];
        var candidates = codePoints
            .Distinct()
            .Select(cp => new Rune(cp))
            .Where(IsDataRuneProbeCandidate)
            .ToArray();
        if (candidates.Length == 0)
            return wide;

        try
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                var rune = candidates[i];
                ClearProbeLine(terminal);
                if (!terminal.TryGetCursorPosition(out var before))
                {
                    AddRemainingCandidates(candidates, i, unmeasured);
                    break;
                }

                terminal.Write(rune.ToString());
                terminal.Flush();
                if (!terminal.TryGetCursorPosition(out var after))
                {
                    AddRemainingCandidates(candidates, i, unmeasured);
                    break;
                }

                var measuredWidth = after.Column - before.Column;
                if (ShouldWidenDataRune(rune, measuredWidth))
                    wide.Add(rune.Value);
                else if (measuredWidth != 1)
                    unmeasured.Add(rune.Value);
            }
        }
        finally
        {
            ClearProbeLine(terminal);
        }

        return wide;
    }

    private static void AddRemainingCandidates(Rune[] candidates, int start, List<int> destination)
    {
        for (var i = start; i < candidates.Length; i++)
            destination.Add(candidates[i].Value);
    }

    private static void ClearProbeLine(TerminalInstance terminal)
    {
        try
        {
            terminal.Write("\r");
            terminal.EraseInLine(2);
            terminal.Flush();
        }
        catch (Exception)
        {
            // Cleanup is best effort; the UI has not taken over the terminal yet.
        }
    }

    private static void EnsureDataRunesLoaded()
    {
        if (_dataRunesLoaded)
            return;
        _dataRunesLoaded = true;
        try
        {
            if (!File.Exists(PendingDataRunesPath))
                return;
            foreach (var line in File.ReadAllLines(PendingDataRunesPath))
                if (
                    int.TryParse(
                        line,
                        System.Globalization.NumberStyles.HexNumber,
                        null,
                        out var cp
                    )
                )
                    DataRunes.Add(cp);
        }
        catch (Exception)
        {
            // 读取失败按空集合继续
        }
    }

    /// <summary>东亚宽度恒定的区块（CJK 表意/假名/全角/emoji 宽区），无需实测。</summary>
    private static bool IsEastAsianStable(int cp) =>
        (cp >= 0x2E80 && cp <= 0x9FFF) // CJK 部首扩展 / 表意文字
        || (cp >= 0xAC00 && cp <= 0xD7AF) // 谚文
        || (cp >= 0xF900 && cp <= 0xFAFF) // CJK 兼容表意
        || (cp >= 0xFE30 && cp <= 0xFE4F) // CJK 兼容形式
        || (cp >= 0xFF00 && cp <= 0xFFEF) // 全角形式
        || (cp >= 0x1F300 && cp <= 0x1FAFF) // emoji
        || (cp >= 0x1F000 && cp <= 0x1F2FF);

    /// <summary>
    /// 实测封闭集清单里「框架按 1 格」的字形；探测中断则返回已测到的部分（不抛异常）。
    /// 探测痕迹在返回前统一擦除。
    /// </summary>
    private static HashSet<int> MeasureInventory(TerminalInstance terminal)
    {
        var wide = new HashSet<int>();
        try
        {
            terminal.Flush();
            foreach (var codePoint in InventoryCodePoints)
            {
                if (!Rune.IsValid(codePoint))
                    continue;

                var rune = new Rune(codePoint);
                if (
                    TerminalTextUtility.GetRuneWidth(
                        rune,
                        null,
                        TerminalTextUtility.DefaultTabWidth
                    ) != 1
                )
                    continue; // 框架已按宽字形处理（emoji / W / F）：加宽语义无关

                if (!terminal.TryGetCursorPosition(out var before))
                    break;
                terminal.Write(rune.ToString());
                terminal.Flush();
                if (!terminal.TryGetCursorPosition(out var after))
                    break;

                if (after.Column - before.Column == 2)
                    wide.Add(codePoint);
            }
        }
        catch (Exception)
        {
            // 中断：保留已测结果
        }
        finally
        {
            try
            {
                terminal.Write("\r");
                terminal.EraseInLine(2);
                terminal.Flush();
            }
            catch (Exception)
            {
                // 擦除失败不影响判定
            }
        }
        return wide;
    }

    /// <summary>逐字形实测格宽；任一字形探测失败返回 null。</summary>
    private static List<(int CodePoint, int Width)>? Measure(
        TerminalInstance terminal,
        char[] glyphs
    )
    {
        terminal.Flush();
        var result = new List<(int, int)>(glyphs.Length);
        foreach (var glyph in glyphs)
        {
            if (!terminal.TryGetCursorPosition(out var before))
                return null;

            terminal.Write(glyph.ToString());
            terminal.Flush();

            if (!terminal.TryGetCursorPosition(out var after))
                return null;

            var width = after.Column - before.Column;
            if (width is not (1 or 2))
                return null;

            result.Add((glyph, width));
        }

        // 擦掉探测痕迹（此时尚未接管屏幕，只回行首清行）
        terminal.Write("\r");
        terminal.EraseInLine(2);
        terminal.Flush();
        return result;
    }
}
