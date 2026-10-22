namespace Core.Plugins;

/// <summary>
/// 插件信任级别 — 三级模型：Builtin（随主程序发布）、Verified（DLL 签名在白名单）、External（无签名）。
/// Builtin 拥有完整服务访问权限；Verified 按 DLL 中的 PluginPermissionAttribute 授权；External 仅最小权限。
/// </summary>
public enum PluginTrustLevel
{
    /// <summary>内置插件 — 随主程序发布，目录在 AppContext.BaseDirectory/plugins/ 下，全权限。</summary>
    Builtin,

    /// <summary>已验证插件 — DLL PublicKeyToken 在白名单中，按 Attribute 授权。</summary>
    Verified,

    /// <summary>外部插件 — 无签名或签名不在白名单，硬编码最小权限。</summary>
    External,
}
