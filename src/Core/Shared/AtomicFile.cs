using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Core.Shared;

/// <summary>
/// 统一原子文件 I/O 工具类。
/// 所有覆写操作使用 File.Replace（OS 级原子替换），统一 UTF-8 无 BOM。
/// 内置 SHA256 + mtime 双重内容追踪，用于检测外部文件修改冲突。
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// 文件追踪信息：SHA256 内容哈希 + 最后写入时间 UTC。
    /// mtime 快速路径：未变则跳过哈希计算；
    /// mtime 变了但哈希未变（如 touch 命令），更新 mtime 追踪即可。
    /// </summary>
    private sealed record FileTrackingInfo
    {
        public string ContentHash { get; init; } = "";
        public DateTime LastWriteTimeUtc { get; init; }
    }

    private static readonly ConcurrentDictionary<string, FileTrackingInfo> TrackedFiles = new();

    private sealed record FastAppendTrackingInfo(long Length, DateTime LastWriteTimeUtc);

    private static readonly ConcurrentDictionary<string, FastAppendTrackingInfo> FastAppendedFiles =
        new();

    /// <summary>
    /// 进程级文件写入锁集合。Key=文件路径，Value=SemaphoreSlim(1,1)。
    /// 用于确保同一进程内对同一 JSONL 文件的并发写入不会交叉。
    /// 替代 FileShare.Read 独占锁（Unix 上为 advisory，不可靠）。
    /// 单行写入依赖 O_APPEND 内核级原子性，但 EnsureUnchangedAsync +
    /// TrackContent 时序需要信号量保护。
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new();

    /// <summary>
    /// 获取文件级别的写入锁（进程级 SemaphoreSlim 互斥）。
    /// 用于需要多行原子写入的场景（如 compact + leaf），
    /// 或需要将 EnsureUnchangedAsync + 写入 + TrackContent 包裹为同一临界区。
    /// 返回的 IDisposable 应在 using 块中使用，释放时自动解锁。
    /// </summary>
    public static async Task<IDisposable> AcquireWriteLockAsync(
        string path,
        CancellationToken ct = default
    )
    {
        var semaphore = WriteLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new SemaphoreReleaser(semaphore);
    }

    /// <summary>
    /// SemaphoreSlim 释放器——using 块结束时自动释放信号量。
    /// </summary>
    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    /// <summary>
    /// 同步原子写入文本文件（UTF-8 无 BOM）。
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
        TrackContent(path, content);
    }

    /// <summary>
    /// 原子写入文本文件（UTF-8 无 BOM）：先写临时文件，再 File.Replace 原子替换。
    /// </summary>
    public static async Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken ct = default
    )
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), ct);
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
        TrackContent(path, content);
    }

    // ── 读取 ──
    // 读取文件内容并追踪内容哈希，用于冲突检测

    /// <summary>
    /// 同步读取文本文件，并追踪内容哈希。
    /// </summary>
    public static string ReadAllText(string path)
    {
        var content = File.ReadAllText(path, Encoding.UTF8);
        TrackContent(path, content);
        return content;
    }

    /// <summary>
    /// 异步读取文本文件，并追踪内容哈希。
    /// </summary>
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        TrackContent(path, content);
        return content;
    }

    // ── 追加写入（JSONL） ──
    // 用于 JSONL 格式的增量写入，非原子操作

    /// <summary>
    /// 追加一行到文件末尾。
    /// 使用进程级 SemaphoreSlim 防并发交叉，写后自动追踪内容哈希。
    /// checkConflict=true 时写入前校验文件未被外部修改。
    /// 单行写入依赖 O_APPEND 内核级原子性，信号量确保 TrackContent 时序正确。
    /// </summary>
    public static async Task AppendLineAsync(
        string path,
        string line,
        bool checkConflict = false,
        CancellationToken ct = default
    )
    {
        using var _ = await AcquireWriteLockAsync(path, ct);

        if (checkConflict)
            await EnsureUnchangedAsync(path, ct);

        await File.AppendAllTextAsync(path, line + "\n", Encoding.UTF8, ct);

        var allContent = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        TrackContent(path, allContent);
    }

    /// <summary>
    /// 追加一行到文件末尾，带冲突检测和内容追踪。
    /// 使用进程级 SemaphoreSlim 确保互斥（替代 FileShare.Read，Unix advisory 不可靠），
    /// 配合 O_APPEND 内核级原子性。
    /// checkConflict=true 时写入前校验文件未被外部修改。
    /// </summary>
    public static async Task AppendTextLockedAsync(
        string path,
        string content,
        bool checkConflict = true,
        CancellationToken ct = default
    )
    {
        using var _ = await AcquireWriteLockAsync(path, ct);

        if (checkConflict)
            await EnsureUnchangedAsync(path, ct);

        {
            await using var fs = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true
            );
            var bytes = Encoding.UTF8.GetBytes(content);
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
        }

        // FileStream 已关闭，安全读取全文用于 TrackContent
        var allContent = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        TrackContent(path, allContent);
    }

    /// <summary>
    /// 批量追加多行到文件末尾，带冲突检测和内容追踪。
    /// 使用进程级 SemaphoreSlim 确保多行原子写入不交叉（替代 FileShare.Read）。
    /// compact + leaf 等需要原子追加多行的场景使用此方法。
    /// checkConflict=true 时写入前校验文件未被外部修改。
    /// </summary>
    public static async Task AppendLinesLockedAsync(
        string path,
        IEnumerable<string> lines,
        bool checkConflict = true,
        CancellationToken ct = default
    )
    {
        using var _ = await AcquireWriteLockAsync(path, ct);

        if (checkConflict)
            await EnsureUnchangedAsync(path, ct);

        {
            await using var fs = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true
            );
            foreach (var line in lines)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await fs.WriteAsync(bytes, ct);
            }
            await fs.FlushAsync(ct);
        }

        // FileStream 已关闭，安全读取全文用于 TrackContent
        var allContent = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        TrackContent(path, allContent);
    }

    /// <summary>
    /// Fast durable JSONL append for a session-owned writer.
    /// </summary>
    public static async Task AppendLinesDurableAsync(
        string path,
        IEnumerable<string> lines,
        bool checkConflict = true,
        CancellationToken ct = default
    )
    {
        using var writeLock = await AcquireWriteLockAsync(path, ct);
        if (checkConflict)
        {
            if (FastAppendedFiles.TryGetValue(path, out var fastTracked))
            {
                var info = new FileInfo(path);
                if (
                    info.Length != fastTracked.Length
                    || info.LastWriteTimeUtc != fastTracked.LastWriteTimeUtc
                )
                    throw new FileConflictException(path);
            }
            else
            {
                await EnsureUnchangedAsync(path, ct);
            }
        }

        // lines may be single-pass; do not pre-enumerate merely to estimate capacity.
        // ★ 统一 LF（与 AppendLinesLockedAsync 一致）：StringBuilder.AppendLine 使用
        // Environment.NewLine，在 Windows 产出 \r\n——JSONL 格式平台相关且与既有 LF 内容混合。
        var payloadBuilder = new StringBuilder();
        foreach (var line in lines)
            payloadBuilder.Append(line).Append('\n');

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true
        );
        await stream.WriteAsync(Encoding.UTF8.GetBytes(payloadBuilder.ToString()), ct);
        await stream.FlushAsync(ct);
        stream.Flush(flushToDisk: true);

        var persisted = new FileInfo(path);
        FastAppendedFiles[path] = new FastAppendTrackingInfo(
            persisted.Length,
            persisted.LastWriteTimeUtc
        );
        TrackedFiles.TryRemove(path, out _);
    }

    /// <summary>
    /// 在写入前校验：文件自上次通过本类读取以来未被外部修改。
    /// 使用 mtime 快速路径 + SHA256 确认的双重校验策略。
    /// 如果文件被修改过，抛出 FileConflictException。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <exception cref="FileConflictException">文件已被外部修改</exception>
    public static async Task EnsureUnchangedAsync(string path, CancellationToken ct = default)
    {
        if (!TrackedFiles.TryGetValue(path, out var tracked))
            return; // 从未追踪过 → 不校验

        if (!File.Exists(path))
            return; // 文件被删除了 → 由调用方决定

        // 读取文件内容并计算 SHA256（比 mtime 更可靠）
        // 注意：不使用 mtime 快速路径——Windows 上 File.Replace 与后续写入可能产生相同 mtime，
        // 导致快速路径误判为"未修改"。
        var currentContent = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        var currentHash = ComputeSha256(currentContent);

        if (currentHash != tracked.ContentHash)
        {
            throw new FileConflictException(path);
        }
    }

    /// <summary>
    /// 手动追踪文件内容的哈希值（用于非本类读取的场景，如反序列化后）。
    /// 同时记录当前文件的 mtime。
    /// </summary>
    public static void TrackContent(string path, string content)
    {
        FastAppendedFiles.TryRemove(path, out _);
        TrackedFiles[path] = new FileTrackingInfo
        {
            ContentHash = ComputeSha256(content),
            LastWriteTimeUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow,
        };
    }

    /// <summary>
    /// 清除指定路径的追踪状态（文件被删除时调用）。
    /// </summary>
    public static void ForgetPath(string path)
    {
        TrackedFiles.TryRemove(path, out _);
        FastAppendedFiles.TryRemove(path, out _);
    }

    // ── 内部工具 ──
    // SHA256 哈希计算工具方法

    private static string ComputeSha256(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>调试用：检查路径是否被追踪。</summary>
    internal static bool Debug_IsTracked(string path) => TrackedFiles.ContainsKey(path);
}
