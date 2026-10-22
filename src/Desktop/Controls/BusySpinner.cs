using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Desktop.Controls;

/// <summary>
/// 激活式 Braille 加载指示器（文本控件）：帧序列与 XenoAtom.Terminal.UI 的
/// <c>SpinnerStyles.Dots</c> 逐帧一致（80ms 一节拍），桌面端与 TUI 旋转动画同源。
/// 多实例共享一个静态 DispatcherTimer；无激活实例时下一拍自动停表（零后台空转）。
/// </summary>
public sealed class BusySpinner : TextBlock
{
    /// <summary>Dots 帧序列（与 TUI <c>SpinnerStyles.Dots</c> 对齐）。</summary>
    public static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    /// <summary>帧间隔（毫秒，与 TUI Dots 节拍一致）。</summary>
    public const int FrameIntervalMs = 80;

    /// <summary>是否激活（true 启动旋转；false 停在当前帧，下一拍被惰性回收）。</summary>
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<
        BusySpinner,
        bool
    >(nameof(IsActive));

    private static readonly List<WeakReference<BusySpinner>> Active = [];
    private static DispatcherTimer? Timer;

    private int _frame;

    static BusySpinner()
    {
        IsActiveProperty.Changed.AddClassHandler<BusySpinner>(
            (spinner, change) => spinner.OnIsActiveChanged((bool)change.NewValue!)
        );
    }

    public BusySpinner()
    {
        Text = Frames[0];
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>是否激活。</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void OnIsActiveChanged(bool isActive)
    {
        if (!isActive)
        {
            // 置 false 无需立即处理：下一拍扫描时移除引用，全部失活则停表
            return;
        }

        _frame = 0;
        Text = Frames[0];
        Active.Add(new WeakReference<BusySpinner>(this));
        EnsureTimer().Start();
    }

    private static DispatcherTimer EnsureTimer()
    {
        if (Timer is null)
        {
            Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameIntervalMs) };
            Timer.Tick += (_, _) => AdvanceAll();
        }

        return Timer;
    }

    private static void AdvanceAll()
    {
        var anyActive = false;
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            if (!Active[i].TryGetTarget(out var spinner) || !spinner.IsActive)
            {
                Active.RemoveAt(i);
                continue;
            }

            anyActive = true;
            spinner._frame = (spinner._frame + 1) % Frames.Length;
            spinner.Text = Frames[spinner._frame];
        }

        if (!anyActive)
            Timer?.Stop();
    }
}
