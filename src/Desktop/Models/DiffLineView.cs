using Core.Shared;

namespace Desktop.Models;

/// <summary>
/// 对话详情中的一行结构化 diff。
/// 颜色不放进模型，由 XAML 按 <see cref="Kind"/> 选择动态主题画刷。
/// </summary>
public sealed class DiffLineView
{
    public required DiffLineType Kind { get; init; }

    public string Text { get; init; } = "";

    public int? OldLineNumber { get; init; }

    public int? NewLineNumber { get; init; }

    public bool IsContext => Kind == DiffLineType.Context;

    public bool IsDelete => Kind == DiffLineType.Delete;

    public bool IsInsert => Kind == DiffLineType.Insert;

    public string Prefix =>
        Kind switch
        {
            DiffLineType.Delete => "-",
            DiffLineType.Insert => "+",
            _ => " ",
        };

    /// <summary>与 TUI 一致：删除/上下文行显示旧行号，插入行显示新行号。</summary>
    public string LineNumber =>
        (
            Kind switch
            {
                DiffLineType.Delete => OldLineNumber,
                DiffLineType.Insert => NewLineNumber,
                _ => OldLineNumber ?? NewLineNumber,
            }
        )?.ToString() ?? "";

    public static DiffLineView From(DiffLine line) =>
        new()
        {
            Kind = line.Type,
            Text = line.Text,
            OldLineNumber = line.OldLineNum,
            NewLineNumber = line.NewLineNum,
        };
}
