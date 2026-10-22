using System.Collections.Concurrent;

namespace Plugins.WritingTools;

/// <summary>
/// 增强快照 — 保存改写前的章节内容，支持撤回。
/// 线程安全，使用 ConcurrentDictionary。
/// </summary>
public sealed class EnhancementSnapshot
{
    private readonly ConcurrentDictionary<(string novelId, string chapterId), string> _snapshots =
        new();

    /// <summary>
    /// 保存章节快照（改写前内容）。
    /// 如果已有快照则不覆盖（保留最早的版本）。
    /// </summary>
    public void Save(string novelId, string chapterId, string originalContent)
    {
        _snapshots.TryAdd((novelId, chapterId), originalContent);
    }

    /// <summary>
    /// 撤回到快照版本。
    /// </summary>
    /// <returns>快照内容，null 表示无快照</returns>
    public string? Rollback(string novelId, string chapterId)
    {
        if (_snapshots.TryRemove((novelId, chapterId), out var content))
            return content;
        return null;
    }
}
