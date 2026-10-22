using ClientPlatform;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 云端工具的统一失败文案：把「transport 按请求 CTS 触发的单请求超时」从通用 catch 里摘出来——
/// 此前它落到 <c>catch (Exception)</c>，用户看到的是框架英文文案（<c>A task was canceled.</c>）。
/// 单请求上限的唯一真源是 <see cref="CloudTimings.CloudApiTimeout"/>（由
/// <c>CloudRestTransport</c> 对所有数据面客户端统一施加）。
///
/// 调用方取消（<c>ct.IsCancellationRequested</c>）不走本类——各工具的前置 catch 直接重抛。
/// </summary>
internal static class CloudToolErrors
{
    /// <summary>超时文案（<paramref name="action"/> 为动作短语，如「网页抓取」）。</summary>
    public static string Timeout(string action) =>
        $"{action}失败：云端请求超时（单请求上限 {CloudTimings.CloudApiTimeout.TotalSeconds:0} 秒），可稍后重试。";
}
