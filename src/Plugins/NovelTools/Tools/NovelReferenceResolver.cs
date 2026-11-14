using System.Globalization;
using System.Text;
using Core.Data;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 写入工具共用的引用解析：把 LLM 传来的 ID/名称/章节序号/卷序号解析成当前小说的真实实体。
/// 返回 Error 表示未找到或有重名；调用方必须把 Error 当成参数错误，不能把原始字符串写进实体。
/// </summary>
internal static class NovelReferenceResolver
{
    internal sealed record Lookup<T>(T? Value, string? Error = null)
        where T : class
    {
        public bool Found => Value is not null;
    }

    public static async Task<Chapter?> ChapterAsync(
        INovelStore store,
        string novelId,
        string? idOrOrder,
        CancellationToken ct
    ) => (await ChapterLookupAsync(store, novelId, idOrOrder, ct)).Value;

    public static async Task<Lookup<Chapter>> ChapterLookupAsync(
        INovelStore store,
        string novelId,
        string? idOrOrder,
        CancellationToken ct
    ) => FindChapterLookup(await store.GetChapters(novelId, ct), idOrOrder);

    public static async Task<Volume?> VolumeAsync(
        INovelStore store,
        string novelId,
        string? idOrOrder,
        CancellationToken ct
    ) => (await VolumeLookupAsync(store, novelId, idOrOrder, ct)).Value;

    public static async Task<Lookup<Volume>> VolumeLookupAsync(
        INovelStore store,
        string novelId,
        string? idOrOrder,
        CancellationToken ct
    ) => FindVolumeLookup(await store.GetVolumes(novelId, ct), idOrOrder);

    public static Task<Character?> CharacterAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) =>
        FindAsync(() => store.GetCharacters(novelId, ct), idOrName, c => c.Id, c => c.Name, "角色");

    public static Task<Location?> LocationAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) => FindAsync(() => store.GetLocations(novelId, ct), idOrName, l => l.Id, l => l.Name, "地点");

    public static Task<Faction?> FactionAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) => FindAsync(() => store.GetFactions(novelId, ct), idOrName, f => f.Id, f => f.Name, "组织");

    public static Task<Item?> ItemAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) => FindAsync(() => store.GetItems(novelId, ct), idOrName, i => i.Id, i => i.Name, "道具");

    public static Task<Plotline?> PlotlineAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) =>
        FindAsync(
            () => store.GetPlotlines(novelId, ct),
            idOrName,
            p => p.Id,
            p => p.Name,
            "情节线"
        );

    public static Task<SceneDefinition?> SceneAsync(
        INovelStore store,
        string novelId,
        string? idOrTitle,
        CancellationToken ct
    ) =>
        FindAsync(
            () => store.GetScenesByNovel(novelId, ct),
            idOrTitle,
            s => s.Id,
            s => s.Title,
            "场景"
        );

    /// <summary>按实体类型解析任意可寻址实体；用于记录变化、计划 delta、世界规则等跨类型引用。</summary>
    public static async Task<Lookup<Entity>> EntityAsync(
        INovelStore store,
        string novelId,
        EntityType type,
        string? idOrName,
        CancellationToken ct
    )
    {
        switch (type)
        {
            case EntityType.Chapter:
            {
                var result = await ChapterLookupAsync(store, novelId, idOrName, ct);
                return result.Value is { } chapter
                    ? new Lookup<Entity>(chapter)
                    : new Lookup<Entity>(null, result.Error);
            }
            case EntityType.Volume:
            {
                var result = await VolumeLookupAsync(store, novelId, idOrName, ct);
                return result.Value is { } volume
                    ? new Lookup<Entity>(volume)
                    : new Lookup<Entity>(null, result.Error);
            }
            case EntityType.Character:
                return AsEntity(
                    await CharacterAsync(store, novelId, idOrName, ct),
                    "角色",
                    idOrName
                );
            case EntityType.Location:
                return AsEntity(
                    await LocationAsync(store, novelId, idOrName, ct),
                    "地点",
                    idOrName
                );
            case EntityType.Faction:
                return AsEntity(await FactionAsync(store, novelId, idOrName, ct), "组织", idOrName);
            case EntityType.Item:
                return AsEntity(await ItemAsync(store, novelId, idOrName, ct), "道具", idOrName);
            case EntityType.Plotline:
                return AsEntity(
                    await PlotlineAsync(store, novelId, idOrName, ct),
                    "情节线",
                    idOrName
                );
            case EntityType.Scene:
                return AsEntity(await SceneAsync(store, novelId, idOrName, ct), "场景", idOrName);
            case EntityType.Relationship:
            {
                var result = await FindAsync(
                    () => store.GetRelationshipsByNovel(novelId, ct),
                    idOrName,
                    r => r.Id,
                    r => r.Id,
                    "关系"
                );
                return AsEntity(result, "关系", idOrName);
            }
            case EntityType.FactionRelationship:
            {
                var result = await FindAsync(
                    () => store.GetFactionRelationships(novelId, ct),
                    idOrName,
                    r => r.Id,
                    r => r.Id,
                    "组织关系"
                );
                return AsEntity(result, "组织关系", idOrName);
            }
            case EntityType.Novel:
            {
                var result = await FindAsync(
                    () => store.GetAllNovels(ct),
                    idOrName,
                    n => n.Id,
                    n => n.Title,
                    "小说"
                );
                return AsEntity(result, "小说", idOrName);
            }
            case EntityType.TimelineEvent:
            {
                var result = await FindAsync(
                    () => store.GetTimelineEvents(novelId, ct),
                    idOrName,
                    e => e.Id,
                    e => e.Title,
                    "时间线事件"
                );
                return AsEntity(result, "时间线事件", idOrName);
            }
            case EntityType.WorldRule:
            {
                var result = await FindAsync(
                    () => store.GetWorldRules(novelId, ct),
                    idOrName,
                    r => r.Id,
                    r => r.Description,
                    "世界规则"
                );
                return AsEntity(result, "世界规则", idOrName);
            }
            case EntityType.Note:
            {
                var result = await FindAsync(
                    () => store.GetNotes(novelId, ct),
                    idOrName,
                    n => n.Id,
                    n => n.Title,
                    "笔记"
                );
                return AsEntity(result, "笔记", idOrName);
            }
            default:
                return new Lookup<Entity>(
                    null,
                    $"实体类型 {type.ToKebabCaseString()} 不支持按 ID/名称引用"
                );
        }
    }

    /// <summary>解析引用并返回真实 ID；这是写工具落盘前最后一道外键闸门。</summary>
    public static async Task<Lookup<string>> EntityIdAsync(
        INovelStore store,
        string novelId,
        EntityType type,
        string? idOrName,
        CancellationToken ct
    )
    {
        var result = await EntityAsync(store, novelId, type, idOrName, ct);
        return result.Value is { } entity
            ? new Lookup<string>(entity.Id)
            : new Lookup<string>(null, result.Error);
    }

    public static Chapter? FindChapter(IReadOnlyList<Chapter> chapters, string? idOrOrder) =>
        FindChapterLookup(chapters, idOrOrder).Value;

    public static Lookup<Chapter> FindChapterLookup(
        IReadOnlyList<Chapter> chapters,
        string? idOrOrder
    )
    {
        var key = NormalizeText(idOrOrder);
        if (key.Length == 0)
            return new Lookup<Chapter>(null, "章节引用为空");

        var byId = chapters.Where(c => string.Equals(c.Id, key, StringComparison.Ordinal)).ToList();
        if (byId.Count == 1)
            return new Lookup<Chapter>(byId[0]);
        if (byId.Count > 1)
            return new Lookup<Chapter>(null, $"章节 ID '{key}' 重复，数据异常");

        byId = chapters
            .Where(c => string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byId.Count == 1)
            return new Lookup<Chapter>(byId[0]);
        if (byId.Count > 1)
            return new Lookup<Chapter>(null, $"章节 ID '{key}' 重复，数据异常");

        if (TryParseOrder(key, out var order))
        {
            var byOrder = chapters.Where(c => c.Order == order).ToList();
            if (byOrder.Count == 1)
                return new Lookup<Chapter>(byOrder[0]);
            if (byOrder.Count > 1)
                return new Lookup<Chapter>(
                    null,
                    $"第 {order} 章存在 {byOrder.Count} 条记录，请改用章节 ID"
                );
        }

        var byTitle = chapters
            .Where(c =>
                string.Equals(NormalizeText(c.Title), key, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (byTitle.Count == 1)
            return new Lookup<Chapter>(byTitle[0]);
        if (byTitle.Count > 1)
            return new Lookup<Chapter>(
                null,
                $"章节标题 '{key}' 对应 {byTitle.Count} 个条目，请改用章节 ID"
            );

        return new Lookup<Chapter>(null, $"章节 '{key}' 不存在");
    }

    public static Volume? FindVolume(IReadOnlyList<Volume> volumes, string? idOrOrder) =>
        FindVolumeLookup(volumes, idOrOrder).Value;

    public static Lookup<Volume> FindVolumeLookup(IReadOnlyList<Volume> volumes, string? idOrOrder)
    {
        var key = NormalizeText(idOrOrder);
        if (key.Length == 0)
            return new Lookup<Volume>(null, "分卷引用为空");

        var byId = volumes.Where(v => string.Equals(v.Id, key, StringComparison.Ordinal)).ToList();
        if (byId.Count == 1)
            return new Lookup<Volume>(byId[0]);
        if (byId.Count > 1)
            return new Lookup<Volume>(null, $"分卷 ID '{key}' 重复，数据异常");

        byId = volumes
            .Where(v => string.Equals(v.Id, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byId.Count == 1)
            return new Lookup<Volume>(byId[0]);
        if (byId.Count > 1)
            return new Lookup<Volume>(null, $"分卷 ID '{key}' 重复，数据异常");

        if (TryParseOrder(key, out var order))
        {
            var byOrder = volumes.Where(v => v.Order == order).ToList();
            if (byOrder.Count == 1)
                return new Lookup<Volume>(byOrder[0]);
            if (byOrder.Count > 1)
                return new Lookup<Volume>(
                    null,
                    $"第 {order} 卷存在 {byOrder.Count} 条记录，请改用分卷 ID"
                );
        }

        var byName = volumes
            .Where(v =>
                string.Equals(NormalizeText(v.Name), key, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (byName.Count == 1)
            return new Lookup<Volume>(byName[0]);
        if (byName.Count > 1)
            return new Lookup<Volume>(
                null,
                $"分卷名 '{key}' 对应 {byName.Count} 个条目，请改用分卷 ID"
            );

        return new Lookup<Volume>(null, $"分卷 '{key}' 不存在");
    }

    /// <summary>
    /// 通用 ID/名称解析：先精确 ID，再规范化名称（全角半角/首尾空白/大小写）。
    /// 名称有多个匹配时返回 null，调用方应报“重名”而不是猜一个。
    /// </summary>
    public static T? FindByIdOrName<T>(
        IReadOnlyList<T> entities,
        string? idOrName,
        Func<T, string> getId,
        Func<T, string> getName
    )
        where T : class => LookupByIdOrName(entities, idOrName, getId, getName, "条目").Value;

    /// <summary>解析 ID/名称并给出可读错误；用于需要在错误信息里区分“未找到/重名”的写路径。</summary>
    public static Lookup<T> LookupByIdOrName<T>(
        IReadOnlyList<T> entities,
        string? idOrName,
        Func<T, string> getId,
        Func<T, string> getName,
        string label
    )
        where T : class
    {
        var key = NormalizeText(idOrName);
        if (key.Length == 0)
            return new Lookup<T>(null, $"{label}引用为空");

        var byId = entities
            .Where(e => string.Equals(getId(e), key, StringComparison.Ordinal))
            .ToList();
        if (byId.Count == 1)
            return new Lookup<T>(byId[0]);
        if (byId.Count > 1)
            return new Lookup<T>(null, $"{label} ID '{key}' 重复，数据异常");

        byId = entities
            .Where(e => string.Equals(getId(e), key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byId.Count == 1)
            return new Lookup<T>(byId[0]);
        if (byId.Count > 1)
            return new Lookup<T>(null, $"{label} ID '{key}' 重复，数据异常");

        var byName = entities
            .Where(e =>
                string.Equals(NormalizeText(getName(e)), key, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (byName.Count == 1)
            return new Lookup<T>(byName[0]);
        if (byName.Count > 1)
            return new Lookup<T>(
                null,
                $"{label}名称 '{key}' 对应 {byName.Count} 个条目，请改用 ID"
                    + $"（{string.Join("、", byName.Take(5).Select(getId))}）"
            );

        return new Lookup<T>(null, $"{label} '{key}' 不存在");
    }

    /// <summary>章节 JSONL/工具参数常传“第12章”“12”“ch12”或中文数字；统一解析为正序号。</summary>
    public static bool TryParseOrder(string? raw, out int order)
    {
        order = 0;
        var text = NormalizeText(raw).ToLowerInvariant().Replace(" ", "").Replace("\t", "");
        if (text.Length == 0)
            return false;

        if (text.StartsWith('第'))
            text = text[1..];
        if (text.StartsWith('#'))
            text = text[1..];

        foreach (var prefix in new[] { "chapter", "ch.", "ch", "volume", "vol.", "vol" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        foreach (var suffix in new[] { "章", "卷", "节", "回", "场" })
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                text = text[..^suffix.Length];
                break;
            }
        }

        if (text.Length == 0)
            return false;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out order))
            return order > 0;
        return TryParseChineseInteger(text, out order) && order > 0;
    }

    public static string NormalizeText(string? value) =>
        (value ?? "").Trim().Normalize(NormalizationForm.FormKC);

    private static async Task<T?> FindAsync<T>(
        Func<Task<List<T>>> load,
        string? key,
        Func<T, string> getId,
        Func<T, string> getName,
        string label
    )
        where T : class
    {
        var result = LookupByIdOrName(await load(), key, getId, getName, label);
        return result.Value;
    }

    private static Lookup<Entity> AsEntity<T>(T? value, string label, string? key)
        where T : Entity =>
        value is not null
            ? new Lookup<Entity>(value)
            : new Lookup<Entity>(null, $"{label} '{NormalizeText(key)}' 不存在");

    private static bool TryParseChineseInteger(string text, out int value)
    {
        value = 0;
        long total = 0;
        long section = 0;
        long number = 0;
        var hasDigit = false;

        foreach (var ch in text)
        {
            if (TryChineseDigit(ch, out var digit))
            {
                number = digit;
                hasDigit = true;
                continue;
            }
            if (!TryChineseUnit(ch, out var unit))
                return false;
            hasDigit = true;
            if (unit == 10_000)
            {
                section += number;
                total += section * unit;
                section = 0;
                number = 0;
                continue;
            }
            if (number == 0)
                number = 1;
            section += number * unit;
            number = 0;
        }

        if (!hasDigit)
            return false;
        total += section + number;
        if (total <= 0 || total > int.MaxValue)
            return false;
        value = (int)total;
        return true;
    }

    private static bool TryChineseDigit(char ch, out int value)
    {
        value = ch switch
        {
            '零' or '〇' or '○' => 0,
            '一' => 1,
            '二' or '两' => 2,
            '三' => 3,
            '四' => 4,
            '五' => 5,
            '六' => 6,
            '七' => 7,
            '八' => 8,
            '九' => 9,
            _ => -1,
        };
        return value >= 0;
    }

    private static bool TryChineseUnit(char ch, out int value)
    {
        value = ch switch
        {
            '十' => 10,
            '百' => 100,
            '千' => 1000,
            '万' => 10_000,
            _ => 0,
        };
        return value > 0;
    }
}

/// <summary>
/// 工具参数枚举解析：只接受已定义的枚举名（kebab / snake 写法），拒绝数字和未定义值。
/// </summary>
internal static class NovelEnum
{
    private static readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, string>> Aliases =
        new Dictionary<Type, IReadOnlyDictionary<string, string>>
        {
            [typeof(CharacterRole)] = new Dictionary<string, string>
            {
                ["主角"] = "Protagonist",
                ["配角"] = "Supporting",
                ["龙套"] = "Minor",
                ["反派"] = "Antagonist",
            },
            [typeof(FactionType)] = new Dictionary<string, string>
            {
                ["门派"] = "Sect",
                ["公司"] = "Company",
                ["家族"] = "Family",
                ["帮会"] = "Gang",
                ["政府"] = "Government",
                ["其他"] = "Other",
            },
            [typeof(LocationType)] = new Dictionary<string, string>
            {
                ["城市"] = "City",
                ["乡村"] = "Rural",
                ["室内"] = "Indoor",
                ["荒野"] = "Wilderness",
                ["虚构"] = "Fictional",
                ["其他"] = "Other",
            },
            [typeof(ItemType)] = new Dictionary<string, string>
            {
                ["武器"] = "Weapon",
                ["法器"] = "Artifact",
                ["信物"] = "Token",
                ["消耗品"] = "Consumable",
                ["其他"] = "Other",
            },
            [typeof(PlotlineType)] = new Dictionary<string, string>
            {
                ["主线"] = "Main",
                ["支线"] = "Side",
                ["隐藏"] = "Hidden",
                ["伏笔"] = "Foreshadowing",
            },
            [typeof(PlotlineTension)] = new Dictionary<string, string>
            {
                ["低"] = "Low",
                ["中"] = "Medium",
                ["高"] = "High",
                ["临界"] = "Critical",
            },
            [typeof(RelationshipStrength)] = new Dictionary<string, string>
            {
                ["弱"] = "Weak",
                ["中"] = "Medium",
                ["强"] = "Strong",
                ["极强"] = "Extreme",
            },
            [typeof(FactionRelationType)] = new Dictionary<string, string>
            {
                ["同盟"] = "Ally",
                ["盟友"] = "Ally",
                ["敌对"] = "Enemy",
                ["中立"] = "Neutral",
                ["贸易"] = "Trade",
                ["附属"] = "Vassal",
            },
            [typeof(SceneType)] = new Dictionary<string, string>
            {
                ["动作"] = "Action",
                ["对话"] = "Dialogue",
                ["反思"] = "Reflection",
                ["过渡"] = "Transition",
            },
        };

    public static bool TryParse<T>(string? value, out T result)
        where T : struct, Enum
    {
        result = default;
        var raw = value?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (
            Aliases.TryGetValue(typeof(T), out var aliases)
            && aliases.TryGetValue(raw, out var alias)
        )
            raw = alias;
        if (!TryParse(typeof(T), raw, out var parsed) || parsed is not T typed)
            return false;
        result = typed;
        return true;
    }

    public static bool TryParse(Type enumType, string? value, out object? result)
    {
        result = null;
        var raw = value?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || !enumType.IsEnum)
            return false;
        var normalized = raw.Replace("-", "").Replace("_", "");
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return false;
        if (!Enum.TryParse(enumType, normalized, ignoreCase: true, out var parsed))
            return false;
        if (parsed is null || !Enum.IsDefined(enumType, parsed))
            return false;
        result = parsed;
        return true;
    }
}
