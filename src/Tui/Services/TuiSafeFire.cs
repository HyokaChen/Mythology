using Microsoft.Extensions.Logging;
using XenoAtom.Terminal.UI.Controls;

namespace Tui.Services;

/// <summary>
/// 统一 async void 异常处理基座。UI 事件处理器（XenoAtom 要求 Action 签名，无法返回 Task）
/// 通过本委托将核心逻辑传入，由本类集中 try/catch + Logger + Toast。
/// 后续维护人在核心逻辑中添加 await 无需担心异常泄漏到 Dispatcher 顶层。
/// </summary>
internal static class TuiSafeFire
{
    /// <summary>
    /// 安全执行异步操作，捕获异常并记录日志与 Toast。
    /// </summary>
    /// <param name="work">核心异步操作。</param>
    /// <param name="operation">操作名称（用于日志消息）。</param>
    /// <param name="logger">日志记录器（可选，为 null 时仅 Toast）。</param>
    /// <param name="errorMessage">自定义错误消息（为 null 时使用默认 "操作失败: {ex.Message}"）。</param>
    internal static async void SafeFire(
        Func<Task> work,
        string operation,
        ILogger? logger = null,
        string? errorMessage = null
    )
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            if (logger is not null)
                TuiLogger.OperationFailed(logger, ex, operation);
            ToastService.Error($"{errorMessage ?? "操作失败"}: {ex.Message}");
        }
    }
}
