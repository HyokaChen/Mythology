using System.Collections.Frozen;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Core.Plugins;

/// <summary>
/// 权限 → 服务类型映射。
/// RestrictedPluginServiceProvider 根据此映射决定放行哪些服务。
/// 使用 FrozenDictionary 实现不可变 + O(1) 查找，构建后不再变更。
/// </summary>
public static class PluginCapabilities
{
    static readonly FrozenDictionary<string, Type[]> _map = new Dictionary<string, Type[]>
    {
        [PluginPermissions.NovelRead] = [typeof(IPluginStore)],
        [PluginPermissions.NovelWrite] = [typeof(INovelStore), typeof(IPluginStore)],
        [PluginPermissions.VectorRead] = [typeof(IEmbeddingService), typeof(RetrievalKernel)],
        [PluginPermissions.VectorWrite] = [typeof(IEmbeddingService), typeof(RetrievalKernel)],
        [PluginPermissions.ConfigRead] = [typeof(IConfiguration)],
        [PluginPermissions.ConfigWrite] = [typeof(IConfiguration), typeof(ConfigStore)],
        [PluginPermissions.FileSystem] = [typeof(TodoStore), typeof(UserQuestionStore)],
        [PluginPermissions.Network] = [], // 网络能力由工具自身控制，不映射到 DI 服务
        [PluginPermissions.Process] = [], // 同上
        [PluginPermissions.UserInteract] = [typeof(UserQuestionStore)],
        [PluginPermissions.CloudHarness] = [typeof(IFictionHarnessClient)],
    }.ToFrozenDictionary();

    /// <summary>所有插件都可获取的非敏感服务。</summary>
    static readonly Type[] _baseline = [typeof(ILogger)];

    /// <summary>
    /// 根据权限列表构建允许的服务类型集合。
    /// 权限是累加的：拥有 novel:write 自动获得 novel:read 映射的服务。
    /// </summary>
    public static HashSet<Type> ResolveAllowedServices(IEnumerable<string> permissions) =>
        permissions.Where(_map.ContainsKey).SelectMany(p => _map[p]).Concat(_baseline).ToHashSet();
}
