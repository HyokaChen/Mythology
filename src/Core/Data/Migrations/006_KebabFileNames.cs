namespace Core.Data.Migrations;

/// <summary>
/// 006：把 novel 仓库内的**数据文件名**统一为 kebab-case
/// （`world_rules.yaml` → `world-rules.yaml`、`faction_relationships.yaml` → `faction-relationships.yaml`）。
///
/// 背景：文档类型由文件名派生（`SyncDocumentEnumerator.Classify` → `fileName[..^5]`），
/// 云端 `SyncDocumentTypes` 与前端 `useSyncApi.ts` 标签表按该类型取值；
/// 文件名含下划线会让文档类型也含下划线，与全仓 kebab 约定不一致（CHANGE_11 C12/C13/C14 的收官项）。
///
/// 幂等：名字已是 kebab 时无操作；新旧名并存时不覆盖（保留两者并记日志，交人工判断）。
/// 单目录失败跳过并记日志（不阻断启动）。
/// </summary>
public sealed class KebabFileNames : IDataMigration
{
    public int Version => 6;
    public string Name => "006_KebabFileNames";
    public string Description =>
        "novel 仓库数据文件名统一 kebab-case（world_rules / faction_relationships）";

    public TextWriter? Log { get; set; }

    private static readonly (string Old, string New)[] s_renames =
    [
        ("world_rules.yaml", "world-rules.yaml"),
        ("faction_relationships.yaml", "faction-relationships.yaml"),
    ];

    public Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var roots = MigrationWalkers.CollectRoots(mythologyDir, novelsRoot, Log);
        var scanned = 0;
        var renamed = 0;
        var skipped = 0;

        foreach (var root in roots)
        {
            foreach (var file in MigrationWalkers.EnumerateDocs(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var target = Array.Find(
                    s_renames,
                    r => string.Equals(r.Old, name, StringComparison.Ordinal)
                );
                if (target.Old is null)
                    continue;
                scanned++;
                var dir = Path.GetDirectoryName(file);
                if (dir is null)
                    continue;
                var dest = Path.Combine(dir, target.New);
                if (File.Exists(dest))
                {
                    skipped++;
                    Log?.WriteLine($"  [warn] 新旧文件名并存，保留不动：{file}");
                    continue;
                }
                try
                {
                    File.Move(file, dest);
                    renamed++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    Log?.WriteLine($"  [warn] 重命名失败 {file}: {ex.Message}");
                }
            }
        }

        Log?.WriteLine(
            $"  数据文件名：扫描 {scanned} 个，重命名 {renamed} 个，跳过 {skipped} 个。"
        );
        return Task.CompletedTask;
    }
}
