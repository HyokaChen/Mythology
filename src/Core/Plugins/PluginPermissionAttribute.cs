namespace Core.Plugins;

/// <summary>
/// 程序集级权限声明 — 编译进 DLL，受强名称签名保护。
/// 运行时从 DLL 反射读取，作为权限授权的唯一数据源。
/// plugin.json 的 permissions 字段由构建工具从 csproj 属性生成，运行时不读取。
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class PluginPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}
