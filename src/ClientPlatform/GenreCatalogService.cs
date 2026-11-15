using System.Collections;
using System.Globalization;
using System.Text;
using Core.Data;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>
/// 题材目录服务 — 云端目录（GET /v1/fiction/genres）→ 本地 YAML 缓存 → 内置 GenreDefinitions。
///
/// 读取顺序刻意分成两步，窗口可以先显示缓存/内置目录（零等待），再在后台换成云端目录：
///   LoadAsync()    = 缓存优先（无缓存 → 内置），绝不发网络请求；
///   RefreshAsync() = 拉云端并原子写 ~/.mythology/genre-catalog.yaml；失败保留当前目录。
///
/// 缓存文件是客户端唯一可编辑的题材目录副本；novel.yaml 只存用户选中的
/// genre（二级）与 genre-parent（一级）。云端目录更新时下一次刷新会整体替换缓存，
/// 不把旧分类合并回来（避免已删除分类永远留在下拉里）。
/// </summary>
public sealed class GenreCatalogService
{
    private readonly GenreCatalogRestClient? _client;
    private readonly string _cachePath;
    private readonly ILogger<GenreCatalogService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GenreCatalog? _current;

    public GenreCatalogService(
        GenreCatalogRestClient? client,
        string? cachePath = null,
        ILogger<GenreCatalogService>? logger = null
    )
    {
        _client = client;
        _cachePath = cachePath ?? DefaultCachePath();
        _logger = logger;
    }

    public string CachePath => _cachePath;

    /// <summary>缓存优先读取；永不抛异常（失败回退内置目录）。</summary>
    public async Task<GenreCatalog> LoadAsync(CancellationToken ct = default)
    {
        if (_current is not null)
            return _current;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_current is not null)
                return _current;

            _current = await ReadCacheAsync(ct).ConfigureAwait(false) ?? GenreCatalog.Local;
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 拉取云端目录并写缓存；失败时保留当前（缓存/内置）目录。
    /// 没有配置云端客户端时直接返回当前目录（离线可用）。
    /// </summary>
    public async Task<GenreCatalog> RefreshAsync(CancellationToken ct = default)
    {
        if (_client is null)
            return await LoadAsync(ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fallback = _current
                ?? await ReadCacheAsync(ct).ConfigureAwait(false)
                ?? GenreCatalog.Local;

            try
            {
                var response = await _client.GetCatalogAsync(ct).ConfigureAwait(false);
                var catalog = FromResponse(response);
                if (catalog.Categories.Count == 0)
                    return fallback;

                await WriteCacheAsync(catalog, ct).ConfigureAwait(false);
                _current = catalog;
                return catalog;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "题材目录云端刷新失败，保留本地缓存/内置目录");
                _current = fallback;
                return fallback;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 缓存优先，然后尽力刷新一次；界面可在 Refresh 返回后再次 SetCatalog。
    /// 供后台任务使用，调用方不需要自己处理异常。
    /// </summary>
    public async Task<GenreCatalog> LoadAndRefreshAsync(CancellationToken ct = default)
    {
        var cached = await LoadAsync(ct).ConfigureAwait(false);
        var refreshed = await RefreshAsync(ct).ConfigureAwait(false);
        return refreshed.Categories.Count > 0 ? refreshed : cached;
    }

    private async Task<GenreCatalog?> ReadCacheAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var text = await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false);
            var dict = YamlHelper.Deserialize(text);
            var categories = ParseCategories(dict.GetValueOrDefault("categories"));
            if (categories.Count == 0)
                return null;

            return new GenreCatalog
            {
                Version = dict.GetValueOrDefault("version")?.ToString() is { Length: > 0 } version
                    ? version
                    : "cache",
                Source = "cache",
                Categories = categories,
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取题材目录缓存失败：{Path}", _cachePath);
            return null;
        }
    }

    private async Task WriteCacheAsync(GenreCatalog catalog, CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var dict = new Dictionary<string, object?>
            {
                ["version"] = catalog.Version,
                ["source"] = catalog.Source,
                ["saved-at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["categories"] = catalog
                    .Categories.Select(category => new Dictionary<string, object?>
                    {
                        ["name"] = category.Name,
                        ["subcategories"] = category.Subcategories.ToList(),
                    })
                    .ToList(),
            };

            // 原子替换：先写同目录临时文件，再 rename；崩溃时不会留下半截 YAML。
            var tempPath = _cachePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, YamlHelper.Serialize(dict), Encoding.UTF8, ct)
                .ConfigureAwait(false);
            File.Move(tempPath, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 缓存写失败不影响本次目录使用；下次启动重新拉云端即可。
            _logger?.LogWarning(ex, "写入题材目录缓存失败：{Path}", _cachePath);
        }
    }

    private static GenreCatalog FromResponse(GenreCatalogResponse response) =>
        new()
        {
            Version = string.IsNullOrWhiteSpace(response.Version) ? "cloud" : response.Version,
            Source = "cloud",
            Categories =
            [
                .. response
                    .Categories.Where(category => !string.IsNullOrWhiteSpace(category.Name))
                    .Select(category => new GenreCategory(
                        category.Name.Trim(),
                        [
                            .. (category.Subcategories ?? [])
                                .Select(subcategory => subcategory.Name?.Trim())
                                .Where(name => !string.IsNullOrWhiteSpace(name))
                                .Select(name => name!),
                        ]
                    ))
                    .Where(category => category.Subcategories.Count > 0),
            ],
        };

    private static List<GenreCategory> ParseCategories(object? raw)
    {
        var result = new List<GenreCategory>();
        if (raw is not IEnumerable items || raw is string)
            return result;

        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> map)
                continue;

            var name = map.GetValueOrDefault("name")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var subcategories = new List<string>();
            if (
                map.GetValueOrDefault("subcategories") is IEnumerable subItems
                && subItems is not string
            )
            {
                foreach (var subItem in subItems)
                {
                    var subName = subItem?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(subName))
                        subcategories.Add(subName!);
                }
            }

            if (subcategories.Count > 0)
                result.Add(new GenreCategory(name!, subcategories));
        }

        return result;
    }

    private static string DefaultCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mythology",
            "genre-catalog.yaml"
        );
}
