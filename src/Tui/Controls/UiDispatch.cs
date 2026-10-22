// 功能：向 UI Dispatcher 投递操作的安全包装
//
// 为什么要这层：State<T> 继承 DispatcherObject（有线程亲和），而通道状态事件是从后台线程
// （绑定流程的 await 续体）触发的。应用已退出时 Dispatcher.Post 会抛 InvalidOperationException
// → 线程池未处理异常 → 进程中止，所以必须吞掉这一种异常而不是让它逃逸。
//
// 与 CloudLoginDialog 的同名私有方法同语义；那边是私有实现，本类供新对话框使用，不改动既有代码。

using XenoAtom.Terminal.UI.Threading;

namespace Tui.Controls;

internal static class UiDispatch
{
    /// <summary>投递到 UI 线程；未附着运行中的 TerminalApp（应用关闭/未启动）时静默丢弃。</summary>
    public static void PostIfAttached(Action action)
    {
        try
        {
            Dispatcher.Current.Post(action);
        }
        catch (InvalidOperationException)
        {
            // 没有运行中的 TerminalApp：没有 UI 需要刷新，直接丢弃
        }
    }
}
