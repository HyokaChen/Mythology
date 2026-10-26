using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using JiebaNet.Segmenter;

namespace Core.Vector;

/// <summary>
/// 每部小说的 jieba 用户词典：角色/组织/地点/道具/情节线/场景名作为分词依据，
/// 避免「皮皮龙 → 皮皮 + 龙」「松雪美乃 → 松雪美 + 乃是」这类实体名被切开。
///
/// 每个 novelId 独立一个 <see cref="JiebaSegmenter"/>，不共享全局词典，避免跨小说污染。
/// 名称集合变化时返回 true，调用方应把该小说知识域标记 stale 后重建。
/// </summary>
public static class NovelLexicon
{
    private static readonly JiebaSegmenter DefaultSegmenter = new();
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(
        StringComparer.Ordinal
    );

    private sealed record Entry(string Hash, JiebaSegmenter Segmenter);

    /// <summary>更新小说的实体名词典；名称集合未变化返回 false。</summary>
    public static bool Update(string novelId, IEnumerable<string> names)
    {
        ArgumentException.ThrowIfNullOrEmpty(novelId);

        var normalized = names
            .Select(name => name.Trim())
            .Where(name => name.Length >= 2 && !name.Any(char.IsWhiteSpace))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalized)))
        );
        if (Entries.TryGetValue(novelId, out var existing) && existing.Hash == hash)
            return false;

        var segmenter = new JiebaSegmenter();
        foreach (var name in normalized)
            segmenter.AddWord(name, 10, "nz");
        Entries[novelId] = new Entry(hash, segmenter);
        return true;
    }

    /// <summary>取该小说的词典版本（名称集合哈希）；未登记返回空串。</summary>
    public static string GetVersion(string? novelId) =>
        !string.IsNullOrEmpty(novelId) && Entries.TryGetValue(novelId, out var entry)
            ? entry.Hash
            : "";

    /// <summary>取该小说的分词器；未登记时回退到默认 jieba。</summary>
    internal static JiebaSegmenter GetSegmenter(string? novelId) =>
        !string.IsNullOrEmpty(novelId) && Entries.TryGetValue(novelId, out var entry)
            ? entry.Segmenter
            : DefaultSegmenter;

    /// <summary>清空缓存（测试/重建用）。</summary>
    public static void Clear() => Entries.Clear();
}
