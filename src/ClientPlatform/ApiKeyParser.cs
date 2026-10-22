namespace ClientPlatform;

/// <summary>
/// 数据面 API Key 的客户端解析（镜像后端 <c>ApiKeyService.TryParse</c> 的格式约定）。
/// Key 格式：<c>mk_live_{publicId}_{secret}</c>；publicId 前缀标识签发来源
/// （<c>oauth…</c> = 本地回调 OAuth、<c>dev…</c> = 设备流）。
/// </summary>
public static class ApiKeyParser
{
    /// <summary>
    /// 尝试从手动输入的 API Key 中解析出 publicId；格式非法返回 null。
    /// 用于 TUI/Desktop 的「手动输入 API Key」兜底登录：校验 <c>mk_live_</c> 前缀 + 4 段结构，
    /// 返回第 3 段（publicId），不再错误地追加 <c>dev_</c> 前缀。
    /// </summary>
    public static string? TryGetPublicId(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var key = apiKey.Trim();
        if (!key.StartsWith("mk_live_", StringComparison.Ordinal))
            return null;

        // Split('_', 4)：publicId 与 secret 均不允许为空；secret 可能含下划线（第 4 段整体为 secret）。
        var parts = key.Split('_', 4);
        if (parts.Length != 4 || parts[2].Length == 0 || parts[3].Length == 0)
            return null;

        return parts[2];
    }
}
