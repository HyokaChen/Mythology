using System.Globalization;
using System.Text.RegularExpressions;
using Core.Shared;

namespace Core.Data.Migrations;

/// <summary>
/// 010：把历史数据里「第N卷 …」形式的空章节还原为分卷实体。
///
/// 背景：<see cref="Volume"/> 实体晚于这批书的数据生成。早期结构把卷标题写成了章节，
/// 于是大纲里所有卷都落在「未分卷」下，每卷只剩一个空章——卷/章两层结构丢失。
///
/// 判定（宁可漏迁，不误删）：
/// <list type="bullet">
///   <item>章节标题匹配 <c>第N卷 …</c>（N 支持中文/阿拉伯数字）；</item>
///   <item>正文为空、状态不是已发布；</item>
///   <item>未归属任何已有分卷（<c>volume-id</c> 为空）；</item>
///   <item>编号可解析且 &gt; 0。</item>
/// </list>
///
/// 处置：标题剩余部分 → <see cref="Volume.Name"/>，编号 → <see cref="Volume.Order"/>；
/// 章节的 summary / plotline-ids / character-ids / notes 一并搬到卷。随后删除空章，
/// 并清理所有指向它的 chapter-id 引用（场景 / 情节线 / 关系 / 世界规则 / 道具时窗 /
/// 出场 / 演化 / 变更集），避免悬空引用。
///
/// 幂等：同 order+name 的卷已存在时不重复创建；章节已删则不再处理。
/// 单本失败记日志并继续，不阻断启动。
/// </summary>
public sealed class VolumeHeadingChapters : IDataMigration
{
    public int Version => 10;
    public string Name => "010_VolumeHeadingChapters";
    public string Description => "把「第N卷」空章节迁移为分卷实体并清理章节引用";

    public TextWriter? Log { get; set; }

    private static readonly Regex s_volumeTitle = new(
        @"^第\s*(?<num>[0-9]+|[一二三四五六七八九十百千万两零〇]+)\s*卷\s*(?<name>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex s_leadingSeparators = new(
        @"^[\s:：·\-—]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    public async Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var roots = MigrationWalkers.CollectRoots(mythologyDir, novelsRoot, Log);
        var scanned = 0;
        var migrated = 0;
        var deleted = 0;
        var skipped = 0;
        var seenNovels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            foreach (var file in MigrationWalkers.EnumerateDocs(root))
            {
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetFileName(file), "novel.yaml", StringComparison.Ordinal))
                    continue;

                var novelDir = Path.GetDirectoryName(file);
                if (novelDir is null || !seenNovels.Add(novelDir))
                    continue;

                scanned++;
                try
                {
                    var result = await MigrateNovelAsync(novelDir, ct);
                    migrated += result.VolumesCreated;
                    deleted += result.ChaptersDeleted;
                    skipped += result.Skipped;
                    if (result.VolumesCreated > 0 || result.ChaptersDeleted > 0)
                    {
                        Log?.WriteLine(
                            $"  {novelDir}：创建 {result.VolumesCreated} 卷，删除 {result.ChaptersDeleted} 个空章，"
                                + $"跳过 {result.Skipped} 个不满足判定"
                        );
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    Log?.WriteLine($"  [warn] 卷标题章节迁移失败 {novelDir}: {ex.Message}");
                }
            }
        }

        Log?.WriteLine(
            $"  卷标题章节：扫描 {scanned} 本，创建 {migrated} 卷，删除 {deleted} 个空章，跳过 {skipped} 本/章。"
        );
    }

    private static async Task<(
        int VolumesCreated,
        int ChaptersDeleted,
        int Skipped
    )> MigrateNovelAsync(string novelDir, CancellationToken ct)
    {
        var chaptersDir = Path.Combine(novelDir, "chapters");
        if (!Directory.Exists(chaptersDir))
            return (0, 0, 0);

        var novelId = ReadNovelId(novelDir);
        if (novelId.Length == 0)
            return (0, 0, 0);

        var volumesDir = Path.Combine(novelDir, "volumes");
        var volumeHeaders = await ReadVolumeHeadersAsync(volumesDir, ct);
        var created = 0;
        var deleted = 0;
        var skipped = 0;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        foreach (var chapterPath in Directory.GetFiles(chaptersDir, "*.md").Order())
        {
            ct.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(chapterPath, ct);
            var (meta, body) = MarkdownHelper.ParseMarkdown(text);
            if (!string.IsNullOrWhiteSpace(body))
            {
                skipped++; // 有正文：可能是真章节（哪怕标题像卷名）
                continue;
            }

            var title = meta.GetValueOrDefault("title")?.Trim() ?? "";
            var match = s_volumeTitle.Match(title);
            if (!match.Success)
            {
                skipped++;
                continue;
            }
            if (
                YamlHelper.YamlToEnum(meta, "status", ChapterStatus.Draft)
                == ChapterStatus.Published
            )
            {
                skipped++; // 已发布章的删除受保护；宁可不迁
                continue;
            }
            if (meta.GetValueOrDefault("volume-id") is { Length: > 0 })
            {
                skipped++; // 已归属某卷：不是「未分卷」里的卷标题占位章
                continue;
            }

            var order = ParseVolumeNumber(match.Groups["num"].Value);
            if (order is null)
            {
                skipped++;
                continue;
            }

            var chapterId = meta.GetValueOrDefault("id")?.Trim() ?? "";
            if (chapterId.Length == 0)
            {
                skipped++;
                continue;
            }

            var name = s_leadingSeparators.Replace(match.Groups["name"].Value.Trim(), "").Trim();
            var summary = meta.GetValueOrDefault("summary") ?? "";
            var plotlineIds = ParseIdList(meta.GetValueOrDefault("plotline-ids"));
            var characterIds = ParseIdList(meta.GetValueOrDefault("character-ids"));
            var notes = meta.GetValueOrDefault("notes") ?? "";

            var existingSameOrder = volumeHeaders.FirstOrDefault(v => v.Order == order.Value);
            if (existingSameOrder.Order == order.Value)
            {
                if (!string.Equals(existingSameOrder.Name, name, StringComparison.Ordinal))
                {
                    // 同卷序已有不同卷名的卷：不重复建同号卷，也不动这个占位章（宁可漏迁）。
                    skipped++;
                    continue;
                }
            }
            else
            {
                await CreateVolumeAsync(
                    volumesDir,
                    novelId,
                    name,
                    order.Value,
                    summary,
                    plotlineIds,
                    characterIds,
                    notes,
                    meta.GetValueOrDefault("created-at") ?? now,
                    meta.GetValueOrDefault("updated-at") ?? now,
                    ct
                );
                volumeHeaders.Add((order.Value, name));
                created++;
            }

            // 先清引用、后删章：中途失败时章节仍在，重跑可继续收敛；不会留下指向不存在章的引用。
            await ClearChapterReferencesAsync(novelDir, chapterId, ct);
            File.Delete(chapterPath);
            deleted++;
        }

        return (created, deleted, skipped);
    }

    private static async Task CreateVolumeAsync(
        string volumesDir,
        string novelId,
        string name,
        int order,
        string summary,
        List<string> plotlineIds,
        List<string> characterIds,
        string notes,
        string createdAt,
        string updatedAt,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(volumesDir);
        var id = Guid.NewGuid().ToString("N")[..12];
        var fileStem = name.Length > 0 ? name : $"第{order}卷";
        var path = Path.Combine(volumesDir, StoreHelper.SanitizeFileName($"{fileStem}-{id}.yaml"));

        var dict = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["novel-id"] = novelId,
            ["name"] = name,
            ["order"] = order,
            ["summary"] = summary,
            ["description"] = "",
            ["goal"] = "",
            ["plotline-ids"] = plotlineIds,
            ["character-ids"] = characterIds,
            ["notes"] = notes,
            ["created-at"] = createdAt,
            ["updated-at"] = updatedAt,
        };

        await AtomicFile.WriteAllTextAsync(path, YamlHelper.Serialize(dict), ct);
    }

    private static string ReadNovelId(string novelDir)
    {
        var path = Path.Combine(novelDir, "novel.yaml");
        if (!File.Exists(path))
            return "";
        try
        {
            return YamlHelper
                .Deserialize(File.ReadAllText(path))
                .GetValueOrDefault("id")
                ?.ToString()
                ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task<List<(int Order, string Name)>> ReadVolumeHeadersAsync(
        string volumesDir,
        CancellationToken ct
    )
    {
        var result = new List<(int Order, string Name)>();
        if (!Directory.Exists(volumesDir))
            return result;

        foreach (var path in Directory.GetFiles(volumesDir, "*.yaml"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dict = YamlHelper.Deserialize(await File.ReadAllTextAsync(path, ct));
                var order = int.TryParse(
                    dict.GetValueOrDefault("order")?.ToString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed
                )
                    ? parsed
                    : 0;
                result.Add((order, dict.GetValueOrDefault("name")?.ToString() ?? ""));
            }
            catch
            {
                // 损坏的卷文件不参与去重判断
            }
        }
        return result;
    }

    /// <summary>清理所有指向被删章节的引用。只改真正命中的文件。</summary>
    private static async Task ClearChapterReferencesAsync(
        string novelDir,
        string chapterId,
        CancellationToken ct
    )
    {
        await ClearMappingReferencesAsync(
            Path.Combine(novelDir, "scenes"),
            chapterId,
            ["chapter-id"],
            ct
        );
        await ClearMappingReferencesAsync(
            Path.Combine(novelDir, "plotlines"),
            chapterId,
            ["planned-reveal-chapter-id", "resolved-at-chapter-id"],
            ct
        );
        await ClearItemOwnershipReferencesAsync(Path.Combine(novelDir, "items"), chapterId, ct);
        await ClearListReferencesAsync(
            Path.Combine(novelDir, "relationships.yaml"),
            chapterId,
            ["since-chapter-id", "until-chapter-id"],
            ct
        );
        await ClearListReferencesAsync(
            Path.Combine(novelDir, "world-rules.yaml"),
            chapterId,
            ["effective-from-chapter-id", "effective-until-chapter-id"],
            ct
        );
        await DropChapterRecordsAsync(Path.Combine(novelDir, "appearances"), chapterId, ct);
        await DropChapterRecordsAsync(Path.Combine(novelDir, "evolution"), chapterId, ct);

        var changesetDir = Path.Combine(novelDir, "changesets", chapterId);
        if (Directory.Exists(changesetDir))
            Directory.Delete(changesetDir, recursive: true);
    }

    private static async Task ClearMappingReferencesAsync(
        string dir,
        string chapterId,
        string[] keys,
        CancellationToken ct
    )
    {
        if (!Directory.Exists(dir))
            return;

        foreach (var path in Directory.GetFiles(dir, "*.yaml"))
        {
            ct.ThrowIfCancellationRequested();
            var dict = YamlHelper.Deserialize(await File.ReadAllTextAsync(path, ct));
            var changed = false;
            foreach (var key in keys)
            {
                if (
                    string.Equals(
                        StoreHelper.GetString(dict, key),
                        chapterId,
                        StringComparison.Ordinal
                    )
                )
                {
                    dict.Remove(key);
                    changed = true;
                }
            }
            if (changed)
                await AtomicFile.WriteAllTextAsync(path, YamlHelper.Serialize(dict), ct);
        }
    }

    private static async Task ClearItemOwnershipReferencesAsync(
        string itemsDir,
        string chapterId,
        CancellationToken ct
    )
    {
        if (!Directory.Exists(itemsDir))
            return;

        foreach (var path in Directory.GetFiles(itemsDir, "*.yaml"))
        {
            ct.ThrowIfCancellationRequested();
            var dict = YamlHelper.Deserialize(await File.ReadAllTextAsync(path, ct));
            if (
                dict.GetValueOrDefault("ownership-periods") is not List<object?> periods
                || periods.Count == 0
            )
                continue;

            var changed = false;
            foreach (var period in periods)
            {
                if (period is not Dictionary<string, object?> map)
                    continue;
                foreach (var key in new[] { "since-chapter-id", "until-chapter-id" })
                {
                    if (
                        string.Equals(
                            StoreHelper.GetString(map, key),
                            chapterId,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        map.Remove(key);
                        changed = true;
                    }
                }
            }
            if (changed)
                await AtomicFile.WriteAllTextAsync(path, YamlHelper.Serialize(dict), ct);
        }
    }

    private static async Task ClearListReferencesAsync(
        string path,
        string chapterId,
        string[] keys,
        CancellationToken ct
    )
    {
        if (!File.Exists(path))
            return;

        var list = YamlHelper.DeserializeList(await File.ReadAllTextAsync(path, ct));
        var changed = false;
        foreach (var dict in list)
        {
            foreach (var key in keys)
            {
                if (
                    string.Equals(
                        StoreHelper.GetString(dict, key),
                        chapterId,
                        StringComparison.Ordinal
                    )
                )
                {
                    dict.Remove(key);
                    changed = true;
                }
            }
        }
        if (changed)
            await AtomicFile.WriteAllTextAsync(path, YamlHelper.SerializeList(list), ct);
    }

    /// <summary>出场 / 演化分片按记录删除；删空则连文件一起删（与删章级联语义一致）。</summary>
    private static async Task DropChapterRecordsAsync(
        string dir,
        string chapterId,
        CancellationToken ct
    )
    {
        if (!Directory.Exists(dir))
            return;

        foreach (var path in Directory.GetFiles(dir, "*.yaml"))
        {
            ct.ThrowIfCancellationRequested();
            var list = YamlHelper.DeserializeList(await File.ReadAllTextAsync(path, ct));
            var removed = list.RemoveAll(dict =>
                string.Equals(
                    StoreHelper.GetString(dict, "chapter-id"),
                    chapterId,
                    StringComparison.Ordinal
                )
            );
            if (removed == 0)
                continue;

            if (list.Count == 0)
                File.Delete(path);
            else
                await AtomicFile.WriteAllTextAsync(path, YamlHelper.SerializeList(list), ct);
        }
    }

    private static List<string> ParseIdList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            :
            [
                .. value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                ),
            ];

    /// <summary>解析「一」..「九千九百九十九」与阿拉伯数字；失败返回 null（不猜）。</summary>
    private static int? ParseVolumeNumber(string text)
    {
        if (
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var arabic)
            && arabic > 0
        )
            return arabic;

        var total = 0;
        var section = 0;
        var number = 0;
        var any = false;
        foreach (var c in text)
        {
            var digit = ChineseDigit(c);
            if (digit >= 0)
            {
                number = digit;
                any = true;
                continue;
            }

            var unit = c switch
            {
                '十' => 10,
                '百' => 100,
                '千' => 1000,
                '万' => 10000,
                _ => 0,
            };
            if (unit == 0)
            {
                if (c is '零' or '〇')
                    continue;
                return null;
            }

            any = true;
            if (unit == 10000)
            {
                total += (section + (number == 0 ? 1 : number)) * unit;
                section = 0;
                number = 0;
            }
            else
            {
                section += (number == 0 ? 1 : number) * unit;
                number = 0;
            }
        }

        total += section + number;
        return any && total > 0 ? total : null;
    }

    private static int ChineseDigit(char c) =>
        c switch
        {
            '一' => 1,
            '二' or '两' => 2,
            '三' => 3,
            '四' => 4,
            '五' => 5,
            '六' => 6,
            '七' => 7,
            '八' => 8,
            '九' => 9,
            '零' or '〇' => 0,
            _ => -1,
        };
}
