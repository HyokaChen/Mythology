using System.Reflection;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Threading;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 终端 UI 测试夹具基类 —— 进程级 <c>Terminal.Instance</c> / <c>Dispatcher</c> 的唯一所有者。
///
/// <para>
/// 为什么需要它：这两个都是进程级单例，而每个终端测试类都会 <c>Terminal.Initialize</c> 并起一个
/// <c>TerminalApp.RunAsync</c>。若上一类的 UI 线程或在途续体没退干净，会出现两类症状：
/// </para>
/// <list type="number">
/// <item>下一类夹具的 <c>Dispatcher.Current.InvokeAsync</c> 落到别人的 UI 线程 →
/// <c>Invalid thread access</c> → 该类全部用例 0ms 失败（级联）；</item>
/// <item>迟到续体在 Dispatcher 已脱离后 <c>Post</c> → <c>InvalidOperationException</c> 抛在线程池上
/// → 未处理异常 → 进程中止（exit 134），MTP 只报已执行的部分（total 漂移）。</item>
/// </list>
/// <para>
/// 因此本基类把「启动 / 退出」变成显式屏障：
/// </para>
/// <list type="bullet">
/// <item>启动：<c>Terminal.ResetForTests()</c>（专为测试进程提供的静态重置）→
/// <c>Initialize(..., force: false)</c>（被占用即显式失败，不再静默顶掉别人的实例）→ 等 Dispatcher 附加；</item>
/// <item>退出：取消 → <b>必须等 <c>RunAsync</c> 真正返回</b>（<c>TerminalApp.EndRunCore</c> 的 finally 里才
/// <c>Dispatcher.DetachFromThread</c>，所以 RunAsync 返回 == Dispatcher 已脱离）→ 断言未附加 → <c>Close()</c>；
/// 超时与异常<b>一律抛出不吞</b>，否则会留下孤儿 UI 线程并污染后续测试类。</item>
/// </list>
/// </summary>
public abstract class TerminalUiFixture : IAsyncLifetime
{
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    /// <summary>本类共用的内存终端后端（测试可推事件/读输出）。</summary>
    protected InMemoryTerminalBackend Backend { get; private set; } = null!;

    /// <summary>应用的取消令牌（<see cref="DisposeAsync"/> 会先取消它）。</summary>
    protected CancellationToken RunToken => _cts?.Token ?? CancellationToken.None;

    /// <summary>终端尺寸（默认 80×24）。</summary>
    protected virtual TerminalSize Size => new(80, 24);

    /// <summary>裸 <see cref="TerminalApp"/> 形状下由基类创建的应用（自带应用的类保持 null）。</summary>
    protected TerminalApp? App { get; private set; }

    /// <summary>
    /// 构造根视觉（「裸 <see cref="TerminalApp"/>」形状）。与 <see cref="CreateAppRunner"/> 二选一覆盖。
    /// </summary>
    protected virtual Visual CreateRoot() =>
        throw new NotSupportedException(
            "覆盖 CreateRoot()（裸 TerminalApp）或 CreateAppRunner()（自带 app）之一"
        );

    /// <summary>应用选项（默认无特殊项；各类按需覆写，如鼠标、HostKind、UpdateWaitDuration）。</summary>
    protected virtual TerminalAppOptions CreateOptions() => new();

    /// <summary>
    /// 在<b>测试线程</b>上准备应用并返回其运行委托（返回的 Task 在 UI 循环结束时完成）。
    /// 默认形状：用 <see cref="CreateRoot"/> + <see cref="CreateOptions"/> 构造裸 <see cref="TerminalApp"/>；
    /// 自带应用的类（如 <c>TuiSlashTestHarness.App</c>）覆盖本方法，在其中保存自己的引用。
    /// </summary>
    protected virtual Func<CancellationToken, Task> CreateAppRunner()
    {
        var app = new TerminalApp(CreateRoot(), Terminal.Instance, CreateOptions());
        App = app;
        return app.RunAsync;
    }

    /// <summary>构造应用<b>之前</b>（设置环境变量、临时目录等全局准备）。</summary>
    protected virtual ValueTask OnPreparingAsync() => ValueTask.CompletedTask;

    /// <summary>应用已构造、<b>尚未开始运行</b>（注册工具、播种会话等）。</summary>
    protected virtual ValueTask OnReadyToRunAsync() => ValueTask.CompletedTask;

    /// <summary>应用已启动（Dispatcher 已附加）后的类内准备：等首帧、断言布局等。</summary>
    protected virtual ValueTask OnStartedAsync() => ValueTask.CompletedTask;

    /// <summary>停止应用前的类内清理：还原全局静态、等待后台任务、释放 harness 等。</summary>
    protected virtual ValueTask OnStoppingAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// 启动前的屏障：等上一个终端完全退出（<c>Dispatcher</c> 未附加任何 <c>TerminalApp</c>），
    /// 再关闭可能残留的全局终端实例（Close 幂等）；同时记下当前全局主题，供
    /// <see cref="StopAppAsync"/> 还原（测试串行 ⇒ 单槽保存安全）。
    /// 非继承类也可直接调用。
    /// </summary>
    public static async Task WaitForPreviousTerminalAsync(int timeoutMs = 10_000)
    {
        // Dispatcher 是进程级单例：仍附加着别的 TerminalApp 时再启动会抛
        // "already attached to another TerminalApp"，或被静默顶掉 → 后续投递落到别人的 UI 线程。
        await WaitUntilAsync(
            () => !DispatcherAttached(),
            "上一个终端已完全退出（Dispatcher 未附加）",
            timeoutMs
        );
        Terminal.Close();
        s_savedTheme = PanelThemes.Current;

        // 测试进程不得打真实云端：错误对话框会污染区域重发断言；探活/请求续体还会在类拆掉后
        // 回投已脱离的 Dispatcher（迟到续体 → 线程池未处理异常 → SIGABRT/134）。收尾还原。
        s_savedCloudUrl = Environment.GetEnvironmentVariable("MYTHOLOGY_CLOUD_URL");
        s_cloudUrlSaved = true;
        Environment.SetEnvironmentVariable("MYTHOLOGY_CLOUD_URL", "http://127.0.0.1:1");

        WriteStartMarker();
    }

    /// <summary>诊断（临时）：记录正在初始化的终端测试类，用于定位宿主中止发生在哪个类。</summary>
    private static void WriteStartMarker()
    {
        try
        {
            var calling = new System.Diagnostics.StackTrace()
                .GetFrames()
                .Select(f => f.GetMethod()?.DeclaringType)
                .FirstOrDefault(t =>
                    t is not null
                    && t != typeof(TerminalUiFixture)
                    && t.Namespace == typeof(TerminalUiFixture).Namespace
                );
            File.AppendAllText(
                "/tmp/tui_fixture_classes.txt",
                $"{DateTime.Now:HH:mm:ss.fff} {calling?.Name}\n"
            );
        }
        catch
        {
            // 诊断失败不影响测试
        }
    }

    private static PanelColors? s_savedTheme;
    private static string? s_savedCloudUrl;
    private static bool s_cloudUrlSaved;

    /// <summary>还原本类开始前的全局主题（<c>PanelThemes.Current</c> 是静态态，未还原会改变后续类布局/渲染）。</summary>
    public static void RestoreTheme()
    {
        if (s_savedTheme is { } theme)
            PanelThemes.Current = theme;
    }

    /// <summary>收尾断言：<c>RunAsync</c> 返回后 Dispatcher 必须已脱离（否则说明还有孤儿 UI 线程）。</summary>
    public static void AssertDispatcherDetached() =>
        Assert.False(DispatcherAttached(), "RunAsync 已返回但 Dispatcher 仍附加着 TerminalApp");

    /// <summary>收尾屏障：取消并等应用真正返回；超时与其它异常一律上抛（只吞取消）。</summary>
    public static async Task StopAppAsync(
        CancellationTokenSource cts,
        Task runTask,
        int timeoutSeconds = 10
    )
    {
        await cts.CancelAsync();
        try
        {
            await runTask.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        }
        catch (OperationCanceledException)
        {
            // 取消是预期退出路径（RunAsync 以取消收尾）
        }

        AssertDispatcherDetached();
        RestoreTheme();

        if (s_cloudUrlSaved)
        {
            Environment.SetEnvironmentVariable("MYTHOLOGY_CLOUD_URL", s_savedCloudUrl);
            s_cloudUrlSaved = false;
        }
    }

    public async ValueTask InitializeAsync()
    {
        // 屏障①：上一类必须已完全退出（Dispatcher 未附加）；屏障②：清掉残留的全局终端实例。
        await WaitForPreviousTerminalAsync();

        Backend = new InMemoryTerminalBackend(Size, null);
        Terminal.Initialize(
            Backend,
            new TerminalOptions { ImplicitStartInput = true },
            force: true
        );

        await OnPreparingAsync();

        // 应用在测试线程上构造（与既有夹具一致），其 RunAsync 放到后台线程跑。
        var runApp = CreateAppRunner();

        await OnReadyToRunAsync();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _runTask = Task.Run(() => runApp(ct));

        await WaitUntilAsync(DispatcherAttached, "TerminalApp 启动（Dispatcher 已附加）");
        // 已绑定 != 已开始泵消息：再等一次「能往返投递」，否则首个 OnUiAsync 会落到测试线程上执行
        await WaitUntilAsync(DispatcherReadyAsync, "TerminalApp 就绪（Dispatcher 可往返投递）");
        await OnStartedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await OnStoppingAsync();

            if (_cts is not null)
            {
                // 屏障：等 RunAsync 返回（= 终端 UI 线程已退出、Dispatcher 已脱离）。
                // 超时/其它异常一律上抛：吞掉会让孤儿线程继续往下一测试类里投递回调
                // （迟到 Post → 线程池未处理异常 → 进程中止）。
                await StopAppAsync(_cts, _runTask!);
            }
        }
        finally
        {
            Terminal.Close();
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Dispatcher 当前是否附加着运行中的 TerminalApp（反射读 internal AttachedApp）。</summary>
    internal static bool DispatcherAttached() =>
        typeof(Dispatcher)
            .GetProperty("AttachedApp", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(Dispatcher.Current)
            is not null;

    /// <summary>
    /// 在 UI 线程上执行并取回结果（视觉对象线程亲和）。
    ///
    /// 应用刚启动时 Dispatcher 可能"已绑定但还没开始泵消息"，或应用已退出 —— 这两种
    /// <see cref="InvalidOperationException"/>（"not attached"/"Invalid thread access"）都不该让探针直接失败。
    /// 短暂等待后重试即可：异常发生在动作执行**之前**，重试不会重复副作用。
    /// </summary>
    internal static async Task<T> OnUiAsync<T>(Func<T> action, int attempts = 40)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Dispatcher.Current.InvokeAsync(action);
            }
            catch (InvalidOperationException) when (attempt < attempts)
            {
                await Task.Delay(25);
            }
        }
    }

    /// <summary>Dispatcher 是否已可往返投递（应用已开始泵消息）。</summary>
    private static async Task<bool> DispatcherReadyAsync()
    {
        try
        {
            await Dispatcher.Current.InvokeAsync(static () => 0);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>轮询直到条件成立；超时以断言失败报告（不再静默继续）。</summary>
    internal static async Task WaitUntilAsync(
        Func<bool> condition,
        string what,
        int timeoutMs = 10_000
    )
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"等待超时：{what}");
    }

    /// <summary>轮询直到异步条件成立；超时以断言失败报告。</summary>
    internal static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string what,
        int timeoutMs = 10_000
    )
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"等待超时：{what}");
    }
}
