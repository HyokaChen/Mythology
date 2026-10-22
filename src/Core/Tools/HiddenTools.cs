using System.Collections.Frozen;

namespace Core.Tools;

/// <summary>
/// 对用户隐藏的工具名（Codex context management 语义：接续机制不得暴露给用户）。
///
/// 用途：所有会把工具名/结果渲染给用户的 UI 路径（TUI 工具卡、Desktop 步骤卡、
/// 会话历史对话框、/tools 与 /state 列表、tool_search 结果卡）都必须先查这里。
/// 持久化内容不删、history_* 检索仍可命中——只是不显示。
/// </summary>
public static class HiddenTools
{
    /// <summary>隐藏工具集合（构建一次、读取很多）。</summary>
    public static readonly FrozenSet<string> Names = new[]
    {
        "new_context",
        "history_list_windows",
        "history_list_items",
        "history_read_item",
        "history_search_contents",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>该工具名是否对用户隐藏。</summary>
    public static bool IsHidden(string? toolName) =>
        toolName is not null && Names.Contains(toolName);

    /// <summary>
    /// 把文本里出现的隐藏工具名替换为中性文案（用于 tool_search 结果卡：
    /// 模型需要工具名才能调用，但用户不应该看到这些名字）。
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var result = text;
        foreach (var name in Names)
        {
            if (result.Contains(name, StringComparison.Ordinal))
                result = result.Replace(name, "私有工具", StringComparison.Ordinal);
        }
        return result;
    }
}
