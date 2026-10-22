using Microsoft.Extensions.Logging;

namespace Core.Data;

/// <summary>
/// 外部插件的只读数据接口 — 只暴露安全的子集，防止数据泄露。
/// 外部插件不应加载全部子实体（Chapters/Characters/...），只提供摘要和按需查询。
/// </summary>
public interface IPluginStore
{
    /// <summary>获取小说摘要（仅 Id/Title/Genre，不含导航属性）。</summary>
    Task<NovelSummary?> GetNovelSummary(string id, CancellationToken ct = default);

    /// <summary>获取所有小说的摘要列表（仅 Id/Title/Genre）。</summary>
    Task<List<NovelSummary>> GetAllNovelSummaries(CancellationToken ct = default);

    Task<Chapter?> GetChapter(string novelId, string id, CancellationToken ct = default);
    Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default);
    Task<Character?> GetCharacter(string novelId, string id, CancellationToken ct = default);
    Task<List<Character>> GetCharacters(string novelId, CancellationToken ct = default);
    Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default);
    Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default);
    Task<List<FactionRelationship>> GetFactionRelationships(
        string novelId,
        CancellationToken ct = default
    );
    Task<Location?> GetLocation(string novelId, string id, CancellationToken ct = default);
    Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default);
    Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default);
    Task<List<Item>> GetItems(string novelId, CancellationToken ct = default);
    Task<List<TimelineEvent>> GetTimelineEvents(string novelId, CancellationToken ct = default);
    Task<List<Relationship>> GetRelationshipsByNovel(
        string novelId,
        CancellationToken ct = default
    );
    Task<List<SceneDefinition>> GetScenesByNovel(string novelId, CancellationToken ct = default);
}

/// <summary>
/// 小说摘要 DTO — 仅包含安全子集字段，不含导航属性。
/// 用于外部插件场景，防止通过 Chapters/Characters 等导航属性泄露全量数据。
/// </summary>
public sealed record NovelSummary
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Genre { get; init; } = "";
}

/// <summary>
/// 外部插件的只读数据代理 — 只暴露安全的子集，防止数据泄露。
/// 实现 IPluginStore 接口，确保外部插件只能通过接口访问受限数据。
/// </summary>
public sealed class PluginStore(INovelStore inner, ILogger? logger = null) : IPluginStore
{
    /// <summary>
    /// 获取小说摘要（仅 Id/Title/Genre，不含导航属性）。
    /// </summary>
    public Task<NovelSummary?> GetNovelSummary(string id, CancellationToken ct = default)
    {
        LogRead(nameof(GetNovelSummary), id);
        return inner.GetNovelSummary(id, ct) is { } task
            ? task.ContinueWith(t =>
                t.Result is null
                    ? null
                    : new NovelSummary
                    {
                        Id = t.Result.Id,
                        Title = t.Result.Title,
                        Genre = t.Result.Genre,
                    }
            )
            : Task.FromResult<NovelSummary?>(null);
    }

    /// <summary>
    /// 获取所有小说的摘要列表（仅 Id/Title/Genre，不含导航属性）。
    /// </summary>
    public async Task<List<NovelSummary>> GetAllNovelSummaries(CancellationToken ct = default)
    {
        LogRead(nameof(GetAllNovelSummaries));
        var novels = await inner.GetAllNovels(ct);
        return novels.ConvertAll(n => new NovelSummary
        {
            Id = n.Id,
            Title = n.Title,
            Genre = n.Genre,
        });
    }

    public Task<Chapter?> GetChapter(string novelId, string id, CancellationToken ct = default)
    {
        LogRead(nameof(GetChapter), novelId);
        return inner.GetChapter(novelId, id, ct);
    }

    public Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default)
    {
        LogRead(nameof(GetChapters), novelId);
        return inner.GetChapters(novelId, ct);
    }

    public Task<Character?> GetCharacter(string novelId, string id, CancellationToken ct = default)
    {
        LogRead(nameof(GetCharacter), novelId);
        return inner.GetCharacter(novelId, id, ct);
    }

    public Task<List<Character>> GetCharacters(string novelId, CancellationToken ct = default)
    {
        LogRead(nameof(GetCharacters), novelId);
        return inner.GetCharacters(novelId, ct);
    }

    public Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default)
    {
        LogRead(nameof(GetPlotlines), novelId);
        return inner.GetPlotlines(novelId, ct);
    }

    public Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default)
    {
        LogRead(nameof(GetFactions), novelId);
        return inner.GetFactions(novelId, ct);
    }

    public Task<List<FactionRelationship>> GetFactionRelationships(
        string novelId,
        CancellationToken ct = default
    )
    {
        LogRead(nameof(GetFactionRelationships), novelId);
        return inner.GetFactionRelationships(novelId, ct);
    }

    public Task<Location?> GetLocation(string novelId, string id, CancellationToken ct = default)
    {
        LogRead(nameof(GetLocation), novelId);
        return inner.GetLocation(novelId, id, ct);
    }

    public Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default)
    {
        LogRead(nameof(GetLocations), novelId);
        return inner.GetLocations(novelId, ct);
    }

    public Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default)
    {
        LogRead(nameof(GetItem), novelId);
        return inner.GetItem(novelId, id, ct);
    }

    public Task<List<Item>> GetItems(string novelId, CancellationToken ct = default)
    {
        LogRead(nameof(GetItems), novelId);
        return inner.GetItems(novelId, ct);
    }

    public Task<List<TimelineEvent>> GetTimelineEvents(
        string novelId,
        CancellationToken ct = default
    )
    {
        LogRead(nameof(GetTimelineEvents), novelId);
        return inner.GetTimelineEvents(novelId, ct);
    }

    public Task<List<Relationship>> GetRelationshipsByNovel(
        string novelId,
        CancellationToken ct = default
    )
    {
        LogRead(nameof(GetRelationshipsByNovel), novelId);
        return inner.GetRelationshipsByNovel(novelId, ct);
    }

    public Task<List<SceneDefinition>> GetScenesByNovel(
        string novelId,
        CancellationToken ct = default
    )
    {
        LogRead(nameof(GetScenesByNovel), novelId);
        return inner.GetScenesByNovel(novelId, ct);
    }

    /// <summary>
    /// 审计日志：记录外部插件的数据读取操作。
    /// </summary>
    private void LogRead(string method, string? novelId = null)
    {
        logger?.LogDebug(
            "PluginStore read: {Method} (NovelId={NovelId})",
            method,
            novelId ?? "N/A"
        );
    }
}
