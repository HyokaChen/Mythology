using System.Text;
using Core.Data;

namespace Tui.Services;

/// <summary>
/// 章节阅读器的内容装配（纯函数，可单测）：细纲带 + 正文。
///
/// 定位：长篇（上千章）列表只给骨架行（序号/标题/状态/字数），**细纲与正文在这里按需展开**——
/// 所以阅读器是「看章节」的主入口，而不是让用户去翻 chapters/*.md 文件。
/// </summary>
internal static class ChapterReaderContent
{
    /// <summary>正文为空时的占位（提示可行动路径）。</summary>
    internal const string EmptyContentPlaceholder =
        "（本章尚无正文。可以让模型用 write_chapter 生成，或点「📝」编辑）";

    /// <summary>细纲为空时的占位。</summary>
    internal const string EmptySummaryPlaceholder =
        "（未填写细纲：本章梗概 / 计划推进 / 计划出场）";

    /// <summary>
    /// 构建阅读器 Markdown：标题 + 细纲带（状态/字数/卷/细纲/计划与实际的推进、出场、变化）+ 正文。
    /// 名称为 null 的 id 原样回显（实体可能已删除），不隐藏信息。
    /// </summary>
    public static string Build(
        Chapter chapter,
        Volume? volume,
        Func<string, string>? plotlineName = null,
        Func<string, string>? characterName = null,
        IReadOnlyList<AppearanceRecord>? appearances = null,
        IReadOnlyList<EntityStateChange>? changes = null,
        Func<string, string>? locationName = null
    )
    {
        var sb = new StringBuilder(capacity: Math.Max(512, chapter.Content.Length + 512));

        sb.Append("# 第").Append(chapter.Order).Append("章 ");
        sb.Append(string.IsNullOrWhiteSpace(chapter.Title) ? "（未命名）" : chapter.Title)
            .Append('\n');
        sb.Append('\n');

        // 细纲带：状态 / 字数 / 所属卷
        var meta = new List<string> { StatusLabel(chapter.Status), $"{chapter.CharCount:N0} 字" };
        if (volume is not null)
            meta.Add($"第{volume.Order}卷 {volume.Name}");
        sb.Append(string.Join(" · ", meta.Select(m => $"`{m}`"))).Append('\n');
        sb.Append('\n');

        sb.Append("## 细纲\n");
        sb.Append(
                string.IsNullOrWhiteSpace(chapter.Summary)
                    ? EmptySummaryPlaceholder
                    : chapter.Summary
            )
            .Append('\n');

        AppendIdLine(sb, "计划推进", chapter.PlotlineIds, plotlineName);
        AppendIdLine(sb, "计划出场", chapter.CharacterIds, characterName);
        if (!string.IsNullOrWhiteSpace(chapter.Notes))
            sb.Append("**备注**: ").Append(chapter.Notes).Append('\n');

        AppendAppearances(sb, appearances, characterName);
        AppendChanges(sb, changes, characterName, locationName, plotlineName);

        sb.Append('\n').Append("---").Append('\n').Append('\n');
        sb.Append("## 正文\n");
        sb.Append(
                string.IsNullOrWhiteSpace(chapter.Content)
                    ? EmptyContentPlaceholder
                    : chapter.Content
            )
            .Append('\n');
        return sb.ToString();
    }

    /// <summary>计划字段行：`**计划推进**: 名A、名B`（空列表整行省略）。</summary>
    private static void AppendIdLine(
        StringBuilder sb,
        string label,
        IReadOnlyList<string> ids,
        Func<string, string>? resolve
    )
    {
        if (ids.Count == 0)
            return;
        var names = ids.Select(id => resolve?.Invoke(id) ?? id);
        sb.Append("**").Append(label).Append("**: ").Append(string.Join("、", names)).Append('\n');
    }

    /// <summary>实际出场（按章记录）：`**实际出场**: 林晚(在场)、陈默(提及)`。</summary>
    private static void AppendAppearances(
        StringBuilder sb,
        IReadOnlyList<AppearanceRecord>? appearances,
        Func<string, string>? resolve
    )
    {
        if (appearances is not { Count: > 0 })
            return;
        var parts = appearances.Select(a =>
        {
            var name = resolve?.Invoke(a.EntityId) ?? a.EntityId;
            return $"{name}({AppearanceModeLabel(a.Mode)})";
        });
        sb.Append("**实际出场**: ").Append(string.Join("、", parts)).Append('\n');
    }

    /// <summary>
    /// 状态变化：`**状态变化**: 林晚: 受伤 → 痊愈（待发布 · 地点 破庙 · 情节线 复仇线）；…`。
    /// 待发布（草稿章中登记、尚未应用）与地点/情节线一并标注——读者能分清「已经发生的」和「将要生效的」。
    /// </summary>
    private static void AppendChanges(
        StringBuilder sb,
        IReadOnlyList<EntityStateChange>? changes,
        Func<string, string>? resolve,
        Func<string, string>? locationName,
        Func<string, string>? plotlineName
    )
    {
        if (changes is not { Count: > 0 })
            return;
        var parts = changes.Select(c =>
        {
            var target = resolve?.Invoke(c.TargetId) ?? c.TargetId;
            var body = string.IsNullOrWhiteSpace(c.Summary)
                ? $"{c.FromSummary ?? "?"} → {c.ToSummary ?? "?"}"
                : c.Summary;

            var marks = new List<string>();
            if (c.Lifecycle == ChangeLifecycle.Pending)
                marks.Add("待发布");
            if (c.LocationId is { Length: > 0 } locationId)
                marks.Add($"地点 {locationName?.Invoke(locationId) ?? locationId}");
            if (c.PlotlineIds.Count > 0)
                marks.Add(
                    "情节线 "
                        + string.Join(
                            "、",
                            c.PlotlineIds.Select(id => plotlineName?.Invoke(id) ?? id)
                        )
                );
            return marks.Count == 0
                ? $"{target}: {body}"
                : $"{target}: {body}（{string.Join(" · ", marks)}）";
        });
        sb.Append("**状态变化**: ").Append(string.Join("；", parts)).Append('\n');
    }

    private static string StatusLabel(ChapterStatus status) =>
        status switch
        {
            ChapterStatus.Draft => "草稿",
            ChapterStatus.Published => "发布",
            _ => status.ToString(),
        };

    private static string AppearanceModeLabel(AppearanceMode mode) =>
        mode switch
        {
            AppearanceMode.Present => "在场",
            AppearanceMode.Mentioned => "提及",
            _ => mode.ToString(),
        };
}
