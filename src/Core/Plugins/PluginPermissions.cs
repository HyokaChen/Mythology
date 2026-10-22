namespace Core.Plugins;

/// <summary>
/// 插件权限常量 — 所有合法权限字符串的唯一来源。
/// 增删权限只改这个文件，csproj 和 C# Attribute 引用常量值。
/// </summary>
public static class PluginPermissions
{
    // ── 数据访问 ──
    public const string NovelRead = "novel:read";
    public const string NovelWrite = "novel:write";
    public const string VectorRead = "vector:read";
    public const string VectorWrite = "vector:write";
    public const string ConfigRead = "config:read";
    public const string ConfigWrite = "config:write";

    /// <summary>
    /// 会话转录读取（跨页历史检索）。会话转录含完整对话与工具输出，属私有模型状态——
    /// 刻意不加入 <see cref="ExternalAllowed"/>（第三方插件不得声明）。
    /// </summary>
    public const string SessionRead = "session:read";

    /// <summary>会话转录写入（换页标记、会话私有笔记）。同样不对外部插件开放。</summary>
    public const string SessionWrite = "session:write";

    // ── 系统能力 ──
    public const string FileSystem = "fs:access";
    public const string Network = "net:access";
    public const string Process = "process:execute";
    public const string UserInteract = "ui:interact";

    // ── 云端能力 ──
    /// <summary>云端 Fiction Harness 调用（IFictionHarnessClient）— 产生云端计量，不加入 ExternalAllowed。</summary>
    public const string CloudHarness = "cloud:harness";

    /// <summary>
    /// External 插件允许的权限子集（低风险，只读）。
    /// 即使 DLL 中的 PluginPermissionAttribute 声明了高风险权限，
    /// External 级别也会自动裁剪，只保留此集合中的权限。
    /// </summary>
    public static readonly HashSet<string> ExternalAllowed =
    [
        NovelRead, // 只读摘要（通过 IPluginStore）
        ConfigRead, // 读配置
        Network, // 网络访问（工具自身控制）
        UserInteract, // 用户交互
    ];
}
