using Core.Data;

namespace Core.Vector;

/// <summary>
/// 从 INovelStore 收集 jieba 用户词典的候选名：**只收实体名**（角色/组织/地点/道具/情节线/场景）。
///
/// 书名/卷名/章名刻意不收（实测依据见 docs/lexical-retokenize-plan.md §1.2(b5)）：
/// 标题是描述性短语，注册成整词会掉召回（「转学生」9/9 → 8/9），且标题本身已有独立文档
/// （ChapterSummary / NovelProfile）承担“标题命中”的排序优势，部分查询靠词项匹配即可。
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
