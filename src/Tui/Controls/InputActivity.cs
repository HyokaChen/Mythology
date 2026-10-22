using System.Diagnostics;
using XenoAtom.Terminal.UI;

namespace Tui.Controls;

/// <summary>
/// 用户输入活动跟踪 + 区域重发限流。
///
/// 为什么必须门控：应用注册了 <c>onUpdate</c>，Auto 模式下是 15ms 活跃帧（约 66Hz）。
/// 无条件每 tick 做重发类操作会持续烧 CPU（全树重排实测 15%~30%，空转也烧），
/// 所以只在「最近有过输入」的时间窗内放行，空转零成本。
/// </summary>
internal static class InputActivity
{
    private static long _lastInputTimestamp;
    private static long _lastRegionRenderTimestamp;
    private static readonly double TicksPerSecond = Stopwatch.Frequency;

    /// <summary>把指针 / 滚轮 / 键盘输入接到任意 Visual 上。</summary>
    /// <remarks>
    /// 用库源生成的路由事件扩展，无需继承、无需 AddHandler。事件从子控件冒泡经过该视觉，
    /// 因此挂在「所有内容的共同祖先」上即可覆盖整片背景。
    /// </remarks>
    public static T ObserveInput<T>(this T visual)
        where T : Visual
    {
        visual.PointerMoved((_, _) => NoteInput());
        visual.PointerWheel((_, _) => NoteInput());
        visual.KeyDown((_, _) => NoteInput());
        return visual;
    }

    /// <summary>记录一次用户输入（指针移动 / 滚轮 / 按键）。</summary>
    public static void NoteInput() =>
        Volatile.Write(ref _lastInputTimestamp, Stopwatch.GetTimestamp());

    /// <summary>
    /// 区域重发的限流放行：<paramref name="recent"/> 内有过输入时，每
    /// <paramref name="interval"/> 最多放行一次；输入停下后再放行一次收尾
    /// （<paramref name="recent"/> ~ 2×<paramref name="recent"/> 之间）。
    ///
    /// 用途：Apple Terminal 的中文输入法内联组词逐格保存/恢复、恢复只还原字符不还原每格颜色，
    /// 把已上色的格子擦成基色；差分渲染器认为那些格子自己已经发过，永远不补。
    /// 被擦坏的格子随组词变长而增多（用户实测「右侧那块不停拉长」），所以打字期间也要持续补。
    /// </summary>
    public static bool TakeRegionRenderSlot(TimeSpan recent, TimeSpan interval)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastInputTimestamp);
        if (last == 0)
            return false;

        var sinceInput = (now - last) / TicksPerSecond;
        // 输入停下超过 2×recent：收尾帧已发过，不再补
        if (sinceInput >= recent.TotalSeconds * 2)
            return false;

        var lastSlot = Volatile.Read(ref _lastRegionRenderTimestamp);
        if ((now - lastSlot) / TicksPerSecond < interval.TotalSeconds)
            return false;

        Volatile.Write(ref _lastRegionRenderTimestamp, now);
        return true;
    }
}
