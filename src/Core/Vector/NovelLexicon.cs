using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JiebaNet.Segmenter;

namespace Core.Vector;

/// <summary>
/// jieba 用户词典：把「实体名」注册成词，避免名字被切开（皮皮龙 → 皮皮/龙、松若美乃 → 松雪美/乃是）。
///
/// 关键事实（实测，见 docs/lexical-retokenize-plan.md §1.2）：
/// <list type="bullet">
/// <item>JiebaNet 的词表是**进程全局**单例（<see cref="WordDictionary.Instance"/>），任何 segmenter 的 AddWord
/// 都会改同一份词表；因此这里不再假装「每部小说独立」，而是维护一个全局注册表 + 单一 segmenter。</item>
/// <item><see cref="JiebaSegmenter.Calc"/> 走的是最大对数概率路径，每条边付一次 <c>−log(Total)</c>；
/// 「整名 vs 切 k 段」胜出条件是 <c>f_名 &gt; (∏f_段) / Total^(k−1)</c>。2 字名阈值可达数千（善我 31.9、有我 2318），
/// 故词频取 <see cref="WordFrequency"/>，合法区间是 (12 991, Total=60 101 967)。</item>
/// </list>
/// 版本是**全局**的（策略前缀 + 全部名字排序），改词频/归一化规则或增删名字都会让旧 tokens 失效。
/// </summary>
public static class NovelLexicon
{
    /// <summary>注册词频。下界须高于两字名最坏阈值 12 991（最高频字 883 634² / Total），上界须低于 Total 60 101 967。</summary>
    public const int WordFrequency = 100_000;

    /// <summary>分词策略版本：改动词频、归一化或发射模式时递增，令旧 tokens 全部失效。</summary>
    private const string PolicyVersion = "v7|select=entity|emit=cutforsearch|freq=100000";

    /// <summary>词表是进程全局的，注册与切词共用同一把锁（AddWord 写 Dictionary，Cut 在读）。</summary>
    private static readonly object Gate = new();

    private static readonly JiebaSegmenter Shared = new();

    /// <summary>名字按小说归档，仅用于「谁贡献了哪些名字」的溯源与版本计算。</summary>
    private static readonly Dictionary<string, string[]> ByNovel = new(StringComparer.Ordinal);

    /// <summary>已注册词集合（全局并集），决定版本与是否重复注册。</summary>
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);

    private static string _version = "i0";

    /// <summary>名字内部的标点视为边界（整名不会以带标点的形式出现在正文里）。空白不是边界——先压掉。</summary>
    private static readonly Regex Separators = new(
        @"[！？，。、—·：；「」『』《》〈〉（）()\[\]【】""'“”‘’…]+",
        RegexOptions.Compiled
    );

    /// <summary>日式名常带空格（「小森 星罗」），注册前压掉，正文里的形态是「小森星罗」。</summary>
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 注册该小说的实体名；名称集合有变化时返回 true。
    /// 归一化：去空白 → 按标点切段 → 只保留 ≥2 字的段（1 字段无法独立成词，且会被停用词过滤掉）。
    /// </summary>
    public static bool Update(string novelId, IEnumerable<string> names)
    {
        ArgumentException.ThrowIfNullOrEmpty(novelId);
        var normalized = Normalize(names);
        lock (Gate)
        {
            if (
                ByNovel.TryGetValue(novelId, out var existing)
                && existing.AsSpan().SequenceEqual(normalized)
            )
                return false;

            ByNovel[novelId] = normalized;
            foreach (var name in normalized)
            {
                if (Registered.Add(name))
                    Shared.AddWord(name, WordFrequency, "nz");
            }

            _version = ComputeVersion();
            return true;
        }
    }

    /// <summary>全局词典版本（名称集合 + 策略前缀的哈希）。未注册任何名字时返回初始版本。</summary>
    public static string GetVersion(string? novelId = null) => _version;

    /// <summary>切词（持锁）。<paramref name="searchMode"/> = 长词与其已知子词都发（索引侧与 OR 查询用）。</summary>
    internal static List<string> Cut(string text, bool searchMode)
    {
        lock (Gate)
        {
            var raw = searchMode ? Shared.CutForSearch(text) : Shared.Cut(text, cutAll: false);
            return raw.ToList();
        }
    }

    /// <summary>清空缓存（测试/重建用）。注意：jieba 词表本身无法撤销，测试里改动的词会留在进程内。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            ByNovel.Clear();
            Registered.Clear();
            _version = "i0";
        }
    }

    private static string[] Normalize(IEnumerable<string> names)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var squashed = Whitespace.Replace(raw, "");
            foreach (var segment in Separators.Split(squashed))
            {
                var name = segment.Trim();
                if (name.Length >= 2)
                    set.Add(name);
            }
        }
        var ordered = set.ToArray();
        Array.Sort(ordered, StringComparer.Ordinal);
        return ordered;
    }

    private static string ComputeVersion()
    {
        var all = Registered.ToArray();
        Array.Sort(all, StringComparer.Ordinal);
        var material = PolicyVersion + "\n" + string.Join('\n', all);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }
}
