namespace Core.Shared;

/// <summary>
/// 路径解析工具类，在插件和 TUI 之间共享。
/// 解析 @ 前缀的路径为相对于当前工作目录的绝对路径。
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// 解析路径：支持 @ 前缀（相对于当前工作目录）或普通相对/绝对路径。
    /// </summary>
    /// <param name="path">要解析的路径字符串。</param>
    /// <returns>解析后的绝对路径。</returns>
    public static string ResolvePath(string path)
    {
        // 处理 @ 前缀路径
        if (path.StartsWith('@'))
        {
            var relative = path[1..];
            // 去除前导斜杠 — LLM 有时会将 @序章.txt 转换为 @/序章.txt
            if (relative.StartsWith('/') || relative.StartsWith('\\'))
                relative = relative[1..];
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative));
        }

        // 处理相对路径
        return !Path.IsPathRooted(path)
            ? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path))
            :
            // 绝对路径直接返回
            path;
    }
}
