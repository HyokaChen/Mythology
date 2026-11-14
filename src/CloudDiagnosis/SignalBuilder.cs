using System.Text;
using Core.Cloud;

namespace CloudDiagnosis;

/// <summary>
/// 云端诊断注入文本的唯一格式化入口。
/// 只消费服务端已脱敏的 <see cref="DiagnosisReport"/>；不解析原始 payload，
/// 不输出规则码、维度键、阈值、能力 ID 或计费单位。
/// </summary>
public static class SignalBuilder
{
    /// <summary>所有注入信号的识别锚点。</summary>
    public const string Prefix = "[云端诊断]";

    /// <summary>云端不可用时的本地自检提醒（不伪造 findings）。</summary>
    public const string HumanizerUnavailableMessage =
        "[本地自检] 云端去AI味校验未完成。交付前按写作风格规范自检"
        + "（对举/结论冒号/口头禅/翻译腔/同义称呼/同骨架构句），"
        + "并在回复中标注「云端去AI味校验未完成」；不要伪造检测结果。";

    public static string Format(
        DiagnosisReport report,
        IReadOnlyList<DiagnosisFinding> localFindings
    )
    {
        var builder = new StringBuilder();
        builder.Append(Prefix).Append(" 结果：").Append(report.Verdict);
        builder.Append('\n').Append("检查：云端诊断");
        builder.Append('\n').Append("结论：").Append(OneLine(report.Summary));

        if (report.Action == "proceed")
            return builder.ToString();

        var selected = new List<DiagnosisFinding>();
        selected.AddRange(report.Findings.Where(f => f.Action is "fix-now" or "suggest").Take(3));
        selected.AddRange(localFindings.Take(2));

        for (var i = 0; i < selected.Count; i++)
        {
            var finding = selected[i];
            builder
                .Append('\n')
                .Append("问题 ")
                .Append(i + 1)
                .Append("（")
                .Append(finding.Category)
                .Append(" · ")
                .Append(finding.SeverityLabel)
                .Append('）');
            builder.Append('\n').Append("问题：").Append(OneLine(finding.Problem));
            if (!string.IsNullOrWhiteSpace(finding.Evidence))
                builder.Append('\n').Append("证据：").Append(OneLine(finding.Evidence));
            if (!string.IsNullOrWhiteSpace(finding.Suggestion))
                builder.Append('\n').Append("建议：").Append(OneLine(finding.Suggestion));
        }

        if (report.Action == "ask-author")
        {
            if (!string.IsNullOrWhiteSpace(report.DecisionQuestion))
                builder
                    .Append('\n')
                    .Append("需要作者决定：")
                    .Append(OneLine(report.DecisionQuestion));
            if (report.DecisionOptions is { Count: > 0 } options)
            {
                builder.Append('\n').Append("选项：");
                for (var i = 0; i < options.Count; i++)
                {
                    if (i > 0)
                        builder.Append(' ');
                    builder.Append(i + 1).Append(". ").Append(OneLine(options[i]));
                }
            }
        }

        return builder.ToString();
    }

    private static string OneLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
