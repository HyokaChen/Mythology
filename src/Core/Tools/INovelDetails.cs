namespace Core.Tools;

/// <summary>
/// 包含小说实体 ID 的 Details 接口。
/// Host 层的 LoadNovelHook 通过模式匹配 <c>details is INovelDetails</c> 提取 ID，
/// 无需反射，也无需 Host 直接依赖 Plugins 层的 ManageNovelDetails 类型。
/// </summary>
public interface INovelDetails
{
    /// <summary>小说项目 ID。</summary>
    string Id { get; }
}
