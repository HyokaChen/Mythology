using System.Text.Json;
using System.Text.RegularExpressions;
using Core.Agent;
using Core.Data;
using Core.Novels;

namespace Core.Tools;

/// <summary>
/// 写入类工具的确定性范围校验。纯函数，方便测试；Hook 只负责取结构和接线。
/// 目标：摘要写错也不能改 Scope 外的章节。
/// </summary>
internal static class TaskScopeGuard
{
    private static readonly HashSet<string> GuardedTools = new(StringComparer.Ordinal)
    {
        "write_chapter",
        "edit_text",
        "rewrite_selection",
        "create_outline",
    };

    public static bool IsGuarded(string toolName) => GuardedTools.Contains(toolName);

    /// <summary>返回 null = 允许；否则返回给模型的阻止原因。</summary>
    public static string? Evaluate(
        TaskScopeState? scope,
        string toolName,
        string arguments,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<Volume> volumes,
        string? turnTargetChapterId
    )
    {
        if (scope is null || !scope.IsActive || !IsGuarded(toolName))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        }
        catch (JsonException)
        {
            return "工具参数不是合法 JSON，已阻止写入。";
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "工具参数不是对象，已阻止写入。";

            var chapterRef = GetString(root, "chapter-id", "chapterId");
            var volumeRef = GetString(root, "volume-id", "volumeId");
            var title = GetString(root, "title");
            var order = GetInt(root, "order");

            if (toolName == "create_outline")
            {
                if (string.IsNullOrWhiteSpace(volumeRef))
                    return "新建章节必须提供 volume-id，无法校验任务范围，已阻止。";
                var volume = ResolveVolume(volumeRef, volumes);
                if (volume is null)
                    return $"无法解析目标分卷 '{volumeRef}'，已阻止写入。";
                return scope.AllowsVolume(volume.Id)
                    ? null
                    : Blocked(scope, $"第{volume.Order}卷《{volume.Name}》");
            }

            Chapter? target = null;
            if (!string.IsNullOrWhiteSpace(chapterRef))
            {
                var lookup = ChapterLocator.Resolve(chapters, chapterRef);
                target = lookup.Chapter ?? ResolveChapterOrderRef(chapters, chapterRef);
                if (target is null && !(order > 0 && !string.IsNullOrWhiteSpace(volumeRef)))
                {
                    return $"无法解析目标章节 '{chapterRef}'：{lookup.Message ?? "未找到"}；已阻止写入。";
                }
            }
            else if (order is > 0)
            {
                // write_chapter 允许用 order+title 命中已有章节（不传 chapter-id）。
                target = chapters.FirstOrDefault(c =>
                    c.Order == order.Value
                    && (
                        string.IsNullOrWhiteSpace(title)
                        || string.Equals(c.Title, title.Trim(), StringComparison.Ordinal)
                    )
                );
            }

            if (target is not null)
            {
                if (
                    !string.IsNullOrWhiteSpace(turnTargetChapterId)
                    && string.Equals(target.Id, turnTargetChapterId, StringComparison.Ordinal)
                )
                    return null;

                if (!scope.AllowsChapter(target))
                {
                    return Blocked(
                        scope,
                        $"第{target.Order}章《{target.Title}》"
                            + $"（卷 ID:{target.VolumeId ?? "未分卷"}）"
                    );
                }

                // 显式传 volume-id = 移动章节；目标章在范围内，但移动到范围外也要拦。
                if (!string.IsNullOrWhiteSpace(volumeRef))
                {
                    var moveVolume = ResolveVolume(volumeRef, volumes);
                    if (moveVolume is null)
                        return $"无法解析目标分卷 '{volumeRef}'，已阻止写入。";
                    if (!scope.AllowsVolume(moveVolume.Id))
                        return Blocked(scope, $"第{moveVolume.Order}卷《{moveVolume.Name}》");
                }

                return null;
            }

            if (order is not > 0)
                return "新建章节必须提供有效 order，无法校验任务范围，已阻止写入。";
            if (string.IsNullOrWhiteSpace(volumeRef))
                return "新建章节必须提供 volume-id，无法校验任务范围，已阻止写入。";

            var newVolume = ResolveVolume(volumeRef, volumes);
            if (newVolume is null)
                return $"无法解析目标分卷 '{volumeRef}'，已阻止写入。";
            return scope.AllowsNewChapter(newVolume.Id, order.Value)
                ? null
                : Blocked(scope, $"第{order}章（第{newVolume.Order}卷《{newVolume.Name}》）");
        }
    }

    private static string Blocked(TaskScopeState scope, string target) =>
        $"工具执行被任务边界阻止：{scope.Describe()}；目标 {target} 不在范围内。"
        + "若要切换任务，请作者明确说“改为写第X章/第X卷”。";

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (
                value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            )
                return value.ToString();
        }
        return null;
    }

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static Chapter? ResolveChapterOrderRef(
        IReadOnlyList<Chapter> chapters,
        string reference
    )
    {
        var match = Regex.Match(reference, @"第?\s*(?<n>\d+)\s*章");
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, out var order))
            return null;
        var candidates = chapters.Where(c => c.Order == order).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static Volume? ResolveVolume(string reference, IReadOnlyList<Volume> volumes)
    {
        var text = reference.Trim();
        if (text.Length == 0)
            return null;
        return volumes.FirstOrDefault(v => string.Equals(v.Id, text, StringComparison.Ordinal))
            ?? (
                int.TryParse(text, out var order)
                    ? volumes.FirstOrDefault(v => v.Order == order)
                    : null
            )
            ?? volumes.FirstOrDefault(v =>
                string.Equals(v.Name.Trim(), text, StringComparison.OrdinalIgnoreCase)
            );
    }
}
