using System.Text;

namespace Core.Shared;

/// <summary>
/// 变体内容的 XML 包裹格式。仅用于注入模型上下文的 prompt；
/// 章节正文写入保持原文，不包标签。
/// </summary>
public static class VariantXml
{
    /// <summary>
    /// 生成 <c>&lt;variant ...&gt;内容&lt;/variant&gt;</c> 块。
    /// 空属性自动省略。
    /// </summary>
    public static string Wrap(
        string content,
        string? variantId = null,
        string? label = null,
        string? type = null,
        string? target = null
    )
    {
        var sb = new StringBuilder("<variant");
        AppendAttribute(sb, "id", variantId);
        AppendAttribute(sb, "label", label);
        AppendAttribute(sb, "type", type);
        AppendAttribute(sb, "target", target);
        sb.Append(">\n");
        sb.Append(content.Trim());
        sb.Append("\n</variant>");
        return sb.ToString();
    }

    private static void AppendAttribute(StringBuilder sb, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        sb.Append(' ').Append(name).Append("=\"").Append(Escape(value.Trim())).Append('"');
    }

    private static string Escape(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
