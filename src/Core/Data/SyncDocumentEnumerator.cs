using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Core.Shared;

namespace Core.Data;

/// <summary>
/// 本地同步文档（cloud-novel-sync v3）：一个本地文件 = 一条推送单元。
/// Sha256 恒等于 Content 字符串 UTF-8 字节的 SHA-256 hex（与云端校验语义一致，
/// utf8/base64 均如此）。
/// </summary>
public sealed record LocalSyncDocument(
    string DocumentType,
    string EntityId,
    string? Title,
    string RelativePath,
    string Content,
    string Sha256,
    string ContentEncoding
);

/// <summary>
/// 本地小说目录枚举器 — 把 novelsRoot 下某本小说的全部文件映射为同步文档。
/// 类型映射：novel.yaml → novel；chapters/*.md → chapter（front-matter id）；
/// 已知实体目录 *.yaml → 对应类型（YAML id 字段）；appearances/evolution 按章分片 →
/// appearance/state-change（EntityId = 文件名解析的 ch_{Order}）；
/// relationships/timeline/faction-relationships → self；
/// 其余全部 → file（EntityId = 相对路径），保证没有任何文件被遗漏。
/// 非 UTF-8 严格解码失败（二进制）→ ContentEncoding=base64。
/// </summary>
public static class SyncDocumentEnumerator
{
    /// <summary>单文件集合文档与 novel.yaml 的固定 EntityId。</summary>
    public const string SelfEntityId = "self";

    private static readonly string[] CollectionFiles =
    [
        "relationships.yaml",
        "timeline.yaml",
        "faction-relationships.yaml",
    ];

    private static readonly Dictionary<string, string> EntityDirToType = new(StringComparer.Ordinal)
    {
        ["characters"] = "character",
        ["plotlines"] = "plotline",
        ["factions"] = "faction",
        ["locations"] = "location",
        ["items"] = "item",
        ["scenes"] = "scene",
        ["volumes"] = "volume",
    };

    /// <summary>
    /// 演化分片目录 → 同步文档类型（ENTITY-EVOLUTION E1）。与 <see cref="EntityDirToType"/> 分开：
    /// 这些目录按章整文件存储（appearances/ch{Order:D3}.yaml），内容是 YAML **列表**而非 mapping，
    /// 文件内没有顶层 id，故实体 Id 由文件名解析为 <c>ch_{Order}</c>（与 setup.check 的
    /// <c>ch_{Order}</c> 章节引用规范化同形，云端 云端图谱投影器 认这个类型）。
    /// </summary>
    private static readonly Dictionary<string, string> EvolutionDirToType = new(
        StringComparer.Ordinal
    )
    {
        ["appearances"] = "appearance",
        ["evolution"] = "state-change",
    };

    /// <summary>小说文件体积硬上限（与云端 MaxSyncDocumentBytes 对齐）。</summary>
    public const int MaxDocumentBytes = 2 * 1024 * 1024;

    public static async Task<IReadOnlyList<LocalSyncDocument>> EnumerateAsync(
        string novelsRoot,
        string novelId,
        CancellationToken ct = default
    )
    {
        var directories = FindNovelDirectories(novelsRoot, novelId);
        if (directories.Count == 0)
        {
            return [];
        }

        var documents = new List<LocalSyncDocument>();
        // 多目录同 novelId（用户复制目录未重生成 id）：全部降级 file，避免 typed key 互相覆盖
        var degradeToFiles = directories.Count > 1;
        var novelTitle =
            directories.Count == 1 ? await ReadNovelTitleAsync(directories[0], ct) : null;

        foreach (var directory in directories)
        {
            foreach (
                var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            )
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(novelsRoot, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (IsIgnored(relativePath))
                {
                    continue;
                }
                var document = await ReadDocumentAsync(
                    file,
                    relativePath,
                    degradeToFiles,
                    novelTitle,
                    ct
                );
                if (document is not null)
                {
                    documents.Add(document);
                }
            }
        }

        // 重复 key（如两章共用同一 front-matter id）：按路径排序取第一个，其余降级 file
        return ResolveDuplicateKeys(documents);
    }

    /// <summary>发现 novelsRoot 下全部小说目录（novel.yaml 含 id 字段）。`:cloud sync all` 用。</summary>
    public static IReadOnlyList<(string NovelId, string Directory)> DiscoverNovels(
        string novelsRoot
    )
    {
        if (!Directory.Exists(novelsRoot))
        {
            return [];
        }
        var results = new List<(string, string)>();
        foreach (var directory in Directory.EnumerateDirectories(novelsRoot))
        {
            var novelYaml = Path.Combine(directory, MythologyPaths.NovelMetaFileName);
            if (!File.Exists(novelYaml) || IsIgnored(Path.GetRelativePath(novelsRoot, directory)))
            {
                continue;
            }
            try
            {
                var meta = YamlHelper.Deserialize(File.ReadAllText(novelYaml));
                if (
                    meta.TryGetValue("id", out var value)
                    && value is { } id
                    && !string.IsNullOrWhiteSpace(id.ToString())
                )
                {
                    results.Add((id.ToString()!, directory));
                }
            }
            catch
            {
                // 损坏的 novel.yaml 不阻塞其余小说的发现
            }
        }
        return results;
    }

    /// <summary>
    /// 发现清单内全部项目目录下的小说（跨目录统一列表）。同一 novelId 只保留一次——
    /// 书被复制到两处时按各自根同步会互相覆盖基线，去重后由 <c>FindRootForNovel</c> 定落点。
    /// </summary>
    public static IReadOnlyList<(string NovelId, string Directory)> DiscoverNovels(
        IEnumerable<string> novelsRoots
    )
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<(string, string)>();
        foreach (var novelsRoot in novelsRoots)
        {
            foreach (var found in DiscoverNovels(novelsRoot))
            {
                if (seen.Add(found.NovelId))
                    results.Add(found);
            }
        }
        return results;
    }

    private static List<string> FindNovelDirectories(string novelsRoot, string novelId)
    {
        if (!Directory.Exists(novelsRoot))
        {
            return [];
        }
        var matches = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(novelsRoot))
        {
            var novelYaml = Path.Combine(directory, MythologyPaths.NovelMetaFileName);
            if (!File.Exists(novelYaml))
            {
                continue;
            }
            try
            {
                var meta = YamlHelper.Deserialize(File.ReadAllText(novelYaml));
                if (
                    meta.TryGetValue("id", out var value)
                    && string.Equals(value?.ToString(), novelId, StringComparison.Ordinal)
                )
                {
                    matches.Add(directory);
                }
            }
            catch
            {
                // 解析失败 = 不是可识别的小说目录
            }
        }
        return matches;
    }

    private static async Task<string?> ReadNovelTitleAsync(
        string novelDirectory,
        CancellationToken ct
    )
    {
        try
        {
            var content = await File.ReadAllTextAsync(
                Path.Combine(novelDirectory, MythologyPaths.NovelMetaFileName),
                ct
            );
            return YamlHelper.Deserialize(content).GetValueOrDefault("title")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<LocalSyncDocument?> ReadDocumentAsync(
        string filePath,
        string relativePath,
        bool degradeToFiles,
        string? novelTitle,
        CancellationToken ct
    )
    {
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var contentBytes = hasBom ? bytes[3..] : bytes;

        string content;
        string encoding;
        try
        {
            content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(contentBytes);
            encoding = "utf8";
        }
        catch (DecoderFallbackException)
        {
            content = Convert.ToBase64String(contentBytes);
            encoding = "base64";
        }

        var (type, entityId, title) = degradeToFiles
            ? (TypeOfFile, relativePath, FileTitle(relativePath))
            : Classify(filePath, relativePath, content, novelTitle);
        return new LocalSyncDocument(
            type,
            entityId,
            title,
            relativePath,
            content,
            ComputeSha(content),
            encoding
        );
    }

    internal static string ComputeSha(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private const string TypeOfFile = "file";

    /// <summary>类型分类：返回 (type, entityId, title)。无法识别 id 的 YAML 降级 file。</summary>
    private static (string Type, string EntityId, string? Title) Classify(
        string filePath,
        string relativePath,
        string content,
        string? novelTitle
    )
    {
        var fileName = Path.GetFileName(filePath);
        var parentDir = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "";

        if (fileName == MythologyPaths.NovelMetaFileName)
        {
            // novel.yaml（相对路径首段即小说目录）
            var meta = YamlHelper.Deserialize(content);
            var id = meta.GetValueOrDefault("id")?.ToString();
            return id is null
                ? (TypeOfFile, relativePath, FileTitle(relativePath))
                : ("novel", SelfEntityId, meta.GetValueOrDefault("title")?.ToString());
        }

        if (
            parentDir == MythologyPaths.ChapterSubDir
            && fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        )
        {
            var (meta, _) = MarkdownHelper.ParseMarkdown(content);
            var id = meta.GetValueOrDefault("id");
            var title = meta.GetValueOrDefault("title");
            return id is null
                ? (TypeOfFile, relativePath, FileTitle(relativePath))
                : ("chapter", id, title);
        }

        if (EntityDirToType.TryGetValue(parentDir, out var entityType))
        {
            try
            {
                var meta = YamlHelper.Deserialize(content);
                var id = meta.GetValueOrDefault("id")?.ToString();
                if (id is not null)
                {
                    var titleKey = entityType == "scene" ? "title" : "name";
                    return (entityType, id, meta.GetValueOrDefault(titleKey)?.ToString());
                }
            }
            catch
            {
                // 解析失败的 YAML 降级 file，不丢文件
            }
            return (TypeOfFile, relativePath, FileTitle(relativePath));
        }

        // 演化分片（ENTITY-EVOLUTION E1）：整章一个文件、内容是 YAML 列表（无顶层 id），
        // EntityId 取文件名解析出的 ch_{Order}；非该形态（用户自放的文件）降级 file，不丢文件。
        if (EvolutionDirToType.TryGetValue(parentDir, out var evolutionType))
        {
            var shardOrder = ParseShardOrder(fileName);
            return shardOrder is null
                ? (TypeOfFile, relativePath, FileTitle(relativePath))
                : (evolutionType, $"ch_{shardOrder}", FileTitle(relativePath));
        }

        if (CollectionFiles.Contains(fileName, StringComparer.Ordinal))
        {
            return (fileName[..^".yaml".Length], SelfEntityId, novelTitle);
        }

        return (TypeOfFile, relativePath, FileTitle(relativePath));
    }

    private static string? FileTitle(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        return name.Length > 160 ? name[..160] : name;
    }

    /// <summary>
    /// 解析演化分片名 <c>ch{Order:D3}.yaml</c> → Order（去前导零，与 ch_{Order} 引用同形）。
    /// 非该形态返回 null（调用方降级 file，不丢文件）。用 <see cref="NumberStyles.None"/>：
    /// 不接受符号/千分位/空白，<c>ch-1</c>、<c>ch 1</c>、<c>ch1x</c> 一律拒绝。
    /// </summary>
    private static int? ParseShardOrder(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return
            stem.Length > 2
            && stem.StartsWith("ch", StringComparison.Ordinal)
            && int.TryParse(
                stem.AsSpan(2),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var order
            )
            ? order
            : null;
    }

    /// <summary>忽略清单：点开头（.DS_Store/.mythology/.git）、系统/临时文件、冲突目录、node_modules。</summary>
    public static bool IsIgnored(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }
            if (
                segment.StartsWith('.')
                || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("cloud-conflicts", StringComparison.Ordinal)
                || segment.Equals("ontology", StringComparison.Ordinal)
                || segment.Equals("changesets", StringComparison.Ordinal)
                // 作者随手记只在本机（不送云端，见驱动推演与随手记 §Assumptions）
                || segment.Equals("notes", StringComparison.Ordinal)
            )
            {
                return true;
            }
        }
        var fileName = relativePath.Split('/')[^1];
        return fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("~$", StringComparison.Ordinal)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".swp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>重复 key 兜底：同 (type, entityId) 只留路径排序第一者，其余降级 file，不丢文件。</summary>
    private static List<LocalSyncDocument> ResolveDuplicateKeys(List<LocalSyncDocument> documents)
    {
        var ordered = documents.OrderBy(d => d.RelativePath, StringComparer.Ordinal).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LocalSyncDocument>(ordered.Count);
        foreach (var document in ordered)
        {
            var key = $"{document.DocumentType}:{document.EntityId}";
            if (document.DocumentType == TypeOfFile || seen.Add(key))
            {
                result.Add(document);
            }
            else
            {
                result.Add(
                    document with
                    {
                        DocumentType = TypeOfFile,
                        EntityId = document.RelativePath,
                        Title = FileTitle(document.RelativePath),
                    }
                );
            }
        }
        return result;
    }
}
