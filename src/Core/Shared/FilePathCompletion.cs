using System.Text;

namespace Core.Shared;

/// <summary>补全条目：<see cref="Name"/> 用于插入输入框（目录尾带 <c>/</c>），<see cref="FullPath"/> 用于展示绝对位置。</summary>
public readonly record struct FileEntry(string Name, string FullPath);

/// <summary>
/// <c>@</c> 文件路径补全（TUI 输入框与 Desktop 对话框共用）。
///
/// 语义（两端对齐，实体提及一律走 <c>#</c> 引用）：
/// <list type="bullet">
///   <item>裸 <c>@</c> → 列出基准目录顶层条目（TUI=CWD，Desktop=小说项目根）</item>
///   <item><c>~/</c> → 用户主目录；<c>/</c> → 绝对路径；其余 → 基准目录相对路径</item>
///   <item>目录候选名尾加 <c>/</c>（选中后可继续输入下一级）；文件候选保持原名</item>
///   <item>查询词与目录名均可含空格（macOS 常见 <c>My Files/</c> 不应断词）</item>
///   <item>最多 <c>MaxEntries</c> 条，按名称排序，弹窗过长保护</item>
/// </list>
/// </summary>
public static class FilePathCompletion
{
    /// <summary>单次返回的条目上限。</summary>
    public const int MaxEntries = 20;

    /// <summary>路径查询词是否为空（含纯空白）。</summary>
    public static bool IsEmptyQuery(string query) => string.IsNullOrWhiteSpace(query);

    /// <summary>
    /// 根据 <c>@</c> 后的部分路径返回补全条目（目录名尾加 <c>/</c>）。
    /// 目录不存在 / 无匹配 / IO 异常 → 空数组（弹窗自然关闭，绝不抛出）。
    /// </summary>
    /// <param name="partialPath">光标前 <c>@</c> 之后的查询词（可含空格）</param>
    /// <param name="rootDir">裸 <c>@</c> 与相对路径的基准目录</param>
    public static FileEntry[] GetEntries(string partialPath, string rootDir)
    {
        try
        {
            var (dir, prefix) = Resolve(partialPath, rootDir);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return [];

            return Directory
                .EnumerateFileSystemEntries(dir)
                .Select(p =>
                {
                    var isDir = File.GetAttributes(p).HasFlag(FileAttributes.Directory);
                    var name = Path.GetFileName(p);
                    return new FileEntry(isDir ? name + "/" : name, p);
                })
                .Where(e =>
                    prefix.Length == 0
                    || e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || e.Name[..^1].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ) // 目录候选含尾 /，需去尾再匹配前缀
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries)
                .ToArray();
        }
        catch
        {
            // IO 异常（权限 / 目录消失竞态）→ 空结果，补全降级为不弹窗
            return [];
        }
    }

    /// <summary>
    /// 解析查询词：返回「要列举的目录」与「目录内前缀」。
    /// 供 UI 侧计算展示路径，避免在调用方重复实现路径拆分。
    /// </summary>
    public static (string Directory, string Prefix) Resolve(string partialPath, string rootDir)
    {
        if (IsEmptyQuery(partialPath))
            return (rootDir, "");

        if (partialPath.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rest = partialPath[2..];
            var slashIdx = rest.LastIndexOf('/');
            return slashIdx < 0
                ? (home, rest)
                : (Path.Combine(home, rest[..slashIdx]), rest[(slashIdx + 1)..]);
        }

        if (partialPath.StartsWith('/'))
        {
            // 绝对路径：最后一个 / 之前为目录，之后为前缀（"/" → 根目录）
            var slashIdx = partialPath.LastIndexOf('/');
            return (partialPath[..(slashIdx == 0 ? 1 : slashIdx)], partialPath[(slashIdx + 1)..]);
        }

        // 相对路径（无 ~/ 与 / 前缀）：基于 rootDir
        var relSlashIdx = partialPath.LastIndexOf('/');
        return relSlashIdx < 0
            ? (rootDir, partialPath)
            : (Path.Combine(rootDir, partialPath[..relSlashIdx]), partialPath[(relSlashIdx + 1)..]);
    }

    /// <summary>候选是否目录（尾加 / 的约定）。</summary>
    public static bool IsDirectoryEntry(string entry) => entry.EndsWith('/');
}
