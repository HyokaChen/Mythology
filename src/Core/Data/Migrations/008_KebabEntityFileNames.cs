namespace Core.Data.Migrations;

/// <summary>
/// 008：把 novel 仓库内的**实体文件名**统一为 kebab-case
/// （`千山秋依_b184a29e9d44.yaml` → `千山秋依-b184a29e9d44.yaml`）。
///
/// 背景：006 只处理了集合文件（<c>world_rules.yaml</c> / <c>faction_relationships.yaml</c>），
/// 实体目录（characters / plotlines / factions / locations / items / scenes / volumes）仍是
/// <c>{Name}_{Id}.yaml</c> 下划线风格，与 005/006/007 建立的 kebab 约定不一致——同一本库里
/// 集合文件已 kebab、实体文件仍下划线，是半转换状态。
///
/// 新名**不靠字符串分割文件名推导**：名称本身可能含下划线（角色「李_楚」），
/// 分割会切错。改为读 YAML 的 <c>id</c> 字段作为权威 id，再把「去掉旧 id 后缀的 stem」
/// 换成 <c>-{id}</c>。名称部分原样保留，<c>name</c> 字段与文件名不脱节。
///
/// appearances/ evolution/ 的 <c>ch{N}.yaml</c> 分片本就无 id 后缀，走同一判定天然跳过。
///
/// 幂等：目标名等于当前名时无操作；新旧名并存时不覆盖（保留两者并记日志，交人工判断）。
/// 单文件失败跳过并记日志（不阻断启动）。
/// </summary>
public sealed class KebabEntityFileNames : IDataMigration
{
    public int Version => 8;
    public string Name => "008_KebabEntityFileNames";
    public string Description => "novel 仓库实体文件名统一 kebab-case（{Name}_{Id} → {Name}-{Id}）";

    public TextWriter? Log { get; set; }

    /// <summary>实体目录名（与 SyncDocumentEnumerator.EntityDirToType 的键同源）。</summary>
    private static readonly string[] s_entityDirs =
    [
        "characters",
        "plotlines",
        "factions",
        "locations",
        "items",
        "scenes",
        "volumes",
        "appearances",
        "evolution",
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

                var parent = Path.GetFileName(Path.GetDirectoryName(file));
                if (parent is null || Array.IndexOf(s_entityDirs, parent) < 0)
                    continue;

                var dest = BuildKebabName(file);
                if (dest is null)
                    continue; // 不是 {Name}_{Id} 形态 → 不动
                scanned++;

                if (string.Equals(dest, file, StringComparison.Ordinal))
                    continue; // 已是 kebab

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
            $"  实体文件名：扫描 {scanned} 个，重命名 {renamed} 个，跳过 {skipped} 个。"
        );
        return Task.CompletedTask;
    }

    /// <summary>
    /// 计算 kebab 目标名；不需要改名时返回 null。
    /// 读 YAML 的 id 字段作为权威 id——名称可能含下划线，字符串分割会切错。
    /// </summary>
    private static string? BuildKebabName(string file)
    {
        var dir = Path.GetDirectoryName(file);
        var stem = Path.GetFileNameWithoutExtension(file);
        if (dir is null || stem.Length == 0)
            return null;

        string? id;
        try
        {
            id = YamlHelper.Deserialize(File.ReadAllText(file)).GetValueOrDefault("id")?.ToString();
        }
        catch (Exception)
        {
            return null; // 解析失败 = 不认识的文件，交给其它流程
        }
        if (string.IsNullOrEmpty(id))
            return null;

        var suffix = $"_{id}";
        if (!stem.EndsWith(suffix, StringComparison.Ordinal))
            return null; // 非 {Name}_{Id} 形态（如 ch001 分片）→ 不动

        var name = stem[..^suffix.Length];
        if (name.Length == 0)
            return null;

        return Path.Combine(dir, $"{name}-{id}.yaml");
    }
}
