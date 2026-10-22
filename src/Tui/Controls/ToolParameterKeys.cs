namespace Tui.Controls;

/// <summary>
/// 工具参数键定义 — 定义参数显示优先级和跳过规则，
/// 用于 ToolCallRenderer 解析工具调用参数时的显示逻辑。
/// </summary>
public static class ToolParameterKeys
{
    /// <summary>参数显示优先级：第一个匹配的 key 作为副标题显示。</summary>
    public static readonly string[] SubtitlePriority =
    [
        "path",
        "filePath",
        "file_path",
        "url",
        "query",
        "name",
        "description",
        "pattern",
        "chapter-title",
        "title",
        "character_name",
        "command",
        "chapterId",
        "chapter-id",
        "characterId",
        "character-id",
        "factionId",
        "faction-id",
        "locationId",
        "location-id",
        "itemId",
        "item-id",
        "plotlineId",
        "plotline-id",
    ];

    /// <summary>在 inline 参数显示中永远跳过的 key。</summary>
    public static readonly HashSet<string> Skip =
    [
        .. SubtitlePriority,
        "novelId",
        "novel-id",
        "sessionId",
        "session_id",
    ];
}
