using System.Text;
using Core.Cloud;
using Core.Data;
using Core.Vector;

namespace Core.Forecast;

/// <summary>
/// 把 <see cref="PlotForecast"/> + 证据 + 笔记线索渲染成注入块（<c>[远期推演]</c> 前缀）。
/// 只用确定性模板：模型读到的是「到期欠账 / 计划转折 / 缺口 / 风险 / 线索」，不需要自己算。
/// </summary>
public static class PlotForecastFormatter
{
    /// <summary>注入消息的识别锚点（测试与 Prompt 层依赖此前缀）。</summary>
    public const string Prefix = "[远期推演]";

    /// <summary>整块字符预算（超出即截断，避免挤占写作上下文）。</summary>
    public const int MaxChars = 1400;

    /// <summary>纲要数据不足时的引导语。</summary>
    public const string NoPlanGuide =
        "[远期推演] 纲要数据不足：请先填写下一章细纲（edit_entities）或情节线的计划揭示章（manage_plotline）。";

    public static string Format(
        PlotForecast forecast,
        IReadOnlyList<ForecastEvidence> evidence,
        NoteSelection? notes = null
    )
    {
        if (!forecast.HasPlanData)
            return NoPlanGuide;

        var sb = new StringBuilder();
        var truncated = false;

        var head = new StringBuilder(Prefix);
        if (forecast.CurrentOrder > 0)
            head.Append(" 已写至第").Append(forecast.CurrentOrder).Append("章");
        else
            head.Append(" 尚未写章");
        head.Append("，下一章：")
            .Append('第')
            .Append(forecast.TargetOrder)
            .Append("章《")
            .Append(
                string.IsNullOrWhiteSpace(forecast.TargetChapterTitle)
                    ? "（未在细纲中登记）"
                    : forecast.TargetChapterTitle
            )
            .Append('》');
        if (!string.IsNullOrWhiteSpace(forecast.VolumeGoal))
            head.Append("（本卷目标：").Append(forecast.VolumeGoal).Append('）');
        sb.AppendLine(head.ToString());

        truncated =
            truncated
            || AppendSection(sb, "到期义务：", forecast.Obligations.Select(FormatObligation));
        truncated = truncated || AppendSection(sb, "风险：", forecast.Risks.Select(r => r.Message));

        truncated =
            truncated
            || AppendSection(
                sb,
                "计划转折：",
                forecast.Beats.Select(b =>
                    $"第{b.ChapterOrder}章 ·《{b.PlotlineName}》[{b.Type} {b.Tension.ToString("0.0")}] {b.Description}"
                )
            );

        if (forecast.Window.Count > 0)
        {
            var first = forecast.Window[0].Order;
            var last = forecast.Window[^1].Order;
            truncated =
                truncated
                || AppendSection(
                    sb,
                    $"计划远期（第{first}-{last}章）：",
                    forecast.Window.Select(w =>
                        $"第{w.Order}章《{w.Title}》：{Truncate(Flatten(w.Summary), 60)}"
                    )
                );
        }

        truncated =
            truncated
            || AppendSection(
                sb,
                "规划缺口：",
                forecast.SeedGaps.Select(g =>
                    $"第{g.ReferencedOrder}章细纲提到的{(g.Kind == "Character" ? "角色" : "情节线")}「{g.Name}」在前文无任何出场/推进记录 → 需先铺垫"
                )
            );

        if (notes is { Notes.Count: > 0 })
        {
            var lines = new List<string>();
            foreach (var note in notes.Notes)
            {
                lines.Add(
                    note.Source switch
                    {
                        "anchor" => $"《{note.Title}》第{note.AnchorOrder}章 · {note.Excerpt}",
                        "past" =>
                            $"《{note.Title}》第{note.AnchorOrder}章（已写）· {note.Excerpt} → 确认是否已落地",
                        "far" => $"《{note.Title}》· {note.Excerpt}（远期提醒）",
                        _ => $"《{note.Title}》· {note.Excerpt}",
                    }
                );
            }
            truncated = truncated || AppendSection(sb, "笔记线索：", lines);
            if (notes.InboxCount > 0)
                truncated =
                    truncated
                    || !TryAppendLine(sb, $"笔记待确认：{notes.InboxCount} 条（确认后才参与推演）");
        }

        truncated = truncated || AppendSection(sb, "前文证据：", evidence.Select(FormatEvidence));

        if (truncated)
            sb.AppendLine("…（已截断）");

        return sb.ToString().TrimEnd();
    }

    private static string FormatObligation(ForecastObligation obligation)
    {
        var sb = new StringBuilder();
        sb.Append('《').Append(obligation.Name).Append("》（").Append(TypeLabel(obligation.Type));
        sb.Append("）计划第").Append(obligation.PlannedOrder).Append("章揭示");
        sb.Append(
            obligation.Overdue
                ? $"，已逾期 {-obligation.RemainingChapters} 章"
                : $"，还有 {obligation.RemainingChapters} 章"
        );
        sb.Append("｜已推进 ").Append(obligation.AdvanceOrders.Count).Append(" 次");
        sb.Append(
            obligation.AdvanceOrders.Count > 0
                ? $"（第{string.Join("、", obligation.AdvanceOrders)}章）"
                : "（尚未推进）"
        );
        return sb.ToString();
    }

    private static string FormatEvidence(ForecastEvidence evidence)
    {
        var sb = new StringBuilder();
        if (evidence.ChapterOrder is { } order)
        {
            sb.Append('第').Append(order).Append('章');
            if (!string.IsNullOrWhiteSpace(evidence.ChapterTitle))
                sb.Append('《').Append(evidence.ChapterTitle).Append('》');
        }
        else
        {
            sb.Append('—');
        }
        sb.Append('·');
        if (evidence.Source == "cloud")
        {
            sb.Append("联想");
            if (evidence.Family is { Length: > 0 } family)
                sb.Append('[').Append(AssociativeLinkClient.FamilyLabel(family));
            if (evidence.Channel is { Length: > 0 } channel)
                sb.Append('·').Append(AssociativeLinkClient.ChannelLabel(channel));
            if (evidence.Family is { Length: > 0 })
                sb.Append(']');
        }
        else
        {
            sb.Append(evidence.Kind?.ToChineseLabel() ?? "联想");
        }
        sb.Append('：').Append(Truncate(Flatten(evidence.Text), 80));
        return sb.ToString();
    }

    private static string TypeLabel(PlotlineType type) =>
        type switch
        {
            PlotlineType.Main => "主线",
            PlotlineType.Side => "支线",
            PlotlineType.Hidden => "暗线",
            _ => "伏笔",
        };

    /// <summary>追加一节（空行集 → 不加标题）；返回是否因预算被截断。</summary>
    private static bool AppendSection(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var headerWritten = false;
        foreach (var line in lines)
        {
            if (!headerWritten)
            {
                if (sb.Length + title.Length + 1 > MaxChars)
                    return true;
                sb.AppendLine(title);
                headerWritten = true;
            }
            if (!TryAppendLine(sb, "- " + line))
                return true;
        }
        return false;
    }

    /// <summary>追加一行；预算不足返回 false（调用方标记截断）。</summary>
    private static bool TryAppendLine(StringBuilder sb, string line)
    {
        if (sb.Length + line.Length + 1 > MaxChars)
            return false;
        sb.AppendLine(line);
        return true;
    }

    private static string Flatten(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
