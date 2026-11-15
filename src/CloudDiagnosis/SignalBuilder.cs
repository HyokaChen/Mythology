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
        + "（对举/结论冒号/口头禅/翻译腔/同义称呼/同骨架构句/独立分隔符是否服务于真实场景切换），"
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

        AppendFindings(builder, report, localFindings);

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

    /// <summary>
    /// 无人值守质量决策：不向作者提问，把诊断交给 Agent 自己判断「改写本章」还是「继续」。
    /// 达到自动修复上限后明确要求停止改写并继续推进，避免长任务卡死或无限修稿。
    /// </summary>
    public static string FormatAutonomousDecision(
        DiagnosisReport report,
        IReadOnlyList<DiagnosisFinding> localFindings,
        int round,
        int maxRounds
    )
    {
        var builder = new StringBuilder();
        builder
            .Append(Prefix)
            .Append(" 质量未达标，Agent 自主处理（")
            .Append(round)
            .Append('/')
            .Append(maxRounds)
            .Append('）');
        builder.Append('\n').Append("结论：").Append(OneLine(report.Summary));
        AppendFindings(builder, report, localFindings);
        builder
            .Append('\n')
            .Append("请自行判断并直接行动，不要调用 ask_user，也不要停下来等待作者确认：");
        builder.Append('\n').Append("- 优先修改本章，解决高严重度问题后继续复检；");
        builder.Append('\n').Append("- 仅当剩余问题属于可接受的过渡/铺垫时，按建议继续下一章；");
        builder.Append('\n').Append("- 不要只解释诊断或复述问题，必须实际改写或推进。");

        if (round >= maxRounds)
        {
            builder
                .Append('\n')
                .Append(
                    "- 已到本轮自动修复上限：停止继续改写，保留未解决问题记录并继续推进，不要再次询问。"
                );
        }

        return builder.ToString();
    }

    /// <summary>问题清单共用格式：云端 fix-now/suggest 最多 3 条 + 本地最多 2 条。</summary>
    private static void AppendFindings(
        StringBuilder builder,
        DiagnosisReport report,
        IReadOnlyList<DiagnosisFinding> localFindings
    )
    {
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
    }

    private static string OneLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
