namespace Core.Plugins;

/// <summary>
/// 受限服务提供器 — 根据 DLL 中的 PluginPermissionAttribute 授权服务访问。
/// 权限数据源是 DLL 反射（受签名保护），不是 plugin.json（可篡改）。
/// </summary>
public sealed class RestrictedPluginServiceProvider(
    IServiceProvider inner,
    HashSet<Type> allowedServices
) : IServiceProvider
{
    public object? GetService(Type serviceType) =>
        allowedServices.Contains(serviceType) ? inner.GetService(serviceType) : null;
}
