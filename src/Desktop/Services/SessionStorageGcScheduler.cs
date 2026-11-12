using Core.Data;
using Microsoft.Extensions.Logging;

namespace Desktop.Services;

/// <summary>
/// 会话存储 GC 调度：Desktop 无 IHost，由 MainWindowViewModel 启动长生命周期循环。
/// 当前是保守 GC：清理崩溃残留的 *.tmp，不删除用户消息/分支。
/// 物理分支回收需要明确的保留策略，不能在后台默默删历史。
/// </summary>
public sealed class SessionStorageGcScheduler(
    FileSessionStore store,
    ILogger<SessionStorageGcScheduler> logger
)
{
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SessionStorageGc] 后台 GC 调度异常退出");
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await store.CollectGarbageAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0)
                logger.LogInformation("[SessionStorageGc] 清理失效临时文件 {Count} 个", removed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SessionStorageGc] 本轮 GC 失败");
        }
    }
}
