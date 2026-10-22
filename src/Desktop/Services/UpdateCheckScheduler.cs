using Desktop.ViewModels;
using Microsoft.Extensions.Logging;

namespace Desktop.Services;

/// <summary>
/// 更新检查调度：启动后 20 秒首查 + 每日东八区 03:00 周期检查。
/// 首个周期 tick 对齐东八区次日 03:00（中国无夏令时，此后 24h 周期恒为 03:00，无漂移）；
/// Desktop 无 IHost，由 MainWindowViewModel.InitializeAsync 手动启动（同微信长轮询循环）。
/// 单次检查异常在 UpdateState 内部已吞；此处再兜一层，保证 RunAsync 永不因单次失败退出。
/// </summary>
public sealed class UpdateCheckScheduler(
    UpdateState updateState,
    ILogger<UpdateCheckScheduler> logger
)
{
    /// <summary>启动延迟：避开启动高峰，让登录/会话恢复/启动引导先跑。</summary>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>周期：03:00 → 次日 03:00 恰为 24 小时（东八区无夏令时，恒对齐）。</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>每日检查时刻（东八区墙上时间 03:00）。</summary>
    private const int CheckHour = 3;

    /// <summary>由 MainWindowViewModel 在 InitializeAsync 中启动；进程退出即终止。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);

            // 睡到下一个东八区 03:00；机器休眠时 Task.Delay 暂停、唤醒后立即补跑（检查迟到不丢失）
            await Task.Delay(NextCheckDelay(BeijingNow()), cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出：正常停止
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UpdateCheckScheduler] 自动更新调度异常退出");
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await updateState.RunScheduledCheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UpdateCheckScheduler] 定时更新检查失败（不影响下次调度）");
        }
    }

    /// <summary>当前东八区时间（固定 +8 偏移，不依赖系统时区库）。</summary>
    internal static DateTimeOffset BeijingNow() =>
        DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));

    /// <summary>距下一个东八区 03:00 的时长（now 必须带 +8 偏移）。</summary>
    internal static TimeSpan NextCheckDelay(DateTimeOffset now)
    {
        var next = new DateTimeOffset(now.Date.AddHours(CheckHour), now.Offset);
        if (next <= now)
            next = next.AddDays(1);
        return next - now;
    }
}
