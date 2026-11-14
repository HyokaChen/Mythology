using Core.Data;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 写入工具共用的引用解析：把 LLM 传来的 ID/名称/章节序号解析成当前小说的真实实体。
/// 返回 null 表示未找到；调用方必须把 null 当成参数错误，不能把原始字符串写进实体。
/// </summary>
internal static class NovelReferenceResolver
{
    public static Task<Chapter?> ChapterAsync(
        INovelStore store,
        string novelId,
        string? idOrOrder,
        CancellationToken ct
    ) =>
        FindAsync(
            () => store.GetChapters(novelId, ct),
            idOrOrder,
            c => c.Id,
            c => c.Order.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );

    public static Task<Character?> CharacterAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) => FindAsync(() => store.GetCharacters(novelId, ct), idOrName, c => c.Id, c => c.Name);

    public static Task<Location?> LocationAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) => FindAsync(() => store.GetLocations(novelId, ct), idOrName, l => l.Id, l => l.Name);

    public static Task<Faction?> FactionAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) => FindAsync(() => store.GetFactions(novelId, ct), idOrName, f => f.Id, f => f.Name);

    public static Task<Item?> ItemAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) => FindAsync(() => store.GetItems(novelId, ct), idOrName, i => i.Id, i => i.Name);

    public static Task<Plotline?> PlotlineAsync(
        INovelStore store,
        string novelId,
        string? idOrName,
        CancellationToken ct
    ) => FindAsync(() => store.GetPlotlines(novelId, ct), idOrName, p => p.Id, p => p.Name);

    public static Task<SceneDefinition?> SceneAsync(
        INovelStore store,
        string novelId,
        string? id,
        CancellationToken ct
    ) => FindAsync(() => store.GetScenesByNovel(novelId, ct), id, s => s.Id, s => s.Title);

    private static async Task<T?> FindAsync<T>(
        Func<Task<List<T>>> load,
        string? key,
        Func<T, string> getId,
        Func<T, string> getAlias
    )
        where T : class
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var items = await load();
        return items.FirstOrDefault(e => getId(e) == key)
            ?? items.FirstOrDefault(e => getAlias(e) == key);
    }
}
