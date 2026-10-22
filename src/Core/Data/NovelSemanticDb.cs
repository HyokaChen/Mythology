using Core.Shared;
using Tacos;

namespace Core.Data;

// ═══════════════════════════════════════════════════════════
//  L0 Inputs
// ═══════════════════════════════════════════════════════════

/// <summary>
/// L0 · 文件事实。Text 进入 Input 是为了让查询体保持严格纯函数；
/// Mtime/Size 用于通道 1（mtime 探测）跳过不必要的全文读取。
/// </summary>
[Input]
public readonly record struct SourceFile(string Path, long Mtime, long Size, string Text);

/// <summary>
/// L0 · 目录事实。聚合查询必须知道“有哪些文件”，因此把目录内容本身建成 Input。
/// SortedFileNames 使用结构相等比较器，保证仅名单变化才触发下游重算。
/// </summary>
[Input]
public readonly record struct DirListing(
    string NovelId,
    string Kind,
    [property: TacosComparer(typeof(StringArrayComparer))] string[] SortedFileNames
);

// ═══════════════════════════════════════════════════════════
//  L1 Immutable Projections
// ═══════════════════════════════════════════════════════════

/// <summary>章节的不可变投影。缓存层只持有 readonly record struct，避免 C2 的可变污染。</summary>
public readonly record struct ChapterData(
    string Id,
    string NovelId,
    string Title,
    int Order,
    ChapterStatus Status,
    string Content,
    string? VolumeId,
    string Summary,
    string PlotlineIds, // 逗号分隔（缓存层保持列式，不持 List）
    string CharacterIds,
    string Notes
);

/// <summary>角色的不可变投影（仅保留聚合/门面需要的最小字段）。</summary>
public readonly record struct CharacterData(
    string Id,
    string NovelId,
    string Name,
    CharacterRole Role,
    CharacterStatus Status,
    string? FactionId
);

/// <summary>情节线的不可变投影。</summary>
public readonly record struct PlotlineData(
    string Id,
    string NovelId,
    string Name,
    PlotlineType Type,
    string? ResolvedAtChapterId
);

/// <summary>关系行投影。</summary>
public readonly record struct RelationRow(
    string CharacterAId,
    string CharacterBId,
    RelationshipType Type,
    RelationshipStrength Strength,
    string Description
);

/// <summary>
/// 方案 2B 的 Tacos 数据库。四层查询图：
/// L0 Input（SourceFile/DirListing）→ L1 Parse → L2 Projection → L3 Aggregate。
/// </summary>
[TacosInput(typeof(SourceFile))]
[TacosInput(typeof(DirListing))]
public sealed partial class NovelSemanticDb : TacosDatabase
{
    // ──────────────────────────────────────────────────────
    //  InputId 注册表（由 NovelSemanticService 维护）
    //  这些字典本身不是 tracked dependencies；查询通过读取对应 Input/Query
    //  建立依赖，因此目录/文件变化会经由 Input 值传播。
    // ──────────────────────────────────────────────────────
    private readonly List<string> _novelIds = [];
    private readonly Dictionary<(string NovelId, string Kind), InputId<DirListing>> _dirListings =
    [];
    private readonly Dictionary<string, InputId<SourceFile>> _sourceFiles = [];
    private readonly Dictionary<string, InputId<SourceFile>> _relationshipFiles = [];
    private readonly Dictionary<
        string,
        (InputId<SourceFile> Source, string NovelId, string Kind)
    > _entityFiles = new(StringComparer.Ordinal);

    internal IReadOnlyList<string> NovelIds => _novelIds;

    internal InputId<DirListing>? GetDirListingId(string novelId, string kind) =>
        _dirListings.TryGetValue((novelId, kind), out var id) ? id : null;

    internal InputId<SourceFile>? GetSourceFileId(string novelId, string fileName)
    {
        // 完整路径由服务层写入；这里用 novelId + 相对文件名拼出稳定键。
        var key = Path.Combine(_sourceKeysPrefix(novelId), fileName);
        return _sourceFiles.TryGetValue(key, out var id) ? id : null;
    }

    internal InputId<SourceFile>? GetRelationshipFileId(string novelId) =>
        _relationshipFiles.TryGetValue(novelId, out var id) ? id : null;

    internal void RegisterNovel(string novelId)
    {
        if (!_novelIds.Contains(novelId))
            _novelIds.Add(novelId);
    }

    internal void RegisterDirListing(string novelId, string kind, InputId<DirListing> id)
    {
        _dirListings[(novelId, kind)] = id;
    }

    internal void RegisterSourceFile(
        string novelId,
        string fileName,
        string fullPath,
        InputId<SourceFile> id
    )
    {
        _sourceFiles[Path.Combine(_sourceKeysPrefix(novelId), fileName)] = id;
        // 同时保留 fullPath 键，便于按完整路径查找。
        _sourceFiles[fullPath] = id;
    }

    internal void RegisterRelationshipFile(string novelId, InputId<SourceFile> id) =>
        _relationshipFiles[novelId] = id;

    internal void RegisterEntityFile(
        string entityId,
        InputId<SourceFile> source,
        string novelId,
        string kind
    )
    {
        _entityFiles[entityId] = (source, novelId, kind);
    }

    internal bool TryGetEntityFile(
        string entityId,
        out InputId<SourceFile> source,
        out string novelId,
        out string kind
    )
    {
        if (_entityFiles.TryGetValue(entityId, out var entry))
        {
            source = entry.Source;
            novelId = entry.NovelId;
            kind = entry.Kind;
            return true;
        }

        source = default;
        novelId = "";
        kind = "";
        return false;
    }

    internal void ClearNovelState()
    {
        _novelIds.Clear();
        _dirListings.Clear();
        _sourceFiles.Clear();
        _relationshipFiles.Clear();
        _entityFiles.Clear();
        ClearCaches();
    }

    private static string _sourceKeysPrefix(string novelId) => $"{novelId}::";

    // ──────────────────────────────────────────────────────
    //  L1 · Parse
    // ──────────────────────────────────────────────────────

    [Tracked]
    public partial ChapterData ParseChapter(InputId<SourceFile> file);

    private ChapterData ParseChapterCore(InputId<SourceFile> file)
    {
        var text = GetSourceFileIdText(file);
        var (meta, body) = MarkdownHelper.ParseMarkdown(text);
        var id = meta.GetValueOrDefault("id") ?? "";
        return new ChapterData(
            id,
            GetString(meta, "novel-id") ?? "",
            meta.GetValueOrDefault("title") ?? "",
            int.TryParse(meta.GetValueOrDefault("order"), out var order) ? order : 0,
            YamlHelper.YamlToEnum(meta, "status", ChapterStatus.Draft),
            body,
            meta.GetValueOrDefault("volume-id") is { Length: > 0 } vid ? vid : null,
            meta.GetValueOrDefault("summary") ?? "",
            meta.GetValueOrDefault("plotline-ids") ?? "",
            meta.GetValueOrDefault("character-ids") ?? "",
            meta.GetValueOrDefault("notes") ?? ""
        );
    }

    [Tracked]
    public partial CharacterData ParseCharacter(InputId<SourceFile> file);

    private CharacterData ParseCharacterCore(InputId<SourceFile> file)
    {
        var text = GetSourceFileIdText(file);
        var dict = YamlHelper.Deserialize(text);
        return new CharacterData(
            StoreHelper.GetString(dict, "id") ?? "",
            StoreHelper.GetString(dict, "novel-id") ?? "",
            StoreHelper.GetString(dict, "name") ?? "",
            YamlHelper.YamlToEnum(dict, "role", CharacterRole.Supporting),
            YamlHelper.YamlToEnum(dict, "status", CharacterStatus.Active),
            StoreHelper.GetString(dict, "faction-id")
        );
    }

    [Tracked]
    public partial PlotlineData ParsePlotline(InputId<SourceFile> file);

    private PlotlineData ParsePlotlineCore(InputId<SourceFile> file)
    {
        var text = GetSourceFileIdText(file);
        var dict = YamlHelper.Deserialize(text);
        return new PlotlineData(
            StoreHelper.GetString(dict, "id") ?? "",
            StoreHelper.GetString(dict, "novel-id") ?? "",
            StoreHelper.GetString(dict, "name") ?? "",
            YamlHelper.YamlToEnum(dict, "type", PlotlineType.Main),
            StoreHelper.GetString(dict, "resolved-at-chapter-id")
        );
    }

    [Tracked]
    [TacosComparer(typeof(RelationRowArrayComparer))]
    public partial RelationRow[] ParseRelationships(InputId<SourceFile> file);

    private RelationRow[] ParseRelationshipsCore(InputId<SourceFile> file)
    {
        var text = GetSourceFileIdText(file);
        var rows = YamlHelper.DeserializeList(text);
        var result = new RelationRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            result[i] = new RelationRow(
                StoreHelper.GetString(row, "character-a-id") ?? "",
                StoreHelper.GetString(row, "character-b-id") ?? "",
                RelationshipTypeExtensions.ParseRelationshipType(StoreHelper.GetString(row, "type"))
                    ?? RelationshipType.Friend,
                YamlHelper.YamlToEnum(row, "strength", RelationshipStrength.Medium),
                StoreHelper.GetString(row, "description") ?? ""
            );
        }
        return result;
    }

    // ──────────────────────────────────────────────────────
    //  L1.5 · id → file 映射（内部 tracked 查询）
    //  这些查询让字符串键的 L2/L3 查询建立到 L1/L0 的正确依赖边，
    //  从而在文件增删/重命名时也能失效，而不是依赖手写字典。
    // ──────────────────────────────────────────────────────

    [Tracked]
    public partial InputId<SourceFile>? ChapterFile(string chapterId);

    private InputId<SourceFile>? ChapterFileCore(string chapterId)
    {
        foreach (var novelId in _novelIds)
        {
            var dirId = GetDirListingId(novelId, "chapters");
            if (dirId is null)
                continue;

            foreach (var fileName in GetDirListingIdSortedFileNames(dirId.Value))
            {
                var sourceId = GetSourceFileId(novelId, fileName);
                if (sourceId is null)
                    continue;
                if (ParseChapter(sourceId.Value).Id == chapterId)
                    return sourceId;
            }
        }
        return null;
    }

    [Tracked]
    public partial InputId<SourceFile>? CharacterFile(string characterId);

    private InputId<SourceFile>? CharacterFileCore(string characterId)
    {
        foreach (var novelId in _novelIds)
        {
            var dirId = GetDirListingId(novelId, "characters");
            if (dirId is null)
                continue;

            foreach (var fileName in GetDirListingIdSortedFileNames(dirId.Value))
            {
                var sourceId = GetSourceFileId(novelId, fileName);
                if (sourceId is null)
                    continue;
                if (ParseCharacter(sourceId.Value).Id == characterId)
                    return sourceId;
            }
        }
        return null;
    }

    [Tracked]
    public partial InputId<SourceFile>? PlotlineFile(string plotlineId);

    private InputId<SourceFile>? PlotlineFileCore(string plotlineId)
    {
        foreach (var novelId in _novelIds)
        {
            var dirId = GetDirListingId(novelId, "plotlines");
            if (dirId is null)
                continue;

            foreach (var fileName in GetDirListingIdSortedFileNames(dirId.Value))
            {
                var sourceId = GetSourceFileId(novelId, fileName);
                if (sourceId is null)
                    continue;
                if (ParsePlotline(sourceId.Value).Id == plotlineId)
                    return sourceId;
            }
        }
        return null;
    }

    // ──────────────────────────────────────────────────────
    //  L2 · Projection
    // ──────────────────────────────────────────────────────

    [Tracked]
    public partial ChapterStatus StatusOf(string chapterId);

    private ChapterStatus StatusOfCore(string chapterId)
    {
        var file = ChapterFile(chapterId);
        return file is null ? ChapterStatus.Draft : ParseChapter(file.Value).Status;
    }

    [Tracked]
    public partial int WordCountOf(string chapterId);

    private int WordCountOfCore(string chapterId)
    {
        var file = ChapterFile(chapterId);
        if (file is null)
            return 0;
        var content = ParseChapter(file.Value).Content;
        return CountWords(content);
    }

    [Tracked]
    [TacosComparer(typeof(StringArrayComparer))]
    public partial string[] ChapterIds(string novelId);

    private string[] ChapterIdsCore(string novelId)
    {
        var dirId = GetDirListingId(novelId, "chapters");
        if (dirId is null)
            return [];

        var result = new List<(int Order, string Id)>();
        foreach (var fileName in GetDirListingIdSortedFileNames(dirId.Value))
        {
            var sourceId = GetSourceFileId(novelId, fileName);
            if (sourceId is null)
                continue;
            var chapter = ParseChapter(sourceId.Value);
            if (chapter.Id.Length > 0)
                result.Add((chapter.Order, chapter.Id));
        }

        return result
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Id)
            .ToArray();
    }

    [Tracked]
    [TacosComparer(typeof(StringArrayComparer))]
    public partial string[] CharacterIds(string novelId);

    private string[] CharacterIdsCore(string novelId)
    {
        var dirId = GetDirListingId(novelId, "characters");
        if (dirId is null)
            return [];

        var result = new List<(string Name, string Id)>();
        foreach (var fileName in GetDirListingIdSortedFileNames(dirId.Value))
        {
            var sourceId = GetSourceFileId(novelId, fileName);
            if (sourceId is null)
                continue;
            var character = ParseCharacter(sourceId.Value);
            if (character.Id.Length > 0)
                result.Add((character.Name, character.Id));
        }

        return result
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Id)
            .ToArray();
    }

    [Tracked]
    [TacosComparer(typeof(StringArrayComparer))]
    public partial string[] PlotlineIds(string novelId);

    private string[] PlotlineIdsCore(string novelId)
    {
        var dirId = GetDirListingId(novelId, "plotlines");
        if (dirId is null)
            return [];

        var result = new List<string>();
        foreach (var fileName in GetDirListingIdSortedFileNames(dirId.Value))
        {
            var sourceId = GetSourceFileId(novelId, fileName);
            if (sourceId is null)
                continue;
            var plotline = ParsePlotline(sourceId.Value);
            if (plotline.Id.Length > 0)
                result.Add(plotline.Id);
        }

        return result.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    [Tracked]
    [TacosComparer(typeof(StringArrayComparer))]
    public partial string[] ConnectedCharacterIds(string novelId);

    private string[] ConnectedCharacterIdsCore(string novelId)
    {
        var relFile = GetRelationshipFileId(novelId);
        if (relFile is null)
            return [];

        var connected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ParseRelationships(relFile.Value))
        {
            if (row.CharacterAId.Length > 0)
                connected.Add(row.CharacterAId);
            if (row.CharacterBId.Length > 0)
                connected.Add(row.CharacterBId);
        }

        return connected.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    [Tracked]
    public partial bool IsOpenForeshadow(string plotlineId);

    private bool IsOpenForeshadowCore(string plotlineId)
    {
        var file = PlotlineFile(plotlineId);
        if (file is null)
            return false;
        var plotline = ParsePlotline(file.Value);
        return plotline is { Type: PlotlineType.Foreshadowing, ResolvedAtChapterId: null };
    }

    // ──────────────────────────────────────────────────────
    //  L3 · Aggregate
    // ──────────────────────────────────────────────────────

    [Tracked]
    public partial int DraftChapterCount(string novelId);

    private int DraftChapterCountCore(string novelId)
    {
        var count = 0;
        foreach (var chapterId in ChapterIds(novelId))
        {
            if (StatusOf(chapterId) == ChapterStatus.Draft)
                count++;
        }
        return count;
    }

    [Tracked]
    public partial int OrphanCharacterCount(string novelId);

    private int OrphanCharacterCountCore(string novelId)
    {
        var all = CharacterIds(novelId);
        // 与现有审计语义保持一致：角色少于 2 个时不判定“孤立角色”。
        if (all.Length < 2)
            return 0;

        var connected = ConnectedCharacterIds(novelId);
        var connectedSet = new HashSet<string>(connected, StringComparer.Ordinal);
        return all.Count(id => !connectedSet.Contains(id));
    }

    [Tracked]
    public partial int OverdueForeshadowCount(string novelId);

    private int OverdueForeshadowCountCore(string novelId)
    {
        var count = 0;
        foreach (var plotlineId in PlotlineIds(novelId))
        {
            if (IsOpenForeshadow(plotlineId))
                count++;
        }
        return count;
    }

    [Tracked]
    public partial int TotalWordCount(string novelId);

    private int TotalWordCountCore(string novelId)
    {
        return ChapterIds(novelId).Sum(WordCountOf);
    }

    // ──────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────

    private static string? GetString(Dictionary<string, string> dict, string key) =>
        dict.GetValueOrDefault(key);

    /// <summary>
    /// 中文/英文混合正文的轻量词数估算。
    /// 中文按字符数计，连续 ASCII 单词按空白分隔计。
    /// </summary>
    private static int CountWords(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            var code = (int)ch;

            // CJK 统一表意文字按单字计，而不是把连续汉字合并成一个“单词”。
            if (code is >= 0x3400 and <= 0x9FFF)
            {
                count++;
                inWord = false;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (char.IsLetterOrDigit(ch))
            {
                if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
            }
        }
        return count;
    }
}

// ─────────────────────────────────────────────────────────────
//  Comparers（数组/记录数组的结构相等）
// ─────────────────────────────────────────────────────────────

/// <summary>string[] 结构比较器。</summary>
public sealed class StringArrayComparer : IEqualityComparer<string[]>
{
    public static StringArrayComparer Instance { get; } = new();

    public bool Equals(string[]? x, string[]? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        if (x.Length != y.Length)
            return false;
        return !x.Where((t, i) => !string.Equals(t, y[i], StringComparison.Ordinal)).Any();
    }

    public int GetHashCode(string[] obj)
    {
        var hash = new HashCode();
        foreach (var item in obj)
            hash.Add(item);
        return hash.ToHashCode();
    }
}

/// <summary>RelationRow[] 结构比较器。</summary>
public sealed class RelationRowArrayComparer : IEqualityComparer<RelationRow[]>
{
    public static RelationRowArrayComparer Instance { get; } = new();

    public bool Equals(RelationRow[]? x, RelationRow[]? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        if (x.Length != y.Length)
            return false;
        for (var i = 0; i < x.Length; i++)
        {
            if (!x[i].Equals(y[i]))
                return false;
        }
        return true;
    }

    public int GetHashCode(RelationRow[] obj)
    {
        var hash = new HashCode();
        foreach (var item in obj)
            hash.Add(item);
        return hash.ToHashCode();
    }
}
