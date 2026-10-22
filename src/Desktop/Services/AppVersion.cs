using System.Reflection;

namespace Desktop.Services;

/// <summary>
/// 版本号运行时入口：从入口程序集的 AssemblyInformationalVersionAttribute 读取编译时烙印的版本号。
/// 形如 "0.0.2-alpha.1+&lt;40 位 git sha&gt;"（发布构建由 installer 传 -p:Version=&lt;V&gt;，SDK/SourceLink
/// 追加 source-revision）；本地裸构建为默认 "1.0.0"（+ sha）。比较前用 UpdateVersion.Normalize 剥掉 + 元数据。
/// </summary>
public static class AppVersion
{
    /// <summary>完整版本号；元数据缺失（异常构建链路）返回 null，由调用方兜底。</summary>
    public static string? Full =>
        Assembly
            .GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
}
