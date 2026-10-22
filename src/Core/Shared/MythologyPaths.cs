namespace Core.Shared;

/// <summary>
/// Mythology 应用程序的路径常量和方法集合。
/// 定义所有目录结构、文件命名规范和路径解析方法。
/// </summary>
public static class MythologyPaths
{
    /// <summary>配置目录名称（位于用户主目录下）。</summary>
    public const string ConfigDirName = ".mythology";

    /// <summary>会话存储子目录名称。</summary>
    public const string SessionsSubDir = "sessions";

    /// <summary>小说存储子目录名称。</summary>
    public const string NovelsSubDir = "novels";

    /// <summary>输入历史子目录名称。</summary>
    public const string InputHistorySubDir = "input-history";

    /// <summary>配置文件名称。</summary>
    public const string ConfigFileName = "config.yaml";

    /// <summary>小说元数据文件名称。</summary>
    public const string NovelMetaFileName = "novel.yaml";

    /// <summary>插件清单文件名称。</summary>
    public const string PluginManifestFileName = "plugin.json";

    /// <summary>章节存储子目录名称。</summary>
    public const string ChapterSubDir = "chapters";

    /// <summary>章节文件命名格式（如 ch001.md）。</summary>
    public const string ChapterFilePattern = "ch{0:D3}.md";

    /// <summary>会话目录命名格式。</summary>
    public const string SessionDirPattern = "--novels-{0}--";

    /// <summary>审计日志文件命名格式。</summary>
    public const string AuditFilePattern = "audit_{0}.jsonl";

    /// <summary>获取 Mythology 配置目录的完整路径。</summary>
    /// <returns>用户主目录下的 .mythology 路径。</returns>
    public static string GetMythologyDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ConfigDirName
        );

    /// <summary>获取会话存储目录的完整路径。</summary>
    /// <returns>配置目录下的 sessions 子目录路径。</returns>
    public static string GetSessionsDir() => Path.Combine(GetMythologyDir(), SessionsSubDir);

    /// <summary>获取小说根目录的完整路径。</summary>
    /// <returns>当前工作目录下的 novels 子目录路径。</returns>
    public static string GetNovelsRoot() =>
        Path.Combine(Environment.CurrentDirectory, NovelsSubDir);

    /// <summary>获取指定小说的会话目录名称。</summary>
    /// <param name="novelId">小说 ID。</param>
    /// <returns>格式化的会话目录名称。</returns>
    public static string GetSessionDirName(string novelId) =>
        string.Format(SessionDirPattern, novelId);
}
