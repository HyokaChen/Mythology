// 功能：通道单实例锁 —— 同一个 bot 只允许一个进程在长轮询
//
// 为什么用文件锁而不是 PID 检查：两个入口（TUI / Desktop）都可能常驻，甚至跑在不同机器上。
// PID + 时间戳方案要处理 PID 复用、进程已崩、跨用户无权读进程信息、机器重启后残留……
// 而 FileShare.None 是内核强制的：进程崩了 OS 自动释放，不存在"判断失误"的分支。
//
// 锁文件里的内容仅供诊断展示（谁占着），不当正确性依据。

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Channels.WeChat;

/// <summary>
/// 通道独占锁。持有期间其它进程（或同进程的第二次启动）无法再拿。
/// 不删除锁文件：删除与加锁之间存在竞态，留着反而更安全。
/// </summary>
public sealed class WeChatChannelLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private WeChatChannelLock(FileStream stream, string filePath)
    {
        _stream = stream;
        FilePath = filePath;
    }

    /// <summary>锁文件路径（诊断用）。</summary>
    public string FilePath { get; }

    /// <summary>
    /// 尝试独占。已被占用时返回 null（不是异常——"另一个入口正在用"是正常状态，宿主据此提示用户）。
    /// </summary>
    /// <param name="directory">目录（不存在则创建）。</param>
    /// <param name="ownerLabel">写入锁文件的入口标签，仅诊断。</param>
    public static WeChatChannelLock? TryAcquire(
        string directory,
        string ownerLabel,
        ILogger? logger = null
    )
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "channel.lock");

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
        }
        catch (IOException ex)
        {
            logger?.LogInformation(ex, "微信通道已被其它进程占用: {Path}", path);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            // 文件权限/只读挂载：同样视为不可用，不要让它冒充"启动成功"
            logger?.LogWarning(ex, "微信通道锁文件不可写: {Path}", path);
            return null;
        }

        try
        {
            var info = JsonSerializer.Serialize(
                new
                {
                    owner = ownerLabel,
                    pid = Environment.ProcessId,
                    since = DateTimeOffset.UtcNow,
                }
            );
            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes(info));
            stream.Flush();
        }
        catch (Exception ex)
        {
            // 写诊断信息失败不该导致拿锁失败
            logger?.LogDebug(ex, "写入微信通道锁信息失败: {Path}", path);
        }

        return new WeChatChannelLock(stream, path);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Dispose();
    }
}
