using System.Text;

namespace Core.Shared;

/// <summary>
/// ask_user 回复的统一文本格式（OMP 风格）：
/// <code>
/// User answers:
/// 方向: [身世, 旧案]
/// 视角: 第三人称
/// </code>
/// 多选带方括号，单选为裸值；多组按提问顺序逐行输出。
/// ask_user 工具结果与 TUI 追加的 user message 共用此格式，避免两套文本漂移。
/// </summary>
public static class AskUserReplyFormatter
{
    /// <summary>回复块首行。</summary>
    public const string Header = "User answers:";

    /// <summary>格式化一组问题；每组一行，未回答的组保留占位以维持顺序。</summary>
    public static string Format(IEnumerable<PendingQuestion> questions)
    {
        var sb = new StringBuilder(Header);
        var count = 0;
        foreach (var question in questions)
        {
            var answers = question.SelectedAnswers ?? [];
            var value =
                answers.Length == 0
                    ? question.MultiSelect
                        ? "[]"
                        : "(未回答)"
                    : FormatAnswers(question.MultiSelect, answers);
            sb.Append('\n').Append(GetKey(question, ++count)).Append(": ").Append(value);
        }
        return count == 0 ? "" : sb.ToString();
    }

    /// <summary>格式化单个问题的答案；<paramref name="multiSelect"/> 决定是否加方括号。</summary>
    private static string FormatAnswers(bool multiSelect, IReadOnlyList<string> answers)
    {
        if (!multiSelect)
            return answers.Count > 0 ? answers[0] : "";
        // 取消不是多选集合，统一保持标量，与迁移后的历史格式一致。
        if (answers.Count == 1 && answers[0] == "(已取消)")
            return answers[0];
        return $"[{string.Join(", ", answers)}]";
    }

    /// <summary>该 user 文本是否为 ask_user 回复（新格式或历史格式）。</summary>
    public static bool IsReply(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        var trimmed = content.TrimStart();
        return trimmed.StartsWith(Header, StringComparison.Ordinal)
            || trimmed.StartsWith("**用户回复:**", StringComparison.Ordinal);
    }

    private static string GetKey(PendingQuestion question, int ordinal) =>
        string.IsNullOrWhiteSpace(question.Header) ? $"Q{ordinal}" : question.Header.Trim();
}
