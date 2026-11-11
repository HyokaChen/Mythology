using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Desktop.Models;
using Markdig;

namespace Desktop.Controls;

/// <summary>
/// 对话块复制文本的统一构造：只在这里决定「复制什么」，按钮与快捷键共用同一口径。
/// 正文走 Markdown → 纯文本，工具/思考步骤保留可读的参数与结果。
/// </summary>
internal static class MessageCopy
{
    /// <summary>根据行类型构造完整复制文本；空内容返回空串（调用方据此不写剪贴板）。</summary>
    internal static string Build(ConversationRow? row)
    {
        if (row is null)
            return "";

        return row.Kind switch
        {
            ConversationRowKind.User => Normalize(row.Message.Content.ToString()),
            ConversationRowKind.System => Normalize(row.Message.Content.ToString()),
            ConversationRowKind.Content => MarkdownToPlain(row.Message.Content.ToString()),
            ConversationRowKind.Step when row.Step is { } step => BuildStep(step),
            ConversationRowKind.Actions => BuildActions(row),
            ConversationRowKind.Interrupted => "⏹ 中断生成",
            _ => Normalize(row.Message.Content.ToString()),
        };
    }

    /// <summary>把 Actions 行（无正文的助手回合）的复制目标设为整条消息的步骤摘要。</summary>
    internal static string BuildMessageOrSteps(ConversationRow row)
    {
        if (row.Message.HasVisibleContent)
            return MarkdownToPlain(row.Message.Content.ToString());
        return string.Join(
            "\n\n",
            row.Message.AgentSteps.Select(BuildStep).Where(text => text.Length > 0)
        );
    }

    /// <summary>
    /// 无正文助手回合的动作行：优先复制完整的步骤/工具记录；
    /// 连一条步骤都没有的纯中断回合复制可见的中断标记，避免按钮点了没反应。
    /// </summary>
    private static string BuildActions(ConversationRow row)
    {
        var text = BuildMessageOrSteps(row);
        return text.Length > 0 || !row.Message.IsInterrupted ? text : "⏹ 中断生成";
    }

    private static string BuildStep(AgentStep step)
    {
        if (step.Kind == StepKind.Content)
            return MarkdownToPlain(step.ContentText);
        if (step.Kind is StepKind.Notice or StepKind.SystemEvent)
            return Normalize(step.ContentText);
        if (step.Kind == StepKind.Thinking)
            return Normalize(step.ThinkingText);

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(step.Title))
            builder.AppendLine(step.Title);

        if (!string.IsNullOrWhiteSpace(step.DetailArguments))
        {
            builder.AppendLine("参数：");
            builder.AppendLine(step.DetailArguments);
        }

        if (step.HasDiff)
        {
            if (!string.IsNullOrWhiteSpace(step.DiffSummary))
                builder.AppendLine(step.DiffSummary);
            foreach (var line in step.DiffLines)
                builder.AppendLine($"{line.Prefix}{line.LineNumber} {line.Text}".TrimEnd());
        }
        else if (!string.IsNullOrWhiteSpace(step.DetailResult))
        {
            builder.AppendLine("结果：");
            builder.Append(step.DetailResult);
        }
        else if (!string.IsNullOrWhiteSpace(step.ResultSummary))
        {
            builder.AppendLine("结果：");
            builder.Append(step.ResultSummary);
        }

        return builder.ToString().TrimEnd();
    }

    private static string MarkdownToPlain(string markdown)
    {
        var text = markdown ?? "";
        if (text.Length == 0)
            return "";
        try
        {
            return Normalize(Markdown.ToPlainText(text));
        }
        catch
        {
            // 极端 Markdown 输入不应让复制按钮失效：退回原文。
            return Normalize(text);
        }
    }

    private static string Normalize(string text) => text.TrimEnd();

    /// <summary>写系统剪贴板；控件未挂到窗口或平台拒绝时不抛异常。</summary>
    internal static async Task<bool> CopyAsync(TopLevel? topLevel, string text)
    {
        if (topLevel?.Clipboard is not { } clipboard || string.IsNullOrEmpty(text))
            return false;

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
