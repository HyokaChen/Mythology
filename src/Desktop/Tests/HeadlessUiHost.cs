using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Desktop;
using Desktop.Styles;

namespace Desktop.Tests;

/// <summary>
/// 进程内唯一的 Avalonia headless UI 线程。
///
/// Avalonia 只允许一个 <see cref="Application"/>：同一进程里第二次
/// <c>AppBuilder...SetupWithoutStarting()</c> 会抛异常，而 <c>Dispatcher.UIThread</c>
/// 又绑定在最初初始化的那个线程上。所以任何需要真实主题/控件的测试都必须复用这一个宿主，
/// 各自新建宿主会让另一个测试类整批 0ms 失败。
/// </summary>
internal sealed class HeadlessUiHost
{
    private static readonly Lazy<HeadlessUiHost> Shared = new(() => new HeadlessUiHost());

    private readonly BlockingCollection<Action> _work = [];
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly List<Window> _windows = [];
    private Exception? _initError;

    private HeadlessUiHost()
    {
        new Thread(Pump) { IsBackground = true, Name = "Mythology.Desktop.Headless" }.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Avalonia headless UI 线程初始化超时");
        if (_initError is not null)
            throw new InvalidOperationException("Avalonia headless 初始化失败", _initError);
    }

    public static HeadlessUiHost Instance => Shared.Value;

    /// <summary>当前仍可见的受跟踪窗口数（只在 UI 线程读取）。</summary>
    public int VisibleWindowCount => _windows.Count(window => window.IsVisible);

    public void Run(Action action)
    {
        using var done = new ManualResetEventSlim(false);
        ExceptionDispatchInfo? error = null;

        _work.Add(() =>
        {
            // 每个 Run 从生产默认值开始，避免上一个测试把窗口动效开关留在 false。
            Motion.WindowMotionEnabled = true;
            // 用集合快照而不是下标：动作里可能先关掉旧窗口再开新窗口，
            // 列表收缩后下标会错位，Skip(start) 会漏掉新窗口。
            var preexisting = _windows.ToHashSet();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                try
                {
                    CloseWindowsOpenedSince(preexisting);
                }
                catch (Exception ex)
                {
                    // 清理异常也必须唤醒测试线程，不能让 done.Wait() 永久挂住。
                    error ??= ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    done.Set();
                }
            }
        });

        if (!done.Wait(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("Avalonia headless 渲染动作超时");
        error?.Throw();
    }

    private void TrackWindow(Window window)
    {
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
    }

    /// <summary>
    /// 兜底关闭本次 Run 新打开且仍可见的窗口。
    /// 清理期间临时把 <see cref="Motion.WindowMotionEnabled"/> 置 false，Close() 同步完成；
    /// 这里再 RunJobs 一次，让 Closed / DetachedFromVisualTree 与动画退订真正落地。
    /// </summary>
    private void CloseWindowsOpenedSince(HashSet<Window> preexisting)
    {
        var pending = _windows.Where(window => !preexisting.Contains(window)).ToArray();
        if (pending.Length == 0)
            return;

        // 临时关闭窗口动效：仍在退场动画里的窗口 exiting=true，会直接放行；
        // 从未 Close 过的窗口则因为 WindowMotionEnabled=false 不再取消 Closing。
        var windowMotion = Motion.WindowMotionEnabled;
        Motion.WindowMotionEnabled = false;
        try
        {
            foreach (var window in pending)
            {
                try
                {
                    if (window.IsVisible)
                        window.Close();
                }
                catch
                {
                    // 清理失败不能覆盖测试自身的断言异常。
                }
            }
        }
        finally
        {
            Motion.WindowMotionEnabled = windowMotion;
        }

        _windows.RemoveAll(window => !window.IsVisible);
        Dispatcher.UIThread.RunJobs();
    }

    private void Pump()
    {
        try
        {
            // headless 也保留真实的窗口退场动效：WindowExit / MarkdownDialog 等测试
            // 需要断言 Closing 被取消、动画后真正关闭。泄漏窗口由 Run 结束时临时
            // 关闭动效后强制 Close，见 CloseWindowsOpenedSince。
            Motion.WindowMotionEnabled = true;

            // 用真实 App：加载 MythologyTheme/Styles + LiveMarkdown 默认样式，
            // 让 CodeBlock 等模板化控件也走生产主题，而不是空的测试 Application。
            if (Application.Current is null)
            {
                AppBuilder
                    .Configure<App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                    .SetupWithoutStarting();
            }

            // 跟踪测试期间打开的所有窗口；Run 结束时兜底关闭本次动作泄漏的窗口。
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => TrackWindow(window));
        }
        catch (Exception ex)
        {
            _initError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
                // 动作内部自行捕获断言异常；这里只防止 UI 线程退出。
            }
        }
    }
}
