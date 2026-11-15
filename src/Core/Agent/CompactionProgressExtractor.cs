using System.Globalization;
using System.Text.Json;
using Core.Data;
using Core.Novels;
using LlmTornado.Code;
using ChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Agent;

/// <summary>
/// 从压缩区成功的工具调用提取任务进度。只认工具事实，不认助手叙述；
/// 范围外写入单独记录为 deviation，绝不改变 Scope 或当前任务焦点。
/// </summary>
internal static class CompactionProgressExtractor
{
    private static readonly HashSet<string> MutatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit_text",
        "write_chapter",
        "rewrite_selection",
    };

    private static readonly HashSet<string> InspectingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_chapter",
        "diagnose_writing",
    };

    public static CompactionTaskProgress? Extract(
        IReadOnlyList<ChatMessage> messages,
        NovelStructure structure,
        CompactionTaskScope? scope,
        CompactionTaskProgress? previous
    )
    {
        // 没有作者 Scope 时不存在“任务进度”；不能把全局写作活动升级成任务状态。
        if (scope is null)
            return null;

        var edited = new HashSet<int>(previous?.EditedOrders ?? []);
        var inspected = new HashSet<int>(previous?.InspectedOrders ?? []);
        var outOfScope = new HashSet<int>(previous?.OutOfScopeOrders ?? []);
        var current = previous?.CurrentOrder;
        var hasEvent = previous is not null;
        int? lastInspected = null;

        var successByCallId = BuildSuccessMap(messages);
        var chapterById = structure
            .Chapters.GroupBy(chapter => chapter.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var chapterByOrder = structure
            .Chapters.GroupBy(chapter => chapter.Order)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var message in messages)
        {
            if (
                message.Role != ChatMessageRoles.Assistant
                || message.ToolCalls is not { Count: > 0 }
            )
                continue;

            foreach (var call in message.ToolCalls)
            {
                var function = call.FunctionCall;
                if (function?.Name is not { Length: > 0 } name || function.Arguments is null)
                    continue;
                if (
                    call.Id is null
                    || !successByCallId.TryGetValue(call.Id, out var succeeded)
                    || !succeeded
                )
                    continue;

                var isMutating = MutatingTools.Contains(name);
                var isInspecting = !isMutating && InspectingTools.Contains(name);
                if (!isMutating && !isInspecting)
                    continue;

                foreach (
                    var chapter in ResolveChapters(function.Arguments, chapterById, chapterByOrder)
                )
                {
                    if (!scope.ChapterOrders.Contains(chapter.Order))
                    {
                        if (isMutating)
                        {
                            outOfScope.Add(chapter.Order);
                            hasEvent = true;
                        }
                        continue;
                    }

                    if (isMutating)
                    {
                        edited.Add(chapter.Order);
                        inspected.Remove(chapter.Order);
                        current = chapter.Order;
                        hasEvent = true;
                    }
                    else if (!edited.Contains(chapter.Order))
                    {
                        inspected.Add(chapter.Order);
                        lastInspected = chapter.Order;
                        hasEvent = true;
                    }
                }
            }
        }

        edited.RemoveWhere(order => !scope.ChapterOrders.Contains(order));
        inspected.RemoveWhere(order =>
            !scope.ChapterOrders.Contains(order) || edited.Contains(order)
        );
        outOfScope.RemoveWhere(order => scope.ChapterOrders.Contains(order));
        if (current is int currentOrder && !scope.ChapterOrders.Contains(currentOrder))
            current = null;
        current ??= lastInspected;

        var next = scope
            .Chapters.OrderBy(chapter => chapter.Order)
            .FirstOrDefault(chapter => !edited.Contains(chapter.Order))
            ?.Order;

        if (!hasEvent)
            return null;
        return CompactionTaskProgress.Create(edited, inspected, current, next, outOfScope);
    }

    private static Dictionary<string, bool> BuildSuccessMap(IReadOnlyList<ChatMessage> messages)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != ChatMessageRoles.Tool || string.IsNullOrEmpty(message.ToolCallId))
                continue;
            result[message.ToolCallId] = message.ToolInvocationSucceeded != false;
        }
        return result;
    }

    private static List<Chapter> ResolveChapters(
        string arguments,
        IReadOnlyDictionary<string, Chapter> chapterById,
        IReadOnlyDictionary<int, Chapter> chapterByOrder
    )
    {
        var result = new List<Chapter>(capacity: 2);
        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return result;

            AddString(root, "chapter-id");
            AddString(root, "chapterId");
            AddArray(root, "chapter-ids");
            AddArray(root, "chapterIds");
        }
        catch (JsonException)
        {
            // 工具参数损坏时不能猜测章节；进度宁缺毋滥。
        }
        return result;

        void AddString(JsonElement root, string name)
        {
            if (
                root.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String
            )
                AddResolved(property.GetString());
        }

        void AddArray(JsonElement root, string name)
        {
            if (
                !root.TryGetProperty(name, out var property)
                || property.ValueKind != JsonValueKind.Array
            )
                return;
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    AddResolved(item.GetString());
                else if (
                    item.ValueKind == JsonValueKind.Number
                    && item.TryGetInt32(out var order)
                    && chapterByOrder.TryGetValue(order, out var chapter)
                )
                    AddChapter(chapter);
            }
        }

        void AddResolved(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;
            if (chapterById.TryGetValue(raw, out var chapter))
            {
                AddChapter(chapter);
                return;
            }
            if (
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var order)
                && chapterByOrder.TryGetValue(order, out chapter)
            )
                AddChapter(chapter);
        }

        void AddChapter(Chapter chapter)
        {
            if (
                !result.Any(existing =>
                    string.Equals(existing.Id, chapter.Id, StringComparison.Ordinal)
                )
            )
                result.Add(chapter);
        }
    }
}
