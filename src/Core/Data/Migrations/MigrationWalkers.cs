using System.Text.Json;
using Core.Shared;

namespace Core.Data.Migrations;

/// <summary>
/// 迁移共用的文件系统走查：登记根目录（novels 根 + workspaces.json 登记的根）与
/// 按忽略规则枚举文档（跳过点开头 / node_modules / 冲突目录）。
/// 由 005（键/值改写）与 006（文件名 kebab）共用，避免两份走查逻辑漂移。
/// </summary>
internal static class MigrationWalkers
{
    internal static List<string> CollectRoots(
        string mythologyDir,
        string novelsRoot,
        TextWriter? log
    )
    {
        var roots = new List<string>();
        AddRoot(roots, novelsRoot);
        var persistPath = Path.Combine(mythologyDir, "workspaces.json");
        if (File.Exists(persistPath))
        {
            try
            {
                using var document = JsonDocument.Parse(AtomicFile.ReadAllText(persistPath));
                if (
                    document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("roots", out var list)
                    && list.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var entry in list.EnumerateArray())
                    {
                        if (
                            entry.ValueKind == JsonValueKind.Object
                            && entry.TryGetProperty("path", out var path)
                        )
                            AddRoot(roots, path.GetString());
                    }
                }
            }
            catch (Exception ex)
            {
                log?.WriteLine($"  [warn] workspaces.json 解析失败，仅迁移活动目录：{ex.Message}");
            }
        }
        return roots;
    }

    internal static void AddRoot(List<string> roots, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;
        var normalized = Path.GetFullPath(root);
        if (!roots.Contains(normalized, StringComparer.Ordinal))
            roots.Add(normalized);
    }

    internal static IEnumerable<string> EnumerateDocs(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (
                    name.StartsWith('.')
                    || string.Equals(name, "node_modules", StringComparison.Ordinal)
                    || name.Contains(".conflict-", StringComparison.Ordinal)
                )
                    continue;
                pending.Push(sub);
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.'))
                    continue;
                if (
                    name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                )
                    yield return file;
            }
        }
    }
}
