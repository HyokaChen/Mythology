namespace Core.Shared;

/// <summary>
/// 文件冲突异常：文件在读取后被外部（人类用户或其他进程）修改，
/// 当前写入操作被拒绝以避免静默覆盖。
/// </summary>
public sealed class FileConflictException : IOException
{
    /// <summary>发生冲突的文件路径。</summary>
    public string FilePath { get; }

    public FileConflictException(string filePath)
        : base(
            $"File '{filePath}' was modified externally since last read. "
                + "Re-read the file and retry the operation."
        )
    {
        FilePath = filePath;
    }

    public FileConflictException(string filePath, string message)
        : base(message)
    {
        FilePath = filePath;
    }
}
