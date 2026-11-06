using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Headless;
using Desktop;

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

    public void Run(Action action)
    {
        using var done = new ManualResetEventSlim(false);
        ExceptionDispatchInfo? error = null;

        _work.Add(() =>
        {
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
                done.Set();
            }
        });

        if (!done.Wait(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("Avalonia headless 渲染动作超时");
        error?.Throw();
    }

    private void Pump()
    {
        try
        {
            // 用真实 App：加载 MythologyTheme/Styles + LiveMarkdown 默认样式，
            // 让 CodeBlock 等模板化控件也走生产主题，而不是空的测试 Application。
            if (Application.Current is null)
            {
                AppBuilder
                    .Configure<App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                    .SetupWithoutStarting();
            }
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
