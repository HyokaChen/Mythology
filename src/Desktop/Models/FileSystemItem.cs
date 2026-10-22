namespace Desktop.Models;

using Lucide.Avalonia;

/// <summary>@ 路径补全建议项（小说实体：角色/章节）。</summary>
public sealed class FileSystemItem
{
    /// <summary>显示名称。</summary>
    public string Name { get; init; } = "";

    /// <summary>完整路径（补全插入文本）。</summary>
    public string FullPath { get; init; } = "";

    /// <summary>类型图标（Lucide 单色描边）。</summary>
    public LucideIconKind IconKind { get; init; } = LucideIconKind.FileText;
}
