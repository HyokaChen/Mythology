using Core.Data;

namespace Core.Vector;

/// <summary>
/// 从 INovelStore 收集 jieba 用户词典的名称来源。
/// 覆盖设定面板的实体集合：角色、组织、地点、情节线、场景、道具、关系端点，
/// 以及大纲里的书名/卷名/章名。只取“名字/标题”，不取描述/性格等自由文本。
/// </summary>
public static class NovelLexiconCollector
{
    public static async Task<HashSet<string>> CollectAsync(
        INovelStore store,
        string novelId,
        CancellationToken ct = default
    )
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        var novel = await store.GetNovel(novelId, ct);
        if (novel is not null)
            names.Add(novel.Title);

        foreach (var chapter in await store.GetChapters(novelId, ct))
            names.Add(chapter.Title);
        foreach (var volume in await store.GetVolumes(novelId, ct))
            names.Add(volume.Name);

        foreach (var character in await store.GetCharacters(novelId, ct))
            names.Add(character.Name);
        foreach (var faction in await store.GetFactions(novelId, ct))
            names.Add(faction.Name);
        foreach (var location in await store.GetLocations(novelId, ct))
            names.Add(location.Name);
        foreach (var item in await store.GetItems(novelId, ct))
            names.Add(item.Name);
        foreach (var plotline in await store.GetPlotlines(novelId, ct))
            names.Add(plotline.Name);
        foreach (var scene in await store.GetScenesByNovel(novelId, ct))
            names.Add(scene.Title);

        return names;
    }
}
