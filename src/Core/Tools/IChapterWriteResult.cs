namespace Core.Tools;

/// <summary>
/// 写入类工具的 Details 接口 — 提供章节 ID 和字数。
/// ChapterDetails / RewriteSelectionDetails 均实现此接口，
/// WritingFlowHook 通过模式匹配 <c>details is IChapterWriteResult</c> 提取数据，无需反射。
/// </summary>
public interface IChapterWriteResult
{
    /// <summary>章节 ID。</summary>
    string Id { get; }

    /// <summary>写入后的字数。</summary>
    int WordCount { get; }
}
