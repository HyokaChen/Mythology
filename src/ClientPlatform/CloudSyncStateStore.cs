using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Shared;

namespace ClientPlatform;

/// <summary>单个文档的同步基线：上次与云端达成一致的 revision、内容 sha 与落盘路径。</summary>
public sealed record CloudSyncDocState(int Revision, string Sha256, string Path);

/// <summary>单本小说的同步状态文件内容（~/.mythology/novel-sync/{novelId}.json）。</summary>
public sealed record CloudSyncNovelState(
    int Version,
    Dictionary<string, CloudSyncDocState> Documents
)
{
    public static CloudSyncNovelState Empty { get; } = new(1, []);
}

/// <summary>
/// 同步状态存储（客户端三路比较的基线）：损坏/缺失 → 空状态不抛（按未同步处理，
/// 由合并规则的保守分支兜底）。读写均经 AtomicFile 原子替换。
/// </summary>
public sealed class CloudSyncStateStore(string mythologyDir)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string PathFor(string novelId) =>
        Path.Combine(mythologyDir, "novel-sync", $"{Sanitize(novelId)}.json");

    public async Task<CloudSyncNovelState> LoadAsync(string novelId, CancellationToken ct = default)
    {
        var path = PathFor(novelId);
        try
        {
            if (!File.Exists(path))
            {
                return CloudSyncNovelState.Empty;
            }
            var state = JsonSerializer.Deserialize<CloudSyncNovelState>(
                await File.ReadAllTextAsync(path, ct),
                Json
            );
            return state ?? CloudSyncNovelState.Empty;
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return CloudSyncNovelState.Empty;
        }
    }

    public async Task SaveAsync(
        string novelId,
        CloudSyncNovelState state,
        CancellationToken ct = default
    )
    {
        var path = PathFor(novelId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(state, Json), ct);
    }

    /// <summary>novelId 为 GUID，但防御性消毒（防路径穿越）。</summary>
    private static string Sanitize(string novelId) =>
        string.Join("", novelId.Split(Path.GetInvalidFileNameChars()));
}
