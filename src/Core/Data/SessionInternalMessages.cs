using Core.Shared;

namespace Core.Data;

/// <summary>
/// 会话里由系统静默注入的消息前缀。
/// 这些内容只给模型看：会话标题、Desktop/TUI 历史气泡都不得把它们当成作者原话。
/// </summary>
public static class SessionInternalMessages
{
    /// <summary>compact 摘要在 UI 系统行中的前缀；Desktop/TUI 历史恢复共用。</summary>
    public const string CompactSummaryPrefix = "🗜 上下文摘要：";

    /// <summary>系统注入的 user 消息首行前缀（按注入点维护）。</summary>
    private static readonly string[] UserPrefixes =
    [
        "[本体 harness]",
        "[云端诊断]",
        "[远期推演]",
        "[联想记忆]",
        "[写作战术",
        "[风格观察]",
        "[人工裁决]",
        "[系统通知]",
        "[系统提醒]",
        "[系统]",
        "[上下文页]",
        "🚨 上下文使用率",
        "⚠️ 上下文使用率",
        "📋 上下文使用率",
        "📋 写作事件信号",
        "📋 审计未通过",
    ];

    /// <summary>该 user 消息是否为系统注入（不是作者输入）。</summary>
    public static bool IsInjectedUserMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        var trimmed = content.TrimStart();
        return IsAskUserReply(trimmed)
            || UserPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>ask_user 回复：新 OMP 风格块或历史 <c>**用户回复:**</c> 块。</summary>
    public static bool IsAskUserReply(string? content) => AskUserReplyFormatter.IsReply(content);

    private static readonly HashSet<string> ContinuationOnlyMessages = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "继续",
        "接着",
        "继续吧",
        "接着来",
        "继续写",
        "继续改",
        "继续优化",
        "好",
        "好的",
        "可以",
        "行",
        "嗯",
        "ok",
        "按这个来",
        "就这样",
    };

    private static readonly char[] ContinuationTrailingChars =
    [
        '。',
        '！',
        '!',
        '，',
        ',',
        '～',
        '~',
        '.',
        '…',
        '、',
        '；',
        ';',
        '：',
        ':',
        ' ',
        '\t',
        '\r',
        '\n',
    ];

    /// <summary>整条消息只是“继续/好/可以”这类确认，没有携带新的任务范围。</summary>
    public static bool IsContinuationOnlyAuthorMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        var normalized = content.Trim().TrimEnd(ContinuationTrailingChars).TrimEnd();
        return ContinuationOnlyMessages.Contains(normalized);
    }

    /// <summary>该 assistant 文本是否为系统提示（稳定性/漂移提醒），应渲染为系统行。</summary>
    public static bool IsSystemAssistantMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        var trimmed = content.TrimStart();
        return trimmed.StartsWith("[系统]", StringComparison.Ordinal)
            || trimmed.StartsWith("[系统提醒]", StringComparison.Ordinal);
    }

    /// <summary>去掉系统提示前缀（系统行已有「系统」标识，不重复展示）。</summary>
    public static string NormalizeSystemText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("[系统提醒]", StringComparison.Ordinal))
            return trimmed["[系统提醒]".Length..].TrimStart();
        if (trimmed.StartsWith("[系统]", StringComparison.Ordinal))
            return trimmed["[系统]".Length..].TrimStart();
        return trimmed;
    }

    /// <summary>
    /// compact 摘要的系统行预览：只取首行并限长，避免历史恢复时把整段
    /// 摘要当成一条巨大的消息控件重建。
    /// </summary>
    public static bool IsCompactSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        var trimmed = content.TrimStart();
        return trimmed.StartsWith(CompactSummaryPrefix, StringComparison.Ordinal)
            || trimmed.StartsWith("[上下文摘要", StringComparison.Ordinal);
    }

    public static string FormatCompactSummaryPreview(string? content, int maxLength = 120)
    {
        var text = GetCompactSummaryBody(content);
        var lineEnd = text.IndexOf('\n');
        var line = (lineEnd >= 0 ? text[..lineEnd] : text).Trim();
        if (line.StartsWith('#'))
            line = line.TrimStart('#').TrimStart();
        else if (line.StartsWith("- ", StringComparison.Ordinal))
            line = line[2..].TrimStart();
        if (line.Length > maxLength)
            line = line[..maxLength] + "…";
        return CompactSummaryPrefix + line;
    }

    /// <summary>
    /// 取出 compact 摘要正文（去掉 UI 前缀或运行时 [上下文摘要 bN] 头）。
    /// 预览与展开全文共用同一套提取规则，避免 UI 行和详情显示不同内容。
    /// </summary>
    public static string GetCompactSummaryBody(string? content)
    {
        var text = (content ?? "").TrimStart();
        if (text.StartsWith(CompactSummaryPrefix, StringComparison.Ordinal))
            return text[CompactSummaryPrefix.Length..];
        if (text.StartsWith("[上下文摘要", StringComparison.Ordinal))
        {
            var headerEnd = text.IndexOf('\n');
            return headerEnd >= 0 ? text[(headerEnd + 1)..] : "";
        }
        return text;
    }

    /// <summary>
    /// 从 user 消息提取会话标题；跳过注入消息。没有可用的作者消息时返回 null。
    /// </summary>
    public static string? ExtractTitle(string? content, int maxLength = 40)
    {
        if (string.IsNullOrWhiteSpace(content) || IsInjectedUserMessage(content))
            return null;
        var firstLine = content.Split('\n')[0].Trim();
        if (firstLine.Length == 0)
            return null;
        return firstLine.Length <= maxLength ? firstLine : firstLine[..maxLength] + "…";
    }
}
