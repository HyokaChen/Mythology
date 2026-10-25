using System.Text.Json;
using Core.Shared;
using Core.Tools;
using Desktop.Models;

namespace Desktop.Services;

/// <summary>结构化 diff：用于详情区渲染与行内摘要，计数与 TUI 共用 DiffComputer。</summary>
public sealed record ToolDiffPresentation(
    int Added,
    int Removed,
    string Summary,
    IReadOnlyList<DiffLineView> Lines
);

/// <summary>
/// 把工具返回的 <see cref="IDiffDetails"/> 转成统一 diff 行。
/// 计算、计数和上下文折叠全部委托给 <see cref="DiffComputer"/>，
/// 与 TUI 的 ToolCallRenderer 使用同一套算法和参数。
/// </summary>
public static class ToolDiffBuilder
{
    /// <summary>与 TUI ToolCallRenderer.TryRenderDataDrivenDiff 保持一致。</summary>
    public const int ContextLines = 5;

    public static ToolDiffPresentation? Build(IDiffDetails? details)
    {
        if (details?.Hunks is not { Length: > 0 } hunks)
            return null;

        var lines = new List<DiffLine>();
        foreach (var hunk in hunks)
        {
            var hunkLines = DiffComputer.Compute(hunk.OldText, hunk.NewText);
            if (lines.Count > 0 && hunkLines.Count > 0)
                lines.Add(new DiffLine(DiffLineType.Context, null, null, "···"));
            lines.AddRange(hunkLines);
        }

        if (lines.Count == 0)
            return null;

        var (added, removed) = DiffComputer.CountChanges(lines);
        var folded = DiffComputer.FoldContext(lines, ContextLines);
        return new ToolDiffPresentation(
            added,
            removed,
            ToolDisplayFormatter.DiffSummary(details.Path, added, removed),
            folded.Select(DiffLineView.From).ToArray()
        );
    }

    /// <summary>
    /// 历史会话没有持久化 Details，只能从 edit 参数里的 oldText/newText 重建 diff。
    /// 仅在结果非错误时由 SessionMessageMapper 调用，避免把失败的编辑显示成已发生的差异。
    /// </summary>
    public static ToolDiffPresentation? BuildFromEditArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var path = GetString(doc.RootElement, "path") ?? "";
            if (
                !doc.RootElement.TryGetProperty("edits", out var edits)
                || edits.ValueKind != JsonValueKind.Array
            )
                return null;

            var hunks = new List<EditHunk>();
            foreach (var edit in edits.EnumerateArray())
            {
                if (edit.ValueKind != JsonValueKind.Object)
                    continue;
                var oldText = GetString(edit, "oldText");
                var newText = GetString(edit, "newText");
                if (oldText is null || newText is null)
                    continue;
                hunks.Add(new EditHunk { OldText = oldText, NewText = newText });
            }

            return hunks.Count == 0 ? null : Build(new InMemoryDiffDetails(path, [.. hunks]));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 历史 edit 结果若明确只应用了部分编辑（或完全未应用），参数里的 old/new
    /// 不能代表文件真实变化，应回退文本结果而不是伪造完整 diff。
    /// </summary>
    public static bool CanRebuildEditDiff(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
            return true;
        if (resultText.Contains("未应用任何编辑", StringComparison.Ordinal))
            return false;

        const string marker = "已应用 ";
        var markerIndex = resultText.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return true;

        var progress = resultText.AsSpan(markerIndex + marker.Length);
        var slash = progress.IndexOf('/');
        if (slash <= 0)
            return true;
        var totalEnd = progress[(slash + 1)..].IndexOf(' ');
        if (totalEnd < 0)
            return true;

        return int.TryParse(progress[..slash], out var applied)
            && int.TryParse(progress.Slice(slash + 1, totalEnd), out var total)
            && applied == total;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record InMemoryDiffDetails(string Path, EditHunk[] Hunks) : IDiffDetails;
}
