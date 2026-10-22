namespace Core.Infrastructure;

/// <summary>
/// ID 生成器 — 基于 GUID 生成短唯一标识符。
/// </summary>
public static class IdGenerator
{
    /// <summary>
    /// 生成默认长度（12位）的短 ID。
    /// </summary>
    /// <returns>12 位十六进制字符串。</returns>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// 生成指定长度的短 ID。
    /// </summary>
    /// <param name="length">ID 长度，最大 32（GUID 十六进制长度）。</param>
    /// <returns>指定长度的十六进制字符串。</returns>
    public static string NewId(int length) => Guid.NewGuid().ToString("N")[..length];
}
