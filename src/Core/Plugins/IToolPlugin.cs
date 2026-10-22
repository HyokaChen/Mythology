using Core.Tools;

namespace Core.Plugins;

/// <summary>
/// 工具插件接口 — 插件契约。每个 .dll 中恰好有一个实现此接口的类。
/// 元数据（名称、版本、描述）由 plugin.json 提供，运行时从 PluginManifest 读取。
/// </summary>
public interface IToolPlugin
{
    /// <summary>
    /// 初始化插件，注入依赖。PluginLoader 通过反射创建实例后调用此方法，
    /// 插件从 <paramref name="sp"/> 获取所需服务。
    /// </summary>
    /// <param name="sp">服务提供器，用于获取依赖服务。</param>
    void Initialize(IServiceProvider sp);

    /// <summary>
    /// 返回此插件提供的所有工具。调用前须先调用 <see cref="Initialize"/>。
    /// </summary>
    /// <returns>插件提供的工具列表。</returns>
    IEnumerable<AgentTool> GetTools();
}
