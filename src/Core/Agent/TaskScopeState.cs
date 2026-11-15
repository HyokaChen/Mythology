using Core.Data;

namespace Core.Agent;

/// <summary>
/// 会话级任务边界（可持久化）。与 compact 摘要解耦：摘要可以丢、模型可以换，
/// 但作者任务范围必须由宿主保存并在每次写入工具执行前校验。
/// </summary>
public sealed class TaskScopeState
{
    /// <summary>write | revise。</summary>
    public string Mode { get; init; } = "write";

    /// <summary>给作者/错误提示看的人话范围，如“第2卷《夏夜的萤火》（第21-40章）”。</summary>
    public string Label { get; init; } = "";

    /// <summary>允许的卷 ID；空 = 不限卷（由 ChapterOrders 决定）。</summary>
    public List<string> VolumeIds { get; init; } = [];

    /// <summary>允许的章节序号；空 = 不限章（由 VolumeIds 决定）。</summary>
    public List<int> ChapterOrders { get; init; } = [];

    /// <summary>作者原始指令（逐字，供摘要顶部保留）。</summary>
    public string Instruction { get; init; } = "";

    /// <summary>产生该范围的作者消息 entryId（可选，用于追溯）。</summary>
    public string? SourceEntryId { get; init; }

    /// <summary>active | completed | cancelled。</summary>
    public string Status { get; init; } = "active";

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
    public bool IsRevise => string.Equals(Mode, "revise", StringComparison.OrdinalIgnoreCase);

    public bool AllowsVolume(string? volumeId) =>
        VolumeIds.Count == 0
        || (
            !string.IsNullOrEmpty(volumeId) && VolumeIds.Contains(volumeId, StringComparer.Ordinal)
        );

    public bool AllowsOrder(int order) => ChapterOrders.Count == 0 || ChapterOrders.Contains(order);

    public bool AllowsChapter(Chapter chapter) =>
        IsActive && AllowsVolume(chapter.VolumeId) && AllowsOrder(chapter.Order);

    public bool AllowsNewChapter(string? volumeId, int order) =>
        IsActive && AllowsVolume(volumeId) && AllowsOrder(order);

    public string Describe() =>
        string.IsNullOrWhiteSpace(Label) ? "当前任务范围" : $"当前任务范围是 {Label}";
}
