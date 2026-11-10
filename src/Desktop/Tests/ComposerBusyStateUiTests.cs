using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Desktop.Services;
using Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI.Primitives;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 发送↔停止按钮的真实 UI 回归（无头 Avalonia）：
/// Enter 受理后，为等待引擎准备（上下文注入 / 压缩预检）的阶段，右侧必须已经显示「停止」、
/// 不再显示「发送」。否则 ReactiveCommand 执行期的 :disabled 灰态会在准备阶段闪一帧。
///
/// 复现方式：门控 INovelStore.GetNovel，让引擎的 InjectNovelContextAsync 挂起；
/// 此窗口内真实视图的 StopButton 必须可见、SendButton 不可见。
/// </summary>
public sealed class ComposerBusyStateUiTests : IDisposable
{
    private static readonly HeadlessUiHost Ui = HeadlessUiHost.Instance;

    private readonly string _base = Path.Combine(
        Path.GetTempPath(),
        $"myth-composer-busy-{Guid.NewGuid():N}"
    );
    private readonly ConversationViewModel _vm;
    private readonly DesktopAgentBridge _bridge;
    private readonly GatedNovelStore _gate;

    public ComposerBusyStateUiTests()
    {
        var novelsRoot = Path.Combine(_base, "novels");
        var storeDir = Path.Combine(_base, "store");
        Directory.CreateDirectory(novelsRoot);
        Directory.CreateDirectory(storeDir);

        var registry = new ToolRegistry();
        var agent = new Agent(registry, null) { CurrentNovelId = "n1" };
        var store = new FileNovelStore(novelsRoot, storeDir);
        store.SaveNovel(new Novel { Id = "n1", Title = "测试小说" }).GetAwaiter().GetResult();

        var (gatedStore, gate) = GatedNovelStore.Wrap(store);
        _gate = gate;
        _bridge = new DesktopAgentBridge(agent);
        var engine = new SessionEngine(
            _bridge,
            new FileSessionStore(storeDir, novelsRoot),
            gatedStore,
            new TodoStore(),
            new ContextTokenCalibrator(),
            new ConfigStore(storeDir),
            NullLogger.Instance
        );
        _vm = new ConversationViewModel(
            _bridge,
            engine,
            gatedStore,
            tools: registry,
            novelsRoot: novelsRoot
        );
        _vm.CurrentNovelId = "n1";
        engine.NewSession();
    }

    public void Dispose()
    {
        _gate.Release();
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void Submit_ShowsStopImmediately_BeforeEnginePrepCompletes()
    {
        Ui.Run(() =>
        {
            var view = new Desktop.Views.ConversationView { DataContext = _vm };
            var send = view.FindControl<Button>("SendButton");
            var stop = view.FindControl<Button>("StopButton");
            Assert.NotNull(send);
            Assert.NotNull(stop);

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                _vm.InputText = "继续写";
                _vm.SendMessageCommand.Execute().Subscribe();
                // ReactiveCommand 的 delegate 必须同步进入 SendMessageAsync：
                // 若这里已经是 false，说明状态翻转被调度到下一轮消息循环，灰态仍可能渲染一帧。
                Assert.True(_vm.IsSubmitting);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                // 引擎卡在上下文注入（门未开）：提交态必须已让发送按钮退场。
                Assert.True(_vm.IsSubmitting);
                Assert.True(_vm.IsBusy);
                Assert.False(send!.IsVisible);
                Assert.True(stop!.IsVisible);

                _gate.Release();
                PumpUntil(() => send!.IsVisible && !stop!.IsVisible, "提交结束并恢复发送按钮");
                Assert.False(_vm.IsBusy);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 提交准备阶段按停止：引擎必须在进入 Agent 循环前兑现取消，
    /// 否则停止按钮可见却阻止不了这次发送。
    /// </summary>
    [Fact]
    public void StopDuringPrep_CancelsBeforeBridgeStarts()
    {
        var bridgeStarts = 0;
        _bridge.OnProcessingChanged += processing =>
        {
            if (processing)
                Interlocked.Increment(ref bridgeStarts);
        };

        Ui.Run(() =>
        {
            var view = new Desktop.Views.ConversationView { DataContext = _vm };
            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                _vm.InputText = "继续写";
                _vm.SendMessageCommand.Execute().Subscribe();
                Assert.True(_vm.IsSubmitting);

                _vm.StopProcessingCommand.Execute().Subscribe();
                _gate.Release();
                PumpUntil(() => !_vm.IsBusy, "停止完成并恢复发送按钮");

                Assert.Equal(0, Volatile.Read(ref bridgeStarts));
                Assert.False(_vm.IsProcessing);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void PumpUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
            if (condition())
                return;
        }
        Assert.Fail($"等待超时：{what}");
    }

    /// <summary>DispatchProxy 门控：GetNovel 挂起直到 Release，其余委托给真实 store。</summary>
    private class GatedNovelStore : DispatchProxy
    {
        private INovelStore _inner = null!;
        private readonly TaskCompletionSource _gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public GatedNovelStore() { }

        public static (INovelStore Store, GatedNovelStore Gate) Wrap(INovelStore inner)
        {
            var store = Create<INovelStore, GatedNovelStore>();
            var proxy = (GatedNovelStore)(object)store;
            proxy._inner = inner;
            return (store, proxy);
        }

        public void Release() => _gate.TrySetResult();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;
            if (targetMethod.Name == nameof(INovelStore.GetNovel))
            {
                var id = (string)args![0]!;
                var ct =
                    args.Length > 1 && args[1] is CancellationToken token
                        ? token
                        : CancellationToken.None;
                return GetNovelGatedAsync(id, ct);
            }
            return targetMethod.Invoke(_inner, args);
        }

        private async Task<Novel?> GetNovelGatedAsync(string id, CancellationToken ct)
        {
            await _gate.Task.WaitAsync(ct);
            return await _inner.GetNovel(id, ct);
        }
    }
}
