using XenoAtom.Terminal;

namespace Tui.Services;

/// <summary>
/// 终端标签页标题 loading 转圈：agent loop 运行期间在终端窗口标题前滚动帧动画，
/// 结束后恢复基础标题。跨平台——底层由 XenoAtom.Terminal 的 TerminalInstance.Title 承担
/// （Windows 后端走 Console.Title，Unix 后端走 OSC IconAndWindowTitle，不支持时静默降级）。
/// </summary>
internal sealed class TerminalTitleSpinner : IDisposable
{
    private const int PeriodMs = 120;
    private const string DefaultBaseTitle = "Mythology";

    private readonly Action<string> _setTitle;
    private readonly Action _tickAction; // 测试可注入，避免真实 Timer 并发推进帧
    private readonly Lock _lock = new();
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"; // 与状态栏 SpinnerStyles.Dots 同族

    private Timer? _timer;
    private int _frameIndex;
    private string _baseTitle = DefaultBaseTitle;
    private string? _busyLabel;

    public TerminalTitleSpinner(Action<string>? setTitle = null, Action? tickAction = null)
    {
        _setTitle = setTitle ?? (title => Terminal.Instance.Title = title);
        _tickAction = tickAction ?? TickLocked;
    }

    /// <summary>
    /// 更新基础标题（如小说名）并立即重写完整标题：busy 时带当前帧，idle 时为纯标题。
    /// </summary>
    public void SetBaseTitle(string title)
    {
        lock (_lock)
        {
            _baseTitle = title;
            WriteCurrentLocked();
        }
    }

    /// <summary>
    /// 进入/退出运行态。label 非空（如「思考中」「压缩中」）启动帧动画；null 停止并恢复纯标题。
    /// </summary>
    public void SetBusy(string? label)
    {
        lock (_lock)
        {
            if (label == _busyLabel)
                return;
            _busyLabel = label;

            if (label is not null)
            {
                _timer ??= new Timer(
                    static state => ((TerminalTitleSpinner)state!)._tickAction(),
                    this,
                    dueTime: 0,
                    period: PeriodMs
                );
                TickLocked();
            }
            else
            {
                StopTimerLocked();
                _setTitle(_baseTitle);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _busyLabel = null; // 停止一切后续标题写入（含在途 Timer 回调）
            StopTimerLocked();
            _setTitle(_baseTitle);
        }
    }

    /// <summary>推进一帧并写出标题。Timer 回调或测试手动驱动。</summary>
    private void TickLocked()
    {
        lock (_lock)
        {
            if (_busyLabel is null)
                return;
            var frame = Frames[_frameIndex % Frames.Length];
            _frameIndex++;
            _setTitle($"{frame} {_busyLabel}…｜{_baseTitle}");
        }
    }

    internal void TickForTest() => TickLocked();

    private void StopTimerLocked()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void WriteCurrentLocked()
    {
        if (_busyLabel is null)
        {
            _setTitle(_baseTitle);
            return;
        }
        var frame = Frames[_frameIndex % Frames.Length];
        _setTitle($"{frame} {_busyLabel}…｜{_baseTitle}");
    }
}
