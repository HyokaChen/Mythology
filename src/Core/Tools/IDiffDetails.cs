using Core.Shared;

namespace Core.Tools;

/// <summary>
/// 包含结构化差异数据的 Details 接口。
/// WriteFileDetails、EditFileDetails、ChapterDetails、EditTextDetails 均实现此接口，
/// TUI 渲染层通过模式匹配 <c>details is IDiffDetails</c> 提取 diff 数据，无需反射。
/// </summary>
public interface IDiffDetails
{
    /// <summary>Diff hunks 列表，null 表示无旧内容（新建文件/章节）。</summary>
    EditHunk[]? Hunks { get; }

    /// <summary>文件或资源路径，null 时 TUI 不显示文件名标题。</summary>
    string? Path { get; }
}
