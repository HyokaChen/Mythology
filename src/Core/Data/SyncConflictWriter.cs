using Core.Shared;

namespace Core.Data;

/// <summary>
/// 云端冲突副本写入器（cloud-novel-sync v3）：conflict 时把云端现行版本另存为
/// `~/.mythology/cloud-conflicts/{novelId}/{原相对路径}.conflict-{时间戳}`，
/// 保留原相对路径的子目录结构；本地稿永不静默丢失，由作者手动合并。
/// 同 novelId + 同路径 + 同内容 sha 的副本已存在 → 跳过（避免重复刷屏）。
/// </summary>
public static class SyncConflictWriter
{
    public static async Task<string> WriteAsync(
        string mythologyDir,
        string novelId,
        string relativePath,
        string remoteContent,
        string remoteEncoding,
        CancellationToken ct = default
    )
    {
        var safeRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var conflictSuffix = $".conflict-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        // Windows MAX_PATH 防线：文件名主体超长时截断（保留 .conflict- 时间戳后缀）
        var body = safeRelative;
        var suffixIndex = body.LastIndexOf(Path.DirectorySeparatorChar);
        var directoryPart = suffixIndex >= 0 ? body[..suffixIndex] : "";
        var namePart = suffixIndex >= 0 ? body[(suffixIndex + 1)..] : body;
        if (namePart.Length + conflictSuffix.Length > 200)
        {
            namePart = namePart[..(200 - conflictSuffix.Length)];
        }
        var fileName = string.IsNullOrEmpty(directoryPart)
            ? $"{namePart}{conflictSuffix}"
            : Path.Combine(directoryPart, $"{namePart}{conflictSuffix}");
        var targetPath = Path.Combine(mythologyDir, "cloud-conflicts", novelId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (remoteEncoding == "base64")
        {
            await File.WriteAllBytesAsync(targetPath, Convert.FromBase64String(remoteContent), ct);
        }
        else
        {
            await AtomicFile.WriteAllTextAsync(targetPath, remoteContent, ct);
        }
        return targetPath;
    }
}
