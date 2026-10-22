namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量工具的 AI 引导语。工具的 Description 会原样注入 LLM 工具定义
/// （ConversationLoop.BuildChatRequest），这些常量用于把"优先批量"变成硬性偏好：
/// 单实体工具在同一轮并行执行时，会并发读改写同一个 YAML 文件（read-modify-write），
/// 触发 AtomicFile.EnsureUnchangedAsync 冲突或丢失更新。
/// </summary>
internal static class BatchToolHints
{
    /// <summary>追加在批量工具 Description 末尾的引导语。</summary>
    public const string PreferBatch =
        "。需要创建 2 个及以上实体时，必须使用本批量工具一次完成，"
        + "禁止连续多次调用单实体 manage_* 工具逐个创建——"
        + "单实体工具并行写同一文件会并发冲突导致丢数据";

    /// <summary>追加在单实体 CRUD 工具 Description 末尾的反向引导语。</summary>
    public const string PreferBatchForMultiple =
        "。一次需要创建多个实体时，优先改用 batch_create_* 批量工具单次完成"
        + "（一次调用内串行落盘，避免并行写同一文件冲突）";
}
